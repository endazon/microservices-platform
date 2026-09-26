using Platform.Shared.Infrastructure.Foundation.Observability;
using System.Security.Claims;

namespace Platform.Bff.Foundation.Session;

// NFR-09, SC-13, ADR-0032, IADR-0251 決定 9, IADR-0273, [[IADR-0429]] (#1393 / #1535):
// **`/bff/*` を `Authorization: Bearer` で叩ける主体を、無人の主体（サービス間）に限る。**
//
// ■ なぜ要るか（口を閉じるだけでは足りない）
//
// #1393 で realm から `platform-spa`（public client・PKCE・standardFlow）を撤去した。
// しかし BFF の側は `BffSmart` の振り分け（IADR-0251 決定 9）により、**realm が発行した
// 有効な JWT なら誰の名義でも受理する**ままだった。public client が 1 つでも realm に足されれば、
// ブラウザは利用者トークンを取って `/bff/*` を直接叩ける ——
// **HttpOnly セッション Cookie も CSRF ヘッダ（IADR-0251 決定 1）も丸ごと迂回される。**
// 口（realm）と受理（ここ）の両方を閉じる。
//
// ■ 判定規則（**無人の主体でなければ拒否＝ fail-closed**）
//
//   **無人の主体**（`MachinePrincipal.IsMachine`）—— サービスアカウント、
//   あるいは `profile` を持たない機械クライアント。サービス間 Bearer（合成監視を含む）はここで通る。
//
// 🔴 **利用者のトークンは `azp` に関わらず通さない**（#1535）。
//    #1393 では「`azp` が BFF 自身の confidential client」の利用者トークンを 2 本目の腕として残していた
//    （ブラウザは client_secret を持てないので ADR-0032 の禁則には当たらない、という理由）。
//    それは非ブラウザの外形確認（`scripts/verify-oidc-edge-flow.sh`）のためだけの**移行期の腕**であり、
//    同スクリプトが BFF のログイン往復で得たセッション Cookie で叩く形へ移ったので落とした。
//    **利用者の資格情報で `/bff/*` に入る口はセッション Cookie ただ 1 つである。**
//
// 🔴 **Cookie セッションはこの門を通らない**（通す必要もない）。`SessionTokenPropagationMiddleware` が
//    セッションのアクセストークン（`azp` = BFF の client の利用者トークン）を `Authorization` へ昇格するのは
//    認証・認可が終わった**後**であり、昇格後に既定スキームで再認証する呼び出しは無い
//    （`BearerArmPipelineTests` が本物の JwtBearer で固定している）。
internal static class BearerCallerPolicy
{
    /// <summary>
    /// Bearer 腕で受理してよい主体か。**未認証・主体不明・利用者は false**（fail-closed）。
    /// </summary>
    /// <param name="user">検証済みトークンから作られた主体。</param>
    internal static bool IsAcceptedCaller(ClaimsPrincipal? user) => MachinePrincipal.IsMachine(user);

    /// <summary>
    /// 拒否したときにログへ残す理由。**利用者識別子は載せない**（`azp` は realm に登録済みの
    /// 有限集合なので基数が閉じる。ADR-0044 決定 1 / MachinePrincipal と同じ規律）。
    /// </summary>
    internal static string RejectionReason(ClaimsPrincipal? user) =>
        "BFF セッション方式（ADR-0032）: 利用者のトークンを Bearer で直接受理しない。"
        + $"セッション Cookie を使うこと（azp={MachinePrincipal.ClientIdOf(user) ?? "(無し)"}）。";
}
