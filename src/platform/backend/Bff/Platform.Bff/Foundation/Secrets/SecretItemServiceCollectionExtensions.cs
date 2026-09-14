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
