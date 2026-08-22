using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Platform.Bff.Foundation.Session;

namespace Platform.Bff.Tests;

// NFR, ADR-0032, IADR-0249, #439 第 3 段(3a): **実測した既定値の上書きを固定する。**
//
// 🔴 **本ファイルの存在理由は「既定値のままでは要件を満たさない」ことが実測で判ったからである。**
// 6 つの既定を上書きしており、**どれも黙って戻ると要件が静かに壊れる**:
//   - ResponseType の既定は id_token → 計画が要求する Authorization Code + PKCE と別のフローになる
//   - ResponseMode の既定は form_post → コールバックがクロスサイト POST になり平文 http が壊れる
//   - CallbackPath の既定は /signin-oidc → エッジが /bff 以外を BFF へ通さないので永久に届かない
//   - SessionStore の既定は null → サーバ側に消す対象が無く「全セッション即時失効」が実現できない
//   - Cookie.SecurePolicy の既定は SameAsRequest → 平文で Cookie が出る
//   - ExpireTimeSpan の既定は 14 日 → realm の値と無関係
//
// **いずれも実行時にしか現れず、上書きを消しても他のテストは緑のままである。**
public class BffSessionConfigurationTests
{
    private static ServiceProvider Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BffSession:Authority"] = "http://keycloak:8080/realms/platform",
                ["BffSession:ClientId"] = "bff",
                ["BffSession:ClientSecret"] = "test-secret",
                ["BffSession:RedisConnectionString"] = "localhost:6379",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBffSession(config);
        return services.BuildServiceProvider();
    }

    private static OpenIdConnectOptions Oidc(ServiceProvider sp) =>
        sp.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
          .Get(OpenIdConnectDefaults.AuthenticationScheme);

    private static CookieAuthenticationOptions Cookie(ServiceProvider sp) =>
        sp.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
          .Get(BffSessionExtensions.SessionScheme);

    // ★ ADR-0032 §決定: Authorization Code + PKCE。**既定は id_token なので明示が要る。**
    [Fact]
    public void Oidc_uses_authorization_code_with_pkce_not_the_id_token_default()
    {
        using var sp = Build();
        var o = Oidc(sp);

        o.ResponseType.Should().Be(OpenIdConnectResponseType.Code);
        o.UsePkce.Should().BeTrue();
    }

    // ★ IADR-0249 決定 2: form_post だと correlation / nonce が SameSite=None（＝Secure 必須）になり、
    // 平文 http のローカル開発でログインが壊れる。
    [Fact]
    public void Callback_is_received_by_query_so_correlation_cookies_can_stay_lax()
    {
        using var sp = Build();
        var o = Oidc(sp);

        o.ResponseMode.Should().Be(OpenIdConnectResponseMode.Query);
        o.CorrelationCookie.SameSite.Should().Be(SameSiteMode.Lax);
        o.NonceCookie.SameSite.Should().Be(SameSiteMode.Lax);
    }

    // ★ IADR-0249 決定 3: エッジは /bff と /bff/ しか BFF へ通さない。
    // 既定（/signin-oidc 等）のままでは認可サーバからのコールバックが BFF に永久に届かない。
    [Fact]
    public void All_oidc_paths_live_under_the_bff_prefix_that_the_edge_routes()
    {
        using var sp = Build();
        var o = Oidc(sp);

        o.CallbackPath.Value.Should().StartWith("/bff/");
        o.SignedOutCallbackPath.Value.Should().StartWith("/bff/");
        o.RemoteSignOutPath.Value.Should().StartWith("/bff/");
    }

    // ★ IADR-0249 決定 4: **これが「全セッション即時失効」の実現手段である。**
    // 既定（null）だとチケットが Cookie 本体に載り、サーバ側に消す対象が無い。
    [Fact]
    public void Session_lives_server_side_so_it_can_be_revoked_immediately()
    {
        using var sp = Build();

        Cookie(sp).SessionStore.Should().NotBeNull(
            "チケットが Cookie 本体に載ると、サーバ側に消す対象が無く即時失効が実現できない");
        Cookie(sp).SessionStore.Should().BeOfType<RedisTicketStore>();
    }

    // ★ ADR-0032 §決定 の Cookie 属性。既定の SecurePolicy は SameAsRequest（＝平文でも出る）。
    [Fact]
    public void Session_cookie_is_httponly_secure_and_lax()
    {
        using var sp = Build();
        var c = Cookie(sp).Cookie;

        c.HttpOnly.Should().BeTrue();
        c.SecurePolicy.Should().Be(CookieSecurePolicy.Always);
        c.SameSite.Should().Be(SameSiteMode.Lax);
        // `__Host-` 接頭辞は Secure ＋ Path=/ ＋ Domain 無しをブラウザ側で強制する。
        c.Name.Should().StartWith("__Host-");
        c.Path.Should().Be("/");
    }

    // 🔴 ★ **3a は受け皿を足すだけで切り替えない。**
    // `AddAuthentication(scheme)` の引数つき版を使うと既定スキームを奪い、既存の BFF 端点が
    // 一斉に Cookie を要求し始める（現行 SPA と既存テストが全部落ちる）。切り替えは 3b。
    [Fact]
    public void Adding_the_session_scheme_does_not_steal_the_default_scheme_from_jwt_bearer()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        // 本番と同じ順序: 先に JwtBearer（AddPlatformAuth 相当）、あとから BFF セッション。
        services.AddAuthentication("Bearer");
        services.AddBffSession(config);

        using var sp = services.BuildServiceProvider();
        var auth = sp.GetRequiredService<IOptions<Microsoft.AspNetCore.Authentication.AuthenticationOptions>>().Value;

        // `AddAuthentication(scheme)` が設定するのは `DefaultScheme` である
        // （`DefaultAuthenticateScheme` は null のままで、解決時にこれへ落ちる）。
        auth.DefaultScheme.Should().Be("Bearer");
    }
}
