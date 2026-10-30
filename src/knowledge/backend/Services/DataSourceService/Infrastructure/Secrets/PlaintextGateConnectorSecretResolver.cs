using DataSourceService.Domain;
using DataSourceService.Domain.Ports;
using Microsoft.Extensions.Options;

namespace DataSourceService.Infrastructure.Secrets;

// NFR-18, FR-01, 計画 ADR-0126 フォローアップ 2, [[IADR-0664]] 決定 2 (#458 段 S4): 移送期間の旗で**平文の解決を止める**飾り。
//
// 作業仕様書 20261003_458 §窓 1 の「両端」の形である。
//   - 参照の行（P+）: 内側の解決器へ委ねる（旗に依らない）。
//   - 平文の行・期間中（P−）: 内側へ委ねる（素通し）。既存のデータソースを一斉に止めない。
//   - 平文の行・期間後（P−）: `PlaintextRejected` で fail-closed。平文を外部へ送らない。
// 判定は `PlaintextCredential.IsPlaintext` と同じ（参照を名乗らない・空白でない）。キーを持たない呼び出しなので、値だけで見る。
public sealed class PlaintextGateConnectorSecretResolver(
    IConnectorSecretResolver inner, IOptions<ConnectorCredentialOptions> options) : IConnectorSecretResolver
{
    // 内側の解決器（配線の試験が、Vault の所在で選ばれた解決器を確かめる）。
    internal IConnectorSecretResolver Inner => inner;

    public Task<ConnectorSecretResolution> ResolveAsync(string configuredValue, CancellationToken ct)
    {
        if (!options.Value.PlaintextAllowed
            && !string.IsNullOrWhiteSpace(configuredValue)
            && !ConnectorSecretReference.LooksLikeReference(configuredValue))
        {
            return Task.FromResult(ConnectorSecretResolution.Failed(ConnectorSecretFailure.PlaintextRejected));
        }

        return inner.ResolveAsync(configuredValue, ct);
    }
}
