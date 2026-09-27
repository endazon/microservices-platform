#!/usr/bin/env bash
# IADR-0457 (#1479): vault-entrypoint.sh の分岐を、`vault` を PATH 上のスタブへ差し替えて固定する。
# 実 Vault・実コンテナは要らない。スタブは呼び出しを $STUB_LOG へ記録し、$STATE の印で応答を切り替える:
#   $STATE/initialized  … `operator init -status` が 0（在る）／2（無い）
#   $STATE/unsealed     … `status` が 0（在る）／2（無い）
#   $STATE/token-ok     … `token lookup` が 0（在る）／1（無い）
#   $STATE/kv-mounted   … `secrets list` に "secret/" を含める（在る）／含めない（無い）
#   $STATE/audit-<path> … `audit list` にその path を含める（在る）／含めない（無い）。`audit enable` が成功すると作る
#   $STATE/audit-fail-<path> … その path の `audit enable` を失敗させる（出力先に届かない）
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
  "token lookup")      [ -f "$STATE/token-ok" ] && exit 0 || exit 1 ;;
  "token create")      [ "${VAULT_TOKEN:-}" = "STUB-ROOT-TOKEN" ] && { : > "$STATE/token-ok"; exit 0; } || exit 1 ;;
  "secrets list")      [ -f "$STATE/kv-mounted" ] && echo '{"secret/":{}}' || echo '{"sys/":{}}'; exit 0 ;;
  "secrets enable")    : > "$STATE/kv-mounted"; exit 0 ;;
  "audit list")
    printf '{'; for f in "$STATE"/audit-*; do [ -e "$f" ] || continue; n="${f##*/audit-}"; case "$n" in fail-*) continue ;; esac; printf '"%s/":{},' "$n"; done; printf '"x/":{}}\n'; exit 0 ;;
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
assert_contains 'T-1479-01 初回: 保存した鍵で unseal する' "$(cat "$STUB_LOG")" 'operator unseal STUB-UNSEAL-KEY'
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
assert_contains 'T-1479-02 再起動: 保存済みの鍵で unseal する' "$(cat "$STUB_LOG")" 'operator unseal STUB-UNSEAL-KEY'
assert_missing 'T-1479-02 再起動: 固定トークンが在れば作らない' "$(cat "$STUB_LOG")" 'token create'
assert_missing 'T-1479-02 再起動: kv-v2 が在れば mount しない' "$(cat "$STUB_LOG")" 'secrets enable'

# ---- T-1479-03: 初期化済みなのに保存ファイルが無い（PVC を差し替えた等）→ 中断し、値をでっち上げない ----
reset_state
: > "$STATE/initialized"
rm -f "$VAULT_INIT_FILE"
OUT="$(bootstrap_after_start 2>&1)"; RC=$?
assert_eq 'T-1479-03 鍵ファイル不在: 非ゼロ終了する' "$RC" "1"
assert_contains 'T-1479-03 鍵ファイル不在: 理由を示す' "$OUT" 'unseal key not found'
assert_missing 'T-1479-03 鍵ファイル不在: unseal を試みない' "$(cat "$STUB_LOG")" 'operator unseal'

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

# ---- T-1683-01: audit（標準出力）: 無ければ有効にする。値を記録しない設定を明示する（NFR-18, ADR-0124 決定 2, IADR-0486） ----
reset_state
bootstrap_after_start >/dev/null 2>&1; RC=$?
LOG="$(cat "$STUB_LOG")"
assert_eq 'T-1683-01 audit: 正常終了する' "$RC" "0"
assert_contains 'T-1683-01 audit: 標準出力の file device を有効にする' "$LOG" 'audit enable -path=stdout file file_path=stdout'
assert_contains 'T-1683-01 audit: 値を HMAC 化する（log_raw=false を明示）' "$(grep 'audit enable -path=stdout' "$STUB_LOG")" 'log_raw=false'
assert_contains 'T-1683-01 audit: accessor も HMAC 化する（hmac_accessor=true を明示）' "$(grep 'audit enable -path=stdout' "$STUB_LOG")" 'hmac_accessor=true'
assert_missing 'T-1683-01 audit: 平文を出す設定を渡さない' "$LOG" 'log_raw=true'
# 秘密の最初の書き込みより前に記録を立てる（kv の mount より先）。
AUDIT_AT="$(grep -n 'audit enable -path=stdout' "$STUB_LOG" | head -1 | cut -d: -f1)"
KV_AT="$(grep -n 'secrets enable' "$STUB_LOG" | head -1 | cut -d: -f1)"
[ -n "$AUDIT_AT" ] && [ -n "$KV_AT" ] && [ "$AUDIT_AT" -lt "$KV_AT" ] && ok 'T-1683-01 audit: kv の mount より先に有効にする' || ng 'T-1683-01 audit: kv の mount より先に有効にする' "audit=$AUDIT_AT kv=$KV_AT"

