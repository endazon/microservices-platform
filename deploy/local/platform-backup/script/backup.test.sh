#!/usr/bin/env bash
# NFR-21, NFR-05, NFR-18, IADR-0471 (#1560): backup.sh の分岐を、pg_dump / pg_dumpall / psql / age を
# PATH 上のスタブへ差し替えて固定する。実 Postgres・実 Vault・実 age・実クラスタは要らない。
#
#   - age スタブは「AGE[」＋ 標準入力 ＋「]」を -o のファイルへ書く（パイプが age を通ったことを中身で確かめる）
#   - 呼び出しの argv はすべて $STUB_LOG へ記録する（パスワードが引数に出ないことをここで確かめる）
#   - $STATE の印で失敗を注入する（psql-fail / dump-fail-<db> / age-fail / mutate-vault / mutate-once）
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
cat > "$WORK/bin/psql" <<'STUB'
#!/usr/bin/env bash
echo "psql $*" >> "$STUB_LOG"
[ -f "$STATE/psql-fail" ] && exit 2
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
if [ -f "$STATE/mutate-vault" ]; then
  echo "$RANDOM$RANDOM" > "$VAULT_DATA_DIR/changed-$RANDOM$RANDOM"
  [ -f "$STATE/mutate-once" ] && rm -f "$STATE/mutate-vault"
