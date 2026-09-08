using AuthorizationService.Domain.Ports;

namespace AuthorizationService.Features.Authz.ResolveScope;

// FR-05, FR-16, NFR-09, UC-05, UC-09, SC-12, SC-17, 計画 ADR-0004, ADR-0062 決定 3, ADR-0080,
// ADR-0084 決定 1, ADR-0086 決定 4, ADR-0087 決定 2, **計画 ADR-0088 決定 1・2**,
// [[IADR-0301]], [[IADR-0329]] 決定 1, [[IADR-0385]] 決定 2, [[IADR-0401]] 決定 2,
// [[IADR-0411]], [[IADR-0413]] (#1333):
// ABAC 判定に使う利用者属性を **IdP から引き直す唯一の点**。
//
// 🔴 **呼び出し元が本文で主張した `user_attributes` は評価に用いない**（計画 `ADR-0088` 決定 1）。
// 従前、`AuthzScope/Resolve` は主張をそのまま評価器へ渡していた ——
// **`platform-service` を持つサービスが 1 つ侵害されれば、任意の利用者の権限スコープが取れた。**
// これは `ADR-0086` 決定 4 が「受け入れたリスク」として記録した構造であり、
// planning#564 の裁定（`ADR-0088`）が是正を決めた。
//
// 🔴 **REST と gRPC の両面がこの 1 つを通る**（同 決定 2）。**面ごとに書かない** ——
// 片方だけが古くなる形は本リポジトリが繰り返し踏んでいる
// （[[IADR-0412]] 決定 6 / #1330 の 5 巡 / [[IADR-0411]] の抽出点 6 か所）。
//
// 🔴 **閉じるのは半分である**（`ADR-0088` 決定 4）。**偽の属性**の主張は閉じるが、
// **他人の `user_id` を名乗る**ことは閉じない —— 引き直しの鍵が本文の `user_id` だからである。
// 残る半分を閉じる手段は token exchange しかなく、`ADR-0086` 決定 2 が今は採らないと定めている。
// **そのかわり `ServiceCaller`（REST・gRPC の両面）が、詐称できる主体をサービスに限る。**
public sealed class ScopeUserAttributeSource(
    IIdentityAdminClient identity, ILogger<ScopeUserAttributeSource> logger)
{
    /// <summary>
    /// 🔴 **「居ない」と「引けなかった」を分ける**（`ADR-0088` 決定 1 /
    /// [[IADR-0401]] が `GetUserAttributes` で既に採っている型）。
    /// **後段が落ちていることを「その利用者に権限が無い」と記録するのは嘘である。**
    /// </summary>
    public enum Outcome
    {
        /// <summary>引けた。<c>Attributes</c> が IdP の値である。</summary>
        Found,

        /// <summary>名簿に居ない。**応答**として deny を返す（200 / gRPC 応答）。</summary>
        NotFound,

        /// <summary>IdP へ届かない・失敗した。**status** で返す（503 / <c>UNAVAILABLE</c>）。</summary>
        Unavailable,
    }

    public readonly record struct Result(Outcome Outcome, Dictionary<string, string> Attributes);

    /// <summary>
    /// `userId`（<c>preferred_username</c>）の ABAC 属性を IdP から引く。
    ///
    /// 🔴 **属性の線上表現は変換しない**（[[IADR-0385]] 決定 2）——
    /// `tags = "sales,hr"` はそのまま届き、集合値の交差判定は `AbacEvaluator` が行う
    /// （[[IADR-0411]] / `ADR-0080` 決定 2）。**符号化の規則を 2 か所に持たない。**
    /// </summary>
    public async Task<Result> ResolveAsync(string userId, CancellationToken ct)
    {
        try
        {
            var user = await identity.FindByUsernameAsync(userId, ct);
            if (user is null)
            {
                // 🔴 **「居ない」は応答である。** 名前を推測した主体に「引けなかった」との差を
                // 見せることにはなるが、それは `ADR-0088` 決定 1 が明示的に選んだ形である
                // （後段の不調を権限の不在として記録しないことのほうを重く見る）。
                logger.LogInformation(
                    "ABAC 判定: 利用者が名簿に居ないため deny とする。userId={UserId}", ForLog(userId));
                return new Result(Outcome.NotFound, []);
            }

            return new Result(Outcome.Found, new Dictionary<string, string>(user.Attributes, StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // 🔴 **ここを `NotFound` へ畳まない。** 畳むと IdP の停止が
            // 「その利用者に権限が無い」として記録され、**原因が追えなくなる。**
            // 呼び出し元はいずれも非 2xx / `RpcException` を deny へ縮退するので、
            // **status で返しても fail-closed は保たれる**（作業仕様書 §実測 2）。
            logger.LogError(ex,
                "ABAC 判定: 利用者属性を IdP から引けなかった。userId={UserId}", ForLog(userId));
            return new Result(Outcome.Unavailable, []);
        }
    }

    /// <summary>
    /// FR-05, NFR-09, [[IADR-0413]] (#1333): 🔴 **ログへ出す前に制御文字を落とす。**
    ///
    /// `userId` は**呼び出し元が本文で渡す検証されていない値**である。
    /// `ResolveScopeValidator` は `action` の値域しか持たず、`user_id` には文字種の制約が無い
    /// （gRPC 面はそもそも validator を通らない）。
    ///
    /// 🔴 **本 PR の前提そのものが「呼び出し元の本文を信じない」である。**
    /// その値を改行ごとログへ流すと、**この PR が強化しようとしている deny の監査ログへ
    /// 偽の行を混ぜられる**（CR/LF のログフォージング）。判定に使う値は生のまま
    /// （IdP へ問い合わせる鍵であり、妙な値なら「居ない」で deny へ倒れる）、
    /// **ログへ出す表現だけを削る。**
    ///
    /// 🔴 **値域を validator へ足す形は採らない。** 制約を足すと**面ごとに 2 か所**へ要る
    /// （REST の validator と gRPC の手書き検証）——
    /// 引き直しの点を 1 つにした決定 1 と逆向きになる。**ここは両面が通る唯一の点である。**
    /// </summary>
    internal static string ForLog(string? userId)
    {
        if (string.IsNullOrEmpty(userId)) return "(空)";

        // 制御文字（CR / LF / TAB / NUL ほか）を落とし、長さを上限で切る
        // （長大な値でログ 1 行を埋めるのも同じ系統の妨害である）。
        var cleaned = new string([.. userId.Where(c => !char.IsControl(c)).Take(LogValueLimit)]);
        return cleaned.Length == 0 ? "(空)" : cleaned;
    }

    private const int LogValueLimit = 256;
}
