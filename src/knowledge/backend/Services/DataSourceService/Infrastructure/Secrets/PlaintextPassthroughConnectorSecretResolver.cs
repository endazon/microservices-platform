using DataSourceService.Domain;
using DataSourceService.Domain.Ports;

namespace DataSourceService.Infrastructure.Secrets;

// FR-01, NFR-18, [[IADR-0493]] 決定 2 (#458 段 S0): **移送期間用**の資格情報の解決器。Vault には繋がない。
//
// - 平文（`vault:` で始まらない値）は**そのまま返す** —— 既存行（平文で保存されたデータソース）の同期を止めない。
//   これは窓 1（作業仕様書 §窓 1）の「前の端」であり、移送期間の終了（平文の行 0 件の readiness 検査と
//   期間フラグ）は段 S4 で入れる。**段 S4 までは本実装が平文を受け入れ続ける。**
// - 参照（`vault:` で始まる値）は**解決器が無い**として失敗させる。🔴 **平文として外部へ送らない** ——
//   参照の文字列を Bearer トークンやパスワードとして相手へ渡すと、Vault のパス構造を外部へ漏らすうえ、
//   「資格情報が設定されていない」状態を「認証に失敗した」状態に見せかける（fail-closed）。
//   形を成していない参照は `MalformedReference` で区別する（設定の誤りの切り分けのため）。
//
// 段 S1（[[IADR-0495]] 決定 3）: 本実装は、Vault が未構成（`Vault:Address` が空）の構成の解決器であり、
// かつ `VaultConnectorSecretResolver` の平文側（移送期間の素通し）でもある。段 S4 の着地で撤去される（[[IADR-0493]] 決定 2）。
public sealed class PlaintextPassthroughConnectorSecretResolver : IConnectorSecretResolver
{
    public Task<ConnectorSecretResolution> ResolveAsync(string configuredValue, CancellationToken ct)
    {
        if (!ConnectorSecretReference.LooksLikeReference(configuredValue))
            return Task.FromResult(ConnectorSecretResolution.Resolved(configuredValue));

        return Task.FromResult(ConnectorSecretReference.TryParse(configuredValue, out _)
            ? ConnectorSecretResolution.Failed(ConnectorSecretFailure.ResolverUnavailable)
            : ConnectorSecretResolution.Failed(ConnectorSecretFailure.MalformedReference));
    }
}
