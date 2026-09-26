using System.Net;
using AiAnalysisService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;

namespace AiAnalysisService.Tests.Infrastructure.ExternalServices;

// FR-04, FR-05, NFR-09, ADR-0004, [[IADR-0379]] 決定 5 (#1378):
// REST 経路のスコープ解決が deny-by-default へ縮退した**理由を WARN で出す**ことを固定する。
// 従前は非 2xx・不達・空本文を無言で `Granted=false` へ畳んでおり、稼働環境で「回答に出典が無い」
// 原因が読めなかった（#1378）。**`Granted=false`（正当な deny）では出さない**（陰性対照）。
//
// `ResolveScopeAsync` は private なので、観測点は `AskAsync` の応答（deny → 空回答）とログである。
// deny の枝は検索・LLM を呼ばないので、器は `/authz/scope` だけに答える。
[Trait("TestKind", "Unit")]
public class RagOrchestratorScopeWarnTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(Knowledge.Contracts.Dtos.AiAnswerDto Answer, CapturingLogger Log)> AskAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respondToScope)
    {
        var log = new CapturingLogger();
        var answer = await new RagOrchestrator(new StubHttpClientFactory(respondToScope), logger: log)
            .AskAsync("質問", "user-1", new Dictionary<string, string> { ["clearance"] = "internal" }, ct: Ct);
        return (answer, log);
    }

    [Fact]
    public async Task A_non_success_status_is_logged_as_a_warning_with_the_status()
    {
        var (answer, log) = await AskAsync(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        answer.Citations.Should().BeEmpty("挙動は変えない —— 非 2xx は従来どおり deny（空回答）");
        log.Warnings.Should().ContainSingle().Which.Field("Status").Should().Be(403);
    }

    [Fact]
    public async Task A_transport_failure_is_logged_as_a_warning_with_the_exception_type()
    {
        var (answer, log) = await AskAsync(_ => throw new HttpRequestException("refused"));

        answer.Citations.Should().BeEmpty();
        var warn = log.Warnings.Should().ContainSingle().Subject;
        warn.Field("ErrorType").Should().Be(nameof(HttpRequestException));
        warn.Exception.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task An_empty_body_is_logged_as_a_warning()
    {
        var (answer, log) = await AskAsync(_ => Json("null"));

        answer.Citations.Should().BeEmpty();
        log.Warnings.Should().ContainSingle().Which.Field("Status").Should().Be(200);
    }

    // 🔴 陰性対照: 正当な deny は WARN を出さない。
    [Fact]
    public async Task A_legitimate_deny_is_not_logged_as_a_warning()
    {
        var (answer, log) = await AskAsync(_ => Json("""{"userId":"user-1","allowedFilters":[],"granted":false}"""));

        answer.Citations.Should().BeEmpty();
        log.Warnings.Should().BeEmpty();
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    internal sealed record Entry(LogLevel Level, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> State)
    {
        public object? Field(string key) => State.FirstOrDefault(p => p.Key == key).Value;
    }

    internal sealed class CapturingLogger : ILogger<RagOrchestrator>
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

    // `/authz/scope` 以外が呼ばれたら落とす（deny の枝は検索・LLM へ進まない）。
    private sealed class StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> respondToScope)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StubHandler(respondToScope)) { BaseAddress = new Uri("http://localhost/") };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respondToScope) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => request.RequestUri!.AbsolutePath == "/authz/scope"
                ? Task.FromResult(respondToScope(request))
                : throw new InvalidOperationException($"deny の枝で {request.RequestUri} を呼んではならない");
    }
}
