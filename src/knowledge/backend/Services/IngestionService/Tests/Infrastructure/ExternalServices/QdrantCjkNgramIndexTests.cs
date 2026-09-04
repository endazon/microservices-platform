using AwesomeAssertions;
using Grpc.Core;
using IngestionService.Domain;
using IngestionService.Domain.Ports;
using IngestionService.Infrastructure.ExternalServices;
using Knowledge.Contracts.Indexing;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace IngestionService.Tests.Infrastructure.ExternalServices;

// FR-03, UC-01, ADR-0009, #1118, [[IADR-0339]] 決定 1・2:
// **日本語 2-gram ペイロード `text_ngram` の索引が全コレクションへ張られ、既存の点へ後付けされること。**
//
// 🔴 索引が在っても、点が `text_ngram` を持たなければ日本語は 0 件のままである。
// 後付け（backfill）が「無い点だけ」を埋め、2 回目以降は何も書かないことをここで固定する。
//
// 実機 Qdrant を立てずに測る（`QdrantGrpcClient(CallInvoker)` に記録用の CallInvoker を挿す。
// `QdrantFullTextIndexBootstrapTests` と同じ作法）。実機での再現率は
// `scripts/verify-qdrant-fulltext-index.sh` 段 7 が測る。
[Trait("TestKind", "Unit")]
public class QdrantCjkNgramIndexTests
{
    private const string CollectionA = "knowledge_chunks_voyage_3_5";
    private const string CollectionB = "knowledge_chunks_ruri_v3";

    // 実配備チャンク（稼働 k3s から scroll した本文）と、CJK を含まない本文。
    private const string JapaneseChunk = "なぜ本文が要るのか\n\nIngestionService の `DocumentUpdatedConsumer` は `MarkdownUri` が null の文書を早期 return で捨てる。";
    private const string AsciiChunk = "IngestionService reads MarkdownUri and upserts chunks.";

    // 決定 2: 索引は**存在の有無によらず**全コレクションへ張る（`text` と同じ作法）。
    [Fact]
    public async Task EnsureCjkNgramIndex_CreatesTextIndexOnEveryCollection()
    {
        var invoker = new RecordingCallInvoker();

        await NewStore(invoker).EnsureCjkNgramIndexAsync(TestContext.Current.CancellationToken);

        invoker.CreatedFieldIndexes.Select(x => x.CollectionName)
            .Should().BeEquivalentTo([CollectionA, CollectionB]);
        invoker.CreatedFieldIndexes.Should().OnlyContain(x =>
            x.FieldName == CjkBigramPayload.PayloadKey && x.FieldType == FieldType.Text);
        invoker.CreatedFieldIndexes.Should().OnlyContain(x =>
            x.FieldIndexParams.TextIndexParams.Tokenizer == TokenizerType.Prefix);
    }

    // 決定 1: 索引の宣言値。**`prefix`・1..2 文字**。黙って変わると 1 文字の語だけが静かに落ちる。
    [Fact]
    public void BuildCjkNgramIndexParams_UsesPrefixTokenizerOverBigrams()
    {
        var p = QdrantIngestionVectorStore.BuildCjkNgramIndexParams().TextIndexParams;

        p.Tokenizer.Should().Be(TokenizerType.Prefix, "2-gram の 1 文字接頭辞も索引に入れ、1 文字の語を当てる");
        p.MinTokenLen.Should().Be(1UL);
        p.MaxTokenLen.Should().Be(2UL, "2-gram より長いトークンは符号化の欠陥であり索引で黙って受けない");
        p.Lowercase.Should().BeTrue();
    }

    // 書き込み側: `text` と同じ本文から `text_ngram` を書く（書き込みと索引を割らない）。
    [Fact]
    public void BuildChunkPayload_WritesCjkNgramFromTheSameText()
    {
        var payload = QdrantIngestionVectorStore.BuildChunkPayload(
            Guid.NewGuid(), "タイトル", "横断検索", 0, null, [], []);

        payload.Should().ContainKey(CjkBigramPayload.PayloadKey);
        payload[CjkBigramPayload.PayloadKey].StringValue.Should().Be("横断 断検 検索");
    }

