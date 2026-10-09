#!/usr/bin/env bash
# IADR-0457 (#1479): vault-entrypoint.sh の分岐を、`vault` を PATH 上のスタブへ差し替えて固定する。
# 実 Vault・実コンテナは要らない。スタブは呼び出しを $STUB_LOG へ記録し、$STATE の印で応答を切り替える:
#   $STATE/initialized  … `operator init -status` が 0（在る）／2（無い）
#   $STATE/unsealed     … `status` が 0（在る）／2（無い）
#   $STATE/token-ok     … `token lookup` が 0（在る）／1（無い）
#   $STATE/kv-mounted   … `secrets list` に "secret/" を含める（在る）／含めない（無い）
#   $STATE/audit-<path> … `audit list` にその path を含める（在る）／含めない（無い）
#   $STATE/no-stdout-device … `audit list` から stdout/ を外す（既定は在る＝local.hcl の宣言。#1840）
#   $STATE/audit-fail-<path> … その path の device を足せない（出力先に届かない。サーバの代役が SIGHUP で見る）
#
#   bash deploy/local/vault-persistence/vault-entrypoint.test.sh
set -u

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
WORK="$(mktemp -d)"
STATE="$WORK/state"; mkdir -p "$STATE"
STUB_LOG="$WORK/vault.log"; : > "$STUB_LOG"
export STATE STUB_LOG
PASSED=0; FAILED=0
ok() { PASSED=$((PASSED + 1)); printf '  ok    %s\n' "$1"; }
ng() { FAILED=$((FAILED + 1)); printf '  NG    %s\n        %s\n' "$1" "$2"; }
assert_eq() { [ "$2" = "$3" ] && ok "$1" || ng "$1" "expected: $3 / actual: $2"; }
assert_contains() { case "$2" in *"$3"*) ok "$1" ;; *) ng "$1" "expected to contain: $3" ;; esac; }
assert_missing() { case "$2" in *"$3"*) ng "$1" "expected NOT to contain: $3" ;; *) ok "$1" ;; esac; }
reset_state() { rm -f "$STATE"/*; : > "$STUB_LOG"; }

mkdir -p "$WORK/bin"
cat > "$WORK/bin/vault" <<'STUB'
#!/usr/bin/env bash
echo "vault $*" >> "$STUB_LOG"
case "$1 $2" in
  "status ")           [ -f "$STATE/unsealed" ] && exit 0 || exit 2 ;;
  "operator init")
    if [ "${3:-}" = "-status" ]; then [ -f "$STATE/initialized" ] && exit 0 || exit 2; fi
    printf 'Unseal Key 1: STUB-UNSEAL-KEY\n\nInitial Root Token: STUB-ROOT-TOKEN\n'
    : > "$STATE/initialized"; exit 0 ;;
  "operator unseal")   [ "${3:-}" = "STUB-UNSEAL-KEY" ] && { : > "$STATE/unsealed"; exit 0; } || exit 1 ;;
  # #1793: unseal は `write sys/unseal key=-`（鍵は stdin）。受けた鍵を控え、正しければ unseal 済みにする。
  "write sys/unseal")  [ "${3:-}" = "key=-" ] || exit 1; k="$(cat)"; printf '%s' "$k" > "$STATE/unseal-stdin"
                       [ "$k" = "STUB-UNSEAL-KEY" ] && { : > "$STATE/unsealed"; exit 0; } || exit 1 ;;
  "token lookup")      [ -f "$STATE/token-ok" ] && exit 0 || exit 1 ;;
  "token create")      [ "${VAULT_TOKEN:-}" = "STUB-ROOT-TOKEN" ] && { : > "$STATE/token-ok"; exit 0; } || exit 1 ;;
  "secrets list")      [ -f "$STATE/kv-mounted" ] && echo '{"secret/":{}}' || echo '{"sys/":{}}'; exit 0 ;;
  "secrets enable")    : > "$STATE/kv-mounted"; exit 0 ;;
  "audit list")
    # #1840: 標準出力の device は local.hcl の宣言なので、在るのが既定（$STATE/no-stdout-device で「無い」にする）。
    printf '{'; [ -f "$STATE/no-stdout-device" ] || printf '"stdout/":{},'
    for f in "$STATE"/audit-*; do [ -e "$f" ] || continue; n="${f##*/audit-}"; case "$n" in fail-*) continue ;; esac; printf '"%s/":{},' "$n"; done; printf '"x/":{}}\n'; exit 0 ;;
  "audit enable")
    p="${3#-path=}"; [ -f "$STATE/audit-fail-$p" ] && exit 2; : > "$STATE/audit-$p"; exit 0 ;;
  "server ")           sleep 30 ;;
