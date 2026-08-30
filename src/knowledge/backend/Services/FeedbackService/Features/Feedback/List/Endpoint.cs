using FeedbackService.Domain;
using FeedbackService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace FeedbackService.Features.Feedback.List;

// FR-08: 品質改善向けの一覧。rating / answerId で絞り込み可（低評価回答のレビュー）。
// NFR（認可）: 自由記述 Comment と UserId（個人特定情報）を返すため、管理者ロールのみ許可する。
//   AuthorizationService の管理系 CRUD と同じ AdminOnly ポリシーで保護する。
// ［2026-08-10 是正 / #521・IADR-0158］従前ここには「/feedback/stats は集計値のみ（PII なし）
//   なので対象外」と書いていたが、**その根拠は計画側で失効していた**。計画 FR-08 は
//   「統計は運用者・管理者に限って参照できる」を確定している（裁定依頼 planning#236 案 2）。
//   **判断軸は PII の有無ではなく「権限で絞るか」である。** /stats へ RequireRole を足した。
internal static class ListFeedbackEndpoint
{
    // FR-08: 一覧のページング既定・上限（無制限な全件返却を防ぐ）。
    // **一覧だけが使う**ため 3 段目に置く（ADR-0068 決定 2）。
    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 500;

    internal static void Map(RouteGroupBuilder g)
    {
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
            return Results.Ok(rows.Select(FeedbackEndpoints.ToDto).ToList());
        }).WithName("ListFeedback").RequireAuthorization(PlatformAuthPolicies.AdminOnly)
          .Produces<List<FeedbackDto>>();
    }
}
