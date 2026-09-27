using System.Security.Claims;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace GraphService.Features.Graph;

// FR-04, FR-05, FR-17, NFR-09, 計画 ADR-0086 決定 1・§結果, ADR-0034 決定 1, [[IADR-0410]] 追記 1 (#1636):
// **gRPC `GraphNeighbors/ExpandNeighbors` の本文の利用者文脈（`UserContext`）を信じてよい呼び出し元の集合。**
//
// 面の門（`ServiceCaller`）は realm ロール `platform-service` だけを見る。そのロールは 11 のサービスアカウント
// （別プロジェクトのものを含む）が持つので、門だけで本文を信じると、どれもが任意の利用者を名乗り、
// その利用者のスコープの辺（文書 ID・辺の種類）を引けた。この面を利用者の権限で呼ぶ中継者は
// 二段検索の近傍展開（RetrievalService の `GrpcGraphNeighborExpander`）だけである。
//
// 🔴 **既定は `retrieval-service` だけ**（helm の retrieval の `serviceToken.clientId`・compose の `ServiceToken__ClientId`。
//   `GraphNeighborsRelayDeploymentWiringTests` が固定する）。**構成したら既定を置き換える**。空白だけなら誰も信じない。
// 判定・既定の解決・構成の形の検査は共有の `TrustedUserContextRelay` が持つ（面ごとに写さない）。
// 🔴 同じ面の `ListEdgeTypeWeights` は利用者文脈を持たない（描画用の語彙）ので本集合の対象外である。
public sealed class GraphNeighborsRelayOptions
{
    public const string SectionName = "GraphNeighbors";

    /// <summary>未構成のときの許可集合。RetrievalService の s2s の client だけ。</summary>
    public static IReadOnlyList<string> DefaultTrustedUserContextClients { get; } = ["retrieval-service"];

    /// <summary>本文の利用者文脈を信じる呼び出し元のクライアント識別子（`azp`）。null（未構成）なら既定。構成すると既定を置き換える。</summary>
    public string[]? TrustedUserContextClients { get; set; }

    /// <summary>実際に使う許可集合（既定の解決・前後空白の除去・空要素の除去の後）。</summary>
    public IReadOnlyList<string> EffectiveClients =>
        TrustedUserContextRelay.Effective(TrustedUserContextClients, DefaultTrustedUserContextClients);

    /// <summary>呼び出し元が本文の利用者文脈を運んでよいか（機械の主体 ∧ クライアント識別が序数一致で許可集合に在る）。</summary>
    public bool TrustsUserContextFrom(ClaimsPrincipal? caller) =>
        TrustedUserContextRelay.Trusts(caller, EffectiveClients);

    /// <summary>1 つの値（配列でない）で構成されていたら起動時に止める（既定へ静かに戻るのを防ぐ）。</summary>
    public static void ThrowIfScalar(Microsoft.Extensions.Configuration.IConfiguration configuration) =>
        TrustedUserContextRelay.ThrowIfScalar(configuration, SectionName, DefaultTrustedUserContextClients[0]);
}
