using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RetrievalService.Common.Observability;
using RetrievalService.Infrastructure.ExternalServices;

namespace RetrievalService.Tests.Infrastructure.ExternalServices;

// FR-04, FR-05, SC-01, SC-08, [[IADR-0502]] 決定 5 (#1760):
// **facet するキーに Qdrant のキーワード索引が無いとき、そのコレクションの候補は空集合である。**
//
// 取り込みは書く点の属性キーへ必ず索引を張るので、索引の無いキー ＝ どの点も持たないキーである
// （例: 軸 `project` を 1 文書も持たない配備）。空集合へ倒さないと、その軸の候補の照会全体が例外になる。
// 🔴 捕まえるのは「索引が無い」だけ。不正なキー（同じ `InvalidArgument`）と Qdrant の不調は従来どおり上げる。
// 実 Qdrant の状態コードと文言は `Knowledge.IntegrationTests` の `KeywordIndexQdrantTests`（I-12）が測る。
[Trait("TestKind", "Unit")]
public class AttributeValuesMissingIndexTests
{
    private const string MissingIndex =
        "Wrong input: No appropriate index for faceting: `attributes.project`. Please create one to facet on this field.";

    private static QdrantVectorStore Store(CallInvoker invoker) =>
        QdrantVectorStore.ForCollection(new QdrantClient(new QdrantGrpcClient(invoker)), "knowledge_chunks_voyage_3_5",
            NullLogger<QdrantVectorStore>.Instance, new KeywordSearchMetrics(new TestMeterFactory()));

    // T-73: 索引が無いキーの facet は空集合（例外にしない）。
    [Fact]
    public async Task MissingFacetIndex_ReturnsNoValues()
    {
        var values = await Store(new FailingInvoker(StatusCode.InvalidArgument, MissingIndex))
            .ListAttributeValuesAsync("attributes.project", null, TestContext.Current.CancellationToken);

        values.Should().BeEmpty();
    }

    // T-73（陽性対照）: 同じ `InvalidArgument` でも、不正なキーは従来どおり上げる。Qdrant の不調も上げる。
    [Theory]
    [InlineData(StatusCode.InvalidArgument, "Wrong input: Invalid json path: 'attributes.my key'")]
    [InlineData(StatusCode.Unavailable, "No appropriate index for faceting")]
    [InlineData(StatusCode.Internal, "simulated")]
    public async Task OtherFacetFailures_StillThrow(StatusCode code, string detail)
    {
        var act = () => Store(new FailingInvoker(code, detail))
            .ListAttributeValuesAsync("attributes.project", null, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(code);
    }

    // T-73: 判定の形（純関数）。
    [Fact]
    public void IsMissingFacetIndex_RequiresBothStatusAndMessage()
    {
        QdrantVectorStore.IsMissingFacetIndex(new RpcException(new Status(StatusCode.InvalidArgument, MissingIndex)))
            .Should().BeTrue();
        QdrantVectorStore.IsMissingFacetIndex(new RpcException(new Status(StatusCode.InvalidArgument, "Invalid json path")))
            .Should().BeFalse();
        QdrantVectorStore.IsMissingFacetIndex(new RpcException(new Status(StatusCode.Unknown, MissingIndex)))
            .Should().BeFalse();
    }

    // T-73: 索引があれば値を返す（件数は捨てる。陽性対照）。
    [Fact]
    public async Task FacetWithIndex_ReturnsSortedValuesWithoutCounts()
    {
        var response = new FacetResponse();
        response.Hits.Add(new FacetHit { Value = new FacetValue { StringValue = "beta" }, Count = 3 });
        response.Hits.Add(new FacetHit { Value = new FacetValue { StringValue = "alpha" }, Count = 9 });

        var values = await Store(new RespondingInvoker(response))
            .ListAttributeValuesAsync("attributes.project", null, TestContext.Current.CancellationToken);

        values.Should().Equal(["alpha", "beta"]);
    }

    private sealed class FailingInvoker(StatusCode code, string detail) : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            method.Name == "Facet"
                ? new(Task.FromException<TResponse>(new RpcException(new Status(code, detail))),
                    Task.FromResult(new Metadata()), () => new Status(code, detail), () => [], () => { })
                : throw new NotSupportedException($"想定していない RPC が出た: {method.Name}");

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

    private sealed class RespondingInvoker(FacetResponse response) : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            method.Name == "Facet"
                ? new(Task.FromResult((TResponse)(object)response), Task.FromResult(new Metadata()),
                    () => Status.DefaultSuccess, () => [], () => { })
                : throw new NotSupportedException($"想定していない RPC が出た: {method.Name}");

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
