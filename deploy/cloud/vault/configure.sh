#!/usr/bin/env bash
# NFR-05, NFR-18, IADR-0689 決定 2・決定 4・決定 6: 初期化の直後に一度だけ、初回の root で OpenBao を設定し、**最後に root を失効させる**。
#
#   bash deploy/cloud/vault/configure.sh --live --init-file <init.sh のファイル>.age --identity <age の識別子> \
#     --recipients <age の受取人のファイル> --operator-out <書き出すファイル>.age
#
# 設定するもの（どれも在れば作り直さない）:
#   - KV v2 `cloud/`（値の置き場。パスの規則は IADR-0689 決定 6）
#   - PKI `pki_cloud/`（基盤との通信の CA。**CA の鍵は OpenBao の外へ出さない**〔internal〕）と、サーバ証明書の role
#     （postgres・rabbitmq・llmgateway-service。SAN は名指しの 4 つだけ・発行した証明書を保存しない）
#   - policy（deploy/cloud/vault/policies/*.hcl）と kubernetes 認証の role（ESO の 3 つの ServiceAccount）
#   - オーナー用の AppRole `cloud-operator`（ループバックからだけ入れる。トークンは 15 分）
# オーナー用の資格（role_id・secret_id）は Pod の中から手元の `age -R` へだけ流し、--operator-out に暗号化して書く。
# 🔴 初回の root を失効させるのは、資格を書き出して復号で確かめた**後**だけ。途中で落ちたら root は残し、そのまま流し直せる
#    （済んだものは作り直さない。資格は流し直しのたびに 1 組へ作り直す）。
# CA の証明書（公開してよい）は ConfigMap platform-infra-ca（platform-infra・microservices-platform）と cloud/shared/platform-infra-ca に置く。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
# shellcheck source=scripts/lib/live-opt-in.sh
. "$ROOT/scripts/lib/live-opt-in.sh"
# shellcheck source=deploy/cloud/vault/lib.sh
. "$HERE/lib.sh"
live_opt_in_scan "$@"
set -- "${LIVE_REST[@]+"${LIVE_REST[@]}"}"

INIT_FILE="" IDENTITY="" RECIPIENTS="" OUT=""
K8S_HOST="${OPENBAO_KUBERNETES_HOST:-https://kubernetes.default.svc}"
CA_NAMESPACES="${CA_NAMESPACES:-platform-infra microservices-platform}"
while [ "$#" -gt 0 ]; do
  case "$1" in
    --init-file) INIT_FILE="${2:-}"; shift 2 ;;
    --identity) IDENTITY="${2:-}"; shift 2 ;;
    --recipients) RECIPIENTS="${2:-}"; shift 2 ;;
    --operator-out) OUT="${2:-}"; shift 2 ;;
    *) echo "ERROR: 未知の引数: $1（受け付けるのは --live・--init-file・--identity・--recipients・--operator-out）" >&2; exit 2 ;;
  esac
done
ob_readable "$INIT_FILE" "init.sh が書いたファイル（--init-file）"
ob_readable "$IDENTITY" "age の識別子のファイル（--identity）"
ob_readable "$RECIPIENTS" "age の受取人のファイル（--recipients）"
ob_fresh "$OUT"
case "$K8S_HOST" in https://*) ;; *) echo "ERROR: OPENBAO_KUBERNETES_HOST は https:// で始める" >&2; exit 2 ;; esac
live_opt_in_require "configure.sh"
ob_need kubectl age node
printf 'probe' | age -R "$RECIPIENTS" >/dev/null || { echo "ERROR: 受取人のファイルで暗号化できない" >&2; exit 2; }

# サーバ証明書の role と、許す名前（短い名前・名前空間つき・.svc・.svc.cluster.local。従来の SAN と同じ）
pki_role() {
  local svc="$1" ns="$2"
  printf 'b write pki_cloud/roles/%s allowed_domains="%s,%s.%s,%s.%s.svc,%s.%s.svc.cluster.local" ' "$svc" "$svc" "$svc" "$ns" "$svc" "$ns" "$svc" "$ns"
  printf 'allow_bare_domains=true allow_subdomains=false allow_glob_domains=false allow_wildcard_certificates=false allow_localhost=false allow_ip_sans=false '
  printf 'server_flag=true client_flag=false key_type=rsa key_bits=3072 max_ttl=8760h no_store=true >/dev/null\n'
}
policy_write() {
  local name="$1"
  printf "b policy write %s - >/dev/null <<'__HCL__'\n" "$name"
  cat "$HERE/policies/${name}.hcl"
  printf '__HCL__\n'
}

