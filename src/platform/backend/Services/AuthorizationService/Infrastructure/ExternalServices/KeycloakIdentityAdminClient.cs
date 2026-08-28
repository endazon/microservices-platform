using AuthorizationService.Domain.Ports;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuthorizationService.Infrastructure.ExternalServices;

// FR-05, FR-09, UC-05, SC-17, ADR-0026, IADR-0301: Keycloak Admin REST による身元管理。
//
// 🔴 **実 Keycloak との疎通は未検証である（#452 の残件）。** 本環境に実 Keycloak が無く、
// `realm-management` ロールを持つ機密クライアントも realm export に未登録である。
// 本クラスはスタブした `HttpMessageHandler` に対してのみ検証した（`KeycloakIdentityAdminClientTests`）。
// **「緑である」ことは「実 IdP へ反映できる」ことを意味しない。**
//
// 認証は client_credentials（機密クライアント）。必要なクライアントロールは 3 つだけである
// （`view-users` / `manage-users` / `view-realm`。KeycloakAdminOptions のコメント参照）。
public sealed class KeycloakIdentityAdminClient(
    IHttpClientFactory httpClientFactory,
    KeycloakAdminOptions options,
    TimeProvider clock,
    ILogger<KeycloakIdentityAdminClient> logger) : IIdentityAdminClient
{
    // Keycloak が自分で作る合成ロール。**人が割り当てる対象ではない**ので値域から外す
    // （出すと「利用者に default-roles-platform を割り当てる」が画面から可能になる）。
    private static readonly string[] NonAssignableRoles =
        ["offline_access", "uma_authorization"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<IdentityUser>> ListUsersAsync(CancellationToken ct)
    {
        var client = await AuthorizedClientAsync(ct);
        // briefRepresentation=false でないと attributes が返らない（属性が空に見える罠）。
        var users = await client.GetFromJsonAsync<List<KeycloakUser>>(
            $"admin/realms/{Realm}/users?briefRepresentation=false&max=1000", Json, ct) ?? [];

        var result = new List<IdentityUser>(users.Count);
        foreach (var user in users)
        {
            if (string.IsNullOrEmpty(user.Id)) continue;
            var roles = await RealmRolesAsync(client, user.Id, ct);
            result.Add(ToIdentityUser(user, roles));
        }
        return result;
    }

    public async Task<IReadOnlyList<string>> ListAssignableRolesAsync(CancellationToken ct)
    {
        var client = await AuthorizedClientAsync(ct);
        var roles = await client.GetFromJsonAsync<List<KeycloakRole>>(
            $"admin/realms/{Realm}/roles", Json, ct) ?? [];
        return
        [
            .. roles
                .Select(r => r.Name)
                .Where(name => !string.IsNullOrEmpty(name))
                .Where(name => !name!.StartsWith("default-roles-", StringComparison.Ordinal))
                .Where(name => !NonAssignableRoles.Contains(name, StringComparer.Ordinal))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal)
        ];
    }

    public async Task<IdentityUser?> ReplaceAttributesAsync(
        string userId, IReadOnlyDictionary<string, string> attributes, CancellationToken ct)
    {
        var client = await AuthorizedClientAsync(ct);
        // Keycloak のユーザー属性は多値（キー → 値の配列）である。契約側は 1 キー 1 値なので
        // 単一要素の配列へ写す。**判定側（BffScopeResolver）も 1 値しか読まない**ので、
        // ここで多値を作ると読まれない値が静かに増える。
        var payload = new Dictionary<string, object?>
        {
            ["attributes"] = attributes.ToDictionary(kv => kv.Key, kv => new[] { kv.Value }),
        };
        return await UpdateAndReloadAsync(client, userId, payload, ct);
    }

    public async Task<IdentityUser?> SetEnabledAsync(string userId, bool enabled, CancellationToken ct)
    {
        var client = await AuthorizedClientAsync(ct);
        var payload = new Dictionary<string, object?> { ["enabled"] = enabled };
        return await UpdateAndReloadAsync(client, userId, payload, ct);
    }

    public async Task<IdentityUser?> ReplaceRealmRolesAsync(
        string userId, IReadOnlyList<string> roles, CancellationToken ct)
    {
        var client = await AuthorizedClientAsync(ct);
        var current = await client.GetAsync(
            $"admin/realms/{Realm}/users/{Uri.EscapeDataString(userId)}/role-mappings/realm", ct);
        if (current.StatusCode == HttpStatusCode.NotFound) return null;
        current.EnsureSuccessStatusCode();
        var assigned = await current.Content.ReadFromJsonAsync<List<KeycloakRole>>(Json, ct) ?? [];

        var available = await client.GetFromJsonAsync<List<KeycloakRole>>(
            $"admin/realms/{Realm}/roles", Json, ct) ?? [];
        var byName = available
            .Where(r => !string.IsNullOrEmpty(r.Name))
            .ToDictionary(r => r.Name!, StringComparer.Ordinal);

        var toAdd = roles
            .Where(r => !assigned.Any(a => string.Equals(a.Name, r, StringComparison.Ordinal)))
            .Where(byName.ContainsKey)
            .Select(r => byName[r])
            .ToList();
        // **消す側は「割り当て済みのうち、今回送られなかったもの」だけ。**
        // Keycloak 既定の合成ロール（default-roles-*）はここへ出さない —— 外すと realm 既定の
        // クライアントスコープごと剥がれる。
        var toRemove = assigned
            .Where(a => !string.IsNullOrEmpty(a.Name))
            .Where(a => !a.Name!.StartsWith("default-roles-", StringComparison.Ordinal))
            .Where(a => !NonAssignableRoles.Contains(a.Name, StringComparer.Ordinal))
            .Where(a => !roles.Contains(a.Name!, StringComparer.Ordinal))
            .ToList();

        var path = $"admin/realms/{Realm}/users/{Uri.EscapeDataString(userId)}/role-mappings/realm";
        if (toAdd.Count > 0)
            (await client.PostAsJsonAsync(path, toAdd, Json, ct)).EnsureSuccessStatusCode();
        if (toRemove.Count > 0)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, path)
            {
                Content = JsonContent.Create(toRemove, options: Json),
            };
            (await client.SendAsync(request, ct)).EnsureSuccessStatusCode();
        }

        return await ReloadAsync(client, userId, ct);
    }

    public async Task<bool> RevokeSessionsAsync(string userId, CancellationToken ct)
    {
        var client = await AuthorizedClientAsync(ct);
        // Keycloak のセッション失効はバックチャネルログアウトを起こし、BFF の
        // BackchannelLogoutProcessor が subject 単位でチケットを削除する（ADR-0032 / IADR-0273）。
        // **realm の client `bff` に backchannel.logout.url が登録されていることが前提**である
        // （登録済み。realm export で実測した）。
        var response = await client.PostAsync(
            $"admin/realms/{Realm}/users/{Uri.EscapeDataString(userId)}/logout", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    private string Realm => Uri.EscapeDataString(options.Realm);

    private async Task<IdentityUser?> UpdateAndReloadAsync(
        HttpClient client, string userId, Dictionary<string, object?> payload, CancellationToken ct)
    {
        var response = await client.PutAsJsonAsync(
            $"admin/realms/{Realm}/users/{Uri.EscapeDataString(userId)}", payload, Json, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await ReloadAsync(client, userId, ct);
    }

    private async Task<IdentityUser?> ReloadAsync(HttpClient client, string userId, CancellationToken ct)
    {
        var response = await client.GetAsync(
            $"admin/realms/{Realm}/users/{Uri.EscapeDataString(userId)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var user = await response.Content.ReadFromJsonAsync<KeycloakUser>(Json, ct);
        if (user is null || string.IsNullOrEmpty(user.Id)) return null;
        var roles = await RealmRolesAsync(client, user.Id, ct);
        return ToIdentityUser(user, roles);
    }

    private async Task<List<string>> RealmRolesAsync(HttpClient client, string userId, CancellationToken ct)
    {
        var roles = await client.GetFromJsonAsync<List<KeycloakRole>>(
            $"admin/realms/{Realm}/users/{Uri.EscapeDataString(userId)}/role-mappings/realm",
            Json, ct) ?? [];
        return
        [
            .. roles
                .Select(r => r.Name)
                .Where(name => !string.IsNullOrEmpty(name))
                .Where(name => !name!.StartsWith("default-roles-", StringComparison.Ordinal))
                .Where(name => !NonAssignableRoles.Contains(name, StringComparer.Ordinal))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal)
        ];
    }

    private static IdentityUser ToIdentityUser(KeycloakUser user, IReadOnlyList<string> roles)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, values) in user.Attributes ?? [])
        {
            // 多値属性は**先頭だけを読む**（判定側が 1 値しか読まないため。上の注記を参照）。
            var first = values?.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (first is not null) attributes[key] = first;
        }

        var displayName = string.Join(' ',
            new[] { user.LastName, user.FirstName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new IdentityUser(
            user.Id ?? string.Empty,
            user.Username ?? string.Empty,
            string.IsNullOrWhiteSpace(displayName) ? user.Username ?? string.Empty : displayName,
            user.Enabled,
            roles,
            attributes);
    }

    private async Task<HttpClient> AuthorizedClientAsync(CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(IdentityAdminRegistration.KeycloakClientName);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(client, ct));
        return client;
    }

    private async Task<string> AccessTokenAsync(HttpClient client, CancellationToken ct)
    {
        // 60 秒の余裕を持って失効させる（境界での 401 を避ける）。
        if (_token is not null && clock.GetUtcNow() < _tokenExpiresAt) return _token;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is not null && clock.GetUtcNow() < _tokenExpiresAt) return _token;

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
            });
            var response = await client.PostAsync(
                $"realms/{Realm}/protocol/openid-connect/token", content, ct);
            response.EnsureSuccessStatusCode();
            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(Json, ct)
                ?? throw new InvalidOperationException("Keycloak のトークン応答が空である。");

            _token = token.AccessToken;
            _tokenExpiresAt = clock.GetUtcNow().AddSeconds(Math.Max(token.ExpiresIn - 60, 5));
            logger.LogDebug("Keycloak admin token refreshed (expires in {ExpiresIn}s).", token.ExpiresIn);
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record KeycloakUser(
        string? Id,
        string? Username,
        string? FirstName,
        string? LastName,
        bool Enabled,
        Dictionary<string, List<string>?>? Attributes);

    private sealed record KeycloakRole(string? Id, string? Name);
}