# ---- T-1683-02: audit（標準出力）: 既に在れば触らない（再起動） ----
reset_state
: > "$STATE/initialized"; : > "$STATE/token-ok"; : > "$STATE/kv-mounted"; : > "$STATE/audit-stdout"
printf 'Unseal Key 1: STUB-UNSEAL-KEY\n\nInitial Root Token: STUB-ROOT-TOKEN\n' > "$VAULT_INIT_FILE"
bootstrap_after_start >/dev/null 2>&1; RC=$?
assert_eq 'T-1683-02 audit 在り: 正常終了する' "$RC" "0"
assert_missing 'T-1683-02 audit 在り: 有効化し直さない' "$(cat "$STUB_LOG")" 'audit enable'

# ---- T-1683-03: audit（標準出力）を有効にできなければ起動を失敗させる（audit の無い Vault を動かさない） ----
reset_state
: > "$STATE/audit-fail-stdout"
OUT="$(bootstrap_after_start 2>&1)"; RC=$?
assert_eq 'T-1683-03 audit 不能: 非ゼロ終了する' "$RC" "1"
assert_contains 'T-1683-03 audit 不能: 理由を示す' "$OUT" 'refusing to run Vault without audit'
assert_missing 'T-1683-03 audit 不能: kv を mount しない（書き込みの受け口を開かない）' "$(cat "$STUB_LOG")" 'secrets enable'

# ---- T-1683-04: audit（socket）: 可観測性基盤の collector へ tcp で出す。値を記録しない設定を明示する ----
reset_state
ensure_audit_socket >/dev/null 2>&1; RC=$?
SOCK="$(grep 'audit enable -path=otel-collector' "$STUB_LOG")"
assert_eq 'T-1683-04 socket: 正常終了する' "$RC" "0"
assert_contains 'T-1683-04 socket: socket device を collector の tcplog 受信へ向ける' "$SOCK" 'socket address=otel-collector.platform-infra.svc:9514 socket_type=tcp'
assert_contains 'T-1683-04 socket: 書き込みの待ちに上限を付ける' "$SOCK" 'write_timeout=2s'
assert_contains 'T-1683-04 socket: 値を HMAC 化する（log_raw=false を明示）' "$SOCK" 'log_raw=false'
assert_contains 'T-1683-04 socket: accessor も HMAC 化する（hmac_accessor=true を明示）' "$SOCK" 'hmac_accessor=true'
reset_state; : > "$STATE/audit-otel-collector"
ensure_audit_socket >/dev/null 2>&1
assert_missing 'T-1683-04 socket 在り: 有効化し直さない' "$(cat "$STUB_LOG")" 'audit enable'

# ---- T-1683-05: audit（socket）: collector に届かなくても上限まで再試行して諦め、WARN を出す（起動は止めない側） ----
reset_state
: > "$STATE/audit-fail-otel-collector"
OUT="$(ensure_audit_socket 2>&1)"; RC=$?
assert_eq 'T-1683-05 socket 不達: 非ゼロで返す（呼び出し側は裏で走らせ、起動を止めない）' "$RC" "1"
assert_eq 'T-1683-05 socket 不達: 上限回数だけ試す' "$(grep -c 'audit enable -path=otel-collector' "$STUB_LOG")" "3"
assert_contains 'T-1683-05 socket 不達: 標準出力だけになることを告げる' "$OUT" 'audit goes to stdout only'
# 起動器は socket を裏で走らせる（`ensure_audit_socket &`）。前景に置くと collector の不在で起動が待たされる。
grep -qE '^[[:space:]]*ensure_audit_socket &$' "$ROOT/deploy/local/vault-persistence/vault-entrypoint.sh" \
  && ok 'T-1683-05 socket: 起動器は socket の有効化を裏で走らせる' \
  || ng 'T-1683-05 socket: 起動器は socket の有効化を裏で走らせる' 'main に `ensure_audit_socket &` が無い'

rm -rf "$WORK"
printf '\n%s passed, %s failed\n' "$PASSED" "$FAILED"
[ "$FAILED" -eq 0 ]
