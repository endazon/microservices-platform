#!/usr/bin/env bash
# IADR-0457 (#1479): vault-entrypoint.sh の分岐を、`vault` を PATH 上のスタブへ差し替えて固定する。
# 実 Vault・実コンテナは要らない。スタブは呼び出しを $STUB_LOG へ記録し、$STATE の印で応答を切り替える:
#   $STATE/initialized  … `operator init -status` が 0（在る）／2（無い）
#   $STATE/unsealed     … `status` が 0（在る）／2（無い）
#   $STATE/token-ok     … `token lookup` が 0（在る）／1（無い）
#   $STATE/kv-mounted   … `secrets list` に "secret/" を含める（在る）／含めない（無い）
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

rm -rf "$WORK"
printf '\n%s passed, %s failed\n' "$PASSED" "$FAILED"
[ "$FAILED" -eq 0 ]
