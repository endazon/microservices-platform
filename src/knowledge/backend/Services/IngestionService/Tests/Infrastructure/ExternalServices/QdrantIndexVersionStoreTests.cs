using AwesomeAssertions;
using Grpc.Core;
using IngestionService.Domain;
using IngestionService.Domain.Ports;
using IngestionService.Infrastructure.ExternalServices;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace IngestionService.Tests.Infrastructure.ExternalServices;

// FR-02, FR-05, [[IADR-0595]] 決定 1・2 (#2101): **版の印と旧版の点の削除**（Qdrant の写像）。
//
// 実機 Qdrant を立てずに、出た gRPC そのものを記録して測る（`LexicalIndexStoreTests` と同じ器）。
// 順序と原子性の振る舞い（どの場面で何が残るか）は `IngestionOrderingAndAtomicityTests` が測る。
[Trait("TestKind", "Unit")]
public class QdrantIndexVersionStoreTests
{
    private const string Voyage = "knowledge_chunks_voyage_3_5";
    private const string Ruri = "knowledge_chunks_ruri_v3";
    private const string Lexical = "knowledge_chunks_lexical";
    private const string Versions = QdrantIngestionVectorStore.IndexVersionCollectionName;

    private static readonly Guid DocId = Guid.Parse("21010000-0000-0000-0000-0000000000aa");
    private static readonly DateTimeOffset Version = new(2026, 10, 23, 1, 2, 3, 456, TimeSpan.Zero);

    private static QdrantIngestionVectorStore NewStore(CallInvoker invoker) =>
        new(new QdrantClient(new QdrantGrpcClient(invoker)),
            Options.Create(new EmbeddingCollectionsOptions
            {
                Collections =
                [
                    new EmbeddingCollectionOptions { Name = Voyage, VectorSize = 1024 },
                    new EmbeddingCollectionOptions { Name = Ruri, VectorSize = 768 },
                ]
            }),
            Lexical);

    // 🔴 旧版の点を選ぶフィルタ: 文書の点 ∧ ¬(updated_at > 版) ∧ ¬(この取り込みで書いた点)。
    // 「より新しい版の点は残す」と「`updated_at` を持たない古い点は消す」を、must_not の範囲条件 1 つで表す。
    [Fact]
    public void 旧版のフィルタは文書の点のうち新しい版と書いた点を除く()
    {
        var keep = Guid.NewGuid();

        var filter = QdrantIngestionVectorStore.BuildSupersededFilter(DocId, Version, null, [keep]);

        filter.Must.Should().ContainSingle().Which.Field.Should().Match<FieldCondition>(f =>
            f.Key == "document_id" && f.Match.Keyword == DocId.ToString());
        filter.MustNot.Should().HaveCount(2);
        filter.MustNot[0].Field.Key.Should().Be("updated_at");
        filter.MustNot[0].Field.Range.Gt.Should().Be(Version.ToUnixTimeMilliseconds());
        filter.MustNot[0].Field.Range.HasGte.Should().BeFalse("等しい版の点（書いていないもの）は消す");
        filter.MustNot[1].HasId.HasId.Should().ContainSingle().Which.Uuid.Should().Be(keep.ToString());

        QdrantIngestionVectorStore.BuildSupersededFilter(DocId, Version, null, []).MustNot.Should().ContainSingle(
            "書いた点が無ければ ID の除外は付けない（空の has_id は何にも当たらない条件になる）");
    }

    // FR-02, IADR-0638 決定 3 (#2150 / #2251): 版に連番があるとき、同じ時刻で連番の大きい点（同じ時刻の新しい版の点）を残す条件を足す。
    // 連番の無い点（旧点）はこの条件に当たらないので、時刻だけで比べられる（同じ時刻なら消える）。
    [Fact]
    public void 版に連番があれば_同じ時刻で連番の大きい点を残す条件が付き_無ければ付かない()
    {
        var filter = QdrantIngestionVectorStore.BuildSupersededFilter(DocId, Version, 5, []);

        filter.MustNot.Should().HaveCount(2);
        var sameTimeNewer = filter.MustNot[1].Filter.Must;
        sameTimeNewer.Should().HaveCount(2);
        sameTimeNewer[0].Field.Key.Should().Be("updated_at");
        sameTimeNewer[0].Field.Range.Gte.Should().Be(Version.ToUnixTimeMilliseconds());
        sameTimeNewer[0].Field.Range.Lte.Should().Be(Version.ToUnixTimeMilliseconds());
        sameTimeNewer[1].Field.Key.Should().Be(QdrantIngestionVectorStore.SequenceKey);
        sameTimeNewer[1].Field.Range.Gt.Should().Be(5);

        QdrantIngestionVectorStore.BuildSupersededFilter(DocId, Version, null, []).MustNot.Should().ContainSingle(
            "連番の無い版（旧形式の電文）は現行と同じ条件だけ");
    }

