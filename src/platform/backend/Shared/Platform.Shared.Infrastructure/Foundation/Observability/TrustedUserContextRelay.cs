using System.Security.Claims;
using Microsoft.Extensions.Configuration;

namespace Platform.Shared.Infrastructure.Foundation.Observability;

// NFR-09, 計画 ADR-0086 決定 1・§結果, ADR-0119 決定 3 (#1636):
// **east-west gRPC の本文の利用者文脈を信じてよい呼び出し元（中継者）の許可集合**を扱う共有の 3 関数。
//
// 面の門（`ServiceCaller`）は realm ロール `platform-service` だけを見る。そのロールは 11 のサービスアカウント
// （別プロジェクトのものを含む）が持つので、門だけで本文の `user_id` を信じると、どれもが任意の利用者を名乗れる。
// ADR-0086 §結果が受け入れたのは「利用者の権限で動く中継者が正直であること」への依存であり、`platform-service` を
// 持つ全主体への依存ではない。面ごとに実在する中継者（既定）へ依存を狭めるのが各面の `*RelayOptions` で、
// 本クラスはその判定・既定の解決・構成の形の検査だけを持つ（面ごとに写さない。先行の #1628 / #1635 の 2 面は
// 同じ規則を各サービスに写しており、本クラスはそれと同じ値を返す）。
//
// 🔴 **判定は `MachinePrincipal` の 2 関数だけで書く**（述語を新設しない。[[IADR-0420]]）。
// 🔴 **許可の集合である** —— 外せば狭くなり、空なら誰も信じない（fail-closed）。`MachinePrincipal` の
//   「サービスアカウントの一覧を構成に持たない」（あちらは外すと統制を免れる）とは向きが逆である。
public static class TrustedUserContextRelay
{
    /// <summary>各面の節の下で許可集合を持つキー名（例 `DocumentTagWrite:TrustedUserContextClients`）。</summary>
    public const string ClientsKey = "TrustedUserContextClients";

    /// <summary>
    /// 実際に使う許可集合。**<paramref name="configured"/> が null（未構成）なら <paramref name="defaults"/>。**
    /// 構成されていれば既定を**置き換える**（足し合わせない）。前後空白を落とし、空白だけの要素を捨てる。
    /// </summary>
    /// <remarks>
    /// 🔴 .NET の配列の束縛は初期値に構成値を**追記する**ので、プロパティの既定を既定の集合にすると外せなくなる。
    /// 各面はプロパティを null のまま持ち、ここで解決する。
    /// </remarks>
    public static IReadOnlyList<string> Effective(string[]? configured, IReadOnlyList<string> defaults)
        => configured is null
            ? defaults
            : configured
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .ToArray();

    /// <summary>
    /// 呼び出し元が本文の利用者文脈を運んでよいか。**機械の主体**であり、かつクライアント識別
    /// （`azp` を第一に、無ければ `service-account-&lt;clientId&gt;` から復元）が許可集合に**序数一致**で含まれるときだけ真。
    /// </summary>
    /// <remarks>
    /// 🔴 機械であることを併せて求める —— `azp` は人のトークンにも付く（BFF のセッションの利用者トークンは `azp=bff`）。
    /// 🔴 接頭辞・大小文字の変種は別のクライアントである（序数一致）。
    /// </remarks>
    public static bool Trusts(ClaimsPrincipal? caller, IReadOnlyList<string> clients)
    {
        if (!MachinePrincipal.IsMachine(caller)) return false;
        var clientId = MachinePrincipal.ClientIdOf(caller);
        if (clientId is null) return false;
        return clients.Contains(clientId, StringComparer.Ordinal);
    }

    /// <summary>
    /// 構成の形を起動時に確かめる。**`&lt;section&gt;:TrustedUserContextClients` が配列ではなく 1 つの値で書かれていたら例外を投げる。**
    /// </summary>
    /// <remarks>
    /// 🔴 1 つの値（`X__TrustedUserContextClients=a,b`）は配列へ束縛されず、プロパティは null のまま**既定へ静かに戻る**
    /// （#1628 の監査の F2）。書き手は集合を置き換えたつもりで既定のまま動く。配列は `__0` / `__1` … で書く。
    /// </remarks>
    public static void ThrowIfScalar(IConfiguration configuration, string sectionName, string exampleClient)
    {
        var key = $"{sectionName}:{ClientsKey}";
        if (configuration.GetSection(key).Value is not null)
            throw new InvalidOperationException(
                $"{key} は配列で構成すること（環境変数なら {sectionName}__{ClientsKey}__0={exampleClient}）。"
                + $"1 つの値（カンマ区切りを含む）は束縛されず、既定の {exampleClient} へ戻ってしまう。");
    }
}
