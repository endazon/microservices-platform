using AwesomeAssertions;
using Grpc.Core;
using IngestionService.Domain.Ports;
using IngestionService.Infrastructure.ExternalServices;
using Knowledge.Contracts.Dtos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Shared.Contracts.Dtos;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace IngestionService.Tests.Infrastructure.ExternalServices;

// FR-04, FR-05, SC-01, SC-08, [[IADR-0502]] 決定 1〜4 (#1760):
// **facet と ABAC フィルタが引くキー（`tags`・`shared_with`・`attributes.<key>`）にキーワード索引を張る。**
//
// 実機 Qdrant を立てずに、出た gRPC そのものを記録して測る（`QdrantFullTextIndexBootstrapTests` と同じ器）。
// 実機での成立（索引があれば facet が通り、無ければ `No appropriate index for faceting` で失敗する。
// フィルタの意味は変わらない）は `Knowledge.IntegrationTests` の `KeywordIndexQdrantTests` が測る。
[Trait("TestKind", "Unit")]
public class QdrantKeywordIndexTests
{
    private const string Voyage = "knowledge_chunks_voyage_3_5";
    private const string Ruri = "knowledge_chunks_ruri_v3";
    private const string Lexical = "knowledge_chunks_lexical";

    // T-34 ([[IADR-0502]] 決定 1): 張るキーの集合は、照会とフィルタが通る写像（`ToPayloadKey`）から導く。
    // 集合値キー（`tags`・`shared_with`）は常に入り、属性キーは `attributes.<key>` になる。空・空白は捨てる。
    [Fact]
    public void KeywordIndexKeys_AreDerivedFromTheQueryMapping()
    {
        AttributeValueKeys.KeywordIndexKeys([]).Should().Equal(
            [AttributeValueKeys.SharedWith, AttributeValueKeys.Tags]);

        AttributeValueKeys.KeywordIndexKeys(["department", "project", "Tags", "", "  ", "department"])
            .Should().Equal(["attributes.department", "attributes.project", "shared_with", "tags"],
                "属性キーは照会と同じく attributes.<key>。`Tags` は照会と同じく最上位の tags へ寄る。重複は 1 つ");

        // 🔴 1 つの情報源: どの照会キーについても、照会が引くペイロードキーに索引が張られる。
        foreach (var key in new[] { "tags", "shared_with", "department", "doc_scope", "owner", "SHARED_WITH" })
            AttributeValueKeys.KeywordIndexKeys([key]).Should().Contain(AttributeValueKeys.ToPayloadKey(key), key);

        // 集合値キーの語彙（`DocumentAttributeEncoding.SetValuedKeys`）が増えたら、索引のキーも増える。
        foreach (var setValued in DocumentAttributeEncoding.SetValuedKeys)
            AttributeValueKeys.KeywordIndexKeys([]).Should().Contain(AttributeValueKeys.ToPayloadKey(setValued));
    }

    // T-35 ([[IADR-0502]] 決定 4): 索引は keyword 型。`is_tenant`・`on_disk` は指定しない（既定の偽）。
    [Fact]
    public void BuildKeywordIndexParams_KeepsQdrantDefaults()
    {
        var p = QdrantIngestionVectorStore.BuildKeywordIndexParams();

        p.IndexParamsCase.Should().Be(PayloadIndexParams.IndexParamsOneofCase.KeywordIndexParams);
        p.KeywordIndexParams.HasIsTenant.Should().BeFalse("属性はテナントの区切りではない");
        p.KeywordIndexParams.HasOnDisk.Should().BeFalse("規模はメモリ上の索引で足りる");
    }

    // T-35 ([[IADR-0502]] 決定 2 (i)・3): 起動時に、集合値キーの索引を**全コレクション**（モデル別 ＋ 語彙索引）へ張る。
    [Fact]
    public async Task EnsureKeywordIndexes_IndexesSetValuedKeysOnEveryCollection()
    {
        var invoker = new RecordingCallInvoker();

        await NewStore(invoker).EnsureKeywordIndexesAsync(TestContext.Current.CancellationToken);

        invoker.FieldIndexes.Select(x => (x.CollectionName, x.FieldName)).Should().BeEquivalentTo(
        [
            (Voyage, "shared_with"), (Voyage, "tags"),
            (Ruri, "shared_with"), (Ruri, "tags"),
            (Lexical, "shared_with"), (Lexical, "tags"),
        ]);
        invoker.FieldIndexes.Should().OnlyContain(x => x.FieldType == FieldType.Keyword
            && x.FieldIndexParams.Equals(QdrantIngestionVectorStore.BuildKeywordIndexParams()));
    }

