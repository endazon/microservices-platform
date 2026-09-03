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
//
// FR-18, SC-03 (#1104, [[IADR-0323]]): **文書での絞り込み（`documentId`）を持つ。**
// SC-03（文書詳細）の承認欄は「当該文書を両端のいずれかとする提案」だけを描くため、
// 従前は権限内の `pending` 全件を転送して画面側で間引いていた。**秘匿の欠陥ではなく規模の
// 欠陥**（無制限の転送 ＋ 下のループの N+1 が全件に対して回る）であり、絞りを端点へ移した。
internal static class ListAiSuggestionsEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/", async (string? state, string? kind, Guid? documentId,
            IGraphAccessResolver accessResolver,
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

            // FR-18, SC-03 (#1104): 🔴 **可視性解決の前段に置く。** 判定の後ろに置いたり、
            // 判定を迂回してはならない —— 下のループ（端点の文書属性による ABAC）が唯一の
            // 秘匿の実施点であり、ここは**転送量を減らすだけ**の絞りである。
            //
            // **タグ提案も拾える**（`TargetDocumentId` は null だが `SourceDocumentId` が対象文書）。
            // 🔴 **不存在・権限外の文書 ID でも 404 に倒さない**（[[IADR-0323]] 決定 2）。
            // 一致 0 件でも、可視性解決で全件落ちても、応答は同じ `200 + []` である ——
            // **「その文書は無い」と「その文書の提案は 0 件」を区別させない**（ADR-0034 決定 2）。
            if (documentId is { } target)
                query = query.Where(s => s.SourceDocumentId == target || s.TargetDocumentId == target);

            var rows = await query.OrderBy(s => s.CreatedAt).ToListAsync(ct);

            // FR-18, SC-03, ADR-0063 決定 3〜5, IADR-0361 決定 4 (#1187): 行ごとに**承認・却下の資格**
            // （`CanDecide`）を載せる。SPA はこの値だけで「承認できる／権限が無い」を分けて描く。
            // write スコープは**要求ごとに 1 回**だけ解決し、**可視な行が 1 つも無ければ解決しない**
            // （往復を増やさない）。判定そのものは承認・却下の口と同じ `CanDecideAsync` である。
            Platform.Shared.Contracts.Dtos.AccessScopeResponse? writeScope = null;

            var visible = new List<AiSuggestionDto>();
            foreach (var s in rows)
            {
                var ends = await AiSuggestionEndpoints.ResolveEndpointsAsync(s, scope, db, ct);
                if (!ends.Visible) continue;
                writeScope ??= await accessResolver.ResolveAsync(http, GraphAccessAction.Write, ct);
                var canDecide = await AiSuggestionEndpoints.CanDecideAsync(
                    s, writeScope, http.User, db, ct);
                // SC-21 主要素 1: 一覧は**両端の文書名**を描く（#918）。可視性の判定で既に
                // 読んでいる複製をそのまま使うので、照会は 1 件も増えない。
                visible.Add(AiSuggestionEndpoints.ToDto(s, ends.SourceTitle, ends.TargetTitle, canDecide));
            }
            return Results.Ok(visible);
        }).WithName("ListAiSuggestions").Produces<List<AiSuggestionDto>>();
    }
}
