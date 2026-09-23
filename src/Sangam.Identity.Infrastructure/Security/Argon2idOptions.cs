namespace Sangam.Identity.Infrastructure.Security;

/// <summary>Argon2id cost parameters. Bound from the <c>Sangam:PasswordHashing</c> configuration section.</summary>
public sealed class Argon2idOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Sangam:PasswordHashing";

    /// <summary>Memory in KiB. Default 65536 (64 MiB).</summary>
    public int MemoryKiB { get; set; } = 65536;

    /// <summary>Number of passes. Default 3.</summary>
    public int Iterations { get; set; } = 3;

    /// <summary>Degree of parallelism. Default 1.</summary>
    public int Parallelism { get; set; } = 1;
}
