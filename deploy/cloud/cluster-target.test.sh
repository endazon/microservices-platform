#!/usr/bin/env bash
# NFR-18, NFR-05, IADR-0703 決定 2・決定 3: オーナーが手元から流す deploy/cloud の手順が、宛先のクラスタを確かめてから kubectl を使い、
# 確かめた context に固定すること（lib/target-cluster.sh・cluster-mark.sh）と、平文の age の識別子の扱い（vault/lib.sh の ob_identity_guard）を固定する。
#
#   bash deploy/cloud/cluster-target.test.sh
#
# kubectl は PATH のスタブ（宛先・印・現在の context を環境変数で変え、呼び出しを全部記録する）。稼働のクラスタには触れない。
# 「確かめ以外の呼び出し」（ACT の行）が 1 つでも記録されたら、宛先を確かめる前に手順が動いたとみなす。
set -euo pipefail
# 標準入力を空にする（スタブの kubectl は当たらない呼び出しで標準入力を読み捨てる。端末やソケットのまま流すとそこで待ち続ける。手順の中のパイプは効く）。
exec </dev/null

HERE="$(cd "$(dirname "$0")" && pwd)"
for t in age age-keygen node; do command -v "$t" >/dev/null || { echo "✗ $t が無い" >&2; exit 1; }; done
W="$(mktemp -d)"
trap 'rm -rf "$W"' EXIT
mkdir -p "$W/bin"
cat > "$W/bin/kubectl" <<'EOF'
#!/usr/bin/env bash
ctx="$(cat "$STUB_CTX_FILE")"
case "$*" in
  "config current-context") echo "CHK $*" >> "$KLOG"; [ -n "$ctx" ] && echo "$ctx"; exit 0 ;;
  "config view --minify --context $ctx -o jsonpath={.clusters[0].cluster.server}") echo "CHK $*" >> "$KLOG"; echo "$STUB_SERVER"; exit 0 ;;
  "--context $ctx get namespace kube-system -o jsonpath={.metadata.uid}") echo "CHK $*" >> "$KLOG"; echo "$STUB_UID"; exit 0 ;;
esac
echo "ACT $*" >> "$KLOG"
# 最初の作業の呼び出しの後で、現在の context を別のクラスタへ替える（途中で替わっても固定が効くことを見る）
[ -n "${STUB_SWITCH_TO:-}" ] && printf '%s' "$STUB_SWITCH_TO" > "$STUB_CTX_FILE"
case "$*" in
  *"get job "*"-o jsonpath={.status.succeeded}/{.status.failed}") echo "1/" ;;
  *"get secret openbao-tls --ignore-not-found -o name"*) ;;
  *) cat >/dev/null 2>&1 || true ;;
esac
exit 0
EOF
chmod +x "$W/bin/kubectl"
export PATH="$W/bin:$PATH" KLOG="$W/k.log" STUB_CTX_FILE="$W/ctx"
UID_A="11111111-2222-4333-8444-555555555555" UID_B="99999999-8888-4777-8666-555555555555"
export CLOUD_CLUSTER_MARK_FILE="$W/conf/cloud-cluster-mark"
age-keygen -o "$W/id.txt" 2> "$W/recip.txt"; sed -i 's/^Public key: //' "$W/recip.txt"
printf '{"unseal_keys_b64":["a"],"root_token":"b","role_id":"c","secret_id":"d"}' | age -R "$W/recip.txt" -o "$W/f.age"

pass=0
ok() { pass=$((pass + 1)); echo "  ok  $1"; }
ng() { echo "  NG  $1" >&2; [ -f "$KLOG" ] && sed 's/^/      /' "$KLOG" >&2; exit 1; }
target() { printf '%s' "$1" > "$STUB_CTX_FILE"; export STUB_SERVER="$2" STUB_UID="$3"; : > "$KLOG"; }
acts() { grep -c '^ACT ' "$KLOG" || true; }

