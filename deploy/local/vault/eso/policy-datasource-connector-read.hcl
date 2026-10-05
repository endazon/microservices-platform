# FR-01, UC-04, NFR-18, 09_datasource-connectors §認証・秘匿情報, IADR-0495 決定 1 (#458 段 S1):
# datasource-service がコネクタの資格情報を**実行時に読む**ための policy（role `datasource-connector-reader` に付与）。
#
# 🔴 専用接頭辞 `datasource/` の data の `read` だけを与える。
#    - `msp/*` の外に置く —— ESO の policy（policy-eso-read.hcl の `secret/data/msp/*`）がこの接頭辞を読めないこと、
#      かつ本 policy が `msp/*`（基盤の資格情報）を読めないことの両方を、path の字面で成り立たせる。
#    - `create` / `update` / `patch` / `delete` / `destroy` / `list` を与えない。書き手は段 S2 以降で決める（planning#716）。
#      ［2026-10-06 / #458 段 S2］書き手は BFF（SC-22 の群「データソースの資格情報」）に決まった（計画 ADR-0126・IADR-0501）。
#      BFF の policy は policy-bff-secret-group-write.hcl（`secret/data/datasource/+` に create・patch。read なし）。
#      datasource-service はデータソースを削除しても Vault の値を消さない（作業仕様書 20261003_458 §窓 2）。
#    - metadata は与えない（解決器は data の GET だけを送る）。
# 本ファイルの path 集合が「この 1 本だけ」であることと、ESO の policy と交わらないことは
# DataSourceService.Tests（ConnectorSecretVaultPolicyTests）が固定する。
path "secret/data/datasource/*" {
  capabilities = ["read"]
}
