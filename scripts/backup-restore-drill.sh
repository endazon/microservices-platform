#!/usr/bin/env bash
# NFR-21, NFR-05, ADR-0002, IADR-0471 (#1560): platform-infra の Postgres バックアップ（age 暗号化）の**リストア試験**。
#
#   bash scripts/backup-restore-drill.sh --run-dir <回のディレクトリ> --identity <age の秘密鍵ファイル> [--live | --live-counts <TSV>]
#                                        [--exact] [--ledger-tables <ファイル>] [--db <DB 名>]... [--image <イメージ>]
#   bash scripts/backup-restore-drill.sh --self-test
#
# 手順（docs/operations/platform-infra-backup-runbook.md の「リストア試験」）:
#   1. 回のディレクトリの SHA256SUMS で暗号文を検証する（写しの破損を先に見つける）
#   2. 使い捨ての Postgres をコンテナで起こす（**--network none**。外から一切つながらない。ポートも開けない）
#   3. globals と各 DB を、age で復号しながらパイプで流し込む（平文をディスクへ書かない）
#   4. 戻した各 DB の全テーブルの行数を取り、稼働 DB（--live。**読み取り専用トランザクション**）か、
#      与えた TSV（--live-counts）と突き合わせる
#   5. 使い捨てのコンテナを消す（失敗しても消す）
#
# 判定:
#   - 稼働側にあって戻した側に無いテーブル …… 失敗
#   - 戻した行数 > 稼働の行数 …… 台帳（--ledger-tables に載せた追記専用のテーブル）なら失敗、それ以外は報告のみ
#   - 戻した行数 < 稼働の行数 …… 報告のみ（バックアップの後に増えた分）
#   - --exact …… 差があれば失敗（切替前など、書き込みを止めてから取った回を試すとき）
#
# 🔴 秘密鍵はパスで受け取り `age -d -i` にだけ渡す。**中身を表示・複写しない。** 環境変数では受け取らない。
# 🔴 稼働クラスタへは --live のときだけ触れ、`default_transaction_read_only=on` の psql で数えるだけ（書き込まない）。
# 🔴 --self-test は PATH 上のスタブ（docker / kubectl / age）だけで走り、稼働クラスタにも Docker にも触れない。
#
# 環境変数: CONTAINER_CLI（既定 docker。Rancher Desktop の moby）/ KUBECTL（既定 kubectl）/
#           LIVE_NAMESPACE（既定 platform-infra）/ LIVE_TARGET（既定 deploy/postgres）
set -u
set -o pipefail

AST_DBS=(audit_svc configuration_svc cost_control_svc market_monitor_svc order_execution_svc report_svc risk_management_svc)
DEFAULT_IMAGE="postgres:16-alpine"

# 全ユーザーテーブルの**正確な**行数（統計の推定値ではなく count(*)）。読み取りだけの 1 文。
COUNT_SQL="SELECT n.nspname || '.' || c.relname, (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM %I.%I', n.nspname, c.relname), false, true, '')))[1]::text FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind IN ('r', 'p') AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg_toast%' ORDER BY 1"

log() { printf '==> drill: %s\n' "$*"; }
err() { printf '==> drill: ERROR: %s\n' "$*" >&2; }

usage() {
	sed -n '4,6p' "$0" | sed 's/^# \{0,1\}//'
}

