#!/usr/bin/env bash
# NFR-21, NFR-18, IADR-0548 (#1868): 秘匿管理の日次バックアップ（CronJob platform-backup-vault）が raft のスナップショットを
# 取るための認証を、秘匿管理（OpenBao）へ入れる runtime bootstrap（再実行可・冪等。deploy/local/vault/eso/bootstrap.sh と同型）。
#
#   bash deploy/local/platform-backup/vault/bootstrap.sh
#
# 入れるもの:
#   1. Vault の SA に TokenReview の権限（deploy/local/vault/eso/vault-auth-rbac.yaml。ESO=1 と同じもの。k8s auth の reviewer）
#   2. kubernetes 認証（未有効時のみ enable。config は ESO の bootstrap と同じ値＝in-cluster local mode）
#   3. ポリシー platform-backup-raft-snapshot（sys/storage/raft/snapshot の read だけ。policy-raft-snapshot.hcl）
#   4. role platform-backup-vault（SA platform-infra/platform-backup-vault にだけ束縛。default ポリシーを付けない。
#      トークンは 1 回きり・5 分）
#
# 🔴 バックアップの Job は root トークンを使わない。root を使うのはこの bootstrap（運用者の操作。vault Pod 内の
#    VAULT_DEV_ROOT_TOKEN_ID を kubectl exec の中で読む）だけで、値はこのスクリプトの引数・出力に現れない。
# 前提: 永続化した秘匿管理（deploy/local/vault-persistence）が unseal 済み（rollout status 済み）。
# 起動スクリプト（scripts/k8s-local-up.sh）は永続化の overlay を当てた後に毎回呼ぶ。手順は docs/operations/platform-infra-backup-runbook.md。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../../.." && pwd)"
INFRA_NS="${INFRA_NS:-platform-infra}"
BACKUP_VAULT_POLICY="platform-backup-raft-snapshot"
BACKUP_VAULT_ROLE="platform-backup-vault"
BACKUP_VAULT_SA="platform-backup-vault"
# 配備を 2 段に分ける口（VAULT_ROLE_AUDIENCE_DEFER=1 のときだけ role の audience を書き換えない。IADR-0585）。
# shellcheck source=scripts/lib/vault-role-audience.sh
. "$ROOT/scripts/lib/vault-role-audience.sh"

# vault Pod 内で root として vault コマンドを実行する（stdin を透過＝policy write に使う）。
vexec() { kubectl -n "$INFRA_NS" exec -i deploy/vault -- sh -c \
  'export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"; '"$1"; }

echo "==> platform-backup(vault): Vault の SA に TokenReview の権限（k8s auth の reviewer）"
kubectl apply -f "$ROOT/deploy/local/vault/eso/vault-auth-rbac.yaml"

echo "==> platform-backup(vault): kubernetes 認証（未有効時のみ enable・config は ESO の bootstrap と同じ値）"
vexec 'vault auth list -format=json 2>/dev/null | grep -q "\"kubernetes/\"" || vault auth enable kubernetes'
vexec 'vault write auth/kubernetes/config kubernetes_host=https://kubernetes.default.svc'

echo "==> platform-backup(vault): policy $BACKUP_VAULT_POLICY（sys/storage/raft/snapshot の read だけ）"
vexec "vault policy write $BACKUP_VAULT_POLICY -" < "$ROOT/deploy/local/platform-backup/vault/policy-raft-snapshot.hcl"

# NFR-18, IADR-0585（Refs GHSA-j4qv-2w99-xg5p）: role は audience=vault のトークンだけを受ける（CronJob の projected token が同じ値を持つ）。
echo "==> platform-backup(vault): role $BACKUP_VAULT_ROLE（SA $INFRA_NS/$BACKUP_VAULT_SA にだけ束縛・1 回きり・5 分）"
vexec "$(vault_role_write_cmd "vault write auth/kubernetes/role/$BACKUP_VAULT_ROLE \
  bound_service_account_names=$BACKUP_VAULT_SA bound_service_account_namespaces=$INFRA_NS \
  audience=vault token_policies=$BACKUP_VAULT_POLICY token_no_default_policy=true \
  token_ttl=5m token_max_ttl=10m token_num_uses=1") >/dev/null"
