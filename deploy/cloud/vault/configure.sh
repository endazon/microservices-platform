#!/usr/bin/env bash
# NFR-05, NFR-18, IADR-0689 決定 2・決定 4・決定 6: 初期化の直後に一度だけ、初回の root で OpenBao を設定し、**最後に root を失効させる**。
#
#   bash deploy/cloud/vault/configure.sh --live --init-file <init.sh のファイル>.age --identity <age の識別子> \
#     --recipients <age の受取人のファイル> --operator-out <書き出すファイル>.age
#   bash deploy/cloud/vault/configure.sh --live --revoke-only --init-file <init.sh のファイル>.age --identity <age の識別子>
#     （前の回が失効の段で止まったときだけ。失効だけを流し直す。既に失効していれば何もしない）
#
# 設定するもの（どれも在れば作り直さない）:
#   - KV v2 `cloud/`（値の置き場。パスの規則は IADR-0689 決定 6）
#   - PKI `pki_cloud/`（基盤との通信の CA。**CA の鍵は OpenBao の外へ出さない**〔internal〕）と、サーバ証明書の role
#     （lib.sh の OB_PKI_ROLES: postgres・rabbitmq・keycloak・llmgateway-service。SAN は名指しの 4 つだけ・発行した証明書を保存しない。
#     稼働中の構成へ role を足すときは root-token.sh apply-pki-roles。IADR-0693）
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

INIT_FILE="" IDENTITY="" RECIPIENTS="" OUT="" REVOKE_ONLY=0
K8S_HOST="${OPENBAO_KUBERNETES_HOST:-https://kubernetes.default.svc}"
CA_NAMESPACES="${CA_NAMESPACES:-platform-infra microservices-platform}"
while [ "$#" -gt 0 ]; do
  case "$1" in
    --init-file) INIT_FILE="${2:-}"; shift 2 ;;
    --identity) IDENTITY="${2:-}"; shift 2 ;;
    --recipients) RECIPIENTS="${2:-}"; shift 2 ;;
    --operator-out) OUT="${2:-}"; shift 2 ;;
    --revoke-only) REVOKE_ONLY=1; shift ;;
    *) echo "ERROR: 未知の引数: $1（受け付けるのは --live・--init-file・--identity・--recipients・--operator-out・--revoke-only）" >&2; exit 2 ;;
  esac
done
ob_readable "$INIT_FILE" "init.sh が書いたファイル（--init-file）"
ob_readable "$IDENTITY" "age の識別子のファイル（--identity）"
ob_identity_guard "$IDENTITY"

# 初回の root を失効させる（既に失効していれば何もしない）。失効したことを確かめて 0。
# 🔴 「失効した」と読むのは、OpenBao に届き、unseal 済みで書き込みを受ける状態（active）にあり、lookup が permission denied（403）を返したときだけ。
#    封印中・届かない・想定外のエラーは、失効したとも生きているとも言わずに止める（fail-open にしない）。
#    開けた直後の standby（読み取りだけが通る）の間は、active になるまで待つ（lib.sh の wait_active）。
# shellcheck disable=SC2016
revoke_root() {
  ob_decrypt_lines "$INIT_FILE" "$IDENTITY" root_token | ob_exec '
set -eu
read -r T
'"$OB_POD_WAIT_ACTIVE"'
rc=0; wait_active || rc=$?
case "$rc" in
  0) ;;
  1) echo "ERROR: OpenBao が書き込みを受ける状態（active）にならない（開けた直後の standby のまま）。失効させられず、判定もしない（OpenBao の状態を確かめてから流し直す）" >&2; exit 1 ;;
  *) echo "ERROR: OpenBao が封印中か、届かない。root が失効したかを判定できない（unseal.sh で開けてから流し直す）" >&2; exit 1 ;;
esac
root_state() {
  out="$(BAO_TOKEN="$T" bao token lookup 2>&1)" && { echo alive; return 0; }
  case "$out" in *"Code: 403"*"permission denied"*) echo revoked ;; *) echo unknown ;; esac
}
case "$(root_state)" in
  revoked) echo "    初回の root は既に失効している"; exit 0 ;;
  alive) ;;
  *) echo "ERROR: root の状態を判定できない（permission denied 以外の失敗）。OpenBao の状態を確かめてから流し直す" >&2; exit 1 ;;
esac
BAO_TOKEN="$T" bao token revoke -self >/dev/null
case "$(root_state)" in
  revoked) echo "    初回の root を失効させた" ;;
  alive) echo "ERROR: 初回の root が失効していない" >&2; exit 1 ;;
  *) echo "ERROR: 失効の後の root の状態を判定できない（permission denied 以外の失敗）" >&2; exit 1 ;;
esac
'
}

# --revoke-only: 前の回が失効の段で止まったとき（資格のファイルは書き出し済み・有効）に、失効だけを流し直す。
if [ "$REVOKE_ONLY" = 1 ]; then
  live_opt_in_require "configure.sh"
  ob_need kubectl age node
  ct_require_target "configure.sh"
  revoke_root || { echo "ERROR: 失効を確かめられない。OpenBao の状態を確かめてから --revoke-only を流し直す" >&2; exit 3; }
  echo "OK: 初回の root は失効している。"
  exit 0
