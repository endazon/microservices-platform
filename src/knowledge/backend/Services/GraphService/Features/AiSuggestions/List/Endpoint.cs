using GraphService.Domain;
using GraphService.Domain.Ports;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;
using Microsoft.EntityFrameworkCore;

namespace GraphService.Features.AiSuggestions.List;

// FR-18, SC-21: 一覧。**リンク提案とタグ提案を同居させる**（画面を分けない）。
//
// 🔴 **権限のない文書に関する提案は一覧にも件数にも現れない**（SC-21 アクセス制御）。
// 判定は既存の `AbacNodeFilter` に委ねる —— 提案そのものは属性を持たないので、
// **端点の文書の属性で判定する**。端点が引けない提案は出さない（deny-closed）。
internal static class ListAiSuggestionsEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/", async (string? state, string? kind, IGraphAccessResolver accessResolver,
            GraphDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (state is not null && state != AiSuggestionEndpoints.AnyState
                && !SuggestionState.IsValid(state))
                return Results.BadRequest(new { error = "invalid_state" });
            if (kind is not null && !SuggestionKind.IsValid(kind))
                return Results.BadRequest(new { error = "invalid_kind" });

            var scope = await accessResolver.ResolveAsync(http, GraphAccessAction.Read, ct);
            if (!scope.Granted) return Results.Ok(new List<AiSuggestionDto>());

            // SC-21: 既定は pending。`all` は絞りを外す（状態そのものではない）。
            var wanted = state ?? SuggestionState.Pending;

            var query = db.AiSuggestions.AsNoTracking();
            if (state != AiSuggestionEndpoints.AnyState) query = query.Where(s => s.State == wanted);
            if (kind is not null) query = query.Where(s => s.Kind == kind);
            var rows = await query.OrderBy(s => s.CreatedAt).ToListAsync(ct);

            var visible = new List<AiSuggestionDto>();
            foreach (var s in rows)
            {
                var ends = await AiSuggestionEndpoints.ResolveEndpointsAsync(s, scope, db, ct);
                if (!ends.Visible) continue;
                // SC-21 主要素 1: 一覧は**両端の文書名**を描く（#918）。可視性の判定で既に
                // 読んでいる複製をそのまま使うので、照会は 1 件も増えない。
                visible.Add(AiSuggestionEndpoints.ToDto(s, ends.SourceTitle, ends.TargetTitle));
            }
            return Results.Ok(visible);
        }).WithName("ListAiSuggestions").Produces<List<AiSuggestionDto>>();
    }
}
