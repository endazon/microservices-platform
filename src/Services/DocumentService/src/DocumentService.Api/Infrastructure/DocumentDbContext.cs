using DocumentService.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace DocumentService.Api.Infrastructure;

// ADR-0002: Database per Service — DocumentService 専用 DbContext
public class DocumentDbContext(DbContextOptions<DocumentDbContext> options) : DbContext(options)
{
    public DbSet<Document> Documents => Set<Document>();

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
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new())
                .HasColumnType("jsonb");
            e.Property(d => d.Tags)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new())
                .HasColumnType("jsonb");
        });
    }
}
