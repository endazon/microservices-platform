using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Platform.Bff.Tests;

// NFR-06, ADR-0032, ADR-0052 決定 4, IADR-0602 (#2105): BFF の readiness は**自プロセス必須の依存（セッションストア）だけ**を見る。
// 下流 4 サービスの疎通は `/health/dependencies` で報告するだけで、1 つが落ちても BFF は Ready のまま
// （エッジから全画面が 503 にならない。検索は継続する）。
//
// 🔴 チェックの**実装（Factory）だけ**を差し替え、登録の**タグはそのまま**使う —— 下流を `ready` タグへ戻す
// 変異は、差し替えた Unhealthy が readiness に拾われて試験 2 が赤になる。
public class BffReadinessIsolationTests(BffTestFactory factory) : IClassFixture<BffTestFactory>
{
    private const string SessionStore = "redis";
    private static readonly string[] Downstreams =
        ["retrieval-service", "aianalysis-service", "feedback-service", "dashboard-service"];

    /// <summary>名前ごとにチェックの結果を差し替えた器を作る（登録のタグ・failureStatus は本番の配線のまま）。</summary>
    private WebApplicationFactory<Program> With(Func<string, HealthCheckRegistration, HealthCheckResult?> result) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.PostConfigure<HealthCheckServiceOptions>(o =>
            {
                foreach (var reg in o.Registrations)
                {
                    var r = result(reg.Name, reg);
                    if (r is { } fixedResult)
                    {
                        reg.Factory = _ => new FixedCheck(fixedResult);
                    }
                }
            })));

    private sealed class FixedCheck(HealthCheckResult result) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    /// <summary>
    /// セッションストアは健全、下流は全部が落ちている。<paramref name="asRegistered"/> が真なら登録上の failureStatus
    /// （＝本番で接続に失敗したときの値）を、偽なら Unhealthy を返す —— readiness の試験は後者で測る
    /// （failureStatus を残したまま `ready` へ戻す変異も、Degraded=200 に紛れず赤になる）。
    /// </summary>
    private WebApplicationFactory<Program> DownstreamsDown(bool asRegistered) =>
        With((name, reg) => name == SessionStore
            ? HealthCheckResult.Healthy()
            : Downstreams.Contains(name)
                ? new HealthCheckResult(asRegistered ? reg.FailureStatus : HealthStatus.Unhealthy, "down")
                : null);

    // 試験 1（構造）: readiness に載るのはセッションストアだけ。下流 4 つは dependencies タグにだけ在る。
    [Fact]
    public void Readinessに載るのはセッションストアだけで下流はdependenciesタグにだけ在る()
    {
        var registrations = factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        registrations.Where(r => r.Tags.Contains("ready")).Select(r => r.Name)
            .Should().BeEquivalentTo([SessionStore], "下流を readiness に入れると 1 サービスの停止で全画面が 503 になる（#2105）");
        foreach (var name in Downstreams)
        {
            var reg = registrations.Should().ContainSingle(r => r.Name == name).Subject;
            reg.Tags.Should().BeEquivalentTo(["dependencies"]);
            reg.FailureStatus.Should().Be(HealthStatus.Degraded, "下流の不達は Degraded として報告する（Unhealthy にしない）");
        }
    }

    // 試験 2（受け入れ基準 1）: 下流が全部落ちても /health/ready は 200、検索は応答する（NFR-06）。
    [Fact]
    public async Task 下流のlivenessが失敗してもreadyは200で検索も応答する()
    {
        await using var host = DownstreamsDown(asRegistered: false);
        var client = host.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        (await client.GetAsync("/health/ready", ct)).StatusCode.Should().Be(
            HttpStatusCode.OK, "下流の停止で BFF を NotReady にすると、エッジから全画面が 503 になる");
        (await client.GetAsync("/health/live", ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        factory.SearchScopeGranted = true;
        (await client.PostAsJsonAsync("/bff/search", new { query = "経費", topK = 5 }, ct)).StatusCode
            .Should().Be(HttpStatusCode.OK, "AI 回答系の下流が落ちても検索は継続する（NFR-06）");
    }

    // 試験 3（受け入れ基準 2）: 下流の状態は /health/dependencies で観測できる（常に 200。状態は本文）。
    [Fact]
    public async Task 下流の状態はdependencies口で下流ごとに観測できる()
    {
        await using var host = DownstreamsDown(asRegistered: true);
        var res = await host.CreateClient().GetAsync("/health/dependencies", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK, "報告口は probe ではない。状態コードで縮退を表さない");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        doc.RootElement.GetProperty("status").GetString().Should().Be("Degraded");
        var entries = doc.RootElement.GetProperty("entries");
        foreach (var name in Downstreams)
        {
            entries.GetProperty(name).GetString().Should().Be("Degraded");
        }
        entries.TryGetProperty(SessionStore, out _).Should().BeFalse("セッションストアは readiness の側であり報告口には出さない");
    }

    // 試験 4（受け入れ基準 4）: 自プロセス必須の依存（セッションストア）が落ちたら readiness は落ちる。
    [Fact]
    public async Task セッションストアが落ちたらreadyは503になる()
    {
        await using var host = With((name, _) => name == SessionStore
            ? HealthCheckResult.Unhealthy("down")
            : Downstreams.Contains(name) ? HealthCheckResult.Healthy() : null);

        (await host.CreateClient().GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.ServiceUnavailable, "セッションの実体・鍵リングが無いレプリカは要求を捌けない（IADR-0602 決定 2）");
    }
}
