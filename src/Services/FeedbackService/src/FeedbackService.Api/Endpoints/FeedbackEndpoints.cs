using FeedbackService.Api.Domain;
using FeedbackService.Api.Infrastructure;
using KnowledgePlatform.Shared.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace FeedbackService.Api.Endpoints;

// FR-08, UC-01: 回答へのフィードバック（👍/👎・コメント）収集エンドポイント
public static class FeedbackEndpoints
{
    public static IEndpointRouteBuilder MapFeedbackEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/feedback").WithTags("Feedback");

        // FR-08: フィードバック送信。新規は 201、既存 (AnswerId, UserId) の更新は 200（upsert・冪等）。
        g.MapPost("/", async (FeedbackRequest req, FeedbackDbContext db, HttpContext http,
            CancellationToken ct) =>
        {
            // バリデーション（入力規則）
            if (req.AnswerId == Guid.Empty)
                return Results.BadRequest(new { error = "answerId is required" });
            if (!FeedbackRating.IsValid(req.Rating))
                return Results.BadRequest(new { error = "rating must be 'up' or 'down'" });
            if (req.Comment is { Length: > AnswerFeedback.MaxCommentLength })
                return Results.BadRequest(new
                {
                    error = $"comment must be {AnswerFeedback.MaxCommentLength} characters or fewer"
                });

            // FR-08: JWT から利用者を特定（テスト・開発環境では anonymous）。
            var userId = http.User.Identity?.Name ?? "anonymous";
            var rating = FeedbackRating.Normalize(req.Rating);

            // FR-08: 同一 (AnswerId, UserId) は upsert（二重計上しない。IADR-0010）。
            var existing = await db.Feedback
                .FirstOrDefaultAsync(f => f.AnswerId == req.AnswerId && f.UserId == userId, ct);
            if (existing is not null)
            {
                existing.Update(rating, req.Comment, req.Question);
                await db.SaveChangesAsync(ct);
                return Results.Ok(ToDto(existing));
            }

            var feedback = AnswerFeedback.Create(req.AnswerId, userId, rating, req.Comment, req.Question);
            db.Feedback.Add(feedback);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/feedback/{feedback.Id}", ToDto(feedback));
        }).WithName("SubmitFeedback").Produces<FeedbackDto>(StatusCodes.Status201Created);

        // FR-08: 品質改善向けの一覧。rating / answerId で絞り込み可（低評価回答のレビュー）。
        g.MapGet("/", async (string? rating, Guid? answerId, FeedbackDbContext db,
            CancellationToken ct) =>
        {
            var q = db.Feedback.AsQueryable();
            if (!string.IsNullOrWhiteSpace(rating) && FeedbackRating.IsValid(rating))
            {
                // 正規化はクエリ外で行う（DB プロバイダはカスタムメソッドを翻訳できない）。
                var norm = FeedbackRating.Normalize(rating);
                q = q.Where(f => f.Rating == norm);
            }
            if (answerId is { } aid && aid != Guid.Empty)
                q = q.Where(f => f.AnswerId == aid);

            // ToDto はクライアント側で写像（Select 内のカスタムメソッドは SQL 翻訳不可）。
            var rows = await q.OrderByDescending(f => f.UpdatedAt).ToListAsync(ct);
            return Results.Ok(rows.Select(ToDto).ToList());
        }).WithName("ListFeedback").Produces<List<FeedbackDto>>();

        // FR-08: 集計（👍/👎 件数・満足率）。品質可視化（FR-10 ダッシュボード）の入力。
        g.MapGet("/stats", async (Guid? answerId, FeedbackDbContext db, CancellationToken ct) =>
        {
            var q = db.Feedback.AsQueryable();
            if (answerId is { } aid && aid != Guid.Empty)
                q = q.Where(f => f.AnswerId == aid);

            var up = await q.CountAsync(f => f.Rating == FeedbackRating.Up, ct);
            var down = await q.CountAsync(f => f.Rating == FeedbackRating.Down, ct);
            var total = up + down;
            var rate = total == 0 ? 0d : (double)up / total;
            return Results.Ok(new FeedbackStatsDto(up, down, total, rate));
        }).WithName("FeedbackStats").Produces<FeedbackStatsDto>();

        return app;
    }

    private static FeedbackDto ToDto(AnswerFeedback f)
        => new(f.Id, f.AnswerId, f.Rating, f.Comment, f.Question, f.UserId, f.CreatedAt, f.UpdatedAt);
}
