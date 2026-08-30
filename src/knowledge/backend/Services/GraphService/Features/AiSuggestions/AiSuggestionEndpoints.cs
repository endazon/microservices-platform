using GraphService.Domain;
using GraphService.Features.AiSuggestions.Approve;
using GraphService.Features.AiSuggestions.Generate;
using GraphService.Features.AiSuggestions.List;
using GraphService.Features.AiSuggestions.Reject;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Features.AiSuggestions;

// FR-18, SC-21, SC-03, ADR-0033 決定 7・10: AI 提案の 3 状態遷移（#914）の合成点。
//
// ADR-0065 決定 2: 各ユースケースの実体は `Features/AiSuggestions/<操作>/` に居る。
// **ここに残すのは、操作をまたいで共有されるもの**だけである —— route group、
// 状態フィルタの解除値、404 の生成点、端点解決（可視性 ＋ 表示名）、write 判定、DTO 変換。
//
// 🔴 **一括承認の口を置かない。** FR-18 と SC-21「描いてはいけないもの」が明示的に禁じている。
// 理由は「タイトルだけを見て機械的に承認する運用に落ちる」であり、承認は両端の文書の内容を
// 見て判断すべきものである。**存在しないことを `AiSuggestionEndpointsTests` が固定する。**
//
// 承認・却下は 1 件ずつ。SC-21 は一覧（従）で、承認の主導線は SC-03（文書詳細）である。
public static class AiSuggestionEndpoints
{
    // FR-18, SC-21 (#918): 状態フィルタの 4 値目「すべて」。
    //
    // 🔴 **`SuggestionState` の値集合へは足さない。** これは**状態の値ではなくフィルタの解除**であり、
    // `SuggestionState.IsValid` に混ぜると永続層へ `all` という状態を書ける形になる。
    public const string AnyState = "all";

    public static IEndpointRouteBuilder MapAiSuggestionEndpoints(this IEndpointRouteBuilder app)
    {
        // NFR-09: 認証のみ。可視性は ABAC が決める（提案は文書に紐づく）。
        var g = app.MapGroup("/graph/suggestions").WithTags("AiSuggestions").RequireAuthorization();

        ListAiSuggestionsEndpoint.Map(g);
        ApproveAiSuggestionEndpoint.Map(g);
        RejectAiSuggestionEndpoint.Map(g);
        GenerateAiSuggestionsEndpoint.Map(g);

        return app;
    }

    // ADR-0034 決定 2: 権限外は 404 に倒す（403 は「権限が無いだけで存在はする」ことを漏らす）。
    internal static IResult NotFound() => Results.NotFound();

    // SC-21: 端点の文書が可視でない提案は出さない。**あわせて表示名を返す**（#918）。
    //
    // **端点が 1 つでも引けない／見えないなら不可視**とする（deny-closed）。リンク提案は両端、
    // タグ提案は対象文書 1 件を見る。
    //
    // 🔴 **不可視のときは表示名を返さない**（空文字・null のまま）。可視性の判定を通っていない
    // 名前を戻り値へ載せると、呼び出し側の書き間違い 1 つで文書名が漏れる形になる。
    internal static async Task<(bool Visible, string SourceTitle, string? TargetTitle)>
        ResolveEndpointsAsync(
            AiSuggestion s, Platform.Shared.Contracts.Dtos.AccessScopeResponse scope,
            GraphDbContext db, CancellationToken ct)
    {
        var source = await db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.DocumentId == s.SourceDocumentId, ct);
        if (source is null || AuthorizedNode.Authorize(source, scope) is null)
            return (false, string.Empty, null);

        if (s.TargetDocumentId is null) return (true, source.Title, null);

        var target = await db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.DocumentId == s.TargetDocumentId.Value, ct);
        return target is not null && AuthorizedNode.Authorize(target, scope) is not null
            ? (true, source.Title, target.Title)
            : (false, string.Empty, null);
    }

    // #993, IADR-0272 決定 3: 提案の**起点文書**が write スコープで許可されているか。
    //
    // **Granted だけを見ない** —— write ポリシーの文書条件を捨てると、狭い write 権限で
    // 範囲外の文書を触れてしまう。述語は直接呼ばず型ゲート（AuthorizedNode.Authorize）を通す。
    // **終点は見ない**（ADR-0034 決定 8 が終点に課すのは閲覧権限である）。
    internal static async Task<bool> IsSourceWritableAsync(
        AiSuggestion s, Platform.Shared.Contracts.Dtos.AccessScopeResponse writeScope,
        GraphDbContext db, CancellationToken ct)
    {
        var source = await db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.DocumentId == s.SourceDocumentId, ct);
        return source is not null && AuthorizedNode.Authorize(source, writeScope) is not null;
    }

    internal static AiSuggestionDto ToDto(AiSuggestion s, string sourceTitle, string? targetTitle)
        => new(
            s.Id, s.Kind, s.SourceDocumentId, s.TargetDocumentId, s.EdgeTypeId, s.TagValue,
            s.Rationale, s.State, s.RejectedCount, s.ReinstatedReason, sourceTitle, targetTitle);
}
