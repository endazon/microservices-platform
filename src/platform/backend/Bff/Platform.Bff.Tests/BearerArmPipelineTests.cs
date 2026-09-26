using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Platform.Bff.Foundation.Session;
using Platform.Shared.Infrastructure.Foundation.Extensions;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;

namespace Platform.Bff.Tests;

// NFR-09, SC-13, ADR-0032, IADR-0251 決定 9, IADR-0273 決定 4, [[IADR-0429]] (#1535):
// **腕 B（BFF 自身の client 名義の利用者トークン）を落としても、Cookie セッションは壊れない**ことを、
// 本番と同じ配線（`AddPlatformAuth` の本物の JwtBearer ＋ `AddBffSession` ＋ Program.cs と同じ順序の
// CSRF → トークン昇格）で固定する。
//
// 🔴 **なぜ純粋関数の試験（`BearerCallerPolicyTests`）だけでは足りないか。**
// セッションが保持するアクセストークンは **`azp` = BFF の client の利用者トークンそのもの**である。
// `SessionTokenPropagationMiddleware` はそれを `Authorization: Bearer` へ昇格する。
// もし昇格の**後**に既定スキーム（`BffSmart`）で再認証する経路があれば、Bearer 側へ振り分けられて
// `BearerCallerPolicy` に拒まれ、**腕 B を落とした瞬間に Cookie 経路が全滅する**。
// 同じトークンが「Cookie 経由なら通り、Bearer で直接送ると 401」になることを 1 つの器で測る。
//
// 🔴 **器は TestServer（メモリ内）であり、どのアドレスにも bind しない。** JwtBearer の metadata も
// 静的構成へ差し替えるので外へも出ない。差し替えるのは I/O（Redis・鍵リング・metadata・検証鍵）だけで、
// 判定（振り分け・JwtBearer の検証・`BearerCallerPolicy`・Cookie ハンドラ・昇格）はすべて本物である。
public sealed class BearerArmPipelineTests : IAsyncLifetime
{
    private const string Issuer = "https://test-issuer/realms/platform";
    private const string BffClientId = "bff";

    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("bff-bearer-arm-pipeline-test-signing-key-0123456789abcdef"));

    private IHost? _host;
    private HttpClient Client => _host!.GetTestServer().CreateClient();

    public async ValueTask InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    var config = new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["Auth:Authority"] = Issuer,
                            ["BffSession:ClientId"] = BffClientId,
                            ["BffSession:ClientSecret"] = "test-secret",
                        })
                        .Build();
                    services.AddRouting();
                    services.AddLogging();
                    // Program.cs と同じ順序: 本物の JwtBearer（AddPlatformAuth）→ BFF セッション。
                    services.AddPlatformAuth(config);
                    services.AddBffSession(config);

                    // ── I/O の器だけを差し替える。
                    services.AddSingleton<IDistributedCache>(new MemoryDistributedCache(
                        Options.Create(new MemoryDistributedCacheOptions())));
                    services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                    services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
                    {
                        o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                            new OpenIdConnectConfiguration { Issuer = Issuer });
                        o.TokenValidationParameters.IssuerSigningKey = SigningKey;
                        o.TokenValidationParameters.ValidIssuer = Issuer;
                    });
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    // Program.cs と同じ順序（CSRF → トークン昇格）。
                    app.UseMiddleware<CsrfHeaderMiddleware>();
                    app.UseMiddleware<SessionTokenPropagationMiddleware>();
                    app.UseEndpoints(e =>
                    {
                        // 本物のセッション Cookie を発行させるためのテスト専用入口。
                        // チケットに保存するアクセストークンは **`azp=bff` の利用者トークン**（本番の BFF が
                        // 認可コード交換で受け取るものと同じ形）。
                        e.MapGet("/test/signin", async (HttpContext http) =>
                        {
                            var props = new AuthenticationProperties();
                            props.StoreTokens(
                            [
                                new AuthenticationToken { Name = "access_token", Value = BffClientUserToken() },
                                new AuthenticationToken { Name = "refresh_token", Value = "RT-1" },
                                new AuthenticationToken
                                {
                                    Name = "expires_at",
                                    Value = DateTimeOffset.UtcNow.AddMinutes(5)
                                        .ToString("o", CultureInfo.InvariantCulture),
                                },
                            ]);
                            await http.SignInAsync(
                                BffSessionExtensions.SessionScheme,
                                new ClaimsPrincipal(new ClaimsIdentity(
                                    [new Claim("sub", "developer"), new Claim(ClaimTypes.Name, "developer")],
                                    BffSessionExtensions.SessionScheme)),
                                props);
                            return Results.Ok();
                        }).AllowAnonymous();

                        // 既存の BFF 端点と同じ形: **スキームを指定しない** `RequireAuthorization()`。
                        // 本文は下流へ透過される `Authorization` を映す（昇格の観測点）。
                        e.MapGet("/bff/probe", (HttpContext http) =>
                                Results.Text(http.Request.Headers.Authorization.ToString()))
                            .RequireAuthorization();
                    });
                }))
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private static string Issue(IDictionary<string, object> claims)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            IssuedAt = now,
            NotBefore = now.AddMinutes(-1),
            Expires = now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
            Claims = claims,
        });
    }

    /// <summary>BFF の confidential client 名義の**利用者**トークン（認可コード交換で得る形）。</summary>
    private static string BffClientUserToken() => Issue(new Dictionary<string, object>
    {
        ["sub"] = "developer",
        ["preferred_username"] = "developer",
        ["azp"] = BffClientId,
        ["realm_access"] = new Dictionary<string, object> { ["roles"] = new[] { "platform-admin" } },
    });

    /// <summary>合成監視のサービスアカウント（client credentials）のトークン。</summary>
    private static string MachineToken() => Issue(new Dictionary<string, object>
    {
        ["sub"] = "svc-synthetic-monitor",
        ["preferred_username"] = "service-account-synthetic-monitor",
        ["azp"] = "synthetic-monitor",
    });

    private async Task<string> SignInAsync()
    {
        var resp = await Client.GetAsync("/test/signin", TestContext.Current.CancellationToken);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var setCookie = resp.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith(new BffSessionOptions().CookieName + "=", StringComparison.Ordinal));
        return setCookie[..setCookie.IndexOf(';')];
    }

    // 🔴 ★ T-24 陰性対照（#1535 の受け入れ基準）: `azp=bff` の利用者トークンを Bearer で直接送ると 401。
    // 腕 B を戻す変異でここが赤になる。
    [Fact]
    public async Task Bff_client_user_token_sent_as_bearer_is_401()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/bff/probe");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", BffClientUserToken());

        var resp = await Client.SendAsync(req, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "利用者の資格情報で /bff/* に入る口はセッション Cookie だけである");
    }

    // ★ T-25 陽性対照: 無人の主体（合成監視）は Bearer で通る。拒否が「何でも 401」ではないこと。
    [Fact]
    public async Task Machine_token_sent_as_bearer_is_accepted()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/bff/probe");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MachineToken());

        var resp = await Client.SendAsync(req, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // 🔴 ★ T-26 陽性対照（本試験の主題）: **同じ `azp=bff` の利用者トークンでも、Cookie セッション経由なら通り**、
    // 下流へは昇格したトークンが渡る。昇格の後に `BffSmart` で再認証する経路があればここが 401 になる。
    [Fact]
    public async Task Cookie_session_is_accepted_and_promotes_the_same_token_downstream()
    {
        var cookie = await SignInAsync();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/bff/probe");
        req.Headers.Add("Cookie", cookie);

        var resp = await Client.SendAsync(req, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "Cookie 経路は BearerCallerPolicy を通らない");
        var forwarded = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        forwarded.Should().StartWith("Bearer ", "下流転送の契約（昇格）は変わらない");
        new JsonWebToken(forwarded["Bearer ".Length..]).GetClaim("azp").Value
            .Should().Be(BffClientId, "昇格したのは Bearer で直接送れば 401 になるのと同じ種類のトークンである");
    }

    // ★ T-26 陰性対照の対照: 資格情報が無ければ 401（「常に通す」器で上の陽性が緑になっていないこと）。
    [Fact]
    public async Task No_credential_is_401()
    {
        var resp = await Client.GetAsync("/bff/probe", TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
