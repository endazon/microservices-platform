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
//
// ── FR-18, SC-03, SC-05, SC-09, ADR-0063 決定 1〜3, IADR-0364 (#1187 / #1014): **タグ提案の反映**
//
// タグ提案の反映先は DocumentService のタグである（本サービスは辺を持たない）。承認者本人の資格で
// `POST /documents/{id}/tags` を呼び（`IDocumentTagWriter`。方式 A の権限伝播）、**反映が確定して
// から状態を `approved` にする** —— 反映できていないのに承認済みと見えるのが最悪である。
//   - 辞書に無い値 → **400 `unknown_tag`**。状態は `pending` のまま（承認できず却下のみ。決定 2 後段）。
//     `unknown_edge_type` と同じ形（提案が指す値が辞書に無い）。
//   - 後段が拒んだ（所有者でも管理者でもない／文書が無い）→ 404（存在秘匿の一本道）
//   - 後段へ到達できない → 502（**成功へ縮退しない**）
// 認可は**②（管理者ロール）を含む選言**である（`CanDecideAsync`。決定 3）。
internal static class ApproveAiSuggestionEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/{id:guid}/approve", async (Guid id, IGraphAccessResolver accessResolver,
            IGraphStore store, IDocumentTagWriter tagWriter, GraphDbContext db, HttpContext http,
            CancellationToken ct) =>
        {
            var scope = await accessResolver.ResolveAsync(http, GraphAccessAction.Read, ct);
            if (!scope.Granted) return AiSuggestionEndpoints.NotFound();

            var suggestion = await db.AiSuggestions.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (suggestion is null) return AiSuggestionEndpoints.NotFound();
            var ends = await AiSuggestionEndpoints.ResolveEndpointsAsync(suggestion, scope, db, ct);
            if (!ends.Visible) return AiSuggestionEndpoints.NotFound();

            // ★書き込みの認可★ —— **状態遷移より前に置く**（拒否したときに副作用を残さない）。
            // ①起点文書への write **または** ②管理者ロール（ADR-0063 決定 3）。
            var writeScope = await accessResolver.ResolveAsync(http, GraphAccessAction.Write, ct);
            if (!await AiSuggestionEndpoints.CanDecideAsync(suggestion, writeScope, http.User, db, ct))
                return AiSuggestionEndpoints.NotFound();

            // 遷移できない状態なら**後段を呼ぶ前に** 409 で返す（書いてから 409 にしない）。
            if (suggestion.State != SuggestionState.Pending)
                return Results.Conflict(new { error = "invalid_transition", state = suggestion.State });

            if (suggestion.Kind == SuggestionKind.Tag)
            {
                // ADR-0063 決定 1・2: **反映が確定してから承認する。** 辞書の権威は後段にある。
                var outcome = await tagWriter.AddTagAsync(
                    suggestion.SourceDocumentId, suggestion.TagValue!, ct);
                switch (outcome)
                {
                    case TagWriteOutcome.UnknownTag:
                        return Results.BadRequest(new { error = "unknown_tag" });
                    case TagWriteOutcome.NotWritable:
                        return AiSuggestionEndpoints.NotFound();
                    case TagWriteOutcome.Unavailable:
                        return Results.StatusCode(StatusCodes.Status502BadGateway);
                }
            }

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

            await db.SaveChangesAsync(ct);
            return Results.Ok(AiSuggestionEndpoints.ToDto(
                suggestion, ends.SourceTitle, ends.TargetTitle, canDecide: true));
        }).WithName("ApproveAiSuggestion").Produces<AiSuggestionDto>();
    }
}
