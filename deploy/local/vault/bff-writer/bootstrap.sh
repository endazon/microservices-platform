#!/usr/bin/env bash
# SC-22, NFR-18, ADR-0095, IADR-0658 (#2230): **BFF の書き込みの役だけ**を Vault（OpenBao）へ入れる bootstrap（再実行可・冪等）。
#
#   VAULT=1 VAULT_BFF_WRITER=1 bash scripts/k8s-local-up.sh --live   # 起動器が VAULT ブロックの後で呼ぶ
#
# 入れるもの: kubernetes 認証の有効化と設定、policy `bff-secret-write` / `bff-secret-group-write`、role `bff-secret-writer`。
# 🔴 **ESO の bootstrap（deploy/local/vault/eso/bootstrap.sh）の部分集合である。** policy の中身は同じ HCL ファイルを読み、
#    role の字面は同じ（両者の一致は scripts/k8s-local-up.test.js が固定する）。ESO の role・KV の種・ExternalSecret は入れない ——
#    Secret の供給は従来の手動の作成のまま変わらない（使い捨ての CI で SC-22 の一覧〔GET /bff/secrets〕を 200 にするためだけの口）。
# 🔴 role の audience（vault）は名乗る側（chart の BFF の projected token）を当てた後に書く（IADR-0585。起動器は [6/7] の後に呼ぶ）。
# 前提: deploy/vault が起動済み（platform-infra）。root トークンは vault Pod の env VAULT_DEV_ROOT_TOKEN_ID。全操作は Pod 内で行う。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../../.." && pwd)"
INFRA_NS="platform-infra"

# vault Pod 内で root として vault コマンドを実行するヘルパ（stdin を透過＝policy write に使う）。ESO の bootstrap と同じ形。
vexec() { kubectl -n "$INFRA_NS" exec -i deploy/vault -- sh -c \
  'export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"; '"$1"; }

# shellcheck source=scripts/lib/vault-role-audience.sh
. "$ROOT/scripts/lib/vault-role-audience.sh"

echo "==> kubernetes 認証を有効化（未有効時のみ・冪等）"
vexec 'vault auth list -format=json 2>/dev/null | grep -q "\"kubernetes/\"" || vault auth enable kubernetes'

echo "==> auth/kubernetes/config（in-cluster local mode: Vault 自身の SA を reviewer に使う）"
vexec 'vault write auth/kubernetes/config kubernetes_host=https://kubernetes.default.svc'

echo "==> policy: bff-secret-write（SC-22 の項目だけ・完全一致パス・data の read なし）"
vexec 'vault policy write bff-secret-write -' < "$ROOT/deploy/local/vault/eso/policy-bff-secret-write.hcl"

echo "==> policy: bff-secret-group-write（SC-22 の群だけ・専用接頭辞の 1 セグメント・data の read なし）"
vexec 'vault policy write bff-secret-group-write -' < "$ROOT/deploy/local/vault/eso/policy-bff-secret-group-write.hcl"

echo "==> role: bff-secret-writer（BFF 専用 SA microservices-platform/bff に束縛）"
vexec "$(vault_role_write_cmd 'vault write auth/kubernetes/role/bff-secret-writer bound_service_account_names=bff bound_service_account_namespaces=microservices-platform policies=bff-secret-write,bff-secret-group-write audience=vault ttl=1h')"

echo "done. BFF の書き込みの役だけを入れた（ESO・KV の種・ExternalSecret は入れていない。Secret の供給は手動の作成のまま）。"
