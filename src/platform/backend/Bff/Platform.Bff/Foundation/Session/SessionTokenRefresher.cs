using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;

namespace Platform.Bff.Foundation.Session;

// NFR, ADR-0032, IADR-0273 決定 3, #439: セッション内アクセストークンの refresh。
//
// セッションの寿命（realm の「記憶」30 日）はアクセストークンの寿命（realm の accessTokenLifespan）
// より桁で長い。refresh が無いと、**ログインから数分で下流サービス呼び出しが全部 401 になる**。
// Cookie 認証の `OnValidatePrincipal`（毎リクエストの認証時）で期限を見て、切れていれば
// refresh_token グラントで更新し、チケットを書き戻す（`ShouldRenew` → `RedisTicketStore.RenewAsync`）。
//
// 🔴 **refresh の失敗は「セッションの死」として扱う（fail-closed）。** 認可サーバがアカウント無効化・
// セッション失効・パスワードリセットを行うと refresh token グラントが拒否される。これは
// バックチャネルログアウト（第 1 経路）が届かなかった場合の**第 2 の即時失効経路**である。
// RejectPrincipal ＋ SignOut でチケットをストアから消し、**その場で 401 にする**。
// ネットワーク断など一過性の失敗でもログアウト側へ倒す —— 可用性より失効の確実性を採る
// （トレードオフの論拠は IADR-0273 決定 3）。
public sealed class SessionTokenRefresher(
    IHttpClientFactory httpFactory,
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    TimeProvider clock,
    ILogger<SessionTokenRefresher> logger)
{
    /// <summary>token endpoint 呼び出し用の名前付き HttpClient（テストで差し替える継ぎ目）。</summary>
    public const string HttpClientName = "BffOidcToken";

    /// <summary>期限ぎりぎりの下流呼び出しが途中で切れないよう、先回りして更新する余白。</summary>
    internal static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

    public async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal?.Identity?.IsAuthenticated != true) return;

        // expires_at はトークン保存時（SaveTokens）に必ず書かれる。持たないチケット
        // （トークンを伴わないテスト用サインイン等）は refresh の対象外として素通しする。
        var expiresAt = context.Properties.GetTokenValue("expires_at");
        if (!NeedsRefresh(expiresAt, clock.GetUtcNow())) return;

        var refreshToken = context.Properties.GetTokenValue("refresh_token");
        if (string.IsNullOrEmpty(refreshToken))
        {
            await RejectAsync(context, "expired access token and no refresh token");
            return;
        }

        var refreshed = await RequestRefreshAsync(refreshToken, context.HttpContext.RequestAborted);
        if (refreshed is null)
        {
            // 🔴 認可サーバが拒んだ＝無効化・失効・期限切れのいずれか。**セッションを即時に殺す。**
            await RejectAsync(context, "token endpoint refused the refresh");
            return;
        }

        context.Properties.UpdateTokenValue("access_token", refreshed.AccessToken);
        if (!string.IsNullOrEmpty(refreshed.RefreshToken))
            context.Properties.UpdateTokenValue("refresh_token", refreshed.RefreshToken);
        if (!string.IsNullOrEmpty(refreshed.IdToken))
            context.Properties.UpdateTokenValue("id_token", refreshed.IdToken);
        context.Properties.UpdateTokenValue(
            "expires_at",
            clock.GetUtcNow().AddSeconds(refreshed.ExpiresIn)
                .ToString("o", CultureInfo.InvariantCulture));
        // ShouldRenew → Cookie ハンドラが SessionStore.RenewAsync を呼び、Redis のチケットが更新される。
        context.ShouldRenew = true;
    }

    /// <summary>期限（`expires_at`・round-trip 形式）がスキュー内へ迫っている／過ぎているか。</summary>
    internal static bool NeedsRefresh(string? expiresAt, DateTimeOffset now) =>
        expiresAt is not null
        && DateTimeOffset.TryParse(
            expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
        && at - now <= ExpirySkew;

    private async Task RejectAsync(CookieValidatePrincipalContext context, string reason)
    {
        logger.LogInformation("BFF session terminated: {Reason}.", reason);
        context.RejectPrincipal();
        // チケットをストアから消す（Cookie の削除だけでは Redis 側に残骸が残る）。
        await context.HttpContext.SignOutAsync(BffSessionExtensions.SessionScheme);
    }

    internal sealed record RefreshedTokens(
        string AccessToken, string? RefreshToken, string? IdToken, double ExpiresIn);

    private async Task<RefreshedTokens?> RequestRefreshAsync(string refreshToken, CancellationToken ct)
    {
        try
        {
            var options = oidcOptions.Get(OpenIdConnectDefaults.AuthenticationScheme);
            var configuration = options.Configuration;
            if (configuration is null && options.ConfigurationManager is not null)
                configuration = await options.ConfigurationManager.GetConfigurationAsync(ct);
            if (string.IsNullOrEmpty(configuration?.TokenEndpoint))
            {
                logger.LogWarning("Token refresh failed: token endpoint unknown.");
                return null;
            }

            var client = httpFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsync(
                configuration.TokenEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = options.ClientId ?? string.Empty,
                    ["client_secret"] = options.ClientSecret ?? string.Empty,
                }),
                ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Token refresh refused by the authorization server ({Status}).",
                    (int)response.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (!root.TryGetProperty("access_token", out var accessToken)
                || accessToken.GetString() is not { Length: > 0 } at)
                return null;

            return new RefreshedTokens(
                at,
                root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
                root.TryGetProperty("id_token", out var idt) ? idt.GetString() : null,
                root.TryGetProperty("expires_in", out var exp) && exp.TryGetDouble(out var seconds)
                    ? seconds
                    : 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Token refresh failed.");
            return null;
        }
    }
}
