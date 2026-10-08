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

// FR-16, FR-09, UC-09, SC-12, 計画 ADR-0123 決定 1・2・フォローアップ 1・3, ADR-0088 決定 1, [[IADR-0516]] (#1786):
// SC-12 の登録・属性の差し替えを Keycloak Admin REST へ書く口。
//
// ■ 書き方（IADR-0516 決定 1・3）
//   1. `POST /clients` — 無人のテンプレート（機密・サービスアカウントつき・人の流れは全部閉じる）で作る。
//      **409 なら何も書かずに `AlreadyExists`**（入口を通らずに作られたクライアントへ属性を書かない）。
//   2. `GET /clients/{id}/service-account-user` — 作ったクライアントのサービスアカウントの利用者。
//   3. 🔴 **`GET /users?username=service-account-<client>&exact=true`** — 認可サービスが判定のたびに引き直すのと
//      **同じ照会**（`KeycloakIdentityAdminClient.FindByUsernameAsync`）で、2 と同じ利用者が 1 人だけ返ることを確かめる
//      （ADR-0123 フォローアップ 3）。返らなければ、書いた属性は判定に使われない —— 書かずに失敗させる。
//   4. `GET` → `PUT /users/{id}` — 属性を **read-modify-write** で書く（`PUT` は部分更新ではない。IADR-0329 の実測）。
//   5. `GET /users/{id}` — 書いた値が読み戻せることを確かめる（realm の user profile が unmanaged 属性を許さないと
//      204 のまま黙って捨てられる。IADR-0329 の実測）。
//   2〜5 のどこかで失敗したら（時間切れを含む）、1 で作ったクライアントを消してから投げる（補償。決定 4）。
//
// ■ 差し替えは `GET /clients/{id}` で入口の印（`managed-by=mcp-server`）を確かめ、無ければ何も書かない（PR #1816 監査 🔴-1）。
// ■ ［2026-10-09 / #1818］照合の読み取り（`IServiceAccountDirectory`）も同じ口が持つ: `GET /clients`（頁で列挙・入口の印の有無）と
//   `GET /users?username=service-account-<client>&exact=true`（認可サービスと同じ照会）。**読むだけで書かない**（IADR-0516 決定 5）。
// ■ ［2026-10-09 / #1829］無効化・再有効化の写し（IADR-0516 決定 4a）: クライアントの完全一致の照会 → `GET /clients/{id}`（入口の印と
//   現在の `enabled`）→ `PUT /clients/{id}` へ **`enabled` と、`serviceAccountsEnabled`・`authorizationServicesEnabled` の現在値**を送る → 読み戻す。
//   表現を丸ごと送り返さない（表現は secret を含み、読んでから書くまでに回された secret を古い値へ戻し得る）。
//   🔴 **［PR #1832 監査 🔴1］`{"enabled": …}` だけでは壊れる。** Keycloak 24 の `ClientResource.updateClientFromRep` は、
//   `rep.isServiceAccountsEnabled()` が TRUE でなければ（null を含む）既存のサービスアカウントの利用者を属性ごと消し、
//   `updateAuthorizationSettings` も TRUE でなければ authorization を無効にする。null の項目を飛ばす `RepresentationToModel.updateClient` は
//   この分岐より後に走る。だから**この 2 つは必ず現在値で送る**（消えた SA は次の client_credentials で空の利用者として作り直され、
//   属性なしのトークンが出る）。
//   **入口の印が無いクライアント（`abac-seeder` 等）・IdP に無いクライアントには何も書かない。**
// ■ 🔴 **要求の取り消しは IdP への書き込みへ伝えない**（書きかけの孤児を作らない）。期限は HttpClient の Timeout が持ち、
//   時間切れは `Failed`（502）へ写す。管理用トークンが 401 で拒まれたら、1 度だけ取り直して送り直す。
//
// ■ 🔴 **疎通は未検証である。** 単体テストはスタブした `HttpMessageHandler` に対する固定であり、
//   「緑である」ことは「実 IdP へ反映できる」ことを意味しない（`KeycloakIdentityAdminClient` と同じ限界）。
//   稼働クラスタでの実測は #1786 の残余（IADR-0516 §残余）。
public sealed class KeycloakServiceAccountProvisioner(
    IHttpClientFactory httpClientFactory,
    ServiceAccountProvisioningOptions options,
    TimeProvider clock,
    ILogger<KeycloakServiceAccountProvisioner> logger) : IServiceAccountProvisioner, IServiceAccountDirectory
{
    /// <summary>作ったクライアントへ付ける印（クライアント属性）。照合と運用者の識別のために置く。</summary>
    public const string ManagedByAttribute = "msp.mcp-client.managed-by";

    public const string ManagedByValue = "mcp-server";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Keycloak がサーバ側で組み立てる読み取り専用の派生値。read-modify-write で送り返さない（IADR-0329）。
    private static readonly string[] ServerComputedFields =
        ["access", "disableableCredentialTypes", "userProfileMetadata"];

    private const string WriteFailed = "サービスアカウントへの属性の書き込みに失敗した。";

    // [[IADR-0516]] 決定 5 の 2026-10-09 追記（#1818）: 照合がクライアントを列挙する頁の大きさと上限（100 頁 ＝ 1 万件）。
    // 上限を超えたら読み切らずに失敗させる（途中までの一覧で孤児を数えると、読まなかった分を「無い」と取り違える）。
    internal const int ClientListPageSize = 100;
    internal const int ClientListMaxPages = 100;

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    private string Realm => Uri.EscapeDataString(options.Realm);

    public async Task<IdpWrite> CreateAsync(
        string clientId, string displayName, IReadOnlyDictionary<string, string> attributes, CancellationToken ct)
    {
        // 書き始める前の取り消しだけは受ける。書き始めたら最後まで（または補償まで）走る。
        ct.ThrowIfCancellationRequested();
        var client = await AuthorizedClientAsync();
        return await CreateWithAttributesAsync(client, clientId, displayName, attributes, enabled: true);
    }

    public async Task<IdpWrite> ReplaceAttributesAsync(
        string clientId, string displayName, IReadOnlyDictionary<string, string> attributes, bool enabled, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var client = await AuthorizedClientAsync();
        var internalId = await FindClientInternalIdAsync(client, clientId);

        // 本入口ができる前の登録簿の行には、IdP 側のクライアントが無い。差し替えはその行を IdP へ載せる唯一の経路である
        // （登録簿の重複検査が再登録を止めるため）。**検証は呼び出し元が書く前に掛け終えている。**
        // 登録簿で無効化された行は無効のまま作る（有効なクライアントを生まない。IADR-0516 決定 4）。
        if (internalId is null)
            return await CreateWithAttributesAsync(client, clientId, displayName, attributes, enabled);

        // 🔴 PR #1816 監査 🔴-1: **入口が作ったクライアントにだけ書く。** 印が無いもの（`abac-seeder` などプラットフォーム自身の
        // 機密クライアント、Keycloak で直接作られたもの）は、同じ clientId の登録簿の行があっても書かない —— 書けばその主体の
        // 本来の属性を、検証の掛からない経路で上書きする。
        if (!await IsManagedAsync(client, internalId))
            return IdpWrite.AlreadyExisting(clientId);

        var userId = await ResolveServiceAccountUserAsync(client, clientId, internalId);
        var previous = await ReadAttributesAsync(client, userId);
        try
        {
            await WriteAttributesAsync(client, userId, attributes);
        }
        catch (IdpProvisioningException)
        {
            // 書きかけ（PUT は通ったが読み戻しが合わない等）を元の属性へ戻してから投げる（補償。決定 4）。
            await CompensateUpdateAsync(client, userId, previous);
            throw;
        }
        catch (Exception ex)
        {
            await CompensateUpdateAsync(client, userId, previous);
            throw Failed(WriteFailed, ex);
        }
        return new IdpWrite(IdpWriteKind.Updated, clientId, internalId, userId, previous,
            new Dictionary<string, string>(attributes));
    }

    // ［2026-10-09 / #1829］IADR-0516 決定 4a: 無効化・再有効化を IdP のクライアントの `enabled` へ写す（多層の防御）。
    // 🔴 **入口が作ったクライアントだけを変える。** 印が無いもの（`abac-seeder` などプラットフォーム自身の機密クライアント）を
    //    SC-12 の無効化の経路から止めたり開いたりしない（決定 4 の「入口が作っていないクライアントへは書かない」と同じ規則）。
    // 🔴 **要求の取り消しは伝えない**（書き込みの口の規則。期限は HttpClient の Timeout）。
    public async Task<IdpWrite> SetEnabledAsync(string clientId, bool enabled, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var client = await AuthorizedClientAsync();
        var internalId = await FindClientInternalIdAsync(client, clientId);
        if (internalId is null) return IdpWrite.Missing(clientId);

        var current = await ReadClientAsync(client, internalId);
        if (!IsManaged(current)) return IdpWrite.AlreadyExisting(clientId);

        var previous = IsEnabled(current);
        if (previous != enabled)
        {
            // PR #1832 再監査 🟡1: SA の項目が読めない表現では書く前に止める（補償も書かない）。
            RequireServiceAccountsFlag(current);
            try
            {
                await WriteEnabledAsync(client, internalId, enabled, current);
            }
            catch (Exception ex)
            {
                // 🔴 失敗したら**閉じる側へだけ**倒す: 開く（再有効化）書き込みが途中で失敗したら無効へ戻す。閉じる（無効化）書き込みの
                // 失敗では戻さない —— 戻すと、通っていたかもしれない無効化を取り消して開いてしまう。残った食い違いは照合が拾う。
                if (enabled) await CompensateEnabledAsync(client, clientId, internalId, false, current);
                if (ex is IdpProvisioningException) throw;
                throw Failed("クライアントの有効・無効の書き込みに失敗した。", ex);
            }
        }
        return new IdpWrite(IdpWriteKind.EnabledChanged, clientId, internalId,
            PreviousEnabled: previous, WrittenEnabled: enabled);
    }

    public async Task UndoAsync(IdpWrite write, CancellationToken ct)
    {
        // 補償は要求の取り消しに依らず最後まで走る（ct は受けるが伝えない）。
        var client = await AuthorizedClientAsync();
        switch (write.Kind)
        {
            case IdpWriteKind.EnabledChanged
                when write.ClientInternalId is { } clientInternalId
                     && write.PreviousEnabled is { } previousEnabled
                     && write.WrittenEnabled is { } writtenEnabled
                     && previousEnabled != writtenEnabled:
                // 書き換えの取り消しと同じ規則: 現在値がこの要求の書いた値のままのときだけ戻す（後の無効化・再有効化を潰さない）。
                var now = await ReadClientAsync(client, clientInternalId);
                if (IsEnabled(now) != writtenEnabled)
                {
                    logger.LogWarning(
                        "クライアント {ClientId} の有効・無効は、この要求の後に書き換えられていた。取り消しで書き戻さない。",
                        ForLog(write.ClientId));
                    break;
                }
                await WriteEnabledAsync(client, clientInternalId, previousEnabled, now);
                break;
            case IdpWriteKind.Created when write.ClientInternalId is { } id:
                await DeleteClientAsync(client, id);
                break;
            case IdpWriteKind.Updated when write.ServiceAccountUserId is { } userId:
                // 🔴 PR #1816 監査 🟡-2: 現在値がこの要求の書いた値のままのときだけ戻す。並行した差し替えが後から書いた値を
                // 古い値で潰さない。戻さなかった食い違い（IdP は後の要求・登録簿は先の要求）は照合（#1818）が拾う。
                var current = await ReadAttributesAsync(client, userId);
                if (write.WrittenAttributes is { } written && !SameAttributes(written, current))
                {
                    logger.LogWarning(
                        "サービスアカウント（利用者 ID {UserId}）の属性は、この要求の後に書き換えられていた。取り消しで書き戻さない。",
                        ForLog(userId));
                    break;
                }
                await WriteAttributesAsync(client, userId, write.PreviousAttributes ?? new Dictionary<string, string>());
                break;
        }
    }

    // ---- 照合の読み取り（IServiceAccountDirectory。IADR-0516 決定 5 / #1818）----------------------------------------
    // 🔴 **読むだけで書かない。** 書き込みの口と違い、要求の取り消しを伝える（途中で止めても孤児は生まれない）。

    public async Task<IReadOnlyList<IdpClientEntry>> ListClientsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var client = await AuthorizedClientAsync();
        var result = new List<IdpClientEntry>();
        for (var page = 0; page < ClientListMaxPages; page++)
        {
            // 一覧の表現はクライアント属性（入口の印）を含む（`briefRepresentation` は clients の一覧には無い）。
            var response = await Send(client, () => client.GetAsync(
                $"admin/realms/{Realm}/clients?first={page * ClientListPageSize}&max={ClientListPageSize}", ct), ct);
            EnsureSuccess(response, "クライアントの列挙");
            var clients = await ReadJsonAsync<List<KeycloakClient>>(response, ct) ?? [];
            foreach (var c in clients)
            {
                if (!string.IsNullOrEmpty(c.ClientId)) result.Add(new IdpClientEntry(c.ClientId, IsManaged(c), IsEnabled(c)));
            }
            if (clients.Count < ClientListPageSize) return result;
        }
        throw Failed($"IdP のクライアントが {ClientListPageSize * ClientListMaxPages} 件以上ある。照合の上限を超えたので読み切らない。");
    }

    public async Task<IReadOnlyDictionary<string, string>?> ReadServiceAccountAttributesAsync(string clientId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var client = await AuthorizedClientAsync();
        // 認可サービスの `FindByUsernameAsync` と同じ照会（判定に効いている値を読む。IADR-0516 決定 6）。書き込みの確かめと同じ 1 つを使う。
        var found = await FindServiceAccountUsersAsync(client, clientId, ct);
        return found.Count switch
        {
            0 => null,
            1 => Decode(found[0].Attributes),
            _ => throw Failed($"利用者名の完全一致の照会が {found.Count} 人を返した（1 人であるべき）。"),
        };
    }

    private async Task<IdpWrite> CreateWithAttributesAsync(
        HttpClient client, string clientId, string displayName,
        IReadOnlyDictionary<string, string> attributes, bool enabled)
    {
        // 🔴 PR #1816 再監査 🟡-A: `POST /clients` そのものが時間切れ・5xx になっても、Keycloak が作り終えている場合がある
        // （印つき・属性なし・有効の孤児が残り、再登録は 409・差し替えは書けない）。**作成の成否が分からない失敗**は、
        // clientId で引き直し、入口の印があれば消してから `Failed` を返す。
        HttpResponseMessage created;
        try
        {
            created = await Send(client, () => client.PostAsJsonAsync(
                $"admin/realms/{Realm}/clients", ServiceAccountClientTemplate(clientId, displayName, enabled), Json,
                CancellationToken.None));
        }
        catch (IdpProvisioningException)
        {
            await CompensateUnconfirmedCreationAsync(client, clientId);
            throw;
        }
        if (created.StatusCode == HttpStatusCode.Conflict) return IdpWrite.AlreadyExisting(clientId);
        if ((int)created.StatusCode >= 500) await CompensateUnconfirmedCreationAsync(client, clientId);
        EnsureSuccess(created, "クライアントの作成");

        string? internalId = InternalIdFromLocation(created.Headers.Location);
        try
        {
            internalId ??= await FindClientInternalIdAsync(client, clientId)
                ?? throw Failed("作成したクライアントを引き直せない。");
            var userId = await ResolveServiceAccountUserAsync(client, clientId, internalId);
            await WriteAttributesAsync(client, userId, attributes);
            return new IdpWrite(IdpWriteKind.Created, clientId, internalId, userId,
                WrittenAttributes: new Dictionary<string, string>(attributes));
        }
        catch (IdpProvisioningException)
        {
            await CompensateCreationAsync(client, clientId, internalId);
            throw;
        }
        catch (Exception ex)
        {
            await CompensateCreationAsync(client, clientId, internalId);
            throw Failed(WriteFailed, ex);
        }
    }

    // IADR-0516 決定 3: 無人の MCP クライアントのテンプレート。
    // 🔴 **人の流れは全部閉じる**（認可コード・暗黙・パスワードの直接付与）。機密クライアントでサービスアカウントだけを開ける。
    // クライアントのスコープは指定しない（realm の既定）—— 判定に使う属性はトークンからではなく、認可サービスが IdP から引き直す
    // （ADR-0088 決定 1・ADR-0123 決定 1）。トークンへ属性を載せても判定は変わらない。
    // `fullScopeAllowed=false`: realm の全ロールをトークンへ載せない（サービスアカウントへ割り当てたロールだけ）。
    internal static Dictionary<string, object?> ServiceAccountClientTemplate(
        string clientId, string displayName, bool enabled = true) => new()
        {
            ["clientId"] = clientId,
            ["name"] = displayName,
            ["description"] = "SC-12 で登録した MCP の無人クライアント。属性はこの入口だけが書く（Keycloak で直接割り当てない）。",
            ["enabled"] = enabled,
            ["protocol"] = "openid-connect",
            ["publicClient"] = false,
            ["clientAuthenticatorType"] = "client-secret",
            ["serviceAccountsEnabled"] = true,
            ["standardFlowEnabled"] = false,
            ["implicitFlowEnabled"] = false,
            ["directAccessGrantsEnabled"] = false,
            ["fullScopeAllowed"] = false,
            ["redirectUris"] = Array.Empty<string>(),
            ["webOrigins"] = Array.Empty<string>(),
            ["attributes"] = new Dictionary<string, string> { [ManagedByAttribute] = ManagedByValue },
        };

    private async Task<string?> FindClientInternalIdAsync(HttpClient client, string clientId)
    {
        // `clientId=` は既定で完全一致だが（`search=true` で部分一致）、こちらでも綴りを確かめ直す。
        var response = await Send(client, () => client.GetAsync(
            $"admin/realms/{Realm}/clients?clientId={Uri.EscapeDataString(clientId)}", CancellationToken.None));
        EnsureSuccess(response, "クライアントの照会");
        var clients = await ReadJsonAsync<List<KeycloakClient>>(response) ?? [];
        return clients.FirstOrDefault(c => string.Equals(c.ClientId, clientId, StringComparison.Ordinal))?.Id;
    }

    private async Task<bool> IsManagedAsync(HttpClient client, string internalId)
        => IsManaged(await ReadClientAsync(client, internalId));

    private async Task<KeycloakClient?> ReadClientAsync(HttpClient client, string internalId)
    {
        var response = await Send(client, () => client.GetAsync(ClientPath(internalId), CancellationToken.None));
        EnsureSuccess(response, "クライアントの取得");
        return await ReadJsonAsync<KeycloakClient>(response);
    }

    // クライアントの `enabled`。項目が無い表現は Keycloak の既定（有効）と読む。
    private static bool IsEnabled(KeycloakClient? representation) => representation?.Enabled != false;

    // ［#1829］`enabled` を書き、読み戻して確かめる（表現を丸ごと送り返さない理由と、SA・authorization の現在値を必ず添える理由は冒頭）。
    // `current` は同じ要求の中で読んだクライアントの表現（SA・authorization の現在値の出どころ）。
    internal static Dictionary<string, bool> EnabledBody(bool enabled, bool serviceAccountsEnabled, bool authorizationServicesEnabled) => new()
    {
        ["enabled"] = enabled,
        ["serviceAccountsEnabled"] = serviceAccountsEnabled,
        ["authorizationServicesEnabled"] = authorizationServicesEnabled,
    };

    // 🔴 PR #1832 再監査 🟡1（fail-closed）: `serviceAccountsEnabled` が読めない（null・欠落）表現から false を推して送ると、
    // Keycloak 24 は SA の利用者を属性ごと消す（🔴1 と同じ事故）。Keycloak 24 の GET は primitive で必ず出すが、版の変更や
    // 応答の加工で欠けたときに黙って壊さないよう、書かずに Failed にする。`authorizationServicesEnabled` は資源サーバが
    // 無いとき表現に出ない（＝無効）ので、欠落を false と読んでよい（false を送っても無効のままで何も消えない）。
    private static bool RequireServiceAccountsFlag(KeycloakClient? current)
        => current?.ServiceAccountsEnabled
           ?? throw Failed("クライアントの表現に serviceAccountsEnabled が無い（推して送るとサービスアカウントの利用者が消えるので書かない）。");

    private async Task WriteEnabledAsync(HttpClient client, string internalId, bool enabled, KeycloakClient? current)
    {
        var body = EnabledBody(enabled,
            serviceAccountsEnabled: RequireServiceAccountsFlag(current),
            authorizationServicesEnabled: current?.AuthorizationServicesEnabled == true);
        var put = await Send(client, () => client.PutAsJsonAsync(ClientPath(internalId), body, Json, CancellationToken.None));
        EnsureSuccess(put, "クライアントの有効・無効の書き込み");
        if (IsEnabled(await ReadClientAsync(client, internalId)) != enabled)
            throw Failed("書いたクライアントの有効・無効が読み戻せない。");
    }

    private async Task CompensateEnabledAsync(
        HttpClient client, string clientId, string internalId, bool enabled, KeycloakClient? current)
    {
        try
        {
            await WriteEnabledAsync(client, internalId, enabled, current);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "クライアント {ClientId} の有効・無効を {Enabled} へ戻せなかった。登録簿と IdP の有効・無効が食い違っている（照合が enabled_differs として拾う）。",
                ForLog(clientId), enabled);
        }
    }

    private string ClientPath(string internalId) => $"admin/realms/{Realm}/clients/{Uri.EscapeDataString(internalId)}";

    // 入口の印（クライアント属性 `managed-by=mcp-server`）があるか。書き込みの確かめと照合の列挙が同じ 1 つを使う。
    private static bool IsManaged(KeycloakClient? representation)
        => representation?.Attributes is { } attributes
           && attributes.TryGetValue(ManagedByAttribute, out var value)
           && string.Equals(value, ManagedByValue, StringComparison.Ordinal);

    // 🔴 ADR-0123 フォローアップ 3: クライアントのサービスアカウントの利用者が、**認可サービスと同じ照会**で引けることを確かめる。
    private async Task<string> ResolveServiceAccountUserAsync(HttpClient client, string clientId, string internalId)
    {
        var response = await Send(client, () => client.GetAsync(
            $"admin/realms/{Realm}/clients/{Uri.EscapeDataString(internalId)}/service-account-user", CancellationToken.None));
        EnsureSuccess(response, "サービスアカウントの利用者の取得");
        var serviceAccount = await ReadJsonAsync<KeycloakUser>(response);
        if (string.IsNullOrEmpty(serviceAccount?.Id))
            throw Failed("クライアントにサービスアカウントの利用者が無い。");

        var found = await FindServiceAccountUsersAsync(client, clientId, CancellationToken.None);

        if (found.Count != 1 || !string.Equals(found[0].Id, serviceAccount.Id, StringComparison.Ordinal))
            throw Failed(
                $"認可サービスと同じ照会（利用者名の完全一致）でサービスアカウントの利用者が 1 人だけ引けない（{found.Count} 件）。"
                + " 書いた属性は判定に使われないため、書かない。");

        return serviceAccount.Id!;
    }

    // 🔴 IADR-0516 決定 6 の核: **認可サービスの `FindByUsernameAsync` と同じ照会**（利用者名の完全一致）。書き込みの確かめ
    // （ResolveServiceAccountUserAsync）と照合の読み取り（ReadServiceAccountAttributesAsync）がこの 1 か所を使う（PR #1831 の AI レビュー）。
    // 照会の結果のうち、ID があり利用者名が一致するもの（大小文字は問わない）だけを返す（`max=2`＝2 人以上は呼び出し元が判定する）。
    private async Task<List<KeycloakUser>> FindServiceAccountUsersAsync(HttpClient client, string clientId, CancellationToken ct)
    {
        var userName = ToolUserContext.ServiceAccountUserName(clientId);
        var lookup = await Send(client, () => client.GetAsync(
            $"admin/realms/{Realm}/users?username={Uri.EscapeDataString(userName)}"
            + "&exact=true&briefRepresentation=false&max=2", ct), ct);
        EnsureSuccess(lookup, "サービスアカウントの利用者の照会");
        return (await ReadJsonAsync<List<KeycloakUser>>(lookup, ct) ?? [])
            .Where(u => !string.IsNullOrEmpty(u.Id)
                        && string.Equals(u.Username, userName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<Dictionary<string, string>> ReadAttributesAsync(HttpClient client, string userId)
    {
        var response = await Send(client, () => client.GetAsync(UserPath(userId), CancellationToken.None));
        EnsureSuccess(response, "サービスアカウントの属性の取得");
        var user = await ReadJsonAsync<KeycloakUser>(response);
        return Decode(user?.Attributes);
    }

    private async Task WriteAttributesAsync(
        HttpClient client, string userId, IReadOnlyDictionary<string, string> attributes)
    {
        var path = UserPath(userId);
        var current = await Send(client, () => client.GetAsync(path, CancellationToken.None));
        EnsureSuccess(current, "サービスアカウントの表現の取得");
        var representation = await ReadJsonAsync<JsonObject>(current)
            ?? throw Failed("サービスアカウントの表現が空である。");

        foreach (var computed in ServerComputedFields) representation.Remove(computed);
        // 🔴 **属性は丸ごと置き換える。** サービスアカウントの利用者属性を書くのはこの入口だけである（ADR-0123 決定 2）。
        representation["attributes"] = JsonSerializer.SerializeToNode(Encode(attributes), Json);

        var put = await Send(client, () => client.PutAsJsonAsync(path, representation, Json, CancellationToken.None));
        EnsureSuccess(put, "サービスアカウントの属性の書き込み");

        var applied = await ReadAttributesAsync(client, userId);
        if (!SameAttributes(attributes, applied))
            throw Failed(
                "書いた属性が読み戻せない（realm の user profile が unmanaged 属性の書き込みを許していない可能性がある）。");
    }

    private async Task DeleteClientAsync(HttpClient client, string internalId)
    {
        var response = await Send(client, () => client.DeleteAsync(
            $"admin/realms/{Realm}/clients/{Uri.EscapeDataString(internalId)}", CancellationToken.None));
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        EnsureSuccess(response, "クライアントの削除");
    }

    // 補償に失敗したら、その旨を残して元の失敗を投げる（補償の失敗で元の理由を上書きしない）。
    // 残ったクライアントは登録簿に無いので、同じ clientId の再登録は `AlreadyExists` で止まる（属性は書かれない）。
    // Location が無く内部 ID が分からないまま失敗したときは、clientId で引き直してから消す。
    private async Task CompensateCreationAsync(HttpClient client, string clientId, string? internalId)
    {
        try
        {
            internalId ??= await FindClientInternalIdAsync(client, clientId);
            if (internalId is not null) await DeleteClientAsync(client, internalId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "作りかけのクライアント（内部 ID {InternalId}）を消せなかった。IdP に登録簿に無いクライアントが残っている。"
                + " Keycloak の管理画面で消すこと（属性は書かれていない、または書きかけである）。",
                ForLog(internalId));
        }
    }

    // 作成の成否が分からないときの補償。**入口の印があるものだけ**消す（同名の印なしのクライアントには触れない）。
    private async Task CompensateUnconfirmedCreationAsync(HttpClient client, string clientId)
    {
        string? internalId = null;
        try
        {
            internalId = await FindClientInternalIdAsync(client, clientId);
            if (internalId is not null && await IsManagedAsync(client, internalId))
                await DeleteClientAsync(client, internalId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "作成の成否が分からないクライアント（内部 ID {InternalId}）を確かめて消せなかった。IdP に孤児が残っている可能性がある。",
                ForLog(internalId ?? "(不明)"));
        }
    }

    private async Task CompensateUpdateAsync(
        HttpClient client, string userId, IReadOnlyDictionary<string, string> previous)
    {
        try
        {
            await WriteAttributesAsync(client, userId, previous);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "サービスアカウント（利用者 ID {UserId}）の属性を元へ戻せなかった。登録簿と IdP の属性が食い違っている。",
                ForLog(userId));
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

    // 管理要求を送る。到達できない・時間切れ（HttpClient の Timeout）は `Failed` へ写す。
    // 401 は 1 度だけトークンを取り直して送り直す（失効の境界・鍵の更新）。
    // `ct` は照合の読み取りだけが渡す（書き込みと補償は渡さない＝取り消しを伝えない）。
    private async Task<HttpResponseMessage> Send(
        HttpClient client, Func<Task<HttpResponseMessage>> call, CancellationToken ct = default)
    {
        var response = await SendRaw(call, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        response.Dispose();
        await InvalidateTokenAsync();
        // 古い Bearer をトークンの取得口へ運ばない。
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(client));
        return await SendRaw(call, ct);
    }

    private static async Task<HttpResponseMessage> SendRaw(
        Func<Task<HttpResponseMessage>> call, CancellationToken ct = default)
    {
        try
        {
            return await call();
        }
        catch (HttpRequestException ex)
        {
            throw Failed("IdP（Keycloak）へ到達できない。", ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 照合の読み取りの呼び出し元の取り消し（停止要求・1 回の照合の期限）。時間切れの失敗へ畳まない。
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // 書き込みは要求の取り消しを伝えていない（CancellationToken.None）ので、ここへ来るのは HttpClient の Timeout である。
            throw Failed("IdP（Keycloak）の応答が期限内に返らない。", ex);
        }
    }

    // 応答の本文が読めない（形が違う・途中で切れた）は `Failed`（502）へ写す。差し替えの try の外でも 500 にしない。
    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct = default)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException or OperationCanceledException)
        {
            throw Failed("IdP（Keycloak）の応答を読めない。", ex);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string what)
    {
        if (!response.IsSuccessStatusCode)
            throw Failed($"{what}が失敗した（HTTP {(int)response.StatusCode}）。");
    }

    private static IdpProvisioningException Failed(string message, Exception? inner = null)
        => new(IdpProvisioningFailure.Failed, message, inner);

    private async Task<HttpClient> AuthorizedClientAsync()
    {
        var client = httpClientFactory.CreateClient(ServiceAccountProvisioningRegistration.KeycloakClientName);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(client));
        return client;
    }

    private async Task InvalidateTokenAsync()
    {
        await _tokenLock.WaitAsync();
        try
        {
            _token = null;
            _tokenExpiresAt = DateTimeOffset.MinValue;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // 認証は client_credentials（機密クライアント。既定 `mcp-client-admin`）。`KeycloakIdentityAdminClient` と同じ形
    // （60 秒の余裕を持って失効させる）。
    private async Task<string> AccessTokenAsync(HttpClient client)
    {
        if (_token is not null && clock.GetUtcNow() < _tokenExpiresAt) return _token;

        await _tokenLock.WaitAsync();
        try
        {
            if (_token is not null && clock.GetUtcNow() < _tokenExpiresAt) return _token;

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
            });
            var response = await SendRaw(() => client.PostAsync(
                $"realms/{Realm}/protocol/openid-connect/token", content, CancellationToken.None));
            EnsureSuccess(response, "管理用トークンの取得");
            var token = await ReadJsonAsync<TokenResponse>(response)
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

    private sealed record KeycloakClient(
        string? Id, string? ClientId, Dictionary<string, string>? Attributes = null, bool? Enabled = null,
        bool? ServiceAccountsEnabled = null, bool? AuthorizationServicesEnabled = null);

    private sealed record KeycloakUser(
        string? Id,
        string? Username,
        Dictionary<string, List<string>?>? Attributes);

    // CodeQL（Log entries created from user input）: 利用者の入力（clientId）に由来する IdP の ID を
    // 行指向のログへそのまま落とさない（制御文字を潰し、長さを切る。ToolDeclarationSource と同じ形）。
    private static string ForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "(不明)";
        var cleaned = new string(Array.ConvertAll(value.ToCharArray(), c => char.IsControl(c) ? '_' : c));
        return cleaned.Length <= 128 ? cleaned : cleaned[..128] + "…";
    }
}
