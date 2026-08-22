using Microsoft.EntityFrameworkCore;
using NotificationService.Api.Foundation.Domain;

namespace NotificationService.Api.Foundation.Persistence;

// ADR-0002（Database per Service）, IADR-0215 決定 1: NotificationService 専用 DbContext。
// 通知は自前のストアを持つ —— 個人資料と同居させると、通知の保持期間（90 日）と資料の版保持が
// 同じ DB の運用に絡む（IADR-0215 決定 1 の選択肢表）。
public class NotificationDbContext(DbContextOptions<NotificationDbContext> options) : DbContext(options)
{
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<EmailOutboxEntry> EmailOutbox => Set<EmailOutboxEntry>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Notification>(e =>
        {
            e.HasKey(n => n.Id);
            e.Property(n => n.Subject).HasMaxLength(255).IsRequired();
            e.Property(n => n.Kind).HasMaxLength(100).IsRequired();
            // FR-22: 読み出しは**常に主体で絞る**。索引もその形に合わせる
            // （「新しい順に本人の分だけ」が唯一の読み方である）。
            e.HasIndex(n => new { n.Subject, n.OccurredAt });
            // IADR-0215 決定 2: 保持期間の経過分を掃くための索引。
            e.HasIndex(n => n.OccurredAt);
        });

        mb.Entity<EmailOutboxEntry>(e =>
        {
            e.HasKey(o => o.Id);
            e.Property(o => o.Subject).HasMaxLength(255).IsRequired();
            e.Property(o => o.Kind).HasMaxLength(100).IsRequired();
            e.Property(o => o.Status).HasMaxLength(20).IsRequired();
            e.Property(o => o.LastReason).HasMaxLength(200);
            // 送出待ちの取り出し（status ＋ 古い順）と、当日の送信数の数え上げ（SentAt）。
            e.HasIndex(o => new { o.Status, o.CreatedAt });
            e.HasIndex(o => o.SentAt);
        });
    }
}
