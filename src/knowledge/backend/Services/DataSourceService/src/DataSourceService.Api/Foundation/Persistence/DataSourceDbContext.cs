using DataSourceService.Api.Foundation.Domain;
using DataSourceService.Api.Foundation.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace DataSourceService.Api.Foundation.Persistence;

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
            // FR-01, UC-04, SC-06（Q14 / #537）: 同期健全性。直近エラーはマスク済みの短い文字列だけを
            // 保存する（SyncErrorRedactor が長さも丸める）。列長はその上限に合わせる。
            e.Property(d => d.LastSyncError).HasMaxLength(SyncErrorRedactor.MaxLength);
            e.Property(d => d.Config)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new())
                .HasColumnType("jsonb")
                .Metadata.SetValueComparer(new ValueComparer<Dictionary<string, string>>(
                    (a, b) => System.Text.Json.JsonSerializer.Serialize(a, (System.Text.Json.JsonSerializerOptions?)null) == System.Text.Json.JsonSerializer.Serialize(b, (System.Text.Json.JsonSerializerOptions?)null),
                    // ハッシュも等価判定と同じ内容ベースにする（参照 GetHashCode は equals と契約不整合になるため）。
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null).GetHashCode(), v => new Dictionary<string, string>(v)));
            // FR-01, FR-05: 原本へ付与する既定 ABAC 属性（confidentiality 等）を jsonb 保管。
            e.Property(d => d.DefaultAttributes)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new())
                .HasColumnType("jsonb")
                .Metadata.SetValueComparer(new ValueComparer<Dictionary<string, string>>(
                    (a, b) => System.Text.Json.JsonSerializer.Serialize(a, (System.Text.Json.JsonSerializerOptions?)null) == System.Text.Json.JsonSerializer.Serialize(b, (System.Text.Json.JsonSerializerOptions?)null),
                    // ハッシュも等価判定と同じ内容ベースにする（参照 GetHashCode は equals と契約不整合になるため）。
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null).GetHashCode(), v => new Dictionary<string, string>(v)));
        });
    }
}
