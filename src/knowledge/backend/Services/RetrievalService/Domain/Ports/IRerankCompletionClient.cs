using Platform.Shared.Contracts.Dtos;

namespace RetrievalService.Domain.Ports;

// FR-03, FR-11, ADR-0010, ADR-0127 決定 3, [[IADR-0498]] 決定 5 (#1746 段 S2):
// 再順位付けのために LLM ゲートウェイのテキスト生成（一括）を呼ぶ**輸送のポート**。
// REST（`POST /complete`）と gRPC（`LlmCompletion/Complete`）の 2 実装があり、埋め込みと同じ
// `Services:LlmGatewayGrpc` の有無で選ぶ（並走中の正は REST）。
//
// 🔴 **送信先はゲートウェイだけである**（ADR-0010）。越境判定（機密区分 × ティア・ZDR）は
// ゲートウェイが行う。輸送は**失敗を例外のまま上げる** —— 縮退（元の順へ戻す）を決めるのは段であり、
// 輸送が「別の送信先へ倒す」枝を持ってはならない。
public interface IRerankCompletionClient
{
    Task<CompletionApiResponse> CompleteAsync(CompletionApiRequest request, CancellationToken ct);
}
