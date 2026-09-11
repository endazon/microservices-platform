using GraphService.Domain.Clustering;

namespace GraphService.Domain.Ports;

// FR-17, FR-18, ADR-0035 決定 3, ADR-0083 決定 2, [[IADR-0430]] 決定 1 (#1395):
// クラスタ要約の LLM 境界ポート。
//
// 🔴 **引数は ClusterSummaryPrompt のみである。これが越境禁止の型による強制である。**
// 封の構築経路は `ClusterSummaryPrompt.Seal` ただ 1 つであり、そこが個人資料
// （ADR-0035 決定 8）と区分を超える文書（同決定 6）を落とす。
// **署名を string や GraphDocument へ緩めてはならない**（`ISuggestionLlmClient` と同じ作法）。
public interface IClusterSummaryLlmClient
{
    // 要約の本文。**採れなければ null** —— 不達・非 2xx・機密区分による送信拒否（Sent=false）・
    // モデルの拒否（StopReason="refusal"）・空応答はすべて null である。
    // 🔴 **例外を投げない。** 1 区分の失敗はそのクラスタを未要約のまま残すだけであり
    // （[[IADR-0430]] 決定 4）、バッチを落とす理由にならない。
    Task<string?> SummarizeAsync(ClusterSummaryPrompt prompt, CancellationToken ct = default);
}
