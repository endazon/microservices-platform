using DataSourceService.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace DataSourceService.Api.Infrastructure;

// ADR-0002: DataSourceService 専用 DbContext
public class DataSourceDbContext(DbContextOptions<DataSourceDbContext> options) : DbContext(options)
{
    public DbSet<DataSource> DataSources => Set<DataSource>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<DataSource>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.Name).HasMaxLength(200).IsRequired();
            e.Property(d => d.SourceType).HasMaxLength(50).IsRequired();
            e.Property(d => d.ConnectionUri).HasMaxLength(2048).IsRequired();
            e.Property(d => d.Status).HasMaxLength(50).IsRequired();
            e.Property(d => d.Config)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new())
                .HasColumnType("jsonb");
        });
    }
}
