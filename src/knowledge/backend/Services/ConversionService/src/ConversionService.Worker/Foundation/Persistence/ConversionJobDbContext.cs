using ConversionService.Worker.Foundation.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ConversionService.Worker.Foundation.Persistence;

// ADR-0002, IADR-0043: ConversionService 専用 DbContext（変換ジョブ読み取りモデル）。
public class ConversionJobDbContext(DbContextOptions<ConversionJobDbContext> options) : DbContext(options)
{
    public DbSet<ConversionJob> ConversionJobs => Set<ConversionJob>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<ConversionJob>(e =>
        {
            e.HasKey(j => j.Id);
            // Id は原本取得イベントの FetchId を採るため EF に生成させない。
            e.Property(j => j.Id).ValueGeneratedNever();
            e.Property(j => j.SourceType).HasMaxLength(50).IsRequired();
            e.Property(j => j.OriginalPath).HasMaxLength(2048).IsRequired();
            e.Property(j => j.Status).HasMaxLength(20).IsRequired();
            // FR-12, SC-07: デッドレター標識。既存行は「未送出」として読むため既定 false。
            e.Property(j => j.DeadLettered).HasDefaultValue(false);
            e.Property(j => j.MarkdownUri).HasMaxLength(2048);
            e.Property(j => j.StorageUri).HasMaxLength(2048).IsRequired();
            e.Property(j => j.ContentType).HasMaxLength(255).IsRequired();

            // 再変換のため原本 ABAC 属性を jsonb 保管（DataSourceService 準拠）。
            e.Property(j => j.Attributes)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new())
                .HasColumnType("jsonb")
                .Metadata.SetValueComparer(new ValueComparer<Dictionary<string, string>>(
                    (a, b) => System.Text.Json.JsonSerializer.Serialize(a, (System.Text.Json.JsonSerializerOptions?)null) == System.Text.Json.JsonSerializer.Serialize(b, (System.Text.Json.JsonSerializerOptions?)null),
                    // ハッシュも等価判定と同じ内容ベースにする（参照 GetHashCode は equals と契約不整合になるため）。
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null).GetHashCode(),
                    v => new Dictionary<string, string>(v)));

            // 再変換のため原本タグを jsonb 保管。
            e.Property(j => j.Tags)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new())
                .HasColumnType("jsonb")
                .Metadata.SetValueComparer(new ValueComparer<List<string>>(
                    (a, b) => System.Text.Json.JsonSerializer.Serialize(a, (System.Text.Json.JsonSerializerOptions?)null) == System.Text.Json.JsonSerializer.Serialize(b, (System.Text.Json.JsonSerializerOptions?)null),
                    // ハッシュも等価判定と同じ内容ベースにする（参照 GetHashCode は equals と契約不整合になるため）。
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null).GetHashCode(),
                    v => new List<string>(v)));
        });
    }
}
