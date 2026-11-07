#!/usr/bin/env bash
# NFR-16, NFR-05, NFR-18, IADR-0696: OpenBao 自身の受け口（TLS）の証明書を作る・作り直す（オーナーが手元から流す）。
#
#   bash deploy/cloud/vault/tls-cert.sh --live issue
#       → 初回（OpenBao を初めて起こす前・平文の構成を移す段 0）。CA と受け口の証明書を作り、Secret openbao/openbao-tls
#         （tls.crt・tls.key・ca.crt）と、ESO が CA を読む ConfigMap openbao-ca（基盤の 2 つの名前空間と、在れば取引ユニットの名前空間）に置く。
#         Secret が既に在れば止まる（作り直しは renew）。
#   bash deploy/cloud/vault/tls-cert.sh --live renew
#       → 作り直し（半年ごと・漏れた疑いのとき）。新しい CA と証明書を作り、CA を併記してから受け口を新しい証明書へ替え
#         （Pod の中で SIGHUP。封印しない）、新しい CA で届くことを確かめてから古い CA を外す。ESO が届かない時間を作らない。
#   bash deploy/cloud/vault/tls-cert.sh --live publish-ca [--namespace <名前空間>]...
#       → Secret の ca.crt（公開してよい）を ConfigMap openbao-ca へ写し直す（取引ユニットを後から入れたとき。既定は全部の名前空間）。
#
# 🔴 鍵は手元のプロセスのメモリ（シェルの変数）にだけ在り、引数・環境変数・ディスクに置かない（IADR-0682 の形）。
#    openssl へはプロセス置換（パイプ）で、kubectl へは標準入力で渡す。**CA の鍵は受け口の証明書に署名した後に捨てる**
#    （どこにも残さない。作り直しは CA ごと作り直す。IADR-0696 決定 1）。
# 🔴 OpenBao の PKI では作らない: 封印のあいだは発行できず、受け口が無いと ESO も unseal の後の手順も OpenBao へ届かない（鶏と卵）。
# 鍵は ECDSA P-256。受け口の証明書は 1 年、CA は 2 年（CA の鍵は捨てるので、CA の期限は作り直しの間の猶予である）。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
# shellcheck source=scripts/lib/live-opt-in.sh
. "$ROOT/scripts/lib/live-opt-in.sh"
# shellcheck source=deploy/cloud/vault/lib.sh
. "$HERE/lib.sh"
live_opt_in_scan "$@"
set -- "${LIVE_REST[@]+"${LIVE_REST[@]}"}"

# CA を読む ConfigMap を置く名前空間。基盤の 2 つは必須（無ければ止まる）、取引ユニットの名前空間は在れば置く
# （取引ユニットを入れる前なら、入れた後に publish-ca を流す。手順書）。
OB_CA_REQUIRED_NS="platform-infra microservices-platform"
OB_CA_OPTIONAL_NS="ai-stock-trading"
# kubelet が Secret の書き換えを Pod へ届けるまで待つ上限（秒）。Pod の中で受け口を読み直させる番号（bao server の PID）。
OB_TLS_SYNC_WAIT_S="${OB_TLS_SYNC_WAIT_S:-180}"
OB_POD_SERVER_PID="${OB_POD_SERVER_PID:-1}"
case "$OB_TLS_SYNC_WAIT_S$OB_POD_SERVER_PID" in '' | *[!0-9]*) echo "ERROR: OB_TLS_SYNC_WAIT_S・OB_POD_SERVER_PID は正の整数" >&2; exit 2 ;; esac

CMD="" NAMESPACES=()
while [ "$#" -gt 0 ]; do
  case "$1" in
    issue | renew | publish-ca) [ -z "$CMD" ] || { echo "ERROR: 操作は 1 つだけ" >&2; exit 2; }; CMD="$1"; shift ;;
    --namespace)
      [[ "${2:-}" =~ ^[a-z0-9]([-a-z0-9]*[a-z0-9])?$ ]] || { echo "ERROR: --namespace は名前空間の名前を与える" >&2; exit 2; }
      NAMESPACES+=("$2"); shift 2 ;;
    *) echo "ERROR: 未知の引数: $1（操作は issue・renew・publish-ca。publish-ca だけが --namespace を受ける）" >&2; exit 2 ;;
  esac
