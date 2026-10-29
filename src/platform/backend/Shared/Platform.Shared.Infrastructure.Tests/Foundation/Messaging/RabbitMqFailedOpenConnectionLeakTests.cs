using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Messaging;

// 🔴 NFR, ADR-0027, [[IADR-0648]] (#2301): **開き損ねた接続が、裏で自動回復を続けて後から生き返らない**こと。
//
// AST#1482 / AST#1491（AST の IADR-0595）で判明した RabbitMQ.Client 7.1.2 の欠陥。MSP も WolverineFx.RabbitMQ 6.24.4 の推移で
// 7.1.2 を解決しており、同じ欠陥を持っていた。AST の PoC では、ブローカの準備前に起動した Worker の Wolverine が開始を再試行し
// （5 秒ごと・最大 20 回）、準備が整った後に接続が 115〜131 本（平常 22 本）残った。
//
// 根本原因（AST の調査。7.1.2 を逆コンパイルして確認）: AutorecoveringConnection.CreateAsync は回復の購読をした後に
// 内側の接続を開き、開くのに失敗しても（ハンドシェイクの途中で切られた・応答が無い）その AutorecoveringConnection を閉じない。
// 開き損ねの shutdown（Library・541 "FailedOpen"）が自動回復を起動し、例外を受けた呼び出し元（Wolverine の起動の再試行）が
// 次の試行で新しい接続を作る裏で、**捨てられた接続が回復の間隔ごとに繋ぎ直しを続け、ブローカの準備が整った時点で一斉に繋がる**。
// Wolverine は接続の自動回復を必ず有効にする（RabbitMqTransport.configureDefaults）。7.2.x は開き損ねた接続を閉じて破棄する
// （上流 rabbitmq-dotnet-client#1857。7.2.0 で是正）。
//
// 試験は AST の RabbitMqFailedOpenConnectionLeakTests と同形である: AMQP のプロトコルヘッダを受けたら黙って切る偽のブローカへ、
// Wolverine と同じく自動回復を有効にして接続する。開くのは失敗する（期待どおり）。その後、回復の間隔の何倍待っても、
// 偽のブローカへの接続の試みは最初の 1 回だけである。版を 7.1.x へ戻すと赤になる。
//
// ■ 素の RabbitMQ.Client は本番コードでは不採用（ADR-0030。Wolverine が置き換える。check-backend-libraries が using を止める）。
//   本試験はライブラリそのものの欠陥を再現するので素のクライアントを使う。同じ試験プロジェクトの WolverineExtensionsTests
//   （ブローカ TLS の試験）と同じく、using を置かずに完全修飾名で書く（本番の名前空間へ素のクライアントを持ち込まない）。
public class RabbitMqFailedOpenConnectionLeakTests
{
    [Fact]
    public async Task 開き損ねた接続は裏で繋ぎ直しを続けない()
    {
        using var broker = new HandshakeDroppingBroker();
        var factory = new RabbitMQ.Client.ConnectionFactory
        {
            Uri = new Uri($"amqp://guest:guest@127.0.0.1:{broker.Port}"),
            // Wolverine の RabbitMqTransport.configureDefaults と同じ（接続の自動回復は常に有効）。
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromMilliseconds(100),
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
            HandshakeContinuationTimeout = TimeSpan.FromSeconds(5),
        };

        var open = async () => await factory.CreateConnectionAsync(TestContext.Current.CancellationToken);
        await open.Should().ThrowAsync<RabbitMQ.Client.Exceptions.BrokerUnreachableException>("前提: 偽のブローカはハンドシェイクの途中で切る");
        // 開く処理の中の試みは 1 回とは限らない。CPU が詰まった環境では、例外が返った時点で 2 回になる（4 コアに 8 本の
        // 負荷を掛けて 6 回とも 2・その後は増えない、を実測）。数えるのは**開き損ねた後に増えるか**であり、開く処理の中の回数ではない。
        broker.Accepted.Should().BeInRange(1, 2, "前提: 開く試みは開く処理の中で終わる");

        // 開く処理の後始末が落ち着くまで回復の間隔（100 ms）の 3 倍待ち、そこからさらに 15 倍待って増えないことを見る。
        // 是正前（7.1.2）はこの間に捨てられた接続が回復の間隔ごとに繋ぎ直しを続ける（1.5 秒で 15 回）。
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        var settled = broker.Accepted;
        await Task.Delay(TimeSpan.FromMilliseconds(1_500), TestContext.Current.CancellationToken);

        broker.Accepted.Should().Be(settled, "開き損ねた接続は閉じて破棄され、裏で自動回復を続けない（#2301）");
    }

    // AMQP のプロトコルヘッダ（8 バイト）を読んだら何も返さずに切る TCP の受け口。受けた接続の数を数える。
    private sealed class HandshakeDroppingBroker : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _accepted;

        public HandshakeDroppingBroker()
        {
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Accepted => Volatile.Read(ref _accepted);

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception) when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                Interlocked.Increment(ref _accepted);
                using (client)
                {
                    var header = new byte[8];
                    try
                    {
                        var stream = client.GetStream();
                        var read = 0;
                        while (read < header.Length)
                        {
                            var n = await stream.ReadAsync(header.AsMemory(read), _stop.Token).ConfigureAwait(false);
                            if (n == 0)
                                break;
                            read += n;
                        }
                    }
                    catch (Exception)
                    {
                        // 読めなくても切るだけ。
                    }
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
