#!/bin/sh
# IADR-0457 (#1479): 経路B の Vault を **永続化して自動で使える状態に戻す** Pod 内ラッパー。
#
# dev モード（`vault server -dev`）はインメモリで、Pod 再起動のたびに k8s auth・policy・role・KV・OIDC が消え、
# ESO の store が InvalidProviderConfig に倒れ、画面（SC-22）で入れた秘密も失われた（2026-09-16 実測）。
# 本ラッパーは file ストレージ（PVC）のサーバを起動し、次を毎回行う:
#   1. API 到達待ち
#   2. 未初期化なら `operator init`（1 鍵）。出力を $INIT_FILE（PVC 上・0600）へ保存する
#   3. unseal（保存した鍵）
#   4. 固定 root トークン（Secret vault-dev-token の値＝VAULT_DEV_ROOT_TOKEN_ID）が無ければ root policy で作る
#      —— bootstrap.sh / ESO store（token 認証版）/ OIDC bootstrap は従来どおり同じトークンで動く
#   5. `secret/` に kv-v2 が無ければ mount する（dev モードが自動でしていたこと）
#   6. audit device を有効にする（NFR-18, ADR-0124 決定 2, IADR-0486 / #1683。下の「audit device」を参照）
#   7. サーバを待つ（SIGTERM は転送する）
#
# 🔴 unseal 鍵と初期 root トークンは PVC 上の平文ファイルに置く。root トークンが既知の dev 既定である現状と
#    守りの水準は同じ（ローカル dev 専用）。k8s Secret に置く案は Pod 内に kubectl が無く Job が要り、
#    鍵も PVC も同じローカルディスク上で差が無いため採らなかった（IADR-0457）。
# 🔴 値をログに出さない（init の出力はファイルへだけ書く）。
# 起動ログ先頭の `You cannot specify a custom root token ID outside of "dev" mode. Your request has been ignored.` は
# env VAULT_DEV_ROOT_TOKEN_ID を残している（本ラッパーが固定トークンの ID として読む）ことによる無害な警告。
#
# 試験（vault-entrypoint.test.sh）は VAULT_ENTRYPOINT_LIB=1 で source し、`vault` を PATH 上のスタブへ差し替える。

VAULT_DATA_DIR="${VAULT_DATA_DIR:-/vault/data}"
VAULT_INIT_FILE="${VAULT_INIT_FILE:-$VAULT_DATA_DIR/.local-dev-init}"
VAULT_CONFIG_FILE="${VAULT_CONFIG_FILE:-/vault/config/local.hcl}"
VAULT_ADDR="${VAULT_ADDR:-http://127.0.0.1:8200}"
export VAULT_ADDR
VAULT_KV_PATH="${VAULT_KV_PATH:-secret}"

# ---- audit device（NFR-18, ADR-0124 決定 2, IADR-0486 / #1683）----
# 🔴 **2 つ並べる。** Vault は、有効な audit device の**少なくとも 1 つ**が書けなければ要求を拒む。
#    socket（可観測性基盤への取り込み）1 つだけにすると、collector / Loki の停止がそのまま Vault の停止になる
#    （画面の書き込みも ESO の同期も止まる）。止まらない方の device（標準出力）を並べてそれを避ける。
#      - stdout/         … file device（file_path=stdout）。kubelet のコンテナログへ出る。**有効にできなければ起動を失敗させる**
#      - otel-collector/ … socket device（tcp）。collector の tcplog 受信 → Loki。**失敗しても起動は止めず、裏で再試行する**
# 🔴 **値を記録しない。** 両方に log_raw=false / hmac_accessor=true を**明示する**（既定と同じ値を書いて固定する）。
#    平文を出す設定（log_raw=true・audit_non_hmac_*）を足さないこと —— scripts/scripts.repo.test.js（#1683）が落ちる。
# 宛先のポートは deploy/local/infra/otel-collector.yaml と deploy/local/observability/otel-collector-forward.yaml の
# `tcplog/vault-audit` と一致させる（同じ試験が突き合わせる）。
VAULT_AUDIT_STDOUT_PATH="${VAULT_AUDIT_STDOUT_PATH:-stdout}"
VAULT_AUDIT_SOCKET_PATH="${VAULT_AUDIT_SOCKET_PATH:-otel-collector}"
VAULT_AUDIT_SOCKET_ADDRESS="${VAULT_AUDIT_SOCKET_ADDRESS:-otel-collector.platform-infra.svc:9514}"
VAULT_AUDIT_SOCKET_WRITE_TIMEOUT="${VAULT_AUDIT_SOCKET_WRITE_TIMEOUT:-2s}"

