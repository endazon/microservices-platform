using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using Grpc.Core;
using Knowledge.Bff.Endpoints.Documents;
using Knowledge.Bff.Endpoints.Search;
using Knowledge.Contracts.Dtos;
using Knowledge.Bff.Endpoints.Usage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Shared.Infrastructure.Foundation.Observability;
using Pb = Knowledge.Contracts.Grpc.Document.V1;
using PbSearch = Knowledge.Contracts.Grpc.Retrieval.V1;

namespace Platform.Bff.Tests;

// FR-03, NFR-06, NFR-19, NFR-28, [[IADR-0603]] (#2104): BFF が後段の不達・時間切れを**黙って畳まない**ことを固定する。
//
// 受け入れ基準:
//   1. 不達・時間切れで WARN と計器 `bff.downstream.failure.total` が 1 件ずつ出る（代表の端点を両ユニットから複数）。
//   2. 検索の不達は 502 であり、「該当なし」（200・0 件）と区別できる。
//   3. 利用者の取り消し（`RequestAborted`）は計器にもログにも数えない。
//   4. 変異（ログ／計器の呼び出しを外す）で 1 が赤になる（各試験がログと計器の両方を 1 件と数えるため）。
//
// 計器は**この試験のホストの `IMeterFactory` が作った Meter だけ**を聴く（他の試験クラスのホストと混ざらない）。
public class BffDownstreamFailureObservabilityTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffDownstreamFailureObservabilityTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.SearchScopeGranted = true;
        _factory.ScopeFilters = [];
        _factory.ScopeBranches = null;
        _factory.WikiStubThrows = false;
        _factory.NotificationStubThrows = false;
        _factory.DocumentStatusCode = HttpStatusCode.OK;
    }

    // ── 1・2: 検索 ──────────────────────────────────────────────

    [Fact]
    public async Task 検索の後段不達は502でありWARNと計器が1件ずつ出る()
    {
        using var host = Host(services => services.AddHttpClient("RetrievalService")
            .ConfigurePrimaryHttpMessageHandler(() => new ThrowingHandler(new HttpRequestException("retrieval unreachable"))));

        var resp = await host.Client.PostAsJsonAsync("/bff/search", new { query = "経費", topK = 5 }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway, "200・0 件へ畳むと利用者には「該当なし」と見える（#2104）");
        (await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain("\"results\"");
        var m = host.SingleMeasurement();
        m.Tags[BffDownstreamFailureMetrics.DownstreamTag].Should().Be("RetrievalService");
        m.Tags[BffDownstreamFailureMetrics.KindTag].Should().Be("unreachable");
        m.Tags[BffDownstreamFailureMetrics.FallbackTag].Should().Be("502");
        ((string)m.Tags[BffDownstreamFailureMetrics.EndpointTag]!).Should().StartWith("/bff/search");
        var warning = host.SingleWarning();
        warning.Should().Contain("RetrievalService").And.Contain(nameof(HttpRequestException)).And.Contain("502")
            .And.Contain("POST /bff/search");
        warning.Should().NotContain("retrieval unreachable", "例外のメッセージは載せない（NFR-28）").And.NotContain("経費", "検索語は載せない");
    }

    [Fact]
    public async Task 検索の後段時間切れは種別timeoutで数える()
    {
        using var host = Host(services => services.AddHttpClient("RetrievalService")
            .ConfigurePrimaryHttpMessageHandler(() => new ThrowingHandler(new TaskCanceledException("HttpClient.Timeout"))));

        var resp = await host.Client.PostAsJsonAsync("/bff/search", new { query = "経費" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        host.SingleMeasurement().Tags[BffDownstreamFailureMetrics.KindTag].Should().Be("timeout");
        host.SingleWarning().Should().Contain("timeout");
    }

    // 対照: 後段が答えた 0 件は 200 のままで、失敗として数えない（区別の反対側）。
    [Fact]
    public async Task 検索の正常な応答では計器もWARNも出ない()
    {
        using var host = Host();

        var resp = await host.Client.PostAsJsonAsync("/bff/search", new { query = "経費" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Measurements.Should().BeEmpty();
        host.Warnings.Should().BeEmpty();
    }

    // ── 1: 他の代表（knowledge の転送・platform の転送・gRPC の文書一覧）────────────

    [Fact]
    public async Task Wikiの後段不達でWARNと計器が1件ずつ出る()
    {
        _factory.WikiStubThrows = true;
        using var host = Host();

        var resp = await host.Client.GetAsync("/bff/wiki/pages", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        host.SingleMeasurement().Tags[BffDownstreamFailureMetrics.DownstreamTag].Should().Be("WikiService");
        host.SingleWarning().Should().Contain("WikiService");
    }

    [Fact]
    public async Task 通知の後段不達でWARNと計器が1件ずつ出る()
    {
        _factory.NotificationStubThrows = true;
        using var host = Host();

        var resp = await host.Client.GetAsync("/bff/notifications", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var m = host.SingleMeasurement();
        m.Tags[BffDownstreamFailureMetrics.DownstreamTag].Should().Be("NotificationService");
        ((string)m.Tags[BffDownstreamFailureMetrics.EndpointTag]!).Should().StartWith("/bff/notifications");
        host.SingleWarning().Should().Contain("NotificationService");
    }

    // 文書の読み取りは従前の WARN（`DocumentReadFailureLog`）を保ち、計器だけ共有の 1 本へ足す。
    [Fact]
    public async Task 文書一覧のgRPC不達は従前のWARNのまま計器へも1件載る()
    {
        var invoker = new FailingInvoker(new RpcException(new Status(StatusCode.Unavailable, "document down")));
        using var host = Host(services => services.AddSingleton(
            new DocumentReadGrpcClient(new Pb.DocumentRead.DocumentReadClient(invoker))));

        var resp = await host.Client.GetAsync("/bff/documents", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var m = host.SingleMeasurement();
        m.Tags[BffDownstreamFailureMetrics.DownstreamTag].Should().Be("DocumentService");
        m.Tags[BffDownstreamFailureMetrics.KindTag].Should().Be("unreachable");
        host.Logs.Where(e => e.Category == "Knowledge.Bff.Endpoints.DocumentBffEndpoints" && e.Level == LogLevel.Warning)
            .Should().ContainSingle();
    }

    // 属性値候補（gRPC 不達）は IADR-0417 決定 9 の縮退（200・空候補）を保ったまま、WARN と計器を出す。
    [Fact]
    public async Task 属性値候補のgRPC不達は200空のままWARNと計器が1件ずつ出る()
    {
        using var host = Host(services => services.AddSingleton(new AttributeValuesGrpcClient(
            new PbSearch.AttributeValues.AttributeValuesClient(
                new FailingInvoker(new RpcException(new Status(StatusCode.Unavailable, "retrieval down")))))));

        var resp = await host.Client.PostAsJsonAsync("/bff/attribute-values", new { key = "tags" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "縮退は維持する（IADR-0417 決定 9）");
        (await resp.Content.ReadFromJsonAsync<AttributeValuesResponse>(TestContext.Current.CancellationToken))!
            .Values.Should().BeEmpty();
        var m = host.SingleMeasurement();
        m.Tags[BffDownstreamFailureMetrics.DownstreamTag].Should().Be("RetrievalService");
        m.Tags[BffDownstreamFailureMetrics.KindTag].Should().Be("unreachable");
        m.Tags[BffDownstreamFailureMetrics.FallbackTag].Should().Be("empty");
        ((string)m.Tags[BffDownstreamFailureMetrics.EndpointTag]!).Should().StartWith("/bff/attribute-values");
        host.SingleWarning().Should().Contain("RetrievalService").And.NotContain("retrieval down");
    }

    // 述語（`IsRetrievalUnreachable`）が通す `InvalidOperationException` は s2s トークンの取得失敗であり、呼び出し側が種別を明示する。
    [Fact]
    public async Task 属性値候補のトークン取得失敗は種別credentialで数える()
    {
        using var host = Host(services => services.AddSingleton(new AttributeValuesGrpcClient(
            new PbSearch.AttributeValues.AttributeValuesClient(
                new FailingInvoker(new InvalidOperationException("ServiceToken 未設定"))))));

        var resp = await host.Client.PostAsJsonAsync("/bff/attribute-values", new { key = "tags" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        host.SingleMeasurement().Tags[BffDownstreamFailureMetrics.KindTag].Should().Be("credential");
    }

    // 🔴 報告口は `InvalidOperationException` を型だけで `credential` と推さない（無関係な呼び出し元の誤分類を防ぐ）。
    // 要求の HTTP メソッドは既知の定数へ写してからログへ載せる（CWE-117）。
    [Fact]
    public void 種別を明示しないInvalidOperationExceptionはerrorでありメソッドは定数へ写す()
    {
        var (provider, sink) = Services();
        using var listener = Listen(provider.GetRequiredService<IMeterFactory>(), out var measurements);
        var http = new DefaultHttpContext { RequestServices = provider };
        http.Request.Method = "GET\r\nforged";

        BffDownstreamFailure.Report(http, "X", new InvalidOperationException(), BffDownstreamFailure.Fallbacks.BadGateway);

        measurements.Should().ContainSingle().Which.Tags[BffDownstreamFailureMetrics.KindTag].Should().Be("error");
        var line = sink.Should().ContainSingle().Which.Message;
        line.Should().Contain("OTHER").And.NotContain("forged");
    }

    // ── 3: 利用者の取り消し ─────────────────────────────────────────

    [Fact]
    public void 利用者の取り消し中は計器にもログにも数えない()
    {
        var (provider, sink) = Services();
        using var listener = Listen(provider.GetRequiredService<IMeterFactory>(), out var measurements);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var aborted = new DefaultHttpContext { RequestServices = provider, RequestAborted = cts.Token };

        BffDownstreamFailure.Report(aborted, "RetrievalService", new TaskCanceledException(), BffDownstreamFailure.Fallbacks.BadGateway);
        BffDownstreamFailure.Count(aborted, "DocumentService", new TaskCanceledException(), BffDownstreamFailure.Fallbacks.BadGateway);

        measurements.Should().BeEmpty("取り消しは後段の失敗ではない");
        sink.Should().BeEmpty();

        // ★ 陽性対照 —— 取り消していない文脈なら同じ呼び出しで 1 件ずつ出る（上の空が配線漏れでないこと）。
        BffDownstreamFailure.Report(new DefaultHttpContext { RequestServices = provider }, "RetrievalService",
            new TaskCanceledException(), BffDownstreamFailure.Fallbacks.BadGateway);
        measurements.Should().ContainSingle();
        sink.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }

    // Meter 名は BFF のサービス名で 1 つ。収集の宣言（`AddMeter(UsageEventMetrics.MeterName)`）がこの計器も拾うことを固定する。
    [Fact]
    public void 計器のMeter名は収集宣言済みのBFFのMeter名と一致する()
        => BffDownstreamFailureMetrics.MeterName.Should().Be(UsageEventMetrics.MeterName);

    // ── 道具 ───────────────────────────────────────────────────

    private TestHost Host(Action<IServiceCollection>? configure = null)
    {
        var logs = new ConcurrentQueue<(LogLevel Level, string Category, string Message)>();
        var app = _factory.WithWebHostBuilder(b =>
        {
            if (configure is not null)
                b.ConfigureServices(configure);
            b.ConfigureLogging(l => l.AddProvider(new CollectingLoggerProvider(logs)));
        });
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        var listener = Listen(app.Services.GetRequiredService<IMeterFactory>(), out var measurements);
        return new TestHost(app, client, listener, measurements, logs);
    }

    private static (ServiceProvider Provider, ConcurrentQueue<(LogLevel Level, string Category, string Message)> Sink) Services()
    {
        var sink = new ConcurrentQueue<(LogLevel Level, string Category, string Message)>();
        var provider = new ServiceCollection()
            .AddLogging(l => l.AddProvider(new CollectingLoggerProvider(sink)))
            .AddMetrics()
            .AddBffDownstreamFailureMetrics()
            .BuildServiceProvider();
        return (provider, sink);
    }

    private static MeterListener Listen(IMeterFactory scope, out ConcurrentQueue<Recorded> measurements)
    {
        var bag = new ConcurrentQueue<Recorded>();
        measurements = bag;
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Scope == scope && instrument.Name == BffDownstreamFailureMetrics.CounterName)
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var dict = new Dictionary<string, object?>();
            foreach (var t in tags)
                dict[t.Key] = t.Value;
            bag.Enqueue(new Recorded(value, dict));
        });
        listener.Start();
        return listener;
    }

    internal sealed record Recorded(long Value, IReadOnlyDictionary<string, object?> Tags);

    private sealed class TestHost(
        WebApplicationFactory<Program> app, HttpClient client, MeterListener listener,
        ConcurrentQueue<Recorded> measurements,
        ConcurrentQueue<(LogLevel Level, string Category, string Message)> logs) : IDisposable
    {
        public HttpClient Client => client;
        public ConcurrentQueue<Recorded> Measurements => measurements;
        public ConcurrentQueue<(LogLevel Level, string Category, string Message)> Logs => logs;

        public IEnumerable<string> Warnings => logs
            .Where(e => e.Category == BffDownstreamFailure.LogCategory)
            .Select(e => e.Message);

        public Recorded SingleMeasurement()
        {
            var m = measurements.Should().ContainSingle("不達 1 回につき計器は 1 件").Which;
            m.Value.Should().Be(1);
            return m;
        }

        public string SingleWarning()
        {
            var e = logs.Where(x => x.Category == BffDownstreamFailure.LogCategory)
                .Should().ContainSingle("不達 1 回につき WARN は 1 行").Which;
            e.Level.Should().Be(LogLevel.Warning);
            return e.Message;
        }

        public void Dispose()
        {
            listener.Dispose();
            client.Dispose();
            app.Dispose();
        }
    }

    private sealed class ThrowingHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(failure);
    }

    private sealed class FailingInvoker(Exception failure) : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
            => new(Task.FromException<TResponse>(failure), Task.FromResult(new Metadata()),
                () => failure is RpcException rpc ? rpc.Status : Status.DefaultSuccess, () => new Metadata(), () => { });

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
            => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
            => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
            => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
            => throw new NotSupportedException();
    }

    private sealed class CollectingLoggerProvider(ConcurrentQueue<(LogLevel, string, string)> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Collector(sink, categoryName);

        public void Dispose()
        {
        }

        private sealed class Collector(ConcurrentQueue<(LogLevel, string, string)> sink, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                sink.Enqueue((logLevel, category, formatter(state, exception) + (exception is null ? string.Empty : " " + exception)));
        }
    }
}
