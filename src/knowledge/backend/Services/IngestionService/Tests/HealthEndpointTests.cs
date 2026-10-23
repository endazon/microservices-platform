using System.Net;
using AwesomeAssertions;
using IngestionService.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using Qdrant.Client;

namespace IngestionService.Tests;

// NFR-21, NFR-06, ADR-0052 決定 4, IADR-0602 (#2140): 取り込みは `/health/live`・`/health/ready` を持ち（helm の probe が叩く）、
// readiness は**自プロセスの必須依存だけ**（ブローカ・自前の Qdrant）で決まる。下流のサービス（LLM ゲートウェイ）は載せない。
// 🔴 チェックの実装だけを差し替え、名前とタグは本番の配線のまま使う —— Map を外す・依存を ready から外す変異で赤になる。
[Trait("TestKind", "Integration")]
public class HealthEndpointTests(IntrospectionEndpointTests.Factory factory)
    : IClassFixture<IntrospectionEndpointTests.Factory>
{
    // MassTransit の "masstransit-bus"（ready）は AddMassTransit が暗黙に登録する（IngestionCompleted の発行側のブローカ疎通）。
    private const string MassTransitBus = "masstransit-bus";

    private static readonly string[] OwnDependencies =
        [WolverineExtensions.BrokerHealthCheckName, MassTransitBus, QdrantHealthCheck.Name];

    private sealed class FixedCheck(HealthStatus status) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HealthCheckResult(status, "fixed"));
    }

    // 名前で選んだ依存を Unhealthy、他の登録をすべて Healthy に固定したホスト（実ブローカ・実 Qdrant に繋がない）。
    private WebApplicationFactory<Program> WithDown(params string[] down) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.PostConfigure<HealthCheckServiceOptions>(o =>
            {
                foreach (var reg in o.Registrations)
                {
                    var status = down.Contains(reg.Name) ? HealthStatus.Unhealthy : HealthStatus.Healthy;
                    reg.Factory = _ => new FixedCheck(status);
                }
            })));

    // 受け入れ基準 2: /health/live は依存を見ずに 200（依存がすべて落ちていても）。
    [Fact]
    public async Task liveは依存がすべて落ちていても200()
    {
        await using var host = WithDown(OwnDependencies);
        (await host.CreateClient().GetAsync("/health/live", TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void readinessに載るのはブローカの2スタックと自前のQdrantだけ()
    {
        var registrations = factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        registrations.Where(r => r.Tags.Contains(HealthCheckExtensions.ReadyTag)).Select(r => r.Name)
            .Should().BeEquivalentTo(OwnDependencies,
                "readiness は自プロセスの必須依存だけ（LLM ゲートウェイ等の下流を入れると、その停止で取り込みが NotReady になる。#2105）");
        registrations.Single(r => r.Name == QdrantHealthCheck.Name).FailureStatus.Should().Be(HealthStatus.Unhealthy);
    }

    // 受け入れ基準 2: /health/ready は自前の依存で決まる。
    [Fact]
    public async Task readyは自前の依存がすべて健全なら200()
    {
        await using var host = WithDown();
        (await host.CreateClient().GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(WolverineExtensions.BrokerHealthCheckName)]
    [InlineData(MassTransitBus)]
    [InlineData(QdrantHealthCheck.Name)]
    public async Task readyは自前の依存の1つが落ちると503(string down)
    {
        await using var host = WithDown(down);
        (await host.CreateClient().GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // Qdrant のチェック自体: 届かない宛先は Unhealthy（例外を握り潰して Healthy にしない）。
    [Fact]
    public async Task Qdrantに届かなければUnhealthy()
    {
        using var client = new QdrantClient("127.0.0.1", 1);
        var check = new QdrantHealthCheck(client);
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(QdrantHealthCheck.Name, check, HealthStatus.Unhealthy, null),
        };

        var result = await check.CheckHealthAsync(context, TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }
}
