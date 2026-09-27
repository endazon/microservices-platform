using System.Security.Claims;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace RetrievalService.Features.Search.Hybrid;

// FR-19, NFR-09, 計画 ADR-0086 決定 1, ADR-0119 決定 3, [[IADR-0426]] 追記 1 (#1635):
// **gRPC `DocumentSearch/Search` の本文の利用者文脈（`UserContext`）を信じてよい呼び出し元の集合。**
//
// 面の門（`ServiceCaller`）は realm ロール `platform-service` だけを見る。そのロールは 11 のサービスアカウント
// （別プロジェクトのものを含む）が持つので、門だけで本文の `user_id` / 属性を信じると、どれもが任意の利用者
// （管理者を含む）を名乗り、その利用者のスコープ（個人資料の分岐を含む）でチャンクの**本文**を読める。
// ADR-0086 §結果が受け入れたのは「利用者の権限で動く中継者が正直であること」への依存であり、
// `DocumentSearch` でその中継者は AI 分析（`GrpcRagSearchTransport`）だけである。依存をそこへ狭める。
//
// 🔴 **既定は `aianalysis-service` だけ**（helm の aianalysis の `serviceToken.clientId`・compose の
//   `ServiceToken__ClientId`。`DocumentSearchRelayDeploymentWiringTests` が固定する）。
//   **構成したら既定を置き換える**（足し合わせない）。.NET の配列の束縛は初期値に構成値を**追記する**ので、
//   プロパティの既定を `["aianalysis-service"]` にすると外せなくなる。既定は null にし、`EffectiveClients` で解決する。
// 🔴 **空白だけの要素は捨てる。1 つも残らなければ誰も信じない**（fail-closed）。
// 🔴 **`MachinePrincipal` の「サービスアカウントの一覧を構成に持たない」とは向きが逆である。** あちらは一覧から
//   外すと統制を免れる（抜け道になる）。こちらは**許可**の集合で、外せば狭くなり、空なら誰も信じない。
public sealed class DocumentSearchRelayOptions
{
    public const string SectionName = "DocumentSearch";

    /// <summary>未構成のときの許可集合。AI 分析の s2s の client だけ。</summary>
    public static IReadOnlyList<string> DefaultTrustedUserContextClients { get; } = ["aianalysis-service"];

    /// <summary>
    /// 本文の利用者文脈を信じる呼び出し元のクライアント識別子（`azp`）。**null（未構成）なら既定の
    /// `aianalysis-service` だけ。** 構成すると既定を置き換える。
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
    /// **序数一致**で含まれるときだけ真（接頭辞・大小文字の変種は別のクライアントである）。
    /// </summary>
    /// <remarks>
    /// 🔴 機械であることを併せて求める —— `azp` は人のトークンにも付く。
    /// 🔴 クライアント識別は `azp` を第一に見る（`MachinePrincipal.ClientIdOf`）。利用者名が
    ///   `service-account-aianalysis-service` でも `azp` が別なら別のクライアントである。
    /// 判定は共有の `MachinePrincipal` の 2 関数だけで書く（述語を新設しない。[[IADR-0420]]）。
    /// </remarks>
    public bool TrustsUserContextFrom(ClaimsPrincipal? caller)
    {
        if (!MachinePrincipal.IsMachine(caller)) return false;
        var clientId = MachinePrincipal.ClientIdOf(caller);
        if (clientId is null) return false;
        return EffectiveClients.Contains(clientId, StringComparer.Ordinal);
    }
}
