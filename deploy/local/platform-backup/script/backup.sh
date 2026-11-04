#!/bin/bash
# NFR-21, NFR-05, ADR-0002, ADR-0008, IADR-0471 (#1560): platform-infra の Postgres / Vault を
# **age の公開鍵で暗号化して**クラスタ外の 2 か所（hostPath）へ置く CronJob の本体。
#
# 🔴 deploy/local 専用（ローカル開発・PoC 環境）。本番のバックアップ設計ではない。
#
# 1 回の実行で 1 種類（BACKUP_KIND=postgres | vault）を扱う:
#   1. 受取人（公開鍵）を検査する。無い・占位のまま・不正な行があれば**何もせずに失敗する**（fail-closed）
#   1b. ［2026-11-04 / #2394・IADR-0670］書き始める前に、依存先（postgres: pg_isready / vault: sys/health）が揃うのを
#      上限つきで待つ（PC の復帰の直後の後追いの回）。上限を過ぎたら何も作らずに失敗する。待つのはここだけで、再試行はしない
#   2. 成果物を staging（emptyDir）へ作る。**平文をディスクへ書かない** —— 生成器の出力を age へパイプで渡す
#        postgres: DB ごとに `pg_dump -Fc` ＋ `pg_dumpall --globals-only`
#        vault:    raft の整合したスナップショット（k8s auth・最小ポリシー。中の SHA256SUMS で検算）＋ init のファイル（IADR-0548 / #1868）
#   3. 各保管先へ `.incoming-<回>` として写し、SHA256SUMS で検証してから `<回>` へ改名する
#      ［2026-11-04 / #2389・IADR-0669］保管先ごと・種類ごとのロック（`.publish.lock`）の中で書く。incoming は回ごとに一意
#      （`.incoming-<回>-<ホスト名>-<PID>`）で、改名の後に保管先の回でもう一度検算してから「書きました」を出す
#      **目印ファイル（.platform-backup-target）が無い保管先には書かない**（ドライブが外れていると kubelet が
#      ディストリ内に空のディレクトリを作る。そこはクラスタ外ではない）
#   4. 保持の規則で古い回を消す（名前の規則に合うディレクトリだけ・再帰削除をしない）
#   片方の保管先に書けなくてももう片方には書く。**どこかで失敗したら exit 1**（Job を失敗として見せる）。
#
# 🔴 秘密の値（DB のパスワード・Vault のトークン・ServiceAccount の JWT・unseal 鍵）をログ・引数へ出さない。
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
VAULT_DATA_DIR="${VAULT_DATA_DIR:-/vault/data}"
# NFR-21, IADR-0669 (#2389): 保管先ごとのロック。待つ上限と、古い（強制終了の残骸の）ロックと見なす経過の秒。
# 古さの既定は、Job の activeDeadlineSeconds（postgres 3600・vault 3600。IADR-0670 で vault を 1800 から上げた）の 2 倍である
# （生きた回のロックを壊さない）。
BACKUP_LOCK_WAIT_SECONDS="${BACKUP_LOCK_WAIT_SECONDS:-900}"
BACKUP_LOCK_STALE_SECONDS="${BACKUP_LOCK_STALE_SECONDS:-7200}"
# 破棄の印（<ロック>.break）の残骸と見なす経過の秒（印を取った直後の強制終了。ミリ秒の窓）。
BACKUP_LOCK_BREAK_STALE_SECONDS="${BACKUP_LOCK_BREAK_STALE_SECONDS:-300}"
BACKUP_LOCK_NAME=".publish.lock"
# NFR-21, IADR-0670 (#2394): 書き始める前に依存先の起動を待つ上限と間隔。
# 最悪の合計（依存先の待ち＋1 回の確かめの上限＋間隔＋保管先の数×ロックの待ち＋作業の余裕 300）が、Job の
# activeDeadlineSeconds（postgres・vault とも 3600）より余りを持って小さい（試験 T-2394-06 で固定する）。
BACKUP_DEPS_WAIT_SECONDS="${BACKUP_DEPS_WAIT_SECONDS:-600}"
BACKUP_DEPS_WAIT_INTERVAL="${BACKUP_DEPS_WAIT_INTERVAL:-5}"
# 1 回の確かめの上限（#2395 のレビュー 🟡1）。応答しない宛先（パケットを捨てる）で、/dev/tcp の接続や応答の待ちが
# OS の時間切れ（約 2 分）や BACKUP_VAULT_HTTP_TIMEOUT（300 秒）まで止まらないようにする。
BACKUP_DEPS_PROBE_TIMEOUT="${BACKUP_DEPS_PROBE_TIMEOUT:-10}"
# 自分が持っているロック（保管先の kind_dir）。trap で解放する。
HELD_LOCKS=()

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
# 🔴 **age の読み方と同じに読む**（前後の空白を削らない）。age（`-R`）は行末の CR だけを落とし
#    （Go の bufio.ScanLines）、空行と「# で始まる行」だけを読み飛ばす。前後に空白のある行や空白だけの行は
#    age では不正な受取人になる —— ここで削って通すと、失敗が pg_dump のパイプの側で出て原因が読めなくなる。
check_recipients() {
	local file="$1" line n=0 lineno=0
	if [ ! -r "$file" ]; then
		err "age の受取人ファイルがありません: $file（ConfigMap platform-backup-age-recipients を作ってください）"
		return 1
	fi
	while IFS= read -r line || [ -n "$line" ]; do
		lineno=$((lineno + 1))
		line="${line%$'\r'}"
		case "$line" in '' | '#'*) continue ;; esac
		if [[ "$line" =~ ^[[:space:]] || "$line" =~ [[:space:]]$ ]]; then
			err "受取人ファイル $file の ${lineno} 行目の前後に空白があります（age は空白を削らずに読むため、この行は受取人として使えません）"
			return 1
		fi
		if [[ ! "$line" =~ $AGE_RECIPIENT_RE ]]; then
			err "受取人ファイル $file の ${lineno} 行目が age の公開鍵（age1...）ではありません（占位のままではないか確かめてください）"
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

