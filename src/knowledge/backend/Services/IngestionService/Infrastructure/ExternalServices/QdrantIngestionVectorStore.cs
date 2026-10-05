using System.Collections.Concurrent;
using IngestionService.Domain.Ports;
using IngestionService.Domain;
using Knowledge.Contracts.Dtos;
using Knowledge.Contracts.Indexing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace IngestionService.Infrastructure.ExternalServices;

// ADR-0009, ADR-0016: IngestionService から Qdrant へ直接書き込む（モデル別コレクション対応）。
//
// FR-02, FR-03, ADR-0127 決定 1, [[IADR-0497]] 決定 1 (#1746): **語彙索引**（ベクトルを持たない専用のコレクション。
// 名前は `lexicalCollection`。省略時は `LexicalCollection.DefaultName`）も同じ実装が持つ。
// ベクトルのコレクション（`Embedding:Collections`）とは**作り方だけが違い**（ベクトルの設定が空）、
// 全文索引・`text_ngram` の後付け・文書単位の削除は全コレクションに同じく効く。
//
// FR-04, FR-05, SC-01, SC-08, [[IADR-0502]] (#1760): facet と ABAC フィルタが引くキー（`tags`・`shared_with`・
// `attributes.<key>`）の**キーワード索引**も同じ実装が張る（起動時・書き込み時・既存の点からの発見の 3 か所）。
public class QdrantIngestionVectorStore(
    QdrantClient client, IOptions<EmbeddingCollectionsOptions> collections,
    string? lexicalCollection = null,
    ILogger<QdrantIngestionVectorStore>? logger = null)
    : IIngestionVectorStore
{
    private readonly IReadOnlyList<EmbeddingCollectionOptions> _collections = collections.Value.Collections;

    private readonly ILogger _logger = logger ?? NullLogger<QdrantIngestionVectorStore>.Instance;

    // [[IADR-0502]] 決定 2: キーワード索引を張り終えた（コレクション, ペイロードキー）。**プロセス内だけ**の記憶であり、
    // 再起動すれば空から始まる（起動時と発見の走査が張り直す。`CreatePayloadIndex` は冪等）。
    // 書き込みのたびに同じキーへ RPC を出さないためのものである。
    private readonly ConcurrentDictionary<(string Collection, string Key), byte> _keywordIndexed = new();

    // [[IADR-0497]] 決定 1: 語彙索引のコレクション名。
    private readonly string _lexical = string.IsNullOrWhiteSpace(lexicalCollection)
        ? LexicalCollection.DefaultName
        : lexicalCollection.Trim();

    // 全文索引・後付け・削除が回るコレクション（ベクトルのコレクション ＋ 語彙索引）。
    // 🔴 **語彙索引を最後に置く**（順序に意味は無いが、既存のコレクションへの呼び出し順を変えない）。
    private IEnumerable<string> AllCollectionNames =>
        _collections.Select(c => c.Name).Append(_lexical);

    // 語彙索引のコレクション名（試験・合成点が読む）。
    internal string LexicalCollectionName => _lexical;

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

        // FR-02, FR-03, ADR-0127 決定 1, [[IADR-0497]] 決定 1 (#1746): 語彙索引は**ベクトルの設定が空の
        // コレクション**として作る（`VectorParamsMap` が空 ＝ 名前つきベクトルを 1 つも持たない）。
        // 実機 v1.18.1 / v1.13.4 で、作成・全文索引・ベクトル無しの点の書き込み・全文の scroll・文書単位の削除が
        // 通り、ベクトル検索は「ベクトルが無い」で拒まれることを実測した（作業仕様書 §実測）。
        // 既存のコレクションへの全文索引は上と同じ作法（存在の有無によらず毎回張る。冪等）。
        if (!await client.CollectionExistsAsync(_lexical, ct))
            await client.CreateCollectionAsync(_lexical, new VectorParamsMap(), cancellationToken: ct);

        await client.CreatePayloadIndexAsync(_lexical, FullTextKey, PayloadSchemaType.Text,
            BuildFullTextIndexParams(), cancellationToken: ct);
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
        // ［2026-10-05 / #1746］[[IADR-0497]] 決定 1: 語彙索引にも張る（日本語の語は `text_ngram` でしか当たらない）。
        foreach (var name in AllCollectionNames)
        {
            await client.CreatePayloadIndexAsync(name, CjkBigramPayload.PayloadKey, PayloadSchemaType.Text,
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
        // ［2026-10-05 / #1746］[[IADR-0497]] 決定 1: 語彙索引も対象にする（書き込みは最初から `text_ngram` を
        // 書くので通常は 0 件。対象から外すと、手で投入した点や将来のペイロード変更で穴が開く）。
        foreach (var name in AllCollectionNames)
        {
            PointId? previousFirst = null;
            while (!ct.IsCancellationRequested)
            {
                var page = await client.ScrollAsync(name,
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
                        $"Backfill of {CjkBigramPayload.PayloadKey} on {name} is not making progress "
                        + $"(point {page.Result[0].Id} was returned twice)");
                previousFirst = page.Result[0].Id;

                var operations = page.Result
                    .Select(p => BuildSetCjkNgramOperation(p.Id,
                        p.Payload.TryGetValue(FullTextKey, out var text) ? text.StringValue : ""))
                    .ToList();
                await client.UpdateBatchAsync(name, operations, cancellationToken: ct);
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

    // FR-04, FR-05, SC-01, SC-08, [[IADR-0502]] 決定 4 (#1760): キーワード索引のパラメータ。
    //
    // **`is_tenant` / `on_disk` は既定（偽）のまま。** 属性は全問い合わせに付くテナントの区切りではなく
    // （`is_tenant` は 1 つのキーで点を分割する最適化）、規模（NFR-08 の数十万点）はメモリ上の索引で足りる。
    // 宣言を空のまま明示して置くのは、ここが変わったことを試験で捕まえるためである（純関数）。
    internal static PayloadIndexParams BuildKeywordIndexParams() =>
        new() { KeywordIndexParams = new KeywordIndexParams() };

    // FR-04, FR-05, SC-01, SC-08, [[IADR-0502]] 決定 2 (i) (#1760): **集合値キー（`tags`・`shared_with`）の
    // キーワード索引を、全コレクションへ存在の有無によらず張る**（`EnsureCollectionsAsync` の `text` と同じ作法）。
    // 属性キー（`attributes.<key>`）は動的なので、書き込み時（下の `EnsureAttributeKeywordIndexesAsync`）と
    // 既存の点からの発見（`EnsureKeywordIndexesForExistingPointsAsync`）が張る。
    //
    // 🔴 失敗は呼び出し元（起動時のブートストラップ）へ上げる —— 全文索引と同じく Error で残る。
    public async Task EnsureKeywordIndexesAsync(CancellationToken ct = default)
    {
        var keys = AttributeValueKeys.KeywordIndexKeys([]);
        foreach (var name in AllCollectionNames)
        {
            foreach (var key in keys)
            {
                await client.CreatePayloadIndexAsync(name, key, PayloadSchemaType.Keyword,
                    BuildKeywordIndexParams(), cancellationToken: ct);
                _keywordIndexed.TryAdd((name, key), 0);
            }
        }
    }

    // FR-04, FR-05, [[IADR-0502]] 決定 2 (ii) (#1760): 書き込む点の属性キー（と集合値キー）のうち、
    // このプロセスでまだ張っていないものだけにキーワード索引を張る。張った数を返す。
    //
    // 🔴 **書き込みを止めない。** 索引の失敗で取り込みを再試行へ落とすと、facet の候補のために本文の索引まで止まる。
    //   - `wait: false`: 索引の構築を取り込みの期限（`IngestionTimeouts`）に入れない（受け付けだけを待つ）。
    //   - JSON パスとして不正なキー（`InvalidArgument`。実測 m-9）は覚えて二度と呼ばない —— そのキーは facet もできない。
    //   - それ以外の失敗（Qdrant の不調・コレクションが無い）は覚えず、次の書き込みで張り直す。Warning を残す。
    //   - 呼び出し元の取り消しは上げる（書き込みそのものも取り消される）。
    internal async Task<int> EnsureAttributeKeywordIndexesAsync(
        string collection, IEnumerable<string> attributeKeys, CancellationToken ct)
    {
        var created = 0;
        foreach (var key in AttributeValueKeys.KeywordIndexKeys(attributeKeys))
        {
            if (_keywordIndexed.ContainsKey((collection, key)))
                continue;
            try
            {
                await client.CreatePayloadIndexAsync(collection, key, PayloadSchemaType.Keyword,
                    BuildKeywordIndexParams(), wait: false, cancellationToken: ct);
                _keywordIndexed.TryAdd((collection, key), 0);
                created++;
            }
            catch (Grpc.Core.RpcException ex) when (!ct.IsCancellationRequested)
            {
                if (ex.StatusCode == Grpc.Core.StatusCode.InvalidArgument)
                    _keywordIndexed.TryAdd((collection, key), 0);
                _logger.LogWarning(ex,
                    "Failed to ensure Qdrant keyword payload index {Key} on {Collection}; "
                    + "scoped attribute values (facet) for this key return no candidates from this collection "
                    + "until the index exists", key, collection);
            }
        }

        return created;
    }

    // 既存の点からキーを拾う 1 回の scroll の点の数（`attributes` だけを読むので軽い）。
    internal const uint KeyDiscoveryPageSize = 1024;

    // FR-04, FR-05, [[IADR-0502]] 決定 3 (#1760): **既存の点に現れる属性キーを拾い、キーワード索引を張る。**
    // 張った数を返す。
    //
    // 書き込み時の付与（上）は「このプロセスが書いたキー」にしか効かない。稼働中の配備には、再起動後に
    // 一度も書かれていないキーを持つ点が残る —— その軸の facet は索引が無いまま空集合になる。
    // 起動後のバックグラウンドで全コレクションの点を `attributes` だけ読んで走査し、書き込み時と同じ経路で張る。
    //
    // 🔴 **全点を読む**（標本にしない）。標本では少数の文書にだけ付いたキーを取りこぼし、
    // 「その値を持つ文書は在るのに候補に出ない」形が残る。費用は IADR-0502 §結果。
    //
    // 🔴 **1 つのコレクションの走査の失敗で、他のコレクションの発見を止めない**（[[IADR-0502]] 決定 3。独立監査 🟡3）。
    // 走査中の `NotFound`（コレクションが無い）・`Unavailable`（Qdrant の不調）はそのコレクションだけを諦めて
    // Warning を残し、次のコレクションへ進む（そのコレクションの既存キーは、次の書き込みか次の起動で張られる）。
    // ログにはコレクション名と状態コードだけを出す（ペイロードの値は出さない）。呼び出し元の取り消しは上げる。
    public async Task<int> EnsureKeywordIndexesForExistingPointsAsync(CancellationToken ct = default)
    {
        var created = 0;
        foreach (var name in AllCollectionNames)
        {
            HashSet<string> keys;
            try
            {
                keys = await ScanAttributeKeysAsync(name, ct);
            }
            catch (Grpc.Core.RpcException ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Failed to scan Qdrant collection {Collection} for attribute keys ({StatusCode}); "
                    + "keyword payload indexes for its pre-existing attribute keys are not ensured until "
                    + "they are written again or the next start, continuing with the next collection",
                    name, ex.StatusCode);
                continue;
            }

            ct.ThrowIfCancellationRequested();
            created += await EnsureAttributeKeywordIndexesAsync(name, keys, ct);
        }

        return created;
    }

    // [[IADR-0502]] 決定 3: 1 つのコレクションの全点を `attributes` だけ読んで走査し、属性キーを集める。
    // 🔴 次のページは**前の応答の `NextPageOffset` から**読む（ここを落とすと先頭ページを読み続けて終わらない）。
    private async Task<HashSet<string>> ScanAttributeKeysAsync(string name, CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        PointId? offset = null;
        do
        {
            var page = await client.ScrollAsync(name,
                limit: KeyDiscoveryPageSize,
                offset: offset,
                payloadSelector: new WithPayloadSelector
                {
                    Include = new PayloadIncludeSelector { Fields = { AttributeValueKeys.AttributesPrefix } }
                },
                vectorsSelector: new WithVectorsSelector { Enable = false },
                cancellationToken: ct);

            foreach (var point in page.Result)
                keys.UnionWith(ReadAttributeKeys(point.Payload));

            offset = page.NextPageOffset;
        }
        while (offset is not null && !ct.IsCancellationRequested);

        return keys;
    }

    // 点のペイロードから属性キー（ネスト構造体 `attributes -> {k: v}` のキー）を読む（純関数。[[IADR-0014]]）。
    internal static IEnumerable<string> ReadAttributeKeys(IDictionary<string, Value> payload) =>
        payload.TryGetValue(AttributeValueKeys.AttributesPrefix, out var attrs)
        && attrs.KindCase == Value.KindOneofCase.StructValue
            ? attrs.StructValue.Fields.Keys
            : [];

    public async Task UpsertChunkAsync(string collection, Guid chunkId, Guid documentId, string title,
        string text, int chunkIndex, float[] vector, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        DateTimeOffset? updatedAt = null,
        List<string>? sharedWith = null,
        CancellationToken ct = default)
    {
        var payload = BuildChunkPayload(documentId, title, text, chunkIndex, markdownUri, attributes,
            tags, updatedAt, sharedWith: sharedWith);
        // [[IADR-0502]] 決定 2 (ii) (#1760): 書く点の属性キーへキーワード索引を張る（張り済みなら RPC を出さない）。
        await EnsureAttributeKeywordIndexesAsync(collection, attributes.Keys, ct);

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
        // [[IADR-0502]] 決定 2 (ii) (#1760): チャンクの口と同じ。
        await EnsureAttributeKeywordIndexesAsync(collection, attributes.Keys, ct);

        await client.UpsertAsync(collection,
            [new PointStruct { Id = new PointId { Uuid = pointId.ToString() }, Vectors = vector, Payload = { payload } }],
            cancellationToken: ct);
    }

    // FR-02, FR-03, FR-05, ADR-0127 決定 1, [[IADR-0497]] 決定 1・3 (#1746): 高機密文書のチャンクを語彙索引へ書く。
    // **ペイロードはチャンクの口と同じ関数（`BuildChunkPayload`）で作る** —— ABAC（`attributes`・`shared_with`）・
    // 削除（`document_id`）・並び順（`updated_at`）・全文（`text` / `text_ngram`）の表現を、埋め込みの有無で割らない。
    // 違うのは**ベクトルが空**（名前つきベクトルを 1 つも持たない）ことと、書き先が語彙索引に固定であることだけである。
    public async Task UpsertLexicalChunkAsync(Guid chunkId, Guid documentId, string title,
        string text, int chunkIndex, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        DateTimeOffset? updatedAt = null,
        List<string>? sharedWith = null,
        CancellationToken ct = default)
    {
        var payload = BuildChunkPayload(documentId, title, text, chunkIndex, markdownUri, attributes,
            tags, updatedAt, sharedWith: sharedWith);
        // [[IADR-0502]] 決定 2 (ii) (#1760): 語彙索引にも張る（検索は語彙索引でも facet する）。
        await EnsureAttributeKeywordIndexesAsync(_lexical, attributes.Keys, ct);

        await client.UpsertAsync(_lexical, [BuildLexicalPoint(chunkId, payload)], cancellationToken: ct);
    }

    // FR-02, FR-03, ADR-0070 決定 4, ADR-0127 決定 1, [[IADR-0497]] 決定 3 (#1746): 本文なしの高機密文書の
    // メタデータ点を語彙索引へ書く（`has_body = false`。メタデータの口と同じペイロード）。
    public async Task UpsertLexicalMetadataPointAsync(Guid pointId, Guid documentId, string title,
        string indexText, string? markdownUri,
        Dictionary<string, string> attributes, List<string> tags,
        DateTimeOffset? updatedAt = null,
        List<string>? sharedWith = null,
        CancellationToken ct = default)
    {
        var payload = BuildChunkPayload(documentId, title, indexText, ChunkId.MetadataChunkIndex,
            markdownUri, attributes, tags, updatedAt, hasBody: false, sharedWith: sharedWith);
        // [[IADR-0502]] 決定 2 (ii) (#1760): 語彙索引のチャンクの口と同じ。
        await EnsureAttributeKeywordIndexesAsync(_lexical, attributes.Keys, ct);

        await client.UpsertAsync(_lexical, [BuildLexicalPoint(pointId, payload)], cancellationToken: ct);
    }

    // [[IADR-0497]] 決定 1: **ベクトルを持たない点**（純関数。試験が形を固定する）。
    // 🔴 `Vectors` を未設定にすると Qdrant は「Expected some vectors」で拒む（実測）。**空の名前つきベクトル**
    // （`NamedVectors` が 0 件）を明示して「ベクトルは無い」を書く。零ベクトル・ハッシュ埋め込みは入れない
    // （ADR-0127 決定 1。ベクトルの系統の順位を汚す）。
    internal static PointStruct BuildLexicalPoint(Guid pointId, Dictionary<string, Value> payload) =>
        new()
        {
            Id = new PointId { Uuid = pointId.ToString() },
            Vectors = new Vectors { Vectors_ = new NamedVectors() },
            Payload = { payload },
        };

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

        // ［2026-10-05 / #1746］[[IADR-0497]] 決定 4: **語彙索引からも消す。** 機密区分が下がった文書
        // （confidential → public）の語彙索引の点が残ると、意味検索に出ない古い本文がキーワードで当たり続ける。
        foreach (var c in _collections)
            await client.DeleteAsync(c.Name, filter, cancellationToken: ct);

        // #1746 監査 🟡3, [[IADR-0497]] 決定 4: 語彙索引は起動時のブートストラップが作る。ブートストラップが失敗して
        // まだ無いときは、消す点も無いので `NotFound` だけを no-op にする（取り込み全体を再試行へ落とさない）。
        // ベクトルのコレクションの削除は従来どおり（例外を上げる）。
        try
        {
            await client.DeleteAsync(_lexical, filter, cancellationToken: ct);
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound
                                                && !ct.IsCancellationRequested)
        {
        }
    }
}
