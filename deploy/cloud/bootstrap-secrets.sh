#!/usr/bin/env bash
# NFR-18, IADR-0674 (#2401): クラウドの最小構成の Secret を、**初回に乱数で作る**（利用者が SSH のトンネル越しに実行する）。
#
#   bash deploy/cloud/bootstrap-secrets.sh --live
#   ANTHROPIC_API_KEY_FILE=<鍵を 1 行で書いた 0600 のファイル> bash deploy/cloud/bootstrap-secrets.sh --live   # LLM の鍵も入れる
#
# 作るもの（**在れば触らない**。作り直しは scripts/rotate-infra-credentials.sh か手順書。リポジトリの既定値は 1 つも使わない）:
#   platform-infra:
#     postgres（管理者）・ast-db（取引ユニットの DB の利用者）・keycloak-db（Keycloak の DB の利用者）・keycloak-admin（管理者）・
#     rabbitmq（ブローカの既定のユーザー。サービスごとのユーザーが揃えば起動のたびに消える）・rabbitmq-users（取引ユニットの
#     サービスごとのユーザー。deploy/local/rabbitmq-setup/users.txt の取引ユニットの行）・keycloak-client-secrets（取引ユニットの
#     5 つの機密クライアント）・keycloak-realm-import（deploy/cloud/realm-for-cloud.js が宣言から組む）・
#     postgres-tls / rabbitmq-tls（この場で作る CA が発行するサーバ証明書）・ConfigMap platform-infra-ca（CA の証明書）
#   microservices-platform:
#     llm-provider-credentials（ANTHROPIC_API_KEY_FILE を与えたときの鍵。与えなければ空で作り、LLM ゲートウェイは起動しない）・
#     llmgateway-tls（同じ CA が発行する LLM ゲートウェイの受け口の証明書）・ConfigMap platform-infra-ca（同じ CA の証明書）
#
# 🔴 値は表示しない。値は 0700 の一時ディレクトリのファイル（終了時に消す）からだけ kubectl へ渡し、どのプロセスの引数にも載せない。
# 🔴 CA の鍵は保存しない（証明書を発行したら捨てる）。サーバ証明書を作り直すときは CA ごと作り直す（README §証明書）。
# 取引ユニットの名前空間へ写すもの（DB・ブローカ・クライアントの secret・CA）は、取引ユニットの側が読む（AST#1525）。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# shellcheck source=scripts/lib/live-opt-in.sh
. "$ROOT/scripts/lib/live-opt-in.sh"
live_opt_in_scan "$@"
set -- "${LIVE_REST[@]+"${LIVE_REST[@]}"}"
live_opt_in_require "bootstrap-secrets.sh"
if [ "$#" -gt 0 ]; then
  echo "ERROR: 未知の引数: $*（受け付けるのは --live だけ）" >&2
  exit 2
fi

INFRA_NS="${INFRA_NS:-platform-infra}"
MSP_NS="${MSP_NS:-microservices-platform}"
USERS_FILE="${RABBITMQ_USERS_FILE:-$ROOT/deploy/local/rabbitmq-setup/users.txt}"
REALM_FILE="${REALM_FILE:-$ROOT/deploy/keycloak/microservices-platform-realm.json}"
CERT_DAYS="${CERT_DAYS:-825}"

TMPD="$(mktemp -d)"
chmod 700 "$TMPD"
trap 'rm -rf "$TMPD"' EXIT

rand() { od -An -N24 -tx1 /dev/urandom | tr -d ' \n'; }
rand_name() { printf '%s_%s' "$1" "$(od -An -N3 -tx1 /dev/urandom | tr -d ' \n')"; }
exists() { kubectl -n "$1" get "$2" "$3" -o name >/dev/null 2>&1; }

# put <ns> <Secret> <キー>=<値のファイル>... —— ファイルから Secret を作る（値は引数に載らない。パスだけが載る）
put() {
  local ns="$1" name="$2" args=() kv
  shift 2
  for kv in "$@"; do args+=("--from-file=$kv"); done
  kubectl -n "$ns" create secret generic "$name" "${args[@]}" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
  echo "    作った: Secret ${ns}/${name}（キー: $(printf '%s ' "$@" | sed -E 's/=[^ ]*//g')）"
}

# val <名前> <値> → 値を 0600 のファイルへ書き、パスを返す
val() {
  local f="$TMPD/$1"
  ( umask 077; printf '%s' "$2" > "$f" )
  printf '%s' "$f"
}

# once <ns> <Secret> <作る関数> —— 在れば触らない（作り直しで食い違わせない）
once() {
  if exists "$1" secret "$2"; then
    echo "    在る: Secret $1/$2（触らない）"
    return 0
  fi
  "$3"
}

