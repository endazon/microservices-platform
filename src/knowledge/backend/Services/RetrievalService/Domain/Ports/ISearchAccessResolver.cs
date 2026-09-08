using Platform.Shared.Contracts.Dtos;

namespace RetrievalService.Domain.Ports;

// FR-03, FR-04, FR-05, NFR-09, UC-01, SC-01, SC-08, ADR-0004, ADR-0034 決定 1,
// [[IADR-0044]], [[IADR-0410]], [[IADR-0416]] (#1339):
// **本サービスが自分で ABAC 許可スコープを解決する口。**
//
// 🔴 **従前、権限の根拠は「呼び出し元が本文で送ってきた `Scope`」だった。**
// `HybridSearchService` のコメント自身が危険を言い当てている ——
// 「ネットワーク到達可能な相手が ABAC を全面バイパスできてしまう（呼び出し側 Scope の無検証信任）」。
// 緩和は「`GrantsAccess=true` の明示を要る」だったが、**偽の `Scope` はそう名乗るだけである。**
//
// 🔴 **判定の位置は動かない**（`ADR-0034` 決定 1 のホップごと判定 / [[IADR-0044]] の最終防衛線）。
// **動くのは根拠の出所だけである** ——「呼び出し元が言った」から「自分で引いた」へ。
//
// **`WikiAccessResolver` / `GraphAccessResolver` と同型である**（未認証は問い合わせず deny、
// REST と gRPC の並走、通信失敗も deny-by-default）。
public interface ISearchAccessResolver
{
    /// <summary>
    /// 要求の利用者から許可スコープを解決する。
    /// **未認証・認可サービス不調はいずれも `Granted=false`** へ縮退する（fail-closed）。
    /// </summary>
    Task<AccessScopeResponse> ResolveAsync(HttpContext ctx, CancellationToken ct = default);
}
