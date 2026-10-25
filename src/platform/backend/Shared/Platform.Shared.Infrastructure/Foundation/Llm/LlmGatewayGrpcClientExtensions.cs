using Microsoft.Extensions.Configuration;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace Platform.Shared.Infrastructure.Foundation.Llm;

// FR-02, FR-03, NFR-09, NFR-16, ADR-0029, ADR-0075, IADR-0379 決定 4・5, IADR-0397 (#1255):
// LlmGateway への east-west gRPC 呼び出し側の登録。参照実装 `AddAuthzScopeGrpcClient` と同型。
//
// ［2026-10-10 / #1255・[[IADR-0533]]］**REST の並走は撤去した。** 呼び出し元（AiAnalysis / Conversion / Graph の提案 /
// Ingestion / Retrieval）の輸送は gRPC だけである（[[IADR-0379]] 決定 5「並走中の正は REST」を反転）。
// `Services:LlmGatewayGrpc`（h2c のアドレス。例: http://llm-gateway:8081）が構成されていなければ、生成クライアントを
// 常に `UNAVAILABLE` を返す呼び出し器の上に組む（[[IADR-0533]] 決定 2。各呼び出し元の「届かない」の枝へ落ちる）。
public static class LlmGatewayGrpcClientExtensions
{
    public const string AddressKey = "Services:LlmGatewayGrpc";

    /// <summary>宛先ごとにチャネルを分けるための DI キー（下の 🔴 を参照）。</summary>
    public const string ChannelKey = "LlmGatewayGrpc";

    public static IServiceCollection AddLlmGatewayGrpcClient(this IServiceCollection services, IConfiguration config)
    {
        var address = config[AddressKey];
        if (string.IsNullOrWhiteSpace(address))
            return services
                .TryAddUnconfiguredGrpcClient(AddressKey, ci => new Pb.LlmEmbedding.LlmEmbeddingClient(ci))
                .TryAddUnconfiguredGrpcClient(AddressKey, ci => new Pb.LlmCompletion.LlmCompletionClient(ci));

        services.AddPlatformServiceToken(config);
        // 🔴 チャネルは**キー付き**で登録する。`AddAuthzScopeGrpcClient` は同じ `GrpcChannel` 型を
        // **キー無し**で（別アドレスに対して）登録するため、両方を構成したサービス（後続スライスの
        // AiAnalysis 等）でキー無し登録を共有すると、片方のクライアントがもう片方の宛先へ繋がる。
        // キー付きにすると宛先ごとに分かれ、かつ破棄は DI が持つ（チャネルは IDisposable である）。
        services.AddKeyedSingleton(ChannelKey, (sp, _) =>
            GrpcClientExtensions.CreatePlatformChannel(address, sp.GetRequiredService<IServiceTokenProvider>()));
        services.AddSingleton(sp => new Pb.LlmEmbedding.LlmEmbeddingClient(
            sp.GetRequiredKeyedService<GrpcChannel>(ChannelKey)));
        // FR-04, FR-11, IADR-0400 (#1255): テキスト生成の生成クライアント。**同じチャネルを共有する**
        // （宛先が同じ 1 つの LlmGateway であり、チャネルは多重化される。2 本張ると接続が二重になる）。
        services.AddSingleton(sp => new Pb.LlmCompletion.LlmCompletionClient(
            sp.GetRequiredKeyedService<GrpcChannel>(ChannelKey)));
        return services;
    }
}