fi
ob_readable "$RECIPIENTS" "age の受取人のファイル（--recipients）"
if [ -e "$OUT" ]; then
  echo "ERROR: ${OUT} は在る。上書きしない。前の回が失効の段で止まったなら、そのファイルは有効なので --revoke-only を流す。それ以外は別の名前を与える" >&2
  exit 2
fi
ob_fresh "$OUT"
case "$K8S_HOST" in https://*) ;; *) echo "ERROR: OPENBAO_KUBERNETES_HOST は https:// で始める" >&2; exit 2 ;; esac
live_opt_in_require "configure.sh"
ob_need kubectl age node
ct_require_target "configure.sh"
printf 'probe' | age -R "$RECIPIENTS" >/dev/null || { echo "ERROR: 受取人のファイルで暗号化できない" >&2; exit 2; }

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
roles="$(ob_pki_roles b)"
policies="$(for p in platform-infra-eso msp-eso ast-eso cloud-operator; do policy_write "$p"; done)"
POD="${POD/__PKI_ROLES__/$roles}"
POD="${POD/__POLICIES__/$policies}"
POD="${POD//__K8S_HOST__/$K8S_HOST}"
# 試験だけが与える（クラスタの中では Pod の ServiceAccount の CA を使う）。公開してよい CA の証明書。
K8S_CA="$( [ -n "${OPENBAO_KUBERNETES_CA_FILE:-}" ] && cat "$OPENBAO_KUBERNETES_CA_FILE" || true)"
POD="${POD/__K8S_CA__/$K8S_CA}"

echo "==> 設定（初回の root を使う。失効は資格を書き出して確かめた後）"
# 経路 1: 書き出しの前に落ちた → 書き出し先を消す。root は残る。同じコマンドで流し直す。
if ! ( umask 077
       ob_decrypt_lines "$INIT_FILE" "$IDENTITY" root_token | ob_exec "$POD" | age -R "$RECIPIENTS" -o "$OUT" ); then
  rm -f "$OUT"
  echo "ERROR: 設定に失敗した（書き出し先は消した）。初回の root は残してある。理由を直して configure.sh を同じ引数で流し直す（済んだものは作り直さない）" >&2
  exit 1
fi
chmod 600 "$OUT"
# 書き出した資格を復号して確かめる（中身は表示しない）。確かめられなければ root を残して止まる。
if ! ob_decrypt_lines "$OUT" "$IDENTITY" role_id secret_id >/dev/null; then
  rm -f "$OUT"
  echo "ERROR: 書き出した資格を復号できない（書き出し先は消した）。初回の root は残してある。configure.sh を同じ引数で流し直す" >&2
  exit 1
fi

echo "==> CA の証明書（公開してよい）を ConfigMap platform-infra-ca に置く"
# 経路 2: ここで落ちた → root は生きていて、資格はまだ誰にも渡っていない。書き出し先を消し、同じコマンドで流し直す
#   （流し直しは secret_id を全部作り直すので、消したファイルの資格はどのみち無効になる）。
for ns in $CA_NAMESPACES; do
  if ! ob_exec 'bao read -field=certificate pki_cloud/cert/ca' \
       | kubectl -n "$ns" create configmap platform-infra-ca --from-file=ca.crt=/dev/stdin --dry-run=client -o yaml | kubectl apply -f - >/dev/null; then
    rm -f "$OUT"
    echo "ERROR: ConfigMap ${ns}/platform-infra-ca を置けない（書き出し先は消した。流し直しで資格は作り直す）。初回の root は残してある。理由を直して configure.sh を同じ引数で流し直す" >&2
    exit 1
  fi
  echo "    作った: ConfigMap ${ns}/platform-infra-ca"
done

echo "==> 失効（オーナー用の資格を書き出して確かめたので、初回の root を失効させる）"
# 経路 3: ここで落ちた → 🔴 資格のファイルは消さない（失効が実は効いていたなら、消すと root も資格も無くなる）。
#   ファイルは有効のまま残し、失効だけを流し直す口（--revoke-only）を案内する。既に失効していれば何もしない。
if ! revoke_root; then
  echo "ERROR: 初回の root の失効を確かめられない。資格のファイル ${OUT} は有効なので消していない（2 か所に保管してよい）。" >&2
  echo "       失効だけを流し直す: bash deploy/cloud/vault/configure.sh --live --revoke-only --init-file ${INIT_FILE} --identity <識別子のファイル>" >&2
  exit 3
fi
echo "OK: 設定した。オーナー用の資格を age で暗号化して ${OUT} に書いた（値は表示しない）。初回の root は失効させた。"
echo "    次: ${OUT} も別々の 2 か所に保管し、seed-secrets.sh を流す（手順書）。"
