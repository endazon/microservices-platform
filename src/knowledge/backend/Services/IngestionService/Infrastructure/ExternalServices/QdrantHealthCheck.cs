using Microsoft.Extensions.Diagnostics.HealthChecks;
using Qdrant.Client;

namespace IngestionService.Infrastructure.ExternalServices;

// NFR-21, NFR-06, [[IADR-0602]] (#2140): 取り込みの**自前のデータストア**（Qdrant。チャンクの書き込み先であり、
// 索引の状態の照会〔DocumentIndexStatus〕の読み先）への疎通を readiness へ載せる。
// 取り込みが実際に使うのと**同じ gRPC クライアント**（`Qdrant:Host` / `Qdrant:Port`）で問う —— 別ポート（6333 の REST）を
// 決め打ちで問うと、実際の経路が切れていても緑になり得るためである。
// 🔴 例外を握り潰して Healthy を返さない。届かなければ Unhealthy（例外の文言は本文に載せない）。
public sealed class QdrantHealthCheck(QdrantClient client) : IHealthCheck
{
    public const string Name = "qdrant";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await client.HealthAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Qdrant に到達できない", ex);
        }
    }
}