# 行数の突き合わせ（純関数）。入力はどちらも `db|schema.table|count` の行。
#   $1 = 戻した側 / $2 = 稼働側 / $3 = 台帳テーブルの一覧（`db|schema.table`。空ファイル可）/ $4 = exact（0|1）
# 出力: 1 行 1 テーブルの判定と、末尾の要約。戻り値 0 = 合格 / 1 = 失敗。
compare_counts() {
	awk -F'|' -v exact="$4" '
		FILENAME == ARGV[1] { if (NF >= 2) ledger[$1 "|" $2] = 1; next }
		FILENAME == ARGV[2] { if (NF >= 3) { r[$1 "|" $2] = $3; keys[$1 "|" $2] = 1 } next }
		FILENAME == ARGV[3] { if (NF >= 3) { l[$1 "|" $2] = $3; keys[$1 "|" $2] = 1 } next }
		END {
			ok = 0; info = 0; fail = 0
			n = 0
			for (k in keys) list[++n] = k
			# 名前順で出す（awk の for-in は順不同）
			for (i = 2; i <= n; i++) { v = list[i]; j = i - 1; while (j > 0 && list[j] > v) { list[j + 1] = list[j]; j-- } list[j + 1] = v }
			for (i = 1; i <= n; i++) {
				k = list[i]; tag = (k in ledger) ? " [台帳]" : ""
				if (!(k in r)) { printf "FAIL  %s%s: 稼働側 %s 行 / 戻した側に無い\n", k, tag, l[k]; fail++; continue }
				if (!(k in l)) {
					if (exact == 1) { printf "FAIL  %s%s: 戻した側 %s 行 / 稼働側に無い\n", k, tag, r[k]; fail++ }
					else { printf "INFO  %s%s: 戻した側 %s 行 / 稼働側に無い（バックアップの後に消えた？）\n", k, tag, r[k]; info++ }
					continue
				}
				rv = r[k] + 0; lv = l[k] + 0
				if (rv == lv) { printf "OK    %s%s: %d 行\n", k, tag, rv; ok++; continue }
				if (exact == 1) { printf "FAIL  %s%s: 戻した側 %d 行 / 稼働側 %d 行（--exact）\n", k, tag, rv, lv; fail++; continue }
				if (rv < lv) { printf "INFO  %s%s: 戻した側 %d 行 / 稼働側 %d 行（バックアップの後に %d 行増えた）\n", k, tag, rv, lv, lv - rv; info++; continue }
				if (k in ledger) { printf "FAIL  %s%s: 戻した側 %d 行 > 稼働側 %d 行（追記専用の台帳が減っている）\n", k, tag, rv, lv; fail++; continue }
				printf "INFO  %s%s: 戻した側 %d 行 > 稼働側 %d 行（状態テーブルの削除）\n", k, tag, rv, lv; info++
			}
			if (n == 0) { print "FAIL  突き合わせるテーブルが 1 つもありません"; fail++ }
			printf "要約: 一致 %d / 報告 %d / 失敗 %d\n", ok, info, fail
			exit (fail > 0) ? 1 : 0
		}
	' "$3" "$1" "$2"
}

# ---------------------------------------------------------------- 本体

DRILL_CLI=""
DRILL_NAME=""
DRILL_WORK=""
# 使い捨てのコンテナと作業ファイル（行数の TSV だけ。平文のダンプは置かない）を片付ける。
# 🔴 再帰削除をしない —— 作ったファイルを名前で消してから rmdir する。
drill_cleanup() {
	if [ -n "$DRILL_NAME" ] && [ -n "$DRILL_CLI" ]; then
		"$DRILL_CLI" rm -f "$DRILL_NAME" >/dev/null 2>&1 || true
	fi
	if [ -n "$DRILL_WORK" ] && [ -d "$DRILL_WORK" ]; then
		rm -f -- "$DRILL_WORK/restored.tsv" "$DRILL_WORK/live.tsv" "$DRILL_WORK/ledger.txt" "$DRILL_WORK/globals.err"
		rmdir -- "$DRILL_WORK" 2>/dev/null || true
	fi
}

