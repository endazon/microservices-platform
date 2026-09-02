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
# NFR, ADR-0027 (#1022): ブローカのパスワード（app 側）。appsettings.json から接続文字列を撤去したため、
# これが無いと ESO=1 では RabbitMQ を使う 7 サービスが起動できない。★値は step 3 の基盤 secret
# `rabbitmq` と**同値**にすること（同じ env RABBITMQ_PASSWORD から作る。ズレると認証破壊）。
vexec "vault kv put secret/msp/rabbitmq-app password='${RABBITMQ_PASSWORD:-guest}'"
vexec "vault kv put secret/msp/wikijs-db password='${WIKIJS_DB_PASSWORD:-kp}'"
vexec "vault kv put secret/msp/wikijs-sync apiKey='${WIKIJS_SYNC_APIKEY:-}'"
# IADR-0098 (#310) PR-3: OIDC client secret 群（minio/grafana/vault/headlamp）。既定は各 <tool>-dev-secret-change-me
# （現行 apply_secret の env 既定と同値）。env で上書き可。realm import の dev client secret と一致させること。
vexec "vault kv put secret/msp/minio-oidc client-secret='${MINIO_OIDC_CLIENT_SECRET:-minio-dev-secret-change-me}'"
# NFR, SC-13, ADR-0032, IADR-0251/IADR-0273/IADR-0316 (#1107): BFF がコンフィデンシャルクライアントとして
# Keycloak と通信するための client secret。**空だと `GET /bff/auth/login` が 500 で落ちる**（PAR が 401）。
# 既定は realm の置き場と同値（一致しないと PAR が同じ 401 を返す）。env で上書き可。
vexec "vault kv put secret/msp/bff-oidc client-secret='${BFF_OIDC_CLIENT_SECRET:-bff-dev-secret-change-me}'"
# FR-05, FR-09, SC-17, IADR-0301/IADR-0329 (#1101): AuthorizationService が Keycloak Admin REST へ
# SC-17 の変更を反映するための client secret（realm の機密クライアント `identity-admin`）。
# **空だと authorization-service Pod が起動しない**（helm は非 optional な secretKeyRef で読む）。
# 既定は realm import の置き場と同値（ズレると client_credentials が 401 になり SC-17 が 500 になる）。
vexec "vault kv put secret/msp/identity-admin-oidc client-secret='${IDENTITY_ADMIN_CLIENT_SECRET:-identity-admin-dev-secret-change-me}'"
vexec "vault kv put secret/msp/grafana-oidc client-secret='${GRAFANA_OIDC_CLIENT_SECRET:-grafana-dev-secret-change-me}'"
vexec "vault kv put secret/msp/vault-oidc client-secret='${VAULT_OIDC_CLIENT_SECRET:-vault-dev-secret-change-me}'"
vexec "vault kv put secret/msp/headlamp-oidc client-secret='${HEADLAMP_OIDC_CLIENT_SECRET:-headlamp-dev-secret-change-me}'"
# IADR-0099 (#310) PR-4: 基盤 secret（postgres/rabbitmq/keycloak-admin）。★値は k8s-local-up.sh step 3 の手動 apply と
# **完全一致**させること（env 由来 or 同じ既定 postgres/guest/admin）。DB/broker/keycloak は既存パスワードで初期化済みのため、
# 値がズレると認証破壊。ExternalSecret は creationPolicy: Merge で同一値を上書きするのみ（値不変＝無害）。
vexec "vault kv put secret/msp/postgres password='${PG_PASSWORD:-postgres}'"
vexec "vault kv put secret/msp/rabbitmq username='${RABBITMQ_USER:-guest}' password='${RABBITMQ_PASSWORD:-guest}'"
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
echo "  #1107: bff-oidc (MSP ns。BFF セッションの client secret。空だと /bff/auth/login が 500)"
echo "  #1101: identity-admin-oidc (MSP ns。SC-17 の Keycloak Admin REST 反映。空だと authorization-service が起動しない)"
echo "  PR-4: postgres, rabbitmq, keycloak-admin (platform-infra ns・creationPolicy: Merge・手動 apply は保持)"
echo "  #438/#1102: keycloak-smtp (platform-infra ns。既定は空＝実値未供給。k8s-local-up.sh の ESO=1 が常時 apply する。docs/operations/keycloak-smtp-relay-setup-runbook.md 参照)"
# 🔴 案内は **実際に apply される名前だけ**を挙げる（#1102: 挙げた名前が作られないと、手順どおり
#    打った人が必ず NotFound を踏む）。grafana-oidc / headlamp-oidc は OBSERVABILITY=1 / HEADLAMP=1 の
#    ときだけ apply されるため、無条件の並びからは外して注記に回す。
echo "  確認(MSP): kubectl -n microservices-platform get externalsecret,secret llm-provider-credentials minio-credentials postgres-app rabbitmq-app wikijs-db wikijs-sync minio-oidc bff-oidc identity-admin-oidc"
echo "  確認(infra): kubectl -n platform-infra get externalsecret,secret postgres rabbitmq keycloak-admin vault-oidc keycloak-smtp"
echo "             （grafana-oidc は OBSERVABILITY=1、headlamp-oidc は HEADLAMP=1 のときだけ apply される）"
