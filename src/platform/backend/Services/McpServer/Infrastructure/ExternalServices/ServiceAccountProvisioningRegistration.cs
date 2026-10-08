using McpServer.Domain.Ports;

namespace McpServer.Infrastructure.ExternalServices;

// FR-16, SC-12, 計画 ADR-0123 決定 2・4, [[IADR-0516]] 決定 2・5 (#1786): IdP への書き込み口の選択と、その資格情報の受け取り。
//
// 値域（`McpClientProvisioning:Provider`）:
//   - `keycloak`  … Keycloak Admin REST へ書く（`McpClientProvisioning:Keycloak:{BaseUrl,Realm,ClientId,ClientSecret}` が必須）。
//   - `in-memory` … プロセス内に書く。**非配備ホスト限定**（IADR-0329 と同じ許可集合）。
//   - 未設定      … 🔴 **書き込み口が無い。** 無人の登録・属性の差し替えを 503 で拒む（登録簿にも書かない）。
//
// 🔴 **未設定を起動失敗にしない理由**（`IdentityAdmin:Provider` との違い）: 配備の資格情報（realm の管理用クライアントと
//   その secret の供給）は本 PR の後の段で入る（IADR-0516 §残余）。起動失敗にすると、その間 MCP サーバーそのもの
//   （ツールの公開・有人の登録・無効化）が止まる。**未設定は「無人を登録簿だけへ書く」へは倒さない** —— それは ADR-0123 が
//   改めた現状そのもの（検証の掛からない属性を写しとして残す）であり、決定 4 の暫定手段（IdP へ配らない）と同じ側の 503 にする。
//   ［2026-10-09 追記 / #1817］配備（helm・compose）は `keycloak` を宣言し、資格情報（realm の `mcp-client-admin` と Secret
//   `mcp-client-admin-oidc`）を配線した。未設定の分岐は、構成を欠いた配備のための安全側として残す（描画で未宣言の配備が残らないことは
//   scripts/helm-mcp-client-provisioning.test.js が固定する）。
public static class ServiceAccountProvisioningRegistration
{
    public const string ProviderKey = "McpClientProvisioning:Provider";
    public const string KeycloakProvider = "keycloak";
    public const string InMemoryProvider = "in-memory";

    /// <summary>後段（Keycloak Admin REST）の named HttpClient 名。</summary>
    public const string KeycloakClientName = "McpClientProvisioningKeycloak";

    /// <summary>
    /// 偽の書き込み口を選んでよいホストの環境名（許可集合＝deny by default）。認可サービスの
    /// <c>IdentityAdminRegistration.NonDeployedEnvironments</c> と同じ 3 つ（IADR-0329）。
    /// </summary>
    public static readonly string[] NonDeployedEnvironments =
        [Environments.Development, "Testing", "Integration"];

    // 🔴 **選択は解決時に行う**（構成は DI の <see cref="IConfiguration"/> から読む）。最小ホスティングでは、ホストの構築後に
    // 足された構成（WebApplicationFactory の上書きを含む）が登録の時点の <c>builder.Configuration</c> には見えない。
    // 起動時に 1 度解決して落とすのは Program.cs が行う（公開構成の検証と同じ理由。要求を受ける前に落とす）。
    public static IServiceCollection AddServiceAccountProvisioning(this IServiceCollection services)
    {
        services.AddHttpClient(KeycloakClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<ServiceAccountProvisioningOptions>();
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            // PR #1816 監査 🟡-1: 期限は明示する。口は要求の取り消しを伝えないので、時間切れだけが書き込みを止める
            // （時間切れは `Failed`＝502 へ写し、作りかけは補償で消す）。
            client.Timeout = options.Timeout;
        });
        services.AddSingleton(sp => ServiceAccountProvisioningOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<InMemoryServiceAccountProvisioner>();
        services.AddSingleton<UnconfiguredServiceAccountProvisioner>();
        services.AddSingleton<KeycloakServiceAccountProvisioner>();
        services.AddSingleton<IServiceAccountProvisioner>(sp => Select(
            sp, sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>()));
        // [[IADR-0516]] 決定 5（#1818）: 照合の読み取りの口は、選ばれた書き込み口そのもの（3 つの実装がどれも兼ねる）。
        // 選択を 2 つにしない —— 書く先と照合が読む先が別の IdP になる構成を作れないようにする。
        services.AddSingleton<IServiceAccountDirectory>(sp =>
            (IServiceAccountDirectory)sp.GetRequiredService<IServiceAccountProvisioner>());
        return services;
    }

