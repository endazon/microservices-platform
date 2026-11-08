using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AwesomeAssertions;
using DotNet.Testcontainers.Configurations;
using Grpc.Core;
using Grpc.Net.Client;
using Knowledge.IntegrationTests.Fixtures;
using Qdrant.Client;
using Qdrant.Client.Grpc;
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
    // 「まだ listen していない gRPC の口」への最初の呼び出しが返す状態の集合（#2311 の型。NFR: flake の是正）。
    //
    // 🔴 **Unavailable だけではない。** Grpc.Net.Client 2.83 は送信の失敗を `ResolveRpcExceptionStatusCode` で写し、
    // 原因の連鎖に SocketException / IOException があれば Unavailable、無ければ Internal にする。
    // 接続を受けてすぐ切る相手では、切断を SocketsHttpHandler がどの時機で観測するかで連鎖が変わる:
    //   - preface（client の最初の 24 バイト＋SETTINGS）の書き込み中・最初のフレームの読み込み中に切断を見る
    //     → IOException（Broken pipe / Connection reset / ResponseEnded）を連鎖に持つ → Unavailable
    //   - preface の書き込みは通り、接続が pool へ渡った後・最初のストリームを予約する前に読み込みの側が切断を見て
    //     接続を閉じる → pool が内側の例外を持たない HttpRequestException（"server did not complete the HTTP/2
    //     handshake"）を作る → Internal（CI の run 42175932124 / 42433029088 の実測。手元で約 1/1500 回）
    // 待機（`QdrantGrpcReady.ProbeAsync`）はどちらも「まだ起動していない」と読む（下の決定的な Theory で固定する）。
    private static readonly StatusCode[] NotListeningYet = [StatusCode.Unavailable, StatusCode.Internal];

    // 対照: 既定の待機（/readyz）だけで進むと起きていたこと。同じ相手への最初の gRPC 呼び出しは接続の失敗で落ちる
    // （#2311 の Integration run 40937476986 の `CollectionExistsAsync` の失敗と同じ型）。どちらの状態になるかは時機で決まる。
    [Fact]
    public async Task 接続を受けてすぐ切る相手への最初の呼び出しは接続の失敗で落ちる()
    {
        using var listener = AcceptAndClose(out var port);
        using var client = new QdrantClient("127.0.0.1", port);

        var act = () => client.CollectionExistsAsync("any", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().BeOneOf(NotListeningYet);
    }

    // 待機の問い合わせは、その相手を「まだ起動していない」と読む（例外を投げず false。待機の再試行に任せる）。
    [Fact]
    public async Task 接続を受けてすぐ切る相手はまだ起動していないと読む()
    {
        using var listener = AcceptAndClose(out var port);

        (await QdrantGrpcReady.ProbeAsync("127.0.0.1", port)).Should().BeFalse();
    }

    // 集合の Unavailable の側を決定的に起こす: 誰も待ち受けていない口（接続の拒否＝SocketException）。
    [Fact]
    public async Task 誰も待ち受けていない口への最初の呼び出しはUnavailableで落ちる()
    {
        var port = FreePort();
        using var client = new QdrantClient("127.0.0.1", port);

        var act = () => client.CollectionExistsAsync("any", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unavailable);
    }

    [Fact]
    public async Task 誰も待ち受けていない口はまだ起動していないと読む()
    {
        var port = FreePort();

        (await QdrantGrpcReady.ProbeAsync("127.0.0.1", port)).Should().BeFalse();
    }

    // 集合の各要素を決定的に起こす: 実際の SocketsHttpHandler が接続の失敗で投げる例外の形をそのまま投げる handler を
    // 挟み、Grpc.Net.Client の写像（原因の連鎖 → 状態）を通す。どの形でも最初の呼び出しは集合の要素で落ち、
    // 待機はそれを「まだ起動していない」と読む。
    public static TheoryData<string, StatusCode> ConnectionFailures => new()
    {
        // 接続の拒否（誰も listen していない）。
        { "refused", StatusCode.Unavailable },
        // preface の書き込み中・最初のフレームの読み込み中に切られた（Broken pipe / ResponseEnded）。
        { "io", StatusCode.Unavailable },
        // 接続が pool へ渡った後、最初のストリームを予約する前に閉じた（内側の例外なし。CI で落ちた側）。
        { "handshake-not-completed", StatusCode.Internal },
    };

    [Theory]
    [MemberData(nameof(ConnectionFailures))]
    public async Task 接続の失敗の形ごとに最初の呼び出しは集合の状態で落ちる(string failure, StatusCode expected)
    {
        using var client = ClientFailingWith(failure);

        var act = () => client.CollectionExistsAsync("any", TestContext.Current.CancellationToken);

        var code = (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode;
        code.Should().Be(expected);
        NotListeningYet.Should().Contain(code);
    }

    // 🔴 Internal も「まだ起動していない」と読むこと（状態で絞って Internal を素通しにすると、#2311 が時機しだいで戻る）。
    [Theory]
    [MemberData(nameof(ConnectionFailures))]
    public async Task 接続の失敗の形によらずまだ起動していないと読む(string failure, StatusCode expected)
    {
        _ = expected;

        (await QdrantGrpcReady.ProbeAsync(ClientFailingWith(failure))).Should().BeFalse();
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

    private static QdrantClient ClientFailingWith(string failure)
    {
        Exception exception = failure switch
        {
            "refused" => new HttpRequestException(
                "Connection refused", new SocketException((int)SocketError.ConnectionRefused)),
            "io" => new HttpRequestException(
                "An error occurred while sending the request.",
                new IOException("Unable to write data to the transport connection: Broken pipe.",
                    new SocketException((int)SocketError.Shutdown))),
            "handshake-not-completed" => new HttpRequestException(
                "An HTTP/2 connection could not be established because the server did not complete the HTTP/2 handshake."),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
        };
        var channel = GrpcChannel.ForAddress(
            "http://127.0.0.1:6334",
            new GrpcChannelOptions { HttpHandler = new ThrowingHandler(exception) });
        return new QdrantClient(new QdrantGrpcClient(channel));
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
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
