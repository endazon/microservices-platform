using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
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
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Platform.Bff.Foundation.Endpoints;
using Platform.Bff.Foundation.Session;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;

namespace Platform.Bff.Tests;

// NFR, SC-16, ADR-0032, IADR-0251, IADR-0273, #439 第 3 段(3b):
// **セッション経路そのものを、本物の Cookie ハンドラ・本物のチケットストアで通しで測る。**
//
// 🔴 既存の `BffTestFactory` は既定スキームを `Test` へ上書きして本物の認証経路を迂回する
// （`TestAuthHandlerLayeringTests` が実測）。**ここでは迂回しない** —— サインインで本物の
// セッション Cookie を発行させ、その Cookie で本物の Cookie ハンドラ＋`RedisTicketStore`
// （Redis の代わりに MemoryDistributedCache）を通す。差し替えているのは
// I/O の器（Redis / 鍵リング / token endpoint への HTTP）だけで、判定はすべて本物である。
public sealed class SessionTestHost : IAsyncDisposable
{
    public const string Issuer = "https://keycloak.test/realms/platform";
    public const string TokenEndpoint = "https://keycloak.test/token";
    public const string EndSessionEndpoint = "https://keycloak.test/logout";

    /// <summary>logout_token の署名に使う鍵（認可サーバ役）。</summary>
    public static readonly RsaSecurityKey SigningKey = NewRsaKey();

    public static RsaSecurityKey NewRsaKey() =>
        new(RSA.Create(2048)) { KeyId = Guid.NewGuid().ToString("N") };

    private readonly IHost _host;
    public HttpClient Client { get; }
    public RedisTicketStore Store => _host.Services.GetRequiredService<RedisTicketStore>();

    /// <summary>token endpoint（refresh）役。テストごとに応答を差し替える。</summary>
    public Func<HttpRequestMessage, HttpResponseMessage> TokenEndpointResponder { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.BadRequest);

    private SessionTestHost(IHost host)
    {
        _host = host;
        Client = host.GetTestServer().CreateClient();
    }

