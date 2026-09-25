using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Platform.Bff.Foundation.Secrets;

// SC-22, ADR-0095 決定 3, IADR-0433, IADR-0453 (#1411): 秘密情報の投入経路の登録。
public static class SecretItemServiceCollectionExtensions
{
    /// <summary>allowlist の置き場を上書きする構成キー。既定は出力ディレクトリの同梱ファイル。</summary>
    public const string CatalogPathKey = "SecretItems:CatalogPath";

    public static IServiceCollection AddSecretItemInjection(this IServiceCollection services)
    {
        // allowlist は構成（テストの上書きを含む）が確定した後に 1 度だけ読む。
        // 起動時に必ず解決させる（`EnsureSecretItemCatalogLoaded`）ので、読めなければ BFF は起動しない。
        services.AddSingleton(sp =>
        {
            var configured = sp.GetRequiredService<IConfiguration>()[CatalogPathKey];
            var path = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(AppContext.BaseDirectory, SecretItemCatalog.DefaultFileName)
                : configured;
            return SecretItemCatalog.Load(path);
        });

        services.AddOptions<VaultOptions>().BindConfiguration(VaultOptions.SectionName);
        services.AddHttpClient(VaultKvClient.ClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<VaultOptions>>().Value;
            // 未構成なら BaseAddress を持たせない（端点は送る前に 503 を返す）。
            if (options.IsConfigured)
                client.BaseAddress = new Uri(options.Address!.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));
        });

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IServiceAccountTokenReader, FileServiceAccountTokenReader>();
        services.TryAddSingleton<IVaultKvClient, VaultKvClient>();
        services.TryAddSingleton<ISecretWriteRecordStore, DistributedCacheSecretWriteRecordStore>();

        // IADR-0456 決定 4 (#1477): 書き込み後に ExternalSecret へ即時同期を依頼する。名乗りは Vault と同じ Pod の SA トークン。
        services.AddOptions<ExternalSecretSyncOptions>().BindConfiguration(ExternalSecretSyncOptions.SectionName);
        services.AddHttpClient(ExternalSecretSyncRequester.ClientName, (sp, client) =>
                client.Timeout = TimeSpan.FromSeconds(
                    Math.Max(1, sp.GetRequiredService<IOptions<ExternalSecretSyncOptions>>().Value.TimeoutSeconds)))
            .ConfigurePrimaryHttpMessageHandler(sp =>
                ExternalSecretSyncRequester.CreateApiServerHandler(sp.GetRequiredService<IOptions<ExternalSecretSyncOptions>>().Value));
        services.TryAddSingleton<IExternalSecretSyncRequester, ExternalSecretSyncRequester>();

        // ADR-0104 決定 2, IADR-0460 決定 1 (#1502): 一覧の「供給元」を、同期先 ExternalSecret の有無（`get`）から読む。
        // 構成・通信路・名乗りは同期依頼と同じもの（`ExternalSecretSync:*`・`KubernetesApi` クライアント・SA トークン）を使う。
        services.TryAddSingleton<IExternalSecretPresenceReader, ExternalSecretPresenceReader>();
        return services;
    }

    /// <summary>
    /// 🔴 **fail-closed**: allowlist を起動時に読み込ませる。読めなければ例外で起動を止める
    /// （IADR-0433 決定 3。「読めなかったから全部許す／空にする」を採らない）。
    /// </summary>
    public static WebApplication EnsureSecretItemCatalogLoaded(this WebApplication app)
    {
        app.Services.GetRequiredService<SecretItemCatalog>();
        return app;
    }
}
