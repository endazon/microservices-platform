using AuthorizationService.Domain.Ports;
using Platform.Shared.Contracts.Dtos;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AuthorizationService.Infrastructure.ExternalServices;

// FR-05, FR-09, UC-05, SC-17, ADR-0026, IADR-0301, IADR-0329: Keycloak Admin REST による身元管理。
//
// ■ 疎通の状態（#1101 で更新。旧記述「実 Keycloak との疎通は未検証」は解消した）
//   稼働 k3s の Keycloak 24 に対して、一覧・属性差し替え・`enabled` 切替を**実測で通した**。
//   実測して初めて分かった罠が 2 つあり、どちらもスタブでは絶対に出ない:
//   1. **`PUT /users/{id}` は部分更新ではない。** 送らなかった項目は消える（`firstName` /
//      `lastName` / `email` が実際に消えた）。→ read-modify-write にした（`UpdateAndReloadAsync`）。
//   2. **realm の user profile が unmanaged 属性の書き込みを許していないと、属性は 204 を返しながら
//      黙って捨てられる。** → realm へ `unmanagedAttributePolicy: ADMIN_EDIT` を入れ、
//      それでも捨てられたら例外にする（`EnsureAttributesWereApplied`）。
//   単体テストは今もスタブした `HttpMessageHandler` に対する固定であり（`KeycloakIdentityAdminClientTests`）、
//   **「緑である」ことは「実 IdP へ反映できる」ことを意味しない。** 疎通は稼働クラスタで測る。
//
// 認証は client_credentials（機密クライアント `identity-admin`）。必要なクライアントロールは
// 3 つだけである（`view-users` / `manage-users` / `view-realm`。KeycloakAdminOptions のコメント参照）。
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

    // #1101: Keycloak がサーバ側で組み立てる読み取り専用の派生値。read-modify-write で送り返さない。
    private static readonly string[] ServerComputedFields =
        ["access", "disableableCredentialTypes", "userProfileMetadata"];

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
        //
        // IADR-0385 (#1243): **ただし集合値キー（tags / projects）は分割して多値で書く**（正準形）。
        // 読み戻しは同じ線上表現へ連結されるので `EnsureAttributesWereApplied` の突合は保たれる。
        var payload = new Dictionary<string, object?>
        {
            ["attributes"] = attributes.ToDictionary(
                kv => kv.Key,
                kv => UserAttributeEncoding.IsSetValued(kv.Key)
                    ? UserAttributeEncoding.SplitOrdered(kv.Value).ToArray()
                    : new[] { kv.Value }),
        };
        var updated = await UpdateAndReloadAsync(client, userId, payload, ct);
        if (updated is not null) EnsureAttributesWereApplied(attributes, updated);
        return updated;
    }

    // 🔴 **書けたことを確かめる（#1101 で実測した静かな縮退への fail-closed）。**
    //
    // realm の user profile が `unmanagedAttributePolicy` を持たない（既定＝無効）と、Keycloak は
    // ABAC 属性（`clearance` / `department` は managed でない）を **204 を返しながら黙って捨てる**。
    // 画面は 200 と「保存しました」を見て、認可判定は一切変わらない —— #1101 が潰した穴と同型の
    // 壊れが、配備の設定から realm の設定へ 1 段ずれて再発する。
    // したがって**書き戻した値を読み直して突き合わせ、食い違ったら例外にする**。
    // 解消は realm 側（`components["org.keycloak.userprofile.UserProfileProvider"]` の
    // `unmanagedAttributePolicy: ADMIN_EDIT`）で行う。
    private static void EnsureAttributesWereApplied(
        IReadOnlyDictionary<string, string> requested, IdentityUser reloaded)
    {
        // IADR-0385 (#1243): **集合値キーは集合として比べる。** 正準化（分割して書き、連結して読む）を
        // 通るため、`"sales hr"` と要求した値は `"sales,hr"` として読み戻る。🔴 これを序数比較すると
        // **realm の設定不備でもないのに「Keycloak が受け付けなかった」と嘘の失敗を上げる。**
        var dropped = requested
            .Where(kv => !reloaded.Attributes.TryGetValue(kv.Key, out var value)
                         || !Applied(kv.Key, kv.Value, value))
            .Select(kv => kv.Key)
            .ToList();
        if (dropped.Count == 0) return;

        throw new InvalidOperationException(
            $"Keycloak が利用者 '{reloaded.Username}' の属性 {string.Join(" / ", dropped)} を"
            + "受け付けなかった（更新要求は成功を返したが、読み直すと反映されていない）。"
            + " realm の user profile が unmanaged 属性の書き込みを許していない可能性が高い"
            + "（components[\"org.keycloak.userprofile.UserProfileProvider\"] の"
            + " unmanagedAttributePolicy を ADMIN_EDIT にする）。"
            + " **成功を返して黙って捨てるより、失敗として上げる。**");
    }

    // 要求した値が反映されたか。**単一値キーは序数で厳密に比べる**（正準化を通らないので、
    // ここを緩めると本当に捨てられた場合を見逃す）。集合値キーだけ集合として比べる。
    private static bool Applied(string key, string requested, string reloaded)
        => UserAttributeEncoding.IsSetValued(key)
            ? UserAttributeEncoding.Split(reloaded).SetEquals(UserAttributeEncoding.Split(requested))
            : string.Equals(reloaded, requested, StringComparison.Ordinal);

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

    // 🔴 **部分更新をしてはならない（#1101 で実 Keycloak に対して実測）。**
    //
    // `PUT /users/{id}` は「送った表現で置き換える」意味論であり、**送らなかった項目は消える**。
    // `{"enabled": false}` だけを送ると `firstName` / `lastName` / `email` が実際に消え、
    // `{"attributes": ...}` だけを送っても同じことが起きる（204 が返るので気付けない）。
    // したがって **read-modify-write** にする —— 現在の表現を取り、変更点だけ上書きし、全体を返す。
    //
    // サーバ計算のフィールド（`access` / `disableableCredentialTypes` / `userProfileMetadata`）は
    // 送り返さない（読み取り専用の派生値であり、送っても意味が無い）。
    private async Task<IdentityUser?> UpdateAndReloadAsync(
        HttpClient client, string userId, Dictionary<string, object?> payload, CancellationToken ct)
    {
        var path = $"admin/realms/{Realm}/users/{Uri.EscapeDataString(userId)}";
        var current = await client.GetAsync(path, ct);
        if (current.StatusCode == HttpStatusCode.NotFound) return null;
        current.EnsureSuccessStatusCode();
        var representation = await current.Content.ReadFromJsonAsync<JsonObject>(Json, ct);
        if (representation is null) return null;

        foreach (var computed in ServerComputedFields) representation.Remove(computed);
        foreach (var (key, value) in payload)
            representation[key] = JsonSerializer.SerializeToNode(value, Json);

        var response = await client.PutAsJsonAsync(path, representation, Json, ct);
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
            // IADR-0385 (#1243): **集合値キー（tags / projects）は連結する。**
            // 従前は一律に先頭 1 値へ畳んでおり、`["sales","hr"]` の `hr` が静かに消えていた
            // （部分集合判定は fail-closed 側へ倒れるが、**拒否理由が嘘になる**）。
            if (UserAttributeEncoding.IsSetValued(key))
            {
                var joined = UserAttributeEncoding.Join(values ?? []);
                if (joined.Length > 0) attributes[key] = joined;
                continue;
            }

            // 単一値キーは**従来どおり先頭だけを読む**（判定側が 1 値しか読まないため。上の注記を参照）。
            // 🔴 ここを一律に連結へ変えると `clearance: ["internal","public"]` が
            // `"internal,public"` になり、階段ポリシーがどれもマッチしなくなる（静かに壊れる）。
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
