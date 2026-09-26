using ConversionService.Domain.Ports;
using ConversionService.Infrastructure.Configuration;

namespace ConversionService.Infrastructure.ExternalServices;

// FR-12, UC-06, ADR-0010, ADR-0012, IADR-0008（2026-09-27 追記 / #1621）: REST の図のコード化の登録。
//
// 🔴 **名前付きクライアントの `Timeout` を明示する**（`DiagramCodingLimits.CallTimeout`・既定 20 秒）。
// 従前は未設定で `HttpClient` の既定 100 秒が効いており、受け口の実行期限（Wolverine 既定 60 秒）より長かった ——
// 応答しないゲートウェイは受け口の ct が先に立つ形でしか終わらず、画像保持への縮退に届かなかった。
// 登録を Program.cs から切り出すのは、試験が**本番と同じ登録**を通して期限を測るためである
// （試験側で `HttpClient` を組み立てると、本番の `Timeout` を外しても試験は緑のままになる）。
public static class DiagramCoderRegistration
{
    public const string BaseAddressKey = "Services:LlmGateway";
    public const string DefaultBaseAddress = "http://llm-gateway:5007";

    public static IHttpClientBuilder AddRestDiagramCoder(
        this IServiceCollection services, IConfiguration configuration, DiagramCodingLimits limits)
        => services.AddHttpClient<IDiagramCoder, LlmGatewayDiagramCoder>(c =>
        {
            c.BaseAddress = new Uri(configuration[BaseAddressKey] ?? DefaultBaseAddress);
            c.Timeout = limits.CallTimeout;
        });
}
