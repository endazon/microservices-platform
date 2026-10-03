using Microsoft.Extensions.Options;

namespace DataSourceService.Infrastructure.Secrets;

// NFR-18, [[IADR-0495]] 決定 3 (#458 段 S1): Pod の ServiceAccount トークンを読む口（試験が差し替える）。
// BFF の同名の型（`Platform.Bff.Foundation.Secrets`）と同型だが、ユニット外参照の規則（IADR-0117）で BFF を引けないため
// サービス内に置く（[[IADR-0495]] §結果）。
public interface IServiceAccountTokenReader
{
    Task<string> ReadAsync(CancellationToken ct);
}

public sealed class FileServiceAccountTokenReader(IOptions<VaultConnectorSecretOptions> options) : IServiceAccountTokenReader
{
    public async Task<string> ReadAsync(CancellationToken ct) =>
        (await File.ReadAllTextAsync(options.Value.ServiceAccountTokenPath, ct)).Trim();
}
