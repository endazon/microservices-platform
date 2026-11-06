#!/usr/bin/env bash
# NFR-21, NFR-18, IADR-0471, IADR-0682: 秘匿管理（OpenBao）のバックアップの回を、使い捨ての OpenBao（コンテナ。ネットワーク無し）へ戻して
# 開けることを確かめる（docs/operations/platform-infra-backup-runbook.md の §3 の 5）。稼働中の秘匿管理には触れない。
#
#   bash scripts/vault-restore-drill.sh --run-dir <回のディレクトリ> --identity <age の秘密鍵ファイル> [--image <イメージ>]
#   bash scripts/vault-restore-drill.sh --self-test
#
# 手順:
#   1. 回のディレクトリの SHA256SUMS で暗号文を検証する
#   2. 使い捨ての OpenBao を起こす（--network none・データは tmpfs）
#   3. スナップショットと init のファイルを、age で復号しながらコンテナの tmpfs へ直に流す（**手元のディスクへ平文を書かない**）
#   4. コンテナの中でスナップショットの中身を検算し、新しい鍵で init → 写しを -force で戻す → 写しの unseal 鍵で開く → KV の名前を数える
#   5. 使い捨てのコンテナを消す（成功・失敗・止められたときのどれでも。名前の無いボリュームごと）
#
# 🔴 復号した物（スナップショット・init のファイル＝unseal 鍵と root トークン）は、コンテナの tmpfs の中にしか置かない。手元に一時ファイルを作らない。
# 🔴 値は表示しない（KV の名前の数だけを出す）。秘密鍵はパスで受け取り `age -d -i` にだけ渡す。
# 🔴 --self-test は PATH 上のスタブ（docker / age）だけで走り、Docker にも稼働クラスタにも触れない。
#
# 環境変数: CONTAINER_CLI（既定 docker）/ DRILL_WAIT_TRIES（既定 30）/ DRILL_WAIT_INTERVAL（既定 1 秒）
set -u
set -o pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
log() { printf '==> vault-drill: %s\n' "$*"; }
err() { printf '==> vault-drill: ERROR: %s\n' "$*" >&2; }

usage() {
	sed -n '4,6p' "$0" | sed 's/^# \{0,1\}//'
}

DRILL_CLI=""
DRILL_NAME=""
# 使い捨てのコンテナを消す（tmpfs の中身はコンテナと一緒に消える）。`-v` で名前の無いボリュームも消す。
drill_cleanup() {
	if [ -n "$DRILL_NAME" ] && [ -n "$DRILL_CLI" ]; then
		"$DRILL_CLI" rm -f -v "$DRILL_NAME" >/dev/null 2>&1 || true
	fi
}

# コンテナの中で走らせる手順（値を出さない。KV の名前の数だけを出す）。各段の前に値を含まない印（step=<段>）を標準出力へ出す。
# 失敗したとき、どの段で止まったかを最後の印で名指す（標準エラーは値を含み得るので捨てる）。
IN_CONTAINER='
set -e
export BAO_ADDR=http://127.0.0.1:8200
echo step=verify-snapshot
mkdir -p /drill/x && tar -xzf /drill/raft.snap -C /drill/x && (cd /drill/x && sha256sum -c SHA256SUMS >/dev/null)
echo step=init
bao operator init -key-shares=1 -key-threshold=1 > /drill/new
echo step=unseal-new
printf "%s" "$(sed -n "s/^Unseal Key 1: //p" /drill/new)" | bao write sys/unseal key=- >/dev/null
sleep 3
echo step=restore
BAO_TOKEN="$(sed -n "s/^Initial Root Token: //p" /drill/new)" bao operator raft snapshot restore -force /drill/raft.snap >/dev/null
sleep 3
echo step=unseal-restored
printf "%s" "$(sed -n "s/^Unseal Key 1: //p" /drill/init)" | bao write sys/unseal key=- >/dev/null
sleep 3
echo step=list
n="$(BAO_TOKEN="$(sed -n "s/^Initial Root Token: //p" /drill/init)" bao kv list -format=json secret/msp | grep -c "\"")"
echo "kv-names=$n"
'

