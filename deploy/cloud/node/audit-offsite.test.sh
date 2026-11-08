#!/usr/bin/env bash
# NFR-18, IADR-0710 決定 5: ノードの監査の記録の目録（audit-offsite.sh）を、使い捨ての根（NODE_ROOT）と curl のスタブで固定する。
#   - 目録は種類ごとのファイルの大きさと先頭の sha256 を持ち、前の回の目録の sha256（prev）で鎖になる。
#   - 送るとき URL は引数に載らない（curl の設定を標準入力で渡す）。URL を出力に出さない。URL が無い・権限が広い・形が違えば送らない。
#   - verify は、記録が追記・改名（世代交代）されても前の目録の行を見つけ、書き換え・削除を NG にする（陰性対照つき）。
#
#   bash deploy/cloud/node/audit-offsite.test.sh
set -euo pipefail
exec </dev/null

HERE="$(cd "$(dirname "$0")" && pwd)"
W="$(mktemp -d)"
trap 'rm -rf "$W"' EXIT
export NODE_ROOT="$W/root"
mkdir -p "$NODE_ROOT/var/lib/rancher/k3s/server/logs" "$NODE_ROOT/var/log/pods/openbao_openbao-abc_1234/openbao" "$NODE_ROOT/etc/msp-audit-offsite" "$W/bin"
AUD="$NODE_ROOT/var/lib/rancher/k3s/server/logs"
OBL="$NODE_ROOT/var/log/pods/openbao_openbao-abc_1234/openbao"
cat > "$W/bin/curl" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$CURL_ARGS"
cfg="$(cat)"; printf '%s\n' "$cfg" >> "$CURL_CFG"
body=""; prev=""
for a in "$@"; do [ "$prev" = --data-binary ] && body="${a#@}"; prev="$a"; done
[ -n "$body" ] && cp "$body" "$CURL_BODY"
[ -f "$CURL_FAIL" ] && { echo "curl: (22) The requested URL returned error: 404 for ${cfg}" >&2; exit 22; }
exit 0
STUB
chmod +x "$W/bin/curl"
export PATH="$W/bin:$PATH" CURL_ARGS="$W/curl.args" CURL_CFG="$W/curl.cfg" CURL_BODY="$W/body" CURL_FAIL="$W/curl.fail"
URL="https://hc-ping.example/stub-uuid-DO-NOT-LEAK"
S="$HERE/audit-offsite.sh"

pass=0
ok() { pass=$((pass + 1)); echo "  ok  $1"; }
ng() { echo "  NG  $1" >&2; exit 1; }

printf '{"a":1}\n{"a":2}\n' > "$AUD/audit.log"
printf '{"old":1}\n' > "$AUD/audit-2026-11-01T00-00-00.000.log"
printf 'ob-1\n' > "$OBL/0.log"

# --- 目録 ---
m="$(bash "$S" manifest)"
grep -q '^msp-audit-manifest v1 [0-9T:Z-]*$' <<< "$m" && grep -q '^prev none$' <<< "$m" || ng "目録の頭（版・prev none）が違う"
want="apiserver /var/lib/rancher/k3s/server/logs/audit.log $(stat -c %s "$AUD/audit.log") $(sha256sum < "$AUD/audit.log" | cut -d ' ' -f 1)"
grep -qxF "$want" <<< "$m" || ng "目録に API サーバーの監査の記録の行（大きさ・sha256）が無い"
grep -q '^openbao /var/log/pods/openbao_openbao-abc_1234/openbao/0.log 5 ' <<< "$m" || ng "目録に OpenBao の監査（Pod のログ）の行が無い"
[ "$(grep -c '^apiserver ' <<< "$m")" = 2 ] || ng "世代交代した記録も目録に入らない"
grep -q '"a"' <<< "$m" && ng "目録に記録の中身が入った"
ok "manifest: 種類ごとのファイルの大きさと先頭の sha256 を出し、中身は出さない"

# --- 送る ---
set +e; bash "$S" send >"$W/out" 2>&1; rc=$?; set -e
[ "$rc" -eq 2 ] && [ ! -e "$CURL_ARGS" ] || ng "URL のファイルが無いのに送った（rc=$rc）"
printf '%s' "$URL" > "$NODE_ROOT/etc/msp-audit-offsite/ping-url"; chmod 644 "$NODE_ROOT/etc/msp-audit-offsite/ping-url"
set +e; bash "$S" send >"$W/out" 2>&1; rc=$?; set -e
[ "$rc" -eq 2 ] && [ ! -e "$CURL_ARGS" ] && grep -q '0600' "$W/out" || ng "URL のファイルの権限が 0600 でないのに送った（rc=$rc）"
printf 'http://hc-ping.example/x' > "$NODE_ROOT/etc/msp-audit-offsite/ping-url"; chmod 600 "$NODE_ROOT/etc/msp-audit-offsite/ping-url"
set +e; bash "$S" send >"$W/out" 2>&1; rc=$?; set -e
[ "$rc" -eq 2 ] && [ ! -e "$CURL_ARGS" ] || ng "https でない URL へ送った（rc=$rc）"
ok "send: URL が無い・0600 でない・https でなければ送らずに 2 で終わる"

