using DocumentService.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DocumentService.Api.Infrastructure;

// ADR-0002: Database per Service — DocumentService 専用 DbContext
public class DocumentDbContext(DbContextOptions<DocumentDbContext> options) : DbContext(options)
{
    public DbSet<Document> Documents => Set<Document>();

    // FR-06, UC-03: 版履歴
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Document>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.Title).HasMaxLength(500).IsRequired();
            e.Property(d => d.Status).HasMaxLength(50).IsRequired();
            e.Property(d => d.MarkdownUri).HasMaxLength(2048);
            e.Property(d => d.OriginalUri).HasMaxLength(2048);
            e.Property(d => d.ContentType).HasMaxLength(200);
            e.Property(d => d.Attributes)
                .HasConversion(DictionaryConverter())
                .HasColumnType("jsonb")
                .Metadata.SetValueComparer(DictionaryComparer());
            e.Property(d => d.Tags)
                .HasConversion(ListConverter())
                .HasColumnType("jsonb")
                .Metadata.SetValueComparer(ListComparer());

            // FR-06: 版履歴は集約配下の append-only コレクション。文書削除時に連動削除する。
            e.HasMany(d => d.Versions)
                .WithOne()
                .HasForeignKey(v => v.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Metadata.FindNavigation(nameof(Document.Versions))!
                .SetPropertyAccessMode(PropertyAccessMode.Field);
        });

        mb.Entity<DocumentVersion>(e =>
        {
            e.HasKey(v => v.Id);
            e.HasIndex(v => new { v.DocumentId, v.Version }).IsUnique();
            e.Property(v => v.Title).HasMaxLength(500).IsRequired();
            e.Property(v => v.Status).HasMaxLength(50).IsRequired();
            e.Property(v => v.MarkdownUri).HasMaxLength(2048);
            e.Property(v => v.ChangeNote).HasMaxLength(500);
            e.Property(v => v.Attributes)
                .HasConversion(DictionaryConverter())
                .HasColumnType("jsonb")
                .Metadata.SetValueComparer(DictionaryComparer());
            e.Property(v => v.Tags)
                .HasConversion(ListConverter())
                .HasColumnType("jsonb")
                .Metadata.SetValueComparer(ListComparer());
        });
    }

    // FR-06: Attributes(Dictionary) / Tags(List) を jsonb 文字列へ変換する共通定義。
    private static ValueConverter<Dictionary<string, string>, string> DictionaryConverter() => new(
        v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
        v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());

    private static ValueComparer<Dictionary<string, string>> DictionaryComparer() => new(
        (a, b) => System.Text.Json.JsonSerializer.Serialize(a, (System.Text.Json.JsonSerializerOptions?)null) == System.Text.Json.JsonSerializer.Serialize(b, (System.Text.Json.JsonSerializerOptions?)null),
        v => v.GetHashCode(), v => new Dictionary<string, string>(v));

    private static ValueConverter<List<string>, string> ListConverter() => new(
        v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
        v => System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());

    private static ValueComparer<List<string>> ListComparer() => new(
        (a, b) => a!.SequenceEqual(b!),
        v => v.Aggregate(0, (h, e) => HashCode.Combine(h, e.GetHashCode())),
        v => v.ToList());
}
