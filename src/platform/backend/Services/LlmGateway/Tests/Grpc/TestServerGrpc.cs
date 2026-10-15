using Grpc.Core;
using Grpc.Net.Client;
using Platform.Shared.Contracts.Dtos;
using Platform.Shared.Infrastructure.Foundation.Llm;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace LlmGateway.Tests.Grpc;

// NFR-16, ADR-0089 決定 1, [[IADR-0533]] (#1902): REST の `/embed`・`/complete/stream` を撤去したので、
// 端点の試験は器（`TestWebApplicationFactory` の TestServer）の上で **gRPC 面**を叩く。
//
// 🔴 **HttpClient は `CreateClient()` が返したものをそのまま使う。** 既定のヘッダ（s2s トークン・
// 合成監視の標識）は gRPC の要求にも載るので、器の作法（既定は `ServiceCaller` として認証済み）が変わらない。
// 待ち受けない（実 Kestrel の h2c ポートは `GrpcKestrelFactory` の試験だけが使う）。
internal static class TestServerGrpc
{
    private static GrpcChannel Channel(HttpClient client) =>
        GrpcChannel.ForAddress(client.BaseAddress!, new GrpcChannelOptions { HttpClient = client });

    /// <summary>逐次生成（`LlmCompletion/CompleteStream`）を最後まで読み、REST の DTO へ写して返す。</summary>
    public static async Task<List<CompletionStreamEvent>> CompleteStreamAsync(
        this HttpClient client, CompletionApiRequest request, CancellationToken ct)
    {
        using var channel = Channel(client);
        using var call = new Pb.LlmCompletion.LlmCompletionClient(channel)
            .CompleteStream(LlmGrpcMapping.ToProto(request), cancellationToken: ct);

        var events = new List<CompletionStreamEvent>();
        await foreach (var ev in call.ResponseStream.ReadAllAsync(ct))
            events.Add(LlmGrpcMapping.ToDto(ev));
        return events;
    }

    /// <summary>埋め込み（`LlmEmbedding/Embed`）を呼び、REST の DTO へ写して返す。</summary>
    public static async Task<EmbedApiResponse> EmbedAsync(
        this HttpClient client, EmbedApiRequest request, CancellationToken ct)
    {
        using var channel = Channel(client);
        var resp = await new Pb.LlmEmbedding.LlmEmbeddingClient(channel)
            .EmbedAsync(LlmGrpcMapping.ToProto(request), cancellationToken: ct);
        return LlmGrpcMapping.ToDto(resp);
    }
}
