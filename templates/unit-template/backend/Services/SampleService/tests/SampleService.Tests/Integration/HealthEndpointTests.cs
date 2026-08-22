using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SampleService.Tests.Integration;

// テンプレート: 起動と主要導線の疎通のみを持つ最小の統合テスト。
// 実サービスでは Testcontainers で PostgreSQL 等を立て、Respawn で各テスト前に初期化する。
public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Health_は200を返す()
    {
        var response = await _factory.CreateClient().GetAsync("/health", TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue();
    }
}
