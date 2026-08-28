using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SampleService.Tests.Features;

// テンプレート: 起動と主要導線の疎通のみを持つ最小の統合テスト。
// 実サービスでは Testcontainers で PostgreSQL 等を立て、Respawn で各テスト前に初期化する。
// 統合テストも専用フォルダは作らず、対象スライスと同じ Tests/Features/ へ置く（IADR-0282）。
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

    [Fact]
    public async Task 作成スライスの入口が疎通する()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/samples", new { name = "sample" }, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue();
    }
}
