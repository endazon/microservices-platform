using System.Net;
using AwesomeAssertions;
using Platform.Bff.Foundation.Http;

namespace Platform.Bff.Tests;

// NFR / IADR-0581（Refs GHSA-5h4q-4m2j-rjhm）: BFF の全応答にセキュリティヘッダが付く。
// 成功・認証の短絡（401）・図画像（後段が返した Content-Type をそのまま中継する口）・未知のパス（404）の
// どれにも付くことを固定する（どこかの口だけに付ける実装を緑にしない）。
public class BffResponseSecurityHeadersTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;

    public BffResponseSecurityHeadersTests(BffTestFactory factory)
    {
        _factory = factory;
        _factory.ConversionStatusCode = HttpStatusCode.OK;
        _factory.ConversionThrows = false;
    }

    private static void AssertHeaders(HttpResponseMessage resp)
    {
        resp.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        resp.Headers.GetValues("Content-Security-Policy").Should()
            .Equal("default-src 'none'; frame-ancestors 'none'; sandbox");
        resp.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        resp.Headers.GetValues("Cross-Origin-Resource-Policy").Should().Equal("same-origin");
    }

    [Fact]
    public async Task Health_endpoint_carries_the_headers()
    {
        var resp = await _factory.CreateClient().GetAsync("/health/live", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertHeaders(resp);
    }

    [Fact]
    public async Task Unauthenticated_short_circuit_carries_the_headers()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AnonymousHeader, "1");

        var resp = await client.GetAsync(
            $"/bff/conversion/jobs/{BffTestFactory.StubJobId}/figures/fig-1/image",
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertHeaders(resp);
    }

    // 図画像は後段の Content-Type（SVG を含む）をそのまま返す口である。文書として開かれても何も実行させない。
    [Fact]
    public async Task Relayed_svg_figure_carries_the_headers()
    {
        _factory.StubFigureImageBytes = [1, 2, 3];
        _factory.StubFigureImageContentType = "image/svg+xml";
        // 既定の身元（管理者）で読む（BffConversionEndpointTests の図画像の試験と同じ）。
        var resp = await _factory.CreateClient().GetAsync(
            $"/bff/conversion/jobs/{BffTestFactory.StubJobId}/figures/fig-1/image",
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("image/svg+xml");
        AssertHeaders(resp);
    }

    [Fact]
    public async Task Unknown_path_carries_the_headers()
    {
        var resp = await _factory.CreateClient().GetAsync("/bff/no-such-endpoint", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        AssertHeaders(resp);
    }

    // 既に値がある応答は上書きしない（決まりの表は 4 本で固定する）。
    [Fact]
    public void Header_table_is_the_four_documented_values()
        => BffResponseSecurityHeadersMiddleware.Headers.Keys.Should().BeEquivalentTo(
            ["X-Content-Type-Options", "Content-Security-Policy", "X-Frame-Options", "Cross-Origin-Resource-Policy"]);
}
