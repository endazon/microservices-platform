# shellcheck shell=bash
# NFR-05, NFR-18, IADR-0689: クラウドの秘匿管理の手順（init.sh・unseal.sh・configure.sh・seed-secrets.sh・root-token.sh）が共用する関数。
#
# 🔴 秘密の値（unseal の鍵・root・オーナー用の AppRole の資格・作った値）を、**引数・環境変数・ディスクに置かない**（IADR-0682 の形）。
#   - 手元: age で暗号化したファイルだけを持つ。復号はパイプの先（kubectl exec の標準入力）へだけ流す。
#   - Pod の中: 標準入力で受け、1 つの sh の変数にだけ持つ。bao へは標準入力（`-`）で渡す。トークンは、その 1 回の子の環境
#     （BAO_TOKEN）にだけ載る（Pod の中。Pod の外へは出ない）。
#   - 引数に載るのは、パス・キーの名前・バイト数・Pod の中で走る手順（値を持たない）だけ。
#
# 宛先は OPENBAO_NS（既定 openbao）の deploy/openbao。kubectl は手元から SSH のトンネル越しに使う（README §1-4）。

OPENBAO_NS="${OPENBAO_NS:-openbao}"

ob_need() {
  local t
  for t in "$@"; do
    command -v "$t" >/dev/null 2>&1 || { echo "ERROR: ${t} が無い（手元に入れてから流す）" >&2; exit 1; }
  done
}

# NFR-16, IADR-0696: Pod の中の bao が話す宛先。OpenBao の受け口は TLS だけ（平文の口を持たない）なので、Pod の中のループバックへも
#   https で、受け口の CA（Secret openbao-tls の ca.crt。tls-cert.sh が作る）で検証して話す。検証を外す指定（*_SKIP_VERIFY）は消す。
#   どちらも値ではない（宛先とファイルのパス）。手順の文字列へ埋め込むため、形を確かめる。試験は実物の OpenBao の宛先へ向ける。
OB_POD_ADDR="${OB_POD_ADDR:-https://127.0.0.1:8443}"
OB_POD_TLS_DIR="${OB_POD_TLS_DIR:-/openbao/tls}"
[[ "$OB_POD_ADDR" =~ ^https://[A-Za-z0-9.-]+:[0-9]+$ ]] || { echo "ERROR: OB_POD_ADDR は https://<名前>:<ポート> の形で与える（平文の宛先は受けない）" >&2; exit 2; }
[[ "$OB_POD_TLS_DIR" =~ ^/[A-Za-z0-9._/-]+$ ]] || { echo "ERROR: OB_POD_TLS_DIR は絶対パスで与える" >&2; exit 2; }

# ob_exec <Pod の中の sh の手順> —— 標準入力をそのまま Pod の中の sh へ渡す（宛先は上の TLS の口）。
ob_exec() {
  kubectl -n "$OPENBAO_NS" exec -i deploy/openbao -c openbao -- sh -c "export BAO_ADDR='${OB_POD_ADDR}' BAO_CACERT='${OB_POD_TLS_DIR}/ca.crt'; unset BAO_SKIP_VERIFY VAULT_SKIP_VERIFY BAO_CAPATH VAULT_CAPATH; $1"
}

# ob_readable <ファイル> <説明> —— 読めるファイルか（中身は読まない）
ob_readable() {
  [ -n "$1" ] && [ -r "$1" ] || { echo "ERROR: ${2} を読めない: ${1:-（未指定）}" >&2; exit 2; }
}

# ob_fresh <ファイル> —— 書き出し先が在れば止める（上書きで鍵を失わない）
ob_fresh() {
  [ -n "$1" ] || { echo "ERROR: 書き出し先が未指定" >&2; exit 2; }
  [ ! -e "$1" ] || { echo "ERROR: ${1} は在る。上書きしない（別の名前を与える）" >&2; exit 2; }
}

# ob_decrypt_lines <age のファイル> <age の識別子のファイル> <JSON のキー>... —— 復号した JSON から、キーの値を 1 行ずつ標準出力へ出す。
#   出力はパイプの先（ob_exec の標準入力）へだけ流す。配列は最初の要素を出す（unseal_keys_b64）。
ob_decrypt_lines() {
  local file="$1" id="$2"
  shift 2
  age -d -i "$id" "$file" | node -e '
    let s = ""; process.stdin.on("data", (c) => { s += c; }).on("end", () => {
      const j = JSON.parse(s);
      for (const k of process.argv.slice(1)) {
        let v = j[k];
        if (Array.isArray(v)) v = v[0];
        if (typeof v !== "string" || !/^[A-Za-z0-9+\/=._-]+$/.test(v)) { process.stderr.write(`ERROR: 復号した中身に ${k} が無いか、形が違う（値は表示しない）\n`); process.exit(1); }
        process.stdout.write(v + "\n");
      }
    });' "$@"
}

# Pod の中の手順の前置き: 1 行目（AppRole の role_id）・2 行目（secret_id）を読み、ループバックからログインして T に持つ。
# shellcheck disable=SC2016
OB_POD_LOGIN='
set -eu
umask 077
read -r rid; read -r sid
T="$(printf "{\"role_id\":\"%s\",\"secret_id\":\"%s\"}" "$rid" "$sid" | bao write -field=token auth/approle/login -)"
unset rid sid
[ -n "$T" ] || { echo "ERROR: オーナー用の資格で入れない" >&2; exit 1; }
b() { BAO_TOKEN="$T" bao "$@"; }
done_revoke() { BAO_TOKEN="$T" bao token revoke -self >/dev/null 2>&1 || true; }
trap done_revoke EXIT
'

# Pod の中の関数: パスとキーの有無を、OpenBao が「無い」と明示したときだけ「無い」と読む（IADR-0689 決定 11。2026-11-07）。
# 🔴 権限・通信・封印などで読めないときは unknown を返す。呼び手は unknown なら書かずに止める（無いと読むと、kv put がパスの
#    既存のキーを全部置き換える）。字面は OpenBao 2.7.1 の CLI で確かめた:
#    パスが無い → 「No value found at cloud/metadata/<パス>」（終了 2）・キーが無い → 「Field "<キー>" not present in …」（終了 1）。
#    値は読まない（kv get の標準出力は捨て、エラーの出力だけを見る）。
# shellcheck disable=SC2016
OB_POD_KV_STATE='
path_state() {
  _e="$(b kv metadata get -mount=cloud "$1" 2>&1 >/dev/null)" && { echo present; return 0; }
  case "$_e" in "No value found at cloud/metadata/$1"*) echo absent ;; *) echo unknown ;; esac
}
key_state() {
  _e="$(b kv get -mount=cloud -field="$2" "$1" 2>&1 >/dev/null)" && { echo present; return 0; }
  case "$_e" in *"Field \"$2\" not present in"*) echo absent ;; *) echo unknown ;; esac
}
'

