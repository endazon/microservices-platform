# IADR-0096 (#310): ESO 用の読み取り専用ポリシー（role `eso` に付与）。同名 store `vault-backend` は MSP と AST の
# 両 ExternalSecret が共有するため、**両方の path** を read 許可する（片方だけだと `ESO=1` 有効化時に他方が 403 で
# 同期失敗する）。最小権限は read のみ（write/delete は付与しない）。
# - MSP: secret/msp/*（本 PR の llm-provider-credentials ほか後続 secret）
# - AST: secret/ai-stock-trading/*（AST chart の ExternalSecret が参照）
path "secret/data/msp/*" {
  capabilities = ["read"]
}
path "secret/metadata/msp/*" {
  capabilities = ["read", "list"]
}
path "secret/data/ai-stock-trading/*" {
  capabilities = ["read"]
}
path "secret/metadata/ai-stock-trading/*" {
  capabilities = ["read", "list"]
}
