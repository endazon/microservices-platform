using System.Net;
using System.Net.Sockets;

namespace Knowledge.IntegrationTests.Fixtures;

// ADR-0027 / NFR（#441 W4）: ブローカへの経路を**試験から遮断できる**ようにする薄い TCP 中継。
//
// 🔴 **なぜブローカ自体を止めないのか。**
// 実ブローカは Testcontainers のコンテナか、共有クラスタの RabbitMQ である。前者は他の試験と
// 共有され、後者は**他セッションが使っている共有資源**であり、どちらも止めてよいものではない。
// 中継を 1 枚挟み、**自分が作った中継だけを閉じる**ことで「起動後にブローカへ到達できなくなる」を
// 再現する。ブローカには一切触れない。
//
// ⚠️ **限界を正直に書く。** 中継を閉じたときの見え方（接続が切れる）は、ブローカ本体が落ちた場合や
// ネットワーク分断の場合と**完全に同一とは限らない**（RST / 無応答 / 半開の違い）。
// 本器が再現するのは「確立済みの接続が切れ、再接続もできない」状態である。
public sealed class BrokerTcpGate : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _clients = [];

    private BrokerTcpGate(TcpListener listener, string targetHost, int targetPort)
    {
        _listener = listener;
        _targetHost = targetHost;
        _targetPort = targetPort;
        LocalPort = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public int LocalPort { get; }

    // 中継を経由する AMQP URI。元の URI から資格情報をそのまま引き継ぐ。
    public static Uri RewriteToLocal(string originalAmqpUri, int localPort)
    {
        var original = new Uri(originalAmqpUri);
        return new UriBuilder(original) { Host = "127.0.0.1", Port = localPort }.Uri;
    }

    public static BrokerTcpGate Start(string targetAmqpUri)
    {
        var target = new Uri(targetAmqpUri);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var gate = new BrokerTcpGate(listener, target.Host, target.Port <= 0 ? 5672 : target.Port);
        _ = gate.AcceptLoopAsync();
        return gate;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient inbound;
            try { inbound = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }

            lock (_clients) { _clients.Add(inbound); }
            _ = PumpAsync(inbound);
        }
    }

    private async Task PumpAsync(TcpClient inbound)
    {
        try
        {
            using var outbound = new TcpClient();
            await outbound.ConnectAsync(_targetHost, _targetPort, _cts.Token);
            lock (_clients) { _clients.Add(outbound); }

            var a = inbound.GetStream().CopyToAsync(outbound.GetStream(), _cts.Token);
            var b = outbound.GetStream().CopyToAsync(inbound.GetStream(), _cts.Token);
            await Task.WhenAny(a, b);
        }
        catch
        {
            // 遮断後の接続失敗は期待される状態であり、試験の失敗理由を覆い隠さないよう飲む。
        }
        finally
        {
            inbound.Dispose();
        }
    }

    // 🔴 経路を落とす。**以後の接続も既存の接続も通らない。**
    public void Sever()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* 既に停止していてよい */ }
        lock (_clients)
        {
            foreach (var c in _clients)
            {
                try { c.Close(); } catch { /* 片付けの失敗は無視する */ }
            }

            _clients.Clear();
        }
    }

    public ValueTask DisposeAsync()
    {
        Sever();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
