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
