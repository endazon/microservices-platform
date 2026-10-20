using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using IngestionService.Domain;
using IngestionService.Domain.Ports;
using IngestionService.Features.Indexing.GetIndexStatuses;
using Knowledge.Contracts.Grpc.Ingestion.V1;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace IngestionService.Tests.Features.Indexing.GetIndexStatuses;

// FR-06, FR-02, SC-05, NFR-09, ADR-0127, [[IADR-0379]], [[IADR-0401]], [[IADR-0536]], [[IADR-0565]] (#1993):
// 索引の状態の gRPC 面（`knowledge.ingestion.v1.DocumentIndexStatus/GetStatuses`）。
//
// 受け入れ基準（作業仕様書 20261020_1993_document-list-index-status の写像）:
//   A1 3 値に分かれる / A4 上限（100）を超える要求は拒む・1 回の照会で答える / A5 ServiceCaller の面（本番の Program.cs が張る）。
//   「引けなかった」は UNAVAILABLE（未索引へ畳まない）。
public class DocumentIndexStatusGrpcServiceTests : IClassFixture<IntrospectionEndpointTests.Factory>
{
    private readonly IntrospectionEndpointTests.Factory _factory;

    public DocumentIndexStatusGrpcServiceTests(IntrospectionEndpointTests.Factory factory) => _factory = factory;

    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    private static GetIndexStatusesRequest Request(params Guid[] ids)
    {
        var request = new GetIndexStatusesRequest();
        request.DocumentIds.AddRange(ids.Select(i => i.ToString()));
        return request;
    }

    // ── 分類（純関数） ─────────────────────────────────────

    [Fact]
    public void 埋め込み待ちの印が先で_次に点の有無で決まる()
    {
        var presence = new DocumentIndexPresence(
            Indexed: new HashSet<Guid> { Id(1), Id(2) },
            EmbeddingPending: new HashSet<Guid> { Id(2), Id(3) });

        DocumentIndexStatusRules.Classify(Id(1), presence).Should().Be(IndexState.Indexed);
        // 一部のチャンクだけが埋め込めた文書（主にも語彙索引の印ありにも点がある）は埋め込み待ち。
        DocumentIndexStatusRules.Classify(Id(2), presence).Should().Be(IndexState.EmbeddingPending);
        DocumentIndexStatusRules.Classify(Id(3), presence).Should().Be(IndexState.EmbeddingPending);
        DocumentIndexStatusRules.Classify(Id(4), presence).Should().Be(IndexState.NotIndexed);
    }

    [Fact]
    public void 上限はBFFの1ページの上限と同じ100()
    {
        DocumentIndexStatusRules.MaxDocuments.Should().Be(100);
    }

    // ── 面 ───────────────────────────────────────────────

    [Fact]
    public async Task 要求の順に3値で答え_重複は1つに畳み_照会は1回()
    {
        var store = new FakeStore
        {
            Presence = new DocumentIndexPresence(new HashSet<Guid> { Id(1) }, new HashSet<Guid> { Id(2) }),
        };

        var resp = await Service(store).GetStatuses(Request(Id(3), Id(1), Id(2), Id(1)), new TestCallContext());

        resp.States.Select(s => (s.DocumentId, s.Status)).Should().Equal(
        [
            (Id(3).ToString(), IndexStatus.NotIndexed),
            (Id(1).ToString(), IndexStatus.Indexed),
            (Id(2).ToString(), IndexStatus.EmbeddingPending),
        ]);
        store.Calls.Should().ContainSingle().Which.Should().Equal([Id(3), Id(1), Id(2)]);
    }

    [Fact]
    public async Task 空の要求は索引を呼ばずに空で答える()
    {
        var store = new FakeStore();

        var resp = await Service(store).GetStatuses(new GetIndexStatusesRequest(), new TestCallContext());

        resp.States.Should().BeEmpty();
        store.Calls.Should().BeEmpty();
    }

