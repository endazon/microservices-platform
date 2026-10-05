using Platform.Shared.Infrastructure.Foundation.Messaging;

namespace RetrievalService.Features.Search.RemoveDeleted;

// FR-06, FR-19, ADR-0027, ADR-0057 (#1640): 検索索引からの削除の受け口（`DocumentDeletedConsumer`）の時間の上限。
//
// 🔴 受け口の ct は Wolverine の 1 通ごとの実行期限（本受け口は方針を入れないので既定 60 秒）を含む。Qdrant の gRPC は
// 従前期限なしで、止まった Qdrant はいつも受け口の ct で「取り消し」として切られていた。削除はコレクション 1 本ごとに
// 1 回（主 ＋ `Qdrant:FusedCollections` の本数）呼ぶので、「本数 × Qdrant 1 回」が 60 秒に収まることを起動時に検査する。
// 最悪の所要時間は既定（追加 0 本 ＋ 語彙索引 1 本）で 20 秒であり、60 秒を正当に超えないので実行期限の方針は入れない。
// ［2026-10-05 / #1746］[[IADR-0497]] 決定 4: 語彙索引（`Qdrant:LexicalCollection`）は常に束ねるので、既定の本数は 2 である。
public sealed record DocumentDeletedTimeouts(TimeSpan VectorStore, int CollectionCount)
{
    public const string VectorStoreKey = "Retrieval:VectorStoreDeleteTimeoutSeconds";
    public const int DefaultVectorStoreSeconds = 10;

    // 計器・ログに載せる呼び出し先の名前（閉じた値域）。
    public const string VectorStoreTarget = "vector-store";

    public static DocumentDeletedTimeouts Default { get; } =
        new(TimeSpan.FromSeconds(DefaultVectorStoreSeconds), CollectionCount: 2);

    // `fusedCollectionCount` は主に加えて束ねる追加コレクションの数（`Qdrant:FusedCollections` ＋ 語彙索引 1 本。#1746）。
    public static DocumentDeletedTimeouts From(IConfiguration configuration, int fusedCollectionCount)
    {
        var timeouts = new DocumentDeletedTimeouts(
            ConsumerHandlerTimeouts.Seconds(configuration, VectorStoreKey, DefaultVectorStoreSeconds),
            1 + Math.Max(0, fusedCollectionCount));

        ConsumerHandlerTimeouts.EnsureFits(DocumentDeletedConsumer.StepName,
            ConsumerHandlerTimeouts.WolverineDefault, "Wolverine の既定の実行期限",
            ($"索引からの削除（{timeouts.CollectionCount} コレクション × {VectorStoreKey}）",
                timeouts.VectorStore * timeouts.CollectionCount));

        return timeouts;
    }
}