# age はイメージ（deploy/local/platform-backup/image/Dockerfile）に同梱してある。
# 🔴 ［2026-09-26 / #1564, IADR-0471 決定 3 追記］**実行時にパッケージを入れない。** 従前は BACKUP_AGE_INSTALL=1 で
#    `apk add age` を毎回ネットワーク越しに行っていた（版が固定されず、日次の回がインターネットへの到達に依存し、
#    root で hostPath と unseal の材料を持つコンテナにパッケージの侵害がそのまま届く）。退避路としても残さない ——
#    env 1 つで同じ経路が開くからである。無ければイメージの取り違えとして、何も書かずに失敗する。
ensure_age() {
	if command -v age >/dev/null 2>&1; then return 0; fi
	err "age がありません（イメージが k3d-local/platform-backup ではない可能性があります。暗号化できないため何も書きません）"
	return 1
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

# NFR-21, NFR-18, IADR-0548 (#1868): 秘匿管理（OpenBao・raft）の回は、raft の**整合したスナップショット**
# （`GET /v1/sys/storage/raft/snapshot`。`bao operator raft snapshot save` と同じ API）と init のファイル（unseal 鍵）を写す。
# 従前の「稼働中の /vault/data を tar で写し、前後のファイル一覧を比べる」は、bbolt がページをその場で書き換えるため
# 同じ秒・同じサイズの書き込みを見分けられず、破れた写しを成功として残し得た（#1868）。
#
# - 認証は k8s auth（専用の ServiceAccount platform-backup-vault の projected token → role platform-backup-vault）。
#   role のポリシーは `sys/storage/raft/snapshot` の read だけ（deploy/local/platform-backup/vault/policy-raft-snapshot.hcl）。
#   🔴 root トークンを使わない（init のファイルに初期 root トークンが在るが、読むのは暗号化して写すためだけ）。
#   得たトークンは 1 回きり（token_num_uses=1）・短命（5 分）で、スナップショットの 1 要求で使い切る。
# - HTTP はイメージに道具を足さずに bash の /dev/tcp で話す（HTTP/1.0・平文。秘匿管理はクラスタ内で tls_disable）。
#   🔴 トークンと JWT は**引数に載せない**（printf は bash の組み込みで、プロセスの argv に現れない。curl / wget の
#   ヘッダ引数は /proc/<pid>/cmdline に出る）。ログにも出さない。
# - 🔴 fail-closed: 応答が 200 でない・空・gzip / tar として壊れている・中の SHA256SUMS と meta.json / state.bin が
#   一致しない、のどれでも**写しを置かない**（スナップショットの成果物が無ければ回そのものを書かない）。
# - スナップショットは平文のまま staging へ置かず、メモリの emptyDir（BACKUP_VAULT_SCRATCH）で検査してから age へ渡し、消す。
#   中身は秘匿管理の鍵（barrier）で暗号化されたままだが、それでも平文をディスクへ書かない約束は守る。
BACKUP_VAULT_ADDR="${BACKUP_VAULT_ADDR:-http://vault.platform-infra:8200}"
BACKUP_VAULT_AUTH_ROLE="${BACKUP_VAULT_AUTH_ROLE:-platform-backup-vault}"
BACKUP_VAULT_SA_JWT_FILE="${BACKUP_VAULT_SA_JWT_FILE:-/var/run/secrets/platform-backup/jwt}"
BACKUP_VAULT_SCRATCH="${BACKUP_VAULT_SCRATCH:-/scratch}"
BACKUP_VAULT_HTTP_TIMEOUT="${BACKUP_VAULT_HTTP_TIMEOUT:-300}"
VAULT_INIT_FILE="${VAULT_INIT_FILE:-$VAULT_DATA_DIR/.local-dev-init}"
VAULT_HTTP_STATUS=""

# vault_http <method> <api-path> <out-file> [<token>] [<json-body>]
# 秘匿管理へ 1 要求を送り、応答の本文を <out-file> へ、状態コードを VAULT_HTTP_STATUS へ置く。
# 0 = 応答を最後まで受け取った（状態コードは問わない）/ 1 = 接続・送受信の失敗（本文は不完全）。
# HTTP/1.0 で送る —— サーバは chunked を使わず、本文の終わりで接続を閉じる（本文をそのまま写せる）。
vault_http() {
	local method="$1" api="$2" out="$3" token="${4:-}" body="${5:-}"
	local hostport host port fd line len req headers_done=0
	VAULT_HTTP_STATUS=""
	case "$BACKUP_VAULT_ADDR" in
		http://*) ;;
		*) err "BACKUP_VAULT_ADDR は http://<ホスト>:<ポート> の形にしてください: $BACKUP_VAULT_ADDR"; return 1 ;;
	esac
	hostport="${BACKUP_VAULT_ADDR#http://}"
	hostport="${hostport%%/*}"
	host="${hostport%:*}"
	port="${hostport##*:}"
	if [ "$host" = "$hostport" ] || [ -z "$host" ] || [[ ! "$port" =~ ^[0-9]+$ ]]; then
		err "BACKUP_VAULT_ADDR にポートがありません: $BACKUP_VAULT_ADDR"
		return 1
	fi
	if ! { exec {fd}<>"/dev/tcp/$host/$port"; } 2>/dev/null; then
		err "秘匿管理へ接続できません: $hostport"
		return 1
	fi
	len="$(LC_ALL=C; printf '%s' "${#body}")"
	# 要求は変数に組んでから 1 度で書く（printf -v は組み込み。トークン・JWT は argv に出ない）。
	printf -v req '%s %s HTTP/1.0\r\nHost: %s\r\nConnection: close\r\n' "$method" "$api" "$hostport"
	if [ -n "$token" ]; then printf -v req '%sX-Vault-Token: %s\r\n' "$req" "$token"; fi
	if [ -n "$body" ]; then
		printf -v req '%sContent-Type: application/json\r\nContent-Length: %s\r\n\r\n%s' "$req" "$len" "$body"
	else
		printf -v req '%s\r\n' "$req"
	fi
	if ! { printf '%s' "$req"; } 1>&"$fd" 2>/dev/null; then
		req=""
		exec {fd}>&-
		err "秘匿管理へ要求を送れません: $hostport"
		return 1
	fi
	req=""
	if ! IFS= read -r -t "$BACKUP_VAULT_HTTP_TIMEOUT" line <&"$fd"; then
		exec {fd}>&-
		err "秘匿管理から応答がありません: $hostport"
		return 1
	fi
	line="${line%$'\r'}"
	if [[ ! "$line" =~ ^HTTP/1\.[01]\ ([0-9]{3}) ]]; then
		exec {fd}>&-
		err "秘匿管理の応答が HTTP ではありません"
		return 1
	fi
	VAULT_HTTP_STATUS="${BASH_REMATCH[1]}"
	# ヘッダを読み捨てる（bash の read はソケットから 1 バイトずつ読むので、本文の頭を食わない）。
	while IFS= read -r -t "$BACKUP_VAULT_HTTP_TIMEOUT" line <&"$fd"; do
		line="${line%$'\r'}"
		if [ -z "$line" ]; then headers_done=1; break; fi
	done
	if [ "$headers_done" -ne 1 ]; then
		exec {fd}>&-
		err "秘匿管理の応答のヘッダが途中で切れました"
		return 1
	fi
	if ! timeout "$BACKUP_VAULT_HTTP_TIMEOUT" cat <&"$fd" > "$out"; then
		exec {fd}>&-
		err "秘匿管理の応答の本文を受け取れません（時間切れか切断）"
		return 1
	fi
	exec {fd}>&-
	return 0
}

# 失敗の応答から errors の文言だけを抜き出す（Vault の errors は秘密を含まない。長さを切る）。
vault_errors() { sed -n 's/.*"errors":\[\([^]]*\)\].*/\1/p' "$1" 2>/dev/null | head -c 200; }

# k8s auth でログインし、トークンを VAULT_SNAPSHOT_TOKEN へ置く（標準出力へ出さない）。<作業ファイル> は応答の置き場で、読んだら消す。
# ログインの入力（role の形・ServiceAccount のトークンの在りかと JWT の形）を確かめる。秘匿管理へは何も送らない。
# #2394・IADR-0670: 依存先を待つ前にも呼ぶ（構成の誤りは待たずに失敗させる。待つ間の sys/health の前に確かめる）。
check_vault_login_inputs() {
	local jwt
	if [[ ! "$BACKUP_VAULT_AUTH_ROLE" =~ ^[a-z0-9][a-z0-9-]*$ ]]; then
		err "BACKUP_VAULT_AUTH_ROLE の形が不正です: $BACKUP_VAULT_AUTH_ROLE"
		return 1
	fi
	if [ ! -r "$BACKUP_VAULT_SA_JWT_FILE" ]; then
		err "ServiceAccount のトークンがありません: $BACKUP_VAULT_SA_JWT_FILE（CronJob の projected volume を確かめてください）"
		return 1
	fi
	jwt="$(< "$BACKUP_VAULT_SA_JWT_FILE")"
	jwt="${jwt%$'\n'}"
	# JWT は base64url の 3 区画。それ以外（空・引用符・改行）は本文へ埋めない。
	if [[ ! "$jwt" =~ ^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$ ]]; then
		err "ServiceAccount のトークンが JWT の形ではありません: $BACKUP_VAULT_SA_JWT_FILE"
		return 1
	fi
	return 0
}

vault_login_k8s() {
	local resp="$1" jwt body
	VAULT_SNAPSHOT_TOKEN=""
	check_vault_login_inputs || return 1
	jwt="$(< "$BACKUP_VAULT_SA_JWT_FILE")"
	jwt="${jwt%$'\n'}"
	printf -v body '{"role":"%s","jwt":"%s"}' "$BACKUP_VAULT_AUTH_ROLE" "$jwt"
	if ! vault_http POST /v1/auth/kubernetes/login "$resp" "" "$body"; then
		rm -f -- "$resp"
		return 1
	fi
	if [ "$VAULT_HTTP_STATUS" != "200" ]; then
		err "秘匿管理へログインできません（HTTP $VAULT_HTTP_STATUS: $(vault_errors "$resp")）。role $BACKUP_VAULT_AUTH_ROLE と k8s auth の設定（deploy/local/platform-backup/vault/bootstrap.sh）を確かめてください"
		rm -f -- "$resp"
		return 1
	fi
	VAULT_SNAPSHOT_TOKEN="$(sed -n 's/.*"client_token":"\([^"]*\)".*/\1/p' "$resp" | head -n 1)"
	rm -f -- "$resp"
	if [[ ! "$VAULT_SNAPSHOT_TOKEN" =~ ^[A-Za-z0-9._-]+$ ]]; then
		VAULT_SNAPSHOT_TOKEN=""
		err "ログインの応答にトークンがありません"
		return 1
	fi
	return 0
}

# raft のスナップショット（gzip の tar）が整合しているかを確かめる。🔴 どれかに当たれば非 0（写しを置かない）。
#   - 空でない・gzip として最後まで読める
#   - meta.json・state.bin・SHA256SUMS を含み、state.bin が空でない
#   - SHA256SUMS の meta.json / state.bin の行が、中身から計算した値と一致する（OpenBao が書いた要約で検算する）
check_raft_snapshot() {
	local f="$1" list name want got
	if [ ! -s "$f" ]; then
		err "スナップショットが空です"
		return 1
	fi
	if ! gzip -t "$f" 2>/dev/null; then
		err "スナップショットが gzip として壊れています（途中で切れた可能性）"
		return 1
	fi
	if ! list="$(tar -tzf "$f" 2>/dev/null)"; then
		err "スナップショットが tar として壊れています"
		return 1
	fi
	for name in meta.json state.bin SHA256SUMS; do
		if ! grep -qxF "$name" <<< "$list"; then
			err "スナップショットに $name がありません（raft のスナップショットではない）"
			return 1
		fi
	done
	for name in meta.json state.bin; do
		want="$(tar -xzOf "$f" SHA256SUMS 2>/dev/null | awk -v n="$name" '$2 == n { print $1; exit }')"
		got="$(tar -xzOf "$f" "$name" 2>/dev/null | sha256sum | cut -d ' ' -f 1)"
		if [ -z "$want" ] || [ "$want" != "$got" ]; then
			err "スナップショットの $name が中の SHA256SUMS と一致しません（破れた写し）"
			return 1
		fi
	done
	if [ "$(tar -xzOf "$f" state.bin 2>/dev/null | wc -c)" -eq 0 ]; then
		err "スナップショットの state.bin が空です"
		return 1
	fi
	return 0
}

# スナップショットを <out> へ取る（ログイン → GET → 状態コードと整合の検査）。失敗なら <out> を残さない。
fetch_raft_snapshot() {
	local out="$1" scratch_dir
	scratch_dir="$(dirname -- "$out")"
	vault_login_k8s "$scratch_dir/login.json" || return 1
	if ! vault_http GET /v1/sys/storage/raft/snapshot "$out" "$VAULT_SNAPSHOT_TOKEN"; then
		VAULT_SNAPSHOT_TOKEN=""
		rm -f -- "$out"
		return 1
	fi
	VAULT_SNAPSHOT_TOKEN=""
	if [ "$VAULT_HTTP_STATUS" != "200" ]; then
		err "スナップショットを取れません（HTTP $VAULT_HTTP_STATUS: $(vault_errors "$out")）。role のポリシー（sys/storage/raft/snapshot の read）を確かめてください"
		rm -f -- "$out"
		return 1
	fi
	if ! check_raft_snapshot "$out"; then
		rm -f -- "$out"
		return 1
	fi
	return 0
}

stage_vault() {
	local dir="$1" scratch snap size
	scratch="$BACKUP_VAULT_SCRATCH/platform-backup-$$"
	if ! mkdir -p -- "$scratch"; then
		err "作業場所を作れません: $scratch"
		return 2
	fi
	snap="$scratch/raft.snap"
	if ! fetch_raft_snapshot "$snap"; then
		rm -f -- "$snap" "$scratch/login.json"
		rmdir -- "$scratch" 2>/dev/null
		err "整合したスナップショットを取れませんでした。秘匿管理の回は書きません"
		return 2
	fi
	size="$(wc -c < "$snap")"
	if ! encrypt_to "$dir/vault-raft.snap.age" < "$snap"; then
		err "スナップショットを暗号化できません"
		rm -f -- "$snap" "$dir/vault-raft.snap.age"
		rmdir -- "$scratch" 2>/dev/null
		return 2
	fi
	rm -f -- "$snap"
	rmdir -- "$scratch" 2>/dev/null
	log "raft snapshot: ok（${size} バイト・整合を検査済み）"
	# init のファイル（unseal 鍵）。スナップショットは同じ鍵でしか開けないので、同じ回に並べる。
	# 欠けたら回は -partial（同じ秘匿管理へ戻すことはできるが、失ったときに開けない）。
	if [ ! -r "$VAULT_INIT_FILE" ]; then
		err "init のファイルがありません: $VAULT_INIT_FILE（スナップショットだけを書きます。失ったときにこの回は開けません）"
		return 1
	fi
	if ! grep -q '^Unseal Key 1: ' "$VAULT_INIT_FILE"; then
		err "init のファイルに unseal 鍵の行がありません: $VAULT_INIT_FILE"
		return 1
	fi
	if ! encrypt_to "$dir/vault-init.age" < "$VAULT_INIT_FILE"; then
		err "init のファイルを暗号化できません"
		rm -f -- "$dir/vault-init.age"
		return 1
	fi
	log "init（unseal 鍵）: ok"
	return 0
}

# ---------------------------------------------------------------- 依存先の起動を待つ（#2394・IADR-0670）
#
# PC の復帰（または起動）の直後に、CronJob は取りこぼした回を後追いで起こす（startingDeadlineSeconds）。その時点では
# Postgres・秘匿管理・名前解決がまだ立ち上がっていないことが多く、backoffLimit: 0 なので 1 回の失敗で後追いが終わっていた。
# 🔴 書き始める前（staging を作る前）に、依存先が揃うのを上限つきで待つ。待つのはここだけである —— 書き込みの途中の失敗を
#    再試行しない（backoffLimit: 0 の意図を保つ）。上限を過ぎたら、何も作らず・何も書かずに失敗する（fail-closed）。

# 1 回の確かめを上限つきで走らせる（#2395 のレビュー 🟡1）。シェルの関数も包めるように、`timeout` ではなく
# 子のサブシェル＋見張りで止める。上限を過ぎたら子を止め、失敗（確かめが済まなかった）として返す。
run_probe_with_timeout() {
	local secs="$1" pid watcher rc
	shift
	( "$@" ) &
	pid=$!
	( trap 'kill "$s" 2>/dev/null; exit 0' TERM; sleep "$secs" & s=$!; wait "$s"; kill -TERM "$pid" 2>/dev/null ) &
	watcher=$!
	wait "$pid"
	rc=$?
	kill -TERM "$watcher" 2>/dev/null
	wait "$watcher" 2>/dev/null
	return "$rc"
}

# Postgres が接続を受け付けるか（PGHOST の名前解決を含む）。libpq の接続の時間切れも同じ上限にする。
# pg_isready を子のサブシェルの唯一のコマンドとして走らせる（bash はそれを exec するので、止めるとき pg_isready そのものに届く）。
deps_ready_postgres() {
	run_probe_with_timeout "$BACKUP_DEPS_PROBE_TIMEOUT" pg_isready -q -t "$BACKUP_DEPS_PROBE_TIMEOUT" >/dev/null 2>&1
}

# 秘匿管理が初期化済み・unseal 済み・active か（sys/health が 200。名前解決と接続を含む）。本文は読まない。
# 接続の失敗の文言（vault_http の err）は、待つ間は出さない（待ち切れたときに 1 度だけ告げる）。
deps_probe_vault() {
	vault_http GET /v1/sys/health /dev/null 2>/dev/null && [ "$VAULT_HTTP_STATUS" = "200" ]
}
deps_ready_vault() { run_probe_with_timeout "$BACKUP_DEPS_PROBE_TIMEOUT" deps_probe_vault; }

# 待ち切れたときに、最後の確かめの理由を 1 度だけ出す（これも 1 回の確かめの上限の内側）。
deps_reason_vault() {
	if vault_http GET /v1/sys/health /dev/null; then
		err "秘匿管理の sys/health が 200 ではありません（HTTP $VAULT_HTTP_STATUS。初期化・unseal・起動の途中）"
	fi
}
deps_reason() {
	local why
	case "$BACKUP_KIND" in
		postgres)
			# pg_isready を直に包む（関数の中の子にすると、止めたときにその子が残る）。
			why="$(run_probe_with_timeout "$BACKUP_DEPS_PROBE_TIMEOUT" pg_isready -t "$BACKUP_DEPS_PROBE_TIMEOUT" 2>&1 | head -n 1)"
			err "Postgres の確かめ: ${why:-${BACKUP_DEPS_PROBE_TIMEOUT} 秒以内に応答がありません}"
			;;
		vault)
			run_probe_with_timeout "$BACKUP_DEPS_PROBE_TIMEOUT" deps_reason_vault ||
				err "秘匿管理の確かめも ${BACKUP_DEPS_PROBE_TIMEOUT} 秒以内に済みませんでした（宛先が応答しません）"
			;;
	esac
}

deps_label() {
	case "$BACKUP_KIND" in
		postgres) printf 'Postgres（%s:%s）' "${PGHOST:-既定}" "${PGPORT:-5432}" ;;
		vault) printf '秘匿管理（%s）' "$BACKUP_VAULT_ADDR" ;;
	esac
}