for ns in "$INFRA_NS" "$MSP_NS"; do
  kubectl get namespace "$ns" -o name >/dev/null 2>&1 || { echo "ERROR: 名前空間 ${ns} が無い。先に kubectl apply -f deploy/cloud/infra/namespaces.yaml を当てる（README §3）" >&2; exit 1; }
done

echo "==> 1. データストア・認可サーバ・ブローカの資格情報（乱数）"
mk_postgres() { put "$INFRA_NS" postgres "username=$(val pg-user "$(rand_name pgadmin)")" "password=$(val pg-pass "$(rand)")"; }
mk_ast_db() { put "$INFRA_NS" ast-db "username=$(val ast-user "$(rand_name ast)")" "password=$(val ast-pass "$(rand)")"; }
mk_keycloak_db() { put "$INFRA_NS" keycloak-db "username=$(val kc-db-user "$(rand_name keycloak)")" "password=$(val kc-db-pass "$(rand)")"; }
mk_keycloak_admin() { put "$INFRA_NS" keycloak-admin "username=$(val kc-admin-user "$(rand_name kcadmin)")" "password=$(val kc-admin-pass "$(rand)")"; }
mk_rabbitmq() { put "$INFRA_NS" rabbitmq "username=$(val mq-user "$(rand_name mqadmin)")" "password=$(val mq-pass "$(rand)")"; }
mk_rabbitmq_users() {
  local u args=()
  # 取引ユニットの行だけ（MSP のサービスはクラウドでブローカを使わない）。名前空間の列が ai-stock-trading のもの。
  while read -r u; do
    args+=("${u}=$(val "mq-${u}" "$(rand)")")
  done < <(grep -v '^[[:space:]]*#' "$USERS_FILE" | awk 'NF >= 2 && $2 == "ai-stock-trading" { print $1 }')
  [ "${#args[@]}" -gt 0 ] || { echo "ERROR: ${USERS_FILE} に取引ユニットの行が無い" >&2; exit 1; }
  put "$INFRA_NS" rabbitmq-users "${args[@]}"
}
once "$INFRA_NS" postgres mk_postgres
once "$INFRA_NS" ast-db mk_ast_db
once "$INFRA_NS" keycloak-db mk_keycloak_db
once "$INFRA_NS" keycloak-admin mk_keycloak_admin
once "$INFRA_NS" rabbitmq mk_rabbitmq
once "$INFRA_NS" rabbitmq-users mk_rabbitmq_users

echo "==> 2. 取引ユニットの機密クライアントの secret と、取り込む realm（宣言の開発用の値を持ち込まない）"
CLIENTS="$(node -e 'process.stdout.write(require(process.argv[1]).CLOUD_CLIENTS.join(" "))' "$ROOT/deploy/cloud/realm-for-cloud.js")"
mk_client_secrets() {
  local c args=()
  for c in $CLIENTS; do args+=("${c}=$(val "cs-${c}" "$(rand)")"); done
  put "$INFRA_NS" keycloak-client-secrets "${args[@]}"
}
once "$INFRA_NS" keycloak-client-secrets mk_client_secrets
mk_realm_import() {
  # クライアントの secret は Secret keycloak-client-secrets から読む（作ったばかりでも、在ったものでも同じ値を使う）。
  node -e '
    const fs = require("fs");
    const [b64Json, out] = process.argv.slice(1);
    const data = JSON.parse(fs.readFileSync(b64Json, "utf8")).data || {};
    const secrets = Object.fromEntries(Object.entries(data).map(([k, v]) => [k, Buffer.from(v, "base64").toString("utf8")]));
    fs.writeFileSync(out, JSON.stringify(secrets), { mode: 0o600 });
  ' <(kubectl -n "$INFRA_NS" get secret keycloak-client-secrets -o json) "$TMPD/client-secrets.json"
  ( umask 077; node "$ROOT/deploy/cloud/realm-for-cloud.js" "$REALM_FILE" "$TMPD/client-secrets.json" > "$TMPD/platform-realm.json" )
  put "$INFRA_NS" keycloak-realm-import "platform-realm.json=$TMPD/platform-realm.json"
}
once "$INFRA_NS" keycloak-realm-import mk_realm_import

echo "==> 3. データストア・ブローカのサーバ証明書（この場で作る CA。CA の鍵は保存しない）"
# 組（3 つのサーバ証明書と、2 つの名前空間の CA）は同じ CA で揃っていなければならない。一部だけ在るときに作り直すと、在る側（使用中の証明書）と
# 新しい CA が食い違う。作り直さずに止め、手順書の証明書の項（組を消してから流す）へ回す。
tls_have=0
for o in "$INFRA_NS secret postgres-tls" "$INFRA_NS secret rabbitmq-tls" "$MSP_NS secret llmgateway-tls" "$INFRA_NS configmap platform-infra-ca" "$MSP_NS configmap platform-infra-ca"; do
  # shellcheck disable=SC2086
  exists $o && tls_have=$((tls_have + 1))
