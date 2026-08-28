using DocumentService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DocumentService.Infrastructure.Persistence;

// ADR-0002: Database per Service — DocumentService 専用 DbContext
public class DocumentDbContext(DbContextOptions<DocumentDbContext> options) : DbContext(options)
{
    public DbSet<Document> Documents => Set<Document>();

    // FR-06, UC-03: 版履歴
    public DbSet<DocumentVersion> DocumentVersions => Set<DocumentVersion>();

    // FR-09, SC-05, SC-09, #634: タグ辞書（IADR-0152 決定 1）。
    public DbSet<Tag> Tags => Set<Tag>();

    // FR-19, FR-20, ADR-0036 D-06, IADR-0253 決定 4（段 4）: 文書の共有先。
    public DbSet<DocumentShare> DocumentShares => Set<DocumentShare>();

    // FR-19, FR-20, ADR-0037, [[IADR-0270]] 決定 2: 個人資料の台帳・保存容量・同期端末。
    public DbSet<PrivateNote> PrivateNotes => Set<PrivateNote>();
    public DbSet<PrivateNoteQuota> PrivateNoteQuotas => Set<PrivateNoteQuota>();
    public DbSet<SyncDevice> SyncDevices => Set<SyncDevice>();

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
            // ADR-0050 決定 1 (#911): 本文指紋（SHA-256 hex = 64 文字。余裕を持たせて 128）。
            e.Property(d => d.ContentFingerprint).HasMaxLength(128);
            e.Property(d => d.Attributes)
                .HasConversion(DictionaryConverter())
                .HasColumnType("jsonb")
                .Metadata.SetValueComparer(DictionaryComparer());
            e.Property(d => d.Tags)
                .HasConversion(ListConverter())
                .HasColumnType("jsonb")
                .Metadata.SetValueComparer(ListComparer());
            // FR-12, ADR-0057 決定 1, [[IADR-0296]]: 図表資産の参照 URI。**`Tags` と同じ jsonb の作法**で
            // 持つ（列を増やさず、要素数が可変の参照集合を 1 列で扱う既存の型に揃える）。
            // 🔴 **`List<string>` 用の変換器を使うこと。** `HasConversion` は非ジェネリック多重定義を
            // 持ち、`List<Guid>` 用（`ListConverter`）を渡してもコンパイルが通る（本ファイル §Tags の実測）。
            e.Property(d => d.AssetUris)
                .HasConversion(StringListConverter())
                .HasColumnType("jsonb")
                .Metadata.SetValueComparer(StringListComparer());

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

        // FR-19, FR-20, ADR-0036 D-06, IADR-0253 決定 4（段 4）: 文書の共有先。
        // 同一文書 × 同一主体の共有は 1 行（重複付与を構造で防ぐ）。文書削除で連動削除する
        // （共有だけが残ると、存在しない文書への到達権が記録として残り続ける）。
        mb.Entity<DocumentShare>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.SubjectType).HasMaxLength(20).IsRequired();
            e.Property(s => s.SubjectId).HasMaxLength(200).IsRequired();
            e.Property(s => s.GrantedBy).HasMaxLength(200).IsRequired();
            e.HasIndex(s => new { s.DocumentId, s.SubjectType, s.SubjectId }).IsUnique();
            e.HasOne<Document>()
                .WithMany()
                .HasForeignKey(s => s.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // FR-19, FR-20, ADR-0037, [[IADR-0270]] 決定 2: 個人資料の台帳（Document と 1:1）。
        // 文書の物理削除（完全削除）で台帳行も連動削除する —— 行が消えることが
        // 「容量から外れる」の実体である（決定 19・20）。
        mb.Entity<PrivateNote>(e =>
        {
            e.HasKey(n => n.DocumentId);
            e.Property(n => n.OwnerId).HasMaxLength(200).IsRequired();
            e.Property(n => n.VaultPath).HasMaxLength(1024).IsRequired();
            e.Property(n => n.ContentHash).HasMaxLength(64);
            e.HasIndex(n => n.OwnerId);
            e.HasOne<Document>()
                .WithOne()
                .HasForeignKey<PrivateNote>(n => n.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // FR-19, NFR-27: 利用者ごとの保存容量（既定 1 GB・最大 1 TB）。
        mb.Entity<PrivateNoteQuota>(e =>
        {
            e.HasKey(q => q.OwnerId);
            e.Property(q => q.OwnerId).HasMaxLength(200);
        });

        // FR-20, ADR-0037 決定 10〜13: 同期端末。トークンはハッシュのみ保存する。
        // ハッシュの一意索引は照合の入口でもある（Bearer トークン → ハッシュ → 端末）。
        mb.Entity<SyncDevice>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.OwnerId).HasMaxLength(200).IsRequired();
            e.Property(d => d.DeviceName).HasMaxLength(200).IsRequired();
            e.Property(d => d.TokenHash).HasMaxLength(64).IsRequired();
            e.HasIndex(d => d.TokenHash).IsUnique();
            e.HasIndex(d => d.OwnerId);
        });

        // FR-09, SC-09, #634: タグ辞書。表示名は**一意**である
        // （SC-09「新しい名前は既存値と重複しない」。追加・改名の両方に効かせる）。
        mb.Entity<Tag>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.Name).HasMaxLength(200).IsRequired();
            e.HasIndex(t => t.Name).IsUnique();
        });
    }

    // FR-06: Attributes(Dictionary) / Tags(List) を jsonb 文字列へ変換する共通定義。
    private static ValueConverter<Dictionary<string, string>, string> DictionaryConverter() => new(
        v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
        v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());

    private static ValueComparer<Dictionary<string, string>> DictionaryComparer() => new(
        (a, b) => System.Text.Json.JsonSerializer.Serialize(a, (System.Text.Json.JsonSerializerOptions?)null) == System.Text.Json.JsonSerializer.Serialize(b, (System.Text.Json.JsonSerializerOptions?)null),
        // ハッシュも等価判定と同じ内容ベースにする（参照 GetHashCode は equals と契約不整合になるため）。
        v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null).GetHashCode(), v => new Dictionary<string, string>(v));

    // **［#635］タグは識別子（`Guid`）の配列である**（[[IADR-0153]] 決定 1。正本は表示名を複写しない）。
    //
    // **型を合わせること自体が守りである。** `HasConversion` には非ジェネリックの多重定義があり、
    // `List<string>` 用の変換器を `List<Guid>` の列へ渡しても**コンパイルは通ってしまう**（実測）。
    // 壊れるのは実行時なので、ここでズレると気づくのがずっと後になる。
    private static ValueConverter<List<Guid>, string> ListConverter() => new(
        v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
        v => System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());

    // FR-12, ADR-0057 決定 1, [[IADR-0296]]: 資産 URI（`List<string>`）用。**タグ（`List<Guid>`）と
    // 別に持つ** —— 型を合わせること自体が守りである（上の §Tags の注記と同じ理由）。
    private static ValueConverter<List<string>, string> StringListConverter() => new(
        v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
        v => System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());

    private static ValueComparer<List<string>> StringListComparer() => new(
        (a, b) => a!.SequenceEqual(b!),
        v => v.Aggregate(0, (h, e) => HashCode.Combine(h, e.GetHashCode())),
        v => v.ToList());

    private static ValueComparer<List<Guid>> ListComparer() => new(
        (a, b) => a!.SequenceEqual(b!),
        v => v.Aggregate(0, (h, e) => HashCode.Combine(h, e.GetHashCode())),
        v => v.ToList());
}
