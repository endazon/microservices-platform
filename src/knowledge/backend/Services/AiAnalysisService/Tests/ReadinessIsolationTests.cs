using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AiAnalysisService.Tests;

// NFR-06, ADR-0052 決定 4, IADR-0602 (#2105): AI 分析の readiness は検索・LLM ゲートウェイの停止で落ちない
// （本サービスは自前のデータストアを持たないので readiness に載せる依存は無い）。下流は /health/dependencies で報告する。
// 🔴 チェックの実装だけを差し替え、タグは本番の配線のまま使う —— 下流を `ready` へ戻す変異で試験 2 が赤になる。
[Trait("TestKind", "Integration")]
public class ReadinessIsolationTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private static readonly string[] Downstreams = ["retrieval-service", "llm-gateway"];

    private sealed class FixedCheck(HealthCheckResult result) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    // asRegistered: 真なら登録上の failureStatus、偽なら Unhealthy（readiness はこちらで測る。Degraded=200 に紛れさせない）。
    private WebApplicationFactory<Program> DownstreamsDown(bool asRegistered) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.PostConfigure<HealthCheckServiceOptions>(o =>
            {
                foreach (var reg in o.Registrations.Where(r => Downstreams.Contains(r.Name)))
                {
                    var result = new HealthCheckResult(asRegistered ? reg.FailureStatus : HealthStatus.Unhealthy, "down");
                    reg.Factory = _ => new FixedCheck(result);
                }
            })));

    [Fact]
    public void Readinessに載る依存は無く下流はdependenciesタグにだけ在る()
    {
        var registrations = factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        registrations.Where(r => r.Tags.Contains("ready")).Should().BeEmpty(
            "検索・LLM ゲートウェイの停止で本サービスを NotReady にすると、BFF 側へ連鎖する（#2105）");
        foreach (var name in Downstreams)
        {
            var reg = registrations.Should().ContainSingle(r => r.Name == name).Subject;
            reg.Tags.Should().BeEquivalentTo(["dependencies"]);
            reg.FailureStatus.Should().Be(HealthStatus.Degraded);
        }
    }

    // 受け入れ基準 3: 検索・LLM ゲートウェイが落ちても readiness は 200。
    [Fact]
    public async Task 検索とLLMゲートウェイが落ちてもreadyは200のまま()
    {
        await using var host = DownstreamsDown(asRegistered: false);
        (await host.CreateClient().GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    // 受け入れ基準 2: 下流の状態は /health/dependencies で観測できる。
    [Fact]
    public async Task 下流の状態はdependencies口で観測できる()
    {
        await using var host = DownstreamsDown(asRegistered: true);
        var res = await host.CreateClient().GetAsync("/health/dependencies", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        doc.RootElement.GetProperty("status").GetString().Should().Be("Degraded");
        foreach (var name in Downstreams)
        {
            doc.RootElement.GetProperty("entries").GetProperty(name).GetString().Should().Be("Degraded");
        }
    }
}
