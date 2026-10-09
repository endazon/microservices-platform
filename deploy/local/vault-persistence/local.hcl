# IADR-0457 (#1479): 経路B の秘匿管理（永続化・既定）。dev モード（インメモリ）の代わりにストレージを PVC に置く。
# ⚠️ ローカル dev 専用。本番の秘匿管理の充足ではない（本番は unseal / 監査 / HA / ローテーションを要する）。
#
# NFR-18, ADR-0132, IADR-0525 (#1840): 製品は OpenBao（Vault API 互換・MPL-2.0）。Vault の file ストレージから 2 点を改めた:
#   - ストレージは raft（単一ノード）。🔴 OpenBao 2.x は file ストレージを持たない（`unknown storage type file` で起動しない。実測）。
#     パスは /vault/data の下の raft/ に分ける —— 旧 Vault の file ストレージ（core/・logical/・sys/）と取り違えないため。
#     ラッパーは「core/ が在って raft/vault.db が無い」なら起動を止める（移行の前に init して unseal 鍵を上書きしないため）。
#   - audit device は**設定ファイルで宣言する**。🔴 OpenBao は API での audit device の作成を既定で拒む
#     （`cannot enable audit device via API`。実測）。止まらない方の device（標準出力）はここで宣言し、
#     collector への socket device はラッパーが collector に届いてから別の設定ファイルで足す（vault-entrypoint.sh）。
#   - `disable_mlock` は OpenBao に無い設定なので外した（与えると unsupported の警告になる）。
storage "raft" {
  path    = "/vault/data/raft"
  node_id = "vault-0"
}

listener "tcp" {
  address     = "0.0.0.0:8200"
  tls_disable = true
}

api_addr     = "http://vault.platform-infra:8200"
# raft は cluster_addr を要る（単一ノード。Pod の外へは出さない）。
cluster_addr = "http://127.0.0.1:8201"
ui           = true

# NFR-18, ADR-0124 決定 2, IADR-0486 / IADR-0525: 止まらない方の audit device（標準出力 → kubelet のコンテナログ）。
# 🔴 値を記録しない。log_raw=false / hmac_accessor=true を**明示する**（既定と同じ値を書いて固定する）。
#    平文を出す設定（log_raw=true・audit_non_hmac_*）を足さないこと —— scripts/scripts.repo.test.js（#1683）が落ちる。
audit "file" "stdout" {
  description = "audit to container stdout (kubelet log)"
  options {
    file_path     = "stdout"
    log_raw       = "false"
    hmac_accessor = "true"
    format        = "json"
  }
}
