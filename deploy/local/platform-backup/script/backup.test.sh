#!/usr/bin/env bash
# NFR-21, NFR-05, NFR-18, IADR-0471 (#1560): backup.sh の分岐を、pg_dump / pg_dumpall / psql / age を
# PATH 上のスタブへ差し替えて固定する。実 Postgres・実 Vault・実 age・実クラスタは要らない。
# Vault の回（IADR-0548 / #1868）は node の偽の秘匿管理（HTTP）を立てて、backup.sh の HTTP をそのまま通す。
#
#   - age スタブは「AGE[」＋ 標準入力 ＋「]」を -o のファイルへ書く（パイプが age を通ったことを中身で確かめる）
#   - 呼び出しの argv はすべて $STUB_LOG へ記録する（パスワードが引数に出ないことをここで確かめる）
#   - $STATE の印で失敗を注入する（psql-fail / dump-fail-<db> / age-fail / snap-*〔偽の秘匿管理の応答。下の Vault の節〕）
#
#   bash deploy/local/platform-backup/script/backup.test.sh
set -u

HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mktemp -d)"
STATE="$WORK/state"; mkdir -p "$STATE"
STUB_LOG="$WORK/stub.log"; : > "$STUB_LOG"
export STATE STUB_LOG
PASSED=0; FAILED=0
ok() { PASSED=$((PASSED + 1)); printf '  ok    %s\n' "$1"; }
ng() { FAILED=$((FAILED + 1)); printf '  NG    %s\n        %s\n' "$1" "$2"; }
assert_eq() { [ "$2" = "$3" ] && ok "$1" || ng "$1" "expected: $3 / actual: $2"; }
assert_ne() { [ "$2" != "$3" ] && ok "$1" || ng "$1" "expected NOT: $3"; }
assert_contains() { case "$2" in *"$3"*) ok "$1" ;; *) ng "$1" "expected to contain: $3" ;; esac; }
assert_missing() { case "$2" in *"$3"*) ng "$1" "expected NOT to contain: $3" ;; *) ok "$1" ;; esac; }
assert_file() { [ -f "$2" ] && ok "$1" || ng "$1" "missing file: $2"; }
assert_nofile() { [ ! -e "$2" ] && ok "$1" || ng "$1" "unexpected path: $2"; }

# ---- スタブ ------------------------------------------------------------------
mkdir -p "$WORK/bin"
# #2394: $STATE/pg-down-count（残りの回数）があり 0 より大きい間、または $STATE/pg-never がある間は、Postgres が立ち上がっていない
# 世界である。psql は名前解決の失敗で落ち、pg_isready は 2（応答なし）を返して残りの回数を 1 つ減らす。
cat > "$WORK/bin/pg_isready" <<'STUB'
#!/usr/bin/env bash
echo "pg_isready $*" >> "$STUB_LOG"
# #2395 のレビュー 🟡1: $STATE/pg-hang があれば、応答しない宛先（パケットを捨てる）として 20 秒止まる。
# （本物の pg_isready と同じく 1 つのプロセスとして止まる。exec して、止められたときに子を残さない。）
[ -f "$STATE/pg-hang" ] && exec sleep 20
[ -f "$STATE/pg-never" ] && exit 2
if [ -f "$STATE/pg-down-count" ]; then
  n="$(cat "$STATE/pg-down-count")"
  if [ "$n" -gt 0 ]; then echo $((n - 1)) > "$STATE/pg-down-count"; exit 2; fi
fi
exit 0
STUB
cat > "$WORK/bin/psql" <<'STUB'
#!/usr/bin/env bash
echo "psql $*" >> "$STUB_LOG"
[ -f "$STATE/psql-fail" ] && exit 2
if [ -f "$STATE/pg-never" ] || { [ -f "$STATE/pg-down-count" ] && [ "$(cat "$STATE/pg-down-count")" -gt 0 ]; }; then
  echo 'psql: error: could not translate host name "postgres" to address: Try again' >&2; exit 2
fi
printf 'audit_svc\npostgres\nrisk_management_svc\n'
STUB
cat > "$WORK/bin/pg_dump" <<'STUB'
#!/usr/bin/env bash
echo "pg_dump $*" >> "$STUB_LOG"
db=""; while [ $# -gt 0 ]; do case "$1" in -d) db="$2"; shift 2 ;; *) shift ;; esac; done
[ -f "$STATE/dump-fail-$db" ] && { echo "pg_dump: error: stub failure" >&2; exit 1; }
printf 'PGDMP-%s' "$db"
STUB
cat > "$WORK/bin/pg_dumpall" <<'STUB'
#!/usr/bin/env bash
echo "pg_dumpall $*" >> "$STUB_LOG"
printf 'GLOBALS'
STUB
cat > "$WORK/bin/age" <<'STUB'
#!/usr/bin/env bash
echo "age $*" >> "$STUB_LOG"
out=""
while [ $# -gt 0 ]; do
  case "$1" in
    -o) out="$2"; shift 2 ;;
    -R) [ -r "$2" ] || exit 1; shift 2 ;;
    *) shift ;;
  esac
done
[ -n "$out" ] || exit 1
[ -f "$STATE/age-fail" ] && { cat > /dev/null; exit 1; }
{ printf 'AGE['; cat; printf ']'; } > "$out"
STUB
# cp は既定で本物を呼ぶ。$STATE/corrupt-copy があれば、写した先の暗号文を 1 つ書き換える
# （保管先への写しが壊れた世界。publish が SHA256SUMS で検証せずに改名すると、壊れた回が残る）。
REAL_CP="$(command -v cp)"
# #2389: $STATE/pause-cp-<BACKUP_TEST_ID> があれば、保管先への写しの後に 1 度だけ止まる（$STATE/paused-<ID> を置き、
# $STATE/release-<ID> が置かれるまで待つ）。並行する 2 本の回の順序を固定するための止め所である。
cat > "$WORK/bin/cp" <<STUB
#!/usr/bin/env bash
"$REAL_CP" "\$@" || exit \$?
if [ -f "\$STATE/corrupt-copy" ]; then
  dest="\${!#}"
  for f in "\$dest"/*.age; do [ -f "\$f" ] && { printf 'X' >> "\$f"; break; }; done
fi
id="\${BACKUP_TEST_ID:-}"
if [ -n "\$id" ] && [ -f "\$STATE/pause-cp-\$id" ]; then
  rm -f "\$STATE/pause-cp-\$id"
  : > "\$STATE/paused-\$id"
  for _ in \$(seq 1 600); do [ -f "\$STATE/release-\$id" ] && break; sleep 0.1; done
fi
exit 0
STUB
# #2389（レビュー 🟡1）: $STATE/pause-rm-<BACKUP_TEST_ID> があれば、ロックの持ち主のファイル（.publish.lock/owner）を
# 消す直前に 1 度だけ止まる。古いロックを 2 本が同時に壊す順序を固定するための止め所である。
REAL_RM="$(command -v rm)"
cat > "$WORK/bin/rm" <<STUB
#!/usr/bin/env bash
id="\${BACKUP_TEST_ID:-}"
if [ -n "\$id" ] && [ -f "\$STATE/pause-rm-\$id" ]; then
  for a in "\$@"; do
    case "\$a" in
      */.publish.lock/owner)
        "$REAL_RM" -f "\$STATE/pause-rm-\$id"
        : > "\$STATE/paused-rm-\$id"
        for _ in \$(seq 1 600); do [ -f "\$STATE/release-rm-\$id" ] && break; sleep 0.1; done
        ;;
    esac
  done
