using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AiAnalysisService.Tests.Features.Analysis;

// NFR-18, ADR-0084 決定 1, IADR-0686 (#2430): `/analysis/*` の群に掛けた認証の門を固定する。
// 🔴 段 1（既定 Report）は従来の応答（未認証は 200 ＋ 空回答）のまま。段 2（Enforce）は未認証を 401 で拒み、
// 認可サービスも呼ばない。認証済みは通す。
[Trait("TestKind", "Integration")]
public class InternalAuthGateTests(AnalysisAnonymousContractTestFactory factory)
    : IClassFixture<AnalysisAnonymousContractTestFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Enforce() =>
        factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:InternalGate:Mode"] = "Enforce" })));

    private static object Body(string path) => path == "/analysis/analyze"
        ? new { instruction = "比較して", taskType = "Compare" }
        : new { question = "規程は？" };

    [Theory]
    [InlineData("/analysis/ask")]
    [InlineData("/analysis/analyze")]
    [InlineData("/analysis/ask/stream")]
    public async Task 段2では未認証の要求を401で拒む(string path)
    {
        using var app = Enforce();

        (await app.CreateClient().PostAsJsonAsync(path, Body(path), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/analysis/ask")]
    [InlineData("/analysis/analyze")]
    public async Task 段2でも認証済みの要求は通る(string path)
    {
        using var app = Enforce();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(AnalysisTestUserAuthHandler.UserHeader, "alice");

        (await client.PostAsJsonAsync(path, Body(path), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 既定の段では未認証の要求も従来どおり200で返す()
    {
        (await factory.CreateClient().PostAsJsonAsync("/analysis/ask", Body("/analysis/ask"), Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