    private static IServiceAccountProvisioner Select(
        IServiceProvider sp, IConfiguration configuration, IHostEnvironment environment)
    {
        var provider = configuration[ProviderKey];
        if (string.IsNullOrWhiteSpace(provider))
            return sp.GetRequiredService<UnconfiguredServiceAccountProvisioner>();

        if (string.Equals(provider, InMemoryProvider, StringComparison.OrdinalIgnoreCase))
        {
            if (!NonDeployedEnvironments.Contains(environment.EnvironmentName, StringComparer.Ordinal))
                throw new InvalidOperationException(
                    $"{ProviderKey}='{InMemoryProvider}' は非配備ホスト"
                    + $"（{string.Join(" / ", NonDeployedEnvironments)}）でしか選べない"
                    + $"（現在の環境は '{environment.EnvironmentName}'）。"
                    + " 偽の書き込み口は IdP へ 1 件も反映しないため、SC-12 の無人の登録が成功したように見えて判定に効かない。");
            return sp.GetRequiredService<InMemoryServiceAccountProvisioner>();
        }

        if (!string.Equals(provider, KeycloakProvider, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{ProviderKey} の値 '{provider}' は不正である（'{KeycloakProvider}' / '{InMemoryProvider}' のいずれか、または未設定）。");

        // 既定の資格情報を埋め込まない（IADR-0286 と同型）。未設定の項目は options の解決で落ちる。
        return sp.GetRequiredService<KeycloakServiceAccountProvisioner>();
    }
}

// [[IADR-0516]] 決定 2: Keycloak Admin REST の接続先と、管理用の機密クライアントの資格情報。
//
// 🔴 与える `realm-management` のクライアントロールは **`manage-clients` と `manage-users` の 2 つだけ**である
//   （作成・補償の削除・サービスアカウントの属性の書き込み。view は manage が含む）。`manage-realm` / `impersonation` は与えない。
//   **認可サービスの `identity-admin` とは別のクライアントにする** —— あちらは `manage-clients` を持たないことが最小権限の要件である
//   （IADR-0301 決定 2・IADR-0329）。
public sealed class ServiceAccountProvisioningOptions
{
    public required string BaseUrl { get; init; }
    public required string Realm { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }

    /// <summary>1 回の管理要求の期限（`McpClientProvisioning:Keycloak:TimeoutSeconds`。既定 10 秒・1〜120 秒）。</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public static ServiceAccountProvisioningOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("McpClientProvisioning:Keycloak");
        return new ServiceAccountProvisioningOptions
        {
            BaseUrl = Require(section, "BaseUrl"),
            Realm = Require(section, "Realm"),
            ClientId = Require(section, "ClientId"),
            ClientSecret = Require(section, "ClientSecret"),
            Timeout = TimeoutOf(section["TimeoutSeconds"]),
        };
    }

    // 値域外は起動時に落とす（打ち間違いを既定へ黙って倒さない）。
    private static TimeSpan TimeoutOf(string? declared)
    {
        if (string.IsNullOrWhiteSpace(declared)) return DefaultTimeout;
        if (int.TryParse(declared.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds is >= 1 and <= 120)
            return TimeSpan.FromSeconds(seconds);
        throw new InvalidOperationException(
            $"McpClientProvisioning:Keycloak:TimeoutSeconds の値 '{declared}' は不正である（1〜120 の整数）。");
    }

    private static string Require(IConfiguration section, string key)
        => section[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"McpClientProvisioning:Keycloak:{key} が未設定である"
                + $"（環境変数 McpClientProvisioning__Keycloak__{key} で注入する）。既定値は持たない。");
}

// 書き込み口が構成されていない配備。**何も書かずに** Unavailable を投げる（呼び出し元は登録簿にも書かない）。
// ［#1818］照合の読み取りも Unavailable を投げる（照合は失敗＝ゲージの系列を出さず、警報「系列が無い」が鳴る）。
public sealed class UnconfiguredServiceAccountProvisioner : IServiceAccountProvisioner, IServiceAccountDirectory
{
    private static IdpProvisioningException Unavailable() => new(
        IdpProvisioningFailure.Unavailable,
        "IdP への書き込み口が構成されていない（McpClientProvisioning:Provider）。"
        + " 無人のクライアントは IdP に作れないため、登録・属性の差し替えを受け付けない。");

    public Task<IdpWrite> CreateAsync(
        string clientId, string displayName, IReadOnlyDictionary<string, string> attributes, CancellationToken ct)
        => throw Unavailable();

    public Task<IdpWrite> ReplaceAttributesAsync(
        string clientId, string displayName, IReadOnlyDictionary<string, string> attributes, bool enabled, CancellationToken ct)
        => throw Unavailable();

    public Task UndoAsync(IdpWrite write, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<IdpClientEntry>> ListClientsAsync(CancellationToken ct) => throw Unavailable();

    public Task<IReadOnlyDictionary<string, string>?> ReadServiceAccountAttributesAsync(string clientId, CancellationToken ct)
        => throw Unavailable();
}

// 非配備ホスト用。IdP の代わりにプロセス内へ書く（Keycloak 版と同じ意味論: 登録は在れば AlreadyExists・差し替えは無ければ作る・
// 入口の印が無い〔`Seed` で置いた〕ものへは書かない・取り消しは現在値が書いた値のままのときだけ戻す）。
// 試験はこの状態を読んで「IdP に何が書かれたか（書かれなかったか）」を確かめる。
// ［#1818］照合の読み取り（`IServiceAccountDirectory`）も持つ。試験は `Tamper` / `SeedManaged` / `Remove` で、入口を通らない
// IdP の直接の操作（ADR-0123 決定 2 が禁じた操作）や補償の残骸を作り、照合がそれを拾うことを確かめる。
public sealed class InMemoryServiceAccountProvisioner : IServiceAccountProvisioner, IServiceAccountDirectory
{
    private sealed record Account(Dictionary<string, string> Attributes, bool Managed, bool Enabled);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.Ordinal);

    /// <summary>IdP 側に在るクライアントの写し（clientId → 属性）。</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Snapshot()
    {
        lock (_gate)
            return _accounts.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(kv.Value.Attributes),
                StringComparer.Ordinal);
    }

    /// <summary>IdP 側のクライアントが有効か（無ければ null）。</summary>
    public bool? IsEnabled(string clientId)
    {
        lock (_gate) return _accounts.TryGetValue(clientId, out var a) ? a.Enabled : null;
    }

    /// <summary>入口を通らずに IdP に在るクライアント（**入口の印が無い**）を置く（試験用）。</summary>
    public void Seed(string clientId, IReadOnlyDictionary<string, string>? attributes = null)
    {
        lock (_gate)
            _accounts[clientId] = new Account(
                new Dictionary<string, string>(attributes ?? new Dictionary<string, string>()), Managed: false, Enabled: true);
    }

    /// <summary>入口の印つきのクライアントを、登録簿を通らずに置く（補償が走らなかった残骸＝孤児の再現。試験用）。</summary>
    public void SeedManaged(string clientId, IReadOnlyDictionary<string, string>? attributes = null)
    {
        lock (_gate)
            _accounts[clientId] = new Account(
                new Dictionary<string, string>(attributes ?? new Dictionary<string, string>()), Managed: true, Enabled: true);
    }

    /// <summary>サービスアカウントの属性を、入口を通らずに書き換える（IdP の管理画面での直接の割当の再現。試験用）。</summary>
    public void Tamper(string clientId, IReadOnlyDictionary<string, string> attributes)
    {
        lock (_gate)
            _accounts[clientId] = _accounts[clientId] with { Attributes = new Dictionary<string, string>(attributes) };
    }

    /// <summary>クライアントを IdP から消す（IdP の管理画面での直接の削除の再現。試験用）。</summary>
    public void Remove(string clientId)
    {
        lock (_gate) _accounts.Remove(clientId);
    }

    public Task<IReadOnlyList<IdpClientEntry>> ListClientsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult<IReadOnlyList<IdpClientEntry>>(
                [.. _accounts.Select(kv => new IdpClientEntry(kv.Key, kv.Value.Managed))]);
    }

