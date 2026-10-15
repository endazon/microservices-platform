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
#        vault:    raft の整合したスナップショット（k8s auth・最小ポリシー。中の SHA256SUMS で検算）＋ init のファイル（IADR-0548 / #1868）
#   3. 各保管先へ `.incoming-<回>` として写し、SHA256SUMS で検証してから `<回>` へ改名する
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
vault_login_k8s() {
	local resp="$1" jwt body
	VAULT_SNAPSHOT_TOKEN=""
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
		[ -L "$old" ] && continue
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
