using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using StackExchange.Redis;
using System.Security.Claims;

namespace Platform.Bff.Foundation.Session;

// NFR, ADR-0032, IADR-0251, #439: BFF セッション（Token Handler パターン）の配線。
//
// 🔴 **ここで上書きしている既定値は、すべて実測して「既定のままでは要件を満たさない」と判った
// ものである**（IADR-0251 §前提の実測）。**「既定で良さそう」で省略しないこと。**
public static class BffSessionExtensions
{
    /// <summary>認証スキーム名。JwtBearer（サービス間）とは別に持つ。</summary>
    public const string SessionScheme = "BffSession";

    /// <summary>
    /// 既定の振り分けスキーム。`Authorization: Bearer` が在れば JwtBearer、無ければ
    /// セッション Cookie へ委ねる。**両方を受理する**ための入口（IADR-0251 決定 9）。
    /// </summary>
    public const string SmartScheme = "BffSmart";

    public static IServiceCollection AddBffSession(
        this IServiceCollection services, IConfiguration config)
    {
        var options = new BffSessionOptions();
        config.GetSection(BffSessionOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        // ── セッション実体の置き場（IADR-0251 決定 4）
        services.AddStackExchangeRedisCache(o => o.Configuration = options.RedisConnectionString);
        services.AddSingleton<RedisTicketStore>();

        // ── ［3b］失効・refresh の処理系（IADR-0273）。TimeProvider は本番時計を既定にし、
        // テストが差し替える（既に登録済みなら尊重する）。
        services.AddHttpClient();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SessionTokenRefresher>();
        services.AddSingleton<BackchannelLogoutProcessor>();

        // ── DataProtection の鍵リング（IADR-0251 決定 5）
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

        // 🔴 **［3b］既定は「振り分けスキーム」にする。Cookie と Bearer の**両方**を受理する。**
        //
        // 素直に `AddAuthentication(SessionScheme)` とすると、**スキームを指定しない端点は
        // Cookie しか見なくなり、`/bff/*` への Bearer 呼び出しが 401 になる**（実測。
        // `DefaultSchemeRoutingTests` が固定している）。`scripts/verify-oidc-edge-flow.sh` は
        // `/bff/*` を Bearer で 4 箇所叩いており、**統合スタックで動いている唯一の外形確認**である。
        // 移行の副作用でそれを失わない。
        //
        // 🔴 **これは計画が許した形ではなく、実装側の判断である。** ADR-0032 が禁じているのは
        // **SPA がトークンを扱うこと**であって、非ブラウザの呼び出し口が `/bff/*` を Bearer で
        // 叩くことではない（原文に言及が無い）。**言及が無いことは許可ではない**ので、
        // 移行期の姿勢として採り、狭める条件を [[IADR-0251]] 決定 9 に書く。
        //
        // **振り分けスキームにするのは、既定ポリシーだけでは足りないからである。**
        // 端点が `RequireAuthorization(p => p.RequireRole(...))` で作る内側のポリシーは
        // **スキームを持たない**ため、`context.User` が誰から作られたかに依存する。
        // ここで振り分けておくと、**既定ポリシーの端点もロール要求の端点も同じように**両方を受理する。
        services.AddAuthentication(SmartScheme)
            .AddPolicyScheme(SmartScheme, SmartScheme, o =>
            {
                o.ForwardDefaultSelector = ctx =>
                    ctx.Request.Headers.Authorization.ToString()
                        .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? JwtBearerDefaults.AuthenticationScheme
                        : SessionScheme;
            })
            .AddCookie(SessionScheme, o =>
            {
                o.Cookie.Name = options.CookieName;
                o.Cookie.HttpOnly = true;
                // 実測: 既定は SameAsRequest。ADR-0032 §決定 は Secure を要求するので固定する。
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                // ADR-0032 §決定 と IADR-0251 決定 1（CSRF の 1 枚目の壁）。
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

                // ── ［3b］アクセストークンの refresh（IADR-0273 決定 3）。
                // セッション（30 日）はアクセストークン（分単位）より桁で長い。毎認証時に期限を見て
                // 更新し、**refresh を拒まれたらその場でセッションを殺す**（無効化の第 2 の即時失効経路）。
                o.Events.OnValidatePrincipal = ctx => ctx.HttpContext.RequestServices
                    .GetRequiredService<SessionTokenRefresher>().ValidatePrincipalAsync(ctx);
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
                // 🔴 **`offline_access` は要求しない**（3a からの是正。IADR-0273 決定 3）。
                // オフライントークンは **SSO セッションが終了しても生き残る** —— 「無効化・退職時に
                // 全セッション即時失効」と**逆向き**の性質である。通常（セッション連動）の refresh token は
                // Keycloak 側のセッション失効と同時に死ぬので、refresh 拒否 → セッション破棄の
                // 失効経路（SessionTokenRefresher）がそのまま効く。code フローの confidential client には
                // `offline_access` 無しでも refresh token が発行される（Keycloak の既定）。

                // ── ［3b］Cookie セッションの principal にレルムロールを載せる（IADR-0273 決定 5）。
                //
                // 🔴 realm の `roles` クライアントスコープは、既定では realm_access を**アクセストークン
                // にだけ**入れる（id_token / userinfo には入らない）。何もしないと Cookie 経路の
                // `RequireRole` / `/bff/auth/me` のロールが**空**になり、管理画面が全員 403 になる。
                // コード交換で受け取ったアクセストークン（**認可サーバから TLS 直で受けたもの**）から
                // `realm_access` を principal へ複写する。展開そのものは既存の
                // KeycloakRolesClaimsTransformation が毎リクエスト行う（複写はその入力を置くだけ）。
                o.Events.OnTokenValidated = ctx =>
                {
                    var accessToken = ctx.TokenEndpointResponse?.AccessToken;
                    if (string.IsNullOrEmpty(accessToken)
                        || ctx.Principal?.Identity is not ClaimsIdentity identity
                        || identity.HasClaim(c => c.Type == "realm_access"))
                        return Task.CompletedTask;
                    try
                    {
                        var jwt = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(accessToken);
                        if (jwt.TryGetClaim("realm_access", out var realmAccess))
                            identity.AddClaim(new Claim("realm_access", realmAccess.Value));
                    }
                    catch (ArgumentException)
                    {
                        // 解析不能なら付与しない（fail-closed: ロール無しのまま通す）。
                    }
                    return Task.CompletedTask;
                };

                // ── ［3b］バックチャネルログアウトの受け口（IADR-0273 決定 2）。
                //
                // 🔴 フレームワーク既定の remote-signout 処理は「リクエストが運ぶ Cookie のセッション」
                // しか消せない。認可サーバからの **サーバ間 POST（Cookie 無し・logout_token）** では
                // **何も失効しない**ため、ここで処理を乗っ取り、logout_token を検証して
                // **subject の全セッションをストアから削除する**。端点は増やさない
                // （`/bff/*` の無認証端点を増やさない不変条件（check-bff-authz-docs）と整合させるため、
                // 認証ハンドラの領分で処理する）。
                o.Events.OnRemoteSignOut = async ctx =>
                {
                    var logoutToken = ctx.ProtocolMessage?.GetParameter("logout_token");
                    if (string.IsNullOrEmpty(logoutToken))
                        return; // front-channel 形（sid/iss のみ）は既定処理に委ねる。

                    ctx.HandleResponse();
                    var accepted = await ctx.HttpContext.RequestServices
                        .GetRequiredService<BackchannelLogoutProcessor>()
                        .ProcessAsync(logoutToken, ctx.HttpContext.RequestAborted);
                    ctx.Response.StatusCode = accepted
                        ? StatusCodes.Status200OK
                        : StatusCodes.Status400BadRequest;
                    ctx.Response.Headers.CacheControl = "no-store";
                };
            });

        // 🔴 IADR-0251 決定 4: `SessionStore` を DI 経由で差し込む。
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
