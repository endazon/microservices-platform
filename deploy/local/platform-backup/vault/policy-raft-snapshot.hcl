# NFR-21, NFR-18, IADR-0548 (#1868): 秘匿管理の日次バックアップ（CronJob platform-backup-vault）が使うポリシー。
# raft の整合したスナップショットを**取るだけ**。🔴 足さないこと:
#   - update（= snapshot の restore。秘匿管理の中身を丸ごと置き換えられる）・`sys/storage/raft/snapshot-force`
#   - KV（secret/*）の read —— スナップショットは barrier で暗号化されたまま取れるので、値を読む権限は要らない
# role（auth/kubernetes/role/platform-backup-vault）は token_no_default_policy=true で、このポリシーだけを持つ。
path "sys/storage/raft/snapshot" {
  capabilities = ["read"]
}
