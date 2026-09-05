using IngestionService.Domain;
using IngestionService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Llm;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace IngestionService.Infrastructure.ExternalServices;

// FR-02, FR-05, ADR-0013, ADR-0016, ADR-0029, ADR-0075, IADR-0256, IADR-0379, IADR-0397 (#1255):
// 取り込み埋め込みの **east-west gRPC 経路**（REST の LlmGatewayEmbeddingService の兄弟）。
//
// **並走中の正は REST である。** 本クラスは `Services:LlmGatewayGrpc` が構成されたときだけ登録され
// （Program.cs）、無ければ従来の HTTP 実装がそのまま使われる。
//
// 🔴 **輸送の失敗は例外のまま上げる。** RpcException も s2s トークンの取得失敗も**捕まえない** ——
// REST 実装の `EnsureSuccessStatusCode` と同じ判断である（IADR-0256 決定 3）。ここで
// `Embedded=false, Retryable=true` へ倒すと、Wolverine / MassTransit の再試行は回るものの、
// **ゲートウェイの故障と「機密区分による送信拒否」が同じ形になり区別できなくなる。**
//
// 🔴 REST 実装が持つ「応答が null（本文欠落）→ Retryable=true」の枝は**持たない**。
// proto の応答メッセージは欠落し得ず（不達は RpcException になる）、起こり得ないケースへの
// 防御的実装をしないという方針に従う。
public class LlmGatewayGrpcEmbeddingService(Pb.LlmEmbedding.LlmEmbeddingClient client) : IEmbeddingService
{
    public async Task<EmbeddingResult> EmbedAsync(
        string text, string? confidentiality, CancellationToken ct = default)
    {
        var request = LlmGrpcMapping.ToProto(
            new EmbedApiRequest(text, confidentiality, EmbedPurpose.Index));

        var resp = await client.EmbedAsync(request, cancellationToken: ct);

        return new EmbeddingResult([.. resp.Vector], resp.Collection, resp.Embedded, resp.Retryable);
    }
}
