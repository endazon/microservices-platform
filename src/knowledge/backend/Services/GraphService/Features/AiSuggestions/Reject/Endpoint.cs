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
// ［2026-10-30 / #2144］指紋は自分の複製（取り込みの同期が保つ `BodyHash`）から読む。本文そのものは持たないが、指紋は持っている。
//
// FR-18, ADR-0063 決定 3・4, IADR-0364 決定 3 (#1187): **承認と同じ資格**（①起点文書への write
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

            // FR-18, ADR-0033 決定 10, [[IADR-0300]] 決定 3 (#2144): **両端の本文指紋は自分の複製（`Documents.BodyHash`）から読む。**
            // BFF は本文を送らない（SPA は指紋を持てない。IADR-0276 決定 2）ので、従前は常に null で控えていた。解除の発火は外側の門
            // （本文指紋の変化）で決まるので変わらないが、`ReinstatedReason` が初回の解除で `both` に化けていた。
            // 複製に指紋が無い（取り込み前・本文なし）ときだけ呼び出し側の値を使う（内部の呼び出し元の互換）。
            var endpointIds = suggestion.TargetDocumentId is { } targetId
                ? new[] { suggestion.SourceDocumentId, targetId }
                : [suggestion.SourceDocumentId];
            var hashes = await db.Documents.AsNoTracking()
                .Where(d => endpointIds.Contains(d.DocumentId))
                .ToDictionaryAsync(d => d.DocumentId, d => d.BodyHash, ct);
            var sourceFingerprint = hashes.GetValueOrDefault(suggestion.SourceDocumentId) ?? req?.SourceFingerprint;
            var targetFingerprint = suggestion.TargetDocumentId is { } t
                ? hashes.GetValueOrDefault(t) ?? req?.TargetFingerprint
                : null;

            if (!suggestion.TryReject(sourceFingerprint, targetFingerprint, clock.GetUtcNow()))
                return Results.Conflict(new { error = "invalid_transition", state = suggestion.State });

            await db.SaveChangesAsync(ct);
            return Results.Ok(AiSuggestionMapper.ToDto(
                suggestion, ends.SourceTitle, ends.TargetTitle, canDecide: true));
        }).WithName("RejectAiSuggestion").Produces<AiSuggestionDto>();
    }
}
