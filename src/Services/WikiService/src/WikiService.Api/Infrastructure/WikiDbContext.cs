using WikiService.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace WikiService.Api.Infrastructure;

// ADR-0002: WikiService 専用 DbContext
public class WikiDbContext(DbContextOptions<WikiDbContext> options) : DbContext(options)
{
    public DbSet<WikiPage> Pages => Set<WikiPage>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<WikiPage>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Title).HasMaxLength(500).IsRequired();
            e.Property(p => p.Slug).HasMaxLength(500).IsRequired();
            e.HasIndex(p => p.Slug).IsUnique();
            e.HasIndex(p => p.DocumentId).IsUnique();
            e.Property(p => p.Attributes)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new())
                .HasColumnType("jsonb");
            e.Property(p => p.Tags)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new())
                .HasColumnType("jsonb");
        });
    }
}