log() { printf '==> vault-entrypoint: %s\n' "$*"; }

# API が応答するまで待つ（`vault status` は 0=unseal 済み・2=sealed/未初期化・1=到達不能）。
wait_for_api() {
	i=0
	while [ "$i" -lt "${VAULT_WAIT_TRIES:-120}" ]; do
		# `if cmd` の後の $? は if 文自身の結果になるので、終了コードは直後に採る。
		vault status >/dev/null 2>&1
		rc=$?
		if [ "$rc" -eq 0 ] || [ "$rc" -eq 2 ]; then return 0; fi
		i=$((i + 1))
		sleep "${VAULT_WAIT_INTERVAL:-0.5}"
	done
	return 1
}

# 未初期化なら init して結果をファイルへ残す（0600）。初期化済みなら何もしない。
ensure_initialized() {
	vault operator init -status >/dev/null 2>&1
	rc=$?
	if [ "$rc" -eq 0 ]; then
		log "initialized (reusing $VAULT_INIT_FILE)"
		# kubelet の fsGroup 処理で緩んでいても 0600 へ戻す（冪等。ファイルが無ければ ensure_unsealed が中断する）。
		[ -f "$VAULT_INIT_FILE" ] && chmod 600 "$VAULT_INIT_FILE"
		return 0
	fi
	if [ "$rc" -ne 2 ]; then
		log "ERROR: cannot determine init status (rc=$rc)"
		return 1
	fi
	log "not initialized: running operator init (1 share)"
	umask 077
	vault operator init -key-shares=1 -key-threshold=1 > "$VAULT_INIT_FILE.tmp" || return 1
	mv "$VAULT_INIT_FILE.tmp" "$VAULT_INIT_FILE"
	chmod 600 "$VAULT_INIT_FILE"
}

init_field() { sed -n "s/^$1: //p" "$VAULT_INIT_FILE" | head -1; }

ensure_unsealed() {
	if vault status >/dev/null 2>&1; then
		log "already unsealed"
		return 0
	fi
	key="$(init_field 'Unseal Key 1')"
	if [ -z "$key" ]; then
		log "ERROR: unseal key not found in $VAULT_INIT_FILE"
		return 1
	fi
	vault operator unseal "$key" >/dev/null || return 1
	log "unsealed"
}

# 固定 root トークン（dev 既定）が無ければ、init の root トークンで作る。
ensure_fixed_root_token() {
	fixed="${VAULT_DEV_ROOT_TOKEN_ID:?VAULT_DEV_ROOT_TOKEN_ID is required}"
	if VAULT_TOKEN="$fixed" vault token lookup >/dev/null 2>&1; then
		log "fixed root token present"
		return 0
	fi
	root="$(init_field 'Initial Root Token')"
	if [ -z "$root" ]; then
		log "ERROR: initial root token not found in $VAULT_INIT_FILE"
		return 1
	fi
	VAULT_TOKEN="$root" vault token create -id="$fixed" -policy=root -orphan -no-default-policy \
		-display-name=local-dev-root >/dev/null || return 1
	log "fixed root token created"
}

# dev モードが自動で mount していた kv-v2（secret/）を、無いときだけ mount する。
ensure_kv_mount() {
	fixed="${VAULT_DEV_ROOT_TOKEN_ID:?}"
	if VAULT_TOKEN="$fixed" vault secrets list -format=json 2>/dev/null | grep -q "\"$VAULT_KV_PATH/\""; then
		log "kv-v2 mounted at $VAULT_KV_PATH/"
		return 0
	fi
	VAULT_TOKEN="$fixed" vault secrets enable -path="$VAULT_KV_PATH" kv-v2 >/dev/null || return 1
	log "kv-v2 enabled at $VAULT_KV_PATH/"
}

# audit device が path に在るか（`vault audit list` は path を `"<path>/"` のキーで返す）。
audit_device_present() {
	fixed="${VAULT_DEV_ROOT_TOKEN_ID:?}"
	VAULT_TOKEN="$fixed" vault audit list -format=json 2>/dev/null | grep -q "\"$1/\""
}

