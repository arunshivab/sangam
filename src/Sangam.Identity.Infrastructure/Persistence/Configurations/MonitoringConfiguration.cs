using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sangam.Identity.Domain.Entities;

namespace Sangam.Identity.Infrastructure.Persistence.Configurations;

internal sealed class MetricPointConfiguration : IEntityTypeConfiguration<MetricPoint>
{
    public void Configure(EntityTypeBuilder<MetricPoint> b)
    {
        b.ToTable("metric_points");
        b.HasKey(p => new { p.Minute, p.Host, p.Name, p.Tag });
        b.Property(p => p.Host).HasMaxLength(40);
        b.Property(p => p.Name).HasMaxLength(80);
        b.Property(p => p.Tag).HasMaxLength(80);
        b.HasIndex(p => new { p.Name, p.Minute }).HasDatabaseName("idx_metric_points_name_minute");
    }
}

internal sealed class MonitoringAlertConfiguration : IEntityTypeConfiguration<MonitoringAlert>
{
    public void Configure(EntityTypeBuilder<MonitoringAlert> b)
    {
        b.ToTable("monitoring_alerts");
        b.HasKey(a => a.Key);
        b.Property(a => a.Key).HasMaxLength(80);
        b.Property(a => a.Summary).HasMaxLength(300).IsRequired();
    }
}