    public Task<IReadOnlyDictionary<string, string>?> ReadServiceAccountAttributesAsync(string clientId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult<IReadOnlyDictionary<string, string>?>(
                _accounts.TryGetValue(clientId, out var a) ? new Dictionary<string, string>(a.Attributes) : null);
    }

    public Task<IdpWrite> CreateAsync(
        string clientId, string displayName, IReadOnlyDictionary<string, string> attributes, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_accounts.ContainsKey(clientId)) return Task.FromResult(IdpWrite.AlreadyExisting(clientId));
            return Task.FromResult(Create(clientId, attributes, enabled: true));
        }
    }

    public Task<IdpWrite> ReplaceAttributesAsync(
        string clientId, string displayName, IReadOnlyDictionary<string, string> attributes, bool enabled, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_accounts.TryGetValue(clientId, out var current))
                return Task.FromResult(Create(clientId, attributes, enabled));
            if (!current.Managed) return Task.FromResult(IdpWrite.AlreadyExisting(clientId));

            _accounts[clientId] = current with { Attributes = new Dictionary<string, string>(attributes) };
            return Task.FromResult(new IdpWrite(IdpWriteKind.Updated, clientId, clientId, clientId,
                current.Attributes, new Dictionary<string, string>(attributes)));
        }
    }

    public Task UndoAsync(IdpWrite write, CancellationToken ct)
    {
        lock (_gate)
        {
            switch (write.Kind)
            {
                case IdpWriteKind.Created:
                    _accounts.Remove(write.ClientId);
                    break;
                case IdpWriteKind.Updated when _accounts.TryGetValue(write.ClientId, out var current):
                    if (write.WrittenAttributes is { } written
                        && !KeycloakServiceAccountProvisioner.SameAttributes(written, current.Attributes))
                        break;
                    _accounts[write.ClientId] = current with
                    {
                        Attributes = new Dictionary<string, string>(write.PreviousAttributes ?? new Dictionary<string, string>()),
                    };
                    break;
            }
        }
        return Task.CompletedTask;
    }

    private IdpWrite Create(string clientId, IReadOnlyDictionary<string, string> attributes, bool enabled)
    {
        _accounts[clientId] = new Account(new Dictionary<string, string>(attributes), Managed: true, Enabled: enabled);
        return new IdpWrite(IdpWriteKind.Created, clientId, clientId, clientId,
            WrittenAttributes: new Dictionary<string, string>(attributes));
    }
}
