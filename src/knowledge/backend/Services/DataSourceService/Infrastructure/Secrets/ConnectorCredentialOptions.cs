namespace DataSourceService.Infrastructure.Secrets;

// NFR-18, FR-01, 計画 ADR-0126 フォローアップ 2, [[IADR-0663]] 決定 2 (#458 段 S4): **移送期間の旗**（`ConnectorCredentials:*`）。
//
// helm の `services.datasource.connectorCredentials.plaintextAllowed` が `ConnectorCredentials__PlaintextAllowed` へ描画する。
// - `true`（既定。移送期間）: 設定の平文を従前どおり受け入れる（同期は平文で動き、SC-06 の API も平文を保存できる）。
// - `false`（移送の後）: 平文を受け入れない。
//   - 同期: 平文の資格情報は `PlaintextRejected` で fail-closed（外部へ送らない）。
//   - SC-06 の API: 秘密のキーへの平文の書き込みを 400 で拒む。
//   - 起動: 設定に平文の行が 1 つでも残っていれば**起動を拒む**（旗を偽にできるのは、平文の行が 0 件のときだけ）。
// 🔴 **時刻で切らない。** 平文の行の件数を数えてから切る（作業仕様書 20261003_458 §窓 1）。
public sealed class ConnectorCredentialOptions
{
    public const string SectionName = "ConnectorCredentials";

    public bool PlaintextAllowed { get; set; } = true;
}