wait_for_dependencies() {
	local start now waited=0 announced=0
	start="$(date +%s)"
	until "deps_ready_$BACKUP_KIND"; do
		now="$(date +%s)"
		waited=$((now - start))
		if [ "$waited" -ge "$BACKUP_DEPS_WAIT_SECONDS" ]; then
			# 最後の 1 回は理由を見せる（待つ間は黙っていた接続の失敗・状態コード）。
			deps_reason
			err "依存先 $(deps_label) が ${BACKUP_DEPS_WAIT_SECONDS} 秒以内に立ち上がりませんでした。何も書きません"
			return 1
		fi
		if [ "$announced" -eq 0 ]; then
			log "依存先 $(deps_label) の起動を待ちます（上限 ${BACKUP_DEPS_WAIT_SECONDS} 秒）"
			announced=1
		fi
		sleep "$BACKUP_DEPS_WAIT_INTERVAL"
	done
	[ "$announced" -eq 1 ] && log "依存先 $(deps_label) が立ち上がりました（$(($(date +%s) - start)) 秒待ちました）"
	return 0
}

# ---------------------------------------------------------------- publish / prune

# 平らなディレクトリ（回・incoming・staging）を消す。🔴 再帰削除をしない —— 想定外の中身
# （サブディレクトリ・シンボリックリンク等の通常ファイル以外）があれば、**1 つも消さずに**失敗として返す。
# 🔴 ディレクトリ自体がシンボリックリンクなら拒む（`[ -d ]` はリンクを辿るので、辿った先のファイルを消してしまう）。
remove_flat_dir() {
	local d="$1" f
	[ -L "$d" ] && return 1
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

# ---------------------------------------------------------------- lock（#2389・IADR-0669）
#
# 同じ種類の回が並行して走ると（手動の Job は CronJob の concurrencyPolicy: Forbid の外にある）、互いの incoming を消し、
# 欠けた回が「完了」と記録された（#2389）。保管先への書き込みと保持の削除を、保管先ごと・種類ごとのロックの中だけで行う。
# 🔴 ロックは `mkdir` の原子性で取る（9p〔drvfs〕の hostPath でも flock に頼らない）。中に持ち主（ホスト名・PID・時刻）を書く。
lock_owner_id() { printf '%s %s' "${HOSTNAME:-unknown}" "$$"; }

acquire_lock() {
	local kind_dir="$1" lock="$1/$BACKUP_LOCK_NAME" waited=0 owner since now
	while :; do
		if mkdir -- "$lock" 2>/dev/null; then
			printf '%s %s\n' "$(lock_owner_id)" "$(date +%s)" > "$lock/owner" || { rmdir -- "$lock" 2>/dev/null; return 1; }
			HELD_LOCKS+=("$kind_dir")
			return 0
		fi
		# 古いロック（Pod が強制終了されて trap が走らなかった残骸）は壊して取り直す。持ち主の時刻が読めなければ古いと見なさない。
		if lock_is_stale "$lock"; then
			case "$(break_stale_lock "$lock")" in
				broken) continue ;;
				failed) return 1 ;;
			esac
		fi
		if [ "$waited" -ge "$BACKUP_LOCK_WAIT_SECONDS" ]; then
			err "ロックを取れません（別の回が書いています。${waited} 秒待ちました）: $lock"
			return 1
		fi
		[ "$waited" -eq 0 ] && log "別の回が書いているので待ちます: $lock"
		sleep 1
		waited=$((waited + 1))
	done
}