    // A4: 上限ちょうどは通り、1 件超えると INVALID_ARGUMENT（切り詰めない）。索引は呼ばない。
    [Fact]
    public async Task 上限ちょうどは通り_超えるとINVALID_ARGUMENTで索引を呼ばない()
    {
        var store = new FakeStore();
        var atLimit = Enumerable.Range(1, DocumentIndexStatusRules.MaxDocuments).Select(Id).ToArray();

        (await Service(store).GetStatuses(Request(atLimit), new TestCallContext())).States.Should().HaveCount(100);

        store.Calls.Clear();
        var over = Enumerable.Range(1, DocumentIndexStatusRules.MaxDocuments + 1).Select(Id).ToArray();
        var act = () => Service(store).GetStatuses(Request(over), new TestCallContext());

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        store.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("")]
    public async Task UUIDでないIDはINVALID_ARGUMENTで索引を呼ばない(string raw)
    {
        var store = new FakeStore();
        var request = Request(Id(1));
        request.DocumentIds.Add(raw);

        var act = () => Service(store).GetStatuses(request, new TestCallContext());

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        store.Calls.Should().BeEmpty();
    }

    // 🔴 「引けなかった」は UNAVAILABLE（未索引へ畳まない）。Qdrant の応答の中身は status に載せない。
    [Fact]
    public async Task 索引が引けなければUNAVAILABLEで未索引へ畳まない()
    {
        var store = new FakeStore { Throws = new RpcException(new Status(StatusCode.Internal, "qdrant: secret detail")) };

        var act = () => Service(store).GetStatuses(Request(Id(1)), new TestCallContext());

        var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
        ex.StatusCode.Should().Be(StatusCode.Unavailable);
        ex.Status.Detail.Should().NotContain("secret");
    }

    // A5: 本番の Program.cs が面を `ServiceCaller` 付きで張る。s2s を持たない要求は認可で止まる
    // （UNIMPLEMENTED〔面が無い〕でも INTERNAL〔認可ミドルウェアが無い〕でもない）。
    [Fact]
    public async Task 本番の面はServiceCallerの後ろにある()
    {
        var endpoint = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/knowledge.ingestion.v1.DocumentIndexStatus/GetStatuses");
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy)
            .Should().Contain(PlatformAuthPolicies.ServiceCaller);

        using var channel = GrpcChannel.ForAddress(_factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = _factory.Server.CreateHandler() });
        var client = new DocumentIndexStatus.DocumentIndexStatusClient(channel);
        var act = async () => await client.GetStatusesAsync(Request(Id(1)),
            cancellationToken: TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode
            .Should().BeOneOf(StatusCode.Unauthenticated, StatusCode.PermissionDenied);
    }

    private static DocumentIndexStatusGrpcService Service(FakeStore store) =>
        new(store, NullLogger<DocumentIndexStatusGrpcService>.Instance);

    private sealed class FakeStore : IIngestionVectorStore
    {
        internal List<List<Guid>> Calls { get; } = [];
        internal DocumentIndexPresence Presence { get; init; } = new(new HashSet<Guid>(), new HashSet<Guid>());
        internal Exception? Throws { get; init; }

        public Task<DocumentIndexPresence> GetIndexPresenceAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken ct = default)
        {
            Calls.Add([.. documentIds]);
            return Throws is null ? Task.FromResult(Presence) : Task.FromException<DocumentIndexPresence>(Throws);
        }

        public Task EnsureCollectionsAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task UpsertChunkAsync(string collection, Guid chunkId, Guid documentId, string title, string text,
            int chunkIndex, float[] vector, string? markdownUri, Dictionary<string, string> attributes,
            List<string> tags, DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task UpsertMetadataPointAsync(string collection, Guid pointId, Guid documentId, string title,
            string indexText, float[] vector, string? markdownUri, Dictionary<string, string> attributes,
            List<string> tags, DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task UpsertLexicalChunkAsync(Guid chunkId, Guid documentId, string title, string text,
            int chunkIndex, string? markdownUri, Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task UpsertLexicalMetadataPointAsync(Guid pointId, Guid documentId, string title,
            string indexText, string? markdownUri, Dictionary<string, string> attributes, List<string> tags,
            DateTimeOffset? updatedAt = null, List<string>? sharedWith = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteByDocumentFromAllAsync(Guid documentId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    // 面を直接呼ぶための最小の呼び出し文脈（取り消しだけを持つ）。
    private sealed class TestCallContext : ServerCallContext
    {
        protected override string MethodCore => "/knowledge.ingestion.v1.DocumentIndexStatus/GetStatuses";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "test";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore { get; } = [];
        protected override CancellationToken CancellationTokenCore => TestContext.Current.CancellationToken;
        protected override Metadata ResponseTrailersCore { get; } = [];
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore { get; } = new(null, []);
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
