using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Platform.Shared.Infrastructure.Foundation.Grpc;

// NFR-09, ADR-0004, IADR-0379 決定 4 (#1201): OAuth2 client credentials で s2s トークンを取り、
// 期限（`expires_in`）まで再利用する。期限の `RefreshSkewSeconds` 手前で取り直す。
//
// 取得失敗（非 2xx・不達・本文不正）は例外にする。呼び出し側（AuthzScopeGrpcClient）はこれを
// 「資格情報が無い」として deny-by-default（null）へ縮退させる —— 静かに匿名で呼ばない。
public sealed class ClientCredentialsServiceTokenProvider(
    IHttpClientFactory httpFactory,
    IOptions<ServiceTokenOptions> options,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<ClientCredentialsServiceTokenProvider> logger) : IServiceTokenProvider
{
    public const string HttpClientName = "ServiceToken";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _refreshAt = DateTimeOffset.MinValue;

    public async ValueTask<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && timeProvider.GetUtcNow() < _refreshAt)
            return _token;

        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null && timeProvider.GetUtcNow() < _refreshAt)
                return _token;

            var (token, expiresIn) = await FetchAsync(ct);
            _token = token;
            var skew = Math.Max(0, options.Value.RefreshSkewSeconds);
            _refreshAt = timeProvider.GetUtcNow().AddSeconds(Math.Max(0, expiresIn - skew));
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    // トークン端点の解決。明示指定 → `Auth:Authority` 由来 の順。どちらも無ければ構成誤り。
    internal static string ResolveTokenEndpoint(ServiceTokenOptions opts, IConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(opts.TokenEndpoint))
            return opts.TokenEndpoint;

        var authority = configuration["Auth:Authority"];
        if (string.IsNullOrWhiteSpace(authority))
            throw new InvalidOperationException(
                $"{ServiceTokenOptions.SectionName}:TokenEndpoint も Auth:Authority も未設定です。s2s トークンの取得先が決まりません。");

        return authority.TrimEnd('/') + "/protocol/openid-connect/token";
    }

    private async Task<(string Token, int ExpiresIn)> FetchAsync(CancellationToken ct)
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.ClientId) || string.IsNullOrWhiteSpace(opts.ClientSecret))
            throw new InvalidOperationException(
                $"{ServiceTokenOptions.SectionName}:ClientId / ClientSecret が未設定です。呼び出し側サービスの資格情報が無いので east-west gRPC を呼べません。");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = opts.ClientId,
            ["client_secret"] = opts.ClientSecret,
        };
        if (!string.IsNullOrWhiteSpace(opts.Scope))
            form["scope"] = opts.Scope;

        var client = httpFactory.CreateClient(HttpClientName);
        using var resp = await client.PostAsync(
            ResolveTokenEndpoint(opts, configuration), new FormUrlEncodedContent(form), ct);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("s2s トークンの取得に失敗しました（HTTP {Status}）。", (int)resp.StatusCode);
            throw new InvalidOperationException($"s2s トークンの取得に失敗しました（HTTP {(int)resp.StatusCode}）。");
        }

        var body = await resp.Content.ReadFromJsonAsync<TokenResponse>(ct);
        if (body is null || string.IsNullOrWhiteSpace(body.access_token))
            throw new InvalidOperationException("s2s トークンの応答に access_token がありません。");

        return (body.access_token, body.expires_in);
    }

    // トークン端点の応答（必要な 2 項目だけ）。
#pragma warning disable IDE1006 // OAuth2 の JSON 名に合わせる
    private sealed record TokenResponse(string? access_token, int expires_in);
#pragma warning restore IDE1006
}
