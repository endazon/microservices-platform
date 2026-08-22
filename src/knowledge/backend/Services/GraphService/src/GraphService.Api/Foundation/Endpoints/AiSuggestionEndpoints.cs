using GraphService.Api.Foundation.Domain;
using GraphService.Api.Foundation.Persistence;
using GraphService.Api.Foundation.Ports;
using GraphService.Api.Foundation.Services;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Api.Foundation.Endpoints;

// FR-18, SC-21, SC-03, ADR-0033 決定 7・10: AI 提案の 3 状態遷移（#914）。
//
// 🔴 **一括承認の口を置かない。** FR-18 と SC-21「描いてはいけないもの」が明示的に禁じている。
// 理由は「タイトルだけを見て機械的に承認する運用に落ちる」であり、承認は両端の文書の内容を
// 見て判断すべきものである。**存在しないことを `AiSuggestionEndpointsTests` が固定する。**
//
// 承認・却下は 1 件ずつ。SC-21 は一覧（従）で、承認の主導線は SC-03（文書詳細）である。
public static class AiSuggestionEndpoints
{
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
            if (state is not null && !SuggestionState.IsValid(state))
                return Results.BadRequest(new { error = "invalid_state" });
            if (kind is not null && !SuggestionKind.IsValid(kind))
                return Results.BadRequest(new { error = "invalid_kind" });

            var scope = await accessResolver.ResolveAsync(http, ct);
            if (!scope.Granted) return Results.Ok(new List<AiSuggestionDto>());

            // SC-21: 既定は pending。
            var wanted = state ?? SuggestionState.Pending;

            var query = db.AiSuggestions.AsNoTracking().Where(s => s.State == wanted);
            if (kind is not null) query = query.Where(s => s.Kind == kind);
            var rows = await query.OrderBy(s => s.CreatedAt).ToListAsync(ct);

            var visible = new List<AiSuggestionDto>();
            foreach (var s in rows)
            {
                if (!await IsVisibleAsync(s, scope, db, ct)) continue;
                visible.Add(ToDto(s));
            }
            return Results.Ok(visible);
        }).WithName("ListAiSuggestions").Produces<List<AiSuggestionDto>>();

        // FR-18, SC-03, ADR-0033 決定 7: 承認。**pending からのみ。**
        //
        // 🔴 **承認は #913 と同じ到達可能性の検証を通す。** 両端が承認者のスコープで可視でなければ
        // 拒む —— 見えない文書へ辺を張れると、辺の存在から文書の存在が漏れる。
        g.MapPost("/{id:guid}/approve", async (Guid id, IGraphAccessResolver accessResolver,
            IGraphStore store, GraphDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var scope = await accessResolver.ResolveAsync(http, ct);
            if (!scope.Granted) return NotFound();

            var suggestion = await db.AiSuggestions.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (suggestion is null) return NotFound();
            if (!await IsVisibleAsync(suggestion, scope, db, ct)) return NotFound();

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
            // 反映の経路は #915 / #918 で決める（本 issue は状態遷移までを射程とする）。

            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(suggestion));
        }).WithName("ApproveAiSuggestion").Produces<AiSuggestionDto>();

        // FR-18, ADR-0033 決定 7・10: 却下。**pending からのみ。**
        //
        // 却下回数を増やし、両端の**本文指紋**を控える（解除の判定に使う）。
        // 指紋は呼び出し側が与える —— 本サービスは本文を持たない。
        g.MapPost("/{id:guid}/reject", async (Guid id, RejectAiSuggestionRequest? req,
            IGraphAccessResolver accessResolver, GraphDbContext db, HttpContext http,
            TimeProvider clock, CancellationToken ct) =>
        {
            var scope = await accessResolver.ResolveAsync(http, ct);
            if (!scope.Granted) return NotFound();

            var suggestion = await db.AiSuggestions.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (suggestion is null) return NotFound();
            if (!await IsVisibleAsync(suggestion, scope, db, ct)) return NotFound();

            if (!suggestion.TryReject(req?.SourceFingerprint, req?.TargetFingerprint,
                    clock.GetUtcNow()))
                return Results.Conflict(new { error = "invalid_transition", state = suggestion.State });

            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(suggestion));
        }).WithName("RejectAiSuggestion").Produces<AiSuggestionDto>();

        return app;
    }

    // ADR-0034 決定 2: 権限外は 404 に倒す（403 は「権限が無いだけで存在はする」ことを漏らす）。
    private static IResult NotFound() => Results.NotFound();

    // SC-21: 端点の文書が可視でない提案は出さない。
    //
    // **端点が 1 つでも引けない／見えないなら不可視**とする（deny-closed）。リンク提案は両端、
    // タグ提案は対象文書 1 件を見る。
    private static async Task<bool> IsVisibleAsync(
        AiSuggestion s, Platform.Shared.Contracts.Dtos.AccessScopeResponse scope,
        GraphDbContext db, CancellationToken ct)
    {
        var source = await db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.DocumentId == s.SourceDocumentId, ct);
        if (source is null || AuthorizedNode.Authorize(source, scope) is null) return false;

        if (s.TargetDocumentId is null) return true;

        var target = await db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.DocumentId == s.TargetDocumentId.Value, ct);
        return target is not null && AuthorizedNode.Authorize(target, scope) is not null;
    }

    private static AiSuggestionDto ToDto(AiSuggestion s) => new(
        s.Id, s.Kind, s.SourceDocumentId, s.TargetDocumentId, s.EdgeTypeId, s.TagValue,
        s.Rationale, s.State, s.RejectedCount, s.ReinstatedReason);
}
