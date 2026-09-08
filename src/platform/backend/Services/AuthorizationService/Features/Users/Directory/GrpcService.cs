using AuthorizationService.Domain.Ports;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Platform.Shared.Contracts.Grpc.Authz.V1;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace AuthorizationService.Features.Users.Directory;

// FR-05, FR-16, UC-04, UC-09, SC-06, SC-12, NFR-09, NFR-16, ADR-0004, ADR-0029, ADR-0062, ADR-0064,
// ADR-0074, ADR-0075, IADR-0329, IADR-0379, IADR-0385, IADR-0401 (#1255):
// 利用者名簿の **s2s 向けの狭い読み口**（gRPC 面）。
//
// 🔴 **REST の `GET /authz/users`（AdminOnly・全件列挙）を写したものではない**（IADR-0401 決定 2）。
//   従前、DataSourceService と McpServer は**利用者の `Authorization` を転送**して AdminOnly の列挙を
//   通していた。east-west の面へ利用者トークンを載せないという規約（IADR-0379 決定 4 /
//   docs/api/east-west-grpc.md §4）を守るため、**呼び出し元が実際に要る問いだけ**を面へ出す ——
//   「この利用者名は実在するか」と「この 1 人の属性は何か」である。
//   **列挙と書き込みはこの面に存在しない。** 出すと `platform-service` を持つ全サービスが
//   名簿全件を引けることになり、コード注記が名指しで避けた経路そのものになる。
//
// 🔴 **ServiceCaller を要求する。** 利用者のトークンは（管理者であっても）通らない ——
//   通すと呼び出し先が「利用者が直接呼んだ」と区別できず confused deputy が成立する。
//   これを機械で守るのは `GrpcUserDirectoryTests` の「管理者トークンでも PERMISSION_DENIED」1 本である。
//
// ■ 残余リスク（IADR-0401 決定 2 に明記・受容）
//   `platform-service` を持つサービスは「名指しした 1 人の**真の**属性」を読める。これは
//   `AuthzScope/Resolve` が呼び出し元の**主張する**属性をそのまま評価に使うのと同じ信頼であり
//   （偽の属性を主張できる方が強い）、境界も同型（ClusterIP ＋ NetworkPolicy ＋ STRICT mTLS）である。
//
// ■ 後段は変わらない
//   `IIdentityAdminClient`（`identity-admin`。`view-users` を持つ主体は 1 つのまま。IADR-0329 決定 1）。
//   by-username の口は現行のポートに無いので、当面 `ListUsersAsync` の上で絞る ——
//   **絞り方は呼び出し元が今やっているものと同一**であり、挙動は変わらない（最適化は別 issue）。
//
// ■ 🔴「居ない」と「引けなかった」を分ける
//   居ないのは**応答**（`exists=false` / `found=false`）、引けなかったのは **gRPC status**。
//   後段が落ちていることを「その利用者は存在しません」と報告するのは嘘である。
[Authorize(Policy = PlatformAuthPolicies.ServiceCaller)]
public sealed class UserDirectoryGrpcService(IIdentityAdminClient identity)
    : UserDirectory.UserDirectoryBase
{
    // FR-05, UC-04, SC-06, ADR-0074 決定 4: 利用者名の実在検証。
    //
    // 🔴 **要求と同じ順・同じ数を返す。** 呼び出し元は位置ではなく `username` で読むが、
    // 「送った名前がすべて答えに現れる」ことは呼び出し元が結果を集合へ畳むときの前提である
    // （落ちた名前が黙って「実在しない」に化けない）。
    //
    // 🔴 **照合は序数一致**（`StringComparer.Ordinal`）。DataSourceService の現行
    // `PlatformUserDirectorySnapshot.Of` と同じであり、**移行で照合規則を変えない**
    // （McpServer 側の大小文字無視との不一致はそのまま写す。統一は別 issue）。
    //
    // 🔴 **無効化された利用者も実在として数える。** `ADR-0074` 決定 4 が課すのは「実在すること」で
    // あって「有効であること」ではない（退職者が所有者だった文書は所有者を失わない）。
    public override async Task<CheckUsernamesResponse> CheckUsernames(
        CheckUsernamesRequest request, ServerCallContext context)
    {
        var users = await identity.ListUsersAsync(context.CancellationToken);
        var known = new HashSet<string>(
            users.Select(u => u.Username).Where(u => !string.IsNullOrWhiteSpace(u)),
            StringComparer.Ordinal);

        var resp = new CheckUsernamesResponse();
        foreach (var username in request.Usernames)
            resp.Results.Add(new UsernameExistence { Username = username, Exists = known.Contains(username) });
        return resp;
    }

    // FR-16, UC-09, SC-12, ADR-0062 決定 3: 名指しした 1 人の ABAC 属性。
    //
    // 🔴 **照合は大小文字無視**（`StringComparison.OrdinalIgnoreCase`）。McpServer の現行
    // `FindRegistrarAsync` と同じであり、**移行で照合規則を変えない**。
    //
    // 🔴 **属性の線上表現は REST の `PlatformUserDto.Attributes` と同じ**（1 キー 1 値・集合値キーは
    // カンマ連結。IADR-0385 決定 2）。`IdentityUser.Attributes` をそのまま写すので、
    // `tags = "sales,hr"` は `"sales,hr"` のまま届く。
    //
    // ロール・有効状態・内部 ID は返さない —— 呼び出し元が使わないものを面へ出さない。
    public override async Task<GetUserAttributesResponse> GetUserAttributes(
        GetUserAttributesRequest request, ServerCallContext context)
    {
        // ★［2026-09-08 / #1333・[[IADR-0413]] 決定 5］🔴 **全件列挙の上で絞る形をやめた。**
        // 従前は `ListUsersAsync` の結果から 1 人を選んでいたが、その列挙は
        // `max=1000` で**黙って打ち切られる**ため、**1001 人目以降が「居ない」に見えた**
        // （加えて 1 人ごとに realm ロールの往復が 1 つ増えていた）。
        // 🔴 **照合規則は変えていない**（`OrdinalIgnoreCase`。`FindByUsernameAsync` が同じ規則で絞る）。
        var user = await identity.FindByUsernameAsync(request.Username, context.CancellationToken);

        // 🔴 「名簿に居ない」は**応答**である（エラーではない）。呼び出し元がこれを
        // 「引けなかった」へ倒すかどうかは呼び出し元の判断であり、輸送の側では決めない。
        if (user is null)
            return new GetUserAttributesResponse { Found = false };

        var resp = new GetUserAttributesResponse { Found = true, Username = user.Username };
        foreach (var (key, value) in user.Attributes)
            resp.Attributes[key] = value;
        return resp;
    }
}