# Pod の中の手順（値を持たない）。1 行目に初回の root を受ける。標準出力（fd 3）にはオーナー用の資格の JSON だけを出す。
# 🔴 ここでは root を失効させない。途中で落ちても root は残り、configure をそのまま流し直せる（済んだものは作り直さない）。
#    失効させるのは、オーナー用の資格を書き出して確かめ終えた後だけ（下の「失効」の段）。
# shellcheck disable=SC2016
POD="$(cat <<'EOF'
set -eu
umask 077
exec 3>&1 1>&2
read -r T
[ -n "$T" ] || { echo "ERROR: root を受け取れない" >&2; exit 1; }
b() { BAO_TOKEN="$T" bao "$@"; }
b token lookup >/dev/null 2>&1 || { echo "ERROR: 初回の root で入れない（設定を終えて失効させた後なら、root が要る操作は root-token.sh）" >&2; exit 1; }
mounts="$(b secrets list -format=json)"
case "$mounts" in *'"cloud/"'*) echo "    在る: KV cloud/" ;; *) b secrets enable -path=cloud -version=2 kv >/dev/null; echo "    作った: KV cloud/" ;; esac
case "$mounts" in *'"pki_cloud/"'*) echo "    在る: PKI pki_cloud/" ;; *) b secrets enable -path=pki_cloud pki >/dev/null; echo "    作った: PKI pki_cloud/" ;; esac
b secrets tune -max-lease-ttl=87600h pki_cloud >/dev/null
if b list pki_cloud/issuers >/dev/null 2>&1; then echo "    在る: CA（pki_cloud）"; else
  b write pki_cloud/root/generate/internal common_name=platform-infra-cloud-ca ttl=87600h key_type=rsa key_bits=3072 >/dev/null
  echo "    作った: CA（pki_cloud。CA の鍵は OpenBao の中だけ）"
fi
__PKI_ROLES__
__POLICIES__
auths="$(b auth list -format=json)"
case "$auths" in *'"kubernetes/"'*) ;; *) b auth enable kubernetes >/dev/null ;; esac
if [ -r /var/run/secrets/kubernetes.io/serviceaccount/ca.crt ]; then
  # クラスタの中（既定）: OpenBao 自身の ServiceAccount のトークンと CA で TokenReview を呼ぶ。
  b write auth/kubernetes/config kubernetes_host="__K8S_HOST__" >/dev/null
else
  # クラスタの外（試験）: 与えた CA（公開してよい）で呼ぶ。
  cat > /tmp/k8s-ca.crt <<'__CA__'
__K8S_CA__
__CA__
  b write auth/kubernetes/config kubernetes_host="__K8S_HOST__" kubernetes_ca_cert=@/tmp/k8s-ca.crt disable_local_ca_jwt=true >/dev/null
fi
b write auth/kubernetes/role/platform-infra-eso bound_service_account_names=eso-openbao bound_service_account_namespaces=platform-infra token_policies=platform-infra-eso audience=vault token_ttl=15m token_max_ttl=1h >/dev/null
b write auth/kubernetes/role/msp-eso bound_service_account_names=eso-openbao bound_service_account_namespaces=microservices-platform token_policies=msp-eso audience=vault token_ttl=15m token_max_ttl=1h >/dev/null
b write auth/kubernetes/role/ast-eso bound_service_account_names=ast-eso-reader bound_service_account_namespaces=ai-stock-trading token_policies=ast-eso audience=vault token_ttl=15m token_max_ttl=1h >/dev/null
echo "    設定した: kubernetes 認証の role（platform-infra-eso・msp-eso・ast-eso）と policy"
ca="$(b read -field=certificate pki_cloud/cert/ca)"
printf '%s' "$ca" | b kv put -mount=cloud shared/platform-infra-ca ca.crt=- >/dev/null
echo "    書いた: cloud/shared/platform-infra-ca（公開してよい CA の証明書）"
case "$auths" in *'"approle/"'*) ;; *) b auth enable approle >/dev/null ;; esac
b write auth/approle/role/cloud-operator token_policies=cloud-operator token_ttl=15m token_max_ttl=1h \
  secret_id_ttl=0 secret_id_num_uses=0 bind_secret_id=true \
  token_bound_cidrs=127.0.0.1/32 secret_id_bound_cidrs=127.0.0.1/32 >/dev/null
