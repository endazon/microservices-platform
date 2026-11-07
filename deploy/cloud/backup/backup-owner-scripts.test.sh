#!/usr/bin/env bash
# NFR-21, NFR-05, IADR-0704 決定 1: オーナーの手元で使うバックアップの手順（s3-creds.sh・check-bucket.sh）を固定する。
#
#   bash deploy/cloud/backup/backup-owner-scripts.test.sh
#
# - s3-creds.sh: 鍵は age で暗号化したファイルだけに書かれ、使うときは 1 つのコマンドの環境にだけ載る（引数・平文のファイル・呼んだシェルに残らない）。
# - check-bucket.sh verify: 版の管理・Object Lock の既定の保持・lifecycle が宣言（object-lock.json・bucket-lifecycle.json）と違えば落ちる。
# - check-bucket.sh probe-writer: 書き込みの鍵で保持の設定を書けたら落ちる。書く前に宣言と突き合わせ、ずれていれば（読めなければ）書かずに止まる。
# - s3-creds.sh run: AWS_ENDPOINT_URL を読まない古い aws CLI では鍵を渡さない。check-bucket.sh は宛先を --endpoint-url で明示する。
# aws は PATH のスタブ（引数を記録し、バケットの設定を作業場の JSON から返す）。age は実物。提供元には触れない。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
for t in age age-keygen node; do command -v "$t" >/dev/null || { echo "✗ $t が無い" >&2; exit 1; }; done
W="$(mktemp -d)"
trap 'rm -rf "$W"' EXIT
mkdir -p "$W/bin" "$W/s3"
cat > "$W/bin/aws" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$ALOG"
[ "${1:-}" = --version ] && { echo "${STUB_AWS_VERSION:-aws-cli/2.15.0 Python/3.11.6 Linux/6.1 exe/x86_64}"; exit 0; }
[ -z "${STUB_READ_FAIL:-}" ] || case "$2" in get-*) echo "An error occurred (AccessDenied) when calling the $2 operation" >&2; exit 254 ;; esac
[ "${1:-}" = s3api ] || { echo "stub: 想定外: $*" >&2; exit 9; }
case "$2" in
  get-bucket-versioning) cat "$S3_DIR/versioning.json" ;;
  get-object-lock-configuration) cat "$S3_DIR/lock.json" ;;
  get-bucket-lifecycle-configuration) cat "$S3_DIR/lifecycle.json" ;;
  put-bucket-lifecycle-configuration | put-object-lock-configuration)
    case "${STUB_PUT:-deny}" in
      allow) exit 0 ;;
      deny) echo "An error occurred (AccessDenied) when calling the $2 operation: Access Denied" >&2; exit 254 ;;
      *) echo "An error occurred (InternalError) when calling the $2 operation" >&2; exit 254 ;;
    esac ;;
  *) echo "stub: 想定外: $*" >&2; exit 9 ;;
esac
EOF
chmod +x "$W/bin/aws"
export PATH="$W/bin:$PATH" ALOG="$W/aws.log" S3_DIR="$W/s3"
age-keygen -o "$W/id.txt" 2> "$W/recip.txt"; sed -i 's/^Public key: //' "$W/recip.txt"
ID_VAL="AKTESTID0000000001" SECRET_VAL="tEstSeCrEt0000000000000000000000000000ab"
EP="https://fsn1.example-objectstorage.test"

pass=0
ok() { pass=$((pass + 1)); echo "  ok  $1"; }
ng() { echo "  NG  $1" >&2; exit 1; }
creds() { bash "$HERE/s3-creds.sh" run --creds "$W/k.age" --identity "$W/id.txt" --endpoint "$EP" -- "$@"; }

