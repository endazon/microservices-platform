using Knowledge.Contracts.Dtos;
using Platform.Shared.Contracts.Dtos;

namespace AiAnalysisService.Domain.Ports;

// FR-03, FR-04, FR-05, FR-07, NFR-09, NFR-16, UC-01, UC-02, SC-01, SC-08, ADR-0004, ADR-0029,
// ADR-0034 決定 1, ADR-0075, 計画 ADR-0086 決定 1, ADR-0087 決定 2, ADR-0089 決定 1,
// [[IADR-0379]], [[IADR-0400]], [[IADR-0415]], [[IADR-0416]], [[IADR-0426]] (#1255):
// RetrievalService のハイブリッド検索を呼ぶ**輸送のポート**。
// 実装は gRPC（`GrpcRagSearchTransport`）の 1 つだけである（［2026-10-10 / #1255・[[IADR-0533]]］REST の
// `HttpRagSearchTransport` を撤去した。`ADR-0089` 決定 1 の「解けた」）。宛先 `Services:RetrievalServiceGrpc` が
// 無い配備では、生成クライアントが常に `UNAVAILABLE` を返し、下の縮退（空）へ落ちる。
// ［2026-10-15 / #1902］「REST と gRPC の 2 実装があり `Program.cs` が選ぶ」という記述を現状へ改めた。
//
// 🔴 **ポートが返すのは検索結果だけである。** 出典への写像・機密区分の算出・
// AI 入力からの除外（[[IADR-0283]] 決定 3）は `RagOrchestrator` に残る ——
// 輸送では変わらない業務判断であり、輸送の側へ写すと（輸送が増えたときに）片方だけが直る事故の口になる。
//
// 🔴 **失敗は例外にせず空へ縮退する**（[[IADR-0426]] 決定 4）。gRPC の `RpcException`（全 status）・
// s2s トークン取得失敗は**同じ枝**である —— 移行の不変条件は「挙動を変えない」であり、
// 撤去した REST 実装も 500 を伝播させていなかった（「非 2xx」「不達」を同じ枝へ落としていた）。
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
/// <b>撤去した REST 輸送だけが送っていた</b>。gRPC 輸送は送らない（`GrpcRagSearchTransport` の 🔴）。
/// 現在の読み手は試験の器（検索の偽物へ渡す `TestRagOrchestrator`）だけである。</item>
/// <item><see cref="UserId"/> / <see cref="UserAttributes"/> … 🔴 <b>gRPC 輸送が本文で運ぶ判定の入力。</b>
/// 受け口が自分でスコープを解決する（判定の位置は動かない）。</item>
/// <item><see cref="NarrowTo"/> … 利用者が指定したデータ範囲そのもの。
/// <b>交差前の値である</b> —— gRPC 輸送はこれを送り、交差は受け口が同じ <c>ScopeNarrowing</c> で行う。</item>
/// <item><see cref="IsSynthetic"/> … ［2026-10-06 / #1746 監査 F1］合成監視の要求か（外周が付けた内周の標識）。
/// 輸送が検索サービスへ**標識として引き継ぐ**（gRPC のメタデータ）。検索サービスの再順位付けの段が
/// 合成監視の検索に LLM の費用を出さないため（ADR-0076 決定 4）。既定は偽（標識を付けない）。</item>
/// </list>
/// </summary>
public sealed record RagSearchQuery(
    string Query,
    int TopK,
    AccessScope EffectiveScope,
    string UserId,
    IReadOnlyDictionary<string, string> UserAttributes,
    IReadOnlyDictionary<string, List<string>>? NarrowTo,
    bool IsSynthetic = false);
