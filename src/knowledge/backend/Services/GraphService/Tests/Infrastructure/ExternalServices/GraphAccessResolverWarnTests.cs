using System.Net;
using System.Security.Claims;
using AwesomeAssertions;
using GraphService.Domain.Ports;
using GraphService.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GraphService.Tests.Infrastructure.ExternalServices;

// FR-05, FR-17, NFR-09, ADR-0004, ADR-0034, [[IADR-0379]] 決定 5 (#1378):
// REST 経路のスコープ解決が deny-by-default へ縮退した**理由を WARN で出す**ことを固定する。
// 従前は非 2xx・不達・空本文を無言で `Granted=false` へ畳んでおり、稼働環境で「見えない」の原因が
// 読めなかった（#1378）。**`Granted=false`（正当な deny）では出さない**（陰性対照）。
// 戻り値が従来どおり deny であることも併せて表明する（挙動は変えない）。
[Trait("TestKind", "Unit")]
public class GraphAccessResolverWarnTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpContext Ctx() => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "test-user"), new Claim("clearance", "internal")], "Test")),
    };

    private static async Task<(Platform.Shared.Contracts.Dtos.AccessScopeResponse Scope, CapturingLogger Log)> ResolveAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var log = new CapturingLogger();
        var scope = await new GraphAccessResolver(new StubHttpClientFactory(respond), logger: log)
            .ResolveAsync(Ctx(), GraphAccessAction.Read, Ct);
        return (scope, log);
    }

    [Fact]
    public async Task A_non_success_status_is_logged_as_a_warning_with_the_status()
    {
        var (scope, log) = await ResolveAsync(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        scope.Granted.Should().BeFalse();
        scope.AllowedFilters.Should().BeEmpty();
        log.Warnings.Should().ContainSingle().Which.Field("Status").Should().Be(403);
    }

    [Fact]
    public async Task A_transport_failure_is_logged_as_a_warning_with_the_exception_type()
    {
        var (scope, log) = await ResolveAsync(_ => throw new HttpRequestException("refused"));

        scope.Granted.Should().BeFalse();
        var warn = log.Warnings.Should().ContainSingle().Subject;
        warn.Field("ErrorType").Should().Be(nameof(HttpRequestException));
        warn.Exception.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task An_empty_body_is_logged_as_a_warning()
    {
        var (scope, log) = await ResolveAsync(_ => Json("null"));

        scope.Granted.Should().BeFalse();
        log.Warnings.Should().ContainSingle().Which.Field("Status").Should().Be(200);
    }

    // 🔴 陰性対照: 正当な deny は WARN を出さない。
    [Fact]
    public async Task A_legitimate_deny_is_not_logged_as_a_warning()
    {
        var (scope, log) = await ResolveAsync(_ => Json("""{"userId":"test-user","allowedFilters":[],"granted":false}"""));

        scope.Granted.Should().BeFalse();
        log.Warnings.Should().BeEmpty();
    }

    // 🔴 本番の登録（`AddScoped<IGraphAccessResolver, GraphAccessResolver>`）で**ロガーが実際に注入される**ことを見る。
    // 構築子のロガーは既定 null の省略可能引数なので、DI が渡さなければ無言のまま緑になり得る。
    [Fact]
    public async Task The_production_registration_injects_the_logger()
    {
        var log = new CapturingLogger();
        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(
            new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        services.AddSingleton<ILogger<GraphAccessResolver>>(log);
        services.AddScoped<IGraphAccessResolver, GraphAccessResolver>();
        await using var sp = services.BuildServiceProvider();

        await using var requestScope = sp.CreateAsyncScope();
        var scope = await requestScope.ServiceProvider.GetRequiredService<IGraphAccessResolver>()
            .ResolveAsync(Ctx(), GraphAccessAction.Read, Ct);

        scope.Granted.Should().BeFalse();
        log.Warnings.Should().ContainSingle();
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    internal sealed record Entry(LogLevel Level, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> State)
    {
        public object? Field(string key) => State.FirstOrDefault(p => p.Key == key).Value;
    }

    internal sealed class CapturingLogger : ILogger<GraphAccessResolver>
    {
        private readonly List<Entry> _entries = [];

        public IReadOnlyList<Entry> Warnings => [.. _entries.Where(e => e.Level == LogLevel.Warning)];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _entries.Add(new Entry(logLevel, exception,
                state as IReadOnlyList<KeyValuePair<string, object?>> ?? []));
    }

    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StubHandler(respond)) { BaseAddress = new Uri("http://localhost/") };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
