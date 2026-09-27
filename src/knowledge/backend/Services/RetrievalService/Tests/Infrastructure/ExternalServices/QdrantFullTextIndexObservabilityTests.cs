using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RetrievalService.Common.Observability;
using RetrievalService.Infrastructure.ExternalServices;
using RetrievalService.Tests.Grpc;

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

    // ── 呼び出し元の取り消し（#1646。本物のチャネル） ─────────────────────────────

    // 前提の表明: Qdrant の公式クライアントも、本物のチャネルでは呼び出し元の取り消しを `RpcException(Cancelled)` で投げる。
    // これが崩れる（OCE を投げる）と、下の取り消しの試験は `catch (RpcException)` の前の守りを測らなくなる。
    [Fact]
    public async Task 前提_Qdrantの公式クライアントは本物のチャネルで取り消しを_RpcException_Cancelled_で投げる()
    {
        var qdrant = new FakeQdrantServer(FakeQdrantBehavior.Hang);
        await using var server = await LoopbackGrpcServer.StartRawAsync(qdrant.HandleAsync, Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var call = new QdrantClient(new QdrantGrpcClient(server.Channel.CreateCallInvoker()))
            .ScrollAsync(Collection, limit: 1, cancellationToken: cts.Token);
        await qdrant.Received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cts.CancelAsync();

        var thrown = await FluentActions.Awaiting(() => call).Should().ThrowAsync<RpcException>();
        thrown.Which.StatusCode.Should().Be(StatusCode.Cancelled);
    }

    // 🔴 NFR-06, #1646: **打ち切られた要求を「キーワード検索の縮退」として数えない。**
    // 127.0.0.1 の偽の Qdrant が要求を受け取ってから呼び出し元が取り消す。外へ出るのは呼び出し元の token を持つ OCE で、
    // 計器（`RecordDegraded`）は 1 件も増えず、縮退の警告も出ない。
    [Fact]
    public async Task KeywordSearch_呼び出し元の取り消しは縮退として数えず外へ出す()
    {
        var qdrant = new FakeQdrantServer(FakeQdrantBehavior.Hang);
        await using var server = await LoopbackGrpcServer.StartRawAsync(qdrant.HandleAsync, Ct);
        var recorder = new MetricRecorder();
        var logger = new RecordingLogger<QdrantVectorStore>();
        var store = new QdrantVectorStore(
            new QdrantClient(new QdrantGrpcClient(server.Channel.CreateCallInvoker())), Config(), logger, recorder.Metrics);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var call = store.KeywordSearchAsync("検索語", 10, null, cts.Token);
        await qdrant.Received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cts.CancelAsync();

        var thrown = await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.CancellationToken.Should().Be(cts.Token, "呼び出し元の取り消しとして外へ出す");
        recorder.Measurements(KeywordSearchMetrics.DegradedCounterName)
            .Should().BeEmpty("取り消しはキーワード検索の縮退ではない（計器を汚さない）");
        logger.OfLevel(LogLevel.Warning).Should().BeEmpty();
    }

    // 🔴 #1646 の対: **呼び出し元が取り消していない `CANCELLED`（Qdrant が返したもの）は従来どおり縮退として数える。**
    // status で判定する変異（`when (ex.StatusCode == StatusCode.Cancelled)`）はここで赤になる。
    [Fact]
    public async Task KeywordSearch_Qdrantが返した_Cancelled_は縮退として数える()
    {
        var qdrant = new FakeQdrantServer(FakeQdrantBehavior.ReturnCancelled);
        await using var server = await LoopbackGrpcServer.StartRawAsync(qdrant.HandleAsync, Ct);
        var recorder = new MetricRecorder();
        var logger = new RecordingLogger<QdrantVectorStore>();
        var store = new QdrantVectorStore(
            new QdrantClient(new QdrantGrpcClient(server.Channel.CreateCallInvoker())), Config(), logger, recorder.Metrics);

        var results = await store.KeywordSearchAsync("検索語", 10, null, Ct);

        results.Should().BeEmpty();
        recorder.Measurements(KeywordSearchMetrics.DegradedCounterName)
            .Should().ContainSingle("★ 陽性対照 —— 縮退の枝は計器を 1 件積む")
            .Which.Reason.Should().Be(KeywordSearchMetrics.BackendErrorReason);
        logger.OfLevel(LogLevel.Warning).Should().ContainSingle();
    }

    // 🔴 #1646: 索引の健全性の検査 2 つも、**検査の打ち切りを `Degraded` へ畳まない**。
    [Theory]
    [InlineData(QdrantFullTextIndexHealthCheck.Name)]
    [InlineData(QdrantCjkNgramIndexHealthCheck.Name)]
    public async Task HealthCheck_呼び出し元の取り消しは_Degraded_へ畳まず外へ出す(string checkName)
    {
        var qdrant = new FakeQdrantServer(FakeQdrantBehavior.Hang);
        await using var server = await LoopbackGrpcServer.StartRawAsync(qdrant.HandleAsync, Ct);
        var check = HealthCheckOver(checkName, server, out var recorder);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var call = check.CheckHealthAsync(new HealthCheckContext(), cts.Token);
        await qdrant.Received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cts.CancelAsync();

        var thrown = await FluentActions.Awaiting(() => call).Should().ThrowAsync<OperationCanceledException>();
        thrown.Which.CancellationToken.Should().Be(cts.Token);
        recorder.Measurements(KeywordSearchMetrics.DegradedCounterName).Should().BeEmpty();
    }

    // 対: Qdrant が返した `CANCELLED` は従来どおり「判定できない」＝ Degraded。
    [Theory]
    [InlineData(QdrantFullTextIndexHealthCheck.Name)]
    [InlineData(QdrantCjkNgramIndexHealthCheck.Name)]
    public async Task HealthCheck_Qdrantが返した_Cancelled_は_Degraded_である(string checkName)
    {
        var qdrant = new FakeQdrantServer(FakeQdrantBehavior.ReturnCancelled);
        await using var server = await LoopbackGrpcServer.StartRawAsync(qdrant.HandleAsync, Ct);
        var check = HealthCheckOver(checkName, server, out _);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Exception.Should().BeOfType<RpcException>()
            .Which.StatusCode.Should().Be(StatusCode.Cancelled, "★ 陽性対照 —— 偽の Qdrant が実際に CANCELLED を返した");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IHealthCheck HealthCheckOver(string name, LoopbackGrpcServer server, out MetricRecorder recorder)
    {
        recorder = new MetricRecorder();
        var client = new QdrantClient(new QdrantGrpcClient(server.Channel.CreateCallInvoker()));
        return name == QdrantFullTextIndexHealthCheck.Name
            ? new QdrantFullTextIndexHealthCheck(client, Config(), recorder.Metrics)
            : new QdrantCjkNgramIndexHealthCheck(client, Config(), recorder.Metrics);
    }

    private enum FakeQdrantBehavior { Hang, ReturnCancelled }

    // 127.0.0.1 に載せる偽の Qdrant（#1646）。公式クライアントはサーバー側の `*Base` を同梱しないので、
    // gRPC の枠を HTTP/2 の素の要求として受ける。`Hang` は要求を受け取ったことを知らせてから取り消されるまで待ち、
    // `ReturnCancelled` は trailers-only の応答で `CANCELLED` を返す（呼び出し元の取り消しではない対照）。
    private sealed class FakeQdrantServer(FakeQdrantBehavior behavior)
    {
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task HandleAsync(HttpContext context)
        {
            Received.TrySetResult();
            if (behavior == FakeQdrantBehavior.ReturnCancelled)
            {
                context.Response.ContentType = "application/grpc";
                context.Response.Headers["grpc-status"] = ((int)StatusCode.Cancelled).ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                context.Response.Headers["grpc-message"] = "fake-qdrant-cancelled";
                return;
            }

            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        }
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
