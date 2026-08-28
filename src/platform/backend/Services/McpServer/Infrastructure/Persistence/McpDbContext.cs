using McpServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace McpServer.Infrastructure.Persistence;

// ADR-0002, FR-16, UC-09: MCP サーバー専用 DbContext（クライアント登録）。
public class McpDbContext(DbContextOptions<McpDbContext> options) : DbContext(options)
{
    public DbSet<McpClient> Clients => Set<McpClient>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        var dictComparer = new ValueComparer<Dictionary<string, string>>(
            (a, b) => System.Text.Json.JsonSerializer.Serialize(a, (System.Text.Json.JsonSerializerOptions?)null)
                == System.Text.Json.JsonSerializer.Serialize(b, (System.Text.Json.JsonSerializerOptions?)null),
            v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null).GetHashCode(),
            v => new Dictionary<string, string>(v));

        mb.Entity<McpClient>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.ClientId).HasMaxLength(200).IsRequired();
            // UC-09: Keycloak のクライアント ID は登録の一意キーである。
            e.HasIndex(c => c.ClientId).IsUnique();
            e.Property(c => c.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(c => c.Attributes)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new())
                .HasColumnType("jsonb")
                .Metadata.SetValueComparer(dictComparer);
        });
    }
}