done
if [ "$tls_have" -eq 5 ]; then
  echo "    在る: Secret postgres-tls / rabbitmq-tls / ${MSP_NS}/llmgateway-tls・ConfigMap platform-infra-ca（両方の名前空間。触らない）"
elif [ "$tls_have" -gt 0 ]; then
  echo "ERROR: 証明書の組（Secret postgres-tls / rabbitmq-tls / ${MSP_NS}/llmgateway-tls・ConfigMap platform-infra-ca（両方の名前空間））が一部だけ在る（${tls_have}/5）。作り直さずに止める（手順書の証明書の項: 組を消してから流す）" >&2
  exit 1
else
  ( umask 077
    openssl req -x509 -newkey rsa:3072 -nodes -days "$CERT_DAYS" -subj "/CN=platform-infra-cloud-ca" \
      -keyout "$TMPD/ca.key" -out "$TMPD/ca.crt" >/dev/null 2>&1
    # LLM ゲートウェイの受け口（取引ユニットから https。メッシュが無いのでアプリで TLS にする）も同じ CA で発行する。
    for pair in "postgres:$INFRA_NS" "rabbitmq:$INFRA_NS" "llmgateway-service:$MSP_NS"; do
      svc="${pair%%:*}"; ns="${pair#*:}"
      openssl req -newkey rsa:3072 -nodes -subj "/CN=${svc}" -keyout "$TMPD/${svc}.key" -out "$TMPD/${svc}.csr" >/dev/null 2>&1
      printf 'subjectAltName=DNS:%s,DNS:%s.%s,DNS:%s.%s.svc,DNS:%s.%s.svc.cluster.local\nextendedKeyUsage=serverAuth\n' \
        "$svc" "$svc" "$ns" "$svc" "$ns" "$svc" "$ns" > "$TMPD/${svc}.ext"
      openssl x509 -req -in "$TMPD/${svc}.csr" -CA "$TMPD/ca.crt" -CAkey "$TMPD/ca.key" -CAcreateserial -days "$CERT_DAYS" \
        -extfile "$TMPD/${svc}.ext" -out "$TMPD/${svc}.crt" >/dev/null 2>&1
    done
    rm -f "$TMPD/ca.key" )
  [ -s "$TMPD/ca.crt" ] && [ -s "$TMPD/postgres.crt" ] && [ -s "$TMPD/rabbitmq.crt" ] && [ -s "$TMPD/llmgateway-service.crt" ] || { echo "ERROR: 証明書を作れない（openssl を確かめる）" >&2; exit 1; }
  put "$INFRA_NS" postgres-tls "tls.crt=$TMPD/postgres.crt" "tls.key=$TMPD/postgres.key"
  put "$INFRA_NS" rabbitmq-tls "ca.crt=$TMPD/ca.crt" "tls.crt=$TMPD/rabbitmq.crt" "tls.key=$TMPD/rabbitmq.key"
  put "$MSP_NS" llmgateway-tls "tls.crt=$TMPD/llmgateway-service.crt" "tls.key=$TMPD/llmgateway-service.key"
  for ns in "$INFRA_NS" "$MSP_NS"; do
    kubectl -n "$ns" create configmap platform-infra-ca --from-file=ca.crt="$TMPD/ca.crt" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
    echo "    作った: ConfigMap ${ns}/platform-infra-ca（公開してよい CA の証明書）"
  done
fi

echo "==> 4. LLM ゲートウェイの提供元の鍵"
mk_llm() {
  local key=""
  if [ -n "${ANTHROPIC_API_KEY_FILE:-}" ]; then
    [ -r "$ANTHROPIC_API_KEY_FILE" ] || { echo "ERROR: ANTHROPIC_API_KEY_FILE を読めない" >&2; exit 1; }
    key="$(tr -d '\r\n' < "$ANTHROPIC_API_KEY_FILE")"
  fi
  put "$MSP_NS" llm-provider-credentials "anthropic-api-key=$(val llm-key "$key")" "openai-api-key=$(val openai-key "")"
  [ -n "$key" ] || echo "    注: 鍵が空なので LLM ゲートウェイは起動しない。ANTHROPIC_API_KEY_FILE を与えて Secret を作り直す（README §LLM の鍵）"
}
once "$MSP_NS" llm-provider-credentials mk_llm

echo "OK: クラウドの最小構成の Secret を確かめた（値は表示しない）。次は README の手順の続き（kubectl apply -k deploy/cloud/infra・helm）"
