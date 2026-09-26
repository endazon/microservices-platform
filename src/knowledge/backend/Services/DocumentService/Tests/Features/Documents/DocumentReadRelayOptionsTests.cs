using System.Security.Claims;
using AwesomeAssertions;
using DocumentService.Features.Documents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocumentService.Tests.Features.Documents;

// FR-19, NFR-09, 計画 ADR-0086 決定 1, ADR-0119 決定 3, [[IADR-0476]] 追記 (#1628):
// 信頼する中継者の集合の**構成の束縛**と判定。AC-5（既定は `bff` だけ・構成は既定を置き換える・空白だけは誰も信じない）。
//
// 🔴 束縛は本番と同じ `Configure<T>(IConfiguration)` で通す —— .NET の配列の束縛は初期値に構成値を**追記する**ので、
//   「構成したら `bff` が外れる」は束縛を通さないと観測できない。
public class DocumentReadRelayOptionsTests
{
    private static DocumentReadRelayOptions Bind(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.Configure<DocumentReadRelayOptions>(config.GetSection(DocumentReadRelayOptions.SectionName));
        return services.BuildServiceProvider().GetRequiredService<IOptions<DocumentReadRelayOptions>>().Value;
    }

    private static ClaimsPrincipal ServiceAccount(string clientId, bool withAzp = true)
    {
        var claims = new List<Claim> { new("preferred_username", $"service-account-{clientId}") };
        if (withAzp) claims.Add(new Claim("azp", clientId));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "preferred_username", ClaimTypes.Role));
    }

    // AC-5: 未構成なら `bff` だけ。
    [Fact]
    public void 未構成なら許可集合はbffだけ()
    {
        var options = Bind([]);

        options.EffectiveClients.Should().Equal("bff");
        options.TrustsUserContextFrom(ServiceAccount("bff")).Should().BeTrue();
        options.TrustsUserContextFrom(ServiceAccount("ai-stock-trading-llm-caller")).Should().BeFalse();
    }

    // AC-5: 🔴 構成は既定を**置き換える**（`bff` が残らない）。
    [Fact]
    public void 構成すると既定を置き換えbffは残らない()
    {
        var options = Bind(new() { ["DocumentRead:TrustedUserContextClients:0"] = " other-relay " });

        options.EffectiveClients.Should().Equal("other-relay");
        options.TrustsUserContextFrom(ServiceAccount("other-relay")).Should().BeTrue("前後空白は落とす");
        options.TrustsUserContextFrom(ServiceAccount("bff")).Should().BeFalse("構成は既定を置き換える");
    }

    // AC-5: 空白だけの構成は誰も信じない（fail-closed）。
    [Fact]
    public void 空白だけの構成は誰も信じない()
    {
        var options = Bind(new() { ["DocumentRead:TrustedUserContextClients:0"] = "  " });

        options.EffectiveClients.Should().BeEmpty();
        options.TrustsUserContextFrom(ServiceAccount("bff")).Should().BeFalse();
    }

    // 判定の形: 機械であること ∧ クライアント識別が序数一致で許可集合に在ること。
    [Fact]
    public void 判定は機械の主体のクライアント識別を序数一致で見る()
    {
        var options = Bind([]);

        options.TrustsUserContextFrom(ServiceAccount("bff", withAzp: false)).Should()
            .BeTrue("azp が無ければ service-account-<clientId> から復元する（MachinePrincipal.ClientIdOf）");
        options.TrustsUserContextFrom(ServiceAccount("BFF")).Should().BeFalse("大小文字を畳まない");

        var human = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("preferred_username", "carol"), new Claim("azp", "bff")], "test", "preferred_username", ClaimTypes.Role));
        options.TrustsUserContextFrom(human).Should().BeFalse("azp=bff の人のトークンは中継者ではない");

        options.TrustsUserContextFrom(new ClaimsPrincipal(new ClaimsIdentity())).Should().BeFalse("未認証");
        options.TrustsUserContextFrom(null).Should().BeFalse();
    }
}