# ロックの取った時刻（エポック秒）を出す。持ち主の時刻を読み、読めなければロックのディレクトリの更新時刻を使う。
# 🔴 2 回目のレビュー 🟡1: `mkdir` の直後・owner を書く前に強制終了されたロックは、持ち主の時刻が無い。読めないから古くないと
#    見なすと、以後の回が永久に待って失敗する。ディレクトリの更新時刻（作った時刻）で古さを読む（破棄の印と同じ手）。
#    取った直後でまだ owner を書いていない回のロックは、更新時刻が新しいので壊さない。
lock_since() {
	local owner since
	owner="$(head -n 1 "$1/owner" 2>/dev/null)"
	since="${owner##* }"
	[[ "$since" =~ ^[0-9]+$ ]] || since="$(stat -c %Y -- "$1" 2>/dev/null)"
	printf '%s' "$since"
}

# 取った時刻が BACKUP_LOCK_STALE_SECONDS より古いか（どちらの時刻も読めなければ古いと見なさない）。
lock_is_stale() {
	local since
	since="$(lock_since "$1")"
	[[ "$since" =~ ^[0-9]+$ ]] && [ $(($(date +%s) - since)) -gt "$BACKUP_LOCK_STALE_SECONDS" ]
}

# 古いロックを壊す。結果を 1 語で出す: broken（壊した・取り直してよい）/ busy（別の回が壊している・待つ）/ failed。
# 🔴 #2389（レビュー 🟡1）: **壊すのは、破棄の印 `<ロック>.break`（mkdir の原子性）を取った 1 本だけで、印の中で古さを読み直す。**
#    古いと見た後に止まっていた回が、別の回が壊して取ったばかりのロックを消すと、2 本が同時に書く（試験 T-2389-08）。
#    印を持つ間は、古いロックはまだ在るので、誰も新しいロックを取れない。だから読み直して古ければ、それは生きたロックではない。
#    印そのものが残った（印を取った直後の強制終了。ミリ秒の窓）ときは、印の作成から BACKUP_LOCK_BREAK_STALE_SECONDS で片付ける。
break_stale_lock() {
	local lock="$1" brk="$1.break" owner since mtime
	if ! mkdir -- "$brk" 2>/dev/null; then
		mtime="$(stat -c %Y -- "$brk" 2>/dev/null)"
		if [[ "$mtime" =~ ^[0-9]+$ ]] && [ $(($(date +%s) - mtime)) -gt "$BACKUP_LOCK_BREAK_STALE_SECONDS" ]; then
			rmdir -- "$brk" 2>/dev/null
		fi
		echo busy
		return 0
	fi
	if lock_is_stale "$lock"; then
		owner="$(head -n 1 "$lock/owner" 2>/dev/null)"
		since="$(lock_since "$lock")"
		log "古いロックを壊します: $lock（持ち主 ${owner:+${owner% *}}${owner:-不明〔持ち主のファイルが無い〕}・$(($(date +%s) - since)) 秒前）" >&2
		if ! remove_flat_dir "$lock"; then
			err "古いロックを消せません（想定外の中身があります）: $lock"
			rmdir -- "$brk" 2>/dev/null
			echo failed
			return 0
		fi
	fi
	rmdir -- "$brk" 2>/dev/null
	echo broken
}

