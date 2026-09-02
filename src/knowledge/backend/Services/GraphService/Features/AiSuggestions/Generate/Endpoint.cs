using GraphService.Domain.Ports;
using GraphService.Infrastructure.Persistence;
using Knowledge.Contracts.Dtos;

namespace GraphService.Features.AiSuggestions.Generate;

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
//
// 生成器（`AiSuggestionGenerator`）は**同じフォルダに居る**（#1093 / IADR-0351）。使う操作が
// この 1 つだけなので ADR-0068 決定 2 により 3 段目へ下ろした。DI へ登録されることも、端点を
// 介さず直接検証されることも、段の判定には関わらない（IADR-0319 決定 1）。
internal static class GenerateAiSuggestionsEndpoint
{
    internal static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/generate/{documentId:guid}", async (Guid documentId,
            IGraphAccessResolver accessResolver, AiSuggestionGenerator generator,
            GraphDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var scope = await accessResolver.ResolveAsync(http, GraphAccessAction.Read, ct);

            var created = await generator.GenerateAsync(documentId, scope, ct);
            // 「存在しない」と「見えない」を同じ 404 に倒す（ADR-0034 決定 2）。
            if (created is null) return AiSuggestionEndpoints.NotFound();

            // #918: 応答の形を一覧とそろえる（表示名つき）。**同じスコープで解決し直す** ——
            // 生成側が許可済み候補しか採らないことに依存せず、公開面へ出す名前は必ず
            // `ResolveEndpointsAsync` を通す（1 か所でしか名前を出さない）。
            var dtos = new List<AiSuggestionDto>();
            foreach (var c in created)
            {
                var ends = await AiSuggestionEndpoints.ResolveEndpointsAsync(c, scope, db, ct);
                dtos.Add(AiSuggestionEndpoints.ToDto(c, ends.SourceTitle, ends.TargetTitle));
            }
            return Results.Ok(dtos);
        }).WithName("GenerateAiSuggestions").Produces<List<AiSuggestionDto>>();
    }
}
