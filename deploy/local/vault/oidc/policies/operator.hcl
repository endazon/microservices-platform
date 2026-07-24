# IADR-0094 (#353): dev Vault の運用者ポリシー（realm ロール platform-operator → external group 経由で付与）。
# 読み取り中心（secret の参照・一覧）。書き込み/管理系は付与しない（fail-safe・最小権限寄り）。
path "secret/data/*" {
  capabilities = ["read", "list"]
}
path "secret/metadata/*" {
  capabilities = ["read", "list"]
}
path "sys/mounts" {
  capabilities = ["read"]
}