esac
exit 0
STUB
chmod +x "$WORK/bin/vault"
export PATH="$WORK/bin:$PATH"

export VAULT_DATA_DIR="$WORK/data"
export VAULT_INIT_FILE="$WORK/data/.local-dev-init"
export VAULT_DEV_ROOT_TOKEN_ID="devroot-test"
export VAULT_WAIT_TRIES=3 VAULT_WAIT_INTERVAL=0
export VAULT_AUDIT_SOCKET_TRIES=3 VAULT_AUDIT_SOCKET_INTERVAL=0
mkdir -p "$VAULT_DATA_DIR"
VAULT_ENTRYPOINT_LIB=1 . "$ROOT/deploy/local/vault-persistence/vault-entrypoint.sh"

# ---- T-1479-01: 初回起動（未初期化）: init → ファイル保存（0600）→ unseal → 固定トークン作成 → kv mount ----
reset_state
bootstrap_after_start >/dev/null 2>&1; RC=$?
assert_eq 'T-1479-01 初回: 正常終了する' "$RC" "0"
assert_contains 'T-1479-01 初回: operator init を 1 鍵で実行する' "$(cat "$STUB_LOG")" 'operator init -key-shares=1 -key-threshold=1'
assert_eq 'T-1479-01 初回: init の出力を 0600 で保存する' "$(stat -c '%a' "$VAULT_INIT_FILE")" "600"
assert_contains 'T-1479-01 初回: 保存した鍵で unseal する（#1793: 鍵は stdin）' "$(cat "$STUB_LOG")" 'write sys/unseal key=-'
assert_eq 'T-1793-01 初回: unseal 鍵は stdin で渡る（末尾の改行なし）' "$(cat "$STATE/unseal-stdin")" "STUB-UNSEAL-KEY"
assert_missing 'T-1793-01 初回: unseal 鍵が vault の引数に載らない' "$(cat "$STUB_LOG")" 'STUB-UNSEAL-KEY'
assert_contains 'T-1479-01 初回: 固定 root トークンを init の root トークンで作る' "$(cat "$STUB_LOG")" 'token create -id=devroot-test -policy=root -orphan'
assert_contains 'T-1479-01 初回: kv-v2 を secret/ に mount する' "$(cat "$STUB_LOG")" 'secrets enable -path=secret kv-v2'

# ---- T-1479-02: 再起動（初期化済み・sealed）: init しない・保存済みの鍵で unseal・トークンと mount は在るので触らない ----
reset_state
: > "$STATE/initialized"; : > "$STATE/token-ok"; : > "$STATE/kv-mounted"
chmod 660 "$VAULT_INIT_FILE"   # kubelet の fsGroup 処理で緩んだ状態を模す（監査 D2）
bootstrap_after_start >/dev/null 2>&1; RC=$?
assert_eq 'T-1479-02 再起動: 正常終了する' "$RC" "0"
assert_eq 'T-1479-02 再起動: 緩んだ init ファイルを 0600 へ戻す' "$(stat -c '%a' "$VAULT_INIT_FILE")" "600"
assert_missing 'T-1479-02 再起動: operator init を実行しない' "$(cat "$STUB_LOG")" 'operator init -key-shares'
assert_contains 'T-1479-02 再起動: 保存済みの鍵で unseal する（#1793: 鍵は stdin）' "$(cat "$STUB_LOG")" 'write sys/unseal key=-'
assert_missing 'T-1793-02 再起動: unseal 鍵が vault の引数に載らない' "$(cat "$STUB_LOG")" 'STUB-UNSEAL-KEY'
assert_missing 'T-1479-02 再起動: 固定トークンが在れば作らない' "$(cat "$STUB_LOG")" 'token create'
assert_missing 'T-1479-02 再起動: kv-v2 が在れば mount しない' "$(cat "$STUB_LOG")" 'secrets enable'

# ---- T-1479-03: 初期化済みなのに保存ファイルが無い（PVC を差し替えた等）→ 中断し、値をでっち上げない ----
reset_state
: > "$STATE/initialized"
rm -f "$VAULT_INIT_FILE"
OUT="$(bootstrap_after_start 2>&1)"; RC=$?
assert_eq 'T-1479-03 鍵ファイル不在: 非ゼロ終了する' "$RC" "1"
assert_contains 'T-1479-03 鍵ファイル不在: 理由を示す' "$OUT" 'unseal key not found'
assert_missing 'T-1479-03 鍵ファイル不在: unseal を試みない' "$(cat "$STUB_LOG")" 'sys/unseal'