done
[ -n "$CMD" ] || { echo "ERROR: 操作（issue・renew・publish-ca のどれか）を与える" >&2; exit 2; }
[ "${#NAMESPACES[@]}" -eq 0 ] || [ "$CMD" = publish-ca ] || { echo "ERROR: --namespace は publish-ca だけが受ける" >&2; exit 2; }
live_opt_in_require "tls-cert.sh"
ob_need kubectl openssl node
ct_require_target "tls-cert.sh"

SECRET=openbao-tls
CONFIGMAP=openbao-ca

# --- 作る（値はメモリの変数にだけ持つ） ---
ca_key="" ca_crt="" key="" crt=""

new_pair() {
  local stamp ca_cnf leaf_cnf csr
  stamp="$(date -u +%Y%m%dT%H%M%SZ)"
  ca_cnf='[req]
distinguished_name = dn
[dn]
[v3_ca]
basicConstraints = critical, CA:TRUE, pathlen:0
keyUsage = critical, keyCertSign, cRLSign
subjectKeyIdentifier = hash'
  # 名指しの名前（Service の 4 つの形）と、Pod の中のループバック（オーナーの手順・readiness）だけ。
  leaf_cnf="[v3_leaf]
basicConstraints = critical, CA:FALSE
keyUsage = critical, digitalSignature
extendedKeyUsage = serverAuth
subjectAltName = DNS:openbao, DNS:openbao.${OPENBAO_NS}, DNS:openbao.${OPENBAO_NS}.svc, DNS:openbao.${OPENBAO_NS}.svc.cluster.local, IP:127.0.0.1
authorityKeyIdentifier = keyid"
  ca_key="$(openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 2>/dev/null)"
  ca_crt="$(openssl req -x509 -new -sha256 -days 730 -key <(printf '%s\n' "$ca_key") \
    -subj "/O=microservices-platform/CN=openbao listener CA ${stamp}" -config <(printf '%s\n' "$ca_cnf") -extensions v3_ca)"
  key="$(openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 2>/dev/null)"
  csr="$(openssl req -new -sha256 -key <(printf '%s\n' "$key") -subj "/CN=openbao.${OPENBAO_NS}.svc" -config <(printf '%s\n' "$ca_cnf"))"
  crt="$(openssl x509 -req -sha256 -days 365 -in <(printf '%s\n' "$csr") -CA <(printf '%s\n' "$ca_crt") -CAkey <(printf '%s\n' "$ca_key") \
    -set_serial "0x$(openssl rand -hex 16)" -extfile <(printf '%s\n' "$leaf_cnf") -extensions v3_leaf 2>/dev/null)"
  # CA の鍵は署名の後に捨てる（この後は使わない）。
  ca_key=""
  printf '%s\n' "$crt" | openssl verify -CAfile <(printf '%s\n' "$ca_crt") >/dev/null \
    || { echo "ERROR: 作った証明書を CA で検証できない（何も置いていない）" >&2; exit 1; }
  [[ "$key" == *"BEGIN PRIVATE KEY"* && "$crt" == *"BEGIN CERTIFICATE"* && "$ca_crt" == *"BEGIN CERTIFICATE"* ]] \
    || { echo "ERROR: 鍵か証明書を作れない（openssl を確かめる。何も置いていない）" >&2; exit 1; }
}

# manifest <Secret|ConfigMap> <名前空間> —— 標準入力（「@@<キー>」の行で区切った PEM）から、適用する JSON を標準出力へ出す。
#   出力は kubectl の標準入力へだけ流す。
manifest() {
  node -e '
    let s = ""; process.stdin.on("data", (c) => { s += c; }).on("end", () => {
      const [kind, ns, name] = process.argv.slice(1);
      const data = {}; let cur = null;
      for (const line of s.split("\n")) {
        const m = /^@@([a-z.]+)$/.exec(line);
        if (m) { cur = m[1]; data[cur] = ""; continue; }
        if (cur !== null) data[cur] += line + "\n";
      }
      for (const k of Object.keys(data)) {
        data[k] = data[k].replace(/\n+$/, "\n");
        if (!/^-----BEGIN [A-Z ]+-----\n/.test(data[k])) { process.stderr.write(`ERROR: ${k} が PEM でない（値は表示しない）\n`); process.exit(1); }
      }
      const metadata = { name, namespace: ns, labels: { app: "openbao" } };
      const doc = kind === "Secret"
        ? { apiVersion: "v1", kind, type: "kubernetes.io/tls", metadata,
            data: Object.fromEntries(Object.entries(data).map(([k, v]) => [k, Buffer.from(v).toString("base64")])) }
        : { apiVersion: "v1", kind, metadata, data };
      process.stdout.write(JSON.stringify(doc));
    });' "$1" "$2" "$3"
}

