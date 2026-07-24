# IADR-0094 (#353): dev Vault の管理者ポリシー（realm ロール platform-admin → external group 経由で付与）。
# dev 検証用の広め権限。本番の最小権限設計は Tier 3（本オーバーレイ対象外）。
path "*" {
  capabilities = ["create", "read", "update", "delete", "list", "sudo"]
}
