using AuthorizationService.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AuthorizationService.Api.Infrastructure;

// ADR-0002: AuthorizationService 専用 DbContext
public class AuthorizationDbContext(DbContextOptions<AuthorizationDbContext> options) : DbContext(options)
{
    public DbSet<AttributeDefinition> AttributeDefinitions => Set<AttributeDefinition>();
    public DbSet<AbacPolicy> Policies => Set<AbacPolicy>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<AttributeDefinition>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Key).HasMaxLength(100).IsRequired();
            e.Property(a => a.Label).HasMaxLength(200).IsRequired();
            e.Property(a => a.Scope).HasMaxLength(50).IsRequired();
            e.Property(a => a.AllowedValues)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new())
                .HasColumnType("jsonb");
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
                .HasColumnType("jsonb");
            e.Property(p => p.DocumentConditions)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new())
                .HasColumnType("jsonb");
        });
    }
}
