using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Shared.Infrastructure.Foundation.Extensions;

namespace Platform.Shared.Infrastructure.Tests.Foundation.Extensions;

// ADR-0004, FR-09 / #901: AddPlatformAuth が全サービス共通で登録する JWT 検証設定・
// ロールポリシー・KeycloakRolesClaimsTransformation の配線を固定する。全サービスが
// この 1 メソッドを通じて認証・認可の基盤を組むため、ここが壊れると個々のサービスの
// テストでは気づきにくい形（設定値のみの変更）で全サービス共通の脆弱化・機能不全が起きる。
public class AuthExtensionsTests
{
    private static JwtBearerOptions BuildJwtBearerOptions(Dictionary<string, string?> configValues)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
        var services = new ServiceCollection();
        services.AddPlatformAuth(config);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
    }

    [Fact]
    public void Authority未設定なら既定のKeycloak_Authorityを使う()
    {
        var options = BuildJwtBearerOptions([]);

        options.Authority.Should().Be("http://keycloak:8080/realms/platform");
    }

    [Fact]
    public void Auth_Authorityを設定するとそれが使われる()
    {
        var options = BuildJwtBearerOptions(new Dictionary<string, string?>
        {
            ["Auth:Authority"] = "http://keycloak.internal/realms/custom",
        });

        options.Authority.Should().Be("http://keycloak.internal/realms/custom");
    }

    // IADR-0086 決定1: MetadataAddress 設定時は Authority と排他にする（両方は設定しない）。
    [Fact]
    public void Auth_MetadataAddress設定時はAuthorityではなくMetadataAddressを使う_排他()
    {
        var options = BuildJwtBearerOptions(new Dictionary<string, string?>
        {
            ["Auth:Authority"] = "http://should-not-be-used/realms/platform",
            ["Auth:MetadataAddress"] = "http://keycloak-internal.svc/realms/platform/.well-known/openid-configuration",
        });

        options.MetadataAddress.Should()
            .Be("http://keycloak-internal.svc/realms/platform/.well-known/openid-configuration");
        // Authority を同時に設定すると JwtBearerHandler の初期化で ArgumentException になり得るため、
        // ここでは「MetadataAddress が優先して設定されていること」だけを見る
        // （options.Authority 自体は AddJwtBearer 内で書き換えられないので入力値のまま残る）。
    }

    [Fact]
    public void RequireHttpsMetadataは無効である_クラスタ内はHTTP()
    {
        var options = BuildJwtBearerOptions([]);

        options.RequireHttpsMetadata.Should().BeFalse();
    }

    // NFR-09, 計画 ADR-0036, [[IADR-0523]] (#1846 / planning#770。利用者裁定 2026-10-09 の方式 C):
    // 既定は共有の audience（platform-api）ただ 1 つを検証する。
    [Fact]
    public void Audienceを検証し既定はplatform_apiだけを受け付ける()
    {
        var options = BuildJwtBearerOptions([]);

        options.TokenValidationParameters.ValidateAudience.Should().BeTrue();
        options.TokenValidationParameters.ValidAudiences.Should().Equal("platform-api");
        options.TokenValidationParameters.ValidAudience.Should().BeNull();
        AuthExtensions.DefaultAudience.Should().Be("platform-api");
    }

    // #1846: サービスごとの audience へは構成（Auth:Audiences）だけで移れる。単一の文字列（env）と配列の両方を受ける。
    [Theory]
    [InlineData("document-service", new[] { "document-service" })]
    [InlineData("platform-api,document-service", new[] { "platform-api", "document-service" })]
    [InlineData(" platform-api  document-service ", new[] { "platform-api", "document-service" })]
    [InlineData("platform-api,platform-api", new[] { "platform-api" })]
    public void Auth_Audiencesの文字列を区切り文字で分割して受け付ける(string raw, string[] expected)
    {
        var options = BuildJwtBearerOptions(new Dictionary<string, string?> { ["Auth:Audiences"] = raw });

        options.TokenValidationParameters.ValidateAudience.Should().BeTrue();
        options.TokenValidationParameters.ValidAudiences.Should().Equal(expected);
    }

    [Fact]
    public void Auth_Audiencesの配列を受け付ける()
    {
        var options = BuildJwtBearerOptions(new Dictionary<string, string?>
        {
            ["Auth:Audiences:0"] = "platform-api",
            ["Auth:Audiences:1"] = "graph-service",
        });

        options.TokenValidationParameters.ValidAudiences.Should().Equal("platform-api", "graph-service");
    }

    // 🔴 #1846: 設定されているのに空なら起動を止める（検証しない形へ黙って縮退させない）。登録の時点で投げる。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ")]
    public void Auth_Audiencesが空なら登録の時点で例外(string raw)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Audiences"] = raw }).Build();

        var act = () => new ServiceCollection().AddPlatformAuth(config);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Auth:Audiences*");
    }

    // 🔴 FR-16, 計画 ADR-0134 決定 1, #1854 / #1846: mcp-server は MCP クライアントのトークンの audience で、/mcp だけが受け付ける。
    // 既定のスキームに入れると MCP クライアントのトークンが platform の API を通るので、起動を止める。
    [Theory]
    [InlineData("mcp-server")]
    [InlineData("platform-api,mcp-server")]
    public void Auth_Audiencesにmcp_serverを入れると登録の時点で例外(string raw)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Audiences"] = raw }).Build();

        var act = () => new ServiceCollection().AddPlatformAuth(config);

        act.Should().Throw<InvalidOperationException>().WithMessage("*mcp-server*");
    }

    [Fact]
    public void RoleClaimTypeはClaimTypes_Roleである()
    {
        // FR-09: これが崩れると IClaimsTransformation が ClaimTypes.Role へ展開したロールを
        // RequireRole が見つけられなくなる（KeycloakRolesClaimsTransformationTests と対）。
        var options = BuildJwtBearerOptions([]);

        options.TokenValidationParameters.RoleClaimType.Should().Be(ClaimTypes.Role);
    }

    [Fact]
    public void NameClaimTypeはpreferred_usernameである()
    {
        // FR-08, FR-15, IADR-0010: 既定の unique_name 写像だと実 Keycloak トークンで
        // Identity.Name が null になり、監査ログの subject が全員 unknown へ潰れる（Issue #118）。
        var options = BuildJwtBearerOptions([]);

        options.TokenValidationParameters.NameClaimType.Should().Be("preferred_username");
    }

    [Fact]
    public void ValidIssuers未設定なら上書きしない_既定挙動を維持する()
    {
        var options = BuildJwtBearerOptions([]);

        options.TokenValidationParameters.ValidIssuers.Should().BeNullOrEmpty();
        // issuer 検証そのものは弱めていないことも併せて固定する（IADR-0086 決定3）。
        options.TokenValidationParameters.ValidateIssuer.Should().BeTrue();
    }

    [Theory]
    [InlineData("https://a.example/realms/x,https://b.example/realms/x", new[] { "https://a.example/realms/x", "https://b.example/realms/x" })]
    [InlineData(" https://a.example/realms/x , https://b.example/realms/x ", new[] { "https://a.example/realms/x", "https://b.example/realms/x" })]
    [InlineData("https://a.example/realms/x\thttps://b.example/realms/x", new[] { "https://a.example/realms/x", "https://b.example/realms/x" })]
    [InlineData("https://a.example/realms/x,,https://b.example/realms/x", new[] { "https://a.example/realms/x", "https://b.example/realms/x" })]
    [InlineData("https://only.example/realms/x", new[] { "https://only.example/realms/x" })]
    public void Auth_ValidIssuersを区切り文字で分割しtrimして採用する(string raw, string[] expected)
    {
        // IADR-0086 決定1: カンマ/空白/タブ区切り、空要素除去、trim。
        var options = BuildJwtBearerOptions(new Dictionary<string, string?> { ["Auth:ValidIssuers"] = raw });

        options.TokenValidationParameters.ValidIssuers.Should().BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Auth_ValidIssuersが未設定または空白のみならValidIssuersを設定しない(string? raw)
    {
        var options = BuildJwtBearerOptions(new Dictionary<string, string?> { ["Auth:ValidIssuers"] = raw });

        options.TokenValidationParameters.ValidIssuers.Should().BeNullOrEmpty();
    }

    [Fact]
    public void KeycloakRolesClaimsTransformationがIClaimsTransformationとして登録される()
    {
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddPlatformAuth(config);

        using var provider = services.BuildServiceProvider();
        provider.GetServices<IClaimsTransformation>()
            .Should().ContainSingle(t => t is KeycloakRolesClaimsTransformation);
    }

    [Fact]
    public void AdminOnlyポリシーはplatform_adminロールのみ許可する()
    {
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddPlatformAuth(config);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        var policy = options.GetPolicy(PlatformAuthPolicies.AdminOnly);

        policy.Should().NotBeNull();
        var rolesReq = policy!.Requirements.OfType<RolesAuthorizationRequirement>().Single();
        rolesReq.AllowedRoles.Should().BeEquivalentTo([PlatformAuthPolicies.AdminRole]);
    }

    [Fact]
    public void ConfigViewerポリシーは管理者と運用者のいずれかを許可する_OR条件()
    {
        // FR-15, SC-11, IADR-0030: RequireRole の複数指定は OR。運用者だけでも構成閲覧できる。
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddPlatformAuth(config);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        var policy = options.GetPolicy(PlatformAuthPolicies.ConfigViewer);

        policy.Should().NotBeNull();
        var rolesReq = policy!.Requirements.OfType<RolesAuthorizationRequirement>().Single();
        rolesReq.AllowedRoles.Should().BeEquivalentTo(
            [PlatformAuthPolicies.AdminRole, PlatformAuthPolicies.OperatorRole]);
    }
}