    // 決定 2: 後付けは **`text_ngram` の無い点だけ**を scroll し、`text` から作って書く。
    [Fact]
    public async Task Backfill_FillsOnlyPointsMissingTheNgramPayload()
    {
        var japanese = Guid.NewGuid();
        var ascii = Guid.NewGuid();
        var invoker = new RecordingCallInvoker
        {
            ScrollPages =
            {
                [CollectionA] = [[(japanese, JapaneseChunk), (ascii, AsciiChunk)]],
            }
        };

        var filled = await NewStore(invoker).BackfillCjkNgramAsync(TestContext.Current.CancellationToken);

        filled.Should().Be(2);
        // scroll の条件: `text_ngram` が空の点だけ、`text` だけを読む、ベクトルは読まない。
        invoker.Scrolls.Should().NotBeEmpty();
        invoker.Scrolls.Should().OnlyContain(s =>
            s.Filter.Must.Any(c => c.IsEmpty != null && c.IsEmpty.Key == CjkBigramPayload.PayloadKey));
        invoker.Scrolls.Should().OnlyContain(s =>
            s.WithPayload.Include.Fields.Contains(QdrantIngestionVectorStore.FullTextKey)
            && !s.WithVectors.Enable);
        // 書き込み: 1 点ずつ SetPayload。日本語は 2-gram、CJK 無しは空文字列（`is_empty` に当たらない）。
        var ops = invoker.UpdateBatches.Should().ContainSingle().Which;
        ops.CollectionName.Should().Be(CollectionA);
        ops.Operations.Should().HaveCount(2);
        var byId = ops.Operations.ToDictionary(
            o => Guid.Parse(o.SetPayload.PointsSelector.Points.Ids.Single().Uuid),
            o => o.SetPayload.Payload[CjkBigramPayload.PayloadKey].StringValue);
        byId[japanese].Should().Be(CjkBigramPayload.Encode(JapaneseChunk));
        byId[japanese].Should().StartWith("なぜ ぜ本 本文");
        byId[ascii].Should().BeEmpty();
    }

    // 決定 2: 埋めるものが無ければ**何も書かない**（2 回目以降の起動はここを通る）。
    [Fact]
    public async Task Backfill_WritesNothing_WhenEveryPointAlreadyHasTheNgramPayload()
    {
        var invoker = new RecordingCallInvoker();

        var filled = await NewStore(invoker).BackfillCjkNgramAsync(TestContext.Current.CancellationToken);

        filled.Should().Be(0);
        invoker.UpdateBatches.Should().BeEmpty();
        invoker.Scrolls.Select(s => s.CollectionName).Should().BeEquivalentTo([CollectionA, CollectionB],
            "全コレクションを 1 回ずつは見る");
    }

    // 🔴 SetPayload が効いていないのに回り続けると無限ループになる。同じ点が続けて返ったら止める。
    [Fact]
    public async Task Backfill_Throws_WhenTheSamePointKeepsComingBack()
    {
        var stuck = Guid.NewGuid();
        var invoker = new RecordingCallInvoker
        {
            ScrollPages =
            {
                [CollectionA] = [[(stuck, JapaneseChunk)], [(stuck, JapaneseChunk)]],
            }
        };

        var act = () => NewStore(invoker).BackfillCjkNgramAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not making progress*");
        invoker.UpdateBatches.Should().ContainSingle("1 ページ目は書いたうえで、2 ページ目で止まる");
    }

    private static QdrantIngestionVectorStore NewStore(CallInvoker invoker) =>
        new(new QdrantClient(new QdrantGrpcClient(invoker)),
            Options.Create(new EmbeddingCollectionsOptions
            {
                Collections =
                [
                    new EmbeddingCollectionOptions { Name = CollectionA, VectorSize = 1024 },
                    new EmbeddingCollectionOptions { Name = CollectionB, VectorSize = 768 },
                ]
            }));

    // 実機 Qdrant なしで「どの RPC がどんな要求で出たか」を記録する器。
    private sealed class RecordingCallInvoker : CallInvoker
    {
        internal List<CreateFieldIndexCollection> CreatedFieldIndexes { get; } = [];
        internal List<ScrollPoints> Scrolls { get; } = [];
        internal List<UpdateBatchPoints> UpdateBatches { get; } = [];

        // コレクションごとの scroll の応答ページ（尽きたら空ページ）。
        internal Dictionary<string, List<List<(Guid Id, string Text)>>> ScrollPages { get; } = [];

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var response = (TResponse)Respond(method.Name, request!);
            return new AsyncUnaryCall<TResponse>(
                Task.FromResult(response), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
        }

        private object Respond(string methodName, object request)
        {
            switch (methodName)
            {
                case "CreateFieldIndex":
                    CreatedFieldIndexes.Add((CreateFieldIndexCollection)request);
                    return new PointsOperationResponse
                    {
                        Result = new UpdateResult { Status = UpdateStatus.Completed }
                    };
                case "Scroll":
                    var scroll = (ScrollPoints)request;
                    Scrolls.Add(scroll);
                    var response = new ScrollResponse();
                    if (ScrollPages.TryGetValue(scroll.CollectionName, out var pages) && pages.Count > 0)
                    {
                        var page = pages[0];
                        pages.RemoveAt(0);
                        foreach (var (id, text) in page)
                        {
                            var point = new RetrievedPoint { Id = new PointId { Uuid = id.ToString() } };
                            point.Payload[QdrantIngestionVectorStore.FullTextKey] = new Value { StringValue = text };
                            response.Result.Add(point);
                        }
                    }
                    return response;
                case "UpdateBatch":
                    UpdateBatches.Add((UpdateBatchPoints)request);
                    return new UpdateBatchResponse();
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