fi
exec "$REAL_RM" "\$@"
STUB
# #2389: $STATE/drop-after-mv があれば、incoming を回の名前へ改名した直後に、改名の先から暗号文を 1 つ消す
# （改名の後に中身が欠けた世界。9p の上で、改名の後に別の回の削除が同じ実体を消した事象の写し）。
REAL_MV="$(command -v mv)"
cat > "$WORK/bin/mv" <<STUB
#!/usr/bin/env bash
"$REAL_MV" "\$@" || exit \$?
if [ -f "\$STATE/drop-after-mv" ]; then
  src="\${@: -2:1}"; dest="\${!#}"
  case "\$src" in
    */.incoming-*) for f in "\$dest"/*.age; do [ -f "\$f" ] && { rm -f "\$f"; break; }; done ;;
  esac
fi
exit 0
STUB
chmod +x "$WORK/bin/"*
export PATH="$WORK/bin:$PATH"

# 公開鍵の形をした**試験用の値**（実在の鍵ではない。bech32 の文字集合で 58 文字）。
FAKE_RECIPIENT="age1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq"
SECRET_PW="stub-pg-password-DO-NOT-LEAK"
export PGPASSWORD="$SECRET_PW"

# 🔴 試験の作業場は mktemp の下に**ケースごとに新しく作る**（前のケースを再帰削除しない）。
CASE=0
new_env() {
	CASE=$((CASE + 1))
	R="$WORK/case$CASE"
	mkdir -p "$R/c" "$R/e" "$R/staging" "$R/vault"
	: > "$R/c/.platform-backup-target"
	: > "$R/e/.platform-backup-target"
	printf '# test recipients\n\n%s\n' "$FAKE_RECIPIENT" > "$R/recipients.txt"
	rm -f "$STATE"/*
	: > "$STUB_LOG"
	export BACKUP_TARGETS="$R/c $R/e"
	export BACKUP_STAGING="$R/staging"
	export BACKUP_RECIPIENTS_FILE="$R/recipients.txt"
	export VAULT_DATA_DIR="$R/vault"
	export BACKUP_NOW="2026-09-26T030000Z"
	export BACKUP_KIND=postgres
	# #2394: 依存先の待ちは短くする（既定 600 秒。届かない世界の試験が試験の時間を食わないように）。
	export BACKUP_DEPS_WAIT_SECONDS=2 BACKUP_DEPS_WAIT_INTERVAL=0.1
}
run_backup() { OUT="$(bash "$HERE/backup.sh" 2>&1)"; RC=$?; }

# ---- A5: 受取人が不在・占位・不正なら何もしない（fail-closed） -------------------
new_env
rm -f "$R/recipients.txt"
run_backup
assert_ne 'T-1560-01 受取人ファイル不在: 失敗で終わる' "$RC" "0"
assert_missing 'T-1560-01 受取人ファイル不在: pg_dump を呼ばない' "$(cat "$STUB_LOG")" 'pg_dump'
assert_nofile 'T-1560-01 受取人ファイル不在: 保管先に何も書かない' "$R/c/postgres"

new_env
cp "$HERE/../age-recipients.example.txt" "$R/recipients.txt"
run_backup
assert_ne 'T-1560-02 リポジトリの例示ファイル（占位）のまま: 失敗で終わる' "$RC" "0"
assert_missing 'T-1560-02 占位のまま: pg_dump を呼ばない' "$(cat "$STUB_LOG")" 'pg_dump'
assert_nofile 'T-1560-02 占位のまま: 保管先に何も書かない' "$R/e/postgres"

new_env
printf '# only comments\n\n' > "$R/recipients.txt"
run_backup
assert_ne 'T-1560-03 公開鍵が 1 行も無い: 失敗で終わる' "$RC" "0"

new_env
printf '%s\nage1short\n' "$FAKE_RECIPIENT" > "$R/recipients.txt"
run_backup
assert_ne 'T-1560-04 正しい鍵に不正な行が混ざる: 失敗で終わる' "$RC" "0"
assert_missing 'T-1560-04 不正な行: pg_dump を呼ばない' "$(cat "$STUB_LOG")" 'pg_dump'

# age は前後の空白を削らずに読む。検査器も同じに読み、失敗の原因として受取人ファイルを名指しする。
for variant in 'lead' 'trail' 'blank' 'indented-comment'; do
	new_env
	case "$variant" in
		lead) printf ' %s\n' "$FAKE_RECIPIENT" > "$R/recipients.txt" ;;
		trail) printf '%s \n' "$FAKE_RECIPIENT" > "$R/recipients.txt" ;;
		blank) printf '%s\n   \n' "$FAKE_RECIPIENT" > "$R/recipients.txt" ;;
		indented-comment) printf '%s\n  # note\n' "$FAKE_RECIPIENT" > "$R/recipients.txt" ;;
	esac
	run_backup
	assert_ne "T-1560-26 受取人の行の前後に空白（$variant）: 失敗で終わる" "$RC" "0"
	assert_missing "T-1560-26 受取人の行の前後に空白（$variant）: pg_dump を呼ばない" "$(cat "$STUB_LOG")" 'pg_dump'
	assert_contains "T-1560-26 受取人の行の前後に空白（$variant）: 受取人ファイルを名指しする" "$OUT" "$R/recipients.txt"
	assert_contains "T-1560-26 受取人の行の前後に空白（$variant）: 原因が空白だと告げる" "$OUT" '前後に空白があります'
done

# ---- A1 / A2: 正常系（2 か所へ同じ回が並び、中身は age を通っている） ---------------
new_env
printf '%s\r\n' "$FAKE_RECIPIENT" > "$R/recipients.txt"   # CRLF でも読める（Windows で作ったファイル）
run_backup
assert_eq 'T-1560-05 正常系: 成功で終わる' "$RC" "0"
for t in c e; do
	d="$R/$t/postgres/2026-09-26T030000Z"
	for f in pg-globals.sql.age pg-audit_svc.dump.age pg-postgres.dump.age pg-risk_management_svc.dump.age SHA256SUMS; do
		assert_file "T-1560-05 正常系: $t に $f が並ぶ" "$d/$f"
	done
	assert_eq "T-1560-05 正常系: $t の DB ダンプは age を通っている" "$(cat "$d/pg-audit_svc.dump.age")" 'AGE[PGDMP-audit_svc]'
	assert_eq "T-1560-05 正常系: $t の globals は age を通っている" "$(cat "$d/pg-globals.sql.age")" 'AGE[GLOBALS]'
	assert_eq "T-1560-05 正常系: $t の SHA256SUMS が検証できる" "$(cd "$d" && sha256sum -c SHA256SUMS >/dev/null 2>&1; echo $?)" "0"
	assert_eq "T-1560-05 正常系: $t に incoming が残らない" "$(ls -A "$R/$t/postgres" | grep -c '^\.incoming' )" "0"
done
assert_contains 'T-1560-05 正常系: DB ごとに pg_dump -Fc を呼ぶ' "$(cat "$STUB_LOG")" 'pg_dump -Fc -d risk_management_svc'
assert_contains 'T-1560-05 正常系: pg_dumpall --globals-only を呼ぶ' "$(cat "$STUB_LOG")" 'pg_dumpall --globals-only'
assert_contains 'T-1560-05 正常系: 非テンプレートかつ接続可の DB を列挙する' "$(cat "$STUB_LOG")" 'NOT datistemplate AND datallowconn'
assert_contains 'T-1560-05 正常系: age は受取人ファイルで暗号化する（-R）' "$(cat "$STUB_LOG")" "age -R $R/recipients.txt"
assert_missing 'T-1560-06 パスワードがどのコマンドの引数にも出ない' "$(cat "$STUB_LOG")" "$SECRET_PW"
assert_missing 'T-1560-06 パスワードがログ（標準出力・標準エラー）に出ない' "$OUT" "$SECRET_PW"
assert_eq 'T-1560-06 staging に何も残らない（平文も暗号文も）' "$(ls -A "$R/staging")" ""
PLAIN="$(grep -rl --exclude=SHA256SUMS -e 'PGDMP' "$R/c" "$R/e" 2>/dev/null | while read -r f; do head -c 4 "$f" | grep -qv 'AGE\[' && echo "$f"; done)"
assert_eq 'T-1560-06 保管先に平文のダンプが無い（すべて age を通っている）' "$PLAIN" ""

# ---- A3: 片方に書けなくても、もう片方には書き、失敗として終わる ---------------------
new_env
rm -f "$R/e/.platform-backup-target"
run_backup
assert_ne 'T-1560-07 目印の無い保管先（ドライブ外れ）: 失敗で終わる' "$RC" "0"
assert_file 'T-1560-07 目印の無い保管先: もう片方（C）には書く' "$R/c/postgres/2026-09-26T030000Z/pg-audit_svc.dump.age"
assert_nofile 'T-1560-07 目印の無い保管先: そこには書かない' "$R/e/postgres"
assert_contains 'T-1560-07 目印の無い保管先: 理由をログに出す' "$OUT" '.platform-backup-target'

new_env
: > "$R/c/postgres"   # ディレクトリを作れない（ファイルが居座っている）
run_backup
assert_ne 'T-1560-08 書けない保管先: 失敗で終わる' "$RC" "0"
assert_file 'T-1560-08 書けない保管先: もう片方（E）には書く' "$R/e/postgres/2026-09-26T030000Z/pg-postgres.dump.age"

new_env
rm -f "$R/c/.platform-backup-target" "$R/e/.platform-backup-target"
run_backup
assert_ne 'T-1560-09 どちらにも書けない: 失敗で終わる' "$RC" "0"
assert_contains 'T-1560-09 どちらにも書けない: そう告げる' "$OUT" 'どの保管先にも書けませんでした'

new_env
: > "$STATE/corrupt-copy"
run_backup
assert_ne 'T-1560-27 保管先への写しが壊れた: 失敗で終わる（SHA256SUMS で検証してから改名する）' "$RC" "0"
assert_nofile 'T-1560-27 保管先への写しが壊れた: 壊れた回を C に残さない' "$R/c/postgres/2026-09-26T030000Z"
assert_nofile 'T-1560-27 保管先への写しが壊れた: 壊れた回を E に残さない' "$R/e/postgres/2026-09-26T030000Z"
assert_eq 'T-1560-27 保管先への写しが壊れた: incoming を残さない' "$(ls -A "$R/c/postgres" | grep -c '^\.incoming')" "0"

# ---- #2389: 同じ種類の回が並行して走る（手動の Job と CronJob の回が同じ秒に重なった） ---------------
# 2 本は Pod ごとに別の作業場（emptyDir）を持つので、試験でも staging を分ける。保管先と回の名前は同じである。
wait_file() { local f="$1" i; for i in $(seq 1 300); do [ -f "$f" ] && return 0; sleep 0.1; done; return 1; }
count_age() { local n=0 f; for f in "$1"/*.age; do [ -f "$f" ] && n=$((n + 1)); done; echo "$n"; }

new_env
mkdir -p "$R/staging-a" "$R/staging-b"
: > "$STATE/pause-cp-A"
( BACKUP_TEST_ID=A BACKUP_STAGING="$R/staging-a" bash "$HERE/backup.sh" > "$R/out-a" 2>&1; echo $? > "$R/rc-a" ) &
PID_A=$!
if wait_file "$STATE/paused-A"; then ok 'T-2389-01 並行: 回 A が C への写しの途中で止まる（順序の固定）'; else ng 'T-2389-01 並行: 回 A が C への写しの途中で止まる（順序の固定）' 'paused-A が来ない'; fi
INC_A="$(ls -d "$R/c/postgres"/.incoming-* 2>/dev/null | head -n 1)"
N_A="$(count_age "$INC_A")"
( BACKUP_TEST_ID=B BACKUP_STAGING="$R/staging-b" BACKUP_LOCK_WAIT_SECONDS=30 bash "$HERE/backup.sh" > "$R/out-b" 2>&1; echo $? > "$R/rc-b" ) &
PID_B=$!
sleep 2
assert_eq 'T-2389-02 並行: 後から来た回 B は、書いている途中の回 A の incoming を消さない' "$( [ -n "$INC_A" ] && [ -d "$INC_A" ] && count_age "$INC_A" )" "$N_A"
: > "$STATE/release-A"
wait "$PID_A" "$PID_B"
assert_eq 'T-2389-03 並行: 先に書いた回 A は成功で終わる' "$(cat "$R/rc-a")" "0"
assert_ne 'T-2389-03 並行: 同じ名前の回 B は失敗で終わる（書いていない）' "$(cat "$R/rc-b")" "0"
assert_missing 'T-2389-03 並行: 回 B は「完了」を出さない' "$(cat "$R/out-b")" '完了（保管先'
for t in c e; do
	d="$R/$t/postgres/2026-09-26T030000Z"
	assert_eq "T-2389-04 並行: $t の回は暗号文が欠けていない" "$(count_age "$d")" "4"
	assert_eq "T-2389-04 並行: $t の回の SHA256SUMS が検証できる" "$(cd "$d" 2>/dev/null && sha256sum -c SHA256SUMS >/dev/null 2>&1; echo $?)" "0"
	assert_eq "T-2389-04 並行: $t に incoming が残らない" "$(ls -A "$R/$t/postgres" | grep -c '^\.incoming')" "0"
	assert_eq "T-2389-04 並行: $t にロックが残らない" "$(ls -A "$R/$t/postgres" | grep -c '^\.publish\.lock')" "0"
done

# 改名の後に中身が欠けた回を「完了」と記録しない（改名の後に保管先の回でもう一度検算する）。
new_env
: > "$STATE/drop-after-mv"
run_backup
assert_ne 'T-2389-05 改名の後に欠けた: 失敗で終わる' "$RC" "0"
assert_missing 'T-2389-05 改名の後に欠けた: 「完了」を出さない' "$OUT" '完了（保管先'
assert_missing 'T-2389-05 改名の後に欠けた: 「書きました」を出さない' "$OUT" '書きました'
assert_nofile 'T-2389-05 改名の後に欠けた: 回の名前のまま残さない（保持の「完全な回」に数えない）' "$R/c/postgres/2026-09-26T030000Z"
assert_eq 'T-2389-05 改名の後に欠けた: broken- へ退かせて残す（調べられるように）' "$(ls -A "$R/c/postgres" | grep -c '^broken-2026-09-26T030000Z')" "1"
BROKEN="$(ls -A "$R/c/postgres" | grep '^broken-' | head -n 1)"
if [ -n "$BROKEN" ] && [[ "$BROKEN" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{6}Z(-partial|-keep)?$ ]]; then ng 'T-2389-05 改名の後に欠けた: 退かせた名前は回の名前の規則に合わない' "$BROKEN"; else ok 'T-2389-05 改名の後に欠けた: 退かせた名前は回の名前の規則に合わない'; fi

# ロック: 生きた別の回のロックは壊さず、待ち切れたらその保管先は失敗にする。古いロックは壊して取り直す。
new_env
mkdir -p "$R/c/postgres/.publish.lock"
printf 'other-host 4242 %s\n' "$(date +%s)" > "$R/c/postgres/.publish.lock/owner"
BACKUP_LOCK_WAIT_SECONDS=2 run_backup
assert_ne 'T-2389-06 生きたロック: 待ち切れたら失敗で終わる' "$RC" "0"
assert_contains 'T-2389-06 生きたロック: 理由をログに出す' "$OUT" 'ロックを取れません'
assert_nofile 'T-2389-06 生きたロック: その保管先には書かない' "$R/c/postgres/2026-09-26T030000Z"
assert_file 'T-2389-06 生きたロック: もう片方（E）には書く' "$R/e/postgres/2026-09-26T030000Z/SHA256SUMS"
assert_file 'T-2389-06 生きたロック: 他の回のロックを消さない' "$R/c/postgres/.publish.lock/owner"

new_env
mkdir -p "$R/c/postgres/.publish.lock"
printf 'gone-host 4242 1000\n' > "$R/c/postgres/.publish.lock/owner"
mkdir -p "$R/c/postgres/.incoming-2026-09-25T030000Z"; echo x > "$R/c/postgres/.incoming-2026-09-25T030000Z/pg-a.dump.age"
BACKUP_LOCK_WAIT_SECONDS=2 run_backup
assert_eq 'T-2389-07 古いロック（強制終了の残骸）: 壊して取り直し、成功で終わる' "$RC" "0"
assert_contains 'T-2389-07 古いロック: 壊したことをログに出す' "$OUT" '古いロック'
assert_eq 'T-2389-07 古いロック: ロックを残さない' "$(ls -A "$R/c/postgres" | grep -c '^\.publish\.lock')" "0"
assert_eq 'T-2389-07 古いロック: ロックの中で前回の incoming の残骸を片付ける' "$(ls -A "$R/c/postgres" | grep -c '^\.incoming')" "0"

# 持ち主のファイルが無いロック（mkdir の直後・owner を書く前の強制終了。2 回目のレビュー 🟡1）: 持ち主の時刻が読めないときは、
# ロックのディレクトリの更新時刻で古さを読む。閾値を過ぎていれば壊して取り直す（永久に待って失敗しない）。
new_env
mkdir -p "$R/c/postgres/.publish.lock"
touch -d '@1000' "$R/c/postgres/.publish.lock"
BACKUP_LOCK_WAIT_SECONDS=2 run_backup
assert_eq 'T-2389-09 持ち主の無い古いロック: 壊して取り直し、成功で終わる' "$RC" "0"
assert_contains 'T-2389-09 持ち主の無い古いロック: 壊したことをログに出す' "$OUT" '古いロック'
assert_eq 'T-2389-09 持ち主の無い古いロック: ロックを残さない' "$(ls -A "$R/c/postgres" | grep -c '^\.publish\.lock')" "0"
# 対照: 持ち主の無い新しいロック（取った直後で、まだ owner を書いていない回）は壊さない。
new_env
mkdir -p "$R/c/postgres/.publish.lock"
BACKUP_LOCK_WAIT_SECONDS=2 run_backup
assert_ne 'T-2389-09 持ち主の無い新しいロック: 壊さずに待ち、待ち切れたら失敗で終わる' "$RC" "0"
[ -d "$R/c/postgres/.publish.lock" ] && ok 'T-2389-09 持ち主の無い新しいロック: 消さない' || ng 'T-2389-09 持ち主の無い新しいロック: 消さない' 'removed'

# 古いロックを 2 本が同時に壊す（レビュー 🟡1）: 先に古いと見た回 B が、消す直前で止まっている間に、回 A も古いと見て壊しにくる。
# B が再開しても、A が取ったばかりのロックを消してはならない（消すと 2 本が同時に書き、A の書きかけを B が片付けで消す）。
# どちらがどの保管先へ書くかは順序で変わる（B が C・A が E に書く回もある）ので、成否の組ではなく次の不変を確かめる:
# 書き込みの途中で失敗した回が無い・保管先ごとに書いた回はちょうど 1 本・保管先の回は検算が通る・ロックも破棄の印も残らない。
new_env
mkdir -p "$R/staging-a" "$R/staging-b"
mkdir -p "$R/c/postgres/.publish.lock"
printf 'gone-host 4242 1000\n' > "$R/c/postgres/.publish.lock/owner"
: > "$STATE/pause-rm-B"
( BACKUP_TEST_ID=B BACKUP_STAGING="$R/staging-b" BACKUP_LOCK_WAIT_SECONDS=30 bash "$HERE/backup.sh" > "$R/out-b" 2>&1; echo $? > "$R/rc-b" ) &
PID_B=$!
if wait_file "$STATE/paused-rm-B"; then ok 'T-2389-08 古いロックの同時の破棄: 回 B が古いロックを消す直前で止まる（順序の固定）'; else ng 'T-2389-08 古いロックの同時の破棄: 回 B が古いロックを消す直前で止まる（順序の固定）' 'paused-rm-B が来ない'; fi
: > "$STATE/pause-cp-A"
( BACKUP_TEST_ID=A BACKUP_STAGING="$R/staging-a" BACKUP_LOCK_WAIT_SECONDS=30 bash "$HERE/backup.sh" > "$R/out-a" 2>&1; echo $? > "$R/rc-a" ) &
PID_A=$!
# 修正の前は、A が古いロックを壊して取り、写しの途中で止まる。修正の後は、A は B の破棄が終わるまで待つ（止まらない）。
for _ in $(seq 1 30); do [ -f "$STATE/paused-A" ] && break; sleep 0.1; done
: > "$STATE/release-rm-B"
sleep 2
: > "$STATE/release-A"
wait "$PID_A" "$PID_B"
BOTH="$(cat "$R/out-a" "$R/out-b")"
assert_missing 'T-2389-08 古いロックの同時の破棄: 書き込みの途中で失敗した回が無い（相手の取りたてのロックを消していない）' "$BOTH" '保管先へ書けませんでした'
for t in c e; do
	assert_eq "T-2389-08 古いロックの同時の破棄: $t へ書いた回はちょうど 1 本" "$(printf '%s\n' "$BOTH" | grep -c "書きました: $R/$t/postgres/")" "1"
	d="$R/$t/postgres/2026-09-26T030000Z"
	assert_eq "T-2389-08 古いロックの同時の破棄: $t の回は暗号文が欠けていない" "$(count_age "$d")" "4"
	assert_eq "T-2389-08 古いロックの同時の破棄: $t の回の SHA256SUMS が検証できる" "$(cd "$d" 2>/dev/null && sha256sum -c SHA256SUMS >/dev/null 2>&1; echo $?)" "0"
	assert_eq "T-2389-08 古いロックの同時の破棄: $t にロックも破棄の印も残らない" "$(ls -A "$R/$t/postgres" | grep -c '^\.publish\.lock')" "0"
done

# ---- #2394: 書き始める前に依存先の起動を上限つきで待つ（PC の復帰の直後の後追いの回） -----------------------
new_env
echo 3 > "$STATE/pg-down-count"
BACKUP_DEPS_WAIT_SECONDS=20 BACKUP_DEPS_WAIT_INTERVAL=0.1 run_backup
assert_eq 'T-2394-01 Postgres が初めは立ち上がっておらず、待つうちに立ち上がる: 成功で終わる' "$RC" "0"
assert_file 'T-2394-01 Postgres を待った回: 回が揃う' "$R/c/postgres/2026-09-26T030000Z/SHA256SUMS"
assert_contains 'T-2394-01 Postgres を待った回: 待ったことをログに出す' "$OUT" '起動を待ちます'
assert_contains 'T-2394-01 Postgres を待った回: 立ち上がったことをログに出す' "$OUT" '立ち上がりました'

new_env
: > "$STATE/pg-never"
BACKUP_DEPS_WAIT_SECONDS=1 BACKUP_DEPS_WAIT_INTERVAL=0.1 run_backup
assert_ne 'T-2394-02 Postgres が上限までに立ち上がらない: 失敗で終わる' "$RC" "0"
assert_contains 'T-2394-02 Postgres が立ち上がらない: 理由をログに出す' "$OUT" '立ち上がりませんでした'
assert_nofile 'T-2394-02 Postgres が立ち上がらない: C に何も書かない（fail-closed）' "$R/c/postgres"
assert_nofile 'T-2394-02 Postgres が立ち上がらない: E に何も書かない' "$R/e/postgres"
assert_eq 'T-2394-02 Postgres が立ち上がらない: staging に何も作らない' "$(ls -A "$R/staging")" ""
assert_missing 'T-2394-02 Postgres が立ち上がらない: 生成器（pg_dump）を呼ばない' "$(cat "$STUB_LOG")" 'pg_dump '

# 1 回の確かめにも上限がある（#2395 のレビュー 🟡1）: 応答しない宛先で、確かめが OS の接続の時間切れまで止まらない。
new_env
: > "$STATE/pg-hang"
T0=$(date +%s)
BACKUP_DEPS_WAIT_SECONDS=1 BACKUP_DEPS_PROBE_TIMEOUT=1 run_backup
EL=$(( $(date +%s) - T0 ))
rm -f "$STATE/pg-hang"
assert_ne 'T-2394-07 応答しない Postgres: 失敗で終わる' "$RC" "0"
[ "$EL" -lt 10 ] && ok "T-2394-07 応答しない Postgres: 1 回の確かめが上限で戻り、待ちの上限の近くで終わる（${EL} 秒）" || ng 'T-2394-07 応答しない Postgres: 1 回の確かめが上限で戻る' "${EL} 秒かかった"
assert_nofile 'T-2394-07 応答しない Postgres: 何も書かない' "$R/c/postgres"

# 書き込みの途中の失敗を再試行しない（backoffLimit: 0 の意図）。失敗した DB を 2 度呼ばない。
new_env
: > "$STATE/dump-fail-audit_svc"
run_backup
assert_ne 'T-2394-03 書き込みの途中の失敗: 失敗で終わる（-partial として書く）' "$RC" "0"
assert_eq 'T-2394-03 書き込みの途中の失敗: 失敗した DB の pg_dump を 1 度しか呼ばない（再試行しない）' "$(grep -c 'pg_dump -Fc -d audit_svc' "$STUB_LOG")" "1"
assert_eq 'T-2394-03 書き込みの途中の失敗: DB の一覧も 1 度しか取らない（全体をやり直さない）' "$(grep -c '^psql ' "$STUB_LOG")" "1"

# ---- 生成の失敗 ------------------------------------------------------------------
new_env
: > "$STATE/dump-fail-audit_svc"
run_backup
assert_ne 'T-1560-10 1 DB の pg_dump が失敗: 失敗で終わる' "$RC" "0"
assert_file 'T-1560-10 1 DB の失敗: 他の DB は -partial の回として書く' "$R/c/postgres/2026-09-26T030000Z-partial/pg-postgres.dump.age"
assert_nofile 'T-1560-10 1 DB の失敗: 失敗した DB の成果物は置かない' "$R/c/postgres/2026-09-26T030000Z-partial/pg-audit_svc.dump.age"
assert_nofile 'T-1560-10 1 DB の失敗: 完全な回の名前は使わない' "$R/c/postgres/2026-09-26T030000Z"

new_env
: > "$STATE/psql-fail"
run_backup
assert_ne 'T-1560-11 DB の一覧を取れない: 失敗で終わる' "$RC" "0"
assert_nofile 'T-1560-11 DB の一覧を取れない: 何も書かない' "$R/c/postgres"

new_env
: > "$STATE/age-fail"
run_backup
assert_ne 'T-1560-12 age が失敗: 失敗で終わる' "$RC" "0"
assert_nofile 'T-1560-12 age が失敗: 何も書かない' "$R/c/postgres"
assert_eq 'T-1560-12 age が失敗: staging に一時ファイルを残さない' "$(ls -A "$R/staging")" ""

new_env
export BACKUP_KIND=nonsense
run_backup
assert_ne 'T-1560-13 BACKUP_KIND が不正: 失敗で終わる' "$RC" "0"

# ---- Vault（IADR-0548 / #1868: raft の整合したスナップショット） ------------------------------
# 偽の秘匿管理（node の HTTP サーバ）を立て、backup.sh の /dev/tcp の HTTP をそのまま通す。
#   - POST /v1/auth/kubernetes/login … role と JWT が期待どおりなら 1 回きりのトークンを返す。違えば 403
#   - GET  /v1/sys/storage/raft/snapshot … トークンが一致すれば $STATE の印に応じた本文を返す（既定は整合したスナップショット）
#       snap-500 / snap-empty / snap-truncated / snap-badsum / snap-nostate / snap-notgz
#   - トークンは 1 回使うと無効（role の token_num_uses=1 と同じ振る舞い）
#   - 受けた要求の行（メソッド・パス・HTTP の版・ヘッダ名）を $WORK/vault-requests.log へ書く
# 🔴 JWT・トークンの形をした文字列をリポジトリに置かない（秘密の走査〔gitleaks〕が本物と見分けられない）。試験の中で組み立てる。
b64url() { printf '%s' "$1" | base64 | tr -d '=\n' | tr '/+' '_-'; }
fake_jwt() { printf '%s.%s.%s' "$(b64url '{"alg":"RS256"}')" "$(b64url "{\"sub\":\"$1\"}")" "$2"; }
FAKE_JWT="$(fake_jwt 'system:serviceaccount:platform-infra:platform-backup-vault' STUB-JWT-SIGNATURE)"
FAKE_VAULT_TOKEN="stub-snapshot-token-do-not-leak"
SNAPDIR="$WORK/snap-src"; mkdir -p "$SNAPDIR/ok" "$SNAPDIR/badsum" "$SNAPDIR/nostate"
printf '{"Version":1,"ID":"bolt-snapshot","Index":40}' > "$SNAPDIR/ok/meta.json"
printf 'STATE-BIN-%s' "$(seq 1 200 | tr -d '\n')" > "$SNAPDIR/ok/state.bin"
(cd "$SNAPDIR/ok" && sha256sum meta.json state.bin > SHA256SUMS && tar -czf ../ok.snap meta.json state.bin SHA256SUMS)
cp "$SNAPDIR/ok/meta.json" "$SNAPDIR/ok/state.bin" "$SNAPDIR/badsum/"
(cd "$SNAPDIR/badsum" && sha256sum meta.json state.bin > SHA256SUMS && printf 'X' >> state.bin && tar -czf ../badsum.snap meta.json state.bin SHA256SUMS)
cp "$SNAPDIR/ok/meta.json" "$SNAPDIR/ok/SHA256SUMS" "$SNAPDIR/nostate/"
(cd "$SNAPDIR/nostate" && tar -czf ../nostate.snap meta.json SHA256SUMS)
head -c 100 "$SNAPDIR/ok.snap" > "$SNAPDIR/truncated.snap"
printf 'not a snapshot' > "$SNAPDIR/notgz.snap"
cat > "$WORK/fake-vault.js" <<'JS'
const http = require('http');
const fs = require('fs');
const path = require('path');
const [portFile, state, snapdir, reqLog, jwt, token] = process.argv.slice(2);
let tokenUses = 0;
const has = (m) => fs.existsSync(path.join(state, m));
http.createServer((req, res) => {
  let body = '';
  req.on('data', (c) => (body += c));
  req.on('end', () => {
    fs.appendFileSync(reqLog, `${req.method} ${req.url} HTTP/${req.httpVersion} [${Object.keys(req.headers).sort().join(',')}]\n`);
    const json = (code, obj) => { res.writeHead(code, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(obj)); };
    // #2394: vault-down-count（残りの回数）が 0 より大きい間、または vault-never がある間は、立ち上がっていない秘匿管理である
    // （再起動の直後で sealed）。どの要求にも 503 を返し、sys/health は残りの回数を 1 つ減らす。
    // #2395 のレビュー 🟡1: vault-hang があれば、sys/health に答えない（接続は受けるが応答を返さない宛先）。
    if (req.url === '/v1/sys/health' && has('vault-hang')) return;
    const downFile = path.join(state, 'vault-down-count');
    const down = has('vault-never') || (fs.existsSync(downFile) && Number(fs.readFileSync(downFile, 'utf8')) > 0);
    if (down) {
      if (req.url === '/v1/sys/health' && fs.existsSync(downFile)) fs.writeFileSync(downFile, String(Number(fs.readFileSync(downFile, 'utf8')) - 1));
      return json(503, { errors: ['Vault is sealed'] });
    }
    if (req.method === 'GET' && req.url === '/v1/sys/health') return json(200, { initialized: true, sealed: false, standby: false });
    if (req.method === 'POST' && req.url === '/v1/auth/kubernetes/login') {
      let b = {};
      try { b = JSON.parse(body); } catch (e) { return json(400, { errors: ['bad json'] }); }
      if (b.role !== 'platform-backup-vault' || b.jwt !== jwt) return json(403, { errors: ['permission denied'] });
      tokenUses = 1;
      return json(200, { auth: { client_token: token, policies: ['platform-backup-raft-snapshot'], num_uses: 1 } });
    }
    if (req.method === 'GET' && req.url === '/v1/sys/storage/raft/snapshot') {
      if (req.headers['x-vault-token'] !== token || tokenUses < 1) return json(403, { errors: ['permission denied'] });
      tokenUses -= 1;
      if (has('snap-500')) return json(500, { errors: ['raft is not ready'] });
      const pick = ['empty', 'truncated', 'badsum', 'nostate', 'notgz'].find((m) => has(`snap-${m}`));
      res.writeHead(200, { 'Content-Type': 'application/gzip' });
      if (pick === 'empty') return res.end();
      return res.end(fs.readFileSync(path.join(snapdir, `${pick || 'ok'}.snap`)));
    }
    json(404, { errors: ['no handler'] });
  });
}).listen(0, '127.0.0.1', function () { fs.writeFileSync(portFile, String(this.address().port)); });
JS
FAKE_VAULT_PID=""
if command -v node >/dev/null 2>&1; then
	node "$WORK/fake-vault.js" "$WORK/fake-vault.port" "$STATE" "$SNAPDIR" "$WORK/vault-requests.log" "$FAKE_JWT" "$FAKE_VAULT_TOKEN" &
	FAKE_VAULT_PID=$!
	trap '[ -n "$FAKE_VAULT_PID" ] && kill "$FAKE_VAULT_PID" 2>/dev/null' EXIT
	for _ in $(seq 1 50); do [ -s "$WORK/fake-vault.port" ] && break; sleep 0.1; done
fi
if [ ! -s "$WORK/fake-vault.port" ]; then
	# 🔴 node が無い・偽の秘匿管理が立たないなら、黙って飛ばさず失敗にする（CI のランナーは node を持つ）。
	ng 'T-1868-00 偽の秘匿管理（node）を立てられる' 'node が無いか、偽の秘匿管理が起動しなかった'
	FAKE_VAULT_ADDR="http://127.0.0.1:1"
else
	ok 'T-1868-00 偽の秘匿管理（node）を立てられる'
	FAKE_VAULT_ADDR="http://127.0.0.1:$(cat "$WORK/fake-vault.port")"
fi

new_vault_env() {
	new_env
	export BACKUP_KIND=vault
	export BACKUP_VAULT_ADDR="$FAKE_VAULT_ADDR"
	export BACKUP_VAULT_SCRATCH="$R/scratch"
	mkdir -p "$R/scratch" "$R/sa"
	printf '%s\n' "$FAKE_JWT" > "$R/sa/jwt"
	export BACKUP_VAULT_SA_JWT_FILE="$R/sa/jwt"
	echo 'Unseal Key 1: STUB-UNSEAL' > "$R/vault/.local-dev-init"
	echo 'Initial Root Token: STUB-ROOT' >> "$R/vault/.local-dev-init"
	: > "$WORK/vault-requests.log"
}
# スタブの枠（先頭 4 バイト「AGE[」と末尾 1 バイト「]」）を外して中身を取り出す。
unwrap_age() { local size; size=$(wc -c < "$1"); tail -c +5 "$1" | head -c $((size - 5)); }

# #2394: 秘匿管理が初めは立ち上がっておらず（再起動の直後で sealed）、待つうちに立ち上がると、その回は成功する。
new_vault_env
echo 3 > "$STATE/vault-down-count"
BACKUP_DEPS_WAIT_SECONDS=20 BACKUP_DEPS_WAIT_INTERVAL=0.1 run_backup
assert_eq 'T-2394-04 秘匿管理が待つうちに立ち上がる: 成功で終わる' "$RC" "0"
assert_file 'T-2394-04 秘匿管理を待った回: スナップショットが並ぶ' "$R/c/vault/2026-09-26T030000Z/vault-raft.snap.age"
assert_contains 'T-2394-04 秘匿管理を待った回: sys/health で待つ' "$(cat "$WORK/vault-requests.log")" 'GET /v1/sys/health HTTP/1.0'
# 上限までに立ち上がらない（sealed のまま・届かない）なら、ログインもせず何も書かずに失敗する。
new_vault_env
: > "$STATE/vault-never"
BACKUP_DEPS_WAIT_SECONDS=1 BACKUP_DEPS_WAIT_INTERVAL=0.1 run_backup
assert_ne 'T-2394-05 秘匿管理が立ち上がらない: 失敗で終わる' "$RC" "0"
assert_contains 'T-2394-05 秘匿管理が立ち上がらない: 理由をログに出す' "$OUT" '立ち上がりませんでした'
assert_nofile 'T-2394-05 秘匿管理が立ち上がらない: 何も書かない（fail-closed）' "$R/c/vault"
assert_missing 'T-2394-05 秘匿管理が立ち上がらない: ログインしない（トークンを作らせない）' "$(cat "$WORK/vault-requests.log")" 'POST /v1/auth/kubernetes/login'
new_vault_env
export BACKUP_VAULT_ADDR="http://127.0.0.1:1"
BACKUP_DEPS_WAIT_SECONDS=1 BACKUP_DEPS_WAIT_INTERVAL=0.1 run_backup
assert_ne 'T-2394-05 秘匿管理へ届かない（名前解決・接続の前）: 失敗で終わる' "$RC" "0"
assert_nofile 'T-2394-05 秘匿管理へ届かない: 何も書かない' "$R/c/vault"
rm -f "$STATE/vault-never"
# 1 回の確かめにも上限がある（#2395 のレビュー 🟡1）: 応答を返さない秘匿管理で、確かめが応答の待ち（BACKUP_VAULT_HTTP_TIMEOUT）まで止まらない。
new_vault_env
: > "$STATE/vault-hang"
T0=$(date +%s)
BACKUP_VAULT_HTTP_TIMEOUT=20 BACKUP_DEPS_WAIT_SECONDS=1 BACKUP_DEPS_PROBE_TIMEOUT=1 run_backup
EL=$(( $(date +%s) - T0 ))
rm -f "$STATE/vault-hang"
assert_ne 'T-2394-08 応答しない秘匿管理: 失敗で終わる' "$RC" "0"
[ "$EL" -lt 10 ] && ok "T-2394-08 応答しない秘匿管理: 1 回の確かめが上限で戻り、待ちの上限の近くで終わる（${EL} 秒）" || ng 'T-2394-08 応答しない秘匿管理: 1 回の確かめが上限で戻る' "${EL} 秒かかった"
assert_nofile 'T-2394-08 応答しない秘匿管理: 何も書かない' "$R/c/vault"

new_vault_env
run_backup
assert_eq 'T-1868-01 Vault: 整合したスナップショットで成功する' "$RC" "0"
V="$R/c/vault/2026-09-26T030000Z"
assert_file 'T-1868-01 Vault: C にスナップショット（age）が並ぶ' "$V/vault-raft.snap.age"
assert_file 'T-1868-01 Vault: C に init のファイル（age）が並ぶ' "$V/vault-init.age"
assert_file 'T-1868-01 Vault: E にも並ぶ' "$R/e/vault/2026-09-26T030000Z/vault-raft.snap.age"
assert_nofile 'T-1868-01 Vault: 稼働中の /vault/data の tar はもう作らない' "$V/vault-data.tar.gz.age"
head -c 4 "$V/vault-raft.snap.age" | grep -q 'AGE\[' && ok 'T-1868-01 Vault: スナップショットは age を通っている' || ng 'T-1868-01 Vault: スナップショットは age を通っている' "$(head -c 8 "$V/vault-raft.snap.age")"
if unwrap_age "$V/vault-raft.snap.age" | cmp -s - "$SNAPDIR/ok.snap"; then ok 'T-1868-01 Vault: 写したのは秘匿管理が返したスナップショットそのもの'; else ng 'T-1868-01 Vault: 写したのは秘匿管理が返したスナップショットそのもの' 'differs'; fi
assert_contains 'T-1868-01 Vault: init のファイル（unseal 鍵）も同じ回に入る' "$(unwrap_age "$V/vault-init.age")" 'Unseal Key 1: STUB-UNSEAL'
REQS="$(cat "$WORK/vault-requests.log")"
assert_contains 'T-1868-01 Vault: k8s auth でログインする' "$REQS" 'POST /v1/auth/kubernetes/login HTTP/1.0'
assert_contains 'T-1868-01 Vault: スナップショットの API（GET sys/storage/raft/snapshot）を呼ぶ' "$REQS" 'GET /v1/sys/storage/raft/snapshot HTTP/1.0'
assert_contains 'T-1868-01 Vault: スナップショットはトークンのヘッダで要求する' "$REQS" 'x-vault-token'
for secret in "$FAKE_VAULT_TOKEN" STUB-JWT-SIGNATURE STUB-UNSEAL STUB-ROOT; do
	assert_missing "T-1868-01 Vault: ログに秘密が出ない（${secret:0:12}…）" "$OUT" "$secret"
	assert_missing "T-1868-01 Vault: スタブの引数に秘密が出ない（${secret:0:12}…）" "$(cat "$STUB_LOG")" "$secret"
done
assert_eq 'T-1868-01 Vault: 平文のスナップショット・ログインの応答を作業場所に残さない' "$(ls -A "$R/scratch")" ""
assert_eq 'T-1868-01 Vault: staging に何も残さない' "$(ls -A "$R/staging")" ""

# 🔴 fail-closed: どの壊れ方でも、スナップショットを「成功」として置かない（回そのものを書かない）。
# 印ごとに「どの検査が止めたか」もログの文言で確かめる（後段の検査が拾うので、前段を外しても赤にならない、を防ぐ）。
declare -A SNAP_REASON=(
	[snap-empty]='スナップショットが空です'
	[snap-truncated]='gzip として壊れています'
	[snap-notgz]='gzip として壊れています'
	[snap-badsum]='state.bin が中の SHA256SUMS と一致しません'
	[snap-nostate]='state.bin がありません'
	[snap-500]='スナップショットを取れません（HTTP 500'
)
for mark in snap-empty snap-truncated snap-badsum snap-nostate snap-notgz snap-500; do
	new_vault_env
	: > "$STATE/$mark"
	run_backup
	assert_ne "T-1868-02 Vault（$mark）: 失敗で終わる" "$RC" "0"
	assert_nofile "T-1868-02 Vault（$mark）: C に何も書かない" "$R/c/vault"
	assert_nofile "T-1868-02 Vault（$mark）: E に何も書かない" "$R/e/vault"
	assert_eq "T-1868-02 Vault（$mark）: 作業場所に何も残さない" "$(ls -A "$R/scratch")" ""
	assert_contains "T-1868-02 Vault（$mark）: 回を書かないことをログに出す" "$OUT" '秘匿管理の回は書きません'
	assert_contains "T-1868-02 Vault（$mark）: 止めた検査が分かる" "$OUT" "${SNAP_REASON[$mark]}"
done

new_vault_env
printf '%s\n' "$(fake_jwt other WRONG-SA)" > "$R/sa/jwt"
run_backup
assert_ne 'T-1868-03 Vault: role に束縛されていない SA（ログイン 403）なら失敗で終わる' "$RC" "0"
assert_contains 'T-1868-03 Vault: ログインできないことをログに出す' "$OUT" 'ログインできません（HTTP 403'
assert_missing 'T-1868-03 Vault: ログインに失敗したらスナップショットを要求しない' "$(cat "$WORK/vault-requests.log")" 'raft/snapshot'
assert_nofile 'T-1868-03 Vault: 何も書かない' "$R/c/vault"

new_vault_env
rm -f "$R/sa/jwt"
run_backup
assert_ne 'T-1868-04 Vault: SA のトークン（projected volume）が無いなら失敗で終わる' "$RC" "0"
assert_eq 'T-1868-04 Vault: SA のトークンが無いなら秘匿管理へ要求を送らない' "$(cat "$WORK/vault-requests.log")" ""

new_vault_env
printf 'not-a-jwt"}\n' > "$R/sa/jwt"
run_backup
assert_ne 'T-1868-05 Vault: JWT の形でないもの（引用符を含む）は本文へ埋めずに失敗する' "$RC" "0"
assert_eq 'T-1868-05 Vault: JWT の形でないなら要求を送らない' "$(cat "$WORK/vault-requests.log")" ""

new_vault_env
export BACKUP_VAULT_ADDR="http://127.0.0.1:1"
run_backup
assert_ne 'T-1868-06 Vault: 秘匿管理へ届かないなら失敗で終わる' "$RC" "0"
assert_contains 'T-1868-06 Vault: 届かないことをログに出す' "$OUT" '秘匿管理へ接続できません'
assert_nofile 'T-1868-06 Vault: 何も書かない' "$R/c/vault"

new_vault_env
export BACKUP_VAULT_ADDR="https://vault.platform-infra:8200"
run_backup
assert_ne 'T-1868-07 Vault: http:// 以外の宛先は受け付けない（黙って別の経路へ送らない）' "$RC" "0"

new_vault_env
rm -f "$R/vault/.local-dev-init"
run_backup
assert_ne 'T-1868-08 Vault: init のファイルが無ければ失敗で終わる（失ったときに開けない）' "$RC" "0"
assert_file 'T-1868-08 Vault: スナップショットは -partial の回として残す' "$R/c/vault/2026-09-26T030000Z-partial/vault-raft.snap.age"
assert_nofile 'T-1868-08 Vault: init の成果物は作らない' "$R/c/vault/2026-09-26T030000Z-partial/vault-init.age"

new_vault_env
echo 'garbage' > "$R/vault/.local-dev-init"
run_backup
assert_ne 'T-1868-09 Vault: init のファイルに unseal 鍵の行が無ければ失敗で終わる' "$RC" "0"
assert_file 'T-1868-09 Vault: スナップショットは -partial の回として残す' "$R/c/vault/2026-09-26T030000Z-partial/vault-raft.snap.age"

new_vault_env
: > "$STATE/age-fail"
run_backup
assert_ne 'T-1868-10 Vault: 暗号化に失敗したら失敗で終わる' "$RC" "0"
assert_nofile 'T-1868-10 Vault: 暗号化に失敗したら何も書かない' "$R/c/vault"
assert_eq 'T-1868-10 Vault: 暗号化に失敗しても平文のスナップショットを残さない' "$(ls -A "$R/scratch")" ""

# ---- A6: 保持（純関数 prune_list を固定の名前で試す） ------------------------------
BACKUP_LIB=1 . "$HERE/backup.sh"
BACKUP_DAILY_KEEP=30
BACKUP_LONG_YEARS=7

# 2026-08-01 〜 2026-09-26 の毎日（57 日）＋ 2019〜2026 の各月の最初の回 ＋ 規則外の名前。
FIX=()
for i in $(seq 0 56); do FIX+=("$(date -u -d "2026-08-01 +$i day" +%Y-%m-%d)T030000Z"); done
FIX+=("2019-08-01T030000Z" "2019-09-01T030000Z" "2019-10-01T030000Z" "2020-01-01T030000Z" "2026-07-01T030000Z")
FIX+=("2026-06-03T030000Z" "2026-06-04T030000Z")          # 6 月は 1 日に PC が止まっていた（最初の回は 3 日）
FIX+=("2019-09-15T030000Z-keep" "2019-09-30T030000Z-keep") # 切替前に運用者が改名した回
FIX+=("2026-09-20T040000Z-partial" "2026-07-15T030000Z-partial")
FIX+=("notes.txt" "2026-09-01" "2026-09-01T030000Z-other" "../2026-01-01T030000Z")
DEL="$(prune_list "2026-09-26T030000Z" "${FIX[@]}")"
has_del() { printf '%s\n' "$DEL" | grep -qx -- "$1"; }
check_kept() { has_del "$2" && ng "$1" "deleted: $2" || ok "$1"; }
check_del() { has_del "$2" && ok "$1" || ng "$1" "kept: $2"; }

check_kept 'T-1560-18 日次: 直近 30 日付の最新（2026-09-26）は残す' '2026-09-26T030000Z'
check_kept 'T-1560-18 日次: 直近 30 日付の最古（2026-08-28）は残す' '2026-08-28T030000Z'
check_del  'T-1560-18 日次: 31 日付前（2026-08-27）は消す' '2026-08-27T030000Z'
check_del  'T-1560-18 日次: 月初でない古い回（2026-08-02）は消す' '2026-08-02T030000Z'
check_kept 'T-1560-19 月次: 各月の最初の回（2026-08-01）は残す' '2026-08-01T030000Z'
check_kept 'T-1560-19 月次: 7 年以内の月（2020-01）は残す' '2020-01-01T030000Z'
check_kept 'T-1560-19 月次: ちょうど 7 年前の月（2019-09）は残す' '2019-09-01T030000Z'
check_del  'T-1560-19 月次: 7 年を超えた月（2019-08）は消す' '2019-08-01T030000Z'
check_kept 'T-1560-19 月次: 1 日に取れなかった月は、その月の最初の回（6/3）を残す' '2026-06-03T030000Z'
check_del  'T-1560-19 月次: その月の 2 回目（6/4）は消す' '2026-06-04T030000Z'
check_kept 'T-1560-20 -keep: 7 年以内は残す（2019-09-30）' '2019-09-30T030000Z-keep'
check_del  'T-1560-20 -keep: 7 年を超えたら消す（2019-09-15）' '2019-09-15T030000Z-keep'
check_kept 'T-1560-21 -partial: 直近 30 日付の中なら残す' '2026-09-20T040000Z-partial'
check_del  'T-1560-21 -partial: 完全な回がある月では月次の起点にならず、古くなれば消す' '2026-07-15T030000Z-partial'
for bad in 'notes.txt' '2026-09-01' '2026-09-01T030000Z-other' '../2026-01-01T030000Z'; do
	check_kept "T-1560-22 規則外の名前（$bad）は決して消す対象にしない" "$bad"
done
assert_eq 'T-1560-22 消す対象は規則に合う名前だけ' "$(printf '%s\n' "$DEL" | grep -cvE "$RUN_NAME_RE")" "0"
# 世代数: 失敗した日（回の無い日）を数えない。
GAP=("2026-09-26T030000Z" "2026-09-10T030000Z" "2026-08-20T030000Z")
assert_eq 'T-1560-23 日次は世代数（回の無い日を数えない）: 3 回なら 1 つも消さない' "$(prune_list "2026-09-26T030000Z" "${GAP[@]}")" ""
assert_eq 'T-1560-23 回が 1 つも無ければ何も出さない' "$(prune_list "2026-09-26T030000Z")" ""
# 月に完全な回が無ければ一部失敗の回が月次の起点になる。
ONLYP=("2026-01-05T030000Z-partial" "2026-01-06T030000Z-partial" "2026-09-26T030000Z")
DEL2="$(BACKUP_DAILY_KEEP=1 prune_list "2026-09-26T030000Z" "${ONLYP[@]}")"
assert_eq 'T-1560-24 完全な回の無い月は、最初の一部失敗の回を月次にする（2 回目だけ消す）' "$DEL2" "2026-01-06T030000Z-partial"
# 「最初」は日付順の最初である（最後・中ほどではない）。一部失敗の回が 3 つある月で、残るのは 1 つ目だけ。
P3=("2026-02-20T030000Z-partial" "2026-02-10T030000Z-partial" "2026-02-25T030000Z-partial" "2026-09-26T030000Z")
DEL3="$(BACKUP_DAILY_KEEP=1 prune_list "2026-09-26T030000Z" "${P3[@]}")"
assert_eq 'T-1560-28 一部失敗だけの月は 1 つ目（2/10）を月次にし、2/20 と 2/25 を消す' "$DEL3" "$(printf '%s\n' 2026-02-20T030000Z-partial 2026-02-25T030000Z-partial)"
# 🔴 一部失敗の回が 30 日以上続いても、最新の完全な回は消さない（日次の枠が一部失敗の回で埋まっても戻せる回を残す）。
STREAK=("2026-08-01T030000Z" "2026-08-02T030000Z")
for i in $(seq 0 34); do STREAK+=("$(date -u -d "2026-08-03 +$i day" +%Y-%m-%d)T030000Z-partial"); done
DEL4="$(prune_list "2026-09-06T030000Z" "${STREAK[@]}")"
has_del4() { printf '%s\n' "$DEL4" | grep -qx -- "$1"; }
has_del4 '2026-08-02T030000Z' && ng 'T-1560-29 完全な回 2 つ → 一部失敗 35 日: 最新の完全な回（8/2）を残す' 'deleted' || ok 'T-1560-29 完全な回 2 つ → 一部失敗 35 日: 最新の完全な回（8/2）を残す'
has_del4 '2026-08-01T030000Z' && ng 'T-1560-29 完全な回 2 つ → 一部失敗 35 日: 月次（8/1）も残す' 'deleted' || ok 'T-1560-29 完全な回 2 つ → 一部失敗 35 日: 月次（8/1）も残す'
has_del4 '2026-08-03T030000Z-partial' && ok 'T-1560-29 完全な回 2 つ → 一部失敗 35 日: 30 日付を過ぎた一部失敗の回は消す' || ng 'T-1560-29 完全な回 2 つ → 一部失敗 35 日: 30 日付を過ぎた一部失敗の回は消す' 'kept'

# ---- A6: prune_target は規則外のものに触れず、再帰削除をしない ---------------------
new_env
BACKUP_KIND=postgres
K="$R/c/postgres"; mkdir -p "$K"
for n in 2019-01-01T030000Z 2019-01-02T030000Z 2026-09-26T030000Z; do mkdir -p "$K/$n"; echo x > "$K/$n/pg-a.dump.age"; done
mkdir -p "$K/2019-01-02T030000Z/unexpected-subdir"
mkdir -p "$K/keep-me"; echo x > "$K/keep-me/file"
echo x > "$K/README.txt"
# 日次の世代数を 1 にして、2019 年の 2 回を保持期間の外へ出す（30 世代のままだと 3 回とも残る）。
BACKUP_DAILY_KEEP=1 prune_target "$R/c" "2026-09-26T030000Z" >/dev/null 2>&1; PRC=$?
assert_nofile 'T-1560-25 prune_target: 保持期間を過ぎた平らな回は消す' "$K/2019-01-01T030000Z"
assert_file 'T-1560-25 prune_target: 想定外の中身（サブディレクトリ）がある回は 1 つも消さない' "$K/2019-01-02T030000Z/pg-a.dump.age"
[ -d "$K/2019-01-02T030000Z/unexpected-subdir" ] && ok 'T-1560-25 prune_target: サブディレクトリを再帰削除しない' || ng 'T-1560-25 prune_target: サブディレクトリを再帰削除しない' 'subdir removed'
assert_ne 'T-1560-25 prune_target: 消し切れなかったら失敗を返す' "$PRC" "0"
assert_file 'T-1560-25 prune_target: 規則外のディレクトリに触れない' "$K/keep-me/file"
assert_file 'T-1560-25 prune_target: 規則外のファイルに触れない' "$K/README.txt"
assert_file 'T-1560-25 prune_target: 最新の回は残す' "$K/2026-09-26T030000Z/pg-a.dump.age"

# ---- age が無ければ止まり、実行時にパッケージを入れようとしない（#1564） -----------------
# 🔴 age はイメージに同梱する（deploy/local/platform-backup/image/Dockerfile）。実行時の `apk add` は撤去した。
#    旧い env（BACKUP_AGE_INSTALL=1）を与えても apk を呼ばないことを、呼ばれたら印を残す apk スタブで確かめる。
APKBIN="$WORK/apkbin"; mkdir -p "$APKBIN"
APK_CALLED="$WORK/apk-called"
printf '#!/usr/bin/env bash\n: > "%s"\nexit 0\n' "$APK_CALLED" > "$APKBIN/apk"
chmod +x "$APKBIN/apk"
if PATH="$APKBIN:/usr/bin:/bin" command -v age >/dev/null 2>&1; then
	printf '  skip  T-1560-46 この環境の /usr/bin に age がある（age 不在の分岐を試せない）\n'
else
	AOUT="$(PATH="$APKBIN:/usr/bin:/bin" BACKUP_AGE_INSTALL=1 ensure_age 2>&1)"; ARC=$?
	assert_ne 'T-1560-46 age が無い: 失敗を返す' "$ARC" "0"
	assert_contains 'T-1560-46 age が無い: 何も書かないと告げる' "$AOUT" '暗号化できないため何も書きません'
	assert_contains 'T-1560-46 age が無い: イメージの取り違えを示す' "$AOUT" 'k3d-local/platform-backup'
	assert_nofile 'T-1560-46 age が無い: BACKUP_AGE_INSTALL=1 でも apk を呼ばない（実行時に入れない）' "$APK_CALLED"
fi

# ---- 🔴 シンボリックリンクを辿って消さない（`[ -d ]` はリンクを辿る） -------------------
# Git Bash（Windows）の ln -s は既定で写しを作り、リンクにならない。そのときは試せないので飛ばす（Linux CI では走る）。
new_env
BACKUP_KIND=postgres
K="$R/c/postgres"; mkdir -p "$K"
OUTSIDE="$R/outside"; mkdir -p "$OUTSIDE"; echo precious > "$OUTSIDE/precious.age"
mkdir -p "$K/2026-09-26T030000Z"; echo x > "$K/2026-09-26T030000Z/pg-a.dump.age"
ln -s "$OUTSIDE" "$K/2019-01-01T030000Z" 2>/dev/null
if [ -L "$K/2019-01-01T030000Z" ]; then
	# 🔴 ［#1564 追記］戻り値と出力を捨てない。prune_target のリンクの読み飛ばしを外すと、リンクは回として数えられ、
	#    remove_flat_dir が拒んで「消せませんでした」と rc=1 を返す（ファイルは守られるので下の assert_file は緑のまま）。
	#    日次の Job が毎日失敗になる退行を、ここで捕まえる。
	POUT="$(BACKUP_DAILY_KEEP=1 prune_target "$R/c" "2026-09-26T030000Z" 2>&1)"; PRC=$?
	assert_eq 'T-1560-45 prune_target: シンボリックリンクだけが古いとき成功を返す（回として数えない）' "$PRC" "0"
	assert_missing 'T-1560-45 prune_target: シンボリックリンクを消そうとしない（「消せませんでした」を出さない）' "$POUT" '消せませんでした'
	assert_file 'T-1560-45 prune_target: 回の名前をしたシンボリックリンクの先のファイルを消さない' "$OUTSIDE/precious.age"
	[ -L "$K/2019-01-01T030000Z" ] && ok 'T-1560-45 prune_target: シンボリックリンクそのものも回として扱わない' || ng 'T-1560-45 prune_target: シンボリックリンクそのものも回として扱わない' 'link removed'
	remove_flat_dir "$K/2019-01-01T030000Z"; RRC=$?
	assert_ne 'T-1560-45 remove_flat_dir: ディレクトリ自体がシンボリックリンクなら拒む' "$RRC" "0"
	assert_file 'T-1560-45 remove_flat_dir: 拒んだときリンク先のファイルは残る' "$OUTSIDE/precious.age"
	mkdir -p "$R/withlink"; echo y > "$R/withlink/pg-b.dump.age"; ln -s "$OUTSIDE/precious.age" "$R/withlink/link.age"
	remove_flat_dir "$R/withlink"; RRC=$?
	assert_ne 'T-1560-45 remove_flat_dir: 中身にシンボリックリンクがあれば拒む' "$RRC" "0"
	assert_file 'T-1560-45 remove_flat_dir: 拒んだとき 1 つも消さない' "$R/withlink/pg-b.dump.age"
	assert_file 'T-1560-45 remove_flat_dir: 中身のリンクの先も残る' "$OUTSIDE/precious.age"
else
	printf '  skip  T-1560-45 シンボリックリンクを作れない環境（Windows の Git Bash 等）\n'
fi

# ---- #2394: 待ちの上限は Job の時間切れ（activeDeadlineSeconds）の内側に余りを持って収まり、再試行はしない（backoffLimit: 0） ----
# 最悪の合計 = 依存先の待ち ＋ 1 回の確かめの上限 ＋ 間隔 ＋ 保管先の数 × ロックの待ち ＋ 作業の余裕 300。
# 🔴 #2395 のレビュー 🟡2: 余りが 0 の設計は脆いので、合計は時間切れより真に小さいこと（余り > 0）を求める。
default_of() { sed -n "s/^$1=\"\\\${$1:-\\([0-9.]*\\)}\"\$/\\1/p" "$HERE/backup.sh"; }
DEPS_WAIT="$(default_of BACKUP_DEPS_WAIT_SECONDS)"
PROBE="$(default_of BACKUP_DEPS_PROBE_TIMEOUT)"
INTERVAL="$(default_of BACKUP_DEPS_WAIT_INTERVAL)"
LOCK_WAIT="$(default_of BACKUP_LOCK_WAIT_SECONDS)"
NTARGETS="$(sed -n 's/^BACKUP_TARGETS="\${BACKUP_TARGETS:-\(.*\)}"$/\1/p' "$HERE/backup.sh" | wc -w)"
for v in DEPS_WAIT PROBE INTERVAL LOCK_WAIT NTARGETS; do
	[[ "${!v}" =~ ^[0-9]+$ ]] && ok "T-2394-06 既定値を読める: $v=${!v}" || ng "T-2394-06 既定値を読める: $v" "${!v}"
done
for kind in postgres vault; do
	CJ="$HERE/../$kind/cronjob.yaml"
	ADL="$(sed -n 's/^ *activeDeadlineSeconds: *\([0-9]*\).*/\1/p' "$CJ" | head -n 1)"
	BOL="$(sed -n 's/^ *backoffLimit: *\([0-9]*\).*/\1/p' "$CJ" | head -n 1)"
	assert_eq "T-2394-06 $kind の CronJob は再試行しない（backoffLimit: 0）" "$BOL" "0"
	if [[ "$ADL" =~ ^[0-9]+$ ]] && [[ "$DEPS_WAIT$PROBE$INTERVAL$LOCK_WAIT$NTARGETS" =~ ^[0-9]+$ ]]; then
		WORST=$((DEPS_WAIT + PROBE + INTERVAL + NTARGETS * LOCK_WAIT + 300))
		if [ "$WORST" -lt "$ADL" ]; then
			ok "T-2394-06 $kind: 最悪の合計 $WORST 秒が activeDeadlineSeconds $ADL 秒より小さい（余り $((ADL - WORST)) 秒）"
		else
			ng "T-2394-06 $kind: 最悪の合計が activeDeadlineSeconds より小さい" "worst=$WORST adl=$ADL"
		fi
	else
		ng "T-2394-06 $kind: 既定値と activeDeadlineSeconds を読める" "adl=$ADL"
	fi
