using Grpc.Core;
using Knowledge.Contracts.Indexing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Qdrant.Client;
using RetrievalService.Common.Observability;

namespace RetrievalService.Infrastructure.ExternalServices;

// FR-03, UC-01, NFR-06, #1118, [[IADR-0339]] 決定 3:
// **検索が見ているコレクションに日本語 2-gram（`text_ngram`）の全文ペイロードインデックスが在るか**を readiness に載せる。
//
// `QdrantFullTextIndexHealthCheck`（`text`）と同型で、見るキーだけが違う。1 つの check に畳まず別に置くのは、
// **`text` だけで Healthy を固定している既存の試験を動かさない**ためと、Degraded の本文で
// 「識別子の系統は生きていて日本語の系統だけが死んでいる」ことを区別できるようにするためである。
//
// 🔴 索引が無くても Qdrant v1.18.1 は例外を返さず、`text_ngram` への Match を部分文字列の全走査へ落とす
// （`text` と同じ。実機で実測）。したがって**索引の存在そのものを見るしかない**。
// **Degraded であって Unhealthy ではない**（ベクトル側も識別子の系統も生きている。NFR-06）。
public sealed class QdrantCjkNgramIndexHealthCheck(
    QdrantClient client, IConfiguration config, KeywordSearchMetrics metrics)
    : IHealthCheck
{
    public const string Name = "qdrant-cjk-ngram-index";

    // 検索と同じ解決を使う（別々に書くと「見ていないコレクションの索引を健全と報告する」）。
    private readonly string _collection = QdrantVectorStore.ResolveCollectionName(config);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var info = await client.GetCollectionInfoAsync(_collection, cancellationToken);

            if (info.PayloadSchema.TryGetValue(CjkBigramPayload.PayloadKey, out var schema)
                && schema.DataType == Qdrant.Client.Grpc.PayloadSchemaType.Text)
            {
                return HealthCheckResult.Healthy(
                    $"日本語 2-gram の全文ペイロードインデックス（{CjkBigramPayload.PayloadKey}）は "
                    + $"コレクション {_collection} に在る");
            }

            // 🔴 数える（0 が正常）。
            metrics.RecordDegraded(KeywordSearchMetrics.MissingNgramIndexReason);
            return HealthCheckResult.Degraded(
                $"コレクション {_collection} に日本語 2-gram の全文ペイロードインデックス"
                + $"（{CjkBigramPayload.PayloadKey} / text）が無い。"
                + "キーワード検索は識別子・型番・略語には当たるが、日本語の語では全文検索として機能していない"
                + "（Qdrant は例外を返さず部分文字列の全走査へ縮退する）。"
                + "取り込みサービスの起動時ブートストラップ（QdrantBootstrapHostedService）を確認すること");
        }
        catch (RpcException ex)
        {
            return HealthCheckResult.Degraded(
                $"コレクション {_collection} の payload_schema を読めないため、"
                + "日本語 2-gram の全文ペイロードインデックスの有無を判定できない", ex);
        }
    }
}
