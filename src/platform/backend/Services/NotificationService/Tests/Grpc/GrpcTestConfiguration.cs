using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Platform.Shared.Infrastructure.Foundation.Grpc;

namespace NotificationService.Tests.Grpc;

// NFR-16, [[IADR-0379]] 決定 3, [[IADR-0419]] (#1255): gRPC の器（`GrpcKestrelFactory`）が使う
// h2c ポートと HTTP/1.1 側の URL を、**実配備と同じ経路（環境変数）**で注入する。
//
// 🔴 `WebApplicationFactory.ConfigureAppConfiguration` では間に合わない
// （`TestDatabaseConfiguration` と同型）。`AddPlatformGrpcListener` は `builder.Build()` より前に
// `Grpc:Port` と `urls` を読むため、プロセス起動時に環境変数で与える。
//
// TestServer を使う他のテスト器（`TestWebApplicationFactory`）にも同じ環境変数が見えるが、
// TestServer は Kestrel の Listen 構成を使わないので影響しない（`AddGrpc` は常に呼ばれる設計）。
internal static class GrpcTestConfiguration
{
    // プロセスで 1 回だけ選ぶ空きポート（クラス並列で 2 つ選ぶと衝突するため 1 つに固定する）。
    internal static readonly int GrpcPort = FreeTcpPort();

    [ModuleInitializer]
    internal static void SetGrpcListenerEnvironment()
    {
        Environment.SetEnvironmentVariable("Grpc__Port", GrpcPort.ToString());
        // 🔴 HTTP/1.1 側は**ループバックの動的ポート**。`ResolveGrpcHost` が HTTP 側の意図に従うため、
        // h2c ポートも 127.0.0.1 にだけ開く（試験を走らせた端末の外から到達できない）。
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

    // NFR-16, IADR-0379 決定 3 (#1509): 試験の待受は**ループバックに限る**。器（GrpcKestrelFactory）が
    // 起動した直後に全待受アドレスを確かめ、1 つでもループバック以外なら待受を止めて起動の時点で落とす。
    // 🔴 従前は `HttpAddress` が `[::]` / `0.0.0.0` を 127.0.0.1 へ**書き換えて**いた。クライアントは常に
    // 127.0.0.1 へ繋ぐので、全インタフェースへ開いても試験は緑のまま通った（隠す側の仕組みだった）。
    // `StartedAsync` はサーバの bind 後に呼ばれ、例外は `StartServer()` まで伝わる
    // （`UseKestrel()` の器は `CreateHost` を通らないため、そちらでは掛けられない。実測）。
    internal sealed class LoopbackOnlyGuard(IServer server) : IHostedLifecycleService
    {
        public async Task StartedAsync(CancellationToken cancellationToken)
        {
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
            var open = addresses.Where(a => !IsLoopback(a)).ToList();
            if (open.Count == 0)
                return;

            await server.StopAsync(cancellationToken);
            throw new InvalidOperationException(
                $"試験の待受がループバック以外に開いた: {string.Join(", ", open)}"
                + $"（全アドレス: {string.Join(", ", addresses)}）。ASPNETCORE_URLS の注入を確かめること。");
        }

        private static bool IsLoopback(string address)
        {
            var host = BindingAddress.Parse(address).Host;
            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
        }

        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