# --- store ---
printf '%s\n%s\n' "$ID_VAL" "$SECRET_VAL" | bash "$HERE/s3-creds.sh" store --recipients "$W/recip.txt" --out "$W/k.age" >/dev/null
[ "$(stat -c %a "$W/k.age")" = 600 ] || ng "store の書き出しが 0600 でない"
grep -qF "$SECRET_VAL" "$W/k.age" && ng "store が鍵を平文で書いた"
[ "$(age -d -i "$W/id.txt" "$W/k.age" | sed -n 2p)" = "$SECRET_VAL" ] || ng "store の暗号文を復号して鍵が戻らない"
set +e
printf '%s\n%s\n' "$ID_VAL" "$SECRET_VAL" | bash "$HERE/s3-creds.sh" store --recipients "$W/recip.txt" --out "$W/k.age" >/dev/null 2>&1; rc1=$?
printf '%s\n%s\n' "$ID_VAL" 'bad value;x' | bash "$HERE/s3-creds.sh" store --recipients "$W/recip.txt" --out "$W/bad.age" >"$W/out" 2>&1; rc2=$?
set -e
[ "$rc1" -eq 2 ] || ng "store が在るファイルを上書きした"
[ "$rc2" -eq 2 ] && [ ! -e "$W/bad.age" ] && ! grep -qF 'bad value' "$W/out" || ng "store が形の違う鍵を拒まない・表示した"
mkdir "$W/st"
( cd "$W/st" && printf '%s\n%s\n' "$ID_VAL" "$SECRET_VAL" | TMPDIR="$W/st" bash "$HERE/s3-creds.sh" store --recipients "$W/recip.txt" --out k2.age >/dev/null )
[ "$(ls -A "$W/st")" = k2.age ] || ng "store が暗号文のほかにファイルを作った"
ok "store: 鍵を age で暗号化したファイル（0600）だけに書き、在れば上書きせず、形の違う鍵は表示せずに拒む"

# --- run: 1 つのコマンドの環境にだけ載る ---
cat > "$W/child.sh" <<EOF
#!/usr/bin/env bash
[ "\$AWS_ACCESS_KEY_ID" = "$ID_VAL" ] && [ "\$AWS_SECRET_ACCESS_KEY" = "$SECRET_VAL" ] && [ "\$AWS_ENDPOINT_URL" = "$EP" ] \
  && [ "\$AWS_ENDPOINT_URL_S3" = "$EP" ] && [ "\$AWS_IGNORE_CONFIGURED_ENDPOINT_URLS" = false ] \
  && [ "\$RCLONE_CONFIG_OBJSTORE_SECRET_ACCESS_KEY" = "$SECRET_VAL" ] && [ "\$RCLONE_CONFIG_OBJSTORE_ACCESS_KEY_ID" = "$ID_VAL" ] \
  && [ "\$RCLONE_CONFIG_OBJSTORE_ENDPOINT" = "$EP" ] && [ "\$RCLONE_CONFIG_OBJSTORE_TYPE" = s3 ] && [ "\$RCLONE_CONFIG_OBJSTORE_ENV_AUTH" = false ] \
  && [ -z "\${id:-}\${secret:-}" ] && echo CHILD-OK
EOF
[ "$(creds bash "$W/child.sh")" = CHILD-OK ] || ng "run が鍵を子の環境へ渡さない"
( unset AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY
  creds true
  [ -z "${AWS_SECRET_ACCESS_KEY:-}${RCLONE_CONFIG_OBJSTORE_SECRET_ACCESS_KEY:-}" ] ) || ng "run の後に、呼んだシェルへ鍵が残った"
# 子の引数に鍵が載らない（引数を記録するスタブで確かめる）
creds aws s3api get-bucket-versioning --bucket b >/dev/null 2>&1 || true
grep -qF "$SECRET_VAL" "$ALOG" && ng "鍵がコマンドの引数に載った"
set +e
bash "$HERE/s3-creds.sh" run --creds "$W/k.age" --identity "$W/recip.txt" --endpoint "$EP" -- true >"$W/out" 2>&1; rc1=$?
bash "$HERE/s3-creds.sh" run --creds "$W/k.age" --identity "$W/id.txt" --endpoint "http://x" -- true >/dev/null 2>&1; rc2=$?
bash "$HERE/s3-creds.sh" run --creds "$W/k.age" --identity "$W/id.txt" --endpoint "$EP" >/dev/null 2>&1; rc3=$?
set -e
[ "$rc1" -eq 1 ] && ! grep -qF "$SECRET_VAL" "$W/out" || ng "復号できないとき 1 で止まらない・鍵を表示した"
[ "$rc2" -eq 2 ] && [ "$rc3" -eq 2 ] || ng "https でない宛先・コマンドの無い run を拒まない"
ok "run: 鍵は 1 つのコマンドの環境（AWS_*・rclone の objstore）にだけ載り、呼んだシェル・引数には残らない。復号できなければ止まる"

