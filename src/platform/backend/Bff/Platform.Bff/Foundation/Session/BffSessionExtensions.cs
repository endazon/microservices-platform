using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using StackExchange.Redis;
using System.Security.Claims;

namespace Platform.Bff.Foundation.Session;

// NFR, ADR-0032, IADR-0249, #439: BFF セッション（Token Handler パターン）の配線。
//
// 🔴 **ここで上書きしている既定値は、すべて実測して「既定のままでは要件を満たさない」と判った
// ものである**（IADR-0249 §前提の実測）。**「既定で良さそう」で省略しないこと。**
public static class BffSessionExtensions
{
    /// <summary>認証スキーム名。JwtBearer（サービス間）とは別に持つ。</summary>
    public const string SessionScheme = "BffSession";

    public static IServiceCollection AddBffSession(
        this IServiceCollection services, IConfiguration config)
    {
        var options = new BffSessionOptions();
        config.GetSection(BffSessionOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        // ── セッション実体の置き場（IADR-0249 決定 4）
        services.AddStackExchangeRedisCache(o => o.Configuration = options.RedisConnectionString);
        services.AddSingleton<RedisTicketStore>();

        // ── DataProtection の鍵リング（IADR-0249 決定 5）
        //
        // 🔴 **共有しないと、リクエストが別レプリカへ振られた瞬間に Cookie を復号できず、
        // ログアウトしたように見える。単一プロセスの単体テストでは絶対に捕まらない**
        // （鍵リングが 1 つしか無いので共有し忘れていても緑になる）。
        // **接続は遅延させる。** 登録時に Connect すると、セッションを使わないテストや
        // Redis 不在の環境でも起動時に落ちる（＝配線の都合でテストが Redis を要求することになる）。
        var lazyRedis = new Lazy<IConnectionMultiplexer>(
            () => ConnectionMultiplexer.Connect(options.RedisConnectionString));
        services.AddSingleton<IConnectionMultiplexer>(_ => lazyRedis.Value);
        services.AddDataProtection()
            .PersistKeysToStackExchangeRedis(() => lazyRedis.Value.GetDatabase(), "bff:dataprotection-keys")
            .SetApplicationName("microservices-platform-bff");

        // 🔴 **既定スキームを変えない。** `AddAuthentication(scheme)` の引数つき版は
        // `DefaultScheme` を上書きするため、先に走る `AddPlatformAuth`（JwtBearer）を奪ってしまい、
        // **既存の BFF 端点が一斉に Cookie を要求し始める**（＝現行 SPA と既存テストが全部落ちる）。
        // 第 3 段は 2 つに分かれており、**本段（3a）は受け皿を足すだけで切り替えない。**
        // 既定を BffSession へ移すのは 3b（SPA 側の切り替えと `oidc-client-ts` 撤去）である。
        services.AddAuthentication()
            .AddCookie(SessionScheme, o =>
            {
                o.Cookie.Name = options.CookieName;
                o.Cookie.HttpOnly = true;
                // 実測: 既定は SameAsRequest。ADR-0032 §決定 は Secure を要求するので固定する。
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                // ADR-0032 §決定 と IADR-0249 決定 1（CSRF の 1 枚目の壁）。
                o.Cookie.SameSite = SameSiteMode.Lax;
                // `__Host-` 接頭辞の条件（Secure ＋ Path=/ ＋ Domain 無し）を満たす。
                o.Cookie.Path = "/";
                // 実測: 既定は 14 日。**根拠が無い値なので realm 由来の構成値へ揃える**（決定 6）。
                o.ExpireTimeSpan = TimeSpan.FromSeconds(options.SessionLifetimeSeconds);
                o.SlidingExpiration = false;

                // SessionStore は DI から差し込む（下の AddOptions を参照）。
                // **ここで BuildServiceProvider() を呼ばない** —— 2 つ目のコンテナができ、
                // シングルトンが二重に作られる（Redis 接続も鍵リングも二重になる）。

                // BFF は API である。未認証時に HTML のログイン画面へ 302 せず、401 を返す
                // （SPA 側が自分でログイン導線を出す）。
                o.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            })
            .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, o =>
            {
                o.SignInScheme = SessionScheme;

                if (!string.IsNullOrWhiteSpace(options.MetadataAddress)) o.MetadataAddress = options.MetadataAddress;
                else o.Authority = options.Authority;
                o.RequireHttpsMetadata = options.RequireHttpsMetadata;

                o.ClientId = options.ClientId;
                // ADR-0032 §決定: BFF はコンフィデンシャルクライアントである。
                o.ClientSecret = options.ClientSecret;

                // 🔴 実測: 既定は `id_token` である。**`code` ではない。**
                // 明示しないと ADR-0032 が要求する Authorization Code + PKCE とは別のフローで動く。
                o.ResponseType = OpenIdConnectResponseType.Code;
                o.UsePkce = true;

                // 🔴 実測: 既定は `form_post` で、`ResponseType=code` にしても変わらない（決定 2）。
                // form_post だとコールバックが**クロスサイト POST** になり、correlation / nonce Cookie を
                // `SameSite=None`（＝ Secure 必須）にせざるを得ず、**平文 http のローカル開発が壊れる。**
                // `query` ならトップレベル GET リダイレクトになり、すべて Lax で通る。
                o.ResponseMode = OpenIdConnectResponseMode.Query;
                o.CorrelationCookie.SameSite = SameSiteMode.Lax;
                o.NonceCookie.SameSite = SameSiteMode.Lax;

                // 🔴 実測: 既定は `/signin-oidc` 等である。**エッジは `/bff` と `/bff/` しか BFF へ
                // 通さない**ため、既定のままでは認可サーバからのコールバックが BFF に届かない（決定 3）。
                o.CallbackPath = "/bff/auth/callback";
                o.SignedOutCallbackPath = "/bff/auth/logout-callback";
                o.RemoteSignOutPath = "/bff/auth/backchannel-logout";

                // 実測: 既定は false。トークンを BFF 側（＝チケット＝Redis）に持つために必要。
                o.SaveTokens = true;
                o.GetClaimsFromUserInfoEndpoint = true;

                o.TokenValidationParameters.NameClaimType = "preferred_username";
                o.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;
                if (ParseIssuers(options.ValidIssuers) is { Length: > 0 } issuers)
                    o.TokenValidationParameters.ValidIssuers = issuers;

                o.Scope.Clear();
                o.Scope.Add("openid");
                o.Scope.Add("profile");
                o.Scope.Add("email");
                // リフレッシュトークンを BFF 側に保持するため（ブラウザには一切出さない）。
                o.Scope.Add("offline_access");
            });

        // 🔴 IADR-0249 決定 4: `SessionStore` を DI 経由で差し込む。
        // これが無いとチケットは Cookie 本体に載り（実測した既定）、**サーバ側に消す対象が無いため
        // 「全セッション即時失効」が構造的に実現できない。**
        services.AddOptions<CookieAuthenticationOptions>(SessionScheme)
            .Configure<RedisTicketStore>((o, store) => o.SessionStore = store);

        return services;
    }

    private static string[] ParseIssuers(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split([',', ' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
