using GraphService.Domain;
using GraphService.Infrastructure.Persistence;
using GraphService.Domain.Ports;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Features.AiSuggestions;

// FR-18, SC-21, SC-03, ADR-0033 決定 7・10: AI 提案の 3 状態遷移（#914）。
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

        // FR-18, SC-21: 一覧。**リンク提案とタグ提案を同居させる**（画面を分けない）。
        //
        // 🔴 **権限のない文書に関する提案は一覧にも件数にも現れない**（SC-21 アクセス制御）。
        // 判定は既存の `AbacNodeFilter` に委ねる —— 提案そのものは属性を持たないので、
        // **端点の文書の属性で判定する**。端点が引けない提案は出さない（deny-closed）。
        g.MapGet("/", async (string? state, string? kind, IGraphAccessResolver accessResolver,
            GraphDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (state is not null && state != AnyState && !SuggestionState.IsValid(state))
                return Results.BadRequest(new { error = "invalid_state" });
            if (kind is not null && !SuggestionKind.IsValid(kind))
                return Results.BadRequest(new { error = "invalid_kind" });

            var scope = await accessResolver.ResolveAsync(http, GraphAccessAction.Read, ct);
            if (!scope.Granted) return Results.Ok(new List<AiSuggestionDto>());

            // SC-21: 既定は pending。`all` は絞りを外す（状態そのものではない）。
            var wanted = state ?? SuggestionState.Pending;

            var query = db.AiSuggestions.AsNoTracking();
            if (state != AnyState) query = query.Where(s => s.State == wanted);
            if (kind is not null) query = query.Where(s => s.Kind == kind);
            var rows = await query.OrderBy(s => s.CreatedAt).ToListAsync(ct);

            var visible = new List<AiSuggestionDto>();
            foreach (var s in rows)
            {
                var ends = await ResolveEndpointsAsync(s, scope, db, ct);
                if (!ends.Visible) continue;
                // SC-21 主要素 1: 一覧は**両端の文書名**を描く（#918）。可視性の判定で既に
                // 読んでいる複製をそのまま使うので、照会は 1 件も増えない。
                visible.Add(ToDto(s, ends.SourceTitle, ends.TargetTitle));
            }
            return Results.Ok(visible);
        }).WithName("ListAiSuggestions").Produces<List<AiSuggestionDto>>();

        // FR-18, SC-03, ADR-0033 決定 7: 承認。**pending からのみ。**
        //
        // 🔴 **承認は #913 と同じ到達可能性の検証を通す。** 両端が承認者のスコープで可視でなければ
        // 拒む —— 見えない文書へ辺を張れると、辺の存在から文書の存在が漏れる。
        //
        // 🔴 **承認は辺を作る＝書き込みである**（ADR-0033 決定 7「承認済みの提案だけが辺になる」）。
        // したがって到達可能性（read）とは別に **write スコープで判定する**（#993 / IADR-0272 決定 2）。
        g.MapPost("/{id:guid}/approve", async (Guid id, IGraphAccessResolver accessResolver,
            IGraphStore store, GraphDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var scope = await accessResolver.ResolveAsync(http, GraphAccessAction.Read, ct);
            if (!scope.Granted) return NotFound();

            var suggestion = await db.AiSuggestions.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (suggestion is null) return NotFound();
            var ends = await ResolveEndpointsAsync(suggestion, scope, db, ct);
            if (!ends.Visible) return NotFound();

            // ★書き込みの認可★ —— **状態遷移より前に置く**（拒否したときに副作用を残さない）。
            var writeScope = await accessResolver.ResolveAsync(http, GraphAccessAction.Write, ct);
            if (!await IsSourceWritableAsync(suggestion, writeScope, db, ct)) return NotFound();

            if (!suggestion.TryApprove())
                return Results.Conflict(new { error = "invalid_transition", state = suggestion.State });

            // ADR-0033 決定 7: 承認済みの提案だけが辺になる。
            if (suggestion.Kind == SuggestionKind.Link)
            {
                var edgeType = await db.EdgeTypes.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == suggestion.EdgeTypeId, ct);
                if (edgeType is null)
                    return Results.BadRequest(new { error = "unknown_edge_type" });

                var edge = Edge.Create(
                    suggestion.SourceDocumentId, suggestion.TargetDocumentId!.Value,
                    edgeType.Id, edgeType.IsSymmetric, EdgeProvenance.AiApproved);

                var duplicate = await db.Edges.AnyAsync(e =>
                    e.SourceDocumentId == edge.SourceDocumentId
                    && e.TargetDocumentId == edge.TargetDocumentId
                    && e.EdgeTypeId == edge.EdgeTypeId
                    && e.SourceAnchor == edge.SourceAnchor
                    && e.TargetAnchor == edge.TargetAnchor, ct);
                if (!duplicate) db.Edges.Add(edge);
            }
            // タグ提案の反映先は DocumentService のタグであり、本サービスは辺を持たない。
            // 反映の経路は #918 で決める（#915 は生成までを射程とし、承認後の反映は含まない）。

            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(suggestion, ends.SourceTitle, ends.TargetTitle));
        }).WithName("ApproveAiSuggestion").Produces<AiSuggestionDto>();

        // FR-18, ADR-0033 決定 7・10: 却下。**pending からのみ。**
        //
        // 🔴 **却下も書き込みである** —— 提案は端点が見える利用者に共有される行であり、却下すると
        // **他の利用者の pending 一覧からも消える**。read しか持たない主体が他人の提案を握り潰せる
        // 形にしない（#993 / IADR-0272 決定 2）。
        //
        // 却下回数を増やし、両端の**本文指紋**を控える（解除の判定に使う）。
        // 指紋は呼び出し側が与える —— 本サービスは本文を持たない。
        g.MapPost("/{id:guid}/reject", async (Guid id, RejectAiSuggestionRequest? req,
            IGraphAccessResolver accessResolver, GraphDbContext db, HttpContext http,
            TimeProvider clock, CancellationToken ct) =>
        {
            var scope = await accessResolver.ResolveAsync(http, GraphAccessAction.Read, ct);
            if (!scope.Granted) return NotFound();

            var suggestion = await db.AiSuggestions.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (suggestion is null) return NotFound();
            var ends = await ResolveEndpointsAsync(suggestion, scope, db, ct);
            if (!ends.Visible) return NotFound();

            // ★書き込みの認可★ —— **状態遷移より前に置く**（拒否したときに副作用を残さない）。
            var writeScope = await accessResolver.ResolveAsync(http, GraphAccessAction.Write, ct);
            if (!await IsSourceWritableAsync(suggestion, writeScope, db, ct)) return NotFound();

            if (!suggestion.TryReject(req?.SourceFingerprint, req?.TargetFingerprint,
                    clock.GetUtcNow()))
                return Results.Conflict(new { error = "invalid_transition", state = suggestion.State });

            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(suggestion, ends.SourceTitle, ends.TargetTitle));
        }).WithName("RejectAiSuggestion").Produces<AiSuggestionDto>();

        // FR-18, ADR-0034 決定 5, ADR-0051 決定 1〜4, IADR-0266 (#915): **提案の生成。**
        //
        // 🔴 **1 実行 = 1 利用者のスコープ**である（ADR-0051 決定 4 の唯一の要件）。要求から解決した
        // スコープが、その実行で LLM へ渡せるものを決める。
        //
        // 🔴 **応答は生成できた提案の配列のみ。** 「候補が N 件あった」「N 件落とした」を返さない
        // （ADR-0051 決定 2「件数・存在も出さない」）。起点が見えない場合は 404（403 ではない）。
        //
        // 🔴 **本経路は read で解決する**（#993 / IADR-0272 決定 6）。提案行を書きはするが、
        // **正しいアクションは `analyze` である可能性が高く、計画は `analyze` の判定規則を
        // 定めていない**（値域に列挙するだけである）。推測で write を当てると生成が全件遮断される。
        // ADR-0051 決定 4 は本経路の不変条件を「1 実行 = 1 利用者のスコープ」だけとしており、
        // read で解決する現状が計画に反しているとは読めない。**裁定待ちとして範囲の外に置く。**
        g.MapPost("/generate/{documentId:guid}", async (Guid documentId,
            IGraphAccessResolver accessResolver, AiSuggestionGenerator generator,
            GraphDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var scope = await accessResolver.ResolveAsync(http, GraphAccessAction.Read, ct);

            var created = await generator.GenerateAsync(documentId, scope, ct);
            // 「存在しない」と「見えない」を同じ 404 に倒す（ADR-0034 決定 2）。
            if (created is null) return NotFound();

            // #918: 応答の形を一覧とそろえる（表示名つき）。**同じスコープで解決し直す** ——
            // 生成側が許可済み候補しか採らないことに依存せず、公開面へ出す名前は必ず
            // `ResolveEndpointsAsync` を通す（1 か所でしか名前を出さない）。
            var dtos = new List<AiSuggestionDto>();
            foreach (var c in created)
            {
                var ends = await ResolveEndpointsAsync(c, scope, db, ct);
                dtos.Add(ToDto(c, ends.SourceTitle, ends.TargetTitle));
            }
            return Results.Ok(dtos);
        }).WithName("GenerateAiSuggestions").Produces<List<AiSuggestionDto>>();

        return app;
    }

    // ADR-0034 決定 2: 権限外は 404 に倒す（403 は「権限が無いだけで存在はする」ことを漏らす）。
    private static IResult NotFound() => Results.NotFound();

    // SC-21: 端点の文書が可視でない提案は出さない。**あわせて表示名を返す**（#918）。
    //
    // **端点が 1 つでも引けない／見えないなら不可視**とする（deny-closed）。リンク提案は両端、
    // タグ提案は対象文書 1 件を見る。
    //
    // 🔴 **不可視のときは表示名を返さない**（空文字・null のまま）。可視性の判定を通っていない
    // 名前を戻り値へ載せると、呼び出し側の書き間違い 1 つで文書名が漏れる形になる。
    private static async Task<(bool Visible, string SourceTitle, string? TargetTitle)>
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
    private static async Task<bool> IsSourceWritableAsync(
        AiSuggestion s, Platform.Shared.Contracts.Dtos.AccessScopeResponse writeScope,
        GraphDbContext db, CancellationToken ct)
    {
        var source = await db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.DocumentId == s.SourceDocumentId, ct);
        return source is not null && AuthorizedNode.Authorize(source, writeScope) is not null;
    }

    private static AiSuggestionDto ToDto(AiSuggestion s, string sourceTitle, string? targetTitle)
        => new(
            s.Id, s.Kind, s.SourceDocumentId, s.TargetDocumentId, s.EdgeTypeId, s.TagValue,
            s.Rationale, s.State, s.RejectedCount, s.ReinstatedReason, sourceTitle, targetTitle);
}
