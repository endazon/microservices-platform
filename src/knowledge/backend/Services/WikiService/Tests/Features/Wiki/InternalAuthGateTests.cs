using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WikiService.Tests.Features.Wiki;

// NFR-18, ADR-0084 決定 1, IADR-0686 (#2430): `/wiki/*` の群に掛けた認証の門を固定する。
// 🔴 段 1（既定 Report）は従来の応答（未認証は 200 の空・404）のまま。段 2（Enforce）は未認証を 401 で拒み、認証済みは通す。
// 設定キーは配備が書く外部の契約なので綴りで書く。
[Trait("TestKind", "Integration")]
public class InternalAuthGateTests(AnonymousContractTestFactory factory)
    : IClassFixture<AnonymousContractTestFactory>
{
    private WebApplicationFactory<Program> Enforce() =>
        factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:InternalGate:Mode"] = "Enforce" })));

    public static TheoryData<string> Paths => new()
    {
        "/wiki/pages",
        "/wiki/search?q=a",
        "/wiki/pages/some-slug",
        $"/wiki/pages/by-doc/{Guid.NewGuid()}",
    };

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task 段2では未認証の要求を401で拒む(string path)
    {
        using var app = Enforce();

        (await app.CreateClient().GetAsync(path, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 段2でも認証済みの要求は通る()
    {
        using var app = Enforce();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestUserAuthHandler.UserHeader, "alice");

        (await client.GetAsync("/wiki/pages", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task 既定の段では未認証の要求も従来どおり200で返す()
    {
        (await factory.CreateClient().GetAsync("/wiki/pages", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
