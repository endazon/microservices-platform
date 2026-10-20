using AwesomeAssertions;
using Grpc.Core;
using IngestionService.Domain.Ports;
using IngestionService.Infrastructure.ExternalServices;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace IngestionService.Tests.Infrastructure.ExternalServices;

// FR-06, SC-05, [[IADR-0565]] 決定 2 (#1993): 索引の状態の材料（どの文書が点を持つか）を、**コレクションごとの facet 1 回**で引く。
//
// 実 Qdrant での往復（facet が `document_id` の索引で通ること・3 値に分かれること）は
// `Knowledge.IntegrationTests` の `IndexStatusQdrantTests` が測る。ここでは要求の形（回数・フィルタ）と応答の読み方を固定する。
public class QdrantIndexPresenceTests
{
    private const string Voyage = "knowledge_chunks_voyage_3_5";
    private const string Ruri = "knowledge_chunks_ruri_v3";
    private const string Lexical = "knowledge_chunks_lexical";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    // A4（N+1 にしない）: 問い合わせはコレクションごとに 1 回（語彙索引は印あり・なしの 2 回）で、ID の数に依らない。
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task 問い合わせの回数はIDの数に依らない(int count)
    {
        var invoker = new FacetInvoker();
        var ids = Enumerable.Range(1, count).Select(Id).ToList();

        await NewStore(invoker).GetIndexPresenceAsync(ids, Ct);

        invoker.Facets.Select(f => f.CollectionName).Should().Equal([Voyage, Ruri, Lexical, Lexical]);
        invoker.Facets.Should().OnlyContain(f => f.Key == "document_id" && f.Exact && f.Limit == (ulong)count);
        // どの問い合わせも渡した ID に絞る（ページの外の文書を数えない）。
        invoker.Facets.Should().OnlyContain(f => f.Filter.Must.Any(c =>
            c.Field.Key == "document_id" && c.Field.Match.Keywords.Strings.SequenceEqual(ids.Select(i => i.ToString()))));
    }

    // 語彙索引の 2 回は印あり（must）と印なし（must_not）に分かれる。ベクトルのコレクションは印で絞らない。
    [Fact]
    public async Task 語彙索引は埋め込み待ちの印で2つに分ける()
    {
        var invoker = new FacetInvoker();

        await NewStore(invoker).GetIndexPresenceAsync([Id(1)], Ct);

        static bool IsPendingMark(Condition c) => c.Field?.Key == "embedding_pending" && c.Field.Match.Boolean;
        invoker.Facets[0].Filter.Must.Any(IsPendingMark).Should().BeFalse();
        invoker.Facets[0].Filter.MustNot.Should().BeEmpty();
        invoker.Facets[2].Filter.Must.Any(IsPendingMark).Should().BeTrue("1 回目は印あり");
        invoker.Facets[3].Filter.MustNot.Any(IsPendingMark).Should().BeTrue("2 回目は印なし");
        invoker.Facets[3].Filter.Must.Any(IsPendingMark).Should().BeFalse();
    }

    // A1: facet の値を集合へ読む。ベクトルのコレクション・語彙索引（印なし）は索引済み、語彙索引（印あり）は埋め込み待ち。
    [Fact]
    public async Task facetの値を索引済みと埋め込み待ちの集合へ読む()
    {
        var invoker = new FacetInvoker
        {
            Hits =
            {
                [(Voyage, false)] = [Id(1)],
                [(Ruri, false)] = [Id(2)],
                [(Lexical, true)] = [Id(3)],
                [(Lexical, false)] = [Id(4)],
            },
        };

        var presence = await NewStore(invoker).GetIndexPresenceAsync([Id(1), Id(2), Id(3), Id(4), Id(5)], Ct);

        presence.Indexed.Should().BeEquivalentTo([Id(1), Id(2), Id(4)]);
        presence.EmbeddingPending.Should().BeEquivalentTo([Id(3)]);
    }

    [Fact]
    public async Task 空の集合はQdrantを呼ばない()
    {
        var invoker = new FacetInvoker();

        var presence = await NewStore(invoker).GetIndexPresenceAsync([], Ct);

        invoker.Facets.Should().BeEmpty();
        presence.Indexed.Should().BeEmpty();
    }

