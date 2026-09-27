using System.Security.Claims;
using AwesomeAssertions;
using RetrievalService.Features.Search.AttributeValues;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace RetrievalService.Tests.Features.Search;

// FR-04, FR-05, NFR-09, 計画 ADR-0086 決定 1, [[IADR-0417]] 追記 1 (#1636):
// 信頼する中継者の集合の**構成の束縛**と判定。AC-7（既定は `bff` だけ・構成は既定を置き換える・
// 空白だけは誰も信じない・1 つの値は起動時に止める）と、判定の形（機械であること ∧ 序数一致）。
//
// 🔴 束縛は本番と同じ `Configure<T>(IConfiguration)` で通す —— .NET の配列の束縛は初期値に構成値を**追記する**ので、
//   「構成したら既定が外れる」は束縛を通さないと観測できない。
[Trait("TestKind", "Unit")]
public class AttributeValuesRelayOptionsTests
{
    private static AttributeValuesRelayOptions Bind(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.Configure<AttributeValuesRelayOptions>(config.GetSection(AttributeValuesRelayOptions.SectionName));
        return services.BuildServiceProvider().GetRequiredService<IOptions<AttributeValuesRelayOptions>>().Value;
    }

    private static ClaimsPrincipal Principal(string? username, string? azp)
    {
        var claims = new List<Claim>();
        if (username is not null) claims.Add(new Claim("preferred_username", username));
        if (azp is not null) claims.Add(new Claim("azp", azp));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "preferred_username", ClaimTypes.Role));
    }

    private static ClaimsPrincipal ServiceAccount(string clientId) => Principal($"service-account-{clientId}", clientId);

    [Fact]
    public void 未構成なら許可集合はbffだけ()
    {
        var options = Bind([]);

        options.EffectiveClients.Should().Equal("bff");
        options.TrustsUserContextFrom(ServiceAccount("bff")).Should().BeTrue();
        options.TrustsUserContextFrom(ServiceAccount("ai-stock-trading-llm-caller")).Should().BeFalse();
        options.TrustsUserContextFrom(ServiceAccount("aianalysis-service")).Should().BeFalse("検索の中継者（aianalysis-service）は属性値の中継者ではない");
    }

    [Fact]
    public void 構成すると既定を置き換えbffは残らない()
    {
        var options = Bind(new() { ["AttributeValues:TrustedUserContextClients:0"] = " other-relay " });

        options.EffectiveClients.Should().Equal("other-relay");
        options.TrustsUserContextFrom(ServiceAccount("other-relay")).Should().BeTrue("前後空白は落とす");
        options.TrustsUserContextFrom(ServiceAccount("bff")).Should().BeFalse("構成は既定を置き換える");
    }

    [Fact]
    public void 空白だけの構成は誰も信じない()
    {
        var options = Bind(new() { ["AttributeValues:TrustedUserContextClients:0"] = "  " });

        options.EffectiveClients.Should().BeEmpty();
        options.TrustsUserContextFrom(ServiceAccount("bff")).Should().BeFalse();
    }

    // 🔴 1 つの値（配列でない）は束縛されず既定へ静かに戻る。起動時に止める。
    [Fact]
    public void 一つの値で構成すると起動時に止まる()
    {
        var scalar = new Dictionary<string, string?> { ["AttributeValues:TrustedUserContextClients"] = "other-relay,bff-2" };

        Bind(scalar).EffectiveClients.Should().Equal(["bff"], "前提: 1 つの値は配列へ束縛されず既定へ戻る（止める理由）");
        var act = () => AttributeValuesRelayOptions.ThrowIfScalar(new ConfigurationBuilder().AddInMemoryCollection(scalar).Build());
        act.Should().Throw<InvalidOperationException>().WithMessage("*AttributeValues__TrustedUserContextClients__0*");

        // 対照: 配列の形・未構成は通る。
        AttributeValuesRelayOptions.ThrowIfScalar(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["AttributeValues:TrustedUserContextClients:0"] = "bff" }).Build());
        AttributeValuesRelayOptions.ThrowIfScalar(new ConfigurationBuilder().Build());
    }

    // 🔴 同じサービスの別の面（#1635 の `DocumentSearch`）の節を構成しても属性値の集合は変わらない（キーを共有しない）。
    [Fact]
    public void DocumentSearchの節は属性値の集合に効かない()
    {
        Bind(new() { ["DocumentSearch:TrustedUserContextClients:0"] = "aianalysis-service" }).EffectiveClients.Should().Equal("bff");
    }

    // realm の実形（`profile` を持たない ⇒ 利用者名なし・`azp` だけ）は機械として信じる。
    [Fact]
    public void 利用者名の無いazpだけの機械クライアントを信じる()
    {
        Bind([]).TrustsUserContextFrom(Principal(null, "bff")).Should().BeTrue();
    }

    [Theory]
    [InlineData("bff-x")]
    [InlineData("bf")]
    [InlineData("xbff")]
    [InlineData("BFF")]
    public void 接頭辞や大小文字の変種は信じない(string variant)
    {
        var options = Bind([]);

        options.TrustsUserContextFrom(ServiceAccount(variant)).Should().BeFalse();
        options.TrustsUserContextFrom(Principal(null, variant)).Should().BeFalse();
        options.TrustsUserContextFrom(Principal($"service-account-{variant}", null)).Should().BeFalse();
    }

    [Fact]
    public void azpの食い違いと人のトークンと未認証は信じない()
    {
        var options = Bind([]);

        options.TrustsUserContextFrom(Principal("service-account-bff", "ai-stock-trading-llm-caller"))
            .Should().BeFalse("azp が第一。利用者名で上書きしない");
        options.TrustsUserContextFrom(Principal("carol", "bff"))
            .Should().BeFalse("azp=bff の人のトークンは中継者ではない");
        options.TrustsUserContextFrom(new ClaimsPrincipal(new ClaimsIdentity())).Should().BeFalse("未認証");
        options.TrustsUserContextFrom(null).Should().BeFalse();
    }
}
