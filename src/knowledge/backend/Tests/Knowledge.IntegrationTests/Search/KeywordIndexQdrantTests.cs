using AwesomeAssertions;
using Grpc.Core;
using IngestionService.Domain.Ports;
using IngestionService.Infrastructure.ExternalServices;
using Knowledge.Contracts.Dtos;
using Knowledge.IntegrationTests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Shared.Contracts.Dtos;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RetrievalService.Common.Observability;
using RetrievalService.Domain.Ports;
using RetrievalService.Infrastructure.ExternalServices;
using Testcontainers.Qdrant;

namespace Knowledge.IntegrationTests.Search;

// FR-04, FR-05, SC-01, SC-08, [[IADR-0502]] 決定 1〜5 (#1760):
// **権限内属性値の facet と ABAC フィルタが引くキーのキーワード索引を、実 Qdrant で通す。**
//
// 単体試験（gRPC の記録）では測れないものだけを測る:
//   - 本番の取り込みアダプタが張った索引で、本番の検索アダプタの facet（`ListAttributeValuesAsync`）が
//     ベクトルのコレクション（主・ティア A の束ねるコレクション）と語彙索引の**全部で**例外にならないこと
//   - 陰性対照: 索引の無いコレクションでは、生の facet が `No appropriate index for faceting` で失敗すること
//     （＝索引が要る根拠。その失敗を検索アダプタは空集合へ倒す）
//   - 既存の配備: 索引を張る前の版が書いた点の属性キーを、発見の走査が拾って索引を張ること
//   - 索引を張っても ABAC フィルタの意味（完全一致・大小文字の区別）が変わらないこと
//
// 🔴 **Qdrant の版は配備と同じ v1.18.1 に固定する**（`LexicalIndexQdrantTests` と同じ理由。置き場所は `QdrantTestImage`。#1790）。
// `Category=Integration` を付ける（実コンテナを起こす。`integration.yml` が回収する）。
[Trait("Category", "Integration")]
[Trait("TestKind", "Integration")]
public sealed class KeywordIndexQdrantTests : IAsyncLifetime
{
    private const int Dimensions = 8;

    // 1 回の試験ごとに別の名前（外部の Qdrant を共有しても混ざらない）。
    private readonly string _primary = $"kw_it_primary_{Guid.NewGuid():N}";
    private readonly string _fused = $"kw_it_fused_{Guid.NewGuid():N}";
    private readonly string _lexical = $"kw_it_lexical_{Guid.NewGuid():N}";

    private QdrantContainer? _qdrant;
    private QdrantClient? _client;

