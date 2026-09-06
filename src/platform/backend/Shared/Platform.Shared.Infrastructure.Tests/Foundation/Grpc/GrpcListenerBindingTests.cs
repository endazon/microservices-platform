using AwesomeAssertions;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

using Platform.Shared.Infrastructure.Foundation.Grpc;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Grpc;

// NFR-16, ADR-0029, ADR-0075, IADR-0379 決定 3: **h2c リスナが実際にどのインタフェースへ bind するか**を
// ソケットで確かめる。
//
// 🔴 **本群が無いと、元の欠陥は緑のまま通る。** 従前 `AddPlatformGrpcListener` は h2c ポートだけを
// 無条件に `ListenAnyIP` していた。試験は `ASPNETCORE_URLS=http://127.0.0.1:0` で HTTP 側を
// ループバックへ絞っていたが、**gRPC 側はそれを無視して全インタフェースへ開いていた** ——
// 試験を走らせた端末の外から h2c ポートへ到達できる状態である。
// 構成の解決（`ResolveGrpcHost`）は `GrpcListenerExtensionsTests` が固定するが、
// **解決した値が本当に bind へ届いているか**は別の主張であり、ここでしか確かめられない。
//
// 陽性対照を対で置く: 同じ器で「ワイルドカードなら外向きにも開く」ことを確かめる。
// 片側（ループバックのみ）だけを見ると、**そもそも待受が立っていない**状態と区別できない。
[Trait("TestKind", "Unit")]
public sealed class GrpcListenerBindingTests
{
    // ループバックでない自分の IPv4。無ければ「外から」を試せないので skip する
    //（CI のコンテナは 1 枚しか持たないことがある）。
    private static IPAddress? FindNonLoopbackIPv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<WebApplication> StartAsync(string httpUrls, int grpcPort)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [GrpcListenerExtensions.UrlsKey] = httpUrls,
            [GrpcListenerExtensions.PortKey] = grpcPort.ToString(),
        });
        builder.AddPlatformGrpcListener();
        var app = builder.Build();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static bool CanConnect(IPAddress address, int port)
    {
        using var client = new TcpClient();
        try
        {
            // 到達不能なら即座に拒否される（同一ホストなので待たされない）。
            return client.ConnectAsync(address, port).Wait(TimeSpan.FromSeconds(2)) && client.Connected;
        }
        catch (AggregateException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    // 🔴 これが本命。HTTP 側をループバックへ絞ったら、h2c ポートも**外からは開かない**。
    [Fact]
    public async Task Grpc_port_is_not_reachable_from_outside_when_http_binds_loopback_only()
    {
        var outside = FindNonLoopbackIPv4();
        Assert.SkipWhen(outside is null, "ループバック以外の IPv4 が無いため『外から』を試せない。");

        var grpcPort = FreeTcpPort();
        await using var app = await StartAsync("http://127.0.0.1:0", grpcPort);

        // 陽性対照: ループバックからは繋がる（＝待受は立っている。立っていないことと区別する）。
        CanConnect(IPAddress.Loopback, grpcPort).Should().BeTrue(
            "h2c リスナがループバックで待受していること（陽性対照。これが偽なら以下の主張は無意味）");

        CanConnect(outside!, grpcPort).Should().BeFalse(
            "HTTP 側を 127.0.0.1 に絞ったのだから h2c ポートも外向きには開かないこと");

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    // 陰性対照（本番の形）: ワイルドカードなら従来どおり全インタフェースへ開く。
    // **狭める側だけを試すと、本番で繋がらない形を作っても気づけない。**
    [Fact]
    public async Task Grpc_port_stays_reachable_from_outside_when_http_binds_all_interfaces()
    {
        var outside = FindNonLoopbackIPv4();
        Assert.SkipWhen(outside is null, "ループバック以外の IPv4 が無いため『外から』を試せない。");

        var grpcPort = FreeTcpPort();
        await using var app = await StartAsync("http://*:0", grpcPort);

        CanConnect(outside!, grpcPort).Should().BeTrue(
            "コンテナ既定（ワイルドカード）では h2c ポートがメッシュから届くこと");

        await app.StopAsync(TestContext.Current.CancellationToken);
    }
}
