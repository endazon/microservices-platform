using AuthorizationService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Features.Groups.Lookup.LookupGroups;

// FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0098 決定 1・3, [[IADR-0447]] (#1447):
// 共有先に指定するグループを名前で探す（部分一致・大小文字無視・**パス順**）。
//
// 🔴 **`q` は 2 文字以上**である（`LookupUsersEndpoint` と同じ規則・同じ理由）。1 文字を許すと
// **グループ木の総なめに近い列挙**になり、「面を 3 項目へ閉じた」だけでは釣り合わない。
//
// 🔴 **並びはパス順である**（利用者検索が表示名順なのと**意図的に違う**）。グループは同名が
// 起こりやすく（`/teams/finance` と `/department/finance`）、表示名順では同名が隣り合って
// **どちらを選んだのか画面で区別できない**。パス順なら階層ごとに並ぶ。
//
// 🔴 **総件数は返さない**（`ADR-0100` 決定 2 と同じ向き）。件数は「絞り込みの手掛かり」だが、
// 名簿の規模そのものの開示でもある —— 上限で打ち切った像だけを返す。
public static class LookupGroupsEndpoint
{
    // 🔴 **既定と上限を型の側に持つ**（画面へ焼き込まない。契約の `default: 20` / `maximum: 50` と対）。
    internal const int DefaultLimit = 20;
    internal const int MaxLimit = 50;
    internal const int MinQueryLength = 2;

    public static IEndpointRouteBuilder MapLookupGroups(this IEndpointRouteBuilder app)
    {
        app.MapGet("/lookup", async (string? q, int? limit, IIdentityAdminClient identity,
            CancellationToken ct) =>
        {
            var query = (q ?? string.Empty).Trim();
            if (query.Length < MinQueryLength)
                return GroupLookupEndpoints.ValidationProblem(
                    [$"q は {MinQueryLength} 文字以上で指定してください。"]);

            var take = limit ?? DefaultLimit;
            if (take < 1 || take > MaxLimit)
                return GroupLookupEndpoints.ValidationProblem(
                    [$"limit は 1〜{MaxLimit} の範囲で指定してください。"]);

            // 平坦化・部分一致・パス順・打ち切りはすべて後段（IdP）の規則である
            // （ポートの注記。ここで絞り直すと本物と偽物で答えが分かれる）。
            var found = await identity.SearchGroupsAsync(query, take, ct);

            return Results.Ok(found
                .Select(g => new GroupSummaryDto(g.Id, g.Name, g.Path))
                .ToList());
        });

        return app;
    }
}