printf '%s' "$URL" > "$NODE_ROOT/etc/msp-audit-offsite/ping-url"
bash "$S" send >"$W/out" 2>&1 || { cat "$W/out" >&2; ng "send が通らない"; }
grep -q 'stub-uuid-DO-NOT-LEAK' "$CURL_ARGS" && ng "URL が curl の引数に載った"
grep -qxF "url = \"$URL\"" "$CURL_CFG" || ng "URL を curl の設定（標準入力）で渡していない"
grep -q 'stub-uuid-DO-NOT-LEAK' "$W/out" && ng "URL を出力に出した"
grep -qxF "$want" "$CURL_BODY" || ng "送った本文に目録の行が無い"
cmp -s "$CURL_BODY" "$NODE_ROOT/var/lib/msp-audit-offsite/last-manifest" || ng "送った目録を前の回として残さない"
ok "send: 目録を本文で送り、URL は引数にも出力にも出さず（curl の設定は標準入力）、送った目録を前の回として残す"

# 鎖: 次の回の prev は前の回の目録の sha256
first="$W/first-manifest"; cp "$CURL_BODY" "$first"
printf '{"a":3}\n' >> "$AUD/audit.log"
bash "$S" send >/dev/null 2>&1 || ng "2 回目の send が通らない"
grep -qx "prev $(sha256sum < "$first" | cut -d ' ' -f 1)" "$CURL_BODY" || ng "2 回目の目録の prev が前の回の sha256 でない"
ok "send: 目録は前の回の目録の sha256（prev）で鎖になる"

# 送れなければ 1。前の回は書き換えない。誤りの文に URL を出さない。
cp "$NODE_ROOT/var/lib/msp-audit-offsite/last-manifest" "$W/last.before"
: > "$CURL_FAIL"
set +e; bash "$S" send >"$W/out" 2>&1; rc=$?; set -e
rm -f "$CURL_FAIL"
[ "$rc" -eq 1 ] && cmp -s "$W/last.before" "$NODE_ROOT/var/lib/msp-audit-offsite/last-manifest" || ng "送れないときに 1 で終わらないか、前の回を書き換えた（rc=$rc）"
grep -q 'stub-uuid-DO-NOT-LEAK' "$W/out" && ng "送れないときに URL を出力に出した"
ok "send: 送れなければ 1 で終わり、前の回を書き換えず、URL を出さない"

# --- verify ---
bash "$S" verify "$first" >"$W/v" 2>&1 || { cat "$W/v" >&2; ng "追記された後の記録で、前の目録を確かめられない"; }
# 世代交代（改名）の後も見つける
mv "$AUD/audit.log" "$AUD/audit-2026-11-08T00-00-00.000.log"; printf '{"b":1}\n' > "$AUD/audit.log"
bash "$S" verify "$first" >"$W/v" 2>&1 || { cat "$W/v" >&2; ng "世代交代した後の記録で、前の目録を確かめられない"; }
ok "verify: 追記・世代交代（改名）の後も、前の目録の行を今のファイルの先頭に見つける"
# 陰性対照: 書き換え（前の目録の範囲の 1 バイト）と削除を NG にする
cp "$AUD/audit-2026-11-08T00-00-00.000.log" "$W/keep"
sed -i 's/"a":1/"a":9/' "$AUD/audit-2026-11-08T00-00-00.000.log"
set +e; bash "$S" verify "$first" >"$W/v" 2>&1; rc=$?; set -e
[ "$rc" -eq 1 ] && grep -q '^NG  apiserver /var/lib/rancher/k3s/server/logs/audit.log' "$W/v" || ng "書き換えた記録を NG にしない（rc=$rc）"
cp "$W/keep" "$AUD/audit-2026-11-08T00-00-00.000.log"
rm -f "$OBL/0.log"
set +e; bash "$S" verify "$first" >"$W/v" 2>&1; rc=$?; set -e
[ "$rc" -eq 1 ] && grep -q '^NG  openbao ' "$W/v" || ng "消した記録を NG にしない（rc=$rc）"
printf 'not a manifest\n' > "$W/bad"
set +e; bash "$S" verify "$W/bad" >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -eq 2 ] || ng "形の違う目録を 2 で拒まない（rc=$rc）"
ok "verify: 書き換え・削除を NG（1）にし、形の違う目録は 2 で拒む"

# --- systemd の宣言 ---
svc="$(cat "$HERE/msp-audit-offsite.service")"; tmr="$(cat "$HERE/msp-audit-offsite.timer")"
grep -qx 'ExecStart=/usr/bin/bash /usr/local/lib/msp/audit-offsite.sh send' <<< "$svc" || ng "service が send を流さない"
for p in /var/lib/rancher /var/log/pods; do grep -qx "ConditionPathIsMountPoint=$p" <<< "$svc" || ng "service が $p のマウントを条件にしない"; done
grep -qx 'Persistent=true' <<< "$tmr" && grep -qE '^OnCalendar=\*-\*-\* [0-9]{2}:[0-9]{2}:00 UTC$' <<< "$tmr" || ng "timer が日次・後追い（Persistent）でない"
ok "systemd: 日次の timer が、暗号化した領域のマウントを条件に send を流す"

echo "audit-offsite.test.sh: ${pass} passed"
