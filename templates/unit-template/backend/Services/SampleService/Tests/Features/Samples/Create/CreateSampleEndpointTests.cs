using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SampleService.Tests.Features.Samples.Create;

// テンプレート: スライスの入口（POST /samples）の疎通を見る統合テスト。
//
// 置き場: **統合テストにも専用フォルダを作らない**。段はテストの技術的種別ではなく
// **叩く操作の数**で決める（IADR-0334 決定 2）——このテストが検証する経路は Create の 1 つだけなので
// 3 段目 Tests/Features/Samples/Create/ に置く。同じ集約の 2 つ以上の操作を叩くようになったら
// 2 段目（Tests/Features/Samples/）へ上げる。
//
// 器（WebApplicationFactory の派生や TestAuthHandler 等）は Tests/ 直下に置く。C# は外側の
// 名前空間を自動で探索するため、ここから無修飾で見える（同決定 5。using は足さない）。
public class CreateSampleEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CreateSampleEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task 作成スライスの入口が疎通する()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/samples", new { name = "sample" }, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue();
    }
}
