using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Platform.Shared.Infrastructure.Foundation.Grpc;

namespace DashboardService.Tests.Grpc;

// NFR-16, [[IADR-0379]] 決定 3, [[IADR-0408]] (#1255): gRPC の器（`GrpcKestrelFactory`）が使う
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

    // 参照を残して「使われていない定数」に見えないようにする（キーの綴りは共通ヘルパが正）。
    internal static string PortKey => GrpcListenerExtensions.PortKey;
}
