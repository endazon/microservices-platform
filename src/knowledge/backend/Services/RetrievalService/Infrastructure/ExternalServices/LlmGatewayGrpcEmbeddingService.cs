using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Llm;
using RetrievalService.Domain.Ports;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace RetrievalService.Infrastructure.ExternalServices;

// FR-03, FR-05, ADR-0013, ADR-0016, ADR-0029, ADR-0075, IADR-0256, IADR-0379, IADR-0397 (#1255):
// クエリ埋め込みの **east-west gRPC 経路**（REST の LlmGatewayEmbeddingService の兄弟）。
//
// **並走中の正は REST である。** 本クラスは `Services:LlmGatewayGrpc` が構成されたときだけ登録され
// （Program.cs）、無ければ従来の HTTP 実装がそのまま使われる。戻すのは構成を外すだけでよい。
//
// 🔴 **輸送の失敗は例外のまま上げる。** RpcException（UNAVAILABLE / UNAUTHENTICATED /
// PERMISSION_DENIED ほか）も s2s トークンの取得失敗（InvalidOperationException）も**捕まえない** ——
// REST 実装の `EnsureSuccessStatusCode` と同じ判断であり、ゲートウェイの故障を「該当なし」に
// 化けさせないためである（IADR-0256 決定 3）。空ベクトルへ縮退させると、HybridSearchService は
// 「意味検索の系統が使えない」と読んで 0 件を返し、**故障が静かに検索結果 0 件になる。**
//
// `Embedded=false`（越境拒否・次元不整合・上流不調）だけは REST と同じく空ベクトルを返す ——
// これはゲートウェイが 200 で明示的に「使えない」と答えた**設計上の縮退**であって故障ではない。
public class LlmGatewayGrpcEmbeddingService(Pb.LlmEmbedding.LlmEmbeddingClient client) : IEmbeddingService
{
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var request = LlmGrpcMapping.ToProto(
            new EmbedApiRequest(text, Confidentiality: null, Purpose: EmbedPurpose.Query));

        var resp = await client.EmbedAsync(request, cancellationToken: ct);

        // FR-02, FR-03, ADR-0016, #995: **`Embedded` を読む。** REST 実装（#995）と同じ判断である。
        if (!resp.Embedded)
            return [];

        return [.. resp.Vector];
    }
}