k_apply() { kubectl apply --server-side --field-manager=openbao-tls-cert --force-conflicts -f - >/dev/null; }

put_secret() { # <受け口の CA の束>
  printf '@@tls.crt\n%s\n@@tls.key\n%s\n@@ca.crt\n%s\n' "$crt" "$key" "$1" | manifest Secret "$OPENBAO_NS" "$SECRET" | k_apply
}

ns_exists() { [ -n "$(kubectl get namespace "$1" --ignore-not-found -o name)" ]; }

# publish <CA の束> [名前空間...] —— ConfigMap openbao-ca を置く。名前空間を与えなければ、必須の 2 つと、在れば取引ユニットの名前空間。
publish() {
  local bundle="$1" ns
  shift
  local targets=("$@")
  if [ "${#targets[@]}" -eq 0 ]; then
    for ns in $OB_CA_REQUIRED_NS; do targets+=("$ns"); done
    for ns in $OB_CA_OPTIONAL_NS; do
      if ns_exists "$ns"; then targets+=("$ns"); else echo "    まだ無い: 名前空間 ${ns}（取引ユニットを入れた後に publish-ca --namespace ${ns} を流す）"; fi
    done
  fi
  for ns in "${targets[@]}"; do
    ns_exists "$ns" || { echo "ERROR: 名前空間 ${ns} が無い（deploy/cloud/infra/namespaces.yaml を先に当てる）" >&2; return 1; }
    printf '@@ca.crt\n%s\n' "$bundle" | manifest ConfigMap "$ns" "$CONFIGMAP" | k_apply \
      || { echo "ERROR: ConfigMap ${ns}/${CONFIGMAP} を置けない" >&2; return 1; }
    echo "    置いた: ConfigMap ${ns}/${CONFIGMAP}（CA の証明書。公開してよい）"
  done
}

# 今の Secret の ca.crt（公開してよい）。無ければ空。読めなければ止まる。鍵（tls.key）は読まない。
current_ca() {
  local b
  b="$(kubectl -n "$OPENBAO_NS" get secret "$SECRET" --ignore-not-found -o 'jsonpath={.data.ca\.crt}')" \
    || { echo "ERROR: Secret ${OPENBAO_NS}/${SECRET} を読めない" >&2; exit 1; }
  [ -n "$b" ] || return 0
  printf '%s' "$b" | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>process.stdout.write(Buffer.from(s,"base64").toString()))'
}

secret_exists() { [ -n "$(kubectl -n "$OPENBAO_NS" get secret "$SECRET" --ignore-not-found -o name)" ]; }

# Pod の中のファイル（公開の部分だけ: tls.crt・ca.crt）が want と同じになるまで待つ（kubelet が Secret を届けるまで）。
wait_mounted() { # <ファイル名> <期待する中身>
  local i=0 got
  while :; do
    got="$(ob_exec "cat '${OB_POD_TLS_DIR}/$1'" 2>/dev/null || true)"
    [ "$got" != "$2" ] || return 0
    i=$((i + 5))
    [ "$i" -lt "$OB_TLS_SYNC_WAIT_S" ] || return 1
    sleep 5
  done
}

# Pod の中から、与えた CA だけで受け口へ届くか（0 = 届く。封印中でもよい: bao status の 0 と 2）。CA は標準入力で渡す（公開の値）。
reachable_with() { # <CA>
  # shellcheck disable=SC2016
  printf '%s\n' "$1" | ob_exec 'set -u; t="$(mktemp)"; cat > "$t"; rc=0; BAO_CACERT="$t" bao status >/dev/null 2>&1 || rc=$?; rm -f "$t"; [ "$rc" -eq 0 ] || [ "$rc" -eq 2 ]'
}