    public async ValueTask InitializeAsync()
    {
        var external = RequiredServices.Qdrant.External;
        if (external is not null)
        {
            var parts = external.Split(':', 2);
            _client = new QdrantClient(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 6334);
            return;
        }

        if (!DockerRequired.IsAvailable()) return;

        _qdrant = QdrantTestImage.CreateBuilder().Build();
        await _qdrant.StartAsync();

        var uri = new Uri(_qdrant.GetGrpcConnectionString());
        _client = new QdrantClient(uri.Host, uri.Port, https: uri.Scheme == Uri.UriSchemeHttps);
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_qdrant is not null) await _qdrant.DisposeAsync();
    }

    // I-11: 主・ティア A の束ねるコレクション・語彙索引の全部で、facet（`tags`・`shared_with`・`attributes.<key>`）が通る。
    [Fact]
    public async Task Facet_Succeeds_OnEveryCollectionKind_AfterIngestion()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Qdrant);
        var ct = TestContext.Current.CancellationToken;
        var writer = await NewWriterAsync(ct);

        var attributes = new Dictionary<string, string>
        {
            ["department"] = "HR",
            ["project"] = "alpha",
            ["confidentiality"] = "internal",
        };
        await writer.UpsertChunkAsync(_primary, Guid.NewGuid(), Guid.NewGuid(), "主の文書", "本文", 0,
            Vector(), null, new(attributes), ["人事"], sharedWith: ["bob"], ct: ct);
        await writer.UpsertMetadataPointAsync(_fused, Guid.NewGuid(), Guid.NewGuid(), "束ねる側の文書", "題名",
            Vector(), null, new(attributes), ["人事"], sharedWith: ["bob"], ct: ct);
        await writer.UpsertLexicalChunkAsync(Guid.NewGuid(), Guid.NewGuid(), "高機密の文書", "本文", 0, null,
            new(attributes) { ["confidentiality"] = "restricted" }, ["人事"], sharedWith: ["bob"], ct: ct);

        foreach (var collection in new[] { _primary, _fused, _lexical })
        {
            await WaitForKeywordIndexAsync(collection, "attributes.project", ct);
            var reader = NewReader(collection);
            (await reader.ListAttributeValuesAsync(AttributeValueKeys.ToPayloadKey("tags"), null, ct))
                .Should().Equal(["人事"], $"{collection}: tags");
            (await reader.ListAttributeValuesAsync(AttributeValueKeys.ToPayloadKey("shared_with"), null, ct))
                .Should().Equal(["bob"], $"{collection}: shared_with");
            (await reader.ListAttributeValuesAsync(AttributeValueKeys.ToPayloadKey("department"), null, ct))
                .Should().Equal(["HR"], $"{collection}: attributes.department");
            (await reader.ListAttributeValuesAsync(AttributeValueKeys.ToPayloadKey("project"), null, ct))
                .Should().Equal(["alpha"], $"{collection}: attributes.project");

            // 生の facet でも通る（検索アダプタの「索引が無ければ空集合」に救われていないこと）。
            var raw = await _client!.FacetAsync(collection, "attributes.department", cancellationToken: ct);
            raw.Hits.Select(h => h.Value.StringValue).Should().Equal(["HR"]);
        }

        var schema = (await _client!.GetCollectionInfoAsync(_primary, ct)).PayloadSchema;
        foreach (var key in AttributeValueKeys.KeywordIndexKeys(attributes.Keys))
            schema.Should().ContainKey(key).WhoseValue.DataType.Should().Be(PayloadSchemaType.Keyword, key);
    }

    // I-11: 索引を張っても ABAC フィルタの意味は変わらない（完全一致・大小文字の区別）。facet も同じフィルタで絞られる。
    // リスト項目（`tags`）の「いずれか一致」（spec m-4）と、否定条件 `must_not attributes.doc_scope == private-note` が
    // キーを持たない点を残すこと（spec m-5）も、索引を張った後で同じであることを測る（独立監査 🟢）。
    [Fact]
    public async Task AbacFilter_KeepsExactCaseSensitiveMatch_WithKeywordIndex()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Qdrant);
        var ct = TestContext.Current.CancellationToken;
        var writer = await NewWriterAsync(ct);
        var hr = Guid.NewGuid();
        var sales = Guid.NewGuid();
        await writer.UpsertLexicalChunkAsync(Guid.NewGuid(), hr, "人事の文書", "abac-keyword-it 人事", 0, null,
            new() { ["department"] = "HR" }, ["人事"], ct: ct);
        await writer.UpsertLexicalChunkAsync(Guid.NewGuid(), sales, "営業の文書", "abac-keyword-it 営業", 0, null,
            new() { ["department"] = "sales" }, ["営業"], ct: ct);
        await WaitForKeywordIndexAsync(_lexical, "attributes.department", ct);

        var reader = NewReader(_lexical);
        var exact = new ScopeFilter([new AttributeFilter("department", ["HR"])]);
        var otherCase = new ScopeFilter([new AttributeFilter("department", ["hr"])]);

        var hits = await reader.KeywordSearchAsync("abac-keyword-it", 10, exact, ct);
        hits.Should().Contain(r => r.DocumentId == hr).And.NotContain(r => r.DocumentId == sales);
        (await reader.KeywordSearchAsync("abac-keyword-it", 10, otherCase, ct))
            .Should().BeEmpty("索引を張っても大小文字を区別する完全一致のまま（過剰に許可しない）");

        (await reader.ListAttributeValuesAsync("tags", exact, ct))
            .Should().Equal(["人事"], "facet も検索と同じフィルタで絞られる（権限外の値は候補に出ない）");
        (await reader.ListAttributeValuesAsync("tags", otherCase, ct)).Should().BeEmpty();

        // m-4: リスト項目（`tags`）は「いずれか一致」のまま。大小文字も区別する。
        var anyHrTag = new ScopeFilter([new AttributeFilter("tags", ["人事", "経理"])]);
        var anyTag = new ScopeFilter([new AttributeFilter("tags", ["人事", "営業"])]);
        (await reader.KeywordSearchAsync("abac-keyword-it", 10, anyHrTag, ct)).Select(r => r.DocumentId)
            .Should().Contain(hr).And.NotContain(sales);
        (await reader.KeywordSearchAsync("abac-keyword-it", 10, anyTag, ct)).Select(r => r.DocumentId)
            .Should().Contain([hr, sales]);

        // m-5: 裁量でない分岐は `must_not attributes.doc_scope == private-note` を付ける。索引を張った後も、
        // `doc_scope` を持たない点（既存の組織文書）は残り、個人資料だけが落ちる（否定形が欠落を落とさない）。
        var note = Guid.NewGuid();
        await writer.UpsertLexicalChunkAsync(Guid.NewGuid(), note, "個人の文書", "abac-keyword-it 個人", 0, null,
            new() { ["department"] = "HR", [DocumentScopes.Key] = DocumentScopes.PrivateNote }, ["人事"], ct: ct);
        await WaitForKeywordIndexAsync(_lexical, AttributeValueKeys.ToPayloadKey(DocumentScopes.Key), ct);
        var organizational = new ScopeFilter([], [[new AttributeFilter("department", ["HR", "sales"])]]);
        var visible = (await reader.KeywordSearchAsync("abac-keyword-it", 10, organizational, ct))
            .Select(r => r.DocumentId).ToList();
        visible.Should().Contain([hr, sales], "doc_scope を持たない点は否定条件で落ちない")
            .And.NotContain(note, "個人資料は裁量でない分岐では許可されない");
    }

    // I-12（陰性対照）: 索引の無いコレクションでは生の facet が `No appropriate index for faceting` で失敗する。
    // 検索アダプタはその失敗だけを空集合へ倒す（[[IADR-0502]] 決定 5）。
    [Fact]
    public async Task Facet_Fails_WithoutKeywordIndex_AndReaderReturnsEmpty()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Qdrant);
        var ct = TestContext.Current.CancellationToken;
        var bare = $"kw_it_bare_{Guid.NewGuid():N}";
        await _client!.CreateCollectionAsync(bare,
            new VectorParams { Size = Dimensions, Distance = Distance.Cosine }, cancellationToken: ct);
        await _client.UpsertAsync(bare, [RawPoint(new() { ["department"] = "HR" }, ["人事"])], cancellationToken: ct);

        var act = () => _client.FacetAsync(bare, "attributes.department", cancellationToken: ct);
        var failure = (await act.Should().ThrowAsync<RpcException>()).Which;
        failure.StatusCode.Should().Be(StatusCode.InvalidArgument);
        failure.Status.Detail.Should().Contain(QdrantVectorStoreMissingIndexMessage);

        (await NewReader(bare).ListAttributeValuesAsync("attributes.department", null, ct)).Should().BeEmpty();
        (await NewReader(bare).ListAttributeValuesAsync("tags", null, ct)).Should().BeEmpty();
    }

    // I-13: 既存の配備 —— 索引を張る前の版が書いた点（取り込みアダプタを通らない）のキーを、発見の走査が拾って張る。
    // 再起動後に一度も書かれていないキーでも facet が通る。JSON パスにならないキーは張れないが、走査は止まらない。
    [Fact]
    public async Task Discovery_IndexesAttributeKeys_OfPointsWrittenBeforeThisVersion()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Qdrant);
        var ct = TestContext.Current.CancellationToken;
        await NewWriterAsync(ct);   // コレクションを作る（集合値キーの索引だけが張られる）
        await _client!.UpsertAsync(_primary,
            [RawPoint(new() { ["project"] = "legacy", ["my key"] = "x" }, [])], cancellationToken: ct);

        var before = () => _client.FacetAsync(_primary, "attributes.project", cancellationToken: ct);
        await before.Should().ThrowAsync<RpcException>("陽性対照: 発見の前は索引が無い");

        // 再起動を模す（プロセス内の記憶が空の新しいアダプタ）。
        var restarted = NewWriter();
        var created = await restarted.EnsureKeywordIndexesForExistingPointsAsync(ct);

        created.Should().BeGreaterThan(0);
        await WaitForKeywordIndexAsync(_primary, "attributes.project", ct);
        (await NewReader(_primary).ListAttributeValuesAsync("attributes.project", null, ct))
            .Should().Equal(["legacy"]);
        (await _client.GetCollectionInfoAsync(_primary, ct)).PayloadSchema
            .Should().NotContainKey("attributes.my key", "JSON パスにならないキーは索引にできない（実測 m-9）");
    }

    // I-13: JSON パスにならない属性キーを持つ文書も書ける（索引の失敗で取り込みを止めない）。
    [Fact]
    public async Task Upsert_Succeeds_EvenWhenAnAttributeKeyCannotBeIndexed()
    {
        RequiredServices.SkipUnlessObtainable(RequiredServices.Qdrant);
        var ct = TestContext.Current.CancellationToken;
        var writer = await NewWriterAsync(ct);
        var documentId = Guid.NewGuid();

        await writer.UpsertLexicalChunkAsync(Guid.NewGuid(), documentId, "文書", "invalid-key-it", 0, null,
            new() { ["my key"] = "x", ["department"] = "HR" }, [], ct: ct);

        (await NewReader(_lexical).KeywordSearchAsync("invalid-key-it", 10, ScopeFilter.Empty, ct))
            .Should().Contain(r => r.DocumentId == documentId);
    }

    // ── 器 ────────────────────────────────────────────────

    // 検索アダプタの判定と同じ文言（`QdrantVectorStore.MissingFacetIndexMessage` は internal なので値を写す）。
    private const string QdrantVectorStoreMissingIndexMessage = "No appropriate index for faceting";

    private QdrantIngestionVectorStore NewWriter() =>
        new(_client!, Options.Create(new EmbeddingCollectionsOptions
        {
            Collections =
            [
                new EmbeddingCollectionOptions { Name = _primary, VectorSize = Dimensions },
                new EmbeddingCollectionOptions { Name = _fused, VectorSize = Dimensions },
            ]
        }), _lexical, NullLogger<QdrantIngestionVectorStore>.Instance);

    // 起動時のブートストラップと同じ順に呼ぶ。
    private async Task<QdrantIngestionVectorStore> NewWriterAsync(CancellationToken ct)
    {
        var writer = NewWriter();
        await writer.EnsureCollectionsAsync(ct);
        await writer.EnsureCjkNgramIndexAsync(ct);
        await writer.EnsureKeywordIndexesAsync(ct);
        return writer;
    }

    // 書き込み時・発見の索引は `wait=false`（受け付けだけを待つ）なので、`payload_schema` に現れるまで待つ。
    private async Task WaitForKeywordIndexAsync(string collection, string key, CancellationToken ct)
    {
        for (var i = 0; i < 50; i++)
        {
            var schema = (await _client!.GetCollectionInfoAsync(collection, ct)).PayloadSchema;
            if (schema.TryGetValue(key, out var info) && info.DataType == PayloadSchemaType.Keyword)
                return;
            await Task.Delay(100, ct);
        }

        throw new TimeoutException($"keyword index {key} did not appear on {collection}");
    }

    private QdrantVectorStore NewReader(string collection) =>
        new(_client!,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Qdrant:CollectionName"] = collection
            }).Build(),
            NullLogger<QdrantVectorStore>.Instance,
            new KeywordSearchMetrics(new QdrantTestMeterFactory()));

    private static float[] Vector() => DeterministicEmbeddingService.Vectorize("keyword-index-it");

    // 索引を張る前の版が書いた点（取り込みアダプタを通さない。ペイロードの形は同じ）。
    private static PointStruct RawPoint(Dictionary<string, string> attributes, List<string> tags) =>
        new()
        {
            Id = new PointId { Uuid = Guid.NewGuid().ToString() },
            Vectors = Vector(),
            Payload = { LegacyPayload(attributes, tags) },
        };

    // 取り込みアダプタと同じ表現（`attributes -> {k: v}`・`tags` のリスト項目。[[IADR-0014]]）を手で組む。
    private static Dictionary<string, Value> LegacyPayload(Dictionary<string, string> attributes, List<string> tags)
    {
        var payload = new Dictionary<string, Value>
        {
            ["document_id"] = new Value { StringValue = Guid.NewGuid().ToString() },
            ["text"] = new Value { StringValue = "legacy point" },
        };
        if (tags.Count > 0)
        {
            var list = new ListValue();
            foreach (var t in tags) list.Values.Add(new Value { StringValue = t });
            payload["tags"] = new Value { ListValue = list };
        }

        var attrs = new Struct();
        foreach (var (k, v) in attributes) attrs.Fields[k] = new Value { StringValue = v };
        payload["attributes"] = new Value { StructValue = attrs };
        return payload;
    }
}
