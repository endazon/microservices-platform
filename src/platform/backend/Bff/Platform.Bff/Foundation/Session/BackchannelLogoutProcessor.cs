using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.Text.Json;

namespace Platform.Bff.Foundation.Session;

// NFR, ADR-0032, IADR-0273, #439: バックチャネルログアウト（OIDC Back-Channel Logout 1.0）の処理本体。
//
// 🔴 **「全セッション即時失効」の入口はここである。** 管理者がアカウントを無効化・全セッション失効
// させると、認可サーバは各クライアントの `backchannel.logout.url` へ **サーバ間 POST**（`logout_token`）
// を送る。この要求は**利用者の Cookie を運ばない**ため、フレームワーク既定の remote-signout 処理
// （「今の要求のセッション」をサインアウトする）では**何も失効しない**。ここで `logout_token` を
// 検証し、**セッションストア側を subject 単位で削除する**（IADR-0251 決定 4 の削除がここで起きる）。
//
// **失効は subject 単位（＝その利用者の全セッション）である。** logout_token は `sid`（個別セッション）
// を運ぶが、チケットストアの索引は subject 単位であり、**過剰失効側（安全側）へ倒す**（IADR-0273 決定 2）。
public sealed class BackchannelLogoutProcessor(
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    RedisTicketStore store,
    ILogger<BackchannelLogoutProcessor> logger)
{
    /// <summary>OIDC Back-Channel Logout 1.0 §2.4 が定めるイベント名。</summary>
    public const string BackchannelLogoutEvent = "http://schemas.openid.net/event/backchannel-logout";

    /// <summary>
    /// `logout_token` を検証し、正当なら対象 subject の全セッションを失効させる。
    /// 戻り値は「処理として受理したか」（HTTP 200/400 に写像される）。
    /// </summary>
    public async Task<bool> ProcessAsync(string logoutToken, CancellationToken ct)
    {
        var options = oidcOptions.Get(OpenIdConnectDefaults.AuthenticationScheme);
        var configuration = options.Configuration;
        if (configuration is null && options.ConfigurationManager is not null)
            configuration = await options.ConfigurationManager.GetConfigurationAsync(ct);
        if (configuration is null)
        {
            logger.LogWarning("Backchannel logout rejected: OIDC configuration unavailable.");
            return false;
        }

        var jwt = await ValidateAsync(logoutToken, options, configuration);
        if (jwt is null) return false;

        // sub は必須とする（仕様は sub / sid の少なくとも一方だが、ストアの索引は subject 単位。
        // sub 無しの token は失効対象を解決できないので**受理しない**＝fail-closed）。
        var subject = jwt.Subject;
        if (string.IsNullOrEmpty(subject))
        {
            logger.LogWarning("Backchannel logout rejected: no sub claim.");
            return false;
        }

        var removed = await store.RemoveAllForSubjectAsync(subject);
        logger.LogInformation(
            "Backchannel logout: revoked {Count} session(s) for subject {Subject}.", removed, subject);
        return true;
    }

    /// <summary>
    /// OIDC Back-Channel Logout 1.0 §2.6 の検証。署名・iss・aud・寿命に加え、
    /// **`events` にログアウトイベントがあること**と **`nonce` が無いこと**（ID トークンとの
    /// すり替え防止。仕様が明示的に禁止）を確かめる。
    /// </summary>
    private async Task<JsonWebToken?> ValidateAsync(
        string logoutToken, OpenIdConnectOptions options, OpenIdConnectConfiguration configuration)
    {
        // ValidIssuers: metadata の issuer に、構成の追加許可（エッジ host issuer。IADR-0086 と同型）を併合する。
        var issuers = new List<string> { configuration.Issuer };
        if (options.TokenValidationParameters.ValidIssuers is { } extra)
            issuers.AddRange(extra);

        var parameters = new TokenValidationParameters
        {
            ValidIssuers = issuers,
            ValidAudience = options.ClientId,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidateLifetime = true,
            RequireExpirationTime = true,
        };

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(logoutToken, parameters);
        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
        {
            logger.LogWarning(result.Exception, "Backchannel logout rejected: token validation failed.");
            return null;
        }

        if (jwt.TryGetClaim("nonce", out _))
        {
            logger.LogWarning("Backchannel logout rejected: nonce present (must be an ID token).");
            return null;
        }

        if (!jwt.TryGetClaim("events", out var events) || !HasLogoutEvent(events.Value))
        {
            logger.LogWarning("Backchannel logout rejected: missing backchannel-logout event.");
            return null;
        }

        return jwt;
    }

    // `events` は {"http://schemas.openid.net/event/backchannel-logout": {}} という JSON オブジェクト。
    // 文字列一致ではなく**プロパティ名として**確かめる（値の中に URL が現れるだけの JSON を通さない）。
    internal static bool HasLogoutEvent(string eventsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(eventsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(BackchannelLogoutEvent, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
