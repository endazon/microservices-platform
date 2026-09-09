using System.Security.Claims;

namespace Platform.Shared.Infrastructure.Foundation.Observability;

// FR-05, FR-09, SC-12, ADR-0036, ADR-0062, ADR-0085 決定 4, [[IADR-0420]] (#1233):
// **「無人の主体（machine principal）か」の判定を基盤に 1 つだけ置く。**
//
// 計画 `ADR-0085` 決定 4 は「**ユニットの主体**が保存した文書のうち `project` を持たない件数」を
// 指標に定めたが、**「ユニットの主体」をどう識別するかは定めていない**。ここがその実装裁量である。
//
// 🔴 **サービスアカウントの一覧を構成（appsettings）へ持たない。** 持てば「一覧から外す」だけで
// 計上を免れられ、**統制の抜け道**になる（`RestrictedProject` が制限値の集合を構成から読まないと
// 決めたのと同じ理由。[[IADR-0373]] 決定 4）。判定は**トークンが名乗る形**だけで行う。
//
// 🔴 **`SyntheticTraffic` と混同しない。目的が違う。** あちらは
// **「この特定の主体は合成監視か」**を*許可集合との照合*で決める（fail-closed。集合が空なら 0 件）。
// こちらは**「主体が人か機械か」という種別**を*集合なしで*決める。**合流させてはならない** ——
// 合成監視の許可集合へ載っていない機械クライアントは山ほど居り、片方の意味でもう片方を書くと
// **どちらの規律も守れなくなる**（fail-closed の集合を「全機械」に広げるか、種別判定を許可集合で
// 塞ぐか、のどちらかに倒れる）。両者は同じフォルダに並べてあるだけである。
//
// ■ 判定規則（Keycloak の規約に閉じる。2 つの腕を持つ）
//   腕 A: 利用者名が `service-account-` で始まる
//         —— Keycloak は client credentials の主体へ `preferred_username = service-account-<clientId>`
//         を発行する（`SyntheticTraffic` の注記が述べているのと同じ規約）。
//   腕 B: 利用者名が**無く**、クライアント識別のクレーム（`azp` / `client_id`）がある
//         —— `profile` スコープを持たない機械クライアントは `preferred_username` を発行しない。
//
// 🔴 **腕 B に「利用者名が無い」を必ず併記する。** `azp` は**人間のトークンにも必ず付く**
//   （SPA の clientId が入る）。クライアント識別クレームの有無だけで判定すると
//   **全利用者が無人主体になり、指標が母集合ごと壊れる**（`ADR-0085` 決定 4 が退けた
//   「`project` を持たない文書の総数」と同じ、張り付いて動かない数字になる）。
//
// 🔴 **迷ったら「無人ではない」へ倒す。** 本判定の用途は「0 が正常」の違反検出であり、
//   人間の保存を 1 件でも数えると**指標が常時非ゼロになって警報の意味が消える**。
//   取りこぼし（機械を人と読む）は 0 のままになるが、それは**次の書き手が指標を信じ続けられる**方向の
//   誤りである。倒す向きを逆にしない。
public static class MachinePrincipal
{
    /// <summary>Keycloak がサービスアカウントの `preferred_username` に付ける接頭辞。</summary>
    public const string ServiceAccountUsernamePrefix = "service-account-";

    // クライアント識別のクレーム候補。`azp`（authorized party）を第一とする
    // （`McpSubjectResolver.ClientIdClaims` と同じ並び。綴りの揺れを 2 か所で別々に決めない）。
    private static readonly string[] ClientIdClaimTypes = ["azp", "client_id", "clientId"];

    // 利用者名のクレーム候補。`Identity.Name` は `AddPlatformAuth` が
    // `NameClaimType = "preferred_username"` を設定するため本番では preferred_username を指すが、
    // **それに依存しない**（テスト用ハンドラや別スキームでは `ClaimTypes.Name` が入る）。
    private static readonly string[] UsernameClaimTypes = ["preferred_username", ClaimTypes.Name, "name"];

    /// <summary>
    /// 主体が無人（サービスアカウント・機械クライアント）か。
    /// **未認証は false**（主体が決まらないものを機械と読まない）。
    /// </summary>
    public static bool IsMachine(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return false;

        var username = UsernameOf(user);
        if (!string.IsNullOrWhiteSpace(username))
            return username.StartsWith(ServiceAccountUsernamePrefix, StringComparison.OrdinalIgnoreCase);

        // 利用者名が無い ＋ クライアント識別がある = `profile` を持たない機械クライアント。
        return ClientIdOf(user) is not null;
    }

    /// <summary>
    /// 主体のクライアント識別子（`azp` の生値）。無ければ
    /// `service-account-<clientId>` 形式の利用者名から復元する。**どちらも無ければ null。**
    ///
    /// 🔴 **計器の属性にはこの値だけを載せる。** realm に登録済みの機密クライアントは有限であり
    /// 基数は閉じる。利用者識別子・文書 ID・題名は**載せない**（`PrivateNoteNotificationMetrics` /
    /// `LlmUsageMetrics` と同じ規律。ADR-0044 決定 1）。
    /// </summary>
    public static string? ClientIdOf(ClaimsPrincipal? user)
    {
        if (user is null) return null;

        foreach (var claimType in ClientIdClaimTypes)
        {
            var value = user.FindFirstValue(claimType);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        var username = UsernameOf(user);
        return username is not null
            && username.StartsWith(ServiceAccountUsernamePrefix, StringComparison.OrdinalIgnoreCase)
            && username.Length > ServiceAccountUsernamePrefix.Length
                ? username[ServiceAccountUsernamePrefix.Length..]
                : null;
    }

    private static string? UsernameOf(ClaimsPrincipal user)
    {
        var name = user.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();

        foreach (var claimType in UsernameClaimTypes)
        {
            var value = user.FindFirstValue(claimType);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return null;
    }
}