case "$CMD" in
  issue)
    ! secret_exists || { echo "ERROR: Secret ${OPENBAO_NS}/${SECRET} は在る。作り直しは renew" >&2; exit 1; }
    for ns in $OB_CA_REQUIRED_NS; do ns_exists "$ns" || { echo "ERROR: 名前空間 ${ns} が無い（deploy/cloud/infra/namespaces.yaml を先に当てる）" >&2; exit 1; }; done
    ns_exists "$OPENBAO_NS" || { echo "ERROR: 名前空間 ${OPENBAO_NS} が無い（kubectl apply -k deploy/cloud/vault を先に当てる）" >&2; exit 1; }
    echo "==> 受け口の CA と証明書を作る（CA の鍵は署名の後に捨てる）"
    new_pair
    # CA の公開の配り先を先に置く（ESO が受け口の証明書を検証できる状態を、受け口より先に作る）。
    echo "==> CA の証明書を ConfigMap ${CONFIGMAP} に置く"
    publish "$ca_crt"
    echo "==> 証明書と鍵を Secret ${OPENBAO_NS}/${SECRET} に置く"
    put_secret "$ca_crt"
    echo "OK: 受け口の証明書を置いた（値は表示しない）。OpenBao の Pod はこの Secret をマウントして起きる。"
    ;;
  renew)
    secret_exists || { echo "ERROR: Secret ${OPENBAO_NS}/${SECRET} が無い。初回は issue" >&2; exit 1; }
    old="$(current_ca)"
    [[ "$old" == *"BEGIN CERTIFICATE"* ]] || { echo "ERROR: Secret の ca.crt を読めない" >&2; exit 1; }
    echo "==> 新しい CA と証明書を作る（CA の鍵は署名の後に捨てる）"
    new_pair
    bundle="$(printf '%s\n%s\n' "$old" "$ca_crt")"
    echo "==> 段 1: 古い CA と新しい CA を併記する（ESO と Pod の中の手順は、どちらの証明書でも検証できる）"
    publish "$bundle"
    put_secret "$bundle"
    wait_mounted tls.crt "$crt" || { echo "ERROR: ${OB_TLS_SYNC_WAIT_S} 秒待っても Pod へ新しい証明書が届かない。受け口は古い証明書のまま（CA は併記のまま。renew を流し直してよい）" >&2; exit 1; }
    echo "==> 段 2: 受け口に新しい証明書を読み直させる（SIGHUP。封印しない）"
    ob_exec "kill -HUP ${OB_POD_SERVER_PID}"
    ok=0
    for _ in 1 2 3 4 5 6 7 8 9 10; do reachable_with "$ca_crt" && { ok=1; break; }; sleep 1; done
    [ "$ok" = 1 ] || { echo "ERROR: 新しい CA だけでは受け口へ届かない。CA は併記のままにした（ESO は届く）。OpenBao のログを確かめ、renew を流し直す" >&2; exit 1; }
    echo "==> 段 3: 古い CA を外す"
    put_secret "$ca_crt"
    publish "$ca_crt"
    wait_mounted ca.crt "$ca_crt" || echo "    注意: Pod の中の ca.crt はまだ古い束（併記）。kubelet が届ければ揃う（受け口は新しい証明書で動いている）"
    echo "OK: 受け口の証明書を作り直した（値は表示しない）。古い CA は外した。"
    ;;
  publish-ca)
    secret_exists || { echo "ERROR: Secret ${OPENBAO_NS}/${SECRET} が無い。先に issue" >&2; exit 1; }
    ca="$(current_ca)"
    [[ "$ca" == *"BEGIN CERTIFICATE"* ]] || { echo "ERROR: Secret の ca.crt を読めない" >&2; exit 1; }
    echo "==> CA の証明書を ConfigMap ${CONFIGMAP} に置き直す"
    publish "$ca" "${NAMESPACES[@]+"${NAMESPACES[@]}"}"
    echo "OK: 置いた。"
    ;;
esac
