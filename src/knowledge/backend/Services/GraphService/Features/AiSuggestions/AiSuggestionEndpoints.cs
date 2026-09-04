using GraphService.Domain;
using GraphService.Features.AiSuggestions.Approve;
using GraphService.Features.AiSuggestions.Generate;
using GraphService.Features.AiSuggestions.List;
using GraphService.Features.AiSuggestions.Reject;
using System.Security.Claims;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Infrastructure.Foundation.Extensions;

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

    // FR-18, SC-03, SC-05, ADR-0063 決定 3・4, IADR-0364 決定 3 (#1187): **承認・却下の資格。**
    //
    //   ① 起点文書への `write`（`IsSourceWritableAsync`。ADR-0036 D-07）  **または**
    //   ② SC-05 の管理者経路のロール（`platform-admin`）
    //
    // 🔴 **①だけを要求してはならない。** 取り込み文書の `owner` は予約値 `system` であり、所有者ベースでは
    // 誰も一致しない —— 提案が最も多く付く文書を誰も承認（も却下も）できなくなる（ADR-0063 決定 3 が
    // 名指しで警告した形）。②は新しいロールではなく、`09_datasource-connectors` が「取り込み文書の編集は
    // SC-05 の管理者経路で行う」と定めた経路を承認欄からも使えるようにするものである。
    //
    // 🔴 **②は `platform-admin` だけである。** SC-05「作成・編集は管理者限定」。運用者は含めない
    // （DocumentService の `UpdateMetadata` が `AdminOnly` であることと揃える。ここで緩めると
    // 後段（最終防衛線）が 404 で拒み、承認欄は「できる」と描いたのに失敗する形になる）。
    //
    // **②でも起点文書の実在は要る**（複製に無い文書へは何もしない。呼び出し側は可視性を先に通している）。
    // **承認と却下は同じ判定を通す**（決定 4）。**種別で分けない** —— リンク提案にも②が効く
    // （ADR-0063 §結果「認可の判定が ADR-0059 と揃う。所有者、または管理者経路という同じ形」）。
    internal static async Task<bool> CanDecideAsync(
        AiSuggestion s, Platform.Shared.Contracts.Dtos.AccessScopeResponse writeScope,
        ClaimsPrincipal user, GraphDbContext db, CancellationToken ct)
    {
        if (await IsSourceWritableAsync(s, writeScope, db, ct))
            return true;

        if (!user.IsInRole(PlatformAuthPolicies.AdminRole))
            return false;

        return await db.Documents.AsNoTracking()
            .AnyAsync(d => d.DocumentId == s.SourceDocumentId, ct);
    }

    internal static AiSuggestionDto ToDto(
        AiSuggestion s, string sourceTitle, string? targetTitle, bool canDecide = false)
        => new(
            s.Id, s.Kind, s.SourceDocumentId, s.TargetDocumentId, s.EdgeTypeId, s.TagValue,
            s.Rationale, s.State, s.RejectedCount, s.ReinstatedReason, sourceTitle, targetTitle,
            canDecide);
}
