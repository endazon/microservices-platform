using AuthorizationService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace AuthorizationService.Infrastructure.Persistence;

// ADR-0002: AuthorizationService 専用 DbContext
public class AuthorizationDbContext(DbContextOptions<AuthorizationDbContext> options) : DbContext(options)
{
    public DbSet<AttributeDefinition> AttributeDefinitions => Set<AttributeDefinition>();
    public DbSet<AbacPolicy> Policies => Set<AbacPolicy>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        var listComparer = new ValueComparer<List<string>>(
            (a, b) => a!.SequenceEqual(b!),
            v => v.Aggregate(0, (h, e) => HashCode.Combine(h, e.GetHashCode())),
            v => v.ToList());

        var dictListComparer = new ValueComparer<Dictionary<string, List<string>>>(
            (a, b) => System.Text.Json.JsonSerializer.Serialize(a, (System.Text.Json.JsonSerializerOptions?)null) == System.Text.Json.JsonSerializer.Serialize(b, (System.Text.Json.JsonSerializerOptions?)null),
            // ハッシュも等価判定と同じ内容ベースにする（参照 GetHashCode は equals と契約不整合になるため）。
            v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null).GetHashCode(),
            v => v.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()));

        mb.Entity<AttributeDefinition>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Key).HasMaxLength(100).IsRequired();
            e.Property(a => a.Label).HasMaxLength(200).IsRequired();
            e.Property(a => a.Scope).HasMaxLength(50).IsRequired();
            // FR-09: 同一スコープ内でキーは一意（辞書としての整合を DB でも担保）
            e.HasIndex(a => new { a.Key, a.Scope }).IsUnique();
            e.Property(a => a.AllowedValues)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new())
                .HasColumnType("jsonb")
                .Metadata.SetValueComparer(listComparer);
        });

        mb.Entity<AbacPolicy>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Name).HasMaxLength(200).IsRequired();
            e.Property(p => p.Action).HasMaxLength(50).IsRequired();
            e.Property(p => p.UserConditions)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new())
                .HasColumnType("jsonb")
                .Metadata.SetValueComparer(dictListComparer);
            e.Property(p => p.DocumentConditions)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new())
                .HasColumnType("jsonb")
                .Metadata.SetValueComparer(dictListComparer);
        });
    }
}
