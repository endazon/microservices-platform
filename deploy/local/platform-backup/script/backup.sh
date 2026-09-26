#!/bin/bash
# NFR-21, NFR-05, ADR-0002, ADR-0008, IADR-0471 (#1560): platform-infra の Postgres / Vault を
# **age の公開鍵で暗号化して**クラスタ外の 2 か所（hostPath）へ置く CronJob の本体。
#
# 🔴 deploy/local 専用（ローカル開発・PoC 環境）。本番のバックアップ設計ではない。
#
# 1 回の実行で 1 種類（BACKUP_KIND=postgres | vault）を扱う:
#   1. 受取人（公開鍵）を検査する。無い・占位のまま・不正な行があれば**何もせずに失敗する**（fail-closed）
#   2. 成果物を staging（emptyDir）へ作る。**平文をディスクへ書かない** —— 生成器の出力を age へパイプで渡す
#        postgres: DB ごとに `pg_dump -Fc` ＋ `pg_dumpall --globals-only`
#        vault:    /vault/data の tar.gz（稼働中の写しなので前後のファイル一覧を比べ、変わったらやり直す）
#   3. 各保管先へ `.incoming-<回>` として写し、SHA256SUMS で検証してから `<回>` へ改名する
#      **目印ファイル（.platform-backup-target）が無い保管先には書かない**（ドライブが外れていると kubelet が
#      ディストリ内に空のディレクトリを作る。そこはクラスタ外ではない）
#   4. 保持の規則で古い回を消す（名前の規則に合うディレクトリだけ・再帰削除をしない）
#   片方の保管先に書けなくてももう片方には書く。**どこかで失敗したら exit 1**（Job を失敗として見せる）。
#
# 🔴 秘密の値（DB のパスワード・Vault のトークン・unseal 鍵）をログ・引数へ出さない。
#    パスワードは env PGPASSWORD（secretKeyRef）で libpq に渡し、コマンドラインに載せない。`set -x` を使わない。
#
# 試験（backup.test.sh）は BACKUP_LIB=1 で source し、pg_dump / psql / age を PATH 上のスタブへ差し替える。
set -u
set -o pipefail

BACKUP_KIND="${BACKUP_KIND:-}"
BACKUP_TARGETS="${BACKUP_TARGETS:-/backup/c /backup/e}"
BACKUP_STAGING="${BACKUP_STAGING:-/staging}"
BACKUP_RECIPIENTS_FILE="${BACKUP_RECIPIENTS_FILE:-/etc/platform-backup/recipients/recipients.txt}"
BACKUP_MARKER_NAME=".platform-backup-target"
BACKUP_DAILY_KEEP="${BACKUP_DAILY_KEEP:-30}"
BACKUP_LONG_YEARS="${BACKUP_LONG_YEARS:-7}"
BACKUP_AGE_INSTALL="${BACKUP_AGE_INSTALL:-0}"
BACKUP_VAULT_TRIES="${BACKUP_VAULT_TRIES:-3}"
VAULT_DATA_DIR="${VAULT_DATA_DIR:-/vault/data}"

# 回の名前: UTC の時刻 ＋ 任意の接尾辞（-partial = 一部失敗 / -keep = 運用者が長期保持へ改名したもの）。
RUN_NAME_RE='^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{6}Z(-partial|-keep)?$'
# age の公開鍵（bech32: "age1" ＋ 58 文字）。
AGE_RECIPIENT_RE='^age1[qpzry9x8gf2tvdw0s3jn54khce6mua7l]{58}$'

log() { printf '==> platform-backup[%s]: %s\n' "${BACKUP_KIND:-?}" "$*"; }
err() { printf '==> platform-backup[%s]: ERROR: %s\n' "${BACKUP_KIND:-?}" "$*" >&2; }

is_run_name() { [[ "$1" =~ $RUN_NAME_RE ]]; }

