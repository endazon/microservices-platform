using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Platform.Shared.Infrastructure.Foundation.Middleware;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Middleware;

// NFR-19, ADR-0006, NFR-28, [[IADR-0649]] (#2129): 相関 ID は TraceId に揃え、受けた値は後段への呼び出しへ引き継ぐ。
//
// 従前の CorrelationIdMiddleware は、ヘッダが無ければ毎ホップで新しい GUID を作り、受けた値も後段へ転送しなかった。
// BFF → 後段の 1 ホップで相関 ID が別物になり、「相関 ID で突合する」（ADR-0006）が HTTP の 1 ホップにしか効かなかった。
public class CorrelationIdTests
{
    private static async Task<(string Response, HttpContext Context)> RunAsync(string? header)
    {
        var context = new DefaultHttpContext();
        if (header is not null) context.Request.Headers[CorrelationId.Header] = header;
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask, NullLogger<CorrelationIdMiddleware>.Instance);
        await middleware.InvokeAsync(context);
        return (context.Response.Headers[CorrelationId.Header].ToString(), context);
    }

    [Fact]
    public async Task ヘッダが無ければ現在のトレースIDを相関IDにする()
    {
        using var activity = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();

        var (response, context) = await RunAsync(null);

        response.Should().Be(activity.TraceId.ToHexString(), "後段も同じトレース（traceparent）から同じ値を導けるように TraceId に揃える");
        context.Items[CorrelationId.ItemKey].Should().Be(response);
    }

    [Fact]
    public async Task 受けた相関IDはそのまま使う()
    {
        var (response, context) = await RunAsync("req-1234_abc.DEF:5");

        response.Should().Be("req-1234_abc.DEF:5");
        context.Items[CorrelationId.ItemKey].Should().Be("req-1234_abc.DEF:5");
    }

    // NFR-28: ヘッダは外から来る。ログのスコープと後段への転送に載るので、行構造を壊せる値・長すぎる値は採らない。
    [Theory]
    [InlineData("forged\nAudit: action=x")]
    [InlineData("has space")]
    [InlineData("")]
    public async Task 形の悪い相関IDは採らずトレースIDにする(string header)
    {
        using var activity = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();

        var (response, _) = await RunAsync(header);

        response.Should().Be(activity.TraceId.ToHexString());
    }

    [Fact]
    public async Task 長すぎる相関IDは採らない()
    {
        using var activity = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();

        var (response, _) = await RunAsync(new string('a', 129));

        response.Should().Be(activity.TraceId.ToHexString());
    }

    // ── 後段への転送 ───────────────────────────────────────────

    private sealed class Recorder : HttpMessageHandler
    {
        public string? Seen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request.Headers.TryGetValues(CorrelationId.Header, out var v) ? string.Join(",", v) : null;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    // 本番と同じ拡張（AddPlatformObservability）で組んだ IHttpClientFactory のクライアントで確かめる。
    private static (HttpClient Client, Recorder Recorder, IHttpContextAccessor Accessor, ServiceProvider Sp) Factory()
    {
        var recorder = new Recorder();
        var services = new ServiceCollection();
        services.AddPlatformObservability(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Otlp:Endpoint"] = "http://127.0.0.1:9" }).Build(),
            "correlation-test");
        services.AddHttpClient("Downstream").ConfigurePrimaryHttpMessageHandler(() => recorder);
        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<IHttpClientFactory>().CreateClient("Downstream"), recorder,
            sp.GetRequiredService<IHttpContextAccessor>(), sp);
    }

    [Fact]
    public async Task 受けた相関IDを後段への呼び出しへ引き継ぐ()
    {
        var (client, recorder, accessor, sp) = Factory();
        using var _ = sp;
        var (_, context) = await RunAsync("req-1234");
        accessor.HttpContext = context;

        await client.GetAsync("http://downstream.test/x", TestContext.Current.CancellationToken);

        recorder.Seen.Should().Be("req-1234", "BFF → 後段の 1 ホップで相関 ID が別物になると突合が切れる");
    }

    [Fact]
    public async Task 呼び出し側が付けた相関IDは上書きしない()
    {
        var (client, recorder, accessor, sp) = Factory();
        using var _ = sp;
        var (_, context) = await RunAsync("req-1234");
        accessor.HttpContext = context;

        using var req = new HttpRequestMessage(HttpMethod.Get, "http://downstream.test/x");
        req.Headers.Add(CorrelationId.Header, "explicit");
        await client.SendAsync(req, TestContext.Current.CancellationToken);

        recorder.Seen.Should().Be("explicit");
    }

    [Fact]
    public async Task 要求の外からの呼び出しには付けない()
    {
        var (client, recorder, accessor, sp) = Factory();
        using var _ = sp;
        accessor.HttpContext = null;

        await client.GetAsync("http://downstream.test/x", TestContext.Current.CancellationToken);

        // 要求の外（バックグラウンド・メッセージの処理）では付けない。後段はトレース（traceparent）から同じ値を導く。
        recorder.Seen.Should().BeNull();
    }

    // IADR-0649 残余リスク（#2303 の AI レビュー）: 転送は外部の宛先（LLM の提供元・IdP 等）へも及ぶ。そこで出る値が新しい情報か
    // どうかを実測する。実ソケット越し（既定の primary handler。.NET の DiagnosticsHandler が traceparent を書く）で、
    // 相関 ID を決めずに来た要求の外向き呼び出しに (1) traceparent が載り、(2) X-Correlation-ID がそのトレース ID と同じであること。
    [Fact]
    public async Task 外向きの呼び出しには既定でtraceparentが載り相関IDはそのトレースIDと同じ()
    {
        var seen = new TaskCompletionSource<(string? TraceParent, string? Correlation)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = WebApplication.CreateBuilder();
        server.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = server.Build();
        app.MapGet("/x", (HttpRequest r) =>
        {
            seen.TrySetResult((r.Headers["traceparent"].FirstOrDefault(), r.Headers[CorrelationId.Header].FirstOrDefault()));
            return Results.Ok();
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        var url = app.Urls.First();

        var services = new ServiceCollection();
        services.AddPlatformObservability(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Otlp:Endpoint"] = "http://127.0.0.1:9" }).Build(),
            "correlation-test");
        await using var sp = services.BuildServiceProvider();
        _ = sp.GetRequiredService<OpenTelemetry.Trace.TracerProvider>();

        using var request = new Activity("incoming").SetIdFormat(ActivityIdFormat.W3C).Start();
        var (_, context) = await RunAsync(null); // 相関 ID を持たずに来た要求（既定でトレース ID になる）
        sp.GetRequiredService<IHttpContextAccessor>().HttpContext = context;

        await sp.GetRequiredService<IHttpClientFactory>().CreateClient("External").GetAsync($"{url}/x", TestContext.Current.CancellationToken);
        var (traceParent, correlation) = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        traceParent.Should().NotBeNull("既定の伝播が外向きの要求に traceparent を書く");
        traceParent!.Split('-')[1].Should().Be(request.TraceId.ToHexString());
        correlation.Should().Be(request.TraceId.ToHexString(), "相関 ID は traceparent が既に運ぶトレース ID と同じで、新しい情報を足さない");
    }
}