    // T-36 ([[IADR-0502]] 決定 2 (ii)): 書き込みの 4 つの口は、書く点の属性キーへ索引を張ってから書く。
    // 同じ（コレクション, キー）へは 2 度呼ばない。別のコレクションには張る。`wait=false`（取り込みの期限に入れない）。
    [Fact]
    public async Task Upserts_IndexAttributeKeysOncePerCollection()
    {
        var invoker = new RecordingCallInvoker();
        var store = NewStore(invoker);
        var ct = TestContext.Current.CancellationToken;
        var attrs = new Dictionary<string, string> { ["department"] = "hr", ["project"] = "alpha" };

        await store.UpsertChunkAsync(Voyage, Guid.NewGuid(), Guid.NewGuid(), "t", "本文", 0, [0.1f], null,
            new(attrs), [], ct: ct);
        await store.UpsertChunkAsync(Voyage, Guid.NewGuid(), Guid.NewGuid(), "t", "本文", 1, [0.1f], null,
            new(attrs), [], ct: ct);
        await store.UpsertMetadataPointAsync(Ruri, Guid.NewGuid(), Guid.NewGuid(), "t", "題名", [0.1f], null,
            new(attrs) { ["owner"] = "bob" }, [], ct: ct);
        await store.UpsertLexicalChunkAsync(Guid.NewGuid(), Guid.NewGuid(), "t", "本文", 0, null,
            new() { ["confidentiality"] = "restricted" }, [], ct: ct);
        await store.UpsertLexicalMetadataPointAsync(Guid.NewGuid(), Guid.NewGuid(), "t", "題名", null,
            new() { ["doc_scope"] = "private-note" }, [], ct: ct);   // 口ごとに別のキー（片方の口の欠落を隠さない）

        invoker.FieldIndexes.Select(x => (x.CollectionName, x.FieldName)).Should().BeEquivalentTo(
        [
            (Voyage, "attributes.department"), (Voyage, "attributes.project"), (Voyage, "shared_with"), (Voyage, "tags"),
            (Ruri, "attributes.department"), (Ruri, "attributes.owner"), (Ruri, "attributes.project"),
            (Ruri, "shared_with"), (Ruri, "tags"),
            (Lexical, "attributes.confidentiality"), (Lexical, "attributes.doc_scope"), (Lexical, "shared_with"), (Lexical, "tags"),
        ], "2 回目のチャンクは同じキーなので呼ばない。コレクションごとに張る");
        invoker.FieldIndexes.Should().OnlyContain(x => x.FieldType == FieldType.Keyword && x.HasWait && !x.Wait);
        invoker.Upserts.Should().HaveCount(5, "全部書いている");
    }

    // T-36: 起動時に張った集合値キーは、書き込みで張り直さない（記憶を共有する）。
    [Fact]
    public async Task Upsert_DoesNotRecreateIndexesEnsuredAtStartup()
    {
        var invoker = new RecordingCallInvoker();
        var store = NewStore(invoker);
        var ct = TestContext.Current.CancellationToken;
        await store.EnsureKeywordIndexesAsync(ct);
        invoker.FieldIndexes.Clear();

        await store.UpsertChunkAsync(Voyage, Guid.NewGuid(), Guid.NewGuid(), "t", "本文", 0, [0.1f], null,
            [], ["タグ"], sharedWith: ["bob"], ct: ct);

        invoker.FieldIndexes.Should().BeEmpty();
    }

