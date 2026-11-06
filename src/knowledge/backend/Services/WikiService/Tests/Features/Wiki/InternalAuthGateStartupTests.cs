using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WikiService.Tests.Features.Wiki;

// NFR-18, IADR-0686 (#2430・AI レビュー): `Auth:InternalGate:Mode` の不正な値は**起動時に**止める（最初の要求で 500 にしない）。
[Trait("TestKind", "Integration")]
public class InternalAuthGateStartupTests(AnonymousContractTestFactory factory)
    : IClassFixture<AnonymousContractTestFactory>
{
    [Fact]
    public void 不正な段の値では起動を止める()
    {
        using var app = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:InternalGate:Mode"] = "Enfroce" })));

        var act = () => { using var _ = app.CreateClient(); };

        act.Should().Throw<Exception>();
    }
}
