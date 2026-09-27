using Grpc.Core;
using Platform.Shared.Infrastructure.Foundation.Grpc;

namespace McpServer.Infrastructure.ExternalServices;

// FR-16, NFR-09, NFR-16, IADR-0462（2026-09-26 追記 / #1515・2026-09-27 追記 / #1516）: MCP サーバーの gRPC 呼び出し
// （申告の収集・ツールの実行）で、**s2s トークンの取得失敗**を見分けるための印。
//
// 取得失敗は CallCredentials の中で起き、gRPC クライアントが例外を包み直すので型では見分けられない ——
// 発行側を包んで印を付け（`ServiceTokenAcquisitionException`）、例外の連鎖から印を探す（経路 ⑤ と同じ手）。
// 取得失敗は配線不備（`ServiceToken:ClientId` / ClientSecret の注入漏れ等。再起動では直らない）なので、
// 呼び出し側は一過性の不達と分けて Error で記録する。
// ［#1516］収集器の private 型だったものを、実行器と 1 つを共有するためにここへ出した（挙動は変えていない）。
internal static class ServiceTokenFailures
{
    // 発行側の失敗に印を付ける（取り消しは印を付けずにそのまま通す）。
    public static IServiceTokenProvider Marking(IServiceTokenProvider inner) => new MarkingTokenProvider(inner);

    // 例外の連鎖（InnerException と RpcException.Status.DebugException）から取得失敗の印を探す。
    public static Exception? Find(Exception? ex)
    {
        for (var depth = 0; ex is not null && depth < 8; depth++)
        {
            if (ex is ServiceTokenAcquisitionException marked)
                return marked;
            ex = ex is RpcException { Status.DebugException: { } debug } ? debug : ex.InnerException;
        }
        return null;
    }

    private sealed class MarkingTokenProvider(IServiceTokenProvider inner) : IServiceTokenProvider
    {
        public async ValueTask<string> GetTokenAsync(CancellationToken ct)
        {
            try
            {
                return await inner.GetTokenAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new ServiceTokenAcquisitionException(ex);
            }
        }
    }

    private sealed class ServiceTokenAcquisitionException(Exception inner)
        : Exception("s2s トークンの取得に失敗しました。", inner);
}