main() {
	local run_dir="" identity="" live=0 live_counts="" exact=0 ledger="" image="$DEFAULT_IMAGE"
	local -a dbs=()
	while [ $# -gt 0 ]; do
		case "$1" in
			--run-dir) run_dir="${2:-}"; shift 2 ;;
			--identity) identity="${2:-}"; shift 2 ;;
			--live) live=1; shift ;;
			--live-counts) live_counts="${2:-}"; shift 2 ;;
			--exact) exact=1; shift ;;
			--ledger-tables) ledger="${2:-}"; shift 2 ;;
			--db) dbs+=("${2:-}"); shift 2 ;;
			--image) image="${2:-}"; shift 2 ;;
			-h | --help) usage; return 0 ;;
			*) err "未知の引数: $1"; usage >&2; return 2 ;;
		esac
	done
	[ "${#dbs[@]}" -gt 0 ] || dbs=("${AST_DBS[@]}")
	if [ -z "$run_dir" ] || [ -z "$identity" ]; then
		err "--run-dir と --identity は必須です"; usage >&2; return 2
	fi
	if [ "$live" -eq 1 ] && [ -n "$live_counts" ]; then
		err "--live と --live-counts はどちらか一方です"; return 2
	fi
	if [ "$live" -eq 0 ] && [ -z "$live_counts" ]; then
		err "突き合わせる相手（--live か --live-counts）が要ります"; return 2
	fi
	if [ ! -r "$identity" ]; then
		err "秘密鍵ファイルを読めません（パスを確かめてください。中身は表示しません）"; return 2
	fi
	if [ -n "$ledger" ] && [ ! -r "$ledger" ]; then err "台帳テーブルの一覧を読めません: $ledger"; return 2; fi
	if [ -n "$live_counts" ] && [ ! -r "$live_counts" ]; then err "稼働側の行数の TSV を読めません: $live_counts"; return 2; fi

	local db
	for db in "${dbs[@]}"; do
		if [[ ! "$db" =~ ^[A-Za-z0-9_]+$ ]]; then err "DB 名が不正です: $db"; return 2; fi
	done

	# 1. 暗号文の検証
	if [ ! -f "$run_dir/SHA256SUMS" ]; then err "SHA256SUMS がありません: $run_dir"; return 1; fi
	if ! (cd "$run_dir" && sha256sum -c SHA256SUMS >/dev/null 2>&1); then
		err "SHA256SUMS と一致しません（写しが壊れています）: $run_dir"; return 1
	fi
	for db in "${dbs[@]}"; do
		[ -f "$run_dir/pg-$db.dump.age" ] || { err "この回に $db のダンプがありません"; return 1; }
	done
	log "暗号文の検証: ok（$run_dir）"

	local kc="${KUBECTL:-kubectl}"
	local ns="${LIVE_NAMESPACE:-platform-infra}" target="${LIVE_TARGET:-deploy/postgres}"
	# 片付け（EXIT の trap）から参照するものは大域に置く（main の local は trap の時点で消えている）。
	DRILL_CLI="${CONTAINER_CLI:-docker}"
	DRILL_NAME="platform-backup-drill-$$"
	DRILL_WORK="$(mktemp -d)"
	trap drill_cleanup EXIT
	local cli="$DRILL_CLI" name="$DRILL_NAME" work="$DRILL_WORK"
	local restored="$work/restored.tsv" livef="$work/live.tsv" ledgerf="$work/ledger.txt"
	: > "$restored"; : > "$livef"; : > "$ledgerf"
	[ -n "$ledger" ] && grep -v '^[[:space:]]*\(#\|$\)' "$ledger" | tr -d '\r' > "$ledgerf"

	# 2. 使い捨ての Postgres（ネットワーク無し・ポートを開けない・パスワード不要＝外から届かない）
	log "使い捨ての Postgres を起こします（$image・--network none）"
	if ! "$cli" run -d --rm --network none --name "$name" -e POSTGRES_HOST_AUTH_METHOD=trust "$image" >/dev/null; then
		err "コンテナを起こせません（$cli）"; return 1
	fi
	local i=0
	# 初期化中の仮サーバはソケットだけで待ち受けるので、TCP（127.0.0.1）で応答したら本起動とみなす。
	until "$cli" exec "$name" pg_isready -q -h 127.0.0.1 -U postgres >/dev/null 2>&1; do
		i=$((i + 1))
		[ "$i" -ge "${DRILL_WAIT_TRIES:-60}" ] && { err "使い捨ての Postgres が起動しません"; return 1; }
		sleep "${DRILL_WAIT_INTERVAL:-1}"
	done

	# 3. globals（ロール）→ 各 DB
	if [ -f "$run_dir/pg-globals.sql.age" ]; then
		# 既に在るロール（postgres）の作成は失敗するので ON_ERROR_STOP にしない。エラーは件数だけ出す。
		if age -d -i "$identity" "$run_dir/pg-globals.sql.age" |
			"$cli" exec -i "$name" psql -q -X -h 127.0.0.1 -U postgres -d postgres >/dev/null 2>"$work/globals.err"; then
			local gerr
			gerr="$(grep -c 'ERROR' "$work/globals.err" 2>/dev/null)" || true
			log "globals: 流し込みました（エラー ${gerr:-0} 件。既存ロールの作成失敗は想定内）"
		else
			err "globals を流し込めません"; return 1
		fi
	else
		log "globals の成果物がありません（-partial の回？）。ロール無しで戻します"
	fi
	for db in "${dbs[@]}"; do
		if ! "$cli" exec "$name" createdb -h 127.0.0.1 -U postgres "$db" >/dev/null 2>&1; then
			err "使い捨ての Postgres に $db を作れません"; return 1
		fi
		if ! age -d -i "$identity" "$run_dir/pg-$db.dump.age" |
			"$cli" exec -i "$name" pg_restore -h 127.0.0.1 -U postgres -d "$db" --exit-on-error >/dev/null; then
			err "$db を戻せません"; return 1
		fi
		if ! "$cli" exec "$name" psql -X -A -t -F '|' -h 127.0.0.1 -U postgres -d "$db" -c "$COUNT_SQL" |
			tr -d '\r' | sed "/^\$/d; s/^/$db|/" >> "$restored"; then
			err "$db の行数を取れません（戻した側）"; return 1
		fi
		log "$db: 戻しました"
	done

	# 4. 稼働側の行数
	if [ "$live" -eq 1 ]; then
		log "稼働 DB の行数を読み取り専用で取ります（$ns/$target）"
		for db in "${dbs[@]}"; do
			if ! "$kc" -n "$ns" exec -i "$target" -- env 'PGOPTIONS=-c default_transaction_read_only=on' \
				psql -X -A -t -F '|' -U postgres -d "$db" -c "$COUNT_SQL" |
				tr -d '\r' | sed "/^\$/d; s/^/$db|/" >> "$livef"; then
				err "$db の行数を取れません（稼働側）"; return 1
			fi
		done
	else
		tr -d '\r' < "$live_counts" | grep -v '^[[:space:]]*\(#\|$\)' > "$livef"
	fi

	# 5. 突き合わせ
	log "行数の突き合わせ（exact=$exact）"
	compare_counts "$restored" "$livef" "$ledgerf" "$exact"
}

