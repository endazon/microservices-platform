using FeedbackService.Domain;
using FeedbackService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace FeedbackService.Features.Feedback.Submit;

// FR-08: フィードバック送信。新規は 201、既存 (AnswerId, UserId) の更新は 200（upsert・冪等）。
internal static class SubmitFeedbackEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
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

            // FR-08: JWT から利用者を特定する。
            //
            // **［2026-08-10 / #521］`anonymous` フォールバックを消した。** 従前は
            // `http.User.Identity?.Name ?? "anonymous"` であり、#586 の追記が「計画と食い違う。
            // 是正は #521 が持つ」と予告していたものである。
            //
            // **フォールバックの害は「無認証で投稿できる」だけではなかった。**
            // `(AnswerId, UserId)` にユニーク索引がある（`FeedbackDbContext`）ため、
            // **無認証の投稿は全員が `"anonymous"` という 1 行を共有し、互いに上書きし合っていた**
            // ——「1 利用者 1 件」の upsert が、匿名に対しては「**全匿名で 1 件**」として働く。
            // 指標の汚染に加えて**他人の投稿の改変**にあたる。
            //
            // 端点に `RequireAuthorization` を付けたので通常ここへ無認証は到達しないが、
            // **フォールバックを残すと「認可を外したときに静かに匿名共有へ戻る」**。
            // 名前が取れない場合は 401 を返す（[[IADR-0039]] 決定 3: 無認証は 401）。
            var userId = http.User.Identity?.Name;
            if (string.IsNullOrEmpty(userId))
                return Results.Unauthorized();
            var rating = FeedbackRating.Normalize(req.Rating);

            // FR-08: 同一 (AnswerId, UserId) は upsert（二重計上しない。IADR-0010）。
            var existing = await db.Feedback
                .FirstOrDefaultAsync(f => f.AnswerId == req.AnswerId && f.UserId == userId, ct);
            if (existing is not null)
            {
                existing.Update(rating, req.Comment, req.Question);
                await db.SaveChangesAsync(ct);
                return Results.Ok(FeedbackEndpoints.ToDto(existing));
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
                return Results.Created($"/feedback/{feedback.Id}", FeedbackEndpoints.ToDto(feedback));
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
                return Results.Ok(FeedbackEndpoints.ToDto(winner));
            }
        }).WithName("SubmitFeedback")
          // FR-08（計画確定 2026-08-07・`02_requirements:33`）: **投稿は認証必須・匿名は許さない。**
          // **ロールは要求しない**——計画が定めていない制限を足すと一般利用者が投稿できなくなる。
          // BFF 側にも同じ認可がある（[[IADR-0044]] 多層防御）。
          .RequireAuthorization().Produces<FeedbackDto>(StatusCodes.Status201Created);
    }
}