# Pod の中の関数: 開けた（sealed=false）だけでなく、書き込みを受ける状態（active。`bao status` の "is_self": true）になるまで待つ
# （IADR-0689 決定 1。2026-11-07）。OpenBao 2.7 の raft は、開けた直後にまず読み取りだけを受ける standby に入り、
# リーダーの選出と active への切り替えが済むまでは書き込みを「local node not active but active cluster node not found」で拒む。
# この間も読み取り（token lookup など）は通るので、開けた直後の書き込み（root の失効など）だけが落ちる。
#   戻り値: 0 = active・1 = 待ち切っても active にならない・2 = 封印中か届かない（待たずに返す）。
#   待つ上限は OB_ACTIVE_WAIT_S 秒（既定 60。整数だけを受ける。手順の文字列へ埋め込むため、形を確かめる）。
OB_ACTIVE_WAIT_S="${OB_ACTIVE_WAIT_S:-60}"
case "$OB_ACTIVE_WAIT_S" in
  '' | *[!0-9]*) echo "ERROR: OB_ACTIVE_WAIT_S は待つ秒数（正の整数）を与える" >&2; exit 2 ;;
esac
[ "$OB_ACTIVE_WAIT_S" -ge 1 ] || { echo "ERROR: OB_ACTIVE_WAIT_S は 1 以上" >&2; exit 2; }
# shellcheck disable=SC2016
OB_POD_WAIT_ACTIVE='
wait_active() {
  _n=0
  while :; do
    _st="$(bao status -format=json 2>/dev/null || true)"
    case "$_st" in *"\"sealed\": false"*) ;; *) return 2 ;; esac
    case "$_st" in *"\"is_self\": true"*) return 0 ;; esac
    _n=$((_n + 1))
    [ "$_n" -lt '"$OB_ACTIVE_WAIT_S"' ] || return 1
    sleep 1
  done
}
'

# NFR-16, NFR-18, IADR-0689 決定 4, IADR-0693: PKI（pki_cloud）のサーバ証明書の role の一覧（<サービス>:<名前空間>）。
#   configure.sh（初回の root）と root-token.sh apply-pki-roles（稼働中の構成。作り直した root）が同じ一覧を書く（2 か所に持たない）。
#   許す名前は短い名前・名前空間つき・.svc・.svc.cluster.local の 4 つだけ（従来の SAN と同じ）。発行した証明書は保存しない（no_store）。
OB_PKI_ROLES="postgres:platform-infra rabbitmq:platform-infra keycloak:platform-infra llmgateway-service:microservices-platform"

# ob_pki_roles <Pod の中で bao を呼ぶ関数の名前（b か r）> —— 一覧の role を書く Pod の中の手順（値を持たない）を標準出力へ出す。
ob_pki_roles() {
  local cmd="$1" e svc ns
  for e in $OB_PKI_ROLES; do
    svc="${e%%:*}" ns="${e#*:}"
    printf '%s write pki_cloud/roles/%s allowed_domains="%s,%s.%s,%s.%s.svc,%s.%s.svc.cluster.local" ' "$cmd" "$svc" "$svc" "$svc" "$ns" "$svc" "$ns" "$svc" "$ns"
    printf 'allow_bare_domains=true allow_subdomains=false allow_glob_domains=false allow_wildcard_certificates=false allow_localhost=false allow_ip_sans=false '
    printf 'server_flag=true client_flag=false key_type=rsa key_bits=3072 max_ttl=8760h no_store=true >/dev/null\n'
    printf 'echo "    書いた: PKI の role %s"\n' "$svc"
  done
}
