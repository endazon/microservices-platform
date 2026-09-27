using Microsoft.Extensions.Configuration;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Messaging;

namespace IngestionService.Features.Ingestion.Ingest;

// FR-02, UC-04, ADR-0013, ADR-0016, ADR-0027 (#1640): 取り込みの受け口（`DocumentUpdatedConsumer`）の時間の上限。
//
// 🔴 受け口の ct は Wolverine の 1 通ごとの実行期限（既定 60 秒）を含む。従前は本文取得が `HttpClient` 既定の 100 秒、
// 埋め込み（REST）が 100 秒、埋め込み（gRPC）と Qdrant が期限なしで、止まった依存先は**いつも受け口の ct が先に立つ形で**
// 取り消しとして落ちていた。しかも埋め込みはチャンク数に比例して回数が増えるので、止まっていなくても
// 大きな文書は 60 秒を超え得た。そこで次の上限を**内側から**持つ（値の理由は作業仕様書と IADR の決定に置く）:
//
//   1. 呼び出しごとの期限 —— 本文の取得（`ContentRead`）、埋め込み 1 回（`Embedding`）、Qdrant 1 回（`VectorStore`）。
//   2. **埋め込みの総枠**（`EmbeddingBudget`）—— チャンクを回す前に判定する。使い切ったら残りを呼ばずに時間切れとして投げ、
//      **再試行せずデッドレターへ送る**（`EmbeddingBudgetDeadLetterPolicy`。大きすぎる文書は何度試しても収まらない）。
//      判定は呼び出しの**前**なので、超過は最後の 1 チャンク（埋め込み＋登録）の期限までに収まる。
//      これでチャンク数が上限を持たなくても、受け口の最悪の所要時間は有限の式になる。
//   3. **受け口の実行期限**（`Handler`）—— `HandlerExecutionTimeoutPolicy<DocumentUpdated>` が与える。
//      「既存チャンクの削除（コレクション数 × Qdrant 1 回）＋ 本文の取得 ＋ 総枠 ＋ 最後の 1 チャンク」を
//      超えていなければ起動を止める（`ConsumerHandlerTimeouts.EnsureFits`）。
//   4. 🔴 **1 回の配信の再試行の連鎖**（試行上限 4 × 受け口の実行期限 ＋ 試行間の待ち 42 秒）が、ブローカの `consumer_timeout`
//      （`Messaging:BrokerConsumerTimeoutSeconds`・既定 1800 秒）より短いこと（`ConsumerHandlerTimeouts.EnsureRetryChainFits`）。
//      Wolverine の RabbitMQ の受信は Inline で、再試行は同じ配信の中で回り ack は最後の試行の後である。超えると
//      ブローカが配信を取り上げて再配信し、試行回数が戻る —— 失敗し続ける 1 通が永久に回る。既定では 4 × 420 ＋ 42 ＝ 1722 秒。
public sealed record IngestionTimeouts(
    TimeSpan ContentRead,
    TimeSpan Embedding,
    TimeSpan VectorStore,
    TimeSpan EmbeddingBudget,
    TimeSpan Handler,
    int CollectionCount)
{
    public const string ContentReadKey = "Ingestion:ContentReadTimeoutSeconds";
    public const string EmbeddingKey = "Ingestion:EmbeddingTimeoutSeconds";
    public const string VectorStoreKey = "Ingestion:VectorStoreTimeoutSeconds";
    public const string EmbeddingBudgetKey = "Ingestion:EmbeddingBudgetSeconds";
    public const string HandlerKey = "Ingestion:HandlerTimeoutSeconds";

    public const int DefaultContentReadSeconds = 20;
    public const int DefaultEmbeddingSeconds = 30;
    public const int DefaultVectorStoreSeconds = 10;
    public const int DefaultEmbeddingBudgetSeconds = 300;
    public const int DefaultHandlerSeconds = 420;

    // 既定の構成（appsettings.json の `Embedding:Collections`）が持つモデル別コレクションの数。`Default` だけが使う。
    public const int DefaultCollectionCount = 2;

    // 計器・ログに載せる呼び出し先の名前（閉じた値域）。
    public const string ContentTarget = "content";
    public const string EmbeddingTarget = "embedding";
    public const string VectorStoreTarget = "vector-store";
    public const string EmbeddingBudgetTarget = "embedding-budget";

    public static IngestionTimeouts Default { get; } = new(
        TimeSpan.FromSeconds(DefaultContentReadSeconds),
        TimeSpan.FromSeconds(DefaultEmbeddingSeconds),
        TimeSpan.FromSeconds(DefaultVectorStoreSeconds),
        TimeSpan.FromSeconds(DefaultEmbeddingBudgetSeconds),
        TimeSpan.FromSeconds(DefaultHandlerSeconds),
        DefaultCollectionCount);

    // 既存チャンクの削除（`DeleteByDocumentFromAllAsync`）はコレクション 1 本ごとに Qdrant を 1 回呼ぶ。
    // ポートの 1 回の呼び出しに与える期限は、その回数分の Qdrant の期限である。
    public TimeSpan DeleteFromAll => VectorStore * Math.Max(1, CollectionCount);

    // 構成から読み、受け口の実行期限に最悪の所要時間が収まることを検査する（収まらなければ InvalidOperationException＝起動失敗）。
    // `collectionCount` は既存チャンクを消すモデル別コレクションの数（`Embedding:Collections`。削除は 1 本ごとに 1 回）。
    public static IngestionTimeouts From(IConfiguration configuration, int collectionCount)
    {
        var timeouts = new IngestionTimeouts(
            ConsumerHandlerTimeouts.Seconds(configuration, ContentReadKey, DefaultContentReadSeconds),
            ConsumerHandlerTimeouts.Seconds(configuration, EmbeddingKey, DefaultEmbeddingSeconds),
            ConsumerHandlerTimeouts.Seconds(configuration, VectorStoreKey, DefaultVectorStoreSeconds),
            ConsumerHandlerTimeouts.Seconds(configuration, EmbeddingBudgetKey, DefaultEmbeddingBudgetSeconds),
            ConsumerHandlerTimeouts.Seconds(configuration, HandlerKey, DefaultHandlerSeconds),
            collectionCount);

        ConsumerHandlerTimeouts.EnsureFits(DocumentUpdatedConsumer.StepName, timeouts.Handler, HandlerKey,
            ($"既存チャンクの削除（{collectionCount} コレクション × {VectorStoreKey}）", timeouts.DeleteFromAll),
            ($"本文の取得（{ContentReadKey}）", timeouts.ContentRead),
            ($"埋め込みの総枠（{EmbeddingBudgetKey}）", timeouts.EmbeddingBudget),
            ($"最後の 1 チャンクの埋め込み（{EmbeddingKey}）", timeouts.Embedding),
            ($"最後の 1 チャンクの登録（{VectorStoreKey}）", timeouts.VectorStore));

        ConsumerHandlerTimeouts.EnsureRetryChainFits(DocumentUpdatedConsumer.StepName, timeouts.Handler, HandlerKey,
            WolverineExtensions.MaxAttempts, WolverineExtensions.TotalRetryCooldown,
            ConsumerHandlerTimeouts.BrokerConsumerTimeout(configuration));

        return timeouts;
    }
}