# --- run: 古い aws CLI には鍵を渡さない ---
for v in "aws-cli/2.13.0 Python/3.11" "aws-cli/1.29.0 Python/3.11" "aws-cli/2.31.4 Python/3.13"; do
  [ "$(STUB_AWS_VERSION="$v" creds echo RAN)" = RAN ] || ng "足りる版（$v）で run が止まった"
done
for v in "aws-cli/2.12.9 Python/3.11" "aws-cli/1.28.99 Python/3.11" "aws-cli/0.9.0 Python/2.7" "something else"; do
  set +e; out="$(STUB_AWS_VERSION="$v" creds echo RAN 2>&1)"; rc=$?; set -e
  [ "$rc" -eq 1 ] && [[ "$out" != *RAN* ]] && [[ "$out" == *"aws CLI の版"* ]] && [[ "$out" != *"$SECRET_VAL"* ]] || ng "古い・判定できない版（$v）で run が鍵を渡した（rc=$rc）"
done
ok "run: AWS_ENDPOINT_URL を読まない古い aws CLI（v2 < 2.13・v1 < 1.29）と判定できない版では、コマンドを流さず 1 で止まる"

# --- verify ---
reset_bucket() {
  echo '{"Status":"Enabled"}' > "$S3_DIR/versioning.json"
  node -e 'const fs=require("fs");process.stdout.write(JSON.stringify({ObjectLockConfiguration:JSON.parse(fs.readFileSync(process.argv[1],"utf8"))}))' "$HERE/object-lock.json" > "$S3_DIR/lock.json"
  cp "$HERE/bucket-lifecycle.json" "$S3_DIR/lifecycle.json"
}
verify() { creds bash "$HERE/check-bucket.sh" verify --bucket ast-backup-test; }
reset_bucket
verify >/dev/null || ng "宣言どおりのバケットで verify が通らない"
mutate() { node -e "const fs=require('fs');const p=process.argv[1];const j=JSON.parse(fs.readFileSync(p,'utf8'));($1)(j);fs.writeFileSync(p,JSON.stringify(j))" "$S3_DIR/$2"; }
expect_ng() {
  set +e; verify >/dev/null 2>"$W/out"; rc=$?; set -e
  [ "$rc" -eq 1 ] && grep -q "$2" "$W/out" || ng "verify が「$1」を検出しない（rc=$rc）"
  reset_bucket
}
mutate 'j=>{j.Rules[0].Expiration.Days=1}' lifecycle.json;          expect_ng "lifecycle の日数を短くした" 'postgres-daily-30d が宣言と違う'
mutate 'j=>{j.Rules[1].Filter.Prefix="x/"}' lifecycle.json;          expect_ng "lifecycle の接頭辞を替えた" 'postgres-monthly-365d が宣言と違う'
mutate 'j=>{j.Rules.push({ID:"extra",Status:"Enabled",Filter:{Prefix:""},Expiration:{Days:1}})}' lifecycle.json; expect_ng "規則を足した" '宣言に無い規則 extra'
mutate 'j=>{j.Rules.pop()}' lifecycle.json;                          expect_ng "規則を消した" 'postgres-monthly-365d が無い'
mutate 'j=>{j.ObjectLockConfiguration.Rule.DefaultRetention.Days=1}' lock.json; expect_ng "既定の保持を短くした" '既定の保持の期間'
mutate 'j=>{j.ObjectLockConfiguration.Rule.DefaultRetention.Mode="GOVERNANCE"}' lock.json; expect_ng "保持の型を GOVERNANCE にした" '既定の保持の型'
mutate 'j=>{delete j.ObjectLockConfiguration.Rule}' lock.json;       expect_ng "既定の保持を外した" '既定の保持の型'
echo '{"Status":"Suspended"}' > "$S3_DIR/versioning.json";           expect_ng "版の管理を止めた" '版の管理が Enabled でない'
grep -q -- 'put-' "$ALOG" && ng "verify が書き込みの呼び出しをした"
grep -q -- "get-bucket-versioning --bucket ast-backup-test --output json --endpoint-url $EP" "$ALOG" || ng "check-bucket.sh が宛先を --endpoint-url で明示しない"
set +e; env -u AWS_ENDPOINT_URL AWS_ACCESS_KEY_ID=x bash "$HERE/check-bucket.sh" verify --bucket ast-backup-test >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -eq 2 ] || ng "宛先の無い環境で check-bucket.sh が 2 で止まらない"
ok "verify: 宣言どおりなら通り、lifecycle（日数・接頭辞・規則の増減）・既定の保持（期間・型・外し）・版の管理の変化を名指して落ちる。書き込まない"