    public static async Task<SessionTestHost> StartAsync()
    {
        SessionTestHost? created = null;
        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    var config = new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["BffSession:ClientSecret"] = "test-secret",
                        })
                        .Build();
                    services.AddRouting();
                    services.AddAuthorization();
                    services.AddLogging();
                    services.AddBffSession(config);
                    // 本番で AddPlatformAuth が登録する JwtBearer の代役（振り分けスキームの Bearer 側の
                    // 受け皿。ここでは Bearer 経路の可否は測らない —— それは DefaultSchemeRoutingTests）。
                    services.AddAuthentication()
                        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions,
                            NoResultHandler>("Bearer", _ => { });

                    // ── I/O の器だけを差し替える（判定は本物のまま）。
                    // Redis → メモリ（チケットの直列化・索引・失効は RedisTicketStore の本物が動く）。
                    services.AddSingleton<IDistributedCache>(new MemoryDistributedCache(
                        Options.Create(new MemoryDistributedCacheOptions())));
                    // 鍵リング → プロセス内（Redis 永続化は IADR-0251 決定 5。単体では検証できない）。
                    services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                    // OIDC メタデータ → 静的構成（実 Keycloak はこの環境に無い）。
                    services.PostConfigure<OpenIdConnectOptions>(
                        OpenIdConnectDefaults.AuthenticationScheme, o =>
                        {
                            var configuration = new OpenIdConnectConfiguration
                            {
                                Issuer = Issuer,
                                TokenEndpoint = TokenEndpoint,
                                EndSessionEndpoint = EndSessionEndpoint,
                            };
                            configuration.SigningKeys.Add(SigningKey);
                            o.Configuration = configuration;
                            // ハンドラ（signout のリダイレクト構築等）は ConfigurationManager 経由で
                            // メタデータを読む。静的構成に**両方**を差し替えないと、Authority
                            // （実在しない keycloak:8080）へ取りに行って落ちる。
                            o.ConfigurationManager =
                                new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<
                                    OpenIdConnectConfiguration>(configuration);
                        });
                    // refresh の token endpoint 呼び出し → スタブ（応答は各テストが決める）。
                    services.AddHttpClient(SessionTokenRefresher.HttpClientName)
                        .ConfigurePrimaryHttpMessageHandler(() =>
                            new DelegatingResponder(req => created!.TokenEndpointResponder(req)));
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
                        e.MapAuthBffEndpoints();

                        // 本物のセッション Cookie を発行させるためのテスト専用入口。
                        e.MapGet("/test/signin", async (
                            string sub, string? sid, string? roles, string? expiresAt, HttpContext http) =>
                        {
                            var claims = new List<Claim>
                            {
                                new("sub", sub),
                                new(ClaimTypes.Name, sub),
                            };
                            if (!string.IsNullOrEmpty(sid)) claims.Add(new Claim("sid", sid));
                            foreach (var role in (roles ?? string.Empty).Split(
                                ',', StringSplitOptions.RemoveEmptyEntries))
                                claims.Add(new Claim(ClaimTypes.Role, role));

                            var props = new AuthenticationProperties();
                            props.StoreTokens(
                            [
                                new AuthenticationToken { Name = "access_token", Value = "AT-1" },
                                new AuthenticationToken { Name = "refresh_token", Value = "RT-1" },
                                new AuthenticationToken
                                {
                                    Name = "expires_at",
                                    Value = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(5)
                                        .ToString("o", CultureInfo.InvariantCulture),
                                },
                            ]);
                            await http.SignInAsync(
                                BffSessionExtensions.SessionScheme,
                                new ClaimsPrincipal(new ClaimsIdentity(
                                    claims, BffSessionExtensions.SessionScheme)),
                                props);
                            return Results.Ok();
                        }).AllowAnonymous();

                        // 下流転送が読むのと同じヘッダを映す（要認証＝セッション経路）。
                        var sessionOnly = new AuthorizationPolicyBuilder(BffSessionExtensions.SessionScheme)
                            .RequireAuthenticatedUser().Build();
                        e.MapGet("/test/echo-auth", (HttpContext http) =>
                                Results.Text(http.Request.Headers.Authorization.ToString()))
                            .RequireAuthorization(sessionOnly);
                        // 匿名版（「セッションが無ければヘッダは立たない」の対照用）。
                        e.MapGet("/test/echo-auth-anon", (HttpContext http) =>
                                Results.Text(http.Request.Headers.Authorization.ToString()))
                            .AllowAnonymous();
                    });
                }))
            .StartAsync();
        created = new SessionTestHost(host);
        return created;
    }

    /// <summary>サインインしてセッション Cookie（`名前=値` の 1 組）を返す。</summary>
    public async Task<string> SignInAsync(
        string sub, string? sid = "sid-1", string roles = "", string? expiresAt = null)
    {
        var url = $"/test/signin?sub={Uri.EscapeDataString(sub)}"
            + (sid is null ? "" : $"&sid={Uri.EscapeDataString(sid)}")
            + $"&roles={Uri.EscapeDataString(roles)}"
            + (expiresAt is null ? "" : $"&expiresAt={Uri.EscapeDataString(expiresAt)}");
        var resp = await Client.GetAsync(url);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var setCookie = resp.Headers.GetValues("Set-Cookie")
            .First(v => v.StartsWith("__Host-msp-session=", StringComparison.Ordinal));
        return setCookie.Split(';')[0];
    }

    public HttpRequestMessage Request(HttpMethod method, string path, string? cookie)
    {
        var req = new HttpRequestMessage(method, path);
        if (cookie is not null) req.Headers.Add("Cookie", cookie);
        return req;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        _host.Dispose();
        await Task.CompletedTask;
    }

    private sealed class DelegatingResponder(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class NoResultHandler(
        IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : Microsoft.AspNetCore.Authentication.AuthenticationHandler<
            Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }
}