# ---- T-1479-04: 秘密をログへ出さない（init の出力はファイルへだけ） ----
reset_state
OUT="$(bootstrap_after_start 2>&1)"
assert_missing 'T-1479-04 ログに unseal 鍵を出さない' "$OUT" 'STUB-UNSEAL-KEY'
assert_missing 'T-1479-04 ログに root トークンを出さない' "$OUT" 'STUB-ROOT-TOKEN'

# ---- T-1479-05: API 到達待ちが上限で諦める（server が上がらないとき無限に待たない） ----
reset_state
cat > "$WORK/bin/vault-down" <<'STUB'
#!/usr/bin/env bash
exit 1
STUB
chmod +x "$WORK/bin/vault-down"
( PATH="$WORK/bin:$PATH"; vault() { vault-down "$@"; }; export -f vault; wait_for_api ) >/dev/null 2>&1; RC=$?
assert_eq 'T-1479-05 API 到達不能: 上限回数で諦める（非ゼロ）' "$RC" "1"

# ---- T-1683-01 / T-1840-01: audit（標準出力）は local.hcl で宣言し、起動器はその存在を確かめる（NFR-18, ADR-0124 決定 2, IADR-0486, IADR-0525） ----
# OpenBao は API での audit device の作成を拒む（#1840 の実測）。起動器は `audit enable` を呼ばず、宣言した device が在ることだけを見る。
reset_state; rm -f "$VAULT_INIT_FILE"   # 初回（未初期化・鍵ファイル無し）
bootstrap_after_start >/dev/null 2>&1; RC=$?
LOG="$(cat "$STUB_LOG")"
assert_eq 'T-1683-01 audit: 正常終了する' "$RC" "0"
assert_missing 'T-1840-01 audit: API で audit device を作らない（OpenBao は拒む）' "$LOG" 'audit enable'
HCL="$(cat "$ROOT/deploy/local/vault-persistence/local.hcl")"
STDOUT_BLOCK="$(sed -n '/^audit "file" "stdout"/,/^}/p' "$ROOT/deploy/local/vault-persistence/local.hcl")"
assert_contains 'T-1840-01 audit: local.hcl が標準出力の file device を宣言する' "$STDOUT_BLOCK" 'file_path     = "stdout"'
assert_contains 'T-1683-01 audit: 値を HMAC 化する（log_raw=false を明示）' "$STDOUT_BLOCK" 'log_raw       = "false"'
assert_contains 'T-1683-01 audit: accessor も HMAC 化する（hmac_accessor=true を明示）' "$STDOUT_BLOCK" 'hmac_accessor = "true"'
assert_missing 'T-1683-01 audit: socket device を local.hcl に書かない（collector の不在で init が失敗する）' "$HCL" 'audit "socket"'
# 秘密の最初の書き込みより前に記録が立っていることを確かめる（kv の mount より先）。
AUDIT_AT="$(grep -n 'audit list' "$STUB_LOG" | head -1 | cut -d: -f1)"
KV_AT="$(grep -n 'secrets enable' "$STUB_LOG" | head -1 | cut -d: -f1)"
[ -n "$AUDIT_AT" ] && [ -n "$KV_AT" ] && [ "$AUDIT_AT" -lt "$KV_AT" ] && ok 'T-1683-01 audit: kv の mount より先に確かめる' || ng 'T-1683-01 audit: kv の mount より先に確かめる' "audit=$AUDIT_AT kv=$KV_AT"

# ---- T-1683-03: audit（標準出力）が無ければ起動を失敗させる（audit の無い秘匿管理を動かさない） ----
reset_state; rm -f "$VAULT_INIT_FILE"   # 初回（未初期化・鍵ファイル無し）
: > "$STATE/no-stdout-device"
OUT="$(VAULT_AUDIT_STDOUT_TRIES=2 VAULT_AUDIT_STDOUT_INTERVAL=0 bootstrap_after_start 2>&1)"; RC=$?
assert_eq 'T-1683-03 audit 不在: 非ゼロ終了する' "$RC" "1"
assert_contains 'T-1683-03 audit 不在: 理由を示す' "$OUT" 'refusing to run Vault without audit'
assert_missing 'T-1683-03 audit 不在: kv を mount しない（書き込みの受け口を開かない）' "$(cat "$STUB_LOG")" 'secrets enable'

