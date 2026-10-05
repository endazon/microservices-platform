using AwesomeAssertions;
using Grpc.Core;
using IngestionService.Domain;
using IngestionService.Domain.Ports;
using IngestionService.Infrastructure.ExternalServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace IngestionService.Tests.Infrastructure.ExternalServices;

// FR-02, FR-03, FR-05, ADR-0127 決定 1・4, [[IADR-0497]] 決定 1〜4 (#1746):
// **語彙索引（ベクトルを持たないコレクション）への書き込み・削除と、埋め込まない文書の判定。**
//
// 実機 Qdrant を立てずに、出た gRPC そのものを記録して測る（`QdrantFullTextIndexBootstrapTests` と同じ器）。
// 実機での成立（ベクトル無しの点が書けて全文で当たり、意味検索に出ない）は
// `Knowledge.IntegrationTests` の `LexicalIndexQdrantTests` が測る。
[Trait("TestKind", "Unit")]
public class LexicalIndexStoreTests
{
    private const string Voyage = "knowledge_chunks_voyage_3_5";
    private const string Ruri = "knowledge_chunks_ruri_v3";
    private const string Lexical = "knowledge_chunks_lexical";

    private static QdrantIngestionVectorStore NewStore(CallInvoker invoker, string? lexical = null) =>
        new(new QdrantClient(new QdrantGrpcClient(invoker)),
            Options.Create(new EmbeddingCollectionsOptions
            {
                Collections =
                [
                    new EmbeddingCollectionOptions { Name = Voyage, VectorSize = 1024 },
                    new EmbeddingCollectionOptions { Name = Ruri, VectorSize = 768 },
                ]
            }),
            lexical);

    // T-29 (FR-02, ADR-0127 決定 1): 語彙索引の点は**ベクトルを持たない**（空の名前つきベクトル）。
    // 零ベクトル・ハッシュ埋め込みで点を作る変異を落とす。
    [Fact]
    public void BuildLexicalPoint_HasNoVectors()
    {
        var id = Guid.NewGuid();

        var point = QdrantIngestionVectorStore.BuildLexicalPoint(id, new Dictionary<string, Value>
        {
            ["text"] = new Value { StringValue = "本文" },
        });

        point.Id.Uuid.Should().Be(id.ToString());
        point.Vectors.VectorsOptionsCase.Should().Be(Vectors.VectorsOptionsOneofCase.Vectors_);
        point.Vectors.Vectors_.Vectors.Should().BeEmpty("意味の無いベクトルで点を作らない（ADR-0127 決定 1）");
        point.Payload.Should().ContainKey("text");
    }

    // T-30 (FR-02, FR-03, FR-05, [[IADR-0497]] 決定 1・3): 語彙索引のチャンクは**語彙索引のコレクションへ**、
    // チャンクの口と同じペイロード（全文・2-gram・ABAC 属性・共有先・文書 ID）で書かれる。
    [Fact]
    public async Task UpsertLexicalChunk_WritesToLexicalCollectionWithFullPayload()
    {
        var invoker = new RecordingCallInvoker();
        var documentId = Guid.NewGuid();

        await NewStore(invoker).UpsertLexicalChunkAsync(Guid.NewGuid(), documentId, "人事評価",
            "評価の手順 ABAC", 0, "storage://x.md",
            new Dictionary<string, string> { ["confidentiality"] = "restricted", ["department"] = "hr" },
            ["評価"], DateTimeOffset.UtcNow, ["bob"], TestContext.Current.CancellationToken);

        var upsert = invoker.Upserts.Should().ContainSingle().Subject;
        upsert.CollectionName.Should().Be(Lexical);
        var point = upsert.Points.Should().ContainSingle().Subject;
        point.Vectors.Vectors_.Vectors.Should().BeEmpty();
        point.Payload["document_id"].StringValue.Should().Be(documentId.ToString());
        point.Payload[QdrantIngestionVectorStore.FullTextKey].StringValue.Should().Contain("ABAC");
        point.Payload["text_ngram"].StringValue.Should().Contain("評価");
        point.Payload["attributes"].StructValue.Fields["confidentiality"].StringValue.Should().Be("restricted");
        point.Payload["shared_with"].ListValue.Values.Select(v => v.StringValue).Should().Equal("bob");
        point.Payload.Should().NotContainKey("has_body", "本文チャンクは印を持たない");
    }

    // T-30 (FR-02, ADR-0070 決定 4): 本文なしの高機密文書のメタデータ点も語彙索引へ、`has_body = false` で書く。
    [Fact]
    public async Task UpsertLexicalMetadataPoint_WritesHasBodyFalseToLexicalCollection()
    {
        var invoker = new RecordingCallInvoker();

        await NewStore(invoker, lexical: "custom_lexical").UpsertLexicalMetadataPointAsync(Guid.NewGuid(),
            Guid.NewGuid(), "題名", "題名 タグ", null,
            new Dictionary<string, string> { ["confidentiality"] = "confidential" }, ["タグ"],
            sharedWith: ["bob", "carol"], ct: TestContext.Current.CancellationToken);

        var upsert = invoker.Upserts.Should().ContainSingle().Subject;
        upsert.CollectionName.Should().Be("custom_lexical", "構成した名前へ書く");
        var point = upsert.Points.Should().ContainSingle().Subject;
        point.Vectors.Vectors_.Vectors.Should().BeEmpty();
        point.Payload["has_body"].BoolValue.Should().BeFalse();
        // #1746 監査 🟡2: 本文なしの点も共有先を運ぶ（落とすと共有先の分岐が語彙索引で効かない）。
        point.Payload["shared_with"].ListValue.Values.Select(v => v.StringValue).Should().Equal("bob", "carol");
    }

