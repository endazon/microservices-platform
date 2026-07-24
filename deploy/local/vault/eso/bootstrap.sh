#!/usr/bin/env bash
# IADR-0096 (#310): Vault の kubernetes 認証を有効化・設定し、ESO 用の policy/role と MSP secret の seed を入れる
# runtime bootstrap（再実行可・[[IADR-0094]] と同型）。dev Vault はインメモリ（Recreate）＝Pod 再起動後は再実行する。
#
#   [ANTHROPIC_API_KEY=... OPENAI_API_KEY=...] bash deploy/local/vault/eso/bootstrap.sh
#
# 前提: VAULT=1 で dev Vault が起動済み（platform-infra）。root トークンは vault Pod の env VAULT_DEV_ROOT_TOKEN_ID。
# 全 vault 操作は vault Pod 内で実行する（kubectl exec）。ホストに vault CLI は不要。
# fail-safe: seed 値は env 由来 or 空既定（空＝外部 LLM を呼ばない現行 fail-safe と同値）。平文はリポジトリに置かない。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../../.." && pwd)"
INFRA_NS="platform-infra"

# vault Pod 内で root として vault コマンドを実行するヘルパ（stdin を透過＝policy write に使う）。
vexec() { kubectl -n "$INFRA_NS" exec -i deploy/vault -- sh -c \
  'export VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN="$VAULT_DEV_ROOT_TOKEN_ID"; '"$1"; }

echo "==> kubernetes 認証を有効化（未有効時のみ・冪等）"
vexec 'vault auth list -format=json 2>/dev/null | grep -q "\"kubernetes/\"" || vault auth enable kubernetes'

echo "==> auth/kubernetes/config（in-cluster local mode: Vault 自身の SA を reviewer に使う）"
vexec 'vault write auth/kubernetes/config kubernetes_host=https://kubernetes.default.svc'

echo "==> policy: eso-read（MSP＋AST の read・store は両者共有のため両 path を許可）"
vexec 'vault policy write eso-read -' < "$ROOT/deploy/local/vault/eso/policy-eso-read.hcl"

echo "==> role: eso（ESO の SA external-secrets/external-secrets に束縛）"
vexec 'vault write auth/kubernetes/role/eso bound_service_account_names=external-secrets bound_service_account_namespaces=external-secrets policies=eso-read ttl=1h'

echo "==> seed: secret/msp/llm-provider-credentials（env 由来 or 空既定・平文非コミット）"
vexec "vault kv put secret/msp/llm-provider-credentials anthropic-api-key='${ANTHROPIC_API_KEY:-}' openai-api-key='${OPENAI_API_KEY:-}'"

echo ""
echo "done. ExternalSecret(llm-provider-credentials) が Vault→k8s Secret を同期する（refresh 1h）。"
echo "  確認: kubectl -n microservices-platform get externalsecret,secret llm-provider-credentials"
