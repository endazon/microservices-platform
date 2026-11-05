using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Platform.Bff.Tests;

// NFR-18, ADR-0032, ADR-0044, IADR-0686 (#2432): BFF の LLM を伴う口の、利用者ごとの要求数の上限。
// 群は 3 つ（analysis = /bff/analysis/*、search = /bff/search、graph-suggestion = /bff/graph/suggestions/generate/*）。
// 🔴 段 1（既定 Report）は止めない。段 2（Enforce）は上限を超えた要求を 429 で拒み、後段へ転送しない。
// 設定キーは配備が書く外部の契約なので綴りで書く。
public sealed class BffUserRateLimitTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly BffTestFactory _base = new();

    public void Dispose() => _base.Dispose();

    private WebApplicationFactory<Program> With(params (string Key, string Value)[] settings) =>
        _base.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))));

    private static HttpClient As(WebApplicationFactory<Program> app, string user)
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Add(TestAuthHandler.UsernameHeader, user);
        return c;
    }

    private static Task<HttpResponseMessage> AskAsync(HttpClient c) =>
        c.PostAsJsonAsync("/bff/analysis/ask", new { question = "締め日は？" }, Ct);

    private static Task<HttpResponseMessage> AnalyzeAsync(HttpClient c) =>
        c.PostAsJsonAsync("/bff/analysis/analyze", new { instruction = "比較して", taskType = "Compare" }, Ct);

    private static Task<HttpResponseMessage> SearchAsync(HttpClient c) =>
        c.PostAsJsonAsync("/bff/search", new { query = "経費", topK = 5 }, Ct);

    [Fact]
    public async Task Enforceでは利用者ごとの上限を超えた要求を429で拒み_別の利用者は自分の枠を持つ()
    {
        using var app = With(("Bff:UserRateLimits:Mode", "Enforce"), ("Bff:UserRateLimits:Groups:analysis", "2"));
        var alice = As(app, "alice");

        (await AskAsync(alice)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AnalyzeAsync(alice)).StatusCode.Should().Be(HttpStatusCode.OK, "ask と analyze は同じ群の枠を使う");
        var third = await AskAsync(alice);
        third.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        third.Headers.RetryAfter.Should().NotBeNull();

        (await AskAsync(As(app, "bob"))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Enforceでは群ごとに別の枠を持つ_検索の上限は回答の枠と混ざらない()
    {
        using var app = With(
            ("Bff:UserRateLimits:Mode", "Enforce"),
            ("Bff:UserRateLimits:Groups:search", "1"),
            ("Bff:UserRateLimits:Groups:analysis", "5"));
        var alice = As(app, "alice");

        (await SearchAsync(alice)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SearchAsync(alice)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await AskAsync(alice)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task EnforceではAI提案の生成も上限に数える()
    {
        using var app = With(("Bff:UserRateLimits:Mode", "Enforce"), ("Bff:UserRateLimits:Groups:graph-suggestion", "1"));
        var alice = As(app, "alice");
        var path = $"/bff/graph/suggestions/generate/{Guid.NewGuid()}";

        (await alice.PostAsync(path, null, Ct)).StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        (await alice.PostAsync(path, null, Ct)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task 既定の段では上限を超えても止めない()
    {
        using var app = With(("Bff:UserRateLimits:Groups:analysis", "1"));
        var alice = As(app, "alice");

        (await AskAsync(alice)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AskAsync(alice)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
