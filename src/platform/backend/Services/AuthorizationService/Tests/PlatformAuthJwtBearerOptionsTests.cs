using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace AuthorizationService.Tests;

// NFR(セキュリティ/運用性), ADR-0004, IADR-0086: AddPlatformAuth が構成する JwtBearer の
// metadata 取得アドレス（in-cluster 名）と issuer 検証値（エッジ host）の分離を検証する。
// 手順B（単一エッジ host OIDC を CoreDNS 無しに成立させる・#314）のための配線であり、
// 既定（新キー未設定）は現行と等価・issuer 検証は弱めない（fail-safe）ことを確認する。
public class PlatformAuthJwtBearerOptionsTests
{
    private const string DefaultAuthority = "http://keycloak:8080/realms/platform";
    private const string WellKnown = DefaultAuthority + "/.well-known/openid-configuration";

    private static JwtBearerOptions BuildOptions(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s =>
                new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformAuth(config);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
    }

    // IADR-0086 決定2, AC#1: 新キー未設定なら Authority 一本の現行挙動へ縮退する（後方互換）。
    [Fact]
    public void Default_UsesAuthority_AndDoesNotSetValidIssuers()
    {
        var options = BuildOptions();

        options.Authority.Should().Be(DefaultAuthority);
        (options.TokenValidationParameters.ValidIssuers ?? Enumerable.Empty<string>())
            .Should().BeEmpty();
    }

    // IADR-0086 決定3, AC#4: 既定の安全な検証設定を維持する（issuer 検証を弱めない）。
    [Fact]
    public void Default_KeepsSafeValidationDefaults()
    {
        var options = BuildOptions();

        options.TokenValidationParameters.ValidateIssuer.Should().BeTrue();
        options.RequireHttpsMetadata.Should().BeFalse();
        options.TokenValidationParameters.ValidateAudience.Should().BeFalse();
        options.TokenValidationParameters.NameClaimType.Should().Be("preferred_username");
    }

    // 現行の Auth:Authority 上書きは MetadataAddress 未設定時にそのまま尊重される。
    [Fact]
    public void CustomAuthority_IsRespected_WhenMetadataAddressUnset()
    {
        var options = BuildOptions(("Auth:Authority", "http://kc/realms/x"));

        options.Authority.Should().Be("http://kc/realms/x");
    }

    // IADR-0086 決定1, AC#2: MetadataAddress 設定時は metadata 取得先を分離し、Authority と排他にする。
    [Fact]
    public void MetadataAddress_SeparatesMetadataFromAuthority()
    {
        var options = BuildOptions(("Auth:MetadataAddress", WellKnown));

        options.MetadataAddress.Should().Be(WellKnown);
        options.Authority.Should().BeNull();
    }

    // IADR-0086 決定1: MetadataAddress と Authority が両設定でも MetadataAddress が優先し、Authority は
    // 使わない（排他）。両設定時の暗黙優先順位に依存しない挙動を回帰固定する。
    [Fact]
    public void MetadataAddress_TakesPrecedence_OverAuthority_WhenBothSet()
    {
        var options = BuildOptions(
            ("Auth:Authority", "http://kc/realms/x"),
            ("Auth:MetadataAddress", WellKnown));

        options.MetadataAddress.Should().Be(WellKnown);
        options.Authority.Should().BeNull();
    }

    // IADR-0086 決定1, AC#3: ValidIssuers はカンマ区切りでパースし、複数 issuer を受理集合に足す。
    [Fact]
    public void ValidIssuers_AreParsed_FromCommaSeparatedValue()
    {
        var options = BuildOptions(
            ("Auth:MetadataAddress", WellKnown),
            ("Auth:ValidIssuers",
                "https://edge.example/realms/platform, " + DefaultAuthority));

        options.TokenValidationParameters.ValidIssuers.Should().BeEquivalentTo(
            "https://edge.example/realms/platform",
            DefaultAuthority);
        // 許可リストを足しても issuer 検証自体は有効のまま（fail-safe）。
        options.TokenValidationParameters.ValidateIssuer.Should().BeTrue();
    }

    // 区切り・空白の揺れを吸収し、空要素は落とす（chart から単一 env 文字列で注入するため）。
    [Fact]
    public void ValidIssuers_IgnoreBlankEntries_AndTrimWhitespace()
    {
        var options = BuildOptions(("Auth:ValidIssuers", "  a , ,\n b "));

        options.TokenValidationParameters.ValidIssuers.Should().BeEquivalentTo("a", "b");
    }
}
