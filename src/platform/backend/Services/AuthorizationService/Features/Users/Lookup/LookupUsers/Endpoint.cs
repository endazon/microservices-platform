using AuthorizationService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Features.Users.Lookup.LookupUsers;

// FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0098 決定 1, [[IADR-0445]] (#1445):
// 共有先に指定する利用者を名前で探す（部分一致・**有効な利用者だけ**・表示名順）。
//
// 🔴 **`q` は 2 文字以上**である。1 文字を許すと**名簿の総なめに近い列挙**になり、
// 「面に出す項目を 3 つへ閉じた」だけでは釣り合わない（ADR-0098 決定 1 の趣旨は
// 共有先を選べるようにすることであって、名簿を配ることではない）。
//
// 🔴 **自分自身も返り得る**（除くのは画面である）。後段が主体で絞ると、
// 「自分を除く」以外の画面の都合（既に共有済みの相手を除く等）を後段が知る必要が出る。
public static class LookupUsersEndpoint
{
    // 🔴 **既定と上限を型の側に持つ**（画面へ焼き込まない。契約の `default: 20` / `maximum: 50` と対）。
    internal const int DefaultLimit = 20;
    internal const int MaxLimit = 50;
    internal const int MinQueryLength = 2;

    public static IEndpointRouteBuilder MapLookupUsers(this IEndpointRouteBuilder app)
    {
        app.MapGet("/lookup", async (string? q, int? limit, IIdentityAdminClient identity,
            CancellationToken ct) =>
        {
            var query = (q ?? string.Empty).Trim();
            if (query.Length < MinQueryLength)
                return UserAdminEndpoints.ValidationProblem(
                    [$"q は {MinQueryLength} 文字以上で指定してください。"]);

            var take = limit ?? DefaultLimit;
            if (take < 1 || take > MaxLimit)
                return UserAdminEndpoints.ValidationProblem(
                    [$"limit は 1〜{MaxLimit} の範囲で指定してください。"]);

            // **有効な利用者だけ**は後段（IdP）が絞る。ここで絞り直さないのは、`max` の打ち切りが
            // 無効な利用者で埋まって有効な候補が消えるのを防ぐためである（ポートの注記）。
            var found = await identity.SearchUsersAsync(query, take, ct);

            return Results.Ok(found
                .Select(u => new UserSummaryDto(u.Username, u.DisplayName, u.Enabled))
                .ToList());
        });

        return app;
    }
}
