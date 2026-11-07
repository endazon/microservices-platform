#!/usr/bin/env bash
# NFR-21, NFR-05, IADR-0704 決定 1: バックアップのバケットの保持の設定を、クラスタの外（オーナーの手元）から確かめる。
# 鍵は s3-creds.sh run が渡す（このスクリプトは鍵を読まない・表示しない）。
#
#   bash deploy/cloud/backup/s3-creds.sh run --creds <管理の鍵>.age --identity <識別子> --endpoint <EP> -- \
#     bash deploy/cloud/backup/check-bucket.sh verify --bucket <バケット>
#       → 版の管理・Object Lock（既定の保持）・lifecycle が、object-lock.json・bucket-lifecycle.json と同じか（読むだけ）。
#         定期に（週に 1 度と、警報・障害の後に）流す。違えば 1 で終わり、違う箇所を名指す。
#   bash deploy/cloud/backup/s3-creds.sh run --creds <書き込みの鍵>.age --identity <識別子> --endpoint <EP> -- \
#     bash deploy/cloud/backup/check-bucket.sh probe-writer --bucket <バケット>
#       → クラスタに入れる書き込みの鍵で、保持の設定を書き換えられないか。宣言の JSON（object-lock.json・bucket-lifecycle.json）を
#         実際に書き込もうとして、拒まれれば 0（書き込みの鍵は保持を変えられない）。通れば 1（鍵の権限が広い）。
#         🔴 書き込みが通ると、その時のバケットの設定は宣言の JSON で上書きされる（ずれていれば宣言に揃い、ずれは消える）。
#         そこで書く前に verify と同じ読み取りと突き合わせを行い、ずれがあれば何も書かずに 1 で止まる
#         （ずれは管理の鍵で調べて直す。手順書 §1 の 6 も verify を先に流す）。読めないときも書かずに止まる。
# 終了コード: 0＝合う／拒まれた / 1＝違う／通った・読めない / 2＝引数の誤り
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
die() { echo "ERROR: $2" >&2; exit "$1"; }
CMD="${1:-}"
[ "$#" -ge 1 ] && shift
BUCKET=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --bucket) BUCKET="${2:-}"; shift 2 ;;
    *) die 2 "未知の引数: $1（受け付けるのは --bucket）" ;;
  esac
done
[[ "$BUCKET" =~ ^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$ ]] || die 2 "--bucket はバケットの名前（小文字・数字・. と -）で与える"
case "$CMD" in verify | probe-writer) ;; *) die 2 "verify か probe-writer を与える" ;; esac
for t in aws node; do command -v "$t" >/dev/null 2>&1 || die 1 "$t が無い"; done
[ -n "${AWS_ACCESS_KEY_ID:-}" ] || die 2 "鍵が環境に無い（s3-creds.sh run の -- の後で流す）"
# 宛先は引数で明示する（AWS_ENDPOINT_URL を読まない古い aws CLI でも、既定の AWS の宛先へ鍵を送らない）。
[[ "${AWS_ENDPOINT_URL:-}" =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?/?$ ]] || die 2 "宛先（AWS_ENDPOINT_URL）が環境に無いか https でない（s3-creds.sh run の -- の後で流す）"

LOCK_JSON="$HERE/object-lock.json"
LIFECYCLE_JSON="$HERE/bucket-lifecycle.json"
s3() { aws s3api "$@" --bucket "$BUCKET" --output json --endpoint-url "$AWS_ENDPOINT_URL"; }

