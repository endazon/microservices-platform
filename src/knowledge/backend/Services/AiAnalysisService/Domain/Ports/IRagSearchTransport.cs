using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace AiAnalysisService.Domain.Ports;

// FR-03, FR-04, FR-05, FR-07, NFR-09, NFR-16, UC-01, UC-02, SC-01, SC-08, ADR-0004, ADR-0029,
// ADR-0034 決定 1, ADR-0075, 計画 ADR-0086 決定 1, ADR-0087 決定 2, ADR-0089 決定 1,
// [[IADR-0379]], [[IADR-0400]], [[IADR-0415]], [[IADR-0416]], [[IADR-0426]] (#1255):
// RetrievalService のハイブリッド検索を呼ぶ**輸送のポート**。
// REST（`HttpRagSearchTransport`）と gRPC（`GrpcRagSearchTransport`）の 2 実装があり、
// `Program.cs` が `Services:RetrievalServiceGrpc` の有無で選ぶ。
// **並走中の正は REST**（[[IADR-0379]] 決定 5 / `ADR-0089` 決定 1）。
//
// 🔴 **ポートが返すのは検索結果だけである。** 出典への写像・機密区分の算出・
// AI 入力からの除外（[[IADR-0283]] 決定 3）は `RagOrchestrator` に残る ——
// 輸送では変わらない業務判断であり、2 実装へ写すと片方だけが直る事故の口になる。
//
// 🔴 **失敗は例外にせず空へ縮退する**（[[IADR-0426]] 決定 4）。REST の「非 2xx」「不達」と
// gRPC の `RpcException`（全 status）・s2s トークン取得失敗は**同じ枝**である ——
// 移行の不変条件は「挙動を変えない」であり、現行 REST 実装は 500 を伝播させていない。
public interface IRagSearchTransport
{
    Task<IReadOnlyList<SearchResultDto>> SearchAsync(RagSearchQuery query, CancellationToken ct);
}

/// <summary>
/// FR-05, NFR-09, 計画 ADR-0086 決定 1, [[IADR-0415]], [[IADR-0416]], [[IADR-0426]] 決定 1:
/// 1 回の RAG 検索の要求。
///
/// 🔴 <b>権限の根拠（利用者文脈）と、利用者が指定した絞り込みを別の欄で持つ。</b>
/// 混ぜたものが REST 面の <c>Scope</c> であり、それが信じられてしまったことが #1339 である。
/// <list type="bullet">
/// <item><see cref="EffectiveScope"/> … 呼び出し元が交差済みの実効スコープ。
/// <b>REST 輸送だけが送る</b>（受け口はこれを絞り込みとしてしか使わない）。</item>
/// <item><see cref="UserId"/> / <see cref="UserAttributes"/> … 🔴 <b>gRPC 輸送が本文で運ぶ判定の入力。</b>
/// 受け口が自分でスコープを解決する（判定の位置は動かない）。</item>
/// <item><see cref="NarrowTo"/> … 利用者が指定したデータ範囲そのもの。
/// <b>交差前の値である</b> —— gRPC 輸送はこれを送り、交差は受け口が同じ <c>ScopeNarrowing</c> で行う。</item>
/// </list>
/// </summary>
public sealed record RagSearchQuery(
    string Query,
    int TopK,
    AccessScope EffectiveScope,
    string UserId,
    IReadOnlyDictionary<string, string> UserAttributes,
    IReadOnlyDictionary<string, List<string>>? NarrowTo);
