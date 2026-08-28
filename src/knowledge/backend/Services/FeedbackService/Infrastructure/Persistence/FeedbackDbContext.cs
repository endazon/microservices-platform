using FeedbackService.Domain;
using Microsoft.EntityFrameworkCore;

namespace FeedbackService.Infrastructure.Persistence;

// ADR-0002: FeedbackService 専用 DbContext（DB-per-service）
public class FeedbackDbContext(DbContextOptions<FeedbackDbContext> options) : DbContext(options)
{
    public DbSet<AnswerFeedback> Feedback => Set<AnswerFeedback>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<AnswerFeedback>(e =>
        {
            e.HasKey(f => f.Id);
            e.Property(f => f.AnswerId).IsRequired();
            e.Property(f => f.Rating).HasMaxLength(4).IsRequired();
            e.Property(f => f.Comment).HasMaxLength(AnswerFeedback.MaxCommentLength);
            e.Property(f => f.Question).HasMaxLength(AnswerFeedback.MaxQuestionLength);
            e.Property(f => f.UserId).HasMaxLength(256).IsRequired();
            e.Property(f => f.CreatedAt).IsRequired();
            e.Property(f => f.UpdatedAt).IsRequired();

            // FR-08: 1 ユーザー 1 回答 1 フィードバック（upsert の基盤。IADR-0010）。
            e.HasIndex(f => new { f.AnswerId, f.UserId }).IsUnique();
        });
    }
}
