using AuthorizationService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Features.Groups.Lookup.ResolveGroups;

// FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0098 決定 1, [[IADR-0447]] (#1447):
// グループ ID の集合を像へ引く（共有台帳の `subjectId` → 画面に出す表示名とパス）。
//
// 🔴 **無い ID は応答から落ちる。エラーではない。** 共有台帳は IdP の外にあり、台帳に残った
// グループが Keycloak から消えていることは起こり得る（削除・realm の入れ替え）。400 にすると
// **画面が 1 件の不整合で共有先を 1 つも表示できなくなる** —— 取り消すべき相手が見えないのが
// 最悪である（画面は落ちた ID を「見つからないグループ」として描き、取り消しだけできる）。
// `ResolveUsersEndpoint` と**同じ判断**である。
//
// 🔴 **`lookup` では引けない。** あちらは名前の部分一致であり、ID から引く口ではない
// （既存の共有先は ID しか分からない。名前で引き直すと改名で表示が消える）。
public static class ResolveGroupsEndpoint
{
    // 1 回に引ける上限。共有台帳 1 件あたりの共有先がこれを超えることは想定していない
    // （`ResolveUsersEndpoint.MaxUsernames` と同値）。
    internal const int MaxIds = 100;

    public static IEndpointRouteBuilder MapResolveGroups(this IEndpointRouteBuilder app)
    {
        app.MapPost("/resolve", async (ResolveGroupsRequest req, IIdentityAdminClient identity,
            CancellationToken ct) =>
        {
            var ids = req?.Ids;
            if (ids is null || ids.Count == 0)
                return GroupLookupEndpoints.ValidationProblem(["ids は 1 件以上必要です。"]);
            if (ids.Count > MaxIds)
                return GroupLookupEndpoints.ValidationProblem(
                    [$"ids は 1 回に {MaxIds} 件までです。"]);
            // 🔴 **null / 空白の要素は 400 である**（落とさない）。落とすと、要求を組み立てた側の
            // 誤りが「無いグループ」として静かに消える（`ResolveUsersEndpoint` と同じ規則）。
            if (ids.Any(string.IsNullOrWhiteSpace))
                return GroupLookupEndpoints.ValidationProblem(["ids に空の要素を含められません。"]);

            // **要求順を保つ**のは後段の責務である（ポートの注記）。画面が受け取る順は
            // 台帳の順（＝付与順）のままになる。
            var found = await identity.GetGroupsByIdsAsync(
                [.. ids.Select(id => id.Trim())], ct);

            return Results.Ok(found
                .Select(g => new GroupSummaryDto(g.Id, g.Name, g.Path))
                .ToList());
        });

        return app;
    }
}