# --- probe-writer ---
probe() { creds bash "$HERE/check-bucket.sh" probe-writer --bucket ast-backup-test; }
STUB_PUT=deny probe >/dev/null || ng "拒まれたときに probe-writer が通らない"
set +e; STUB_PUT=allow probe >/dev/null 2>"$W/out"; rc1=$?; STUB_PUT=other probe >/dev/null 2>&1; rc2=$?; set -e
[ "$rc1" -eq 1 ] && grep -q '書き込みの鍵で lifecycle を書けた' "$W/out" || ng "書き込みの鍵で書けたことを検出しない"
[ "$rc2" -eq 1 ] || ng "拒否でも成功でもない応答を通した"
grep -qF "put-bucket-lifecycle-configuration --lifecycle-configuration file://$HERE/bucket-lifecycle.json" "$ALOG" || ng "probe-writer が宣言と違う設定を書こうとした"
grep -qF "put-object-lock-configuration --object-lock-configuration file://$HERE/object-lock.json" "$ALOG" || ng "probe-writer が宣言と違う Object Lock を書こうとした"
set +e; env -u AWS_ACCESS_KEY_ID bash "$HERE/check-bucket.sh" verify --bucket ast-backup-test >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -eq 2 ] || ng "鍵の無い環境で verify が 2 で止まらない"
ok "probe-writer: 宣言の JSON だけを書こうとし、拒まれれば通り、書けた・判定できないときは落ちる"

# --- probe-writer: ずれていれば（読めなければ）書かない ---
probe_nowrite() {
  : > "$ALOG"
  set +e; STUB_PUT=allow probe >/dev/null 2>"$W/out"; rc=$?; set -e
  [ "$rc" -eq 1 ] && grep -q '書き込みを試さずに止めた' "$W/out" || ng "probe-writer が「$1」で止まらない（rc=$rc）"
  grep -q -- 'put-' "$ALOG" && ng "probe-writer が「$1」なのに書き込みの呼び出しをした"
  reset_bucket
}
mutate 'j=>{j.Rules[0].Expiration.Days=1}' lifecycle.json;                       probe_nowrite "lifecycle が宣言とずれている"
mutate 'j=>{j.ObjectLockConfiguration.Rule.DefaultRetention.Days=1}' lock.json; probe_nowrite "既定の保持が宣言とずれている"
export STUB_READ_FAIL=1;                                                          probe_nowrite "設定を読めない"; unset STUB_READ_FAIL
ok "probe-writer: 書く前に宣言と突き合わせ、ずれている・読めないときは何も書かずに 1 で止まる（ずれを宣言で上書きしない）"

echo "backup-owner-scripts.test.sh: ${pass} passed"
