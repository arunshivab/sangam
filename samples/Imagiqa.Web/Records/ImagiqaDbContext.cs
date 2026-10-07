using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Imagiqa.Web.Records;

/// <summary>imagiQa's own database. Sangam holds who people are; imagiQa holds its patients.</summary>
public sealed class ImagiqaDbContext : DbContext
{
    /// <summary>Initialises the context.</summary>
    /// <param name="options">Options.</param>
    public ImagiqaDbContext(DbContextOptions<ImagiqaDbContext> options)
        : base(options)
    {
    }

    /// <summary>Patients.</summary>
    public DbSet<Patient> Patients => Set<Patient>();

    /// <summary>Vital signs.</summary>
    public DbSet<VitalSigns> Vitals => Set<VitalSigns>();

    /// <summary>Consultation notes.</summary>
    public DbSet<ConsultationNote> Notes => Set<ConsultationNote>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasSequence<long>("patient_mrn").StartsAt(1);

        modelBuilder.Entity<Patient>(b =>
        {
            b.Property(p => p.Mrn).HasDefaultValueSql("nextval('patient_mrn')");
            b.HasIndex(p => p.Mrn).IsUnique();
            b.HasIndex(p => new { p.OrganisationId, p.RegisteredAt });
            b.Property(p => p.GivenName).HasMaxLength(100).IsRequired();
            b.Property(p => p.FamilyName).HasMaxLength(100).IsRequired();
            b.Property(p => p.Sex).HasMaxLength(10).IsRequired();
            b.Property(p => p.Mobile).HasMaxLength(15);
            b.Property(p => p.RegisteredByName).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<VitalSigns>(b =>
        {
            b.HasIndex(v => new { v.PatientId, v.RecordedAt });
            b.Property(v => v.TemperatureC).HasPrecision(4, 1);
            b.Property(v => v.RecordedByName).HasMaxLength(200).IsRequired();
            b.HasOne<Patient>().WithMany().HasForeignKey(v => v.PatientId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ConsultationNote>(b =>
        {
            b.HasIndex(n => new { n.PatientId, n.WrittenAt });
            b.Property(n => n.Text).HasMaxLength(4000).IsRequired();
            b.Property(n => n.WrittenByName).HasMaxLength(200).IsRequired();
            b.HasOne<Patient>().WithMany().HasForeignKey(n => n.PatientId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>Lets <c>dotnet ef</c> build the model without starting the application.</summary>
internal sealed class ImagiqaDbContextDesignFactory : IDesignTimeDbContextFactory<ImagiqaDbContext>
{
    /// <inheritdoc />
    public ImagiqaDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<ImagiqaDbContext>()
            .UseNpgsql("Host=localhost;Database=imagiqa_sample;Username=sangam_identity;Password=sangam_dev")
            .UseSnakeCaseNamingConvention()
            .Options);
}
