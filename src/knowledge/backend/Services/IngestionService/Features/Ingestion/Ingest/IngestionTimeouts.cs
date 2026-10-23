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
//      ［2026-10-17 / #1746・[[IADR-0560]]］**語彙索引への書き込みの輪（高機密の文書・埋め込み待ちの点）にも同じ値の総枠を掛ける**
//      （呼び出し先 `lexical-budget`。デッドレターへの直行も同じ）。待ちの点の輪は埋め込みの輪の始めから続けて測るので、
//      1 文書のチャンクの輪全体が 1 つの総枠に収まり、下の式（`EnsureFits`）は変わらない。
//   3. **受け口の実行期限**（`Handler`）—— `HandlerExecutionTimeoutPolicy<DocumentUpdated>` が与える。
//      「既存チャンクの削除（コレクション数 × Qdrant 1 回）＋ 本文の取得 ＋ 総枠 ＋ 最後の 1 チャンク」を
//      超えていなければ起動を止める（`ConsumerHandlerTimeouts.EnsureFits`）。
//      ［2026-10-23 / #2101・[[IADR-0595]] 決定 5］版の印の照会・記録・読み直し（Qdrant 3 回）と、書き終えた後の
//      旧版の点の削除（コレクション数 × Qdrant 1 回）を足した。既定（2 本）で 20＋30＋20＋20＋300＋30＋10＝430 秒になるので、
//      受け口の実行期限の既定を 420 → 435 秒へ上げた（連鎖は 4 × 435 ＋ 42 ＝ 1782 秒 ＜ 1800 秒）。
//      ［2026-10-24 / #2066・[[IADR-0613]]］🔴 **コレクションを足す opt-in（Ruri・決定的ローカル埋め込み）で既定が起動を止めていた。**
//      削除は 2 回 × コレクション数 × Qdrant なので、3 本で 450 秒・4 本で 470 秒になり、受け口の期限は連鎖（決定 4）から
//      439 秒より上げられない —— 既定のままでは**どの値を与えても起動できない**組み合わせだった。そこで**総枠を構成で
//      与えていなければ**、総枠の既定を「受け口の期限 − 他の項目 − 余白 5 秒」へ縮める（上限は 300 秒。既定の 2 本では
//      300 秒のまま）。3 本で 280 秒・4 本で 260 秒。総枠を構成で与えたときは縮めず、収まらなければ従前どおり起動を止める。
//   4. 🔴 **1 回の配信の再試行の連鎖**（試行上限 4 × 受け口の実行期限 ＋ 試行間の待ち 42 秒）が、ブローカの `consumer_timeout`
//      （`Messaging:BrokerConsumerTimeoutSeconds`・既定 1800 秒）より短いこと（`ConsumerHandlerTimeouts.EnsureRetryChainFits`）。
//      Wolverine の RabbitMQ の受信は Inline で、再試行は同じ配信の中で回り ack は最後の試行の後である。超えると
//      ブローカが配信を取り上げて再配信し、試行回数が戻る —— 失敗し続ける 1 通が永久に回る。既定では 4 × 435 ＋ 42 ＝ 1782 秒。
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
    public const int DefaultHandlerSeconds = 435;

    // FR-02, [[IADR-0613]] (#2066): 総枠を構成で与えていないときに、受け口の期限の内側に残す余白（完了イベントの発行など、
    // 期限の式の外の処理のため）。既定の構成（2 本）での余り 435 − 430 ＝ 5 秒と同じ値で、既定の 2 本では総枠は 300 秒のまま。
    public const int DefaultBudgetHeadroomSeconds = 5;

    // 既定の構成で削除が回るコレクションの数。`Default` だけが使う。
    // appsettings.json の `Embedding:Collections`（モデル別。既定は voyage の 1 本）＋ 語彙索引 1 本
    // （［2026-10-05 / #1746］[[IADR-0497]] 決定 4。語彙索引からも消すので 1 本増えた）。
    // ［2026-10-17 / #1746 段 S4］ADR-0127 決定 5, [[IADR-0085]]: Ruri のコレクションは既定に無くなった（opt-in のときだけ
    // 足され、そのときは `From` が実際の本数で数える）。3 → 2。
    public const int DefaultCollectionCount = 2;

    // 計器・ログに載せる呼び出し先の名前（閉じた値域）。
    public const string ContentTarget = "content";
    public const string EmbeddingTarget = "embedding";
    public const string VectorStoreTarget = "vector-store";
    public const string EmbeddingBudgetTarget = "embedding-budget";
    // FR-02, ADR-0127 決定 1, [[IADR-0560]] (#1746): 語彙索引への書き込みの輪の総枠（値は `EmbeddingBudget` と同じ）。
    public const string LexicalBudgetTarget = "lexical-budget";

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

    // FR-02, [[IADR-0595]] 決定 1・5 (#2101): 1 通の取り込みが版の印に触る回数（照会・記録・書き終えた後の読み直し）。
    public const int IndexVersionCalls = 3;

    // 構成から読み、受け口の実行期限に最悪の所要時間が収まることを検査する（収まらなければ InvalidOperationException＝起動失敗）。
    // `collectionCount` は既存チャンクを消すモデル別コレクションの数（`Embedding:Collections`。削除は 1 本ごとに 1 回）。
    public static IngestionTimeouts From(IConfiguration configuration, int collectionCount)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var contentRead = ConsumerHandlerTimeouts.Seconds(configuration, ContentReadKey, DefaultContentReadSeconds);
        var embedding = ConsumerHandlerTimeouts.Seconds(configuration, EmbeddingKey, DefaultEmbeddingSeconds);
        var vectorStore = ConsumerHandlerTimeouts.Seconds(configuration, VectorStoreKey, DefaultVectorStoreSeconds);
        var handler = ConsumerHandlerTimeouts.Seconds(configuration, HandlerKey, DefaultHandlerSeconds);

        // 🔴 FR-02, [[IADR-0613]] (#2066): 総枠を構成で与えていなければ、既定（300 秒）を上限に、受け口の期限から
        // 総枠以外の項目と余白を引いた値へ縮める。コレクションを足す opt-in（3〜4 本）でも既定のまま起動できるようにする。
        // 構成で与えた総枠は縮めない（運用者の値を黙って変えない。収まらなければ下の検査が起動を止める）。
        var embeddingBudget = configuration.GetValue<int?>(EmbeddingBudgetKey) is null
            ? DefaultBudgetFor(handler, contentRead, embedding, vectorStore, collectionCount)
            : ConsumerHandlerTimeouts.Seconds(configuration, EmbeddingBudgetKey, DefaultEmbeddingBudgetSeconds);

        var timeouts = new IngestionTimeouts(contentRead, embedding, vectorStore, embeddingBudget, handler, collectionCount);

        ConsumerHandlerTimeouts.EnsureFits(DocumentUpdatedConsumer.StepName, timeouts.Handler, HandlerKey,
            ($"既存チャンクの削除（{collectionCount} コレクション × {VectorStoreKey}）", timeouts.DeleteFromAll),
            ($"版の印の照会・記録・読み直し（3 × {VectorStoreKey}）", timeouts.VectorStore * IndexVersionCalls),
            ($"旧版の点の削除（{collectionCount} コレクション × {VectorStoreKey}）", timeouts.DeleteFromAll),
            ($"本文の取得（{ContentReadKey}）", timeouts.ContentRead),
            ($"埋め込みの総枠（{EmbeddingBudgetKey}）", timeouts.EmbeddingBudget),
            ($"最後の 1 チャンクの埋め込み（{EmbeddingKey}）", timeouts.Embedding),
            ($"最後の 1 チャンクの登録（{VectorStoreKey}）", timeouts.VectorStore));

        ConsumerHandlerTimeouts.EnsureRetryChainFits(DocumentUpdatedConsumer.StepName, timeouts.Handler, HandlerKey,
            WolverineExtensions.MaxAttempts, WolverineExtensions.TotalRetryCooldown,
            ConsumerHandlerTimeouts.BrokerConsumerTimeout(configuration));

        return timeouts;
    }

    // FR-02, [[IADR-0613]] (#2066): 総枠を構成で与えていないときの総枠。下の `EnsureFits` の式の総枠以外の項目
    // （削除 2 回・版の印 3 回・本文・最後の 1 チャンク）と余白を受け口の期限から引き、既定の 300 秒で頭を打つ。
    // 1 秒を下回るほど期限が短いなら 1 秒にし、判定は `EnsureFits` に委ねる（起動を止める）。
    private static TimeSpan DefaultBudgetFor(
        TimeSpan handler, TimeSpan contentRead, TimeSpan embedding, TimeSpan vectorStore, int collectionCount)
    {
        var deleteFromAll = vectorStore * Math.Max(1, collectionCount);
        var others = deleteFromAll * 2 + vectorStore * IndexVersionCalls + contentRead + embedding + vectorStore;
        var fitted = handler - others - TimeSpan.FromSeconds(DefaultBudgetHeadroomSeconds);
        var cap = TimeSpan.FromSeconds(DefaultEmbeddingBudgetSeconds);
        return fitted > cap ? cap : fitted < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : fitted;
    }
}
