using AuthorizationService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;

namespace AuthorizationService.Features.Users.Lookup.ResolveUsers;

// FR-19, UC-11, SC-19 主要素 3, 計画 ADR-0098 決定 1, [[IADR-0445]] (#1445):
// 利用者名の集合を表示名へ引く（共有台帳の `subjectId` → 画面に出す表示名）。
//
// 🔴 **居ない名前は応答から落ちる。エラーではない。** 共有台帳は IdP の外にあり、
// 台帳に残った利用者が IdP から消えていることは起こり得る（完全削除・realm の入れ替え）。
// 400 にすると**画面が 1 件の不整合で共有先を 1 つも表示できなくなる** ——
// 取り消すべき相手が見えないのが最悪である（画面は落ちた名前を「（不明な利用者）」で描く）。
//
// 🔴 **無効化済み（退職者）も返す。** `enabled=false` を添えて返すので画面が区別できる。
// `lookup`（候補の検索）が退職者を返さないのと**意図的に非対称**である ——
// 新たに指定はさせないが、既にある共有先は見せて取り消させる。
public static class ResolveUsersEndpoint
{
    // 1 回に引ける上限。共有台帳 1 件あたりの共有先がこれを超えることは想定していない。
    internal const int MaxUsernames = 100;

    public static IEndpointRouteBuilder MapResolveUsers(this IEndpointRouteBuilder app)
    {
        app.MapPost("/resolve", async (ResolveUsersRequest req, IIdentityAdminClient identity,
            CancellationToken ct) =>
        {
            var usernames = req?.Usernames;
            if (usernames is null || usernames.Count == 0)
                return UserAdminEndpoints.ValidationProblem(["usernames は 1 件以上必要です。"]);
            if (usernames.Count > MaxUsernames)
                return UserAdminEndpoints.ValidationProblem(
                    [$"usernames は 1 回に {MaxUsernames} 件までです。"]);
            // 🔴 **null / 空白の要素は 400 である**（落とさない）。落とすと、要求を組み立てた側の
            // 誤りが「居ない利用者」として静かに消える。
            if (usernames.Any(string.IsNullOrWhiteSpace))
                return UserAdminEndpoints.ValidationProblem(["usernames に空の要素を含められません。"]);

            // **要求順を保つ。** `Task.WhenAll` は結果の順序を入力順で返すので、
            // 並列に引いても画面が受け取る順は台帳の順（＝付与順）のままである。
            var resolved = await Task.WhenAll(
                usernames.Select(name => identity.FindByUsernameAsync(name.Trim(), ct)));

            return Results.Ok(resolved
                .Where(u => u is not null)
                .Select(u => new UserSummaryDto(u!.Username, u.DisplayName, u.Enabled))
                .ToList());
        });

        return app;
    }
}
