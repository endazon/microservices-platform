using Platform.Shared.Contracts.Dtos;
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
public sealed class GrpcRerankCompletionClient(Pb.LlmCompletion.LlmCompletionClient client) : IRerankCompletionClient
{
    public async Task<CompletionApiResponse> CompleteAsync(CompletionApiRequest request, CancellationToken ct)
    {
        var response = await client.CompleteAsync(LlmGrpcMapping.ToProto(request), cancellationToken: ct);
        return LlmGrpcMapping.ToDto(response);
    }
}