main() {
	local run_dir="" identity="" image=""
	while [ $# -gt 0 ]; do
		case "$1" in
			--run-dir) run_dir="${2:-}"; shift 2 ;;
			--identity) identity="${2:-}"; shift 2 ;;
			--image) image="${2:-}"; shift 2 ;;
			-h | --help) usage; return 0 ;;
			*) err "未知の引数: $1"; usage >&2; return 2 ;;
		esac
	done
	if [ -z "$run_dir" ] || [ -z "$identity" ]; then err "--run-dir と --identity は必須です"; usage >&2; return 2; fi
	if [ ! -r "$identity" ]; then err "秘密鍵ファイルを読めません（パスを確かめてください。中身は表示しません）"; return 2; fi
	if [ -z "$image" ]; then
		image="$(sed -n 's/^ *image: \(openbao\/openbao:.*\)$/\1/p' "$ROOT/deploy/local/vault/vault-dev.yaml" | head -1)"
		[ -n "$image" ] || { err "OpenBao のイメージを deploy/local/vault/vault-dev.yaml から読めません（--image で与える）"; return 2; }
	fi

	# 1. 暗号文の検証
	local f
	for f in SHA256SUMS vault-raft.snap.age vault-init.age; do
		[ -f "$run_dir/$f" ] || { err "この回に $f がありません（OpenBao へ移る前の回は対象外）: $run_dir"; return 1; }
	done
	if ! (cd "$run_dir" && sha256sum -c SHA256SUMS >/dev/null 2>&1); then
		err "SHA256SUMS と一致しません（写しが壊れています）: $run_dir"; return 1
	fi
	log "暗号文の検証: ok（$run_dir）"

	# 2. 使い捨ての OpenBao（ネットワーク無し・データと復号物は tmpfs）
	DRILL_CLI="${CONTAINER_CLI:-docker}"
	DRILL_NAME="vault-restore-drill-$$"
	trap drill_cleanup EXIT
	# 止められた（Ctrl-C・SIGTERM・端末の切断）ときも EXIT の片付けへ進める。
	trap 'exit 130' INT
	trap 'exit 143' TERM
	trap 'exit 129' HUP
	local cli="$DRILL_CLI" name="$DRILL_NAME"
	log "使い捨ての OpenBao を起こします（$image・--network none）"
	# /drill は tmpfs。イメージの利用者（root ではない）が書けるよう 1777 にし、中のファイルは umask 077 で書く（読めるのはその利用者だけ）。
	# shellcheck disable=SC2016 # 中の $ はコンテナの sh が展開する
	if ! "$cli" run -d --network none --name "$name" --tmpfs /drill:rw,mode=1777 --tmpfs /tmp:rw,mode=1777 --entrypoint sh "$image" -c '
		umask 077; mkdir -p /drill/raft && printf "%s\n" "storage \"raft\" {" "  path = \"/drill/raft\"" "  node_id = \"drill\"" "}" \
			"listener \"tcp\" {" "  address = \"127.0.0.1:8200\"" "  tls_disable = true" "}" \
			"api_addr = \"http://127.0.0.1:8200\"" "cluster_addr = \"http://127.0.0.1:8201\"" > /drill/drill.hcl &&
		exec bao server -config=/drill/drill.hcl' >/dev/null; then
		err "コンテナを起こせません（$cli）"; return 1
	fi
	local i=0
	until "$cli" exec "$name" sh -c 'BAO_ADDR=http://127.0.0.1:8200 bao status >/dev/null 2>&1; [ $? -eq 2 ]' 2>/dev/null; do
		i=$((i + 1))
		[ "$i" -ge "${DRILL_WAIT_TRIES:-30}" ] && { err "使い捨ての OpenBao が起動しません"; return 1; }
		sleep "${DRILL_WAIT_INTERVAL:-1}"
	done

	# 3. 復号しながらコンテナの tmpfs へ直に流す（手元のディスクへ平文を書かない）
	if ! age -d -i "$identity" "$run_dir/vault-raft.snap.age" | "$cli" exec -i "$name" sh -c 'umask 077; cat > /drill/raft.snap'; then
		err "スナップショットを復号・転送できません"; return 1
	fi
	if ! age -d -i "$identity" "$run_dir/vault-init.age" | "$cli" exec -i "$name" sh -c 'umask 077; cat > /drill/init'; then
		err "init のファイルを復号・転送できません"; return 1
	fi

	# 4. 戻して開ける
	local out
	if ! out="$("$cli" exec "$name" sh -c "$IN_CONTAINER" 2>/dev/null)"; then
		local at
		at="$(printf '%s\n' "$out" | sed -n 's/^step=//p' | tail -1)"
		err "使い捨ての OpenBao へ戻して開けられません（止まった段: ${at:-起動の前}。値は表示しません）"; return 1
	fi
	local n
	n="$(printf '%s\n' "$out" | sed -n 's/^kv-names=//p' | tail -1)"
	if [ -z "$n" ] || [ "$n" -eq 0 ]; then err "戻した写しの secret/msp に名前が 1 つもありません"; return 1; fi
	log "合格: 写しを戻して開け、secret/msp の名前を ${n} 件読めました（値は表示しません）"
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
	mkdir -p "$w/bin" "$w/state" "$w/run" "$w/home"
	local log_file="$w/stub.log"
	cat > "$w/bin/docker" <<'STUB'
