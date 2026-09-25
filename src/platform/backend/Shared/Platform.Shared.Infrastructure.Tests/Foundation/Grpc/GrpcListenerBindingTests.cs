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
// 対になる主張「ワイルドカードなら全インタフェースへ開く」は、**実際には待ち受けずに**確かめる（#1507）。
// 従前は `http://*:0` で本当にホストを起動しており、試験そのものが 0.0.0.0 で待ち受けて
// 端末の外から届く状態を作っていた。本群の実ソケットはループバック（127.0.0.1）だけに限る。
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

    // 🔴 空きポートは **OS の動的（ephemeral）範囲の外**から選ぶ（PR #1355 の CI で実測した flake）。
    // 従前は `TcpListener(…, 0)` で OS に選ばせて解放し、その番号を後で Kestrel が bind していた。
    // 解放から bind までの間に、並列に走る他のテストプロセスの**送信側ソケット**が同じ番号を取り得る ——
    // 動的範囲（Linux の既定 32768〜60999）は送信側の割当にも使われるからである。CI では gRPC の
    // 試験 25 件が「address already in use」で一斉に落ちた（本番コードは無変更の push で）。
    // 動的範囲の外（20000〜29999）から候補を引き、bind できることを確かめてから返す。残る衝突は
    // 「別プロセスが同時に同じ候補を引く」ときだけである。`Grpc:Port` に 0 は使えない（0 は「立てない」）。
    private static int FreeTcpPort()
    {
        var random = new Random();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var candidate = random.Next(20000, 30000);
            var listener = new TcpListener(IPAddress.Loopback, candidate);
            try
            {
                listener.Start();
                return candidate;
            }
            catch (SocketException)
            {
                // 使用中。次の候補へ。
            }
            finally
            {
                listener.Stop();
            }
        }

        throw new InvalidOperationException("20000〜29999 に空きポートが見つからなかった。");
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
    // 🔴 #1507: 全インタフェースへの待受を試験で実際に立てない。`AddPlatformGrpcListener` が h2c ポートの
    // 待受先を決めるのと同じ関数の連鎖（HTTP 側の解決 → gRPC ホストの解決 → 待受先の判定）を通し、
    // 全インタフェース（`ListenAnyIP`）に倒れることを確かめる。判定が実際の bind に届いていることは、
    // 上のループバックの試験が実ソケットで確かめている（同じ `Listen` を通る）。
    [Theory]
    [InlineData("http://*:0")]
    [InlineData("http://+:0")]
    [InlineData("http://0.0.0.0:0")]
    [InlineData("http://[::]:0")]
    public void Grpc_port_targets_all_interfaces_when_http_binds_all_interfaces(string httpUrls)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [GrpcListenerExtensions.UrlsKey] = httpUrls })
            .Build();

        var grpcHost = GrpcListenerExtensions.ResolveGrpcHost(GrpcListenerExtensions.ResolveHttpAddresses(config));
        var target = GrpcListenerExtensions.ResolveListenTarget(
            Microsoft.AspNetCore.Http.BindingAddress.Parse($"http://{grpcHost}:50051"));

        target.Kind.Should().Be(GrpcListenerExtensions.ListenTargetKind.AnyIP,
            "コンテナ既定（ワイルドカード）では h2c ポートがメッシュから届くこと");
    }

    // 対: ループバックに絞った構成は、待受先の判定でもループバックの特定アドレスになる。
    [Fact]
    public void Grpc_port_targets_loopback_when_http_binds_loopback_only()
    {
        var target = GrpcListenerExtensions.ResolveListenTarget(
            Microsoft.AspNetCore.Http.BindingAddress.Parse("http://127.0.0.1:50051"));

        target.Kind.Should().Be(GrpcListenerExtensions.ListenTargetKind.Specific);
        target.Address.Should().Be(IPAddress.Loopback);
    }
}
