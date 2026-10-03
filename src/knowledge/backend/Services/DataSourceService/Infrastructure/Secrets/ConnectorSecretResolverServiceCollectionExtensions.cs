using DataSourceService.Domain.Ports;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DataSourceService.Infrastructure.Secrets;

// FR-01, NFR-18, [[IADR-0495]] 決定 3 (#458 段 S1): コネクタの資格情報の解決器の配線。
//
// - `Vault:Address` が**空**（Vault を配備しない構成。compose・既定の helm values）→ 移送期間用の素通しだけ
//   （[[IADR-0493]] 決定 2 と同じ。`vault:` 参照は `ResolverUnavailable` で fail-closed）。
// - `Vault:Address` が**在る** → Vault 解決器（平文は素通しへ委ね、参照は Vault から読む）。
//
// 判定は解決時（構成が確定した後）に行う。`builder.Configuration` を起動前に読むと、試験の構成の上書きが効かない。
public static class ConnectorSecretResolverServiceCollectionExtensions
{
    public static IServiceCollection AddConnectorSecretResolver(this IServiceCollection services)
    {
        services.AddOptions<VaultConnectorSecretOptions>().BindConfiguration(VaultConnectorSecretOptions.SectionName);
        services.AddHttpClient(VaultConnectorSecretResolver.ClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<VaultConnectorSecretOptions>>().Value;
            // 未構成なら BaseAddress を持たせない（そもそも Vault 解決器が配線されない）。
            if (options.IsConfigured)
                client.BaseAddress = new Uri(options.Address!.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));
        });

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IServiceAccountTokenReader, FileServiceAccountTokenReader>();
        services.TryAddSingleton<PlaintextPassthroughConnectorSecretResolver>();
        services.TryAddSingleton<VaultConnectorSecretResolver>();
        services.TryAddSingleton<IConnectorSecretResolver>(sp =>
            sp.GetRequiredService<IOptions<VaultConnectorSecretOptions>>().Value.IsConfigured
                ? sp.GetRequiredService<VaultConnectorSecretResolver>()
                : sp.GetRequiredService<PlaintextPassthroughConnectorSecretResolver>());
        return services;
    }
}
