using System.Security.Claims;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace DocumentService.Features.Documents;

// FR-19, NFR-09, 計画 ADR-0086 決定 1, ADR-0119 決定 3, [[IADR-0476]] 追記 (#1628):
// **gRPC `DocumentRead` の本文の利用者文脈（`UserContext`）を信じてよい呼び出し元の集合。**
//
// 面の門（`ServiceCaller`）は realm ロール `platform-service` だけを見る。そのロールは 11 のサービスアカウント
// （別プロジェクトのものを含む）が持つので、門だけで本文の `user_id` を信じると、どれもが任意の利用者を名乗って
// その利用者の個人資料を読める。ADR-0086 §結果が受け入れたのは「利用者の権限で動く中継者が正直であること」への
// 依存であり、`DocumentRead` でその中継者は BFF だけである。依存をそこへ狭める。
//
// 🔴 **既定は `bff` だけ**（helm の BFF の `serviceToken.clientId`）。**構成したら既定を置き換える**（足し合わせない）。
//   .NET の配列の束縛は初期値に構成値を**追記する**ので、プロパティの既定を `["bff"]` にすると `bff` を外せなくなる。
//   既定は null にし、「null なら既定」を `EffectiveClients` で解決する。
// 🔴 **空白だけの要素は捨てる。1 つも残らなければ誰も信じない**（fail-closed）。
// 🔴 **`MachinePrincipal` の「サービスアカウントの一覧を構成に持たない」とは向きが逆である。** あちらは一覧から
//   外すと統制を免れる（抜け道になる）。こちらは**許可**の集合で、外せば狭くなり、空なら誰も信じない
//   （`SyntheticMonitoringOptions.Subjects` と同じ fail-closed の形）。
public sealed class DocumentReadRelayOptions
{
    public const string SectionName = "DocumentRead";

    /// <summary>未構成のときの許可集合。BFF の s2s の client だけ。</summary>
    public static IReadOnlyList<string> DefaultTrustedUserContextClients { get; } = ["bff"];

    /// <summary>
    /// 本文の利用者文脈を信じる呼び出し元のクライアント識別子（`azp`）。**null（未構成）なら既定の `bff` だけ。**
    /// 構成すると既定を置き換える。
    /// </summary>
    public string[]? TrustedUserContextClients { get; set; }

    /// <summary>実際に使う許可集合（既定の解決・前後空白の除去・空要素の除去の後）。</summary>
    public IReadOnlyList<string> EffectiveClients =>
        TrustedUserContextClients is null
            ? DefaultTrustedUserContextClients
            : TrustedUserContextClients
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .ToArray();

    /// <summary>
    /// 呼び出し元が本文の利用者文脈を運んでよいか。**機械の主体**であり、かつクライアント識別が許可集合に
    /// 序数一致で含まれるときだけ真。
    /// </summary>
    /// <remarks>
    /// 🔴 機械であることを併せて求める —— `azp` は人のトークンにも付く（BFF のセッションの利用者トークンは `azp=bff`）。
    /// 判定は `MachinePrincipal` の 2 関数だけで書く（述語を新設しない。[[IADR-0420]]）。
    /// </remarks>
    public bool TrustsUserContextFrom(ClaimsPrincipal? caller)
    {
        if (!MachinePrincipal.IsMachine(caller)) return false;
        var clientId = MachinePrincipal.ClientIdOf(caller);
        if (clientId is null) return false;
        return EffectiveClients.Contains(clientId, StringComparer.Ordinal);
    }
}
