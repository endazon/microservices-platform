# IADR-0457 (#1479): 経路B の Vault（永続化・既定）。dev モード（インメモリ）の代わりに file ストレージを PVC に置く。
# ⚠️ ローカル dev 専用。本番の Vault 化充足ではない（本番は unseal / 監査 / HA / ローテーションを要する）。
storage "file" {
  path = "/vault/data"
}

listener "tcp" {
  address     = "0.0.0.0:8200"
  tls_disable = true
}

api_addr     = "http://vault.platform-infra:8200"
disable_mlock = true
ui            = true