    // T-37 ([[IADR-0502]] 決定 2 (ii)): 索引の失敗は書き込みを止めない。JSON パスにならないキー（`InvalidArgument`）は
    // 二度と呼ばない。それ以外の失敗は次の書き込みで張り直す。
    [Fact]
    public async Task IndexFailure_DoesNotBlockUpsert_AndOnlyTransientFailuresAreRetried()
    {
        var invoker = new RecordingCallInvoker
        {
            FailIndex =
            {
                ["attributes.my key"] = StatusCode.InvalidArgument,
                ["attributes.department"] = StatusCode.Unavailable,
            }
        };
        var store = NewStore(invoker);
        var ct = TestContext.Current.CancellationToken;
        var attrs = new Dictionary<string, string> { ["my key"] = "x", ["department"] = "hr" };

        await store.UpsertChunkAsync(Voyage, Guid.NewGuid(), Guid.NewGuid(), "t", "本文", 0, [0.1f], null,
            new(attrs), [], ct: ct);
        await store.UpsertChunkAsync(Voyage, Guid.NewGuid(), Guid.NewGuid(), "t", "本文", 1, [0.1f], null,
            new(attrs), [], ct: ct);

        invoker.Upserts.Should().HaveCount(2, "索引の失敗で書き込みを止めない");
        invoker.IndexAttempts.Count(k => k == "attributes.my key").Should().Be(1, "不正なキーは再試行しない");
        invoker.IndexAttempts.Count(k => k == "attributes.department").Should().Be(2, "一時的な失敗は次の書き込みで張り直す");
    }

    // T-37: 呼び出し元の取り消しは握りつぶさない（書き込みも取り消される）。
    [Fact]
    public async Task IndexCancellation_IsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        var invoker = new RecordingCallInvoker { OnIndex = cts.Cancel, FailIndex = { ["tags"] = StatusCode.Cancelled } };

        var act = () => NewStore(invoker).UpsertChunkAsync(Voyage, Guid.NewGuid(), Guid.NewGuid(), "t", "本文", 0,
            [0.1f], null, [], [], ct: cts.Token);

