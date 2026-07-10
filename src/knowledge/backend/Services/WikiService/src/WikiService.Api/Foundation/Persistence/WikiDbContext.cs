using WikiService.Api.Foundation.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace WikiService.Api.Foundation.Persistence;

// ADR-0002: WikiService 専用 DbContext
public class WikiDbContext(DbContextOptions<WikiDbContext> options) : DbContext(options)
{
    public DbSet<WikiPage> Pages => Set<WikiPage>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<WikiPage>(e =>
        {
            e.HasKey(p => p.Id);
            // IADR-0021: WikiPath は DocumentId 由来の計算値（列として保持しない）。
            e.Ignore(p => p.WikiPath);
            e.Property(p => p.Title).HasMaxLength(500).IsRequired();
            e.Property(p => p.Slug).HasMaxLength(500).IsRequired();
            e.HasIndex(p => p.Slug).IsUnique();
            e.HasIndex(p => p.DocumentId).IsUnique();
            e.Property(p => p.Attributes)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new())
                .HasColumnType("jsonb")
                .Metadata.SetValueComparer(new ValueComparer<Dictionary<string, string>>(
                    (a, b) => System.Text.Json.JsonSerializer.Serialize(a, (System.Text.Json.JsonSerializerOptions?)null) == System.Text.Json.JsonSerializer.Serialize(b, (System.Text.Json.JsonSerializerOptions?)null),
                    // ハッシュも等価判定と同じ内容ベースにする（参照 GetHashCode は equals と契約不整合になるため）。
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null).GetHashCode(), v => new Dictionary<string, string>(v)));
            e.Property(p => p.Tags)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new())
                .HasColumnType("jsonb")
                .Metadata.SetValueComparer(new ValueComparer<List<string>>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (h, e) => HashCode.Combine(h, e.GetHashCode())),
                    v => v.ToList()));
        });
    }
}
