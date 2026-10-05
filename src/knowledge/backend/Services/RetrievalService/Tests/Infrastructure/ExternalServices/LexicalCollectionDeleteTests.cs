using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RetrievalService.Common.Observability;
using RetrievalService.Infrastructure.ExternalServices;

namespace RetrievalService.Tests.Infrastructure.ExternalServices;

// FR-06, ADR-0057 決定 1, ADR-0127 決定 1, [[IADR-0497]] 決定 4 (#1746 監査 🟡3):
// **語彙索引のコレクションがまだ無いとき、文書削除は no-op である**（取り込みが起動時に作る前・ブートストラップの失敗）。
// `NotFound` で失敗させると削除の購読が全件再試行・デッドレターへ回る。無いコレクションには消す点も無い。
// 🔴 主・ベクトルの追加コレクションは従来どおり例外を上げる（陽性対照）。他の失敗も上げる。
[Trait("TestKind", "Unit")]
public class LexicalCollectionDeleteTests
{
    private static QdrantVectorStore Store(CallInvoker invoker, bool missingCollectionIsEmpty) =>
        QdrantVectorStore.ForCollection(new QdrantClient(new QdrantGrpcClient(invoker)), "knowledge_chunks_lexical",
            NullLogger<QdrantVectorStore>.Instance, new KeywordSearchMetrics(new TestMeterFactory()),
            missingCollectionIsEmpty);

    // T-97（監査 🟡3）: 語彙索引の NotFound は no-op。
    [Fact]
    public async Task 語彙索引が無ければ削除はno_opになる()
    {
        var act = () => Store(new FailingInvoker(StatusCode.NotFound), missingCollectionIsEmpty: true)
            .DeleteByDocumentAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
    }

    // 陽性対照: 主（既定の false）の NotFound は従来どおり例外。
    [Fact]
    public async Task 主のコレクションが無ければ従来どおり例外になる()
    {
        var act = () => Store(new FailingInvoker(StatusCode.NotFound), missingCollectionIsEmpty: false)
            .DeleteByDocumentAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    // 陽性対照: 語彙索引でも NotFound 以外（Qdrant の不調）は上げる（再試行へ委ねる）。
    [Fact]
    public async Task 語彙索引でもNotFound以外の失敗は例外になる()
    {
        var act = () => Store(new FailingInvoker(StatusCode.Unavailable), missingCollectionIsEmpty: true)
            .DeleteByDocumentAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unavailable);
    }

    private sealed class FailingInvoker(StatusCode code) : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            new(Task.FromException<TResponse>(new RpcException(new Status(code, "simulated"))),
                Task.FromResult(new Metadata()), () => new Status(code, ""), () => [], () => { });

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
