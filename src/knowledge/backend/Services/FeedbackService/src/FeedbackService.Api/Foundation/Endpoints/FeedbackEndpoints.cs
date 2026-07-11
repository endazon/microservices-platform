using FeedbackService.Api.Foundation.Domain;
using FeedbackService.Api.Foundation.Persistence;
using Knowledge.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Microsoft.EntityFrameworkCore;

namespace FeedbackService.Api.Foundation.Endpoints;

// FR-08, UC-01: 回答へのフィードバック（👍/👎・コメント）収集エンドポイント
public static class FeedbackEndpoints
{
    // FR-08: 一覧のページング既定・上限（無制限な全件返却を防ぐ）。
    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 500;
    // FR-10: 満足率の集計期間の上限（DashboardService の利用状況集計と揃える）。
    private const int MaxStatsDays = 90;

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

            // FR-08, IADR-0010: read-then-write は非アトミック。ほぼ同時の 2 重送信（ダブルクリック・
            // クライアント再試行）は両方が「既存なし」と判定し、後勝ちの INSERT が一意制約
            // (IX_Feedback_AnswerId_UserId) 違反で DbUpdateException を投げる。捕捉して既存行の
            // 更新へフォールバックし、冪等（1 利用者 1 回答 1 行）を保つ。
            // ※ AuthorizationService の属性辞書登録と同じ「事前確認 + 競合捕捉」パターン。
            var feedback = AnswerFeedback.Create(req.AnswerId, userId, rating, req.Comment, req.Question);
            db.Feedback.Add(feedback);
            try
            {
                await db.SaveChangesAsync(ct);
                return Results.Created($"/feedback/{feedback.Id}", ToDto(feedback));
            }
            catch (DbUpdateException)
            {
                // 競合した INSERT を破棄し、相手が先に作成した行を読み直して更新する。
                db.Entry(feedback).State = EntityState.Detached;
                var winner = await db.Feedback
                    .FirstOrDefaultAsync(f => f.AnswerId == req.AnswerId && f.UserId == userId, ct);
                if (winner is null)
                    throw; // 一意制約違反以外（想定外）はそのまま伝播させる。
                winner.Update(rating, req.Comment, req.Question);
                await db.SaveChangesAsync(ct);
                return Results.Ok(ToDto(winner));
            }
        }).WithName("SubmitFeedback").Produces<FeedbackDto>(StatusCodes.Status201Created);

        // FR-08: 品質改善向けの一覧。rating / answerId で絞り込み可（低評価回答のレビュー）。
        // NFR（認可）: 自由記述 Comment と UserId（個人特定情報）を返すため、管理者ロールのみ許可する。
        //   AuthorizationService の管理系 CRUD と同じ AdminOnly ポリシーで保護する。
        //   一方 /feedback/stats は集計値のみ（PII なし）で BFF ダッシュボードが匿名集約するため対象外。
        g.MapGet("/", async (string? rating, Guid? answerId, int? skip, int? take,
            FeedbackDbContext db, CancellationToken ct) =>
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

            // FR-08: 全件無制限返却を防ぐページング。skip>=0 / 1<=take<=MaxPageSize にクランプする。
            var pageSkip = Math.Max(0, skip ?? 0);
            var pageTake = Math.Clamp(take ?? DefaultPageSize, 1, MaxPageSize);

            // ToDto はクライアント側で写像（Select 内のカスタムメソッドは SQL 翻訳不可）。
            var rows = await q.OrderByDescending(f => f.UpdatedAt)
                .Skip(pageSkip).Take(pageTake).ToListAsync(ct);
            return Results.Ok(rows.Select(ToDto).ToList());
        }).WithName("ListFeedback").RequireAuthorization(PlatformAuthPolicies.AdminOnly)
          .Produces<List<FeedbackDto>>();

        // FR-08: 集計（👍/👎 件数・満足率）。品質可視化（FR-10 ダッシュボード）の入力。
        // 集計値のみで PII を含まないため一覧のような AdminOnly は課さない（BFF が集約して画面へ）。
        //   days — FR-10: 期間指定（日数）。指定時はその範囲に絞る。未指定は全期間（後方互換）。
        g.MapGet("/stats", async (Guid? answerId, int? days, FeedbackDbContext db, CancellationToken ct) =>
        {
            var q = db.Feedback.AsQueryable();
            if (answerId is { } aid && aid != Guid.Empty)
                q = q.Where(f => f.AnswerId == aid);
            // FR-10: 期間指定があれば絞り込む。ダッシュボードの利用状況と満足率の期間を揃えるため、
            // BFF は利用状況と同じ days を渡す（DashboardService と同一のクランプ・起点算出）。
            if (days is { } d)
            {
                var since = SinceUtc(d);
                q = q.Where(f => f.CreatedAt >= since);
            }

            var up = await q.CountAsync(f => f.Rating == FeedbackRating.Up, ct);
            var down = await q.CountAsync(f => f.Rating == FeedbackRating.Down, ct);
            var total = up + down;
            var rate = total == 0 ? 0d : (double)up / total;
            return Results.Ok(new FeedbackStatsDto(up, down, total, rate));
        }).WithName("FeedbackStats").Produces<FeedbackStatsDto>();

        return app;
    }

    // FR-10: days を [1, MaxStatsDays] にクランプし、集計開始時刻（UTC 当日 00:00 を含む起点）を求める。
    //   DashboardService.SinceUtc と同一のロジック（利用状況と満足率の期間の起点を揃える）。
    private static DateTimeOffset SinceUtc(int days)
    {
        var clamped = Math.Clamp(days, 1, MaxStatsDays);
        var startDate = DateTimeOffset.UtcNow.UtcDateTime.Date.AddDays(-(clamped - 1));
        return new DateTimeOffset(startDate, TimeSpan.Zero);
    }

    private static FeedbackDto ToDto(AnswerFeedback f)
        => new(f.Id, f.AnswerId, f.Rating, f.Comment, f.Question, f.UserId, f.CreatedAt, f.UpdatedAt);
}
