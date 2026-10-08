namespace LlmGateway.Domain.Ports;

// FR-02, NFR-19, IADR-0504 追記 (#1819): 埋め込みプロバイダが**構成として使えない**（鍵が未設定など）ことを表す例外。
//
// 一時的な上流の不調（5xx・通信断）と**型で**区別するために置く。両者は応答としては同じ
// （`Embedded=false`・`Retryable=true`。取り込みは再試行の後 DLQ へ送る）だが、ログの要否が違う:
//   - 上流の不調は 1 件ごとに状況が違い得るので、従来どおりスタック付きで残す。
//   - 構成の欠落は**何回起きても同じ 1 つの原因**である。毎回スタックを残すと、#1819 の PoC のように
//     ログを 1,146 回分のスタックで埋め、他の行（取引判断の呼び出し）を押し出す。
// `InvalidOperationException` の派生にするのは、既存の呼び出し側・試験（型で受けるもの）を壊さないためである。
public sealed class EmbeddingProviderNotConfiguredException(string message) : InvalidOperationException(message);
