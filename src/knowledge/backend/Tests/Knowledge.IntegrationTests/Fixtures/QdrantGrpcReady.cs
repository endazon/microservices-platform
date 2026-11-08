using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Grpc.Core;
using Qdrant.Client;

namespace Knowledge.IntegrationTests.Fixtures;

// FR-02, ADR-0009 (#2283): 統合試験の Qdrant は **gRPC の口が応答するまで**起動完了としない。
//
// 🔴 Testcontainers.Qdrant 4.12.0 の既定の待機は REST の `/readyz`（6333）だけを見る。Qdrant v1.18.1 は
// REST と gRPC を別のスレッドで起こす（上流 `src/main.rs` の "web" と "grpc"）ので、`/readyz` が 200 を返した時点で
// gRPC（6334）がまだ listen していないことがある。その間も Docker のポート転送は接続を受けてすぐ切るため、
// 最初の gRPC 呼び出しが `Unavailable`（Broken pipe）で落ちる（#2311 の Integration run 40937476986 の実測）。
// 外部ポートの TCP 待機（`UntilExternalTcpPortIsAvailable`）では塞げない —— ポート転送が接続を受けてしまうためである。
// そこで gRPC の Health を実際に呼び、応答が返るまで待つ。
public sealed class QdrantGrpcReady : IWaitUntil
{
    public const int GrpcPort = 6334;

    // 1 回の問い合わせの上限。待機の全体の上限は CreateBuilder 側で与える。
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    public async Task<bool> UntilAsync(IContainer container)
        => await ProbeAsync(container.Hostname, container.GetMappedPublicPort(GrpcPort));

    // 応答が返れば true。つながらない・途中で切られる・時間切れは false（例外を投げない —— 待機の再試行に任せる）。
    // 🔴 「つながらない・途中で切られる」は Unavailable だけでなく Internal でも返る（Grpc.Net.Client が
    // 内側に IOException を持たない接続の失敗を Internal に写すため。QdrantGrpcReadyTests の集合が正）。
    // RpcException は状態を問わず false にする —— 状態で絞ると、時機で変わる側を準備完了と取り違える。
    public static Task<bool> ProbeAsync(string host, int port) => ProbeAsync(new QdrantClient(host, port));

    // 試験が接続の失敗の型を決定的に与えるための入口（client は受け取って破棄する）。
    internal static async Task<bool> ProbeAsync(QdrantClient client)
    {
        using var _ = client;
        using var cts = new CancellationTokenSource(ProbeTimeout);
        try
        {
            await client.HealthAsync(cts.Token);
            return true;
        }
        catch (RpcException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