# 手順の入口（どれも宛先を確かめてから kubectl を使う）。値の形は確かめの前に通るもの。
entries=(
  "vault/init.sh --live --recipients $W/recip.txt --out $W/new-init.age"
  "vault/unseal.sh --live --init-file $W/f.age --identity $W/id.txt"
  "vault/configure.sh --live --init-file $W/f.age --identity $W/id.txt --recipients $W/recip.txt --operator-out $W/new-op.age"
  "vault/configure.sh --live --revoke-only --init-file $W/f.age --identity $W/id.txt"
  "vault/seed-secrets.sh --live --operator-file $W/f.age --identity $W/id.txt"
  "vault/root-token.sh --live --init-file $W/f.age --operator-file $W/f.age --identity $W/id.txt apply-pki-roles"
  "vault/put-external-secret.sh --live --operator-file $W/f.age --identity $W/id.txt cloud/msp/x k"
  "vault/tls-cert.sh --live issue"
  "keycloak-clients/keycloak-clients.sh --live check"
  "keycloak-admin/keycloak-admin.sh --live check"
  "db-users/ast-db-users.sh --live apply"
)

# --- 記録（record）---
target ctx-a "https://10.0.0.5:6443" "$UID_A"
set +e; bash "$HERE/cluster-mark.sh" --live record >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -eq 4 ] && [ ! -e "$CLOUD_CLUSTER_MARK_FILE" ] || ng "record はトンネル（127.0.0.1:6443）でない宛先では書かずに 4 で終わる（rc=$rc）"
set +e; bash "$HERE/cluster-mark.sh" record >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -eq 3 ] || ng "record は --live が無ければ 3 で終わる"
target ctx-a "https://127.0.0.1:6443" "$UID_A"
bash "$HERE/cluster-mark.sh" --live record >/dev/null 2>&1 || ng "record が書けない"
[ "$(cat "$CLOUD_CLUSTER_MARK_FILE")" = "$UID_A" ] && [ "$(stat -c %a "$CLOUD_CLUSTER_MARK_FILE")" = 600 ] || ng "record の印・権限（0600）が違う"
set +e; bash "$HERE/cluster-mark.sh" --live record >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -eq 2 ] && [ "$(cat "$CLOUD_CLUSTER_MARK_FILE")" = "$UID_A" ] || ng "2 回目の record は上書きせずに 2 で終わる"
[ "$(acts)" = 0 ] || ng "record が確かめ以外の kubectl を呼んだ"
ok "record: トンネルの宛先のときだけ、印（kube-system の UID）を 0600 で書く。在れば上書きしない。読むだけ"

bash "$HERE/cluster-mark.sh" --live check >/dev/null 2>&1 || ng "check が一致で 0 にならない"
ok "check: 記録した印と一致すれば 0"

# --- 宛先が違えば、どの入口も何もせずに 4 で終わる ---
for case_ in "ctx-b|https://127.0.0.1:6443|$UID_B|別のクラスタ（印が違う）" \
             "ctx-a|https://10.0.0.5:6443|$UID_A|トンネルでない宛先" \
             "|https://127.0.0.1:6443|$UID_A|現在の context が無い" \
             "ctx-a|https://127.0.0.1:6443|not-a-uid|印を読めない"; do
  IFS='|' read -r c s u why <<< "$case_"
  for e in "${entries[@]}" "cluster-mark.sh --live check"; do
    target "$c" "$s" "$u"
    # shellcheck disable=SC2086
    set +e; bash "$HERE"/$e >"$W/out" 2>&1; rc=$?; set -e
    [ "$rc" -eq 4 ] || { cat "$W/out" >&2; ng "${why}: ${e%% *} が 4 で終わらない（rc=$rc）"; }
    [ "$(acts)" = 0 ] || ng "${why}: ${e%% *} が宛先を確かめる前に kubectl を使った"
  done
done
ok "宛先が違う（別のクラスタ・トンネルでない・context が無い・印を読めない）と、${#entries[@]} の入口のどれも kubectl を使わずに 4 で終わる"

