using GraphService.Domain;
using GraphService.Domain.Ports;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Features.AiSuggestions.Approve;

// FR-18, SC-03, ADR-0033 決定 7: 承認。**pending からのみ。**
//
// 🔴 **承認は #913 と同じ到達可能性の検証を通す。** 両端が承認者のスコープで可視でなければ
// 拒む —— 見えない文書へ辺を張れると、辺の存在から文書の存在が漏れる。
//
// 🔴 **承認は辺を作る＝書き込みである**（ADR-0033 決定 7「承認済みの提案だけが辺になる」）。
// したがって到達可能性（read）とは別に **write スコープで判定する**（#993 / IADR-0272 決定 2）。
internal static class ApproveAiSuggestionEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/{id:guid}/approve", async (Guid id, IGraphAccessResolver accessResolver,
            IGraphStore store, GraphDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var scope = await accessResolver.ResolveAsync(http, GraphAccessAction.Read, ct);
            if (!scope.Granted) return AiSuggestionEndpoints.NotFound();

            var suggestion = await db.AiSuggestions.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (suggestion is null) return AiSuggestionEndpoints.NotFound();
            var ends = await AiSuggestionEndpoints.ResolveEndpointsAsync(suggestion, scope, db, ct);
            if (!ends.Visible) return AiSuggestionEndpoints.NotFound();

            // ★書き込みの認可★ —— **状態遷移より前に置く**（拒否したときに副作用を残さない）。
            var writeScope = await accessResolver.ResolveAsync(http, GraphAccessAction.Write, ct);
            if (!await AiSuggestionEndpoints.IsSourceWritableAsync(suggestion, writeScope, db, ct))
                return AiSuggestionEndpoints.NotFound();

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
            return Results.Ok(
                AiSuggestionEndpoints.ToDto(suggestion, ends.SourceTitle, ends.TargetTitle));
        }).WithName("ApproveAiSuggestion").Produces<AiSuggestionDto>();
    }
}