    [Fact]
    public void 版の印は連番を往復で保ち_連番の無い印は連番なしとして読む()
    {
        var withSequence = QdrantIngestionVectorStore.BuildIndexVersionPoint(DocId, new DocumentIndexVersion(Version, "fp", 7));
        QdrantIngestionVectorStore.ReadIndexVersion(withSequence.Payload)!.Sequence.Should().Be(7);

        var legacy = QdrantIngestionVectorStore.BuildIndexVersionPoint(DocId, new DocumentIndexVersion(Version, "fp"));
        legacy.Payload.ContainsKey(QdrantIngestionVectorStore.SequenceKey).Should().BeFalse();
        QdrantIngestionVectorStore.ReadIndexVersion(legacy.Payload)!.Sequence.Should().BeNull();
    }

    // 全コレクション（モデル別 ＋ 語彙索引）を巡り、残す点はコレクションごとに分けて渡す
    // （同じ ID の点が別のコレクションに残っていれば消す —— 埋め込み待ち → 主への移動）。
    [Fact]
    public async Task 旧版の削除は全コレクションを巡り残す点をコレクションごとに分ける()
    {
        var invoker = new RecordingCallInvoker();
        var inVoyage = Guid.NewGuid();
        var inLexical = Guid.NewGuid();

        await NewStore(invoker).DeleteSupersededAsync(DocId, Version, null,
            [new IndexedPointRef(Voyage, inVoyage), IndexedPointRef.Lexical(inLexical)],
            TestContext.Current.CancellationToken);

        invoker.Deletes.Select(d => d.CollectionName).Should().Equal(Voyage, Ruri, Lexical);
        KeptIds(invoker.Deletes[0]).Should().Equal(inVoyage.ToString());
        KeptIds(invoker.Deletes[1]).Should().BeEmpty();
        KeptIds(invoker.Deletes[2]).Should().Equal(inLexical.ToString());
    }

