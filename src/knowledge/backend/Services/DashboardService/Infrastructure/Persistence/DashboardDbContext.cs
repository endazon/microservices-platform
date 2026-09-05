using DashboardService.Domain;
using Microsoft.EntityFrameworkCore;

namespace DashboardService.Infrastructure.Persistence;

// ADR-0002: DashboardService 専用 DbContext（DB-per-service）
public class DashboardDbContext(DbContextOptions<DashboardDbContext> options) : DbContext(options)
{
    public DbSet<UsageEvent> UsageEvents => Set<UsageEvent>();

    // FR-10, FR-17, FR-18, SC-10 (#443): ナレッジ健全性の観測値。
    public DbSet<KnowledgeHealthObservation> KnowledgeHealthObservations
        => Set<KnowledgeHealthObservation>();

    // FR-10, SC-10, planning#494 決定 3 (#1186): 指標ごとの現在のしきい値。
    public DbSet<KnowledgeHealthIndicatorThreshold> KnowledgeHealthIndicatorThresholds
        => Set<KnowledgeHealthIndicatorThreshold>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<UsageEvent>(e =>
        {
            e.HasKey(u => u.Id);
            e.Property(u => u.EventType).HasMaxLength(16).IsRequired();
            e.Property(u => u.Query).HasMaxLength(UsageEvent.MaxQueryLength);
            e.Property(u => u.OccurredAt).IsRequired();

            // FR-10: 期間フィルタ・種別集計を効率化するインデックス。
            // **保持期間の削除（UsageEventRetention）も同じ索引で引く**
            // （述語は `OccurredAt < 基準時刻` であり、集計の `>=` の否定である）。
            e.HasIndex(u => new { u.OccurredAt, u.EventType });
        });

        // FR-10, FR-17, FR-18, SC-10 (#443): ナレッジ健全性の観測値。
        mb.Entity<KnowledgeHealthObservation>(e =>
        {
            e.HasKey(o => o.Id);
            e.Property(o => o.Indicator)
                .HasMaxLength(KnowledgeHealthObservation.MaxIndicatorLength).IsRequired();
            e.Property(o => o.SubjectKey)
                .HasMaxLength(KnowledgeHealthObservation.MaxSubjectKeyLength).IsRequired();
            e.Property(o => o.DocScope)
                .HasMaxLength(KnowledgeHealthObservation.MaxDocScopeLength);
            // ★［2026-09-05 / #1246］内訳の軸。**NULL 可**（軸を持たない指標がある）。
            e.Property(o => o.Dimension)
                .HasMaxLength(KnowledgeHealthObservation.MaxDimensionLength);
            e.Property(o => o.ObservedAt).IsRequired();

            // 指標ごとの置換・集計を効率化する。DocScope を含めるのは、
            // 個人資料の除外が**毎回の集計で必ず走る**述語だからである。
            //
            // 🔴 **Dimension は索引へ足さない**（#1246）。閲覧は全行を読み込んでから
            // メモリ上で畳んでおり、軸での絞り込みを DB へ投げていない。
            // 使われない列を複合索引へ足すと**書き込み側（1 時間ごとの全量置換）の費用だけが増える**。
            e.HasIndex(o => new { o.Indicator, o.DocScope });
        });

        // FR-10, SC-10, planning#494 決定 3 (#1186): 指標ごとの現在のしきい値。
        // **指標名が主キー**である（1 指標につき 1 行。観測値と違い集合ではない）。
        mb.Entity<KnowledgeHealthIndicatorThreshold>(e =>
        {
            e.HasKey(t => t.Indicator);
            e.Property(t => t.Indicator)
                .HasMaxLength(KnowledgeHealthObservation.MaxIndicatorLength).IsRequired();
            e.Property(t => t.ThresholdDays).IsRequired();
            e.Property(t => t.ReportedAt).IsRequired();
        });
    }
}
