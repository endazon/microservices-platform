using System.Security.Claims;
using Platform.Shared.Infrastructure.Foundation.Observability;

namespace DocumentService.Features.Documents.AddTag;

// FR-05, FR-18, NFR-09, SC-05, 計画 ADR-0086 決定 1・§結果, ADR-0063 決定 3, [[IADR-0410]] 追記 1 (#1636):
// **gRPC `DocumentTagWrite/AddTag` の本文の利用者文脈（`user_id` / `user_attributes` / `user_roles`）を信じてよい呼び出し元の集合。**
//
// 面の門（`ServiceCaller`）は realm ロール `platform-service` だけを見る。そのロールは 11 のサービスアカウント
// （別プロジェクトのものを含む）が持つので、門だけで本文を信じると、どれもが任意の利用者を名乗り、
// さらに `user_roles=["platform-admin"]` を名乗って管理者の上書き（ADR-0063 決定 3 の②）で任意の組織文書へタグを書けた。
// この面を利用者の権限で呼ぶ中継者は AI タグ提案の承認を反映する GraphService（`GrpcDocumentTagWriter`）だけである。
//
// 🔴 **既定は `graph-service` だけ**（helm の graph の `serviceToken.clientId`・compose の `ServiceToken__ClientId`。
//   `DocumentTagWriteRelayDeploymentWiringTests` が固定する）。**構成したら既定を置き換える**。空白だけなら誰も信じない。
// 判定・既定の解決・構成の形の検査は共有の `TrustedUserContextRelay` が持つ（面ごとに写さない）。
public sealed class DocumentTagWriteRelayOptions
{
    public const string SectionName = "DocumentTagWrite";

    /// <summary>未構成のときの許可集合。GraphService の s2s の client だけ。</summary>
    public static IReadOnlyList<string> DefaultTrustedUserContextClients { get; } = ["graph-service"];

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
