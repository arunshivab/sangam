using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sangam.Identity.Infrastructure.Policies;

/// <summary>
/// The offline breached-password list (D-J): Pwned Passwords' downloadable SHA-1 list, stored compactly on the
/// server so that nothing about a password ever leaves it.
/// <para>
/// File layout (all integers little-endian unless noted): the 8-byte magic <c>SGMPWD01</c>; the entry count
/// (int64); the list's date as <c>yyyymmdd</c> (int32); the smallest breach count kept (int32); then 65,537 int64
/// bucket starts — entry index of the first hash whose top 16 bits are the bucket number, the last being the
/// count; then the entries, each the first 8 bytes of a SHA-1 (big-endian, so they sort as the hashes do),
/// ascending and without duplicates. Eight bytes of a hash are plenty: with a billion entries the chance that an
/// unbreached password matches one by accident is about one in eighteen billion.
/// </para>
/// </summary>
public static class PwnedPasswordList
{
    /// <summary>The file's magic.</summary>
    public static ReadOnlySpan<byte> Magic => "SGMPWD01"u8;

    /// <summary>How many buckets the index has (the top 16 bits of a hash).</summary>
    public const int Buckets = 65_536;

    /// <summary>Bytes before the entries: magic, count, date, minimum count, and the bucket index.</summary>
    public const int HeaderLength = 8 + 8 + 4 + 4 + ((Buckets + 1) * 8);