# ---------------------------------------------------------------- self-test

self_test() {
	local passed=0 failed=0
	ok() { passed=$((passed + 1)); printf '  ok    %s\n' "$1"; }
	ng() { failed=$((failed + 1)); printf '  NG    %s\n        %s\n' "$1" "$2"; }
	has() { case "$2" in *"$3"*) ok "$1" ;; *) ng "$1" "expected to contain: $3" ;; esac; }
	lacks() { case "$2" in *"$3"*) ng "$1" "expected NOT to contain: $3" ;; *) ok "$1" ;; esac; }
	eq() { [ "$2" = "$3" ] && ok "$1" || ng "$1" "expected: $3 / actual: $2"; }

	local w
	w="$(mktemp -d)"
	# ---- 純関数: compare_counts ----
	printf 'audit_svc|public.audit_log|10\naudit_svc|public.state|5\n' > "$w/r.tsv"
	printf 'audit_svc|public.audit_log|10\naudit_svc|public.state|5\n' > "$w/l.tsv"
	printf 'audit_svc|public.audit_log\n' > "$w/ledger.txt"
	: > "$w/empty.txt"
	compare_counts "$w/r.tsv" "$w/l.tsv" "$w/ledger.txt" 0 >/dev/null; eq 'T-1560-30 一致なら合格' "$?" "0"

	printf 'audit_svc|public.audit_log|10\naudit_svc|public.state|5\n' > "$w/r.tsv"
	printf 'audit_svc|public.audit_log|12\naudit_svc|public.state|5\n' > "$w/l.tsv"
	OUT="$(compare_counts "$w/r.tsv" "$w/l.tsv" "$w/ledger.txt" 0)"; RC=$?
	eq 'T-1560-31 バックアップの後に台帳が増えただけなら合格' "$RC" "0"
	has 'T-1560-31 増えた行数を報告する' "$OUT" '2 行増えた'
	compare_counts "$w/r.tsv" "$w/l.tsv" "$w/ledger.txt" 1 >/dev/null; eq 'T-1560-32 --exact では差があれば失敗' "$?" "1"

	printf 'audit_svc|public.audit_log|12\n' > "$w/r.tsv"
	printf 'audit_svc|public.audit_log|10\n' > "$w/l.tsv"
	OUT="$(compare_counts "$w/r.tsv" "$w/l.tsv" "$w/ledger.txt" 0)"; RC=$?
	eq 'T-1560-33 台帳が稼働側より多い（台帳が減った）なら失敗' "$RC" "1"
	has 'T-1560-33 台帳の印を付けて報告する' "$OUT" '[台帳]'
	compare_counts "$w/r.tsv" "$w/l.tsv" "$w/empty.txt" 0 >/dev/null; eq 'T-1560-34 台帳でないテーブルが減ったのは報告のみ' "$?" "0"

	printf 'audit_svc|public.state|5\n' > "$w/r.tsv"
	printf 'audit_svc|public.audit_log|10\naudit_svc|public.state|5\n' > "$w/l.tsv"
	compare_counts "$w/r.tsv" "$w/l.tsv" "$w/empty.txt" 0 >/dev/null; eq 'T-1560-35 戻した側に無いテーブルがあれば失敗' "$?" "1"
	: > "$w/r0.tsv"; : > "$w/l0.tsv"
	compare_counts "$w/r0.tsv" "$w/l0.tsv" "$w/empty.txt" 0 >/dev/null; eq 'T-1560-36 突き合わせる行が 0 件なら失敗（空の突合を合格にしない）' "$?" "1"

	# ---- 本体をスタブの下で走らせる（稼働クラスタ・Docker に触れない） ----
	mkdir -p "$w/bin" "$w/state" "$w/run"
	local log_file="$w/stub.log"; : > "$log_file"
	cat > "$w/bin/docker" <<'STUB'