public class BffSessionFlowTests
{
    // ★ 陽性対照（T-2 の対）: セッション Cookie で `/bff/auth/me` が 200 になり、身元とロールと
    // ログアウト URL が返る。**これが無いと、下の否定形は「常に 404 の実装」でも緑になる。**
    [Fact]
    public async Task Me_returns_identity_roles_and_logout_url_for_a_cookie_session()
    {
        await using var host = await SessionTestHost.StartAsync();
        var cookie = await host.SignInAsync("alice", sid: "sess-1", roles: "platform-admin");

        var resp = await host.Client.SendAsync(
            host.Request(HttpMethod.Get, "/bff/auth/me", cookie));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"alice\"");
        body.Should().Contain("platform-admin");
        body.Should().Contain("/bff/auth/logout?sid=sess-1");
    }

    // 🔴 T-2（否定形）: **トークンはブラウザへ一切出ない。** 応答の本文にもヘッダにも、
    // チケットに保存したトークン値が現れないこと（陽性対照は上のテスト）。
    [Fact]
    public async Task Tokens_never_appear_in_any_browser_visible_response()
    {
        await using var host = await SessionTestHost.StartAsync();
        var cookie = await host.SignInAsync("alice", roles: "platform-admin");

        var resp = await host.Client.SendAsync(
            host.Request(HttpMethod.Get, "/bff/auth/me", cookie));

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain("AT-1").And.NotContain("RT-1");
        var allHeaders = resp.Headers.Concat(resp.Content.Headers)
            .SelectMany(h => h.Value).ToList();
        allHeaders.Should().NotContain(v => v.Contains("AT-1") || v.Contains("RT-1"));
    }

    // ★ 未認証は 401（存在秘匿ではなく認証境界。SPA はここでログイン導線を出す）。
    [Fact]
    public async Task Me_without_a_cookie_is_401()
    {
        await using var host = await SessionTestHost.StartAsync();

        var resp = await host.Client.GetAsync("/bff/auth/me");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // 🔴 T-1（NFR の本丸）: **失効 → “次の” リクエストで 401。**
    // 暫定措置（最大 10 分遅延）へ退行していないことを、時計を進めずに固定する。
    [Fact]
    public async Task Revoked_subject_is_rejected_on_the_very_next_request()
    {
        await using var host = await SessionTestHost.StartAsync();
        var cookie = await host.SignInAsync("alice", roles: "platform-admin");
        (await host.Client.SendAsync(host.Request(HttpMethod.Get, "/bff/auth/me", cookie)))
            .StatusCode.Should().Be(HttpStatusCode.OK, "失効前は通る（陽性対照）");

        var removed = await host.Store.RemoveAllForSubjectAsync("alice");
        removed.Should().BeGreaterThan(0);

        (await host.Client.SendAsync(host.Request(HttpMethod.Get, "/bff/auth/me", cookie)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── T-7: セッション → 下流への資格情報伝播（IADR-0273 決定 4）

    // ★ 陽性: セッション認証のリクエストでは、チケットのアクセストークンが Authorization に昇格する。
    [Fact]
    public async Task Session_access_token_is_promoted_to_the_authorization_header()
    {
        await using var host = await SessionTestHost.StartAsync();
        var cookie = await host.SignInAsync("alice");

        var resp = await host.Client.SendAsync(
            host.Request(HttpMethod.Get, "/test/echo-auth", cookie));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Be("Bearer AT-1");
    }

    // ★ 陰性: セッションが無ければヘッダは立たない（「常に付ける実装」を落とす対照）。
    [Fact]
    public async Task No_session_means_no_authorization_header()
    {
        await using var host = await SessionTestHost.StartAsync();

        var resp = await host.Client.GetAsync("/test/echo-auth-anon");

        (await resp.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    // ★ 陰性: 既に Bearer を運ぶ呼び出し（サービス間）は上書きしない。
    [Fact]
    public async Task Existing_bearer_header_is_not_overwritten()
    {
        await using var host = await SessionTestHost.StartAsync();
        var cookie = await host.SignInAsync("alice");

        var req = host.Request(HttpMethod.Get, "/test/echo-auth-anon", cookie);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-token");
        var resp = await host.Client.SendAsync(req);

        (await resp.Content.ReadAsStringAsync()).Should().Be("Bearer caller-token");
    }

    // ── T-1 の第 2 経路 ＋ refresh（IADR-0273 決定 3）

    // ★ 陽性: 期限切れでも refresh が通れば継続し、**新しい**トークンが下流へ渡る。
    [Fact]
    public async Task Expired_access_token_is_refreshed_and_the_new_token_flows_downstream()
    {
        await using var host = await SessionTestHost.StartAsync();
        host.TokenEndpointResponder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"access_token":"AT-2","refresh_token":"RT-2","expires_in":300}""",
                System.Text.Encoding.UTF8, "application/json"),
        };
        var cookie = await host.SignInAsync(
            "alice", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1)
                .ToString("o", CultureInfo.InvariantCulture));

        var resp = await host.Client.SendAsync(
            host.Request(HttpMethod.Get, "/test/echo-auth", cookie));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Be("Bearer AT-2");
    }

    // 🔴 陰性（第 2 の即時失効経路）: 認可サーバが refresh を拒む（＝無効化・失効・期限切れ）と、
    // **その場で 401** になり、同じ Cookie は以後も通らない。
    [Fact]
    public async Task Refused_refresh_kills_the_session_immediately()
    {
        await using var host = await SessionTestHost.StartAsync();
        host.TokenEndpointResponder = _ => new HttpResponseMessage(HttpStatusCode.BadRequest);
        var cookie = await host.SignInAsync(
            "alice", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1)
                .ToString("o", CultureInfo.InvariantCulture));

        (await host.Client.SendAsync(host.Request(HttpMethod.Get, "/test/echo-auth", cookie)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await host.Client.SendAsync(host.Request(HttpMethod.Get, "/bff/auth/me", cookie)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "チケットはストアから消えている");
    }

    // ── T-3: ログアウト（GET ＋ sid 一致。IADR-0273 決定 6）

    // ★ 陽性: 正しい sid なら、ローカルセッションを終え認可サーバの end-session へ 302 する。
    // その後、同じ Cookie は通らない（チケットが消えている）。
    [Fact]
    public async Task Logout_with_the_correct_sid_signs_out_and_redirects_to_the_end_session()
    {
        await using var host = await SessionTestHost.StartAsync();
        var cookie = await host.SignInAsync("alice", sid: "sess-9");

        var resp = await host.Client.SendAsync(host.Request(
            HttpMethod.Get, "/bff/auth/logout?sid=sess-9", cookie));

        resp.StatusCode.Should().Be(HttpStatusCode.Found);
        resp.Headers.Location!.ToString().Should().StartWith(SessionTestHost.EndSessionEndpoint);
        (await host.Client.SendAsync(host.Request(HttpMethod.Get, "/bff/auth/me", cookie)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ★ 陰性: sid 不一致・欠落は 400 で、**セッションは生き続ける**（強制ログアウト CSRF を防ぐ）。
    [Theory]
    [InlineData("/bff/auth/logout?sid=wrong")]
    [InlineData("/bff/auth/logout")]
    public async Task Logout_with_a_wrong_or_missing_sid_is_rejected_and_the_session_survives(
        string path)
    {
        await using var host = await SessionTestHost.StartAsync();
        var cookie = await host.SignInAsync("alice", sid: "sess-9");

        (await host.Client.SendAsync(host.Request(HttpMethod.Get, path, cookie)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await host.Client.SendAsync(host.Request(HttpMethod.Get, "/bff/auth/me", cookie)))
            .StatusCode.Should().Be(HttpStatusCode.OK, "拒否はログアウトさせない");
    }

    // ★ 未認証のログアウトは 401（sid を知っていても Cookie が無ければ何も起きない）。
    [Fact]
    public async Task Logout_without_a_session_is_401()
    {
        await using var host = await SessionTestHost.StartAsync();

        (await host.Client.GetAsync("/bff/auth/logout?sid=anything"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
