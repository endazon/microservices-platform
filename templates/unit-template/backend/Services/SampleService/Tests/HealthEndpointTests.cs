using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SampleService.Tests;

// テンプレート: 起動と /health の疎通だけを持つ最小の統合テスト。
// 実サービスでは Testcontainers で PostgreSQL 等を立て、Respawn で各テスト前に初期化する。
//
// 置き場: **Tests/ 直下**である（IADR-0334 決定 4）。検証しているのは Program.cs が配線した
// /health であり、本体でも Program.cs はサービス直下に在る。**その鏡写しの位置が Tests/ 直下**
// であって、「鏡写しから漏れた残り」ではない。
// スライスを叩くテストはここに置かない —— Tests/Features/<集約>/<操作>/ へ置く
// （同 IADR 決定 2。実例は Features/Samples/Create/CreateSampleEndpointTests.cs）。
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