# 受取人ファイルを検査する。空行と # 行を除いたすべての行が age の公開鍵で、1 行以上あること。
# 🔴 占位（リポジトリの例示ファイルそのまま）・不在・不正な行は失敗にする —— 暗号化できない鍵で
#    「成功」を積まないため。行の中身は表示しない（公開鍵だが、表示の必要が無い）。
check_recipients() {
	local file="$1" line n=0 lineno=0
	if [ ! -r "$file" ]; then
		err "age の受取人ファイルがありません: $file（ConfigMap platform-backup-age-recipients を作ってください）"
		return 1
	fi
	while IFS= read -r line || [ -n "$line" ]; do
		lineno=$((lineno + 1))
		line="${line%$'\r'}"
		line="${line#"${line%%[![:space:]]*}"}"
		line="${line%"${line##*[![:space:]]}"}"
		case "$line" in '' | '#'*) continue ;; esac
		if [[ ! "$line" =~ $AGE_RECIPIENT_RE ]]; then
			err "受取人ファイルの ${lineno} 行目が age の公開鍵（age1...）ではありません（占位のままではないか確かめてください）"
			return 1
		fi
		n=$((n + 1))
	done < "$file"
	if [ "$n" -eq 0 ]; then
		err "受取人ファイルに age の公開鍵が 1 つもありません（占位のまま）: $file"
		return 1
	fi
	return 0
}

ensure_age() {
	if command -v age >/dev/null 2>&1; then return 0; fi
	if [ "$BACKUP_AGE_INSTALL" = "1" ]; then
		log "age が無いため Alpine のパッケージを入れます"
		apk add --no-cache age >/dev/null 2>&1 || true
	fi
	if ! command -v age >/dev/null 2>&1; then
		err "age を用意できません（暗号化できないため何も書きません）"
		return 1
	fi
}

# 標準入力を暗号化して $1 へ書く（一時名 → 改名）。平文は受け取るだけでディスクへ置かない。
encrypt_to() {
	local out="$1"
	age -R "$BACKUP_RECIPIENTS_FILE" -o "$out.tmp" || { rm -f -- "$out.tmp"; return 1; }
	mv -- "$out.tmp" "$out"
}

# ---------------------------------------------------------------- postgres

list_databases() {
	psql -X -A -t -v ON_ERROR_STOP=1 -d "${PGDATABASE:-postgres}" \
		-c "SELECT datname FROM pg_database WHERE NOT datistemplate AND datallowconn ORDER BY datname"
}

stage_postgres() {
	local dir="$1" dbs db rc=0
	if ! dbs="$(list_databases)"; then
		err "DB の一覧を取れません（接続・認証を確かめてください）"
		return 2
	fi
	if [ -z "$dbs" ]; then
		err "DB が 1 つも見つかりません"
		return 2
	fi
	if pg_dumpall --globals-only | encrypt_to "$dir/pg-globals.sql.age"; then
		log "globals: ok"
	else
		err "pg_dumpall --globals-only が失敗しました"
		rm -f -- "$dir/pg-globals.sql.age"
		rc=1
	fi
	while IFS= read -r db; do
		[ -n "$db" ] || continue
		# ファイル名に使うので、想定外の文字を持つ DB 名は写さずに失敗として数える。
		if [[ ! "$db" =~ ^[A-Za-z0-9_]+$ ]]; then
			err "DB 名に想定外の文字があるため写しません: $db"
			rc=1
			continue
		fi
		if pg_dump -Fc -d "$db" | encrypt_to "$dir/pg-$db.dump.age"; then
			log "db $db: ok"
		else
			err "pg_dump が失敗しました: $db"
			rm -f -- "$dir/pg-$db.dump.age"
			rc=1
		fi
	done <<< "$dbs"
	return "$rc"
}

# ---------------------------------------------------------------- vault

# ファイル一覧（名前・サイズ・更新時刻）の要約。写す前後で比べ、稼働中の書き込みを検出する。
tree_state() {
	find "$1" -exec stat -c '%n|%s|%Y' {} + 2>/dev/null | LC_ALL=C sort | sha256sum
}

stage_vault() {
	local dir="$1" try=1 before after
	if [ ! -d "$VAULT_DATA_DIR" ] || [ -z "$(ls -A "$VAULT_DATA_DIR" 2>/dev/null)" ]; then
		err "Vault のデータが見つかりません: $VAULT_DATA_DIR"
		return 2
	fi
	while [ "$try" -le "$BACKUP_VAULT_TRIES" ]; do
		before="$(tree_state "$VAULT_DATA_DIR")"
		if ! tar -C "$VAULT_DATA_DIR" -czf - . | encrypt_to "$dir/vault-data.tar.gz.age"; then
			err "Vault のデータを固められません"
			rm -f -- "$dir/vault-data.tar.gz.age"
			return 2
		fi
		after="$(tree_state "$VAULT_DATA_DIR")"
		if [ "$before" = "$after" ]; then
			log "vault data: ok（写している間に変化なし・試行 $try）"
			return 0
		fi
		log "写している間に Vault のデータが変わりました。やり直します（試行 $try）"
		rm -f -- "$dir/vault-data.tar.gz.age"
		try=$((try + 1))
	done
	err "Vault のデータが変わり続けるため、整合した写しを取れませんでした"
	return 2
}

