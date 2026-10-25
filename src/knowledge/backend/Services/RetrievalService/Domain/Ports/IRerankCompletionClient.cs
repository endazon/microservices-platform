using Platform.Shared.Contracts.Dtos;

namespace RetrievalService.Domain.Ports;

// FR-03, FR-11, ADR-0010, ADR-0127 決定 3, [[IADR-0498]] 決定 5 (#1746 段 S2):
// 再順位付けのために LLM ゲートウェイのテキスト生成（一括）を呼ぶ**輸送のポート**。
// 実装は gRPC（`LlmCompletion/Complete`）だけである（［2026-10-10 / #1255・[[IADR-0533]]］REST の `HttpRerankCompletionClient` は撤去した。
// `Services:LlmGatewayGrpc` が無ければ UNAVAILABLE になり、段は縮退する）。
//
// 🔴 **送信先はゲートウェイだけである**（ADR-0010）。越境判定（機密区分 × ティア・ZDR）は
// ゲートウェイが行う。輸送は**失敗を例外のまま上げる** —— 縮退（元の順へ戻す）を決めるのは段であり、
// 輸送が「別の送信先へ倒す」枝を持ってはならない。
//
// NFR-02, ADR-0044, ADR-0076 決定 4, [[IADR-0378]], [[IADR-0498]] 決定 2（2026-10-06 追記）: `isSynthetic` は内周の標識
// （`X-Synthetic-Traffic`）を**ゲートウェイへ引き継ぐ**ためにある。段は合成監視の検索では呼ばないので通常は偽だが、
// 引き継ぎを輸送に持たせておくと、段の判定が外れても費用はゲートウェイが除外する（二重の守り）。
// 🔴 利用者の取り消し（`ct`）は `OperationCanceledException` で上げる（gRPC の `RpcException(Cancelled)` を畳まない）。
public interface IRerankCompletionClient
{
    Task<CompletionApiResponse> CompleteAsync(CompletionApiRequest request, bool isSynthetic, CancellationToken ct);
}