# 前の回が資格を書き出す前に落ちたなら、その回の secret_id は誰も持たない。全部消してから 1 つだけ作る（流し直しても資格は 1 組）。
# 一覧は JSON の配列（無ければ「{}」）。accessor の形のものだけを拾う。
for a in $(b list -format=json auth/approle/role/cloud-operator/secret-id 2>/dev/null | tr -d "[]\", " | grep -E "^[0-9a-f-]{36}$" || true); do
  printf '{"secret_id_accessor":"%s"}' "$a" | b write auth/approle/role/cloud-operator/secret-id-accessor/destroy - >/dev/null
done
rid="$(b read -field=role_id auth/approle/role/cloud-operator/role-id)"
sid="$(b write -f -field=secret_id auth/approle/role/cloud-operator/secret-id)"
printf '{"role_id":"%s","secret_id":"%s"}\n' "$rid" "$sid" >&3
unset rid sid
echo "    作った: オーナー用の資格（AppRole cloud-operator。ループバックからだけ）"
EOF
)"
# bash 5.2 の置換は既定で & を一致した字面に読み替える（patsub_replacement）。差し込む手順を字面のまま入れる。
shopt -u patsub_replacement 2>/dev/null || true
roles="$(pki_role postgres platform-infra; pki_role rabbitmq platform-infra; pki_role llmgateway-service microservices-platform)"
policies="$(for p in platform-infra-eso msp-eso ast-eso cloud-operator; do policy_write "$p"; done)"
POD="${POD/__PKI_ROLES__/$roles}"
POD="${POD/__POLICIES__/$policies}"
POD="${POD//__K8S_HOST__/$K8S_HOST}"
# 試験だけが与える（クラスタの中では Pod の ServiceAccount の CA を使う）。公開してよい CA の証明書。
K8S_CA="$( [ -n "${OPENBAO_KUBERNETES_CA_FILE:-}" ] && cat "$OPENBAO_KUBERNETES_CA_FILE" || true)"
POD="${POD/__K8S_CA__/$K8S_CA}"

echo "==> 設定（初回の root を使う。失効は資格を書き出して確かめた後）"
if ! ( umask 077
       ob_decrypt_lines "$INIT_FILE" "$IDENTITY" root_token | ob_exec "$POD" | age -R "$RECIPIENTS" -o "$OUT" ); then
  rm -f "$OUT"
  echo "ERROR: 設定に失敗した（書き出し先は消した）。初回の root は残してある。理由を直して configure.sh をそのまま流し直す（済んだものは作り直さない）" >&2
  exit 1
fi
chmod 600 "$OUT"
# 書き出した資格を復号して確かめる（中身は表示しない）。確かめられなければ root を残して止まる。
if ! ob_decrypt_lines "$OUT" "$IDENTITY" role_id secret_id >/dev/null; then
  rm -f "$OUT"
  echo "ERROR: 書き出した資格を復号できない（書き出し先は消した）。初回の root は残してある。configure.sh を流し直す" >&2
  exit 1
fi

echo "==> CA の証明書（公開してよい）を ConfigMap platform-infra-ca に置く"
for ns in $CA_NAMESPACES; do
  ob_exec 'bao read -field=certificate pki_cloud/cert/ca' \
    | kubectl -n "$ns" create configmap platform-infra-ca --from-file=ca.crt=/dev/stdin --dry-run=client -o yaml | kubectl apply -f - >/dev/null
  echo "    作った: ConfigMap ${ns}/platform-infra-ca"
done

echo "==> 失効（オーナー用の資格を書き出して確かめたので、初回の root を失効させる）"
# shellcheck disable=SC2016
ob_decrypt_lines "$INIT_FILE" "$IDENTITY" root_token | ob_exec '
set -eu
read -r T
BAO_TOKEN="$T" bao token revoke -self >/dev/null
if BAO_TOKEN="$T" bao token lookup >/dev/null 2>&1; then echo "ERROR: 初回の root が失効していない" >&2; exit 1; fi
echo "    初回の root を失効させた"
'
echo "OK: 設定した。オーナー用の資格を age で暗号化して ${OUT} に書いた（値は表示しない）。初回の root は失効させた。"
echo "    次: ${OUT} も別々の 2 か所に保管し、seed-secrets.sh を流す（手順書）。"
