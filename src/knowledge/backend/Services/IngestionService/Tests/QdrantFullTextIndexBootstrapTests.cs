using AwesomeAssertions;
using Grpc.Core;
using IngestionService.Domain;
using IngestionService.Domain.Ports;
using IngestionService.Infrastructure.ExternalServices;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace IngestionService.Tests;

// FR-02, FR-03, UC-01, #1116, [[IADR-0316]] 決定 1・2:
// **`text` の全文ペイロードインデックスが、新規コレクションにも既に在るコレクションにも張られること。**
//
// 🔴 これが本 issue の中心である。従前の `EnsureCollectionsAsync` は
// `if (await CollectionExistsAsync(...)) continue;` で早期に抜けており、**既に在るコレクションには
// 何もしなかった**。索引の生成をそこへ足しても、**稼働中の配備は永久に索引を持たない**。
//
// **実機 Qdrant を立てずに測る。** `QdrantClient` の各メソッドは `final virtual` で差し替えられないが、
// `QdrantGrpcClient(CallInvoker)` の口が空いているので、**gRPC の呼び出しそのものを記録する**
// `CallInvoker` を挿す。実機の挙動（索引が在るときの検索結果）は
// `scripts/verify-qdrant-fulltext-index.sh` が別に測る（Testcontainers は Docker API を要し、
// containerd の環境では skip のまま緑になるので判定に使わない）。
public class QdrantFullTextIndexBootstrapTests
{
    private const string CollectionA = "knowledge_chunks_voyage_3_5";
    private const string CollectionB = "knowledge_chunks_ruri_v3";

    // FR-03, #1116: 既に在るコレクションにも索引を張る（**後付け**が要件である）。
    [Fact]
    public async Task EnsureCollections_CreatesFullTextIndex_EvenWhenCollectionAlreadyExists()
    {
        var invoker = new RecordingCallInvoker(collectionExists: true);
        var store = NewStore(invoker);

        await store.EnsureCollectionsAsync(TestContext.Current.CancellationToken);

        invoker.CreatedCollections.Should().BeEmpty("既に在るのだから作り直さない");
        invoker.CreatedFieldIndexes.Select(x => x.Collection)
            .Should().BeEquivalentTo([CollectionA, CollectionB],
                "既存コレクションにも後付けしなければ、稼働中の配備は永久に索引を持たない");
        invoker.CreatedFieldIndexes.Should().OnlyContain(
            x => x.FieldName == QdrantIngestionVectorStore.FullTextKey);
    }

    // FR-02, FR-03, #1116: 新規作成の経路でも索引を張る。
    [Fact]
    public async Task EnsureCollections_CreatesCollectionAndFullTextIndex_WhenMissing()
    {
        var invoker = new RecordingCallInvoker(collectionExists: false);
        var store = NewStore(invoker);

        await store.EnsureCollectionsAsync(TestContext.Current.CancellationToken);

        invoker.CreatedCollections.Should().BeEquivalentTo([CollectionA, CollectionB]);
        invoker.CreatedFieldIndexes.Select(x => x.Collection)
            .Should().BeEquivalentTo([CollectionA, CollectionB]);
    }

    // FR-03, #1116: 索引は **text 型**として張る（キーワード型で張ると full-text Match が成立しない）。
    [Fact]
    public async Task EnsureCollections_DeclaresTextFieldType()
    {
        var invoker = new RecordingCallInvoker(collectionExists: true);

        await NewStore(invoker).EnsureCollectionsAsync(TestContext.Current.CancellationToken);

        invoker.CreatedFieldIndexes.Should().OnlyContain(x => x.FieldType == FieldType.Text);
    }

    // FR-03, #1116, [[IADR-0316]] 決定 1: トークナイザの宣言値を固定する。
    //
    // **`multilingual` を実測で選んだ**（word / whitespace は日本語がほぼ全滅し、prefix は語頭しか
    // 当たらない。索引なしは「部分文字列の全走査」で、語でない断片に当たり語順にも依存する）。
    // ここが黙って変わると、**検索結果は返り続けたまま**質だけが落ちるので固定する。
    [Fact]
    public void BuildFullTextIndexParams_UsesMultilingualTokenizer()
    {
        var p = QdrantIngestionVectorStore.BuildFullTextIndexParams();

        p.TextIndexParams.Tokenizer.Should().Be(TokenizerType.Multilingual);
        p.TextIndexParams.Lowercase.Should().BeTrue("型番・略語の大小文字差を吸収する");
        p.TextIndexParams.MinTokenLen.Should().Be(1UL, "日本語 1 文字の語を落とさない");
        p.TextIndexParams.MaxTokenLen.Should().Be(40UL);
    }

    // FR-03, #1116: 書き込みのキーと索引のキーは同じ 1 つの値である（割れると索引が空振る）。
    [Fact]
    public void BuildChunkPayload_WritesTextUnderTheIndexedKey()
    {
        var payload = QdrantIngestionVectorStore.BuildChunkPayload(
            Guid.NewGuid(), "タイトル", "本文", 0, null, [], []);

        payload.Should().ContainKey(QdrantIngestionVectorStore.FullTextKey);
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

    // 実機 Qdrant なしで「どの RPC が出たか」を固定するための器。
    // 応答は成功で固定し、**呼ばれたかどうかだけ**を判定材料にする。
    private sealed class RecordingCallInvoker(bool collectionExists) : CallInvoker
    {
        internal List<string> CreatedCollections { get; } = [];
        internal List<(string Collection, string FieldName, FieldType FieldType)> CreatedFieldIndexes { get; } = [];

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var response = (TResponse)Respond(method.Name, request!);
            return new AsyncUnaryCall<TResponse>(
                Task.FromResult(response),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
        }

        private object Respond(string methodName, object request) => methodName switch
        {
            "CollectionExists" => new CollectionExistsResponse
            {
                Result = new CollectionExists { Exists = collectionExists }
            },
            "Create" => Record(((CreateCollection)request).CollectionName,
                new CollectionOperationResponse { Result = true }),
            "CreateFieldIndex" => RecordIndex((CreateFieldIndexCollection)request),
            _ => throw new NotSupportedException(
                $"想定していない RPC が出た: {methodName}。テストの器を更新すること"),
        };

        private object Record(string collection, object response)
        {
            CreatedCollections.Add(collection);
            return response;
        }

        private object RecordIndex(CreateFieldIndexCollection request)
        {
            CreatedFieldIndexes.Add((request.CollectionName, request.FieldName, request.FieldType));
            return new PointsOperationResponse
            {
                Result = new UpdateResult { Status = UpdateStatus.Completed }
            };
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