# 自分のロックだけを解放する（持ち主が自分でなければ触らない）。
release_lock() {
	local kind_dir="$1" lock="$1/$BACKUP_LOCK_NAME" owner i
	owner="$(head -n 1 "$lock/owner" 2>/dev/null)"
	if [ "${owner% *}" = "$(lock_owner_id)" ]; then
		remove_flat_dir "$lock"
	fi
	for i in "${!HELD_LOCKS[@]}"; do
		[ "${HELD_LOCKS[$i]}" = "$kind_dir" ] && unset 'HELD_LOCKS[i]'
	done
	return 0
}

release_all_locks() {
	local d
	for d in "${HELD_LOCKS[@]}"; do release_lock "$d"; done
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
	# 🔴 #2389・IADR-0669: ここから先（片付け・incoming・改名・検算）はロックの中だけで行う。ロックを持つ間は、他の回が
	#    この保管先へ書いていない。だから前回の残骸の片付けで、動いている別の回の incoming を消すことが無い。
	acquire_lock "$kind_dir" || return 1
	# 前回の途中で残った incoming を片付ける（平らなものだけ）。
	for old in "$kind_dir"/.incoming-*; do
		[ -L "$old" ] && continue
		[ -d "$old" ] && remove_flat_dir "$old"
	done
	# incoming は回ごとに一意にする（同じ秒・同じ種類の回が重なっても、同じパスを使わない）。
	incoming="$kind_dir/.incoming-$run-${HOSTNAME:-unknown}-$$"
	if [ -e "$kind_dir/$run" ]; then
		err "同じ名前の回が既にあります: $kind_dir/$run"
		return 1
	fi
	if mkdir -- "$incoming" 2>/dev/null &&
		cp -- "$staging"/* "$incoming"/ 2>/dev/null &&
		(cd "$incoming" && sha256sum -c SHA256SUMS >/dev/null 2>&1) &&
		mv -- "$incoming" "$kind_dir/$run" 2>/dev/null; then
		# 🔴 #2389: 改名の後に、保管先の回でもう一度検算する。通らなければ「書きました」を出さず、回を名前の規則の外
		#    （broken-…）へ退かせる —— 保持の規則の「最新の完全な回」に数えさせないためである。
		if (cd "$kind_dir/$run" && sha256sum -c SHA256SUMS >/dev/null 2>&1); then
			log "書きました: $kind_dir/$run"
			return 0
		fi
		local broken
		broken="$kind_dir/broken-$run-$(date -u +%Y%m%dT%H%M%SZ)"
		if mv -- "$kind_dir/$run" "$broken" 2>/dev/null; then
			err "書いた回の検算が合いません。回を退かせました: $broken"
		else
			err "書いた回の検算が合いません。退かせることもできませんでした: $kind_dir/$run（手で broken- へ改名してください）"
		fi
		return 1
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
#   - 最新の完全な回（-partial でも -keep でもない回）は、上の規則に関わらず必ず残す
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
	local days=0 d m newest_full=""
	# 日次（新しい順）。-keep は日次の数え方から外す（長期保持の規則で別に扱う）。
	while IFS= read -r n; do
		[ -n "$n" ] || continue
		case "$n" in *-keep) continue ;; esac
		# 🔴 **最新の完全な回は必ず残す。** 一部失敗の回が 30 日以上続くと、日次の枠が一部失敗の回だけで埋まり、
		#    戻せる最後の完全な回を消してしまう（監査の指摘）。
		case "$n" in *-partial) ;; *) [ -z "$newest_full" ] && newest_full="$n" && keep[$n]=1 ;; esac
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
		# シンボリックリンクは回として扱わない（辿った先を消さない）。
		[ -L "$p" ] && continue
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
	# #2394・IADR-0670: 書き始める前に依存先を待つ（staging も保管先も、まだ何も作っていない）。
	# 構成の誤り（ServiceAccount のトークン）は待たずに失敗させる。
	if [ "$BACKUP_KIND" = vault ]; then check_vault_login_inputs || return 2; fi
	wait_for_dependencies || return 1

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

	# #2389・IADR-0669: 強制終了（activeDeadlineSeconds の TERM）でもロックを残さない。
	trap 'release_all_locks' EXIT
	trap 'release_all_locks; exit 143' TERM
	trap 'release_all_locks; exit 130' INT
	for target in $BACKUP_TARGETS; do
		if publish "$target" "$run" "$staging"; then
			written=$((written + 1))
			prune_target "$target" "$now" || rc=1
		else
			rc=1
		fi
		# 保持の削除もロックの中で行い、終わったら解放する（publish が失敗したときもロックを持っていれば解放する）。
		release_lock "$target/$BACKUP_KIND"
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
