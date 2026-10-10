namespace Sangam.Identity.Infrastructure.Security;

/// <summary>
/// Argon2id cost parameters and the pepper. Bound from the <c>Sangam:PasswordHashing</c> configuration section; the
/// pepper comes from a secret file (<c>Sangam__PasswordHashing__Pepper</c>), never a settings file.
/// </summary>
public sealed class Argon2idOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Sangam:PasswordHashing";

    /// <summary>Configuration key of the current pepper.</summary>
    public const string PepperKey = SectionName + ":Pepper";

    /// <summary>The shortest pepper accepted, in bytes (256 bits).</summary>
    public const int MinimumPepperBytes = 32;

    /// <summary>Memory in KiB. Default 65536 (64 MiB).</summary>
    public int MemoryKiB { get; set; } = 65536;

    /// <summary>Number of passes. Default 3.</summary>
    public int Iterations { get; set; } = 3;

    /// <summary>Degree of parallelism. Default 1.</summary>
    public int Parallelism { get; set; } = 1;

    /// <summary>
    /// The pepper (rc.5, ASVS V2.4.5): at least 32 random bytes in base64, held outside the database and its backups.
    /// It is Argon2id's own secret input, so a copy of the database alone reveals no password. <see langword="null"/>
    /// in Development and Testing; the hosts refuse to start without it anywhere else (<see cref="PasswordPepperGuard"/>).
    /// </summary>
    public string? Pepper { get; set; }

    /// <summary>The current pepper's version, written into every new hash as <c>keyid</c>. Raise it when the pepper is
    /// replaced, and keep the old one under <see cref="PreviousPeppers"/> until everyone has signed in again.</summary>
    public int PepperVersion { get; set; } = 1;

    /// <summary>Earlier peppers by version (base64), so hashes made with them still verify and are rehashed with the
    /// current pepper at the next sign-in.</summary>
    public Dictionary<int, string> PreviousPeppers { get; set; } = [];
}