fi
{ printf 'AGE['; cat; printf ']'; } > "$out"
STUB
# cp は既定で本物を呼ぶ。$STATE/corrupt-copy があれば、写した先の暗号文を 1 つ書き換える
# （保管先への写しが壊れた世界。publish が SHA256SUMS で検証せずに改名すると、壊れた回が残る）。
REAL_CP="$(command -v cp)"
cat > "$WORK/bin/cp" <<STUB
#!/usr/bin/env bash
"$REAL_CP" "\$@" || exit \$?
if [ -f "\$STATE/corrupt-copy" ]; then
  dest="\${!#}"
  for f in "\$dest"/*.age; do [ -f "\$f" ] && { printf 'X' >> "\$f"; break; }; done
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
	unset BACKUP_AGE_INSTALL
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

# ---- Vault --------------------------------------------------------------------------
new_env
export BACKUP_KIND=vault
mkdir -p "$R/vault/logical/abc"
echo 'Unseal Key 1: STUB-UNSEAL' > "$R/vault/.local-dev-init"
echo 'data' > "$R/vault/logical/abc/_entry"
run_backup
assert_eq 'T-1560-14 Vault: 成功で終わる' "$RC" "0"
V="$R/c/vault/2026-09-26T030000Z/vault-data.tar.gz.age"
assert_file 'T-1560-14 Vault: C に tar.gz.age が並ぶ' "$V"
assert_file 'T-1560-14 Vault: E に tar.gz.age が並ぶ' "$R/e/vault/2026-09-26T030000Z/vault-data.tar.gz.age"
EXTRACT="$WORK/extract"; mkdir -p "$EXTRACT"
head -c 4 "$V" | grep -q 'AGE\[' && ok 'T-1560-14 Vault: 成果物は age を通っている' || ng 'T-1560-14 Vault: 成果物は age を通っている' "$(head -c 8 "$V")"
# スタブの枠（先頭 4 バイト「AGE[」と末尾 1 バイト「]」）を外すと元の tar.gz に戻る。
SIZE=$(wc -c < "$V"); tail -c +5 "$V" | head -c $((SIZE - 5)) | tar -xzf - -C "$EXTRACT" 2>/dev/null
assert_file 'T-1560-14 Vault: 写しに init ファイルまで含まれる（だから暗号化が必須）' "$EXTRACT/.local-dev-init"
assert_file 'T-1560-14 Vault: 写しにデータが含まれる' "$EXTRACT/logical/abc/_entry"
assert_missing 'T-1560-14 Vault: unseal 鍵がログに出ない' "$OUT" 'STUB-UNSEAL'
assert_eq 'T-1560-14 Vault: 平文の tar を staging に残さない' "$(ls -A "$R/staging")" ""

new_env
export BACKUP_KIND=vault
echo 'x' > "$R/vault/file"
: > "$STATE/mutate-vault"; : > "$STATE/mutate-once"
run_backup
assert_eq 'T-1560-15 Vault: 写している間に 1 度変わっても、やり直して成功する' "$RC" "0"
assert_contains 'T-1560-15 Vault: やり直したことをログに出す' "$OUT" 'やり直します'

new_env
export BACKUP_KIND=vault
echo 'x' > "$R/vault/file"
: > "$STATE/mutate-vault"
run_backup
assert_ne 'T-1560-16 Vault: 変わり続けるなら失敗で終わる（黙って不整合な写しを置かない）' "$RC" "0"
assert_nofile 'T-1560-16 Vault: 変わり続けるなら何も書かない' "$R/c/vault"

new_env
export BACKUP_KIND=vault
run_backup
assert_ne 'T-1560-17 Vault: データが空なら失敗で終わる' "$RC" "0"

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

# ---- age の導入に失敗したら、apk の理由を出して止まる ------------------------------
APKBIN="$WORK/apkbin"; mkdir -p "$APKBIN"
printf '#!/usr/bin/env bash\necho "ERROR: unable to select packages: age (no such package)" >&2\nexit 1\n' > "$APKBIN/apk"
chmod +x "$APKBIN/apk"
if PATH="$APKBIN:/usr/bin:/bin" command -v age >/dev/null 2>&1; then
	printf '  skip  T-1560-31 この環境の /usr/bin に age がある（age 不在の分岐を試せない）\n'
else
	AOUT="$(PATH="$APKBIN:/usr/bin:/bin" BACKUP_AGE_INSTALL=1 ensure_age 2>&1)"; ARC=$?
	assert_ne 'T-1560-31 age を入れられない: 失敗を返す' "$ARC" "0"
	assert_contains 'T-1560-31 age を入れられない: apk の理由をログに出す' "$AOUT" 'unable to select packages'
	assert_contains 'T-1560-31 age を入れられない: 何も書かないと告げる' "$AOUT" 'age を用意できません'
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
	BACKUP_DAILY_KEEP=1 prune_target "$R/c" "2026-09-26T030000Z" >/dev/null 2>&1
	assert_file 'T-1560-30 prune_target: 回の名前をしたシンボリックリンクの先のファイルを消さない' "$OUTSIDE/precious.age"
	[ -L "$K/2019-01-01T030000Z" ] && ok 'T-1560-30 prune_target: シンボリックリンクそのものも回として扱わない' || ng 'T-1560-30 prune_target: シンボリックリンクそのものも回として扱わない' 'link removed'
	remove_flat_dir "$K/2019-01-01T030000Z"; RRC=$?
	assert_ne 'T-1560-30 remove_flat_dir: ディレクトリ自体がシンボリックリンクなら拒む' "$RRC" "0"
	assert_file 'T-1560-30 remove_flat_dir: 拒んだときリンク先のファイルは残る' "$OUTSIDE/precious.age"
	mkdir -p "$R/withlink"; echo y > "$R/withlink/pg-b.dump.age"; ln -s "$OUTSIDE/precious.age" "$R/withlink/link.age"
	remove_flat_dir "$R/withlink"; RRC=$?
	assert_ne 'T-1560-30 remove_flat_dir: 中身にシンボリックリンクがあれば拒む' "$RRC" "0"
	assert_file 'T-1560-30 remove_flat_dir: 拒んだとき 1 つも消さない' "$R/withlink/pg-b.dump.age"
	assert_file 'T-1560-30 remove_flat_dir: 中身のリンクの先も残る' "$OUTSIDE/precious.age"
else
	printf '  skip  T-1560-30 シンボリックリンクを作れない環境（Windows の Git Bash 等）\n'
fi

# 作業場（mktemp の下）は再帰削除しない。CI のランナーは使い捨てで、手元では一時領域の掃除に任せる。
printf '\nbackup.test.sh: %d passed, %d failed\n' "$PASSED" "$FAILED"
[ "$FAILED" -eq 0 ]
