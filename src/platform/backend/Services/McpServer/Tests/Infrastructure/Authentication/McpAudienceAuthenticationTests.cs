using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AwesomeAssertions;
using McpServer.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace McpServer.Tests.Infrastructure.Authentication;

// FR-16, UC-08, 計画 ADR-0134 決定 1・フォローアップ 2, [[IADR-0516]]（2026-10-09 追記 / #1844）:
// **MCP のプロトコル面（`/mcp`）はトークンの audience（mcp-server）を検証する。** 管理 API（`/mcp-clients`）は既定のスキームのまま。
//
// 🔴 試験の器の既定のスキーム（`TestAuthHandler`）は**どの要求も認証する**。`/mcp` のポリシーがスキームを指名していなければ、
//    トークンの無い要求・audience の違うトークンも通ってしまう —— 否定形がその退行を捕まえる（陽性対照と対で置く）。
// 署名鍵と発行元だけを試験の値へ差し替える（audience の検証の設定は本物のまま）。
[Trait("TestKind", "Integration")]
public class McpAudienceAuthenticationTests(McpAudienceAuthenticationTests.Factory factory)
    : IClassFixture<McpAudienceAuthenticationTests.Factory>
{
    public sealed class Factory : TestWebApplicationFactory
    {
        public const string Issuer = "https://localhost/realms/test";

        public static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes(new string('k', 64)));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
                services.PostConfigure<JwtBearerOptions>(McpAudienceAuthentication.Scheme, o =>
                {
                    var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                    configuration.SigningKeys.Add(Key);
                    o.Configuration = configuration;
                    o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                }));
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Token(string? audience, string issuer = Factory.Issuer) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = DateTime.UtcNow,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["azp"] = "human-x", ["preferred_username"] = "poc-user" },
            SigningCredentials = new SigningCredentials(Factory.Key, SecurityAlgorithms.HmacSha256),
        });

    private async Task<HttpStatusCode> PostMcp(string? bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        // 応答の本文は SSE の流れになり得るので、見出しだけを読む（状態コードだけを見る）。
        using var response = await factory.CreateClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        return response.StatusCode;
    }

    // C-69（陽性対照）: audience が mcp-server のトークンは `/mcp` を通る（認証・認可で止まらない）。
    [Fact]
    public async Task Audienceがmcp_serverのトークンは通る()
    {
        var status = await PostMcp(Token(McpAudienceAuthentication.Audience));

        status.Should().NotBe(HttpStatusCode.Unauthorized).And.NotBe(HttpStatusCode.Forbidden);
        ((int)status).Should().BeLessThan(500);
    }

    // C-70（否定形）: audience が違う・無い・トークンが無い・発行元が違うものは 401（既定のスキームの主体では通さない）。
    [Theory]
    [InlineData("account")]
    [InlineData("bff")]
    [InlineData("platform-api")] // #1846: 既定のスキームが受け付ける共有の audience は /mcp を通らない
    [InlineData(null)]
    public async Task Audienceが違うトークンは401(string? audience)
        => (await PostMcp(Token(audience))).Should().Be(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task トークンが無ければ既定のスキームが認証しても401()
        => (await PostMcp(null)).Should().Be(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task 発行元が違えばaudienceが正しくても401()
        => (await PostMcp(Token(McpAudienceAuthentication.Audience, issuer: "https://evil.example/realms/test")))
            .Should().Be(HttpStatusCode.Unauthorized);

    // C-71: 既定のスキーム（管理 API が使う。BFF が利用者のトークンを中継する）は共有の audience（platform-api。#1846）、
    // `/mcp` のスキームは mcp-server **だけ**を検証する（共有の設定の ValidAudiences を残すと和集合になる）。
    // 発行元の検証・名前のクレームは既定のスキームの値を写す（2 つにしない）。
    [Fact]
    public void Mcpのスキームはmcp_serverだけを検証し他の検証は既定のスキームを写す()
    {
        var all = factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();
        var platform = all.Get(JwtBearerDefaults.AuthenticationScheme);
        var mcp = all.Get(McpAudienceAuthentication.Scheme);

        platform.TokenValidationParameters.ValidateAudience.Should().BeTrue();
        platform.TokenValidationParameters.ValidAudiences.Should().Equal("platform-api");
        mcp.TokenValidationParameters.ValidateAudience.Should().BeTrue();
        mcp.TokenValidationParameters.ValidAudience.Should().Be("mcp-server");
        mcp.TokenValidationParameters.ValidAudiences.Should().Equal("mcp-server");
        mcp.Authority.Should().Be(platform.Authority);
        mcp.TokenValidationParameters.ValidateIssuer.Should().BeTrue();
        mcp.TokenValidationParameters.NameClaimType.Should().Be(platform.TokenValidationParameters.NameClaimType);
        mcp.TokenValidationParameters.RoleClaimType.Should().Be(platform.TokenValidationParameters.RoleClaimType);
    }
}
