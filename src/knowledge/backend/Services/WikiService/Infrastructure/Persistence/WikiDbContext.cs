using WikiService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace WikiService.Infrastructure.Persistence;

// ADR-0002: WikiService 専用 DbContext
public class WikiDbContext(DbContextOptions<WikiDbContext> options) : DbContext(options)
{
    public DbSet<WikiPage> Pages => Set<WikiPage>();

    // FR-13, [[IADR-0596]] 決定 1 (#2102): 文書ごとの版の印。`Pages` と別に持ち、撤去・削除で行を消しても残す。
    public DbSet<DocumentSyncMark> SyncMarks => Set<DocumentSyncMark>();

    // [[IADR-0596]] 決定 3 (#2102): 版の印の主キーの名前。同じ文書の印を 2 つの受け口が同時に作ったとき（23505）を
    // 保存の突き合わせの負けとして扱うために、受け口が制約名と突き合わせる（EF の既定の名前と同じ値）。
    public const string SyncMarkKeyName = "PK_SyncMarks";

    // [[IADR-0596]] 決定 3 (#2102): 同期メタデータの文書 ID の一意索引の名前（初期マイグレーションの値）。初回の同期で 2 者が同じ文書の行を
    // 同時に作ると、印より先にこの索引で 23505 になる（実 PostgreSQL の結合試験で実測）。これも保存の突き合わせの負けとして扱う。
    public const string DocumentIdIndexName = "IX_Pages_DocumentId";

    // FR-13, [[IADR-0541]] (#1934): slug の索引の名前（EF の既定の名前と同じ値。既存のマイグレーションの索引名を変えない）。
    // ［2026-10-26 / #2108・ADR-0091 決定 1・[[IADR-0644]]］**一意でない索引にした**（slug に一意性を課さない）。
    // slug での引き当て（旧形式の URL の互換の解決）の速さのために索引そのものは残す。
    public const string SlugIndexName = "IX_Pages_Slug";

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<DocumentSyncMark>(e =>
        {
            e.HasKey(m => m.DocumentId).HasName(SyncMarkKeyName);
            e.Property(m => m.DocumentId).ValueGeneratedNever();
            // [[IADR-0596]] 決定 3: 保存の突き合わせ。後から保存する側は DbUpdateConcurrencyException で負ける。
            e.Property(m => m.Revision).IsConcurrencyToken();
        });

        mb.Entity<WikiPage>(e =>
        {
            e.HasKey(p => p.Id);
            // IADR-0021: WikiPath は DocumentId 由来の計算値（列として保持しない）。
            e.Ignore(p => p.WikiPath);
            e.Property(p => p.Title).HasMaxLength(WikiPage.TitleMaxLength).IsRequired();
            e.Property(p => p.Slug).HasMaxLength(WikiPage.SlugMaxLength).IsRequired();
            e.HasIndex(p => p.Slug).HasDatabaseName(SlugIndexName);
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