#!/usr/bin/env bash
echo "docker $*" >> "$STUB_LOG"
case "$*" in
  run\ *) [ -f "$STUB_STATE/run-fail" ] && exit 125; echo "stub-id"; exit 0 ;;
  rm\ *) exit 0 ;;
  *"bao status"*) [ -f "$STUB_STATE/hang" ] && { : > "$STUB_STATE/hanging"; sleep 3; }; exit 0 ;;
  *"cat > /drill/"*) cat >> "$STUB_STATE/received"; exit 0 ;;
  *"bao operator init"*) [ -f "$STUB_STATE/restore-fail" ] && { echo step=verify-snapshot; echo step=init; echo step=restore; exit 1; }; echo "kv-names=4"; exit 0 ;;
esac
exit 0
STUB
	cat > "$w/bin/age" <<'STUB'
#!/usr/bin/env bash
echo "age $*" >> "$STUB_LOG"
id=""; prev=""; for a in "$@"; do [ "$prev" = "-i" ] && id="$a"; prev="$a"; done
[ -r "$id" ] || exit 1
printf 'PLAINTEXT-%s\n' "$(basename "${*: -1}")"
STUB
	chmod +x "$w/bin/"*
	local secret="AGE-SECRET-KEY-1STUBSTUBSTUBDONOTPRINT"
	printf '%s\n' "$secret" > "$w/identity.txt"
	for f in vault-raft.snap.age vault-init.age; do printf 'cipher-%s' "$f" > "$w/run/$f"; done
	(cd "$w/run" && sha256sum -- *.age > SHA256SUMS)

	drill() {
		# 🔴 PATH の先頭にスタブ・TMPDIR は空の作業ディレクトリ（手元に一時ファイルを作れば見える）。
		PATH="$w/bin:$PATH" TMPDIR="$w/home" STUB_LOG="$log_file" STUB_STATE="$w/state" CONTAINER_CLI=docker \
			DRILL_WAIT_TRIES=2 DRILL_WAIT_INTERVAL=0 bash "$0" "$@" 2>&1
	}
	local OUT RC LOG
	: > "$log_file"; rm -f "$w/state/received"
	OUT="$(drill --run-dir "$w/run" --identity "$w/identity.txt" --image openbao/openbao:stub)"; RC=$?
	LOG="$(cat "$log_file")"
	eq 'T-V-01 正常系: 戻して開ければ合格' "$RC" "0"
	has 'T-V-01 合格を名前の数で告げる（値は出さない）' "$OUT" '名前を 4 件'
	has 'T-V-02 使い捨てのコンテナはネットワーク無し' "$(grep ' run ' "$log_file")" '--network none'
	has 'T-V-02 復号物の置き場はコンテナの tmpfs' "$(grep ' run ' "$log_file")" '--tmpfs /drill:rw,mode=1777'
	lacks 'T-V-02 手元のディレクトリをコンテナへ見せない（bind mount しない）' "$(grep ' run ' "$log_file")" ' -v '
	has 'T-V-03 復号物はコンテナへ直に流す（スナップショット）' "$(cat "$w/state/received")" 'PLAINTEXT-vault-raft.snap.age'
	has 'T-V-03 復号物はコンテナへ直に流す（init のファイル）' "$(cat "$w/state/received")" 'PLAINTEXT-vault-init.age'
	eq 'T-V-03 手元（TMPDIR）に一時ファイルを作らない' "$(find "$w/home" -mindepth 1 | wc -l | tr -d ' ')" "0"
	lacks 'T-V-03 age は手元のファイルへ書かない（-o を使わない）' "$(grep '^age' "$log_file")" ' -o '
	lacks 'T-V-04 秘密鍵の中身が出力に出ない' "$OUT" "$secret"
	lacks 'T-V-04 秘密鍵の中身がどのコマンドの引数にも出ない' "$LOG" "$secret"
	lacks 'T-V-04 復号物が出力に出ない' "$OUT" 'PLAINTEXT-'
	has 'T-V-05 最後に使い捨てのコンテナをボリュームごと消す' "$LOG" 'docker rm -f -v vault-restore-drill-'

	: > "$log_file"; : > "$w/state/restore-fail"
	OUT="$(drill --run-dir "$w/run" --identity "$w/identity.txt" --image openbao/openbao:stub)"; RC=$?
	rm -f "$w/state/restore-fail"
	eq 'T-V-06 戻せなければ失敗' "$RC" "1"
	has 'T-V-06 止まった段を値を含まない印で名指す' "$OUT" '止まった段: restore'
	has 'T-V-06 失敗しても使い捨てのコンテナをボリュームごと消す' "$(cat "$log_file")" 'docker rm -f -v vault-restore-drill-'

	: > "$log_file"; : > "$w/state/run-fail"
	OUT="$(drill --run-dir "$w/run" --identity "$w/identity.txt" --image openbao/openbao:stub)"; RC=$?
	rm -f "$w/state/run-fail"
	eq 'T-V-07 コンテナを起こせなければ失敗' "$RC" "1"
	has 'T-V-07 起こせなくても名前で消す' "$(cat "$log_file")" 'docker rm -f -v vault-restore-drill-'
	lacks 'T-V-07 起こせなければ復号しない' "$(cat "$log_file")" 'age -d'

	: > "$log_file"; : > "$w/state/hang"; rm -f "$w/state/hanging"
	PATH="$w/bin:$PATH" TMPDIR="$w/home" STUB_LOG="$log_file" STUB_STATE="$w/state" CONTAINER_CLI=docker \
		DRILL_WAIT_TRIES=2 DRILL_WAIT_INTERVAL=0 bash "$0" --run-dir "$w/run" --identity "$w/identity.txt" --image openbao/openbao:stub >/dev/null 2>&1 &
	local pid=$! n=0
	until [ -f "$w/state/hanging" ] || [ "$n" -ge 50 ]; do sleep 0.1; n=$((n + 1)); done
	kill -TERM "$pid" 2>/dev/null
	wait "$pid"; RC=$?
	rm -f "$w/state/hang" "$w/state/hanging"
	lacks 'T-V-08 止められた（SIGTERM）ら合格にしない' "$RC" "0"
	has 'T-V-08 止められても使い捨てのコンテナをボリュームごと消す' "$(cat "$log_file")" 'docker rm -f -v vault-restore-drill-'
	lacks 'T-V-08 止められたら復号へ進まない' "$(cat "$log_file")" 'age -d'

	printf 'tampered' > "$w/run/vault-init.age"
	: > "$log_file"
	OUT="$(drill --run-dir "$w/run" --identity "$w/identity.txt" --image openbao/openbao:stub)"; RC=$?
	eq 'T-V-09 暗号文が SHA256SUMS と合わなければ失敗' "$RC" "1"
	lacks 'T-V-09 検証に落ちたらコンテナを起こさない' "$(cat "$log_file")" 'docker run'
	OUT="$(drill --run-dir "$w/run" --identity "$w/no-such-identity")"; RC=$?
	eq 'T-V-10 秘密鍵ファイルが無ければ使い方の誤りで止まる' "$RC" "2"

	rm -rf "$w"
	printf '\nvault-restore-drill self-test: %d passed, %d failed\n' "$passed" "$failed"
	[ "$failed" -eq 0 ]
}

if [ "${1:-}" = "--self-test" ]; then
	self_test
	exit $?
fi
main "$@"
exit $?
