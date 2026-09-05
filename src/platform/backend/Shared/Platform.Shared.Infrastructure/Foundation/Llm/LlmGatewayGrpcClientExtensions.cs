using Microsoft.Extensions.Configuration;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Infrastructure.Foundation.Grpc;
using Pb = Platform.Shared.Contracts.Grpc.LlmGateway.V1;

namespace Platform.Shared.Infrastructure.Foundation.Llm;

// FR-02, FR-03, NFR-09, NFR-16, ADR-0029, ADR-0075, IADR-0379 決定 4・5, IADR-0397 (#1255):
// LlmGateway への east-west gRPC 呼び出し側の登録。参照実装 `AddAuthzScopeGrpcClient` と同型。
//
// **並走中の正は REST である。** `Services:LlmGatewayGrpc`（h2c のアドレス。例:
// http://llm-gateway:8081）が構成されたときだけ生成クライアントを登録し、未設定なら**何も登録しない**
// —— 呼び出し元は登録の有無で REST 実装と gRPC 実装を選ぶ（戻すのは構成を外すだけ。コードは変えない）。
public static class LlmGatewayGrpcClientExtensions
{
    public const string AddressKey = "Services:LlmGatewayGrpc";

    /// <summary>宛先ごとにチャネルを分けるための DI キー（下の 🔴 を参照）。</summary>
    public const string ChannelKey = "LlmGatewayGrpc";

    public static IServiceCollection AddLlmGatewayGrpcClient(this IServiceCollection services, IConfiguration config)
    {
        var address = config[AddressKey];
        if (string.IsNullOrWhiteSpace(address))
            return services;

        services.AddPlatformServiceToken(config);
        // 🔴 チャネルは**キー付き**で登録する。`AddAuthzScopeGrpcClient` は同じ `GrpcChannel` 型を
        // **キー無し**で（別アドレスに対して）登録するため、両方を構成したサービス（後続スライスの
        // AiAnalysis 等）でキー無し登録を共有すると、片方のクライアントがもう片方の宛先へ繋がる。
        // キー付きにすると宛先ごとに分かれ、かつ破棄は DI が持つ（チャネルは IDisposable である）。
        services.AddKeyedSingleton(ChannelKey, (sp, _) =>
            GrpcClientExtensions.CreatePlatformChannel(address, sp.GetRequiredService<IServiceTokenProvider>()));
        services.AddSingleton(sp => new Pb.LlmEmbedding.LlmEmbeddingClient(
            sp.GetRequiredKeyedService<GrpcChannel>(ChannelKey)));
        // FR-04, FR-11, IADR-0398 (#1255): テキスト生成の生成クライアント。**同じチャネルを共有する**
        // （宛先が同じ 1 つの LlmGateway であり、チャネルは多重化される。2 本張ると接続が二重になる）。
        services.AddSingleton(sp => new Pb.LlmCompletion.LlmCompletionClient(
            sp.GetRequiredKeyedService<GrpcChannel>(ChannelKey)));
        return services;
    }
}