    [Fact]
    public async Task 旧版の削除は語彙索引が無ければ飛ばし_ベクトルのコレクションが無ければ投げる()
    {
        var lexicalMissing = new RecordingCallInvoker { MissingCollection = Lexical };
        await NewStore(lexicalMissing).DeleteSupersededAsync(DocId, Version, null, [], TestContext.Current.CancellationToken);
        lexicalMissing.Deletes.Select(d => d.CollectionName).Should().Equal(Voyage, Ruri);

        var vectorMissing = new RecordingCallInvoker { MissingCollection = Ruri };
        var act = () => NewStore(vectorMissing).DeleteSupersededAsync(DocId, Version, null, [],
            TestContext.Current.CancellationToken);
        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    // 🔴 版の印は文書 ID・版・判定軸の指紋だけを持つ（題名・本文・タグ・属性の値は持たない）。ベクトルも持たない。
    [Fact]
    public void 版の印の点は三つの項目だけを持ち往復で同じ版に戻る()
    {
        var version = new DocumentIndexVersion(Version, "abc123");

        var point = QdrantIngestionVectorStore.BuildIndexVersionPoint(DocId, version);

        point.Id.Uuid.Should().Be(DocId.ToString());
        point.Vectors.Vectors_.Vectors.Should().BeEmpty();
        point.Payload.Keys.Should().BeEquivalentTo(["document_id", "updated_at", "access_fingerprint"]);
        QdrantIngestionVectorStore.ReadIndexVersion(point.Payload).Should().Be(
            new DocumentIndexVersion(DateTimeOffset.FromUnixTimeMilliseconds(Version.ToUnixTimeMilliseconds()), "abc123"));
        QdrantIngestionVectorStore.ReadIndexVersion(new Dictionary<string, Value>()).Should().BeNull(
            "版の読めない点は印が無いのと同じ（判定軸は不明 → 先に消す側へ倒れる）");
    }

    [Fact]
    public async Task 版の印のコレクションが無ければ印は無いと読み_最初の書き込みで作る()
    {
        var invoker = new RecordingCallInvoker { MissingCollection = Versions };
        var store = NewStore(invoker);

        (await store.GetIndexVersionAsync(DocId, TestContext.Current.CancellationToken)).Should().BeNull();

        await store.SetIndexVersionAsync(DocId, new DocumentIndexVersion(Version, "f"), TestContext.Current.CancellationToken);

        invoker.Created.Should().ContainSingle().Which.Should().Match<CreateCollection>(c =>
            c.CollectionName == Versions && c.VectorsConfig.ParamsMap.Map.Count == 0);
        invoker.Upserts.Should().ContainSingle(u => u.CollectionName == Versions, "作った後に書き直す");
    }

    [Fact]
    public async Task 版の印は文書_ID_の点として読み書きされる()
    {
        var invoker = new RecordingCallInvoker();
        var store = NewStore(invoker);

        await store.SetIndexVersionAsync(DocId, new DocumentIndexVersion(Version, "f"), TestContext.Current.CancellationToken);
        invoker.StoredForGet = invoker.Upserts.Single().Points.Single();

        var read = await store.GetIndexVersionAsync(DocId, TestContext.Current.CancellationToken);

        read!.UpdatedAtMs.Should().Be(Version.ToUnixTimeMilliseconds());
        read.AccessFingerprint.Should().Be("f");
        invoker.Gets.Should().ContainSingle().Which.Should().Match<GetPoints>(g =>
            g.CollectionName == Versions && g.Ids.Single().Uuid == DocId.ToString());
    }

    private static IEnumerable<string> KeptIds(DeletePoints delete) =>
        delete.Points.Filter.MustNot.Where(c => c.HasId is not null).SelectMany(c => c.HasId.HasId).Select(i => i.Uuid);

    private sealed class RecordingCallInvoker : CallInvoker
    {
        internal List<UpsertPoints> Upserts { get; } = [];
        internal List<DeletePoints> Deletes { get; } = [];
        internal List<GetPoints> Gets { get; } = [];
        internal List<CreateCollection> Created { get; } = [];
        internal PointStruct? StoredForGet { get; set; }
        // このコレクションへの点の呼び出しは Qdrant と同じく NotFound で失敗させる（作られるまで）。
        internal string? MissingCollection { get; set; }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var collection = request switch
            {
                DeletePoints d => d.CollectionName,
                UpsertPoints u => u.CollectionName,
                GetPoints g => g.CollectionName,
                _ => null,
            };
            if (collection is not null && collection == MissingCollection)
                return Fail<TResponse>(collection);

            object response = method.Name switch
            {
                "Upsert" => Ok(() => Upserts.Add((UpsertPoints)(object)request!)),
                "Delete" => Ok(() => Deletes.Add((DeletePoints)(object)request!)),
                "Get" => Retrieve((GetPoints)(object)request!),
                "CollectionExists" => new CollectionExistsResponse { Result = new CollectionExists { Exists = false } },
                "Create" => Create((CreateCollection)(object)request!),
                _ => throw new NotSupportedException($"想定していない RPC が出た: {method.Name}"),
            };
            return new AsyncUnaryCall<TResponse>(Task.FromResult((TResponse)response), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
        }

        private GetResponse Retrieve(GetPoints request)
        {
            Gets.Add(request);
            var response = new GetResponse();
            if (StoredForGet is { } p)
                response.Result.Add(new RetrievedPoint { Id = p.Id, Payload = { p.Payload } });
            return response;
        }

        private CollectionOperationResponse Create(CreateCollection request)
        {
            Created.Add(request);
            MissingCollection = null;
            return new CollectionOperationResponse { Result = true };
        }

        private static AsyncUnaryCall<TResponse> Fail<TResponse>(string name) =>
            new(Task.FromException<TResponse>(new RpcException(new Status(StatusCode.NotFound,
                    $"Not found: Collection `{name}` doesn't exist!"))),
                Task.FromResult(new Metadata()), () => new Status(StatusCode.NotFound, ""), () => [], () => { });

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
