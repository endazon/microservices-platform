using IngestionService.Domain.Ports;
using IngestionService.Domain;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Indexing;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace IngestionService.Infrastructure.ExternalServices;

// ADR-0009, ADR-0016: IngestionService から Qdrant へ直接書き込む（モデル別コレクション対応）。
public class QdrantIngestionVectorStore(
    QdrantClient client, IOptions<EmbeddingCollectionsOptions> collections)
    : IIngestionVectorStore
{
    private readonly IReadOnlyList<EmbeddingCollectionOptions> _collections = collections.Value.Collections;

    // FR-03, #1116: 全文検索が引くペイロードキー。
    // **検索側（RetrievalService.QdrantVectorStore.KeywordSearchAsync の FieldCondition.Key）と
    // 書き込み側（BuildChunkPayload の "text"）と同じ 1 つの値でなければならない。**
    // サービスを跨ぐため型では束ねられない（`document_id` と同じ事情。[[IADR-0014]]）。
    internal const string FullTextKey = "text";

    // FR-02: 全モデル別コレクションの存在を保証する。未作成なら各コレクションの次元で作成する。
    //
    // FR-03, #1116: **コレクションの存在に関わらず、`text` の全文ペイロードインデックスを毎回張る。**
    // 🔴 `CollectionExistsAsync` で `continue` すると**既に在るコレクションにだけ索引が付かない**——
    // それが #1116 の欠陥そのものである（新規作成の経路しか無ければ、稼働中の配備は永久に索引を持たない）。
    // Qdrant の `CreatePayloadIndex` は冪等であり、パラメータが違えば張り替える（実機 v1.18.1 で実測。
    // [[IADR-0318]] 決定 2）。したがって**起動のたびに無条件で 1 回呼ぶだけで、新規・既存・張り替えが収束する。**
    public async Task EnsureCollectionsAsync(CancellationToken ct = default)
    {
        foreach (var c in _collections)
        {
            if (!await client.CollectionExistsAsync(c.Name, ct))
            {
                await client.CreateCollectionAsync(c.Name,
                    new VectorParams { Size = (ulong)c.VectorSize, Distance = Distance.Cosine },
                    cancellationToken: ct);
            }

            await client.CreatePayloadIndexAsync(c.Name, FullTextKey, PayloadSchemaType.Text,
                BuildFullTextIndexParams(), cancellationToken: ct);
        }
    }

    // FR-03, #1116, [[IADR-0318]] 決定 1: 全文インデックスのパラメータ。
    //
    // **`multilingual` を採る。** 実機 v1.18.1（公式イメージ）で受理されることと、日本語の語中に当たること、
    // 語でない断片（`anpop`）に当たらないこと、語順に依存しないことを実測した。
    // `word` / `whitespace` は日本語がほぼ全滅し、`prefix` は語頭しか当たらず索引も肥大する。
    //
    // 🔴 **索引が無いときの「当たり」は全文検索ではない。** v1.18.1 は例外を投げず**部分文字列の全走査**へ
    // 黙って落ちる（v1.9.2 は例外だった）。版で静かに変わる挙動に FR-03 を預けないための索引である。
    //
    // `MinTokenLen = 1`: 日本語 1 文字の語（例「本」）と 1 文字の識別子を落とさない。
    // `MaxTokenLen = 40`: 長大な識別子・URL 断片で索引が膨らむのを抑える。
    // `Lowercase = true`: 型番・略語の大小文字差を吸収する（`ABAC` / `abac`）。
    //
    // **純関数として切り出してある**——実機 Qdrant なしで宣言値を固定できる唯一の面である
    // （`BuildChunkPayload` と同じ位置づけ）。
    internal static PayloadIndexParams BuildFullTextIndexParams() =>
        new()
        {
            TextIndexParams = new TextIndexParams
            {
                Tokenizer = TokenizerType.Multilingual,
                MinTokenLen = 1,
                MaxTokenLen = 40,
                Lowercase = true,
            }
        };

    // FR-03, #1118, [[IADR-0339]] 決定 1・2: 日本語（CJK）2-gram ペイロード `text_ngram` の全文索引を、
    // 全コレクションへ**存在の有無によらず**張る（`EnsureCollectionsAsync` の `text` と同じ作法）。
    //
    // 🔴 `multilingual` は公式イメージ v1.18.1 では日本語の分かち書きを持たず、語で当たるかは連なりの切れ目次第
    // （稼働 Qdrant で実測: 実配備チャンクの日本語 25 語のうち当たるのは 1 語）。そこで CJK は
    // アプリ側で 2-gram に割って別ペイロードに載せ（`CjkBigramPayload`）、その索引をここで張る。
    // `text` の索引・系統は #1117 のまま変えない（識別子・型番・略語の再現率を落とさない）。
    public async Task EnsureCjkNgramIndexAsync(CancellationToken ct = default)
    {
        foreach (var c in _collections)
        {
            await client.CreatePayloadIndexAsync(c.Name, CjkBigramPayload.PayloadKey, PayloadSchemaType.Text,
                BuildCjkNgramIndexParams(), cancellationToken: ct);
        }
    }

    // FR-03, #1118, [[IADR-0339]] 決定 1: `text_ngram` の索引パラメータ。
    //
    // **`prefix` を採る。** ペイロードは 2 文字トークンの列なので、`prefix` は各トークンの
    // 1 文字接頭辞も索引に入れる。これで **1 文字の語（「本」）も当たる**（実測: whitespace / word では
    // 1 文字が 0 件、prefix では当たる）。`MaxTokenLen = 2` は 2-gram より長いトークンを索引に入れない
    // （入るとしたら符号化の欠陥であり、索引で黙って受けない）。
    internal static PayloadIndexParams BuildCjkNgramIndexParams() =>
        new()
        {
            TextIndexParams = new TextIndexParams
            {
                Tokenizer = TokenizerType.Prefix,
                MinTokenLen = 1,
                MaxTokenLen = 2,
                Lowercase = true,
            }
        };

    // 1 回の scroll で埋める点の数。索引の後付けは起動後のバックグラウンドで走り、
    // 1 ページごとに `UpdateBatch`（SetPayload × 点数）を 1 回出す。
    internal const uint BackfillPageSize = 256;

    // FR-03, #1118, [[IADR-0339]] 決定 2: `text_ngram` を持たない点だけを scroll し、`text` から
    // 2-gram を作って後付けする。
    //
    // 🔴 **移行スクリプトにしない**（[[IADR-0318]] が索引の後付けで退けた案 2 と同じ理由。呼び忘れが
    // 起き、実行されたかを誰も見ない）。起動のたびに「無い点だけ」を埋めるので、**2 回目以降は
    // 0 件走査で終わる**（埋めた点は `is_empty` に当たらない）。再取り込み（DocumentUpdated の再発行）は要らない。
    //
    // `text` が無い点にも空文字列を書く —— Qdrant の `is_empty` は空文字列を「空」と見ないので、
    // 同じ点を毎回拾い直すことはない。
    public async Task<int> BackfillCjkNgramAsync(CancellationToken ct = default)
    {
        var filled = 0;
        foreach (var c in _collections)
        {
            PointId? previousFirst = null;
            while (!ct.IsCancellationRequested)
            {
                var page = await client.ScrollAsync(c.Name,
                    filter: BuildMissingCjkNgramFilter(),
                    limit: BackfillPageSize,
                    payloadSelector: new WithPayloadSelector
                    {
                        Include = new PayloadIncludeSelector { Fields = { FullTextKey } }
                    },
                    vectorsSelector: new WithVectorsSelector { Enable = false },
                    cancellationToken: ct);

                if (page.Result.Count == 0)
                    break;

                // 🔴 同じ先頭の点が続けて返ったら止める。SetPayload が効いていないのに回り続けると
                //    無限ループになる（wait=true でも保証を疑って、進んでいないことを自分で見る）。
                if (previousFirst is not null && previousFirst.Equals(page.Result[0].Id))
                    throw new InvalidOperationException(
                        $"Backfill of {CjkBigramPayload.PayloadKey} on {c.Name} is not making progress "
                        + $"(point {page.Result[0].Id} was returned twice)");
                previousFirst = page.Result[0].Id;

                var operations = page.Result
                    .Select(p => BuildSetCjkNgramOperation(p.Id,
                        p.Payload.TryGetValue(FullTextKey, out var text) ? text.StringValue : ""))
                    .ToList();
                await client.UpdateBatchAsync(c.Name, operations, cancellationToken: ct);
                filled += operations.Count;
            }
        }

        return filled;
    }

    // `text_ngram` を持たない点だけを選ぶフィルタ（純関数。試験が形を固定する）。
    internal static Filter BuildMissingCjkNgramFilter() =>
        new()
        {
            Must =
            {
                new Condition
                {
                    IsEmpty = new IsEmptyCondition { Key = CjkBigramPayload.PayloadKey }
                }
            }
        };

    // 1 点に `text_ngram` を書く SetPayload 操作（純関数）。
    internal static PointsUpdateOperation BuildSetCjkNgramOperation(PointId id, string text) =>
        new()
        {
            SetPayload = new PointsUpdateOperation.Types.SetPayload
            {
                Payload =
                {
                    [CjkBigramPayload.PayloadKey] = new Value { StringValue = CjkBigramPayload.Encode(text) }
                },
                PointsSelector = new PointsSelector { Points = new PointsIdsList { Ids = { id } } },
            }
        };

    public async Task UpsertChunkAsync(string collection, Guid chunkId, Guid documentId, string title,
        string text, int chunkIndex, float[] vector, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        DateTimeOffset? updatedAt = null,
        List<string>? sharedWith = null,
        CancellationToken ct = default)
    {
        var payload = BuildChunkPayload(documentId, title, text, chunkIndex, markdownUri, attributes,
            tags, updatedAt, sharedWith: sharedWith);

        await client.UpsertAsync(collection,
            [new PointStruct { Id = new PointId { Uuid = chunkId.ToString() }, Vectors = vector, Payload = { payload } }],
            cancellationToken: ct);
    }

    // FR-02, FR-03, SC-02, ADR-0070 決定 4, #1193, [[IADR-0358]] 決定 1・2:
    // 本文なしの文書のメタデータ点を索引する。**チャンクと同じコレクション・同じペイロード表現**で、
    // 違うのは `has_body = false` と、`text` に入るのが本文ではなく索引テキストであることだけである。
    //
    // 同じ表現にしてあるので、ABAC フィルタ（`attributes`）・削除（`document_id`）・
    // 並び順（`updated_at`）・全文索引（`text` / `text_ngram`）は**1 行も書き足さずにそのまま効く。**
    public async Task UpsertMetadataPointAsync(string collection, Guid pointId, Guid documentId,
        string title, string indexText, float[] vector, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        DateTimeOffset? updatedAt = null,
        List<string>? sharedWith = null,
        CancellationToken ct = default)
    {
        var payload = BuildChunkPayload(documentId, title, indexText, ChunkId.MetadataChunkIndex,
            markdownUri, attributes, tags, updatedAt, hasBody: false, sharedWith: sharedWith);

        await client.UpsertAsync(collection,
            [new PointStruct { Id = new PointId { Uuid = pointId.ToString() }, Vectors = vector, Payload = { payload } }],
            cancellationToken: ct);
    }

    // FR-02, FR-05: チャンクの Qdrant ペイロードを構築する。
    // IADR-0014（選択肢C・実機検証済み・Issue #71）: ABAC 属性はネスト構造体 `attributes -> { k: v }`
    // へ統一する。RetrievalService.QdrantVectorStore の書き込み・フィルタ表現と一致させる。
    //
    // FR-02, FR-03, ADR-0070 決定 4, #1193: `hasBody = false` はメタデータ点である
    // （`text` に入るのは本文ではなく索引テキスト）。**`has_body` は本文なしのときだけ書く** ——
    // 既存の点はすべて本文チャンクであり、**キーの欠落が「本文あり」を正しく表す**ので
    // backfill が要らない（[[IADR-0358]] 決定 3。`DocumentBodyPresence.DefaultWhenAbsent`）。
    internal static Dictionary<string, Value> BuildChunkPayload(Guid documentId, string title,
        string text, int chunkIndex, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        DateTimeOffset? updatedAt = null,
        bool hasBody = true,
        List<string>? sharedWith = null)
    {
        var payload = new Dictionary<string, Value>
        {
            ["document_id"] = new Value { StringValue = documentId.ToString() },
            ["document_title"] = new Value { StringValue = title },
            // FR-03, #1116: 全文インデックスを張るキーと同じ 1 つの値を使う（書き込みと索引を割らない）。
            [FullTextKey] = new Value { StringValue = text },
            // FR-03, #1118, [[IADR-0339]] 決定 1: 日本語（CJK）の 2-gram。同じ本文から、検索側と共有する
            // 変換（`CjkBigramPayload`）で作る。CJK を含まない本文では空文字列（`is_empty` には当たらない）。
            [CjkBigramPayload.PayloadKey] = new Value { StringValue = CjkBigramPayload.Encode(text) },
            ["markdown_uri"] = new Value { StringValue = markdownUri ?? "" },
            // FR-02: チャンクの並び順・出典の一部として保持
            ["chunk_index"] = new Value { IntegerValue = chunkIndex },
        };

        // FR-02, FR-03, SC-02, #1193: 本文なしの点だけが印を持つ（上の注記のとおり、欠落は「本文あり」）。
        if (!hasBody)
            payload[DocumentBodyPresence.PayloadKey] = new Value { BoolValue = false };

        // FR-03, SC-02, #536: 文書の更新日時（利用者裁定 Q6）。**Unix epoch ミリ秒の整数**で持つ
        // （IADR-0149 決定 1）。ISO-8601 文字列は同じ時刻を `+09:00` とも `Z` とも書けるため、
        // 文字列のまま並べると辞書順が実時刻順と一致しない（並び順は #532 が使う）。
        // 検索側（RetrievalService.QdrantVectorStore）の書き込み・復元と表現を揃えること。
        if (updatedAt is { } at)
            payload["updated_at"] = new Value { IntegerValue = at.ToUnixTimeMilliseconds() };

        // FR-02: タグをペイロードに保持（検索結果の絞り込み・表示用）
        if (tags.Count > 0)
        {
            var tagList = new ListValue();
            foreach (var t in tags)
                tagList.Values.Add(new Value { StringValue = t });
            payload["tags"] = new Value { ListValue = tagList };
        }

        // FR-19, FR-20, ADR-0036 D-06, ADR-0061 決定 5 / [[IADR-0396]] 決定 3 (#1184):
        // 共有先を**リスト項目**として保持する（`tags` と同じ表現・同じ「いずれか一致」の意味論）。
        // 🔴 **属性へ入れない** —— 単一値では集合を表せず、共有先が 1 人しか効かない索引になる。
        // 0 件のときはキー自体を書かない（`tags` / `attributes` と同じ扱い）。
        if (sharedWith is { Count: > 0 })
        {
            var shareList = new ListValue();
            foreach (var subject in sharedWith)
                shareList.Values.Add(new Value { StringValue = subject });
            payload[AttributeValueKeys.SharedWith] = new Value { ListValue = shareList };
        }

        // FR-05: ABAC 属性をペイロードに保持（検索時フィルタ用）。ネスト構造体へ統一する（IADR-0014 選択肢C）。
        if (attributes.Count > 0)
        {
            var attrs = new Struct();
            foreach (var (k, val) in attributes)
                attrs.Fields[k] = new Value { StringValue = val };
            payload["attributes"] = new Value { StructValue = attrs };
        }

        return payload;
    }

    // FR-02, FR-05: 全モデル別コレクションから当該文書のチャンクを削除する（機密区分変更時の残存防止）。
    public async Task DeleteByDocumentFromAllAsync(Guid documentId, CancellationToken ct = default)
    {
        var filter = new Filter
        {
            Must =
            {
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = "document_id",
                        Match = new Match { Keyword = documentId.ToString() }
                    }
                }
            }
        };

        foreach (var c in _collections)
            await client.DeleteAsync(c.Name, filter, cancellationToken: ct);
    }
}
