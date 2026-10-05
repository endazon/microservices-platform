using Grpc.Core;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Observability;
using Platform.Shared.Infrastructure.Foundation.Llm;
using RetrievalService.Domain.Ports;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace RetrievalService.Infrastructure.ExternalServices;

// FR-03, FR-11, NFR-09, NFR-16, ADR-0010, ADR-0029, ADR-0075, [[IADR-0379]] 決定 5, [[IADR-0400]],
// [[IADR-0498]] 決定 5 (#1746 段 S2): 再順位付けのテキスト生成の **east-west gRPC 輸送**（`LlmCompletion/Complete`）。
// `Services:LlmGatewayGrpc` が構成されたときだけ使う（クエリ埋め込みと同じ切り替え）。写像は AI 分析と同じ
// `LlmGrpcMapping` を通す（輸送ごとに要求の形を作り分けない）。
//
// 🔴 **失敗は例外のまま上げる**（`RpcException`・s2s トークンの取得失敗）。縮退は段が決める。
//
// 🔴 ［2026-10-06 / #1746 監査 F4］**取り消しは `OperationCanceledException` で上げる。** チャネルは
// `ThrowOperationCanceledOnCancellation` を立てていない（`GrpcClientExtensions.CreatePlatformChannel`）ので、
// 呼び出し元の取り消し（利用者の中断・段の期限）は `RpcException(Cancelled / DeadlineExceeded)` で表れる。
// そのまま上げると、段は利用者の中断を「輸送の失敗」として縮退させ、期限切れも `transport` と数え違える。
// **`ct` が取り消されているときだけ**写す —— 取り消していないのに来た Cancelled は上流の不調（輸送の失敗）である。
public sealed class GrpcRerankCompletionClient(Pb.LlmCompletion.LlmCompletionClient client) : IRerankCompletionClient
{
    public async Task<CompletionApiResponse> CompleteAsync(
        CompletionApiRequest request, bool isSynthetic, CancellationToken ct)
    {
        // NFR-02, ADR-0076 決定 4, [[IADR-0378]], [[IADR-0400]] 決定 3: 標識は**メタデータ**で運ぶ（AI 分析の生成の輸送と同じ）。
        var headers = new Metadata();
        SyntheticTraffic.PropagateTo(headers, isSynthetic);
        try
        {
            var response = await client.CompleteAsync(LlmGrpcMapping.ToProto(request), headers, cancellationToken: ct);
            return LlmGrpcMapping.ToDto(response);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Cancelled or StatusCode.DeadlineExceeded
                                      && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("LLM gateway rerank call was cancelled by the caller", ex, ct);
        }
    }
}
