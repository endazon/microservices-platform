using IngestionService.Common.Concurrency;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IngestionService.Infrastructure.ExternalServices;

// FR-02, NFR-21, [[IADR-0650]] (#2283): **コレクション・索引の保証が一度成功するまで、取り込みは Ready にしない。**
// 従前は起動時の 1 回の保証が失敗しても Error のログだけで、Pod は Ready のまま書き込みに失敗し続けた
// （MSP#2281 の e2e-live で G13「コレクションが 1 件も無い」。Deployment は 18/18 available）。
// 書き込めないスタックを Ready に見せない —— `check-stack-ready` の「Deployment が available」で捕まる。
// 疎通（`QdrantHealthCheck`）とは別の問いである（届いても保証が済んでいなければ書き込めない）。
public sealed class QdrantBootstrapHealthCheck(QdrantBootstrapState state) : IHealthCheck
{
    public const string Name = "qdrant-bootstrap";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.IsEnsured
            ? HealthCheckResult.Healthy()
            : new HealthCheckResult(context.Registration.FailureStatus,
                "Qdrant のコレクション・索引の保証がまだ成功していない（再試行中）"));
}