    // 🔴 「引けなかった」は例外で上げる（空の集合＝未索引へ畳まない）。語彙索引が無い（NotFound）だけは「点が無い」。
    [Fact]
    public async Task ベクトルのコレクションが引けなければ例外を上げる()
    {
        var invoker = new FacetInvoker { Fail = { [Voyage] = StatusCode.Unavailable } };

        var act = () => NewStore(invoker).GetIndexPresenceAsync([Id(1)], Ct);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unavailable);
    }

    [Fact]
    public async Task 語彙索引が無いときはそこに点が無いとして読む()
    {
        var invoker = new FacetInvoker
        {
            Fail = { [Lexical] = StatusCode.NotFound },
            Hits = { [(Voyage, false)] = [Id(1)] },
        };

        var presence = await NewStore(invoker).GetIndexPresenceAsync([Id(1), Id(2)], Ct);

        presence.Indexed.Should().BeEquivalentTo([Id(1)]);
        presence.EmbeddingPending.Should().BeEmpty();
    }

    // [[IADR-0565]] 決定 2: facet が要る `document_id` のキーワード索引を、全コレクション（モデル別 ＋ 語彙索引）へ張る。
    [Fact]
    public async Task document_idのキーワード索引を全コレクションへ張る()
    {
        var invoker = new FacetInvoker();

        await NewStore(invoker).EnsureDocumentIdIndexAsync(Ct);

        invoker.FieldIndexes.Select(x => (x.CollectionName, x.FieldName)).Should().Equal(
            [(Voyage, "document_id"), (Ruri, "document_id"), (Lexical, "document_id")]);
        invoker.FieldIndexes.Should().OnlyContain(x => x.FieldType == FieldType.Keyword
            && x.FieldIndexParams.Equals(QdrantIngestionVectorStore.BuildKeywordIndexParams()));
    }

    private static QdrantIngestionVectorStore NewStore(CallInvoker invoker) =>
        new(new QdrantClient(new QdrantGrpcClient(invoker)),
            Options.Create(new EmbeddingCollectionsOptions
            {
                Collections =
                [
                    new EmbeddingCollectionOptions { Name = Voyage, VectorSize = 1024 },
                    new EmbeddingCollectionOptions { Name = Ruri, VectorSize = 768 },
                ]
            }));

    // facet と索引の作成だけを受ける器。応答は（コレクション, 印ありの問い合わせか）ごとの文書 ID。
    private sealed class FacetInvoker : CallInvoker
    {
        internal List<FacetCounts> Facets { get; } = [];
        internal List<CreateFieldIndexCollection> FieldIndexes { get; } = [];
        internal Dictionary<(string Collection, bool Pending), List<Guid>> Hits { get; } = [];
        internal Dictionary<string, StatusCode> Fail { get; } = [];

        private object Respond(object request)
        {
            switch (request)
            {
                case FacetCounts facet:
                    Facets.Add(facet);
                    if (Fail.TryGetValue(facet.CollectionName, out var code))
                        throw new RpcException(new Status(code, "simulated"));
                    var pending = facet.Filter.Must.Any(c => c.Field?.Key == "embedding_pending");
                    var response = new FacetResponse();
                    foreach (var id in Hits.GetValueOrDefault((facet.CollectionName, pending), []))
                        response.Hits.Add(new FacetHit { Value = new FacetValue { StringValue = id.ToString() }, Count = 3 });
                    return response;
                case CreateFieldIndexCollection index:
                    FieldIndexes.Add(index);
                    return new PointsOperationResponse { Result = new UpdateResult { Status = UpdateStatus.Completed } };
                default:
                    throw new NotSupportedException($"想定していない RPC: {request.GetType().Name}");
            }
        }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            Task<TResponse> task;
            try
            {
                task = Task.FromResult((TResponse)Respond(request!));
            }
            catch (RpcException ex)
            {
                task = Task.FromException<TResponse>(ex);
            }
            return new AsyncUnaryCall<TResponse>(task, Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
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