# ---- T-1683-04 / T-1840-02: audit（socket）: collector へ tcp で出す宣言を書いて SIGHUP で読み直させる。値を記録しない設定を明示する ----
# サーバの代役: SIGHUP を受けたら、宣言のファイルが在り collector に届く（audit-fail-* が無い）ときだけ device を足す。
export VAULT_AUDIT_CONFIG_DIR="$WORK/audit.d"; mkdir -p "$VAULT_AUDIT_CONFIG_DIR"
export VAULT_AUDIT_RELOAD_WAIT=0.3
fake_server() {
  bash -c 'trap '\''echo hup >> "$STATE/hups"; if [ -f "$VAULT_AUDIT_CONFIG_DIR/otel-collector.hcl" ] && [ ! -f "$STATE/audit-fail-otel-collector" ]; then : > "$STATE/audit-otel-collector"; fi'\'' HUP; while :; do sleep 0.05; done' &
  FAKE_PID=$!
  sleep 0.2
}
reset_state; rm -f "$VAULT_AUDIT_CONFIG_DIR"/*
fake_server
ensure_audit_socket "$FAKE_PID" >/dev/null 2>&1; RC=$?
SOCK="$(cat "$VAULT_AUDIT_CONFIG_DIR/otel-collector.hcl" 2>/dev/null)"
assert_eq 'T-1683-04 socket: 正常終了する' "$RC" "0"
assert_eq 'T-1840-02 socket: SIGHUP で読み直させる' "$(grep -c hup "$STATE/hups" 2>/dev/null)" "1"
assert_missing 'T-1840-02 socket: API で audit device を作らない' "$(cat "$STUB_LOG")" 'audit enable'
assert_contains 'T-1683-04 socket: socket device を宣言する' "$SOCK" 'audit "socket" "otel-collector"'
assert_contains 'T-1683-04 socket: collector の tcplog 受信へ向ける' "$SOCK" 'address       = "otel-collector.platform-infra.svc:9514"'
assert_contains 'T-1683-04 socket: tcp で送る' "$SOCK" 'socket_type   = "tcp"'
assert_contains 'T-1683-04 socket: 書き込みの待ちに上限を付ける' "$SOCK" 'write_timeout = "2s"'
assert_contains 'T-1683-04 socket: 値を HMAC 化する（log_raw=false を明示）' "$SOCK" 'log_raw       = "false"'
assert_contains 'T-1683-04 socket: accessor も HMAC 化する（hmac_accessor=true を明示）' "$SOCK" 'hmac_accessor = "true"'
reset_state; : > "$STATE/audit-otel-collector"
ensure_audit_socket "$FAKE_PID" >/dev/null 2>&1
assert_eq 'T-1683-04 socket 在り: 読み直させない' "$( [ -f "$STATE/hups" ] && echo sent || echo none)" "none"
kill "$FAKE_PID" 2>/dev/null; wait "$FAKE_PID" 2>/dev/null

# ---- T-1683-05 / T-1840-03: audit（socket）: collector に届かなければ上限まで再試行して諦め、宣言を残さず WARN を出す ----
reset_state; rm -f "$VAULT_AUDIT_CONFIG_DIR"/*
: > "$STATE/audit-fail-otel-collector"
fake_server
OUT="$(ensure_audit_socket "$FAKE_PID" 2>&1)"; RC=$?
assert_eq 'T-1683-05 socket 不達: 非ゼロで返す（呼び出し側は裏で走らせ、起動を止めない）' "$RC" "1"
assert_eq 'T-1683-05 socket 不達: 上限回数だけ試す' "$(grep -c hup "$STATE/hups" 2>/dev/null)" "3"
assert_contains 'T-1683-05 socket 不達: 標準出力だけになることを告げる' "$OUT" 'audit goes to stdout only'
assert_eq 'T-1840-03 socket 不達: 足せなかった宣言を残さない（次の起動の unseal を巻き込まない）' "$(ls "$VAULT_AUDIT_CONFIG_DIR" | wc -l)" "0"
kill "$FAKE_PID" 2>/dev/null; wait "$FAKE_PID" 2>/dev/null
# 起動器は socket を裏で走らせる（`ensure_audit_socket … &`）。前景に置くと collector の不在で起動が待たされる。
grep -qE '^[[:space:]]*ensure_audit_socket "\$server_pid" &$' "$ROOT/deploy/local/vault-persistence/vault-entrypoint.sh" \
  && ok 'T-1683-05 socket: 起動器は socket の宣言の追加を裏で走らせる' \
  || ng 'T-1683-05 socket: 起動器は socket の宣言の追加を裏で走らせる' 'main に `ensure_audit_socket "$server_pid" &` が無い'

# ---- T-1840-04: 移行の門: 旧 Vault の file ストレージが移行されずに残っていれば止め、init ファイルに触れない ----
reset_state
LEGACY="$WORK/legacy"; mkdir -p "$LEGACY/core" "$LEGACY/logical" "$LEGACY/raft"
printf 'Unseal Key 1: STUB-OLD-KEY\n' > "$LEGACY/.local-dev-init"
OUT="$(VAULT_DATA_DIR="$LEGACY" VAULT_RAFT_DIR="$LEGACY/raft" VAULT_INIT_FILE="$LEGACY/.local-dev-init" refuse_unmigrated_file_storage 2>&1)"; RC=$?
assert_eq 'T-1840-04 未移行: 非ゼロで止める' "$RC" "1"
assert_contains 'T-1840-04 未移行: 手順書を名指しする' "$OUT" 'docs/operations/secret-store-openbao-migration-runbook.md'
assert_missing 'T-1840-04 未移行: 鍵の値をログに出さない' "$OUT" 'STUB-OLD-KEY'
: > "$LEGACY/raft/vault.db"
VAULT_DATA_DIR="$LEGACY" VAULT_RAFT_DIR="$LEGACY/raft" refuse_unmigrated_file_storage >/dev/null 2>&1; RC=$?
assert_eq 'T-1840-04 移行済み（raft/vault.db が在る）: 通す' "$RC" "0"
FRESH="$WORK/fresh"; mkdir -p "$FRESH"
VAULT_DATA_DIR="$FRESH" VAULT_RAFT_DIR="$FRESH/raft" refuse_unmigrated_file_storage >/dev/null 2>&1; RC=$?
assert_eq 'T-1840-04 新規（何も無い）: 通す' "$RC" "0"
# 門はサーバの起動（と init）より前にある。
MAIN="$(sed -n '/^main() {/,/^}/p' "$ROOT/deploy/local/vault-persistence/vault-entrypoint.sh")"
GATE_AT="$(printf '%s\n' "$MAIN" | grep -n 'refuse_unmigrated_file_storage' | head -1 | cut -d: -f1)"
SERVER_AT="$(printf '%s\n' "$MAIN" | grep -n 'vault server' | head -1 | cut -d: -f1)"
[ -n "$GATE_AT" ] && [ -n "$SERVER_AT" ] && [ "$GATE_AT" -lt "$SERVER_AT" ] && ok 'T-1840-04 門はサーバの起動より前にある' || ng 'T-1840-04 門はサーバの起動より前にある' "gate=$GATE_AT server=$SERVER_AT"

# ---- T-1866-01: 未初期化なのに鍵ファイルが在れば init しない（移行の写し・戻したバックアップの鍵を上書きしない） ----
reset_state
printf 'Unseal Key 1: STUB-KEEP-KEY\n\nInitial Root Token: STUB-KEEP-ROOT\n' > "$VAULT_INIT_FILE"
BEFORE="$(sha256sum "$VAULT_INIT_FILE" | cut -d' ' -f1)"
OUT="$(bootstrap_after_start 2>&1)"; RC=$?
assert_eq 'T-1866-01 鍵ファイル在り・未初期化: 非ゼロで止める' "$RC" "1"
assert_missing 'T-1866-01 鍵ファイル在り・未初期化: operator init を実行しない' "$(cat "$STUB_LOG")" 'operator init -key-shares'
assert_eq 'T-1866-01 鍵ファイル在り・未初期化: 鍵ファイルを書き換えない' "$(sha256sum "$VAULT_INIT_FILE" | cut -d' ' -f1)" "$BEFORE"
assert_contains 'T-1866-01 鍵ファイル在り・未初期化: 理由を示す' "$OUT" 'refusing to init over existing keys'
assert_missing 'T-1866-01 鍵ファイル在り・未初期化: 鍵の値をログに出さない' "$OUT" 'STUB-KEEP-KEY'

rm -rf "$WORK"
printf '\n%s passed, %s failed\n' "$PASSED" "$FAILED"
[ "$FAILED" -eq 0 ]