# ---------------------------------------------------------------- publish / prune

# 平らなディレクトリ（回・incoming・staging）を消す。🔴 再帰削除をしない —— 想定外の中身
# （サブディレクトリ等の通常ファイル以外）があれば、**1 つも消さずに**失敗として返す。
remove_flat_dir() {
	local d="$1" f
	[ -d "$d" ] || return 0
	for f in "$d"/* "$d"/.[!.]*; do
		[ -e "$f" ] || [ -L "$f" ] || continue
		if [ -L "$f" ] || [ ! -f "$f" ]; then return 1; fi
	done
	for f in "$d"/* "$d"/.[!.]*; do
		[ -f "$f" ] && rm -f -- "$f"
	done
	rmdir -- "$d" 2>/dev/null
}

write_sums() {
	(cd "$1" && sha256sum -- *.age > SHA256SUMS)
}

publish() {
	local target="$1" run="$2" staging="$3" kind_dir incoming old
	if [ ! -d "$target" ] || [ ! -f "$target/$BACKUP_MARKER_NAME" ]; then
		err "保管先に目印 $BACKUP_MARKER_NAME がありません: $target（ドライブが外れていないか、手順書の準備を済ませたか確かめてください）"
		return 1
	fi
	kind_dir="$target/$BACKUP_KIND"
	if ! mkdir -p -- "$kind_dir" 2>/dev/null || [ ! -d "$kind_dir" ]; then
		err "保管先にディレクトリを作れません: $kind_dir"
		return 1
	fi
	# 前回の途中で残った incoming を片付ける（平らなものだけ）。
	for old in "$kind_dir"/.incoming-*; do
		[ -d "$old" ] && remove_flat_dir "$old"
	done
	incoming="$kind_dir/.incoming-$run"
	if [ -e "$kind_dir/$run" ]; then
		err "同じ名前の回が既にあります: $kind_dir/$run"
		return 1
	fi
	if mkdir -- "$incoming" 2>/dev/null &&
		cp -- "$staging"/* "$incoming"/ 2>/dev/null &&
		(cd "$incoming" && sha256sum -c SHA256SUMS >/dev/null 2>&1) &&
		mv -- "$incoming" "$kind_dir/$run" 2>/dev/null; then
		log "書きました: $kind_dir/$run"
		return 0
	fi
	err "保管先へ書けませんでした: $kind_dir"
	remove_flat_dir "$incoming"
	return 1
}

# 保持の規則で「消す回」の名前を 1 行ずつ出す（純関数。ファイルシステムに触れない）。
#   $1 = 現在時刻（回の名前と同じ形。例 2026-09-26T030000Z）、以降 = 既存の回の名前
# 規則:
#   - 日次: 回のある日付（完全・一部失敗の両方）の新しい方から BACKUP_DAILY_KEEP 日分の回をすべて残す
#           （失敗した日を数えない＝世代数。PC を止めていた日があっても 30 世代は残る）
#   - 月次: 各月で最初に取れた回（完全な回を優先し、無ければ一部失敗の回）を BACKUP_LONG_YEARS 年残す
#           （月初に PC を止めていても、その月の最初の回が月次になる）
#   - -keep: BACKUP_LONG_YEARS 年残す（切替前など、運用者が改名した回）
#   - 規則に合わない名前は出さない（＝消さない）
prune_list() {
	local now="$1"
	shift
	local -a names=()
	local n
	for n in "$@"; do is_run_name "$n" && names+=("$n"); done
	[ "${#names[@]}" -gt 0 ] || return 0

	local y=$((10#${now:0:4} - BACKUP_LONG_YEARS))
	local cutoff_month cutoff_date
	cutoff_month="$(printf '%04d-%s' "$y" "${now:5:2}")"
	cutoff_date="$(printf '%04d-%s' "$y" "${now:5:5}")"

	local -A keep=() day_seen=() month_full=() month_any=()
	local days=0 d m
	# 日次（新しい順）。-keep は日次の数え方から外す（長期保持の規則で別に扱う）。
	while IFS= read -r n; do
		[ -n "$n" ] || continue
		case "$n" in *-keep) continue ;; esac
		d="${n:0:10}"
		if [ -z "${day_seen[$d]:-}" ]; then
			days=$((days + 1))
			day_seen[$d]="$days"
		fi
		[ "${day_seen[$d]}" -le "$BACKUP_DAILY_KEEP" ] && keep[$n]=1
	done < <(printf '%s\n' "${names[@]}" | LC_ALL=C sort -r)
	# 月次（古い順に見て、各月の最初）。
	while IFS= read -r n; do
		[ -n "$n" ] || continue
		m="${n:0:7}"
		case "$n" in
			*-keep) [[ ! "${n:0:10}" < "$cutoff_date" ]] && keep[$n]=1 ;;
			*-partial) [ -z "${month_any[$m]:-}" ] && month_any[$m]="$n" ;;
			*) [ -z "${month_full[$m]:-}" ] && month_full[$m]="$n" ;;
		esac
	done < <(printf '%s\n' "${names[@]}" | LC_ALL=C sort)
	for m in "${!month_any[@]}" "${!month_full[@]}"; do
		[[ "$m" < "$cutoff_month" ]] && continue
		if [ -n "${month_full[$m]:-}" ]; then keep[${month_full[$m]}]=1; else keep[${month_any[$m]}]=1; fi
	done
	for n in "${names[@]}"; do
		[ -n "${keep[$n]:-}" ] || printf '%s\n' "$n"
	done | LC_ALL=C sort
}

prune_target() {
	local target="$1" now="$2" kind_dir p name rc=0
	kind_dir="$target/$BACKUP_KIND"
	[ -d "$kind_dir" ] || return 0
	local -a names=()
	for p in "$kind_dir"/*; do
		[ -d "$p" ] || continue
		name="${p##*/}"
		is_run_name "$name" && names+=("$name")
	done
	[ "${#names[@]}" -gt 0 ] || return 0
	while IFS= read -r name; do
		[ -n "$name" ] || continue
		if remove_flat_dir "$kind_dir/$name"; then
			log "保持期間を過ぎた回を消しました: $kind_dir/$name"
		else
			err "消せませんでした（想定外の中身があります）: $kind_dir/$name"
			rc=1
		fi
	done < <(prune_list "$now" "${names[@]}")
	return "$rc"
}

