using IngestionService.Domain.Ports;
using RetrievalService.Domain.Ports;
using RetrievalService.Infrastructure.ExternalServices;

namespace Knowledge.IntegrationTests.Search;

// FR-02, FR-03, ADR-0009, [[IADR-0390]] (#1247): 取り込み側の書き込みポートを、
// 検索側が読む索引へそのまま落とす**橋**。
//
// 取り込み（`IIngestionVectorStore`）と検索（`IVectorStore`）は**別サービスの別ポート**であり、
// 本番ではどちらも Qdrant の同じコレクションを指すことで繋がっている。**その「同じ索引を指している」
// という前提そのものが、in-repo のテストで 1 度も測られていなかった**（#1247）。
//
// 本クラスは Docker の無い環境で段 5（書き込み）と段 6（読み出し）を繋ぐ唯一の実体である。
// 索引の実体は RetrievalService の `InMemoryVectorStore`（**本番コードに実在するポート実装**）で、
// テストは書き手と読み手へ**同一インスタンス**を注入する。
//
// 🔴 **本クラスは 2 つの Qdrant アダプタの表現の一致を測れない。**
// 自分で書いた写像は自分と必ず一致する —— ペイロード鍵（`text` / `document_id` / `attributes.*`）が
// 書き込み側と読み出し側でずれる型の欠陥（[[IADR-0014]]）は、**実 Qdrant を立てる
// `IngestToSearchQdrantTests` の担当**である。ここでその担当を兼ねたと読まないこと。
//
// 🔴 **`HasBody` を写す。** メタデータ点（本文なし）は `HasBody: false` で入れる ——
// ここを既定の `true` にすると、[[IADR-0358]] 決定 3 が閉じた
// 「題名由来の索引テキストが本文抜粋として利用者へ返る」が本テストの下で再発し、
// しかも**テストは緑のまま**になる。
internal sealed class SharedIndexIngestionVectorStore(InMemoryVectorStore index) : IIngestionVectorStore
{
    // コレクションの分離（ADR-0016 のモデル別ルーティング）は `InMemoryVectorStore` が持たない。
    // **書かれたコレクション名を記録するだけ**にして、テスト側が「どこへ書いたか」を主張できるようにする
    // —— 稼働環境で検索が全件 0 件になった事故（#1215）の一因は**読み書き先コレクションの不一致**で
    // あり、その軸を落とすと本テストは事故を再現できない。
    private readonly List<string> _collections = [];

    public IReadOnlyList<string> WrittenCollections
    {
        get { lock (_collections) return _collections.ToList(); }
    }

    public Task EnsureCollectionsAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task UpsertChunkAsync(string collection, Guid chunkId, Guid documentId, string title,
        string text, int chunkIndex, float[] vector, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
        CancellationToken ct = default)
    {
        Record(collection);
        return index.UpsertAsync(
            new ChunkPayload(chunkId, documentId, title, text, vector, markdownUri,
                attributes, tags, updatedAt, HasBody: true, SharedWith: sharedWith), ct);
    }

    public Task UpsertMetadataPointAsync(string collection, Guid pointId, Guid documentId, string title,
        string indexText, float[] vector, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
        CancellationToken ct = default)
    {
        Record(collection);
        return index.UpsertAsync(
            new ChunkPayload(pointId, documentId, title, indexText, vector, markdownUri,
                attributes, tags, updatedAt, HasBody: false), ct);
    }

    // 取り込みは「全コレクションから消す」。索引が 1 つしか無いのでそのまま 1 回消す。
    public Task DeleteByDocumentFromAllAsync(Guid documentId, CancellationToken ct = default)
        => index.DeleteByDocumentAsync(documentId, ct);

    private void Record(string collection)
    {
        lock (_collections) _collections.Add(collection);
    }
}

// LLM ゲートウェイを立てないため、**決定的な**ベクトルを返すスタブ。
//
// 🔴 零ベクトルにしない。`InMemoryVectorStore.SearchWithinDocumentsAsync` はコサイン類似度で採点し、
// ノルム 0 をスコア 0 にする（#995 の縮退）。零ベクトルだと「ベクトル側が一切効いていない」状態で
// 緑になり得るため、**語ごとに違う非零ベクトル**を返す。
internal sealed class DeterministicEmbeddingService(string collection)
    : IngestionService.Domain.Ports.IEmbeddingService
{
    internal const int Dimensions = 8;

    public Task<EmbeddingResult> EmbedAsync(string text, string? confidentiality,
        CancellationToken ct = default)
        => Task.FromResult(new EmbeddingResult(Vectorize(text), collection, Embedded: true));

    // 文字を 8 個のバケットへ畳む単純なハッシュ埋め込み。同じ文字列は必ず同じベクトルになり、
    // 違う文字列はほぼ違うベクトルになる（検索語と本文が「似ている」ことまでは保証しない ——
    // 意味の近さは本テストの主張ではない）。
    internal static float[] Vectorize(string text)
    {
        var v = new float[Dimensions];
        foreach (var ch in text)
            v[ch % Dimensions] += 1f;
        // 全ゼロ（空文字列）を避ける —— ノルム 0 はスコア 0 に潰れる。
        if (v.All(x => x == 0f)) v[0] = 1f;
        return v;
    }
}

// storage:// を実取得できないため、渡された本文を返すだけのリーダ。
// **本文はテストが決める** —— 検索語が当たる／当たらないの対を作るために可変にしてある。
internal sealed class FixedContentReader(string body) : IDocumentContentReader
{
    public Task<string> ReadAsync(string markdownUri, string title, CancellationToken ct = default)
        => Task.FromResult(body);
}

// 取り込み完了イベントの発行先。ブローカを立てないので呼び出しを記録するだけ。
internal sealed class RecordingCompletedPublisher : IIngestionCompletedPublisher
{
    private readonly List<(Guid DocumentId, int ChunkCount)> _completed = [];

    public IReadOnlyList<(Guid DocumentId, int ChunkCount)> Completed
    {
        get { lock (_completed) return _completed.ToList(); }
    }

    public Task PublishCompletedAsync(Guid documentId, int chunkCount, DateTimeOffset completedAt,
        CancellationToken ct = default)
    {
        lock (_completed) _completed.Add((documentId, chunkCount));
        return Task.CompletedTask;
    }
}
