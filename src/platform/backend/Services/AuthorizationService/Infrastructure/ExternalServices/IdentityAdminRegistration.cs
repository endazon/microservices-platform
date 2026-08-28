using AuthorizationService.Domain.Ports;

namespace AuthorizationService.Infrastructure.ExternalServices;

// FR-05, FR-09, SC-17, IADR-0301 決定 3: 身元プロバイダ実装の選択と、その資格情報の受け取り。
//
// 🔴 **`IdentityAdmin:Provider` に既定を置かない。**
//   - 既定を `in-memory` にすると、構成の注入漏れが「起動失敗」ではなく
//     「**反映したつもりで消える**」へ倒れる（#1012 と同型の静かな壊れ。管理者は保存が成功したと
//     読み、認可判定は一切変わらない）。
//   - 既定を `keycloak` にすると、`realm-management` ロールを持つ機密クライアントがまだ realm に
//     無いため、既存の配備が一斉に起動できなくなる。
//   **どちらの既定も誤りなので、宣言そのものを必須にして選択を配備側へ出す。**
public static class IdentityAdminRegistration
{
    public const string ProviderKey = "IdentityAdmin:Provider";
    public const string KeycloakProvider = "keycloak";
    public const string InMemoryProvider = "in-memory";

    /// <summary>後段（Keycloak Admin REST）の named HttpClient 名。</summary>
    public const string KeycloakClientName = "KeycloakAdmin";

    public static IServiceCollection AddIdentityAdminClient(
        this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration[ProviderKey];
        if (string.IsNullOrWhiteSpace(provider))
            throw new InvalidOperationException(
                $"{ProviderKey} が未設定である（環境変数 IdentityAdmin__Provider で注入する）。"
                + $" 値域は '{KeycloakProvider}'（実 IdP へ反映する）または"
                + $" '{InMemoryProvider}'（開発・テスト用。**実 IdP へは反映されない**）。"
                + " 既定値は持たない —— 既定を in-memory にすると注入漏れが"
                + "「反映したつもりで消える」へ倒れ、既定を keycloak にすると"
                + "資格情報未整備の配備が一斉に起動できなくなるためである。");

        if (string.Equals(provider, InMemoryProvider, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IIdentityAdminClient, InMemoryIdentityAdminClient>();
            return services;
        }

        if (!string.Equals(provider, KeycloakProvider, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{ProviderKey} の値 '{provider}' は不正である"
                + $"（'{KeycloakProvider}' / '{InMemoryProvider}' のいずれか）。");

        // NFR, #1012 / IADR-0286 と同型: **既定の資格情報を埋め込まない。**
        // 埋め込むと、構成の注入漏れが「起動失敗」ではなく「既定の資格情報で接続成功」へ倒れる。
        var options = KeycloakAdminOptions.FromConfiguration(configuration);
        services.AddSingleton(options);
        services.AddHttpClient(KeycloakClientName, client =>
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"));
        services.AddSingleton<IIdentityAdminClient, KeycloakIdentityAdminClient>();
        return services;
    }
}

// FR-05, SC-17, IADR-0301 決定 2: Keycloak Admin REST の接続先と機密クライアントの資格情報。
//
// 与えるクライアントロールは `realm-management` の **3 つだけ**である
// （`view-users` / `manage-users` / `view-realm`）。`manage-realm` / `manage-clients` /
// `create-client` / `impersonation` は与えない。**取り込み経路のサービスクライアントとは
// 別のクライアントにする**（計画 06_technical/09_datasource-connectors が「別である」と明記し、
// 権限設計を保留していた点。IADR-0301 決定 2 が本節を埋める）。
public sealed class KeycloakAdminOptions
{
    public required string BaseUrl { get; init; }
    public required string Realm { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }

    public static KeycloakAdminOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("IdentityAdmin:Keycloak");
        return new KeycloakAdminOptions
        {
            BaseUrl = Require(section, "BaseUrl"),
            Realm = Require(section, "Realm"),
            ClientId = Require(section, "ClientId"),
            ClientSecret = Require(section, "ClientSecret"),
        };
    }

    private static string Require(IConfiguration section, string key)
        => section[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"IdentityAdmin:Keycloak:{key} が未設定である"
                + $"（環境変数 IdentityAdmin__Keycloak__{key} で注入する）。"
                + " 既定値は持たない —— 未注入を「既定の資格情報で接続成功」へ倒さないためである。");
}
