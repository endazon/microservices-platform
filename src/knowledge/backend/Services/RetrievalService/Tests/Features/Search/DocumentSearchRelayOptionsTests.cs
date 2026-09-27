using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetrievalService.Features.Search.Hybrid;

namespace RetrievalService.Tests.Features.Search;

// FR-19, NFR-09, 計画 ADR-0086 決定 1, ADR-0119 決定 3, [[IADR-0426]] 追記 1 (#1635):
// 信頼する中継者の集合の**構成の束縛**と判定。AC-7（既定は `aianalysis-service` だけ・構成は既定を置き換える・
// 空白だけは誰も信じない）と、判定の形（機械であること ∧ 序数一致）。
//
// 🔴 束縛は本番と同じ `Configure<T>(IConfiguration)` で通す —— .NET の配列の束縛は初期値に構成値を**追記する**ので、
//   「構成したら既定が外れる」は束縛を通さないと観測できない。
[Trait("TestKind", "Unit")]
public class DocumentSearchRelayOptionsTests
{
    private static DocumentSearchRelayOptions Bind(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.Configure<DocumentSearchRelayOptions>(config.GetSection(DocumentSearchRelayOptions.SectionName));
        return services.BuildServiceProvider().GetRequiredService<IOptions<DocumentSearchRelayOptions>>().Value;
    }

    private static ClaimsPrincipal Principal(string? username, string? azp)
    {
        var claims = new List<Claim>();
        if (username is not null) claims.Add(new Claim("preferred_username", username));
        if (azp is not null) claims.Add(new Claim("azp", azp));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "preferred_username", ClaimTypes.Role));
    }

    private static ClaimsPrincipal ServiceAccount(string clientId) => Principal($"service-account-{clientId}", clientId);

    // AC-7: 未構成なら `aianalysis-service` だけ。
    [Fact]
    public void 未構成なら許可集合はaianalysis_serviceだけ()
    {
        var options = Bind([]);

        options.EffectiveClients.Should().Equal("aianalysis-service");
        options.TrustsUserContextFrom(ServiceAccount("aianalysis-service")).Should().BeTrue();
        options.TrustsUserContextFrom(ServiceAccount("ai-stock-trading-llm-caller")).Should().BeFalse();
        options.TrustsUserContextFrom(ServiceAccount("bff")).Should().BeFalse("DocumentRead の中継者（bff）は検索の中継者ではない");
    }

    // AC-7: 🔴 構成は既定を**置き換える**（`aianalysis-service` が残らない）。
    [Fact]
    public void 構成すると既定を置き換えaianalysis_serviceは残らない()
    {
        var options = Bind(new() { ["DocumentSearch:TrustedUserContextClients:0"] = " other-relay " });

        options.EffectiveClients.Should().Equal("other-relay");
        options.TrustsUserContextFrom(ServiceAccount("other-relay")).Should().BeTrue("前後空白は落とす");
        options.TrustsUserContextFrom(ServiceAccount("aianalysis-service")).Should().BeFalse("構成は既定を置き換える");
    }

    // AC-7: 空白だけの構成は誰も信じない（fail-closed）。
    [Fact]
    public void 空白だけの構成は誰も信じない()
    {
        var options = Bind(new() { ["DocumentSearch:TrustedUserContextClients:0"] = "  " });

        options.EffectiveClients.Should().BeEmpty();
        options.TrustsUserContextFrom(ServiceAccount("aianalysis-service")).Should().BeFalse();
    }

    // 🔴 #1631 の `DocumentRead` の節を構成しても検索の集合は変わらない（キーを共有しない）。
    [Fact]
    public void DocumentReadの節は検索の集合に効かない()
    {
        var options = Bind(new() { ["DocumentRead:TrustedUserContextClients:0"] = "bff" });

        options.EffectiveClients.Should().Equal("aianalysis-service");
    }

    // AC-5: realm の実形（`profile` を持たない ⇒ 利用者名なし・`azp` だけ）は機械として信じる。
    [Fact]
    public void 利用者名の無いazpだけの機械クライアントを信じる()
    {
        Bind([]).TrustsUserContextFrom(Principal(null, "aianalysis-service")).Should().BeTrue();
    }

    // AC-2: 接頭辞・大小文字の変種は別のクライアント（序数一致）。
    [Theory]
    [InlineData("aianalysis-service-x")]
    [InlineData("aianalysis-servic")]
    [InlineData("xaianalysis-service")]
    [InlineData("AIANALYSIS-SERVICE")]
    public void 接頭辞や大小文字の変種は信じない(string variant)
    {
        var options = Bind([]);

        options.TrustsUserContextFrom(ServiceAccount(variant)).Should().BeFalse();
        options.TrustsUserContextFrom(Principal(null, variant)).Should().BeFalse();
        options.TrustsUserContextFrom(Principal($"service-account-{variant}", null)).Should().BeFalse();
    }

    // AC-3 / AC-4: `azp` の食い違い・人のトークン・未認証。
    [Fact]
    public void azpの食い違いと人のトークンと未認証は信じない()
    {
        var options = Bind([]);

        options.TrustsUserContextFrom(Principal("service-account-aianalysis-service", "ai-stock-trading-llm-caller"))
            .Should().BeFalse("azp が第一。利用者名で上書きしない");
        options.TrustsUserContextFrom(Principal("carol", "aianalysis-service"))
            .Should().BeFalse("azp=aianalysis-service の人のトークンは中継者ではない");
        options.TrustsUserContextFrom(new ClaimsPrincipal(new ClaimsIdentity())).Should().BeFalse("未認証");
        options.TrustsUserContextFrom(null).Should().BeFalse();
    }
}
