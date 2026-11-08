using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace LlmGateway.Tests;

// NFR-09, NFR-18, IADR-0710: OpenAPI の文書は開発の環境（Development）でだけ出す。
// 本番（Production）と試験の器（Testing）では口を張らない（資格情報の有無に関わらず 404）。
// 陽性対照: Development では 200（開発の使い方は従来どおり）。
[Trait("TestKind", "Integration")]
public class OpenApiExposureTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    private const string DocumentPath = "/openapi/v1.json";

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task 本番の環境では_OpenAPI_の文書を出さない(string environment)
    {
        using var app = factory.WithWebHostBuilder(b => b.UseEnvironment(environment));
        using var anonymous = app.CreateClient();
        anonymous.DefaultRequestHeaders.Authorization = null;

        var withoutToken = await anonymous.GetAsync(DocumentPath, TestContext.Current.CancellationToken);
        withoutToken.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 資格情報（ServiceCaller）を持っていても出さない（口そのものが無い）。
        var withToken = await app.CreateClient().GetAsync(DocumentPath, TestContext.Current.CancellationToken);
        withToken.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task 試験の器の既定_Testing_でも_OpenAPI_の文書を出さない()
    {
        var response = await factory.CreateAnonymousClient().GetAsync(DocumentPath, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task 開発の環境では_OpenAPI_の文書を従来どおり出す()
    {
        // Development は DI の構築時の検証（ValidateOnBuild）を既定で有効にする。器が差し替えたスタブの外に残る
        // 本番の登録（鍵の要る SDK の生成）を検証で落とさないよう、ここでは検証だけを切る（主題は口の有無）。
        using var app = factory.WithWebHostBuilder(b => b
            .UseEnvironment("Development")
            .UseDefaultServiceProvider(o => { o.ValidateOnBuild = false; o.ValidateScopes = false; }));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = null;

        var response = await client.GetAsync(DocumentPath, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("\"openapi\"");
    }
}
