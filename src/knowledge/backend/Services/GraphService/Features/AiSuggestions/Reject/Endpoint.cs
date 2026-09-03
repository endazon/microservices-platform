using GraphService.Domain;
using GraphService.Domain.Ports;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Features.AiSuggestions.Reject;

// FR-18, ADR-0033 決定 7・10: 却下。**pending からのみ。**
//
// 🔴 **却下も書き込みである** —— 提案は端点が見える利用者に共有される行であり、却下すると
// **他の利用者の pending 一覧からも消える**。read しか持たない主体が他人の提案を握り潰せる
// 形にしない（#993 / IADR-0272 決定 2）。
//
// 却下回数を増やし、両端の**本文指紋**を控える（解除の判定に使う）。
// 指紋は呼び出し側が与える —— 本サービスは本文を持たない。
//
// FR-18, ADR-0063 決定 3・4, IADR-0361 決定 3 (#1187): **承認と同じ資格**（①起点文書への write
// **または** ②管理者ロール）で判定する。却下レコードは永久保持され再提案を止める（ADR-0033 決定 10）
// ので、却下だけを誰にでも開かない。②が無いと取り込み文書（`owner=system`）の提案は誰も却下できない。
internal static class RejectAiSuggestionEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/{id:guid}/reject", async (Guid id, RejectAiSuggestionRequest? req,
            IGraphAccessResolver accessResolver, GraphDbContext db, HttpContext http,
            TimeProvider clock, CancellationToken ct) =>
        {
            var scope = await accessResolver.ResolveAsync(http, GraphAccessAction.Read, ct);
            if (!scope.Granted) return AiSuggestionEndpoints.NotFound();

            var suggestion = await db.AiSuggestions.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (suggestion is null) return AiSuggestionEndpoints.NotFound();
            var ends = await AiSuggestionEndpoints.ResolveEndpointsAsync(suggestion, scope, db, ct);
            if (!ends.Visible) return AiSuggestionEndpoints.NotFound();

            // ★書き込みの認可★ —— **状態遷移より前に置く**（拒否したときに副作用を残さない）。
            var writeScope = await accessResolver.ResolveAsync(http, GraphAccessAction.Write, ct);
            if (!await AiSuggestionEndpoints.CanDecideAsync(suggestion, writeScope, http.User, db, ct))
                return AiSuggestionEndpoints.NotFound();

            if (!suggestion.TryReject(req?.SourceFingerprint, req?.TargetFingerprint,
                    clock.GetUtcNow()))
                return Results.Conflict(new { error = "invalid_transition", state = suggestion.State });

            await db.SaveChangesAsync(ct);
            return Results.Ok(AiSuggestionEndpoints.ToDto(
                suggestion, ends.SourceTitle, ends.TargetTitle, canDecide: true));
        }).WithName("RejectAiSuggestion").Produces<AiSuggestionDto>();
    }
}
