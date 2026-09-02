using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Grpc.Core;
using Knowledge.Contracts.Indexing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RetrievalService.Common.Observability;
using RetrievalService.Infrastructure.ExternalServices;

namespace RetrievalService.Tests.Infrastructure.ExternalServices;

// FR-03, UC-01, NFR-06, ADR-0009, #1118, [[IADR-0339]] 決定 1・3:
// **日本語のクエリは 2-gram にして `text_ngram` へ、識別子は `text` へ Match すること**と、
// **`text_ngram` の索引の欠落を readiness で観測できること**。
//
// 🔴 #1117 の獲得物（識別子・型番・略語）を落とさないことがここで固定される ——
// 識別子の系統は `text`（`multilingual`）のまま、条件を 1 つも変えない。
public class QdrantCjkNgramSearchTests
{
    private const string Collection = "knowledge_chunks_test";

    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Qdrant:CollectionName"] = Collection
            })
            .Build();

    // 決定 1: 識別子と日本語が混じるクエリは 2 条件（両方 must）。
    [Fact]
    public void BuildFullTextConditions_SplitsIdentifiersAndJapanese()
    {
        var conditions = QdrantVectorStore.BuildFullTextConditions("msp-searchseed-tanpopo 検索導線");

        conditions.Should().HaveCount(2);
        conditions[0].Field.Key.Should().Be(QdrantVectorStore.FullTextKey);
        conditions[0].Field.Match.Text.Should().Be("msp-searchseed-tanpopo");
        conditions[1].Field.Key.Should().Be(CjkBigramPayload.PayloadKey);
        conditions[1].Field.Match.Text.Should().Be("検索 索導 導線");
    }

    // 決定 1: 日本語だけのクエリは `text` へ投げない（`multilingual` は日本語を分かち書きしないのでほぼ当たらない）。
    [Fact]
    public void BuildFullTextConditions_JapaneseOnly_TargetsOnlyTheNgramPayload()
    {
        var conditions = QdrantVectorStore.BuildFullTextConditions("横断検索");

        var only = conditions.Should().ContainSingle().Which;
        only.Field.Key.Should().Be(CjkBigramPayload.PayloadKey);
        only.Field.Match.Text.Should().Be("横断 断検 検索");
    }

    // 🔴 受け入れ基準 3: 識別子だけのクエリは #1117 と**同じ 1 条件**（`text` へそのまま）。
    [Theory]
    [InlineData("tanpopo searchseed msp")]
    [InlineData("RX-7800X3D")]
    [InlineData("ABAC")]
    public void BuildFullTextConditions_IdentifierOnly_IsUnchangedFromTheMultilingualPath(string query)
    {
        var conditions = QdrantVectorStore.BuildFullTextConditions(query);

        var only = conditions.Should().ContainSingle().Which;
        only.Field.Key.Should().Be(QdrantVectorStore.FullTextKey);
        only.Field.Match.Text.Should().Be(query);
    }

    // 決定 1: 分割した条件がそのまま Scroll のフィルタへ載る（ABAC 条件と同じ `must` の中）。
    [Fact]
    public async Task KeywordSearch_SendsBothConditionsToQdrant()
    {
        var invoker = new FakeCallInvoker { NgramIndexExists = true };
        var store = NewStore(invoker, out _);

        await store.KeywordSearchAsync("IngestionService 本文", 10, null, TestContext.Current.CancellationToken);

        var scroll = invoker.Scrolls.Should().ContainSingle().Which;
        scroll.Filter.Must.Should().Contain(c =>
            c.Field.Key == QdrantVectorStore.FullTextKey && c.Field.Match.Text == "IngestionService");
        scroll.Filter.Must.Should().Contain(c =>
            c.Field.Key == CjkBigramPayload.PayloadKey && c.Field.Match.Text == "本文");
    }

    // 決定 3: 索引が在れば Healthy。
    [Fact]
    public async Task HealthCheck_IsHealthy_WhenNgramIndexExists()
    {
        var check = NewCheck(new FakeCallInvoker { NgramIndexExists = true }, out _);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    // 🔴 決定 3: **`text` の索引が在っても `text_ngram` が無ければ Degraded**（日本語だけが静かに 0 件になる形）。
    [Fact]
    public async Task HealthCheck_IsDegraded_WhenOnlyTheTextIndexExists()
    {
        var check = NewCheck(new FakeCallInvoker { NgramIndexExists = false }, out var recorder);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain(Collection).And.Contain(CjkBigramPayload.PayloadKey);
        recorder.Measurements(KeywordSearchMetrics.DegradedCounterName)
            .Should().ContainSingle()
            .Which.Reason.Should().Be(KeywordSearchMetrics.MissingNgramIndexReason);
    }

    // NFR-06: Unhealthy にしない（ベクトル側も識別子の系統も生きている）。
    [Fact]
    public async Task HealthCheck_NeverReportsUnhealthy()
    {
        foreach (var invoker in new[]
                 {
                     new FakeCallInvoker { NgramIndexExists = true },
                     new FakeCallInvoker { NgramIndexExists = false },
                     new FakeCallInvoker { ThrowOnGet = true },
                 })
        {
            var result = await NewCheck(invoker, out _).CheckHealthAsync(
                new HealthCheckContext(), TestContext.Current.CancellationToken);
            result.Status.Should().NotBe(HealthStatus.Unhealthy);
        }
    }

    private static QdrantVectorStore NewStore(FakeCallInvoker invoker, out MetricRecorder recorder)
    {
        recorder = new MetricRecorder();
        return new QdrantVectorStore(
            new QdrantClient(new QdrantGrpcClient(invoker)),
            Config(), NullLogger<QdrantVectorStore>.Instance, recorder.Metrics);
    }

    private static QdrantCjkNgramIndexHealthCheck NewCheck(FakeCallInvoker invoker, out MetricRecorder recorder)
    {
        recorder = new MetricRecorder();
        return new QdrantCjkNgramIndexHealthCheck(
            new QdrantClient(new QdrantGrpcClient(invoker)), Config(), recorder.Metrics);
    }

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

    // 実機 Qdrant なしで応答を決める器。`text` の索引は常に在るものとし、`text_ngram` だけを切り替える。
    private sealed class FakeCallInvoker : CallInvoker
    {
        internal bool NgramIndexExists { get; init; }
        internal bool ThrowOnGet { get; init; }
        internal List<ScrollPoints> Scrolls { get; } = [];

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
                case "Get" when ThrowOnGet:
                    throw new RpcException(new Status(StatusCode.NotFound, "collection not found"));
                case "Get":
                    var info = new CollectionInfo();
                    info.PayloadSchema.Add(QdrantVectorStore.FullTextKey,
                        new PayloadSchemaInfo { DataType = PayloadSchemaType.Text });
                    if (NgramIndexExists)
                    {
                        info.PayloadSchema.Add(CjkBigramPayload.PayloadKey,
                            new PayloadSchemaInfo { DataType = PayloadSchemaType.Text });
                    }
                    return new GetCollectionInfoResponse { Result = info };

                case "Scroll":
                    Scrolls.Add((ScrollPoints)request);
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
