using FeedbackService.Domain;
using FeedbackService.Infrastructure.Persistence;
using FluentValidation;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Kernel;

namespace FeedbackService.Features.Feedback.Submit;

// FR-08: フィードバック送信。新規は 201、既存 (AnswerId, UserId) の更新は 200（upsert・冪等）。
internal static class SubmitFeedbackEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/", async (FeedbackRequest req, IValidator<FeedbackRequest> validator,
            FeedbackDbContext db, HttpContext http, CancellationToken ct) =>
        {
            // FR-08 / IADR-0371 決定 2・4: 入力検証（FluentValidation）と利用者特定という
            // **2 つの失敗経路を Kernel の Result で 1 本に束ね、HTTP への写像は 1 度だけ行う**
            // （計画 ADR-0030 §決定「ProblemDetails 変換は API 層」/ ADR-0041 §結果）。
            // 判定の順序は移送前のガード節と同じ（検証 → 利用者特定）であり、
            // 返す状態コードも本文も変わらない。
            var gate = Validate(validator, req).Bind(() => Identify(http));
            if (gate.IsFailure)
            {
                // ErrorKind で HTTP を決める。**分岐は 1 箇所に閉じる。**
                return gate.Error.Kind == ErrorKind.Unauthorized
                    ? Results.Unauthorized()
                    : Results.BadRequest(new { error = gate.Error.Message });
            }

            // FR-08: 利用者は `Identify` が特定済みである（失敗は上の 401 で返り切っている）。
            var userId = gate.Value;
            var rating = FeedbackRating.Normalize(req.Rating);

            // FR-08: 同一 (AnswerId, UserId) は upsert（二重計上しない。IADR-0010）。
            var existing = await db.Feedback
                .FirstOrDefaultAsync(f => f.AnswerId == req.AnswerId && f.UserId == userId, ct);
            if (existing is not null)
            {
                existing.Update(rating, req.Comment, req.Question);
                await db.SaveChangesAsync(ct);
                return Results.Ok(FeedbackMapper.ToDto(existing));
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
                return Results.Created($"/feedback/{feedback.Id}", FeedbackMapper.ToDto(feedback));
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
                return Results.Ok(FeedbackMapper.ToDto(winner));
            }
        }).WithName("SubmitFeedback")
          // FR-08（計画確定 2026-08-07・`02_requirements:33`）: **投稿は認証必須・匿名は許さない。**
          // **ロールは要求しない**——計画が定めていない制限を足すと一般利用者が投稿できなくなる。
          // BFF 側にも同じ認可がある（[[IADR-0044]] 多層防御）。
          .RequireAuthorization().Produces<FeedbackDto>(StatusCodes.Status201Created);
    }

    // FR-08 / IADR-0371 決定 2: 入力規則の判定。**規則そのものは `SubmitFeedbackValidator` が持つ。**
    //
    // 🔴 **`Errors[0]` を採る。** FluentValidation は既定で全規則を走らせるため、
    // 移送前の「最初の違反で 400 を返す」と同じ本文にするには最初の失敗を採るしかない。
    // 規則の宣言順が応答の契約の一部になっている（同 Validator のコメントを参照）。
    private static Result Validate(IValidator<FeedbackRequest> validator, FeedbackRequest req)
    {
        var result = validator.Validate(req);
        return result.IsValid
            ? Result.Success()
            : Result.Failure(Error.Validation("feedback.submit.invalid", result.Errors[0].ErrorMessage));
    }

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
    private static Result<string> Identify(HttpContext http)
    {
        var userId = http.User.Identity?.Name;
        return string.IsNullOrEmpty(userId)
            ? Result<string>.Failure(Error.Unauthorized(
                "feedback.submit.unauthenticated", "投稿者を特定できない（認証が必要である）。"))
            : Result<string>.Success(userId);
    }
}
