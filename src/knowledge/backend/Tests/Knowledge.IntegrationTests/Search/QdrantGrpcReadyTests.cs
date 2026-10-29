using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AwesomeAssertions;
using DotNet.Testcontainers.Configurations;
using Grpc.Core;
using Knowledge.IntegrationTests.Fixtures;
using Qdrant.Client;
using Testcontainers.Qdrant;
using Xunit;

namespace Knowledge.IntegrationTests.Search;

// FR-02, ADR-0009 (#2283): 統合試験の Qdrant の起動待ちが gRPC の口まで見ていることの確認。
//
// 🔴 **Trait を付けない**（コンテナを起こさないので PR の ci.yml で走る。`QdrantTestImageDefinitionTests` と同型）。
// Docker のポート転送は、コンテナの中で gRPC がまだ listen していなくても接続を受けてすぐ切る。
// その相手を「接続を受けてすぐ閉じる TCP の受け口」で再現する。
public sealed class QdrantGrpcReadyTests
{
    // 対照: 既定の待機（/readyz）だけで進むと起きていたこと。同じ相手への最初の gRPC 呼び出しは Unavailable で落ちる
    // （#2311 の Integration run 40937476986 の `CollectionExistsAsync` の失敗と同じ型）。
    [Fact]
    public async Task 接続を受けてすぐ切る相手への最初の呼び出しはUnavailableで落ちる()
    {
        using var listener = AcceptAndClose(out var port);
        using var client = new QdrantClient("127.0.0.1", port);

        var act = () => client.CollectionExistsAsync("any", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unavailable);
    }

    // 待機の問い合わせは、その相手を「まだ起動していない」と読む（例外を投げず false。待機の再試行に任せる）。
    [Fact]
    public async Task 接続を受けてすぐ切る相手はまだ起動していないと読む()
    {
        using var listener = AcceptAndClose(out var port);

        (await QdrantGrpcReady.ProbeAsync("127.0.0.1", port)).Should().BeFalse();
    }

    [Fact]
    public async Task 誰も待ち受けていない口はまだ起動していないと読む()
    {
        var port = FreePort();

        (await QdrantGrpcReady.ProbeAsync("127.0.0.1", port)).Should().BeFalse();
    }

    // 🔴 試験の Qdrant を組み立てる唯一の入口が、gRPC の待機を持っていること（外すと赤）。
    [Fact]
    public void 試験のQdrantはgRPCの待機を持つ()
    {
        // 構成は builder の protected なプロパティにしか出ていない（基底と派生で同名が 2 つあるので型で選ぶ）。
        var config = typeof(QdrantBuilder)
            .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(p => p.Name == "DockerResourceConfiguration" && typeof(IContainerConfiguration).IsAssignableFrom(p.PropertyType))
            .Select(p => (IContainerConfiguration)p.GetValue(QdrantTestImage.CreateBuilder())!)
            .First();

        var untils = config.WaitStrategies
            .Select(w => typeof(WaitStrategy)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(f => f.GetValue(w))
                .OfType<IWaitUntil>()
                .FirstOrDefault())
            .ToList();

        untils.OfType<QdrantGrpcReady>().Should().ContainSingle(
            "REST の /readyz だけでは gRPC の口が開く前に進むことがある");
    }

    private static TcpListener AcceptAndClose(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var socket = await listener.AcceptSocketAsync();
                    socket.Close();
                }
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                // 試験の終わりに受け口を止めた。
            }
        });
        return listener;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