    /// <summary>The first 8 bytes of a password's SHA-1, as the list stores it.</summary>
    /// <param name="password">The password.</param>
    public static ulong Key(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
#pragma warning disable CA5350 // The list is indexed by SHA-1; it is a lookup key here, not a protection.
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(password));
#pragma warning restore CA5350
        return BinaryPrimitives.ReadUInt64BigEndian(hash);
    }

    /// <summary>The key of a 40-character hexadecimal SHA-1 (or the first 16 characters of one).</summary>
    /// <param name="hex">The hash in hexadecimal.</param>
    public static ulong KeyOfHex(ReadOnlySpan<char> hex)
        => ulong.Parse(hex[..16], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds a list file from the download. <paramref name="source"/> is either one text file of
    /// <c>HASH:COUNT</c> lines in hash order (the official downloader's single-file output), or the downloader's
    /// directory of 1,048,576 files named by the first five hex characters, each with <c>SUFFIX:COUNT</c> lines.
    /// The new list is written next to <paramref name="destination"/> and moved into place at the end, so a
    /// running server never reads a half-written file.
    /// </summary>
    /// <param name="source">The downloaded file or directory.</param>
    /// <param name="destination">The list file to write.</param>
    /// <param name="listDate">The date the list was downloaded (shown on the monitoring page).</param>
    /// <param name="minimumCount">Keep only hashes seen at least this many times (1 keeps everything).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many entries were written.</returns>
    public static long Build(string source, string destination, DateOnly listDate, int minimumCount = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        string temporary = destination + ".building";
        long[] starts = new long[Buckets + 1];
        long count = 0;
        ulong previous = 0;
        bool any = false;
        int bucket = 0;

        using (FileStream output = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: false))
        {
            output.Position = HeaderLength;
            byte[] entry = new byte[8];
            foreach ((ulong key, long seen) in ReadSource(source, cancellationToken))
            {
                if (seen < minimumCount)
                {
                    continue;
                }

                if (any && key < previous)
                {
                    throw new InvalidDataException("The list is not in hash order. Download it with the official Pwned Passwords downloader, which writes hash order.");
                }

                if (any && key == previous)
                {
                    continue;
                }

                int top = (int)(key >> 48);
                while (bucket < top)
                {
                    bucket++;
                    starts[bucket] = count;
                }

                BinaryPrimitives.WriteUInt64BigEndian(entry, key);
                output.Write(entry);
                previous = key;
                any = true;
                count++;
            }

            while (bucket < Buckets)
            {
                bucket++;
                starts[bucket] = count;
            }

            output.Position = 0;
            byte[] header = new byte[HeaderLength];
            Magic.CopyTo(header);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), count);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), (listDate.Year * 10_000) + (listDate.Month * 100) + listDate.Day);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), minimumCount);
            for (int i = 0; i <= Buckets; i++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24 + (i * 8)), starts[i]);
            }

            output.Write(header);
            output.Flush(flushToDisk: true);
        }

        File.Move(temporary, destination, overwrite: true);
        return count;
    }

    /// <summary>Reads a list file's header, or explains why it cannot be used.</summary>
    /// <param name="path">The list file.</param>
    public static (Header? Header, string? Problem) ReadHeader(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            return (null, "The list file is not there yet.");
        }

        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        long length = RandomAccess.GetLength(handle);
        if (length < HeaderLength)
        {
            return (null, "The list file is too short to be a list.");
        }

        byte[] header = new byte[HeaderLength];
        RandomAccess.Read(handle, header, 0);
        if (!header.AsSpan(0, 8).SequenceEqual(Magic))
        {
            return (null, "The file is not a Sangam breached-password list.");
        }

        long count = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
        if (length != HeaderLength + (count * 8))
        {
            return (null, "The list file is incomplete.");
        }

        int date = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
        long[] starts = new long[Buckets + 1];
        for (int i = 0; i <= Buckets; i++)
        {
            starts[i] = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24 + (i * 8)));
        }

        DateOnly? listDate = date > 0 && DateOnly.TryParseExact(date.ToString(CultureInfo.InvariantCulture), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d) ? d : null;
        return (new Header(count, listDate, BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20)), starts), null);
    }

    /// <summary>Whether <paramref name="key"/> is in the list file at <paramref name="path"/>.</summary>
    /// <param name="path">The list file.</param>
    /// <param name="header">Its header.</param>
    /// <param name="key">The key (<see cref="Key(string)"/>).</param>
    public static bool Contains(string path, Header header, ulong key)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(header);
        int top = (int)(key >> 48);
        long low = header.Starts[top];
        long high = header.Starts[top + 1] - 1;
        if (low > high)
        {
            return false;
        }

        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        Span<byte> buffer = stackalloc byte[8];
        while (low <= high)
        {
            long middle = low + ((high - low) / 2);
            RandomAccess.Read(handle, buffer, HeaderLength + (middle * 8));
            ulong found = BinaryPrimitives.ReadUInt64BigEndian(buffer);
            if (found == key)
            {
                return true;
            }

            if (found < key)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return false;
    }

    private static IEnumerable<(ulong Key, long Count)> ReadSource(string source, CancellationToken cancellationToken)
    {
        if (Directory.Exists(source))
        {
            for (int prefix = 0; prefix < 1 << 20; prefix++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = prefix.ToString("X5", CultureInfo.InvariantCulture);
                string file = Path.Combine(source, name + ".txt");
                if (!File.Exists(file))
                {
                    continue;
                }

                foreach (string line in File.ReadLines(file))
                {
                    if (Parse(name + line.Trim()) is (ulong, long) entry)
                    {
                        yield return entry;
                    }
                }
            }

            yield break;
        }

        long lineNumber = 0;
        foreach (string line in File.ReadLines(source))
        {
            if ((++lineNumber & 0xFFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (Parse(line.Trim()) is (ulong, long) entry)
            {
                yield return entry;
            }
        }
    }

    private static (ulong Key, long Count)? Parse(string line)
    {
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        string hash = colon < 0 ? line : line[..colon];
        if (hash.Length != 40 || !hash.All(char.IsAsciiHexDigit))
        {
            return null;
        }

        long count = colon < 0 || !long.TryParse(line.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out long c) ? 1 : c;
        return (KeyOfHex(hash), count);
    }

    /// <summary>A list file's header.</summary>
    /// <param name="Entries">How many hashes it holds.</param>
    /// <param name="ListDate">The date the list was downloaded.</param>
    /// <param name="MinimumCount">The smallest breach count kept.</param>
    /// <param name="Starts">The bucket index.</param>
    public sealed record Header(long Entries, DateOnly? ListDate, int MinimumCount, long[] Starts);
}