# 止まらない方の device（標準出力）。**これが無い Vault を動かさない**（失敗したら起動を失敗させる）。
ensure_audit_stdout() {
	fixed="${VAULT_DEV_ROOT_TOKEN_ID:?}"
	if audit_device_present "$VAULT_AUDIT_STDOUT_PATH"; then
		log "audit device present at $VAULT_AUDIT_STDOUT_PATH/"
		return 0
	fi
	VAULT_TOKEN="$fixed" vault audit enable -path="$VAULT_AUDIT_STDOUT_PATH" file \
		file_path=stdout log_raw=false hmac_accessor=true format=json >/dev/null || {
		log "ERROR: cannot enable audit device $VAULT_AUDIT_STDOUT_PATH/ (refusing to run Vault without audit)"
		return 1
	}
	log "audit device enabled at $VAULT_AUDIT_STDOUT_PATH/ (file -> stdout)"
}

# 可観測性基盤への device（socket）。Vault は有効化のときに試験の 1 行を書くので、collector が居ないと失敗する。
# **起動は止めない**（標準出力の device が在る）。上限まで待って再試行し、諦めたら WARN を出す。
# 一度有効になれば Vault の storage に残るので、2 回目以降の起動では既に在る。
ensure_audit_socket() {
	fixed="${VAULT_DEV_ROOT_TOKEN_ID:?}"
	i=0
	while [ "$i" -lt "${VAULT_AUDIT_SOCKET_TRIES:-60}" ]; do
		if audit_device_present "$VAULT_AUDIT_SOCKET_PATH"; then
			log "audit device present at $VAULT_AUDIT_SOCKET_PATH/"
			return 0
		fi
		if VAULT_TOKEN="$fixed" vault audit enable -path="$VAULT_AUDIT_SOCKET_PATH" socket \
			address="$VAULT_AUDIT_SOCKET_ADDRESS" socket_type=tcp write_timeout="$VAULT_AUDIT_SOCKET_WRITE_TIMEOUT" \
			log_raw=false hmac_accessor=true format=json >/dev/null 2>&1; then
			log "audit device enabled at $VAULT_AUDIT_SOCKET_PATH/ (socket -> $VAULT_AUDIT_SOCKET_ADDRESS)"
			return 0
		fi
		i=$((i + 1))
		sleep "${VAULT_AUDIT_SOCKET_INTERVAL:-5}"
	done
	log "WARN: audit device $VAULT_AUDIT_SOCKET_PATH/ not enabled ($VAULT_AUDIT_SOCKET_ADDRESS unreachable). audit goes to stdout only; restart the vault pod after the collector is up"
	return 1
}

bootstrap_after_start() {
	wait_for_api || { log "ERROR: API did not come up"; return 1; }
	ensure_initialized || return 1
	ensure_unsealed || return 1
	ensure_fixed_root_token || return 1
	# audit を kv の mount より先に有効にする（秘密の最初の書き込みより前に記録を立てる）。
	ensure_audit_stdout || return 1
	ensure_kv_mount || return 1
	log "ready"
}

main() {
	mkdir -p "$VAULT_DATA_DIR"
	vault server -config="$VAULT_CONFIG_FILE" &
	server_pid=$!
	trap 'kill -TERM "$server_pid" 2>/dev/null' TERM INT
	if ! bootstrap_after_start; then
		kill -TERM "$server_pid" 2>/dev/null
		wait "$server_pid" 2>/dev/null
		exit 1
	fi
	# 裏で再試行する（collector より先に上がっても起動を待たせない）。失敗は WARN だけ。
	# 上の trap はこの再試行には伝えない —— コンテナの終了時にはコンテナごと回収されるので、止める必要が無い。
	ensure_audit_socket &
	# `wait` はシグナルで中断されるので、サーバが実際に終わるまで待ち直す（PID 1 が先に抜けると残りが SIGKILL される）。
	while kill -0 "$server_pid" 2>/dev/null; do
		wait "$server_pid"
	done
}

if [ "${VAULT_ENTRYPOINT_LIB:-}" = "1" ]; then
	return 0 2>/dev/null || exit 0
fi
main
