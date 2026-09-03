using GraphService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace GraphService.Infrastructure.Persistence;

// ADR-0002, ADR-0033 決定 1: GraphService 専用 DbContext（DB-per-service）。
public class GraphDbContext(DbContextOptions<GraphDbContext> options) : DbContext(options)
{
    public DbSet<GraphDocument> Documents => Set<GraphDocument>();
    public DbSet<EdgeType> EdgeTypes => Set<EdgeType>();
    public DbSet<Edge> Edges => Set<Edge>();
    // FR-18, ADR-0033 決定 7・10: AI 提案（リンク・タグを同居させる。#914）。
    public DbSet<AiSuggestion> AiSuggestions => Set<AiSuggestion>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // FR-17, ADR-0033 決定 2: ノード（ABAC 属性の複製）。
        mb.Entity<GraphDocument>(e =>
        {
            e.ToTable("graph_documents");
            e.HasKey(d => d.DocumentId);
            e.Property(d => d.Title).HasMaxLength(1000).IsRequired();
            e.Property(d => d.BodyHash).HasMaxLength(128);
            e.Property(d => d.UpdatedAt).IsRequired();
            // FR-10, SC-10, planning#494 決定 2, [[IADR-0353]] (#1186): 本文更新の時刻。
            // **必須である**（既存行はマイグレーションが UpdatedAt を写す）。NULL 可にすると
            // 「不明」が母集合から落ち、指標がほぼ 0 を返し続ける（決定 2 の却下案）。
            // **索引は置かない** —— 走査は 1 時間に 1 回の全件であり、
            // 行数（実データ 2,368 件）に対して索引の維持費の方が高い。
            e.Property(d => d.BodyUpdatedAt).IsRequired();

            // FR-05: 認可の属性軸は増減する（#516 の是正で department 等が入り始める）。
            // jsonb なら**スキーマ無変更で追随できる**。属性ごとに列を切ると軸が 1 つ増えるたびに
            // マイグレーションが要る。DataSourceService.Config と同じ作法。
            e.Property(d => d.Attributes)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                             v, (System.Text.Json.JsonSerializerOptions?)null) ?? new())
                .HasColumnType("jsonb")
                .Metadata.SetValueComparer(new ValueComparer<Dictionary<string, string>>(
                    (a, b) => a != null && b != null
                        ? a.Count == b.Count && !a.Except(b).Any()
                        : a == b,
                    d => d.Aggregate(0, (acc, kv) => HashCode.Combine(acc, kv.Key.GetHashCode(), kv.Value.GetHashCode())),
                    d => new Dictionary<string, string>(d)));
        });

        // FR-17, SC-09, ADR-0033 決定 3・9: 辺の型辞書（実行時管理。コード定義にしない）。
        mb.Entity<EdgeType>(e =>
        {
            e.ToTable("edge_types");
            e.HasKey(t => t.Id);
            e.Property(t => t.Name).HasMaxLength(100).IsRequired();
            e.Property(t => t.Layer).HasMaxLength(20).IsRequired();
            e.Property(t => t.IsSymmetric).IsRequired();
            e.Property(t => t.IsSeed).IsRequired();
            // FR-04, ADR-0035 決定 2 (#947a): 二段検索の再ランクで使う重み（#970 が使う。現時点は未使用）。
            e.Property(t => t.Weight).IsRequired().HasDefaultValue(EdgeType.DefaultWeight);

            // SC-09: 「新しい名前は既存値と重複しない」。正規化後の名前で一意。
            e.HasIndex(t => t.Name).IsUnique().HasDatabaseName("ux_edge_types_name");
        });

        // FR-18, ADR-0033 決定 7・10: AI 提案（#914）。
        //
        // **リンク提案とタグ提案を 1 表に同居させる。** SC-21 が「画面を分けると片方が忘れられる」
        // として同一一覧を求めており、表を分けると一覧の実装が 2 経路に割れて同じ事故を招く。
        // 種別ごとに使う列が違うため、**種別固有の列は NULL 可**にする。
        mb.Entity<AiSuggestion>(e =>
        {
            e.ToTable("ai_suggestions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(10).IsRequired();
            e.Property(x => x.SourceDocumentId).IsRequired();
            // リンク提案のみ。タグ提案では NULL。
            e.Property(x => x.TargetDocumentId);
            e.Property(x => x.EdgeTypeId);
            // タグ提案のみ。
            e.Property(x => x.TagValue).HasMaxLength(100);
            e.Property(x => x.Rationale).HasMaxLength(2000).IsRequired().HasDefaultValue(string.Empty);
            e.Property(x => x.State).HasMaxLength(10).IsRequired();

            // ADR-0033 §結果 フォローアップ: 却下回数。**しきい値を後から入れられるように数える。**
            e.Property(x => x.RejectedCount).IsRequired().HasDefaultValue(0);
            e.Property(x => x.RejectedAt);
            // 決定 10: 却下時点の**本文指紋**。本文そのものは持たない。
            e.Property(x => x.SourceFingerprint).HasMaxLength(128);
            e.Property(x => x.TargetFingerprint).HasMaxLength(128);
            // 決定 10 の具体化: 解除は削除ではなく**無効化として記録**する。
            e.Property(x => x.ReinstatedAt);
            e.Property(x => x.ReinstatedReason).HasMaxLength(10);
            e.Property(x => x.CreatedAt).IsRequired();

            // SC-21: 既定は state=pending の一覧。状態で引く索引を置く。
            e.HasIndex(x => x.State).HasDatabaseName("ix_ai_suggestions_state");
            // 決定 7 の具体化「却下された組み合わせは以後の提案生成で候補から除外する」——
            // 生成側（#915）が組み合わせで引く。
            e.HasIndex(x => new { x.SourceDocumentId, x.TargetDocumentId })
                .HasDatabaseName("ix_ai_suggestions_endpoints");

            // 🔴 **辺の型への外部キーを張らない。**
            // 張ると、参照している提案が pending / rejected でも型の削除が RESTRICT で拒まれる。
            // ADR-0033 決定 9 が削除を拒む条件は「**辺**が参照していること」であり、提案は辺ではない
            // （未承認の提案は辺を持たない）。外部キーを張ると決定 9 より厳しい規則を勝手に作ることになる。
        });

        // FR-17, ADR-0033 決定 4・5・6: 辺。
        mb.Entity<Edge>(e =>
        {
            e.ToTable("edges");
            e.HasKey(x => x.Id);
            e.Property(x => x.SourceDocumentId).IsRequired();
            e.Property(x => x.TargetDocumentId).IsRequired();
            e.Property(x => x.EdgeTypeId).IsRequired();
            e.Property(x => x.Provenance).HasMaxLength(20).IsRequired();

            // ADR-0033 決定 5: Phase 3（チャンク粒度）の予約欄。
            //
            // **NULL ではなく空文字を「文書単位」の表現に使う。** 一意制約に参加する列を NULL 可に
            // すると、PostgreSQL の既定では NULL 同士が相異なるものとして扱われ、
            // **同一の (source, target, type) を何行でも入れられてしまう** —— 予約列のせいで
            // 重複防止が壊れるのは本末転倒である。COALESCE 式索引や NULLS NOT DISTINCT でも
            // 表現できるが、空文字の既定値なら素の一意制約がそのまま効き、EF の
            // モデルスナップショットとも齟齬が出ない。
            e.Property(x => x.SourceAnchor).HasMaxLength(200).IsRequired().HasDefaultValue(string.Empty);
            e.Property(x => x.TargetAnchor).HasMaxLength(200).IsRequired().HasDefaultValue(string.Empty);

            // ADR-0033 決定 6, IADR-0281 (#912): 自動抽出の起点文書。
            // **NULL 可でよい**（一意制約 ux_edges に参加しないため、NULL 同士が相異なる扱いでも
            // 重複防止は壊れない）。利用者付与・AI 承認済みの辺では NULL である。
            e.Property(x => x.ExtractedFrom);
            // 差分更新の母集合（provenance=auto かつ起点が当該文書）を引く索引。
            e.HasIndex(x => x.ExtractedFrom).HasDatabaseName("ix_edges_extracted_from");

            // ADR-0033 決定 9: **参照が 1 件でもある型は削除できない**（削除ガードの最後の防壁）。
            // サービス層は事前カウントで 409 ＋使用件数を返し、RESTRICT 例外を素の 500 で漏らさない（#910）。
            e.HasOne<EdgeType>()
                .WithMany()
                .HasForeignKey(x => x.EdgeTypeId)
                .OnDelete(DeleteBehavior.Restrict);

            // ADR-0033 決定 6: 最新版のみ保持（差分更新）。同一の関係が二重に入らないようにする。
            e.HasIndex(x => new
            {
                x.SourceDocumentId,
                x.TargetDocumentId,
                x.EdgeTypeId,
                x.SourceAnchor,
                x.TargetAnchor,
            }).IsUnique().HasDatabaseName("ux_edges");

            e.HasIndex(x => x.SourceDocumentId).HasDatabaseName("ix_edges_source");
            // FR-17: **バックリンクの逆引き。** 行を増やさずに被参照を辿るための索引。
            e.HasIndex(x => x.TargetDocumentId).HasDatabaseName("ix_edges_target");
            // SC-10: 型別の使用件数（#910）。削除ガードの事前カウントと同一クエリ。
            e.HasIndex(x => x.EdgeTypeId).HasDatabaseName("ix_edges_type");
        });

        // 🔴 **graph_documents への外部キーは張らない。**
        // リンク抽出（#912）はノード同期（#911）より先に届き得るし、ノードレコードの欠落は
        // 「不可視」として認可側で吸収される（IADR-0242 決定 12-3）。FK を張るとイベントの
        // 到着順に人工的な依存が生まれる。
    }
}
