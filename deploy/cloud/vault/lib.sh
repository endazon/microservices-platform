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

# ob_exec <Pod の中の sh の手順> —— 標準入力をそのまま Pod の中の sh へ渡す。
ob_exec() {
  kubectl -n "$OPENBAO_NS" exec -i deploy/openbao -c openbao -- sh -c "$1"
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