# ---------------------------------------------------------------- main

main() {
	local rc=0 stage_rc run now staging target written=0
	case "$BACKUP_KIND" in
		postgres | vault) ;;
		*) err "BACKUP_KIND は postgres か vault です: '${BACKUP_KIND}'"; return 2 ;;
	esac
	check_recipients "$BACKUP_RECIPIENTS_FILE" || return 2
	ensure_age || return 2

	now="${BACKUP_NOW:-$(date -u +%Y-%m-%dT%H%M%SZ)}"
	if ! is_run_name "$now"; then
		err "時刻の形が不正です: $now"
		return 2
	fi
	run="$now"
	umask 077
	staging="$BACKUP_STAGING/$run"
	mkdir -p -- "$staging" || { err "staging を作れません: $staging"; return 2; }

	"stage_$BACKUP_KIND" "$staging"
	stage_rc=$?
	if ! ls "$staging"/*.age >/dev/null 2>&1; then
		err "成果物が 1 つもできませんでした。何も書きません"
		remove_flat_dir "$staging"
		return 1
	fi
	if [ "$stage_rc" -ne 0 ]; then
		run="$run-partial"
		rc=1
		err "一部の成果物が欠けています。回を $run として書きます"
	fi
	write_sums "$staging" || { err "SHA256SUMS を作れません"; remove_flat_dir "$staging"; return 1; }

	for target in $BACKUP_TARGETS; do
		if publish "$target" "$run" "$staging"; then
			written=$((written + 1))
			prune_target "$target" "$now" || rc=1
		else
			rc=1
		fi
	done
	remove_flat_dir "$staging"
	if [ "$written" -eq 0 ]; then
		err "どの保管先にも書けませんでした"
		return 1
	fi
	[ "$rc" -eq 0 ] && log "完了（保管先 $written か所）" || err "失敗があります（書けた保管先 $written か所）。上のログを確かめてください"
	return "$rc"
}

if [ "${BACKUP_LIB:-}" != "1" ]; then
	main
	exit $?
fi
