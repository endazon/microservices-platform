using DashboardService.Api.Foundation.Domain;
using Microsoft.EntityFrameworkCore;

namespace DashboardService.Api.Foundation.Persistence;

// ADR-0002: DashboardService 専用 DbContext（DB-per-service）
public class DashboardDbContext(DbContextOptions<DashboardDbContext> options) : DbContext(options)
{
    public DbSet<UsageEvent> UsageEvents => Set<UsageEvent>();

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
    }
}
