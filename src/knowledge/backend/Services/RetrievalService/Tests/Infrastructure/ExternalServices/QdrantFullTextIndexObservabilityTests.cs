using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RetrievalService.Common.Observability;
using RetrievalService.Infrastructure.ExternalServices;

namespace RetrievalService.Tests.Infrastructure.ExternalServices;

// FR-03, UC-01, NFR-06, #1116, [[IADR-0318]] 決定 3:
// **キーワード検索が全文検索として機能していないことを、応答の外側から観測できること。**
//
// 🔴 本 issue の欠陥は「壊れているのに 200 が返る」形である。#972 / #992 が同型の穴
// （`200 ＋ 空`）を潰した先例に倣い、**応答の契約は 1 バイトも変えず**（存在秘匿・
// [[IADR-0313]] 決定 1 が案 3 を退けた）、readiness とメトリクスで観測できるようにする。
//
// 🔴 **索引が無いことは例外にならない**（Qdrant v1.18.1 は部分文字列の全走査へ黙って落ちる。実機で実測）。
// だから「例外を数える」だけでは足りず、**索引の存在そのもの**を見る health check が要る。
[Trait("TestKind", "Unit")]
public class QdrantFullTextIndexObservabilityTests
{
    private const string Collection = "knowledge_chunks_test";

    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Qdrant:CollectionName"] = Collection
            })
            .Build();

    // FR-03, #1116: 索引が在れば Healthy。
    [Fact]
    public async Task HealthCheck_IsHealthy_WhenTextIndexExists()
    {
        var check = NewCheck(new FakeCallInvoker { TextIndexExists = true }, out _);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    // 🔴 FR-03, #1116: **索引が無ければ Degraded。** これが本 issue の運用上の検出点である。
    [Fact]
    public async Task HealthCheck_IsDegraded_WhenTextIndexIsMissing()
    {
        var check = NewCheck(new FakeCallInvoker { TextIndexExists = false }, out var metrics);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain(Collection);
        metrics.Measurements(KeywordSearchMetrics.DegradedCounterName)
            .Should().ContainSingle()
            .Which.Reason.Should().Be(KeywordSearchMetrics.MissingIndexReason);
    }

    // FR-03, NFR-06, #1116: **Unhealthy にしない。**
    // ベクトル側は生きており、キーワード側の欠落で検索全体を落とすのは
    // 計画 NFR-06（障害時の縮退運転: 検索は継続）に反する。
    // Degraded なら `/health/ready` は 200 のままで、pod は Ready から外れない。
    [Fact]
    public async Task HealthCheck_NeverReportsUnhealthy()
    {
        foreach (var exists in new[] { true, false })
        {
            var check = NewCheck(new FakeCallInvoker { TextIndexExists = exists }, out _);
            var result = await check.CheckHealthAsync(
                new HealthCheckContext(), TestContext.Current.CancellationToken);
            result.Status.Should().NotBe(HealthStatus.Unhealthy);
        }
    }

    // FR-03, #1116: Qdrant が答えないときは「判定できない」＝ Degraded（到達性は別の check が見る）。
    [Fact]
    public async Task HealthCheck_IsDegraded_WhenQdrantRejectsTheCall()
    {
        var check = NewCheck(new FakeCallInvoker { ThrowOnGet = true }, out _);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Degraded);
    }

    // 🔴 FR-03, #1116: **縮退をログ 1 行で終わらせない。** 全文検索が拒まれたら必ず数える。
    [Fact]
    public async Task KeywordSearch_RecordsDegradation_WhenQdrantRejectsTheQuery()
    {
        var recorder = new MetricRecorder();
        var store = new QdrantVectorStore(
            new QdrantClient(new QdrantGrpcClient(new FakeCallInvoker { ThrowOnScroll = true })),
            Config(), NullLogger<QdrantVectorStore>.Instance, recorder.Metrics);

        var results = await store.KeywordSearchAsync(
            "検索語", 10, null, TestContext.Current.CancellationToken);

        results.Should().BeEmpty("検索全体は失敗させない（ベクトルのみへ縮退する）");
        recorder.Measurements(KeywordSearchMetrics.DegradedCounterName)
            .Should().ContainSingle()
            .Which.Reason.Should().Be(KeywordSearchMetrics.BackendErrorReason);
    }

    // FR-02, FR-03, #1116: 検索と readiness が**同じコレクション**を指すこと
    // （別々に解決すると「見ていないコレクションの索引を健全と報告する」）。
    [Theory]
    [InlineData("Qdrant:CollectionName", "from-collection-name")]
    [InlineData("Qdrant:Collection", "from-legacy-key")]
    public void ResolveCollectionName_HonoursBothKeys(string key, string value)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

        QdrantVectorStore.ResolveCollectionName(config).Should().Be(value);
    }

    [Fact]
    public void ResolveCollectionName_FallsBackToDefault() =>
        QdrantVectorStore.ResolveCollectionName(new ConfigurationBuilder().Build())
            .Should().Be("knowledge_chunks");

    private static QdrantFullTextIndexHealthCheck NewCheck(
        FakeCallInvoker invoker, out MetricRecorder recorder)
    {
        recorder = new MetricRecorder();
        return new QdrantFullTextIndexHealthCheck(
            new QdrantClient(new QdrantGrpcClient(invoker)), Config(), recorder.Metrics);
    }

    // 計測値を拾う器（`MeterListener` で購読する）。
    private sealed class MetricRecorder
    {
        private readonly List<(string Instrument, string Reason)> _measurements = [];

        internal KeywordSearchMetrics Metrics { get; }

        internal MetricRecorder()
        {
            var factory = new TestMeterFactory();
            Metrics = new KeywordSearchMetrics(factory);

            var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Scope == factory) l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            {
                var reason = "";
                foreach (var t in tags)
                    if (t.Key == KeywordSearchMetrics.ReasonTag) reason = t.Value?.ToString() ?? "";
                lock (_measurements) _measurements.Add((instrument.Name, reason));
            });
            listener.Start();
        }

        internal IReadOnlyList<(string Instrument, string Reason)> Measurements(string name)
        {
            lock (_measurements) return [.. _measurements.Where(m => m.Instrument == name)];
        }
    }

    // 実機 Qdrant なしで応答を決める器（IngestionService.Tests の同型を検索側にも置く）。
    private sealed class FakeCallInvoker : CallInvoker
    {
        internal bool TextIndexExists { get; init; }
        internal bool ThrowOnGet { get; init; }
        internal bool ThrowOnScroll { get; init; }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var response = (TResponse)Respond(method.Name);
            return new AsyncUnaryCall<TResponse>(
                Task.FromResult(response), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => [], () => { });
        }

        private object Respond(string methodName)
        {
            switch (methodName)
            {
                case "Get" when ThrowOnGet:
                    throw new RpcException(new Status(StatusCode.NotFound, "collection not found"));
                case "Get":
                    var info = new CollectionInfo();
                    if (TextIndexExists)
                    {
                        info.PayloadSchema.Add(QdrantVectorStore.FullTextKey,
                            new PayloadSchemaInfo { DataType = PayloadSchemaType.Text });
                    }
                    return new GetCollectionInfoResponse { Result = info };

                case "Scroll" when ThrowOnScroll:
                    throw new RpcException(new Status(StatusCode.InvalidArgument,
                        "Index required but not found for \"text\""));
                case "Scroll":
                    return new ScrollResponse();

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
