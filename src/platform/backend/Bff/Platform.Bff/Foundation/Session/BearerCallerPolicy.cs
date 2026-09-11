using Platform.Shared.Infrastructure.Foundation.Observability;
using System.Security.Claims;

namespace Platform.Bff.Foundation.Session;

// NFR, SC-13, ADR-0032, IADR-0251 決定 9, IADR-0273, [[IADR-0429]] (#1393):
// **`/bff/*` を `Authorization: Bearer` で叩ける主体を、ブラウザが取得し得ないトークンに絞る。**
//
// ■ なぜ要るか（口を閉じるだけでは足りない）
//
// #1393 で realm から `platform-spa`（public client・PKCE・standardFlow）を撤去した。
// しかし BFF の側は `BffSmart` の振り分け（IADR-0251 決定 9）により、**realm が発行した
// 有効な JWT なら誰の名義でも受理する**ままである。public client が 1 つでも realm に足されれば、
// ブラウザは利用者トークンを取って `/bff/*` を直接叩ける ——
// **HttpOnly セッション Cookie も CSRF ヘッダ（IADR-0251 決定 1）も丸ごと迂回される。**
// `platform-spa` は #126 から 8 か月残った。**同型の client は再び足され得る**ので、
// 口（realm）と受理（ここ）の両方を閉じる。
//
// ■ 判定規則（2 つの腕。**どちらでもなければ拒否＝ fail-closed**）
//
//   腕 A: **無人の主体**（`MachinePrincipal.IsMachine`）—— サービスアカウント、
//         あるいは `profile` を持たない機械クライアント。サービス間 Bearer はここで通る。
//   腕 B: **`azp` が BFF 自身の OIDC クライアント ID と一致する**トークン。
//
// 🔴 **腕 B が抜け道にならないのは、そのクライアントが confidential だからである。**
//    ブラウザは client_secret を持てないので、`azp = <BFF の client>` のトークンを取得できない。
//    ADR-0032 が禁じた「SPA がトークンを扱う」形はこの腕では成立しない。
//    残しているのは非ブラウザの外形確認（`scripts/verify-oidc-edge-flow.sh`。統合スタックの門）
//    のためであり、**移行期の姿勢**である。
//
// 🔴 **狭める条件**（IADR-0251 決定 9 条件 1 をそのまま引き継ぐ）:
//    `verify-oidc-edge-flow.sh` が Cookie 方式へ移ったら**腕 B を落とす**（＝機械のみ）。
//    狭めるのは緩める方向ではないので後から実施できる。逆は承認が要る。
//
// 🔴 **クライアント ID の許可リストを構成で持たない。** 腕 B が見るのは
//    `BffSessionOptions.ClientId`（BFF 自身が OIDC で名乗る値）ただ 1 つである ——
//    「一覧に足すだけで通る」形にすると、統制が構成ファイルの編集権限まで薄まる
//    （[[IADR-0420]] が許可リストを退けたのと同じ理由）。
internal static class BearerCallerPolicy
{
    /// <summary>
    /// Bearer 腕で受理してよい主体か。**未認証・主体不明は false**（fail-closed）。
    /// </summary>
    /// <param name="user">検証済みトークンから作られた主体。</param>
    /// <param name="bffClientId">BFF 自身の OIDC クライアント ID（<see cref="BffSessionOptions.ClientId"/>）。</param>
    internal static bool IsAcceptedCaller(ClaimsPrincipal? user, string? bffClientId)
    {
        if (user?.Identity?.IsAuthenticated != true) return false;

        // 腕 A: 無人の主体（サービス間 Bearer）。
        if (MachinePrincipal.IsMachine(user)) return true;

        // 腕 B: BFF 自身のクライアント名義。**空の構成値を「何でも一致」にしない**
        // （ClientId を空にした構成が、全クライアントを通す形へ静かに縮退するのを防ぐ）。
        if (string.IsNullOrWhiteSpace(bffClientId)) return false;

        return string.Equals(MachinePrincipal.ClientIdOf(user), bffClientId, StringComparison.Ordinal);
    }

    /// <summary>
    /// 拒否したときにログへ残す理由。**利用者識別子は載せない**（`azp` は realm に登録済みの
    /// 有限集合なので基数が閉じる。ADR-0044 決定 1 / MachinePrincipal と同じ規律）。
    /// </summary>
    internal static string RejectionReason(ClaimsPrincipal? user) =>
        "BFF セッション方式（ADR-0032）: 利用者のトークンを Bearer で直接受理しない。"
        + $"ブラウザはセッション Cookie を使うこと（azp={MachinePrincipal.ClientIdOf(user) ?? "(無し)"}）。";
}
