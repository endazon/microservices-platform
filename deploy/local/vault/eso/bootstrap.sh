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

echo "==> seed: secret/msp/*（env 由来 or dev 既定・平文の実 secret は非コミット）"
# 値は現行 apply_secret の既定と同一（minioadmin/kp/空）。env で上書き可。
vexec "vault kv put secret/msp/llm-provider-credentials anthropic-api-key='${ANTHROPIC_API_KEY:-}' openai-api-key='${OPENAI_API_KEY:-}'"
# IADR-0097 (#310) PR-2: minio-credentials / wikijs-db / wikijs-sync。
vexec "vault kv put secret/msp/minio-credentials accessKey='${MINIO_ACCESS_KEY:-minioadmin}' secretKey='${MINIO_SECRET_KEY:-minioadmin}'"
# NFR, ADR-0002 (#1012): サービス DB のパスワード。appsettings.json から接続文字列を撤去したため、
# これが無いと ESO=1 では DB を持つ全サービスが起動できない。dev 既定は init スクリプトが作る `kp`。
vexec "vault kv put secret/msp/postgres-app password='${APP_DB_PASSWORD:-kp}'"
vexec "vault kv put secret/msp/wikijs-db password='${WIKIJS_DB_PASSWORD:-kp}'"
vexec "vault kv put secret/msp/wikijs-sync apiKey='${WIKIJS_SYNC_APIKEY:-}'"
# IADR-0098 (#310) PR-3: OIDC client secret 群（minio/grafana/vault/headlamp）。既定は各 <tool>-dev-secret-change-me
# （現行 apply_secret の env 既定と同値）。env で上書き可。realm import の dev client secret と一致させること。
vexec "vault kv put secret/msp/minio-oidc client-secret='${MINIO_OIDC_CLIENT_SECRET:-minio-dev-secret-change-me}'"
vexec "vault kv put secret/msp/grafana-oidc client-secret='${GRAFANA_OIDC_CLIENT_SECRET:-grafana-dev-secret-change-me}'"
vexec "vault kv put secret/msp/vault-oidc client-secret='${VAULT_OIDC_CLIENT_SECRET:-vault-dev-secret-change-me}'"
vexec "vault kv put secret/msp/headlamp-oidc client-secret='${HEADLAMP_OIDC_CLIENT_SECRET:-headlamp-dev-secret-change-me}'"
# IADR-0099 (#310) PR-4: 基盤 secret（postgres/rabbitmq/keycloak-admin）。★値は k8s-local-up.sh step 3 の手動 apply と
# **完全一致**させること（env 由来 or 同じ既定 postgres/guest/admin）。DB/broker/keycloak は既存パスワードで初期化済みのため、
# 値がズレると認証破壊。ExternalSecret は creationPolicy: Merge で同一値を上書きするのみ（値不変＝無害）。
vexec "vault kv put secret/msp/postgres password='${PG_PASSWORD:-postgres}'"
vexec "vault kv put secret/msp/rabbitmq password='${RABBITMQ_PASSWORD:-guest}'"
vexec "vault kv put secret/msp/keycloak-admin password='${KEYCLOAK_ADMIN_PASSWORD:-admin}'"
# #438, ADR-0045 決定 2-b/6: SMTP リレー（go-live では Google Workspace への STARTTLS リレー）の資格情報。
# **実環境の値は未供給のため既定は空文字**（他 secret と同じ fail-safe。空のままでは Keycloak の smtpServer は
# 機能しない＝現状と不変）。値の投入手順・Secret の消費方法は
# docs/operations/keycloak-smtp-relay-setup-runbook.md を参照。host/port/starttls は ADR-0045 決定 2-b の
# 確定値（smtp.gmail.com / 587 / true）を既定に置く——これらは接続先の書式であり秘匿値ではない。
vexec "vault kv put secret/msp/keycloak-smtp \
  host='${SMTP_HOST:-smtp.gmail.com}' port='${SMTP_PORT:-587}' starttls='${SMTP_STARTTLS:-true}' \
  from='${SMTP_FROM:-}' user='${SMTP_USER:-}' password='${SMTP_PASSWORD:-}'"

echo ""
echo "done. ExternalSecret が Vault→k8s Secret を同期する（refresh 1h）:"
echo "  PR-1: llm-provider-credentials / PR-2: minio-credentials, wikijs-db, wikijs-sync"
echo "  PR-3: minio-oidc (MSP ns) / grafana-oidc, vault-oidc, headlamp-oidc (platform-infra ns)"
echo "  PR-4: postgres, rabbitmq, keycloak-admin (platform-infra ns・creationPolicy: Merge・手動 apply は保持)"
echo "  #438: keycloak-smtp (platform-infra ns。既定は空＝実値未供給。docs/operations/keycloak-smtp-relay-setup-runbook.md 参照)"
echo "  確認(MSP): kubectl -n microservices-platform get externalsecret,secret llm-provider-credentials minio-credentials wikijs-db wikijs-sync minio-oidc"
echo "  確認(infra): kubectl -n platform-infra get externalsecret,secret postgres rabbitmq keycloak-admin vault-oidc grafana-oidc headlamp-oidc keycloak-smtp"