done

# ---- #2402・IADR-0674: S3 互換のオブジェクトストレージの保管先（クラウドの最小構成） ----
# rclone のスタブは、リモート `objstore:` を $STUB_S3_ROOT の下のディレクトリとして扱う（オブジェクトストレージのモック）。
# 改名もロックも持たない（本物のオブジェクトストレージと同じく、書いた名前のまま置くだけ）。
# 失敗の注入: $STATE/s3-upload-fail（送れない）/ $STATE/s3-corrupt-upload（送った先の暗号文を 1 つ書き換える）。
cat > "$WORK/bin/rclone" <<'STUB'
#!/usr/bin/env bash
echo "rclone $*" >> "$STUB_LOG"
map() {
  case "$1" in
    objstore:*) printf '%s/%s' "$STUB_S3_ROOT" "${1#objstore:}" ;;
    *:*) echo "stub rclone: 未知のリモート: $1" >&2; return 1 ;;
    *) printf '%s' "$1" ;;
  esac
}
is_remote() { case "$1" in objstore:*) return 0 ;; esac; return 1; }
args=()
for a in "$@"; do case "$a" in -*) ;; *) args+=("$a") ;; esac; done
cmd="${args[0]:-}"
case "$cmd" in
  lsf)
    d="$(map "${args[1]}")" || exit 1
    [ -d "$d" ] || exit 0
    for p in "$d"/*; do [ -e "$p" ] || continue; n="${p##*/}"; [ -d "$p" ] && echo "$n/" || echo "$n"; done
    ;;
  copy)
    src="$(map "${args[1]}")" || exit 1; dst="$(map "${args[2]}")" || exit 1
    if is_remote "${args[2]}" && [ -f "$STATE/s3-upload-fail" ]; then echo "stub rclone: upload failed" >&2; exit 1; fi
    mkdir -p "$dst" && cp "$src"/* "$dst"/ || exit 1
    if is_remote "${args[2]}" && [ -f "$STATE/s3-corrupt-upload" ]; then
      for f in "$dst"/*.age; do [ -f "$f" ] && { printf 'X' >> "$f"; break; }; done
    fi
    ;;
  copyto)
    src="$(map "${args[1]}")" || exit 1; dst="$(map "${args[2]}")" || exit 1
    if is_remote "${args[2]}" && [ -f "$STATE/s3-upload-fail" ]; then exit 1; fi
    mkdir -p "$(dirname "$dst")" && cp "$src" "$dst" || exit 1
    ;;
  *) echo "stub rclone: 未対応の操作: $cmd" >&2; exit 1 ;;
esac
STUB
chmod +x "$WORK/bin/rclone"
S3_SECRET="stub-s3-secret-DO-NOT-LEAK"
new_s3_env() {
	new_env
	export STUB_S3_ROOT="$R/s3"
	mkdir -p "$STUB_S3_ROOT/bkt"
	export BACKUP_TARGETS=none
	export BACKUP_S3_DEST="objstore:bkt/ast-poc"
	export RCLONE_CONFIG_OBJSTORE_SECRET_ACCESS_KEY="$S3_SECRET"
	export RCLONE_CONFIG_OBJSTORE_ACCESS_KEY_ID="stub-s3-key-id"
	export BACKUP_NOW="2026-11-01T030000Z"
}
S3D() { printf '%s/bkt/ast-poc/postgres/%s/%s' "$R/s3" "$1" "$2"; }

new_s3_env
run_backup
assert_eq 'T-2402-01 S3: 正常系は成功で終わる' "$RC" "0"
D="$(S3D daily 2026-11-01T030000Z)"
for f in pg-globals.sql.age pg-audit_svc.dump.age pg-postgres.dump.age pg-risk_management_svc.dump.age SHA256SUMS COMPLETE; do
	assert_file "T-2402-01 S3: daily の回に $f が並ぶ" "$D/$f"
done
assert_eq 'T-2402-01 S3: DB ダンプは age を通っている（暗号化した成果物だけを送る）' "$(cat "$D/pg-audit_svc.dump.age" 2>/dev/null)" 'AGE[PGDMP-audit_svc]'
assert_eq 'T-2402-01 S3: 送った回の SHA256SUMS が検算できる' "$(cd "$D" 2>/dev/null && sha256sum -c SHA256SUMS >/dev/null 2>&1; echo $?)" "0"
assert_eq 'T-2402-01 S3: COMPLETE は SHA256SUMS の sha256 である' "$(cat "$D/COMPLETE" 2>/dev/null)" "$(sha256sum < "$D/SHA256SUMS" 2>/dev/null | cut -d ' ' -f 1)"
assert_contains 'T-2402-01 S3: 送った回を読み戻して検算する' "$(cat "$STUB_LOG")" "copy objstore:bkt/ast-poc/postgres/daily/2026-11-01T030000Z $R/staging/.verify-daily-"
assert_contains 'T-2402-01 S3: 書き終えたと告げる' "$OUT" '書きました: objstore:bkt/ast-poc/postgres/daily/2026-11-01T030000Z'
assert_eq 'T-2402-01 S3: staging（読み戻しの作業場所を含む）に何も残らない' "$(ls -A "$R/staging")" ""
assert_file 'T-2402-02 S3: 月の最初の完全な回は monthly にも書く（COMPLETE がある）' "$(S3D monthly 2026-11-01T030000Z)/COMPLETE"
assert_eq 'T-2402-02 S3: monthly の回は daily と同じ中身' "$(cat "$(S3D monthly 2026-11-01T030000Z)/COMPLETE" 2>/dev/null)" "$(cat "$D/COMPLETE" 2>/dev/null)"
assert_missing 'T-2402-03 S3: 資格情報がどのコマンドの引数にも出ない' "$(cat "$STUB_LOG")" "$S3_SECRET"
assert_missing 'T-2402-03 S3: 資格情報がログに出ない' "$OUT" "$S3_SECRET"
assert_missing 'T-2402-03 S3: パスワードがどのコマンドの引数にも出ない' "$(cat "$STUB_LOG")" "$SECRET_PW"
assert_nofile 'T-2402-04 S3: BACKUP_TARGETS=none なら hostPath に書かない' "$R/c/postgres"
assert_nofile 'T-2402-04 S3: BACKUP_TARGETS=none なら「none」という保管先を作らない' "none"
assert_missing 'T-2402-04 S3: 保持の削除を呼ばない（lifecycle に任せる）' "$(cat "$STUB_LOG")" 'rclone delete'
assert_missing 'T-2402-04 S3: purge を呼ばない' "$(cat "$STUB_LOG")" 'rclone purge'

# 同じ月の 2 回目は monthly へ書かない。
export BACKUP_NOW="2026-11-02T030000Z"
: > "$STUB_LOG"
run_backup
assert_eq 'T-2402-02 S3: 同じ月の 2 回目も成功で終わる' "$RC" "0"
assert_file 'T-2402-02 S3: 2 回目は daily に書く' "$(S3D daily 2026-11-02T030000Z)/COMPLETE"
assert_nofile 'T-2402-02 S3: 同じ月の 2 回目は monthly へ書かない' "$(S3D monthly 2026-11-02T030000Z)"

# 同じ名前の回が既にある → 書かずに失敗し、既にある回を壊さない。
BEFORE="$(cat "$(S3D daily 2026-11-02T030000Z)/COMPLETE")"
: > "$STUB_LOG"
run_backup
assert_ne 'T-2402-07 S3: 同じ名前の回が既にあれば失敗で終わる' "$RC" "0"
assert_eq 'T-2402-07 S3: 既にある回の COMPLETE を書き換えない' "$(cat "$(S3D daily 2026-11-02T030000Z)/COMPLETE")" "$BEFORE"
assert_missing 'T-2402-07 S3: 既にある回へ送らない' "$(cat "$STUB_LOG")" "/2026-11-02T030000Z/COMPLETE"

# 送った先の暗号文が壊れた → COMPLETE を置かずに失敗する。
new_s3_env
: > "$STATE/s3-corrupt-upload"
run_backup
assert_ne 'T-2402-05 S3: 送った回の検算が合わなければ失敗で終わる' "$RC" "0"
assert_nofile 'T-2402-05 S3: 検算が合わない回に COMPLETE を置かない' "$(S3D daily 2026-11-01T030000Z)/COMPLETE"
assert_nofile 'T-2402-05 S3: 検算が合わない回を monthly へ書かない' "$(S3D monthly 2026-11-01T030000Z)/COMPLETE"
assert_missing 'T-2402-05 S3: 「書きました」を出さない' "$OUT" '書きました'
assert_eq 'T-2402-05 S3: staging に何も残らない' "$(ls -A "$R/staging")" ""

# 送れない → 失敗し、COMPLETE を置かない。
new_s3_env
: > "$STATE/s3-upload-fail"
run_backup
assert_ne 'T-2402-06 S3: 送れなければ失敗で終わる' "$RC" "0"
assert_nofile 'T-2402-06 S3: 送れなかった回に COMPLETE を置かない' "$(S3D daily 2026-11-01T030000Z)/COMPLETE"
assert_contains 'T-2402-06 S3: 理由をログに出す' "$OUT" 'オブジェクトストレージへ書けませんでした'

# 保管先が 1 つも無い・宛先の形が不正 → 何もダンプせずに失敗する。
new_s3_env
unset BACKUP_S3_DEST
run_backup
assert_ne 'T-2402-08 保管先が 1 つも無い（none かつ S3 なし）: 失敗で終わる' "$RC" "0"
assert_missing 'T-2402-08 保管先が 1 つも無い: pg_dump を呼ばない' "$(cat "$STUB_LOG")" 'pg_dump'
for bad in 'objstore:' 'objstore:bkt/../x' 'objstore:bkt/a b' '/local/path' 'objstore:B'; do
	new_s3_env
	export BACKUP_S3_DEST="$bad"
	run_backup
	assert_ne "T-2402-08 S3 の宛先の形が不正（$bad）: 失敗で終わる" "$RC" "0"
	assert_missing "T-2402-08 S3 の宛先の形が不正（$bad）: pg_dump を呼ばない" "$(cat "$STUB_LOG")" 'pg_dump'
done

# 一部失敗の回は daily にだけ書き、monthly へは書かない。
new_s3_env
: > "$STATE/dump-fail-audit_svc"
run_backup
assert_ne 'T-2402-10 S3: 一部失敗の回は失敗で終わる' "$RC" "0"
assert_file 'T-2402-10 S3: 一部失敗の回は daily に -partial として書く' "$(S3D daily 2026-11-01T030000Z-partial)/COMPLETE"
assert_nofile 'T-2402-10 S3: 一部失敗の回は monthly へ書かない' "$(S3D monthly 2026-11-01T030000Z-partial)"

# hostPath と S3 の両方: 両方に書く（ローカルへ S3 を足す形も壊れない）。
new_s3_env
export BACKUP_TARGETS="$R/c $R/e"
run_backup
assert_eq 'T-2402-11 hostPath と S3 の両方: 成功で終わる' "$RC" "0"
assert_file 'T-2402-11 hostPath と S3 の両方: C に書く' "$R/c/postgres/2026-11-01T030000Z/SHA256SUMS"
assert_file 'T-2402-11 hostPath と S3 の両方: S3 に書く' "$(S3D daily 2026-11-01T030000Z)/COMPLETE"
unset BACKUP_S3_DEST RCLONE_CONFIG_OBJSTORE_SECRET_ACCESS_KEY RCLONE_CONFIG_OBJSTORE_ACCESS_KEY_ID STUB_S3_ROOT

# ローカルの既定（hostPath）は変わらない: 既定の保管先は 2 本のまま、S3 の宛先が無ければ rclone を呼ばない。
assert_eq 'T-2402-09 hostPath の既定の保管先は変わらない' "$(sed -n 's/^BACKUP_TARGETS="\${BACKUP_TARGETS:-\(.*\)}"$/\1/p' "$HERE/backup.sh")" '/backup/c /backup/e'
new_env
run_backup
assert_eq 'T-2402-09 S3 の宛先が無い hostPath の回は成功で終わる' "$RC" "0"
assert_missing 'T-2402-09 S3 の宛先が無い hostPath の回は rclone を呼ばない' "$(cat "$STUB_LOG")" 'rclone'
for kind in postgres vault; do
	assert_missing "T-2402-09 ローカルの $kind の CronJob は S3 の宛先を持たない" "$(cat "$HERE/../$kind/cronjob.yaml")" 'BACKUP_S3_DEST'
done

# クラウドの CronJob: 最悪の合計（依存先の待ち＋確かめ＋間隔＋ S3 の呼び出しの数 × 1 回の上限＋余裕 300）が時間切れより小さい。
# 呼び出しは daily（存在の確かめ・送る・読み戻す・COMPLETE）の 4 ＋ monthly の有無の確かめ 1 ＋ monthly の 4 ＝ 9。
CLOUD_CJ="$HERE/../../../cloud/backup/cronjob-postgres.yaml"
S3_CALL="$(default_of BACKUP_S3_CALL_TIMEOUT)"
CADL="$(sed -n 's/^ *activeDeadlineSeconds: *\([0-9]*\).*/\1/p' "$CLOUD_CJ" 2>/dev/null | head -n 1)"
CBOL="$(sed -n 's/^ *backoffLimit: *\([0-9]*\).*/\1/p' "$CLOUD_CJ" 2>/dev/null | head -n 1)"
assert_eq 'T-2402-12 クラウドの CronJob は再試行しない（backoffLimit: 0）' "$CBOL" "0"
if [[ "$CADL" =~ ^[0-9]+$ ]] && [[ "$S3_CALL" =~ ^[0-9]+$ ]] && [[ "$DEPS_WAIT$PROBE$INTERVAL" =~ ^[0-9]+$ ]]; then
	CWORST=$((DEPS_WAIT + PROBE + INTERVAL + 9 * S3_CALL + 300))
	if [ "$CWORST" -lt "$CADL" ]; then
		ok "T-2402-12 クラウド: 最悪の合計 $CWORST 秒が activeDeadlineSeconds $CADL 秒より小さい"
	else
		ng 'T-2402-12 クラウド: 最悪の合計が activeDeadlineSeconds より小さい' "worst=$CWORST adl=$CADL"
	fi
else
	ng 'T-2402-12 クラウド: 既定値と activeDeadlineSeconds を読める' "adl=$CADL s3=$S3_CALL"
fi

# 作業場（mktemp の下）は再帰削除しない。CI のランナーは使い捨てで、手元では一時領域の掃除に任せる。
printf '\nbackup.test.sh: %d passed, %d failed\n' "$PASSED" "$FAILED"
[ "$FAILED" -eq 0 ]
