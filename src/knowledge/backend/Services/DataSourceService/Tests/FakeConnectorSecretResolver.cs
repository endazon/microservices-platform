using DataSourceService.Domain;
using DataSourceService.Domain.Ports;

namespace DataSourceService.Tests;

// NFR-18, [[IADR-0493]] (#458 段 S0): 試験用の資格情報の解決器。
//
// - 参照（`vault:…`）は `References` の表で引く。表に無ければ `NotFound`。
// - 平文は既定でそのまま返す（移送期間の解決器と同じ）。
// - 呼び出しをすべて記録する（**解決器の呼び出しを数える**ための口。作業仕様書 §設計 S0 の試験）。
// - `Version` を進めると、参照の解決結果の末尾が変わる（同期の途中で Vault の版が変わったことを模す。§窓 2）。
// - `Throw` を与えると例外を投げる（解決器そのものの失敗を模す）。
public sealed class FakeConnectorSecretResolver : IConnectorSecretResolver
{
    public Dictionary<string, string> References { get; } = new(StringComparer.Ordinal);

    public List<string> Calls { get; } = [];

    public int Version { get; set; } = 1;

    public bool AppendVersion { get; set; }

    public Exception? Throw { get; set; }

    public ConnectorSecretFailure? FailWith { get; set; }

    public Task<ConnectorSecretResolution> ResolveAsync(string configuredValue, CancellationToken ct)
    {
        Calls.Add(configuredValue);
        if (Throw is not null)
            throw Throw;
        if (FailWith is { } failure)
            return Task.FromResult(ConnectorSecretResolution.Failed(failure));
        if (!ConnectorSecretReference.LooksLikeReference(configuredValue))
            return Task.FromResult(ConnectorSecretResolution.Resolved(configuredValue));
        if (!References.TryGetValue(configuredValue.Trim(), out var value))
            return Task.FromResult(ConnectorSecretResolution.Failed(ConnectorSecretFailure.NotFound));
        return Task.FromResult(ConnectorSecretResolution.Resolved(AppendVersion ? $"{value}-v{Version}" : value));
    }
}
