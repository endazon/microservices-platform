using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Platform.Shared.Infrastructure.Foundation.Grpc;

namespace LlmGateway.Tests.Features.Embeddings.Embed;

// NFR-16, IADR-0379 決定 3, IADR-0397 (#1255): gRPC の器（GrpcKestrelFactory）が使う h2c ポートと
// HTTP/1.1 側の URL を、**実配備と同じ経路（環境変数）**で注入する。
// AuthorizationService 側の同名クラス（参照実装 #1201）と同型である。
//
// 🔴 `WebApplicationFactory.ConfigureAppConfiguration` では間に合わない。
// `AddPlatformGrpcListener` は `builder.Build()` より前に `Grpc:Port` と `urls` を読むため、
// in-memory 構成では未反映のままリスナが 1 つも立たない。プロセス起動時に環境変数で与える。
//
// TestServer を使う他のテスト器（TestWebApplicationFactory）にも同じ環境変数が見えるが、
// TestServer は Kestrel の Listen 構成を使わないので影響しない（AddGrpc は常に呼ばれる設計）。
internal static class GrpcTestConfiguration
{
    // プロセスで 1 回だけ選ぶ空きポート（クラス並列で 2 つ選ぶと衝突するため 1 つに固定する）。
    internal static readonly int GrpcPort = FreeTcpPort();

    [ModuleInitializer]
    internal static void SetGrpcListenerEnvironment()
    {
        Environment.SetEnvironmentVariable("Grpc__Port", GrpcPort.ToString());
        // HTTP/1.1 側は loopback の動的ポート。ListenAnyIP ではなく 127.0.0.1 へ bind させる。
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // 参照を残して「使われていない定数」に見えないようにする（キーの綴りは共通ヘルパが正）。
    internal static string PortKey => GrpcListenerExtensions.PortKey;
}
