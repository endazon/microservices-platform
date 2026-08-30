using Grpc.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Qdrant.Client;
using RetrievalService.Common.Observability;

namespace RetrievalService.Infrastructure.ExternalServices;

// FR-03, UC-01, NFR-06, #1116, [[IADR-0316]] 決定 3:
// **検索が見ているコレクションに `text` の全文ペイロードインデックスが在るか**を readiness に載せる。
//
// 🔴 **本 issue の縮退は例外を伴わない。** Qdrant v1.18.1 は索引が無くても `Match { Text }` を
// 受理し、**部分文字列の全走査**へ黙って落ちる（実機で実測）。したがって
// 「`RpcException` を数える」だけでは**何も捕まらない**。**索引の存在そのものを見るしかない。**
//
// **Degraded であって Unhealthy ではない。**
//   - ベクトル側は生きており、検索は結果を返し続ける。ここで Unhealthy にすると pod が
//     Ready から外れ、**キーワード側の欠落を理由に検索全体を落とす**ことになる。
//     計画 NFR-06（障害時の縮退運転: 検索は継続）に反する。
//   - `MapPlatformHealthChecks` の `/health/ready` は Degraded を **200** で返す（既定の
//     `ResultStatusCodes`）。k8s の readinessProbe は落ちず、**運用（本文・Grafana）からは見える**。
//     「壊れているのに完全に正常に見える」状態だけを解消する、という本 issue の要求どおりの強さである。
//
// **Qdrant へ届かないときも Degraded にする。** 到達性そのものは同じ readiness の
// `qdrant`（`AddUrlGroup`）が Unhealthy で受け持っており、ここで二重に落とす意味が無い。
// ここは「索引が在ると言い切れない」ことだけを報告する。
public sealed class QdrantFullTextIndexHealthCheck(
    QdrantClient client, IConfiguration config, KeywordSearchMetrics metrics)
    : IHealthCheck
{
    public const string Name = "qdrant-fulltext-index";

    // 検索と同じ解決を使う（別々に書くと「見ていないコレクションの索引を健全と報告する」）。
    private readonly string _collection = QdrantVectorStore.ResolveCollectionName(config);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var info = await client.GetCollectionInfoAsync(_collection, cancellationToken);

            if (info.PayloadSchema.TryGetValue(QdrantVectorStore.FullTextKey, out var schema)
                && schema.DataType == Qdrant.Client.Grpc.PayloadSchemaType.Text)
            {
                return HealthCheckResult.Healthy(
                    $"全文ペイロードインデックス（{QdrantVectorStore.FullTextKey}）は "
                    + $"コレクション {_collection} に在る");
            }

            // 🔴 ここが #1116 そのものである。**数える**（0 が正常）。
            metrics.RecordDegraded(KeywordSearchMetrics.MissingIndexReason);
            return HealthCheckResult.Degraded(
                $"コレクション {_collection} に全文ペイロードインデックス"
                + $"（{QdrantVectorStore.FullTextKey} / text）が無い。"
                + "ハイブリッド検索のキーワード側は全文検索として機能していない"
                + "（Qdrant は例外を返さず部分文字列の全走査へ縮退する）。"
                + "取り込みサービスの起動時ブートストラップ（QdrantBootstrapHostedService）を確認すること");
        }
        catch (RpcException ex)
        {
            return HealthCheckResult.Degraded(
                $"コレクション {_collection} の payload_schema を読めないため、"
                + "全文ペイロードインデックスの有無を判定できない", ex);
        }
    }
}