    // T-31 (FR-02, FR-05, [[IADR-0497]] 決定 4): 文書単位の削除は**語彙索引からも**消す
    // （機密区分が下がった文書の古い語彙索引の点を残さない）。ベクトルのコレクションも従来どおり全部。
    [Fact]
    public async Task DeleteByDocumentFromAll_AlsoDeletesFromLexicalCollection()
    {
        var invoker = new RecordingCallInvoker();

        await NewStore(invoker).DeleteByDocumentFromAllAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        invoker.Deletes.Should().BeEquivalentTo([Voyage, Ruri, Lexical]);
    }

    // T-32 (FR-02, FR-05, ADR-0127 決定 1・4): 埋め込んでよいのは public / internal だけ（大小文字は問わない）。
    // confidential・restricted・未指定・空・未知・前後空白つきはすべて語彙索引だけ（安全側）。
    [Theory]
    [InlineData("public", false)]
    [InlineData("internal", false)]
    [InlineData("PUBLIC", false)]
    [InlineData("confidential", true)]
    [InlineData("restricted", true)]
    [InlineData("", true)]
    [InlineData("top-secret", true)]
    [InlineData(" internal", true)]
    [InlineData(null, true)]
    public void IsLexicalOnly_AllowsEmbeddingOnlyForPublicAndInternal(string? level, bool expected)
    {
        var attributes = new Dictionary<string, string> { ["department"] = "hr" };
        if (level is not null) attributes["confidentiality"] = level;

        LexicalIndexPolicy.IsLexicalOnly(attributes).Should().Be(expected);
    }

    // T-33 ([[IADR-0497]] 決定 1): 語彙索引の名前は既定名へ倒れ（無効化の口は無い）、
    // ベクトルのコレクションと同名なら起動を止める。
    [Theory]
    [InlineData(null, Lexical)]
    [InlineData("", Lexical)]
    [InlineData("  ", Lexical)]
    [InlineData(" other_lexical ", "other_lexical")]
    public void LexicalCollection_ResolvesToDefaultWhenBlank(string? configured, string expected)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([new(LexicalCollection.ConfigKey, configured)])
            .Build();

        LexicalCollection.Resolve(config).Should().Be(expected);
    }

    [Fact]
    public void LexicalCollection_RejectsNameOfAVectorCollection()
    {
        var act = () => LexicalCollection.EnsureDistinct(Voyage, [Voyage, Ruri]);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{LexicalCollection.ConfigKey}*");
        LexicalCollection.EnsureDistinct(Lexical, [Voyage, Ruri]);   // 陽性対照: 別名なら通る
    }

    // T-31（監査 🟡3）: 語彙索引のコレクションがまだ無い（ブートストラップの失敗）なら、その削除は `NotFound` を
    // no-op にして他のコレクションの削除を済ませる。ベクトルのコレクションの `NotFound` は従来どおり例外（陽性対照）。
    [Fact]
    public async Task DeleteByDocumentFromAll_TreatsMissingLexicalCollectionAsEmpty()
    {
        var invoker = new RecordingCallInvoker { MissingCollection = Lexical };

        await NewStore(invoker).DeleteByDocumentFromAllAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        invoker.Deletes.Should().BeEquivalentTo([Voyage, Ruri], "ベクトルのコレクションからは消している");
    }

    [Fact]
    public async Task DeleteByDocumentFromAll_StillThrows_WhenAVectorCollectionIsMissing()
    {
        var invoker = new RecordingCallInvoker { MissingCollection = Ruri };

        var act = () => NewStore(invoker).DeleteByDocumentFromAllAsync(Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    private sealed class RecordingCallInvoker : CallInvoker
    {
        internal List<UpsertPoints> Upserts { get; } = [];
        internal List<string> Deletes { get; } = [];
        internal List<CreateFieldIndexCollection> FieldIndexes { get; } = [];
        // このコレクションへの呼び出しは Qdrant と同じく NotFound で失敗させる。
        internal string? MissingCollection { get; init; }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            if (request is DeletePoints { CollectionName: var name } && name == MissingCollection)
                return new AsyncUnaryCall<TResponse>(
                    Task.FromException<TResponse>(new RpcException(new Status(StatusCode.NotFound,
                        $"Not found: Collection `{name}` doesn't exist!"))),
                    Task.FromResult(new Metadata()), () => new Status(StatusCode.NotFound, ""), () => [], () => { });

            object response = method.Name switch
            {
                "Upsert" => Ok(() => Upserts.Add((UpsertPoints)(object)request!)),
                "Delete" => Ok(() => Deletes.Add(((DeletePoints)(object)request!).CollectionName)),
                // [[IADR-0502]] 決定 2 (ii) (#1760): 書き込みの口は書く前にキーワード索引を張る（記録だけする）。
                "CreateFieldIndex" => Ok(() => FieldIndexes.Add((CreateFieldIndexCollection)(object)request!)),
                _ => throw new NotSupportedException($"想定していない RPC が出た: {method.Name}"),
            };
            return new AsyncUnaryCall<TResponse>(
                Task.FromResult((TResponse)response),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
        }

        private static PointsOperationResponse Ok(Action record)
        {
            record();
            return new PointsOperationResponse { Result = new UpdateResult { Status = UpdateStatus.Completed } };
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