# 版の管理・Object Lock・lifecycle を読み、宣言と突き合わせる（読むだけ）。合えば 0、違う・読めなければ 1。
check_declared() {
  ver="$(s3 get-bucket-versioning)" || { echo "NG: 版の管理の設定を読めない" >&2; return 1; }
  lock="$(s3 get-object-lock-configuration)" || { echo "NG: Object Lock の設定を読めない（バケットが Object Lock なしで作られていないか）" >&2; return 1; }
  life="$(s3 get-bucket-lifecycle-configuration)" || { echo "NG: lifecycle の設定を読めない" >&2; return 1; }
  node - "$LOCK_JSON" "$LIFECYCLE_JSON" "$ver" "$lock" "$life" <<'JS'
const fs = require('fs');
const [lockFile, lifeFile, verRaw, lockRaw, lifeRaw] = process.argv.slice(2);
const want = { lock: JSON.parse(fs.readFileSync(lockFile, 'utf8')), life: JSON.parse(fs.readFileSync(lifeFile, 'utf8')) };
const got = { ver: JSON.parse(verRaw || '{}'), lock: (JSON.parse(lockRaw || '{}').ObjectLockConfiguration) || {}, life: JSON.parse(lifeRaw || '{}') };
const out = [];
if (got.ver.Status !== 'Enabled') out.push(`版の管理が Enabled でない（${got.ver.Status || '無し'}）`);
const wr = want.lock.Rule.DefaultRetention, gr = (got.lock.Rule || {}).DefaultRetention || {};
if (got.lock.ObjectLockEnabled !== 'Enabled') out.push('Object Lock が有効でない');
if (gr.Mode !== wr.Mode) out.push(`既定の保持の型が ${gr.Mode || '無し'}（宣言 ${wr.Mode}）`);
if (gr.Days !== wr.Days || gr.Years !== undefined) out.push(`既定の保持の期間が ${gr.Days ?? '?'} 日${gr.Years !== undefined ? `・${gr.Years} 年` : ''}（宣言 ${wr.Days} 日）`);
const key = (r) => JSON.stringify({ id: r.ID, status: r.Status, prefix: (r.Filter || {}).Prefix ?? r.Prefix, days: (r.Expiration || {}).Days, nc: (r.NoncurrentVersionExpiration || {}).NoncurrentDays });
const wRules = new Map(want.life.Rules.map((r) => [r.ID, key(r)]));
const gRules = new Map((got.life.Rules || []).map((r) => [r.ID, key(r)]));
for (const [id, k] of wRules) {
  if (!gRules.has(id)) out.push(`lifecycle の規則 ${id} が無い`);
  else if (gRules.get(id) !== k) out.push(`lifecycle の規則 ${id} が宣言と違う（${gRules.get(id)}）`);
}
for (const id of gRules.keys()) if (!wRules.has(id)) out.push(`lifecycle に宣言に無い規則 ${id} がある`);
if (out.length) { for (const l of out) process.stderr.write(`NG: ${l}\n`); process.exit(1); }
process.stdout.write('OK: 版の管理・Object Lock の既定の保持・lifecycle は宣言どおり\n');
JS
}

if [ "$CMD" = verify ]; then
  check_declared
  exit $?
fi

# probe-writer: 先に読んで宣言と突き合わせ、ずれが無いときだけ宣言の JSON を書き込もうとする（通れば設定は宣言で上書きされる）。
# 拒まれることを期待する。
if ! check_declared; then
  echo "NG: バケットの設定が宣言とずれている（か読めない）ので、書き込みを試さずに止めた。書けばずれが宣言で上書きされて消える。管理の鍵で verify を流して調べる" >&2
  exit 1
fi
rc=0
for op in lifecycle lock; do
  case "$op" in
    lifecycle) set +e; err="$(s3 put-bucket-lifecycle-configuration --lifecycle-configuration "file://$LIFECYCLE_JSON" 2>&1 >/dev/null)"; r=$?; set -e; what="lifecycle" ;;
    lock) set +e; err="$(s3 put-object-lock-configuration --object-lock-configuration "file://$LOCK_JSON" 2>&1 >/dev/null)"; r=$?; set -e; what="Object Lock の既定の保持" ;;
  esac
  if [ "$r" -eq 0 ]; then
    echo "NG: 書き込みの鍵で ${what} を書けた（設定は宣言のままだが、この鍵は保持を変えられる。手順書 §1 の 6 で鍵を絞る）" >&2; rc=1
  elif [[ "$err" == *AccessDenied* || "$err" == *Forbidden* || "$err" == *"(403)"* ]]; then
    echo "OK: 書き込みの鍵では ${what} を書けない（拒まれた）"
  else
    echo "NG: ${what} の書き込みの結果を判定できない（拒否でも成功でもない）。提供元の応答を確かめる" >&2; rc=1
  fi
done
exit "$rc"
