using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using McpServer.Domain;
using McpServer.Domain.Ports;
using Platform.Shared.Contracts.Dtos;

namespace McpServer.Infrastructure.ExternalServices;

// FR-16, FR-09, UC-09, SC-12, 計画 ADR-0123 決定 1・2・フォローアップ 1・3, ADR-0088 決定 1, [[IADR-0515]] (#1786):
// SC-12 の登録・属性の差し替えを Keycloak Admin REST へ書く口。
//
// ■ 書き方（IADR-0515 決定 1・3）
//   1. `POST /clients` — 無人のテンプレート（機密・サービスアカウントつき・人の流れは全部閉じる）で作る。
//      **409 なら何も書かずに `AlreadyExists`**（入口を通らずに作られたクライアントへ属性を書かない）。
//   2. `GET /clients/{id}/service-account-user` — 作ったクライアントのサービスアカウントの利用者。
//   3. 🔴 **`GET /users?username=service-account-<client>&exact=true`** — 認可サービスが判定のたびに引き直すのと
//      **同じ照会**（`KeycloakIdentityAdminClient.FindByUsernameAsync`）で、2 と同じ利用者が 1 人だけ返ることを確かめる
//      （ADR-0123 フォローアップ 3）。返らなければ、書いた属性は判定に使われない —— 書かずに失敗させる。
//   4. `GET` → `PUT /users/{id}` — 属性を **read-modify-write** で書く（`PUT` は部分更新ではない。IADR-0329 の実測）。
//   5. `GET /users/{id}` — 書いた値が読み戻せることを確かめる（realm の user profile が unmanaged 属性を許さないと
//      204 のまま黙って捨てられる。IADR-0329 の実測）。
//   2〜5 のどこかで失敗したら、1 で作ったクライアントを消してから投げる（補償。決定 4）。
//
// ■ 🔴 **疎通は未検証である。** 単体テストはスタブした `HttpMessageHandler` に対する固定であり、
//   「緑である」ことは「実 IdP へ反映できる」ことを意味しない（`KeycloakIdentityAdminClient` と同じ限界）。
//   稼働クラスタでの実測は #1786 の残余（IADR-0515 §残余）。
public sealed class KeycloakServiceAccountProvisioner(
    IHttpClientFactory httpClientFactory,
    ServiceAccountProvisioningOptions options,
    TimeProvider clock,
    ILogger<KeycloakServiceAccountProvisioner> logger) : IServiceAccountProvisioner
{
    /// <summary>作ったクライアントへ付ける印（クライアント属性）。照合と運用者の識別のために置く。</summary>
    public const string ManagedByAttribute = "msp.mcp-client.managed-by";

    public const string ManagedByValue = "mcp-server";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Keycloak がサーバ側で組み立てる読み取り専用の派生値。read-modify-write で送り返さない（IADR-0329）。
    private static readonly string[] ServerComputedFields =
        ["access", "disableableCredentialTypes", "userProfileMetadata"];

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    private string Realm => Uri.EscapeDataString(options.Realm);

    public async Task<IdpWrite> CreateAsync(
        string clientId, string displayName, IReadOnlyDictionary<string, string> attributes, CancellationToken ct)
    {
        var client = await AuthorizedClientAsync(ct);
        return await CreateWithAttributesAsync(client, clientId, displayName, attributes, ct);
    }

    public async Task<IdpWrite> ReplaceAttributesAsync(
        string clientId, string displayName, IReadOnlyDictionary<string, string> attributes, CancellationToken ct)
    {
        var client = await AuthorizedClientAsync(ct);
        var internalId = await FindClientInternalIdAsync(client, clientId, ct);

        // 本入口ができる前の登録簿の行には、IdP 側のクライアントが無い。差し替えはその行を IdP へ載せる唯一の経路である
        // （登録簿の重複検査が再登録を止めるため）。**検証は呼び出し元が書く前に掛け終えている。**
        if (internalId is null)
            return await CreateWithAttributesAsync(client, clientId, displayName, attributes, ct);

        var userId = await ResolveServiceAccountUserAsync(client, clientId, internalId, ct);
        var previous = await ReadAttributesAsync(client, userId, ct);
        try
        {
            await WriteAttributesAsync(client, userId, attributes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 書きかけ（PUT は通ったが読み戻しが合わない等）を元の属性へ戻してから投げる（補償。決定 4）。
            await CompensateUpdateAsync(client, userId, previous, ct);
            throw ex as IdpProvisioningException ?? Failed("サービスアカウントへの属性の書き込みに失敗した。", ex);
        }
        return new IdpWrite(IdpWriteKind.Updated, clientId, internalId, userId, previous);
    }

    public async Task UndoAsync(IdpWrite write, CancellationToken ct)
    {
        var client = await AuthorizedClientAsync(ct);
        switch (write.Kind)
        {
            case IdpWriteKind.Created when write.ClientInternalId is { } id:
                await DeleteClientAsync(client, id, ct);
                break;
            case IdpWriteKind.Updated when write.ServiceAccountUserId is { } userId:
                await WriteAttributesAsync(client, userId, write.PreviousAttributes ?? new Dictionary<string, string>(), ct);
                break;
        }
    }

    private async Task<IdpWrite> CreateWithAttributesAsync(
        HttpClient client, string clientId, string displayName,
        IReadOnlyDictionary<string, string> attributes, CancellationToken ct)
    {
        var created = await Send(() => client.PostAsJsonAsync(
            $"admin/realms/{Realm}/clients", ServiceAccountClientTemplate(clientId, displayName), Json, ct));
        if (created.StatusCode == HttpStatusCode.Conflict) return IdpWrite.AlreadyExisting(clientId);
        EnsureSuccess(created, "クライアントの作成");

        var internalId = InternalIdFromLocation(created.Headers.Location)
            ?? await FindClientInternalIdAsync(client, clientId, ct)
            ?? throw Failed("作成したクライアントを引き直せない。");

        try
        {
            var userId = await ResolveServiceAccountUserAsync(client, clientId, internalId, ct);
            await WriteAttributesAsync(client, userId, attributes, ct);
            return new IdpWrite(IdpWriteKind.Created, clientId, internalId, userId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await CompensateCreationAsync(client, internalId, ct);
            throw ex as IdpProvisioningException ?? Failed("サービスアカウントへの属性の書き込みに失敗した。", ex);
        }
    }

    // IADR-0515 決定 3: 無人の MCP クライアントのテンプレート。
    // 🔴 **人の流れは全部閉じる**（認可コード・暗黙・パスワードの直接付与）。機密クライアントでサービスアカウントだけを開ける。
    // クライアントのスコープは指定しない（realm の既定）—— 判定に使う属性はトークンからではなく、認可サービスが IdP から引き直す
    // （ADR-0088 決定 1・ADR-0123 決定 1）。トークンへ属性を載せても判定は変わらない。
    internal static Dictionary<string, object?> ServiceAccountClientTemplate(string clientId, string displayName) => new()
    {
        ["clientId"] = clientId,
        ["name"] = displayName,
        ["description"] = "SC-12 で登録した MCP の無人クライアント。属性はこの入口だけが書く（Keycloak で直接割り当てない）。",
        ["enabled"] = true,
        ["protocol"] = "openid-connect",
        ["publicClient"] = false,
        ["clientAuthenticatorType"] = "client-secret",
        ["serviceAccountsEnabled"] = true,
        ["standardFlowEnabled"] = false,
        ["implicitFlowEnabled"] = false,
        ["directAccessGrantsEnabled"] = false,
        ["redirectUris"] = Array.Empty<string>(),
        ["webOrigins"] = Array.Empty<string>(),
        ["attributes"] = new Dictionary<string, string> { [ManagedByAttribute] = ManagedByValue },
    };

    private async Task<string?> FindClientInternalIdAsync(HttpClient client, string clientId, CancellationToken ct)
    {
        // `clientId=` は既定で完全一致だが（`search=true` で部分一致）、こちらでも綴りを確かめ直す。
        var response = await Send(() => client.GetAsync(
            $"admin/realms/{Realm}/clients?clientId={Uri.EscapeDataString(clientId)}", ct));
        EnsureSuccess(response, "クライアントの照会");
        var clients = await response.Content.ReadFromJsonAsync<List<KeycloakClient>>(Json, ct) ?? [];
        return clients.FirstOrDefault(c => string.Equals(c.ClientId, clientId, StringComparison.Ordinal))?.Id;
    }

    // 🔴 ADR-0123 フォローアップ 3: クライアントのサービスアカウントの利用者が、**認可サービスと同じ照会**で引けることを確かめる。
    private async Task<string> ResolveServiceAccountUserAsync(
        HttpClient client, string clientId, string internalId, CancellationToken ct)
    {
        var response = await Send(() => client.GetAsync(
            $"admin/realms/{Realm}/clients/{Uri.EscapeDataString(internalId)}/service-account-user", ct));
        EnsureSuccess(response, "サービスアカウントの利用者の取得");
        var serviceAccount = await response.Content.ReadFromJsonAsync<KeycloakUser>(Json, ct);
        if (string.IsNullOrEmpty(serviceAccount?.Id))
            throw Failed("クライアントにサービスアカウントの利用者が無い。");

        var userName = ToolUserContext.ServiceAccountUserName(clientId);
        var lookup = await Send(() => client.GetAsync(
            $"admin/realms/{Realm}/users?username={Uri.EscapeDataString(userName)}"
            + "&exact=true&briefRepresentation=false&max=2", ct));
        EnsureSuccess(lookup, "サービスアカウントの利用者の照会");
        var found = (await lookup.Content.ReadFromJsonAsync<List<KeycloakUser>>(Json, ct) ?? [])
            .Where(u => !string.IsNullOrEmpty(u.Id)
                        && string.Equals(u.Username, userName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (found.Count != 1 || !string.Equals(found[0].Id, serviceAccount.Id, StringComparison.Ordinal))
            throw Failed(
                $"認可サービスと同じ照会（利用者名の完全一致）でサービスアカウントの利用者が 1 人だけ引けない（{found.Count} 件）。"
                + " 書いた属性は判定に使われないため、書かない。");

        return serviceAccount.Id!;
    }

    private async Task<Dictionary<string, string>> ReadAttributesAsync(HttpClient client, string userId, CancellationToken ct)
    {
        var response = await Send(() => client.GetAsync(UserPath(userId), ct));
        EnsureSuccess(response, "サービスアカウントの属性の取得");
        var user = await response.Content.ReadFromJsonAsync<KeycloakUser>(Json, ct);
        return Decode(user?.Attributes);
    }

    private async Task WriteAttributesAsync(
        HttpClient client, string userId, IReadOnlyDictionary<string, string> attributes, CancellationToken ct)
    {
        var path = UserPath(userId);
        var current = await Send(() => client.GetAsync(path, ct));
        EnsureSuccess(current, "サービスアカウントの表現の取得");
        var representation = await current.Content.ReadFromJsonAsync<JsonObject>(Json, ct)
            ?? throw Failed("サービスアカウントの表現が空である。");

        foreach (var computed in ServerComputedFields) representation.Remove(computed);
        // 🔴 **属性は丸ごと置き換える。** サービスアカウントの利用者属性を書くのはこの入口だけである（ADR-0123 決定 2）。
        representation["attributes"] = JsonSerializer.SerializeToNode(Encode(attributes), Json);

        var put = await Send(() => client.PutAsJsonAsync(path, representation, Json, ct));
        EnsureSuccess(put, "サービスアカウントの属性の書き込み");

        var applied = await ReadAttributesAsync(client, userId, ct);
        if (!SameAttributes(attributes, applied))
            throw Failed(
                "書いた属性が読み戻せない（realm の user profile が unmanaged 属性の書き込みを許していない可能性がある）。");
    }

    private async Task DeleteClientAsync(HttpClient client, string internalId, CancellationToken ct)
    {
        var response = await Send(() => client.DeleteAsync(
            $"admin/realms/{Realm}/clients/{Uri.EscapeDataString(internalId)}", ct));
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        EnsureSuccess(response, "クライアントの削除");
    }

    // 補償に失敗したら、その旨を残して元の失敗を投げる（補償の失敗で元の理由を上書きしない）。
    // 残ったクライアントは登録簿に無いので、同じ clientId の再登録は `AlreadyExists` で止まる（属性は書かれない）。
    private async Task CompensateCreationAsync(HttpClient client, string internalId, CancellationToken ct)
    {
        try
        {
            await DeleteClientAsync(client, internalId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "作りかけのクライアント（内部 ID {InternalId}）を消せなかった。IdP に登録簿に無いクライアントが残っている。"
                + " Keycloak の管理画面で消すこと（属性は書かれていない、または書きかけである）。",
                internalId);
        }
    }

    private async Task CompensateUpdateAsync(
        HttpClient client, string userId, IReadOnlyDictionary<string, string> previous, CancellationToken ct)
    {
        try
        {
            await WriteAttributesAsync(client, userId, previous, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "サービスアカウント（利用者 ID {UserId}）の属性を元へ戻せなかった。登録簿と IdP の属性が食い違っている。",
                userId);
        }
    }

    // 集合値キー（tags / projects）は多値で書く。単一値キーは 1 要素の配列（認可サービスの読み方と同じ。IADR-0385）。
    internal static Dictionary<string, string[]> Encode(IReadOnlyDictionary<string, string> attributes)
        => attributes.ToDictionary(
            kv => kv.Key,
            kv => UserAttributeEncoding.IsSetValued(kv.Key)
                ? UserAttributeEncoding.SplitOrdered(kv.Value).ToArray()
                : [kv.Value],
            StringComparer.Ordinal);

    internal static Dictionary<string, string> Decode(Dictionary<string, List<string>?>? attributes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, values) in attributes ?? [])
        {
            if (UserAttributeEncoding.IsSetValued(key))
            {
                var joined = UserAttributeEncoding.Join(values ?? []);
                if (joined.Length > 0) result[key] = joined;
                continue;
            }
            var first = values?.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (first is not null) result[key] = first;
        }
        return result;
    }

    // 集合値キーは集合として、単一値キーは文字列として比べる。
    internal static bool SameAttributes(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual)
    {
        var wanted = expected
            .Where(kv => UserAttributeEncoding.IsSetValued(kv.Key)
                ? UserAttributeEncoding.Split(kv.Value).Count > 0
                : !string.IsNullOrWhiteSpace(kv.Value))
            .ToList();
        if (wanted.Count != actual.Count) return false;
        foreach (var (key, value) in wanted)
        {
            if (!actual.TryGetValue(key, out var got)) return false;
            var same = UserAttributeEncoding.IsSetValued(key)
                ? UserAttributeEncoding.Split(value).SetEquals(UserAttributeEncoding.Split(got))
                : string.Equals(value, got, StringComparison.Ordinal);
            if (!same) return false;
        }
        return true;
    }

    private string UserPath(string userId) => $"admin/realms/{Realm}/users/{Uri.EscapeDataString(userId)}";

    private static string? InternalIdFromLocation(Uri? location)
    {
        if (location is null) return null;
        var segments = location.OriginalString.TrimEnd('/').Split('/');
        return segments.Length > 0 && segments[^1].Length > 0 ? Uri.UnescapeDataString(segments[^1]) : null;
    }

    // 到達できない（接続拒否・時間切れ）は `Failed` へ写す。取り消しはそのまま外へ出す。
    private static async Task<HttpResponseMessage> Send(Func<Task<HttpResponseMessage>> call)
    {
        try
        {
            return await call();
        }
        catch (HttpRequestException ex)
        {
            throw Failed("IdP（Keycloak）へ到達できない。", ex);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string what)
    {
        if (!response.IsSuccessStatusCode)
            throw Failed($"{what}が失敗した（HTTP {(int)response.StatusCode}）。");
    }

    private static IdpProvisioningException Failed(string message, Exception? inner = null)
        => new(IdpProvisioningFailure.Failed, message, inner);

    private async Task<HttpClient> AuthorizedClientAsync(CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(ServiceAccountProvisioningRegistration.KeycloakClientName);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(client, ct));
        return client;
    }

    // 認証は client_credentials（機密クライアント。既定 `mcp-client-admin`）。`KeycloakIdentityAdminClient` と同じ形
    // （60 秒の余裕を持って失効させる）。
    private async Task<string> AccessTokenAsync(HttpClient client, CancellationToken ct)
    {
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
            var response = await Send(() => client.PostAsync(
                $"realms/{Realm}/protocol/openid-connect/token", content, ct));
            EnsureSuccess(response, "管理用トークンの取得");
            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(Json, ct)
                ?? throw Failed("Keycloak のトークン応答が空である。");

            _token = token.AccessToken;
            _tokenExpiresAt = clock.GetUtcNow().AddSeconds(Math.Max(token.ExpiresIn - 60, 5));
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

    private sealed record KeycloakClient(string? Id, string? ClientId);

    private sealed record KeycloakUser(
        string? Id,
        string? Username,
        Dictionary<string, List<string>?>? Attributes);
}