        await act.Should().ThrowAsync<RpcException>();
        invoker.Upserts.Should().BeEmpty();
    }

    // T-38 ([[IADR-0502]] 決定 3): 既存の点の属性キーを全コレクションで（ページを辿って）拾い、索引を張る。
    // 読むのは `attributes` だけで、ベクトルは読まない。
    [Fact]
    public async Task Discovery_IndexesKeysFoundOnExistingPoints_AcrossPages()
    {
        var invoker = new RecordingCallInvoker();
        invoker.ScrollPages[Voyage] =
        [
            [new() { ["department"] = "hr" }, new() { ["project"] = "alpha" }],
            [new() { ["owner"] = "bob" }, []],
        ];
        invoker.ScrollPages[Lexical] = [[new() { ["confidentiality"] = "restricted" }]];

        var created = await NewStore(invoker).EnsureKeywordIndexesForExistingPointsAsync(
            TestContext.Current.CancellationToken);

        invoker.FieldIndexes.Where(x => x.FieldName.StartsWith("attributes.", StringComparison.Ordinal))
            .Select(x => (x.CollectionName, x.FieldName)).Should().BeEquivalentTo(
            [
                (Voyage, "attributes.department"), (Voyage, "attributes.owner"), (Voyage, "attributes.project"),
                (Lexical, "attributes.confidentiality"),
            ], "2 ページ目の owner も拾う。点の無いコレクション（ruri）には属性キーを張らない");
        created.Should().Be(invoker.FieldIndexes.Count);
        invoker.Scrolls.Should().OnlyContain(s => s.WithPayload.Include.Fields.SequenceEqual(new[] { "attributes" })
            && !s.WithVectors.Enable);
        invoker.Scrolls.Count(s => s.CollectionName == Voyage).Should().Be(2, "次のページの位置を辿る");
        // 🔴 2 回目の scroll は 1 回目の応答の `NextPageOffset` から読む（独立監査 🟡1。器はこの位置でページを引く）。
        var voyageScrolls = invoker.Scrolls.Where(s => s.CollectionName == Voyage).ToList();
        voyageScrolls[0].Offset.Should().BeNull("先頭から読む");
        voyageScrolls[1].Offset.Should().Be(RecordingCallInvoker.PageOffset(Voyage, 1));
    }

    // T-38 ([[IADR-0502]] 決定 3。独立監査 🟡3): 1 つのコレクションの走査の失敗で、他のコレクションの発見を止めない。
    // 失敗したコレクションはコレクション名と状態コードだけを Warning に残す（ペイロードの値は出さない）。
    [Theory]
    [InlineData(StatusCode.NotFound)]
    [InlineData(StatusCode.Unavailable)]
    public async Task Discovery_ContinuesWithOtherCollections_WhenOneScanFails(StatusCode code)
    {
        var invoker = new RecordingCallInvoker { FailScroll = { [Voyage] = code } };
        invoker.ScrollPages[Ruri] = [[new() { ["department"] = "secret-hr-value" }]];
        invoker.ScrollPages[Lexical] = [[new() { ["owner"] = "bob" }]];
        var logger = new ListLogger<QdrantIngestionVectorStore>();

        var created = await NewStore(invoker, logger).EnsureKeywordIndexesForExistingPointsAsync(
            TestContext.Current.CancellationToken);

        invoker.FieldIndexes.Select(x => (x.CollectionName, x.FieldName)).Should().BeEquivalentTo(
        [
            (Ruri, "attributes.department"), (Ruri, "shared_with"), (Ruri, "tags"),
            (Lexical, "attributes.owner"), (Lexical, "shared_with"), (Lexical, "tags"),
        ], "最初のコレクションが失敗しても、後のコレクションには張る");
        created.Should().Be(6);
        var warning = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Which;
        warning.Message.Should().Contain(Voyage).And.Contain(code.ToString());
        logger.Entries.Should().NotContain(e => e.Message.Contains("secret-hr-value") || e.Message.Contains("bob"));
    }

    // T-38: 呼び出し元の取り消しは走査の失敗として飲み込まない。
    [Fact]
    public async Task Discovery_CallerCancellation_IsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var invoker = new RecordingCallInvoker { FailScroll = { [Voyage] = StatusCode.Cancelled } };

        var act = () => NewStore(invoker).EnsureKeywordIndexesForExistingPointsAsync(cts.Token);

        await act.Should().ThrowAsync<Exception>();
        invoker.Scrolls.Should().ContainSingle("取り消し後に次のコレクションへ進まない");
    }

    // T-38: 点のペイロードから属性キーを読む（ネスト構造体以外は無視する）。
    [Fact]
    public void ReadAttributeKeys_ReadsNestedStructKeysOnly()
    {
        var payload = QdrantIngestionVectorStore.BuildChunkPayload(Guid.NewGuid(), "t", "本文", 0, null,
            new() { ["department"] = "hr", ["owner"] = "bob" }, ["タグ"]);

        QdrantIngestionVectorStore.ReadAttributeKeys(payload).Should().BeEquivalentTo(["department", "owner"]);
        QdrantIngestionVectorStore.ReadAttributeKeys(new Dictionary<string, Value>
        {
            ["attributes"] = new Value { StringValue = "not-a-struct" },
        }).Should().BeEmpty();
    }

    private static QdrantIngestionVectorStore NewStore(
        CallInvoker invoker, ILogger<QdrantIngestionVectorStore>? logger = null) =>
        new(new QdrantClient(new QdrantGrpcClient(invoker)),
            Options.Create(new EmbeddingCollectionsOptions
            {
                Collections =
                [
                    new EmbeddingCollectionOptions { Name = Voyage, VectorSize = 1024 },
                    new EmbeddingCollectionOptions { Name = Ruri, VectorSize = 768 },
                ]
            }), logger: logger);

    // ログの水準と本文を観測する器（新規パッケージを増やさないため手書きする）。
    private sealed class ListLogger<T> : ILogger<T>
    {
        internal sealed record Entry(LogLevel Level, string Message);

        internal List<Entry> Entries { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new Entry(logLevel, formatter(state, exception)));
    }

    // 実機 Qdrant なしで「どの RPC がどんな要求で出たか」を記録する器。
    private sealed class RecordingCallInvoker : CallInvoker
    {
        internal List<CreateFieldIndexCollection> FieldIndexes { get; } = [];
        internal List<string> IndexAttempts { get; } = [];
        internal List<UpsertPoints> Upserts { get; } = [];
        internal List<ScrollPoints> Scrolls { get; } = [];

        // このキーへの索引の作成を、指定の状態コードで失敗させる。
        internal Dictionary<string, StatusCode> FailIndex { get; } = [];
        internal Action? OnIndex { get; init; }

        // コレクションごとの scroll の応答ページ（点ごとの属性）。無ければ空の 1 ページ。
        // 🔴 **ページは要求の `Offset` で引く**（実機 Qdrant と同じ。独立監査 🟡1）: `Offset` 無し ＝ 先頭ページ、
        // それ以外は直前に返した `NextPageOffset` と一致するページだけを返す。知らない位置は器の誤りとして落とす。
        // 呼び出しの順で次のページを返すと、`Offset` を送らない実装（実機では先頭ページを読み続けて終わらない）が通ってしまう。
        internal Dictionary<string, List<List<Dictionary<string, string>>>> ScrollPages { get; } = [];

        // このコレクションの scroll を、指定の状態コードで失敗させる。
        internal Dictionary<string, StatusCode> FailScroll { get; } = [];

        // ページ i（i ≥ 1）の位置。コレクションとページ番号から決まる（応答と要求の突き合わせに使う）。
        internal static PointId PageOffset(string collection, int page)
        {
            var hash = System.Security.Cryptography.MD5.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{collection}#{page}"));
            return new PointId { Uuid = new Guid(hash).ToString() };
        }

        private ScrollResponse ServePage(ScrollPoints scroll)
        {
            var pages = ScrollPages.TryGetValue(scroll.CollectionName, out var p) ? p : [[]];
            if (Scrolls.Count(s => s.CollectionName == scroll.CollectionName) > pages.Count)
                throw new InvalidOperationException(
                    $"{scroll.CollectionName} を {pages.Count} ページより多く読んだ（続きの位置を送っていない）");

            int index;
            if (scroll.Offset is null)
                index = 0;
            else
            {
                index = Enumerable.Range(1, Math.Max(pages.Count - 1, 0))
                    .FirstOrDefault(i => PageOffset(scroll.CollectionName, i).Equals(scroll.Offset), -1);
                if (index < 0)
                    throw new InvalidOperationException($"知らない位置から読んだ: {scroll.Offset}");
            }

            var response = new ScrollResponse();
            foreach (var attributes in pages[index])
            {
                var point = new RetrievedPoint { Id = new PointId { Uuid = Guid.NewGuid().ToString() } };
                if (attributes.Count > 0)
                {
                    var s = new Struct();
                    foreach (var (k, v) in attributes) s.Fields[k] = new Value { StringValue = v };
                    point.Payload["attributes"] = new Value { StructValue = s };
                }
                response.Result.Add(point);
            }
            if (index + 1 < pages.Count)
                response.NextPageOffset = PageOffset(scroll.CollectionName, index + 1);
            return response;
        }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            if (request is CreateFieldIndexCollection index)
            {
                IndexAttempts.Add(index.FieldName);
                OnIndex?.Invoke();
                if (FailIndex.TryGetValue(index.FieldName, out var code))
                    return new AsyncUnaryCall<TResponse>(
                        Task.FromException<TResponse>(new RpcException(new Status(code, "simulated"))),
                        Task.FromResult(new Metadata()), () => new Status(code, ""), () => [], () => { });
                FieldIndexes.Add(index);
            }

            object raw;
            try
            {
                raw = Respond(method.Name, request!);
            }
            catch (RpcException ex)
            {
                return new AsyncUnaryCall<TResponse>(
                    Task.FromException<TResponse>(ex),
                    Task.FromResult(new Metadata()), () => ex.Status, () => [], () => { });
            }

            var response = (TResponse)raw;
            return new AsyncUnaryCall<TResponse>(
                Task.FromResult(response), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
        }

        private object Respond(string methodName, object request)
        {
            switch (methodName)
            {
                case "CreateFieldIndex":
                    return new PointsOperationResponse { Result = new UpdateResult { Status = UpdateStatus.Completed } };
                case "Upsert":
                    Upserts.Add((UpsertPoints)request);
                    return new PointsOperationResponse { Result = new UpdateResult { Status = UpdateStatus.Completed } };
                case "Scroll":
                    var scroll = (ScrollPoints)request;
                    Scrolls.Add(scroll);
                    if (FailScroll.TryGetValue(scroll.CollectionName, out var scrollFailure))
                        throw new RpcException(new Status(scrollFailure, "simulated"));
                    return ServePage(scroll);
                default:
                    throw new NotSupportedException(
                        $"想定していない RPC が出た: {methodName}。テストの器を更新すること");
            }
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException();

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotSupportedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotSupportedException();
    }
}
