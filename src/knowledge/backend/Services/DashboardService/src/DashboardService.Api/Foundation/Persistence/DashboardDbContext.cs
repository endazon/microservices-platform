using DashboardService.Api.Foundation.Domain;
using Microsoft.EntityFrameworkCore;

namespace DashboardService.Api.Foundation.Persistence;

// ADR-0002: DashboardService 専用 DbContext（DB-per-service）
public class DashboardDbContext(DbContextOptions<DashboardDbContext> options) : DbContext(options)
{
    public DbSet<UsageEvent> UsageEvents => Set<UsageEvent>();

    // FR-10, FR-17, FR-18, SC-10 (#443): ナレッジ健全性の観測値。
    public DbSet<KnowledgeHealthObservation> KnowledgeHealthObservations
        => Set<KnowledgeHealthObservation>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<UsageEvent>(e =>
        {
            e.HasKey(u => u.Id);
            e.Property(u => u.EventType).HasMaxLength(16).IsRequired();
            e.Property(u => u.Query).HasMaxLength(UsageEvent.MaxQueryLength);
            e.Property(u => u.UserId).HasMaxLength(256).IsRequired();
            e.Property(u => u.OccurredAt).IsRequired();

            // FR-10: 期間フィルタ・種別集計を効率化するインデックス。
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
            e.Property(o => o.ObservedAt).IsRequired();

            // 指標ごとの置換・集計を効率化する。DocScope を含めるのは、
            // 個人資料の除外が**毎回の集計で必ず走る**述語だからである。
            e.HasIndex(o => new { o.Indicator, o.DocScope });
        });
    }
}