mv "$CLOUD_CLUSTER_MARK_FILE" "$W/mark.bak"
for e in "${entries[@]}"; do
  target ctx-a "https://127.0.0.1:6443" "$UID_A"
  # shellcheck disable=SC2086
  set +e; bash "$HERE"/$e >"$W/out" 2>&1; rc=$?; set -e
  [ "$rc" -eq 4 ] && grep -q 'cluster-mark.sh --live record' "$W/out" && [ "$(acts)" = 0 ] || ng "記録が無いとき ${e%% *} が record を案内して止まらない"
done
mv "$W/mark.bak" "$CLOUD_CLUSTER_MARK_FILE"
ok "印の記録が無ければ、どの入口も record を案内して止まる（記録の無いまま流さない）"

# --- 確かめた後は、確かめた context に固定される（途中で現在の context が替わっても） ---
for e in "keycloak-clients/keycloak-clients.sh --live check" "keycloak-admin/keycloak-admin.sh --live check" "db-users/ast-db-users.sh --live apply"; do
  target ctx-a "https://127.0.0.1:6443" "$UID_A"
  # shellcheck disable=SC2086
  STUB_SWITCH_TO=ctx-b bash "$HERE"/$e >/dev/null 2>&1 || ng "${e%% *} が一致の宛先で通らない"
  n="$(acts)"
  [ "$n" -ge 3 ] || ng "${e%% *} の作業の呼び出しが記録されない（陽性対照）"
  [ "$(grep '^ACT ' "$KLOG" | grep -vc '^ACT --context ctx-a ' || true)" = 0 ] || ng "${e%% *} の kubectl が確かめた context（ctx-a）に固定されていない"
done
ok "確かめた後の kubectl は、途中で現在の context が替わっても、確かめた context に固定される"

# --- age の識別子（ob_identity_guard） ---
target ctx-a "https://127.0.0.1:6443" "$UID_A"
set +e; OB_IDENTITY_POLICY=enforce bash "$HERE/vault/unseal.sh" --live --init-file "$W/f.age" --identity "$W/id.txt" >"$W/out" 2>&1; rc=$?; set -e
[ "$rc" -eq 2 ] && [ "$(acts)" = 0 ] && grep -q '平文の秘密鍵のまま' "$W/out" || ng "enforce で平文の識別子を拒まない（rc=$rc）"
AGE_PREFIX='AGE-SECRET-'; AGE_PREFIX+='KEY-1'   # 分けて組み立てる（deploy/ に秘密鍵らしき字面を置かない）
grep -q "$AGE_PREFIX" "$W/out" && ng "識別子の中身を表示した"
: > "$KLOG"
set +e; bash "$HERE/vault/unseal.sh" --live --init-file "$W/f.age" --identity "$W/id.txt" >"$W/out" 2>&1; set -e
grep -q '^WARN: age の識別子' "$W/out" && [ "$(acts)" -ge 1 ] || ng "warn（既定）で警告して進まない"
age -R "$W/recip.txt" -o "$W/id.wrapped" < "$W/id.txt"   # 包んだ識別子の代わり（平文の秘密鍵の行を持たない）
: > "$KLOG"
set +e; OB_IDENTITY_POLICY=enforce bash "$HERE/vault/unseal.sh" --live --init-file "$W/f.age" --identity "$W/id.wrapped" >"$W/out" 2>&1; set -e
grep -q '平文の秘密鍵のまま' "$W/out" && ng "包んだ識別子を平文と見なした"
[ "$(acts)" -ge 1 ] || ng "包んだ識別子で、enforce のまま先へ進まない"
set +e; OB_IDENTITY_POLICY=off bash "$HERE/vault/unseal.sh" --live --init-file "$W/f.age" --identity "$W/id.txt" >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -eq 2 ] || ng "OB_IDENTITY_POLICY の値域の外を拒まない"
ok "平文の age の識別子は、warn（既定）で警告して進み、enforce で kubectl を使わずに止まる。包んだ識別子は通す。中身は表示しない"

echo "cluster-target.test.sh: ${pass} passed"