#!/usr/bin/env bash
echo "docker $*" >> "$STUB_LOG"
case "$*" in
  run\ *) echo "stub-container-id"; exit 0 ;;
  rm\ *) exit 0 ;;
  *pg_isready*) exit 0 ;;
  *createdb*) exit 0 ;;
  *pg_restore*) cat > /dev/null; [ -f "$STUB_STATE/restore-fail" ] && exit 1; exit 0 ;;
  *psql*-c*) db=""; prev=""; for a in "$@"; do [ "$prev" = "-d" ] && db="$a"; prev="$a"; done; cat "$STUB_STATE/restored-$db.tsv" 2>/dev/null; exit 0 ;;
  *psql*) cat > /dev/null; exit 0 ;;
esac
exit 0
STUB
	cat > "$w/bin/kubectl" <<'STUB'
#!/usr/bin/env bash
echo "kubectl $*" >> "$STUB_LOG"
db=""; prev=""; for a in "$@"; do [ "$prev" = "-d" ] && db="$a"; prev="$a"; done
cat "$STUB_STATE/live-$db.tsv" 2>/dev/null
exit 0
STUB
	cat > "$w/bin/age" <<'STUB'
#!/usr/bin/env bash
echo "age $*" >> "$STUB_LOG"
id=""; prev=""; for a in "$@"; do [ "$prev" = "-i" ] && id="$a"; prev="$a"; done
[ -r "$id" ] || exit 1
printf 'PLAINTEXT-STREAM'
STUB
	chmod +x "$w/bin/"*
	local secret="AGE-SECRET-KEY-1STUBSTUBSTUBDONOTPRINT"
	printf '%s\n' "$secret" > "$w/identity.txt"
	for f in pg-globals.sql.age pg-audit_svc.dump.age pg-report_svc.dump.age; do printf 'cipher-%s' "$f" > "$w/run/$f"; done
	(cd "$w/run" && sha256sum -- *.age > SHA256SUMS)
	printf 'public.audit_log|7\n' > "$w/state/restored-audit_svc.tsv"
	printf 'public.report|3\n' > "$w/state/restored-report_svc.tsv"
	printf 'public.audit_log|9\n' > "$w/state/live-audit_svc.tsv"
	printf 'public.report|3\n' > "$w/state/live-report_svc.tsv"

	drill() {
		# 🔴 PATH の先頭にスタブ・KUBECONFIG は存在しないパス —— 万一スタブを外れても稼働クラスタへ届かない。
		PATH="$w/bin:$PATH" KUBECONFIG="$w/no-such-kubeconfig" STUB_LOG="$log_file" STUB_STATE="$w/state" \
			CONTAINER_CLI=docker KUBECTL=kubectl DRILL_WAIT_TRIES=2 DRILL_WAIT_INTERVAL=0 \
			bash "$0" "$@" 2>&1
	}

	: > "$log_file"
	OUT="$(drill --run-dir "$w/run" --identity "$w/identity.txt" --live --db audit_svc --db report_svc)"; RC=$?
	eq 'T-1560-37 --live: 戻して突き合わせ、合格する（台帳が増えただけ）' "$RC" "0"
	LOG="$(cat "$log_file")"
	has 'T-1560-37 使い捨てのコンテナはネットワーク無しで起こす' "$LOG" 'run -d --rm --network none'
	lacks 'T-1560-37 使い捨てのコンテナはポートを開けない' "$LOG" ' -p '
	has 'T-1560-37 稼働側は読み取り専用トランザクションで数える' "$LOG" 'PGOPTIONS=-c default_transaction_read_only=on psql'
	has 'T-1560-37 稼働側は platform-infra の postgres だけを見る' "$LOG" 'kubectl -n platform-infra exec -i deploy/postgres'
	lacks 'T-1560-37 稼働側へ書き込みの動詞を投げない（apply / delete / scale / patch）' "$(grep '^kubectl' "$log_file")" ' apply '
	lacks 'T-1560-37 稼働側へ delete を投げない' "$(grep '^kubectl' "$log_file")" ' delete '
	has 'T-1560-37 秘密鍵はパスで age -d -i に渡す' "$LOG" "age -d -i $w/identity.txt"
	lacks 'T-1560-37 秘密鍵の中身が出力に出ない' "$OUT" "$secret"
	lacks 'T-1560-37 秘密鍵の中身がどのコマンドの引数にも出ない' "$LOG" "$secret"
	has 'T-1560-37 使い捨てのコンテナを最後に消す' "$LOG" 'docker rm -f platform-backup-drill-'
	has 'T-1560-37 globals を先に流し込む' "$LOG" 'psql -q -X -h 127.0.0.1 -U postgres -d postgres'

	: > "$log_file"
	: > "$w/state/restore-fail"
	OUT="$(drill --run-dir "$w/run" --identity "$w/identity.txt" --live --db audit_svc)"; RC=$?
	rm -f "$w/state/restore-fail"
	eq 'T-1560-38 戻せなければ失敗' "$RC" "1"
	has 'T-1560-38 失敗しても使い捨てのコンテナを消す' "$(cat "$log_file")" 'docker rm -f platform-backup-drill-'

	printf 'audit_svc|public.audit_log|7\nreport_svc|public.report|3\n' > "$w/live.tsv"
	: > "$log_file"
	OUT="$(drill --run-dir "$w/run" --identity "$w/identity.txt" --live-counts "$w/live.tsv" --exact --db audit_svc --db report_svc)"; RC=$?
	eq 'T-1560-39 --live-counts ＋ --exact: 一致すれば合格' "$RC" "0"
	lacks 'T-1560-39 --live-counts なら稼働クラスタに触れない' "$(cat "$log_file")" 'kubectl'

	printf 'tampered' > "$w/run/pg-report_svc.dump.age"
	: > "$log_file"
	OUT="$(drill --run-dir "$w/run" --identity "$w/identity.txt" --live-counts "$w/live.tsv" --db report_svc)"; RC=$?
	eq 'T-1560-40 暗号文が SHA256SUMS と合わなければ失敗' "$RC" "1"
	lacks 'T-1560-40 検証に落ちたらコンテナを起こさない' "$(cat "$log_file")" 'docker run'

	OUT="$(drill --run-dir "$w/run" --identity "$w/no-such-identity" --live --db audit_svc)"; RC=$?
	eq 'T-1560-41 秘密鍵ファイルが無ければ使い方の誤りで止まる' "$RC" "2"
	OUT="$(drill --run-dir "$w/run" --identity "$w/identity.txt" --db audit_svc)"; RC=$?
	eq 'T-1560-41 突き合わせる相手が無ければ止まる' "$RC" "2"
	OUT="$(drill --run-dir "$w/run" --identity "$w/identity.txt" --live --db 'x;drop')"; RC=$?
	eq 'T-1560-41 不正な DB 名は止まる' "$RC" "2"

	printf '\nbackup-restore-drill self-test: %d passed, %d failed\n' "$passed" "$failed"
	[ "$failed" -eq 0 ]
}

if [ "${1:-}" = "--self-test" ]; then
	self_test
	exit $?
fi
main "$@"
exit $?
