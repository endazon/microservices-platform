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
  # IADR-0706: 超利用者の TCP のゲート（ast-db-users.sh は閉じたことを status で確かめる）。超利用者の突き合わせ（rotate-superuser.sh）は合っている形。
  *"superuser-tcp-gate.sh status") echo "superuser-tcp: closed" ;;
  # IADR-0710 決定 2: reconcile_recreate_strategy（README §3 の手で打つ段）が読む RollingUpdate の残り。
  *"get deploy "*"-o jsonpath={.spec.strategy.rollingUpdate}") echo '{"maxSurge":"25%"}' ;;
  *"exec -i deploy/postgres -c postgres -- sh -s -- "*) cat >/dev/null 2>&1 || true; echo "superuser: Secret の値で入れる" ;;
  *) cat >/dev/null 2>&1 || true ;;
esac
exit 0
EOF
chmod +x "$W/bin/kubectl"
export PATH="$W/bin:$PATH" KLOG="$W/k.log" STUB_CTX_FILE="$W/ctx"
UID_A="11111111-2222-4333-8444-555555555555" UID_B="99999999-8888-4777-8666-555555555555"
export CLOUD_CLUSTER_MARK_FILE="$W/conf/cloud-cluster-mark"
# IADR-0710 決定 4: 識別子の既定は enforce。入口の確かめ（下）は使い捨ての平文の識別子で流すので warn を明示する（既定の拒みは末尾の節が見る）。
export OB_IDENTITY_POLICY=warn
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
  "db-users/rotate-superuser.sh --live check"
  "apiserver-egress-check.sh --live"
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
# IADR-0706: 超利用者の突き合わせ（rotate-superuser.sh）は kubectl を 1 回だけ呼ぶ（Pod の中の手順へ標準入力で渡す）。その 1 回も固定される。
target ctx-a "https://127.0.0.1:6443" "$UID_A"
STUB_SWITCH_TO=ctx-b bash "$HERE/db-users/rotate-superuser.sh" --live check >/dev/null 2>&1 || ng "rotate-superuser.sh が一致の宛先で通らない"
[ "$(acts)" = 1 ] && grep -q '^ACT --context ctx-a -n platform-infra exec -i deploy/postgres -c postgres -- sh -s -- check$' "$KLOG" \
  || ng "rotate-superuser.sh の kubectl が確かめた context（ctx-a）に固定されていない"
ok "確かめた後の kubectl は、途中で現在の context が替わっても、確かめた context に固定される"

# --- 手で打つ段（README §3）: ct_pin_shell（IADR-0710 決定 2） ---
cat > "$W/bin/helm" <<'EOF2'
#!/usr/bin/env bash
echo "HELM $*" >> "$KLOG"
EOF2
chmod +x "$W/bin/helm"
ROOT_DIR="$(cd "$HERE/../.." && pwd)"
# README §3 の段と同じ形（パイプ・source した関数の中の kubectl・helm）を、固定した対話のシェルで流す。
PIN_STEPS='. "$1/deploy/cloud/lib/target-cluster.sh"; ct_pin_shell readme; echo "pin=$?"
kubectl apply -f deploy/cloud/infra/namespaces.yaml
printf "x" | kubectl apply -f -
helm upgrade --install msp deploy/helm/microservices-platform -n microservices-platform
. "$1/scripts/lib/recreate-strategy.sh" && reconcile_recreate_strategy microservices-platform llmgateway-service
echo "shell-alive"'
target ctx-a "https://127.0.0.1:6443" "$UID_A"
out="$(STUB_SWITCH_TO=ctx-b bash -c "$PIN_STEPS" x "$ROOT_DIR" 2>&1)"
grep -q '^pin=0$' <<< "$out" && grep -q '^shell-alive$' <<< "$out" || { echo "$out" >&2; ng "ct_pin_shell が一致の宛先で 0 を返さない"; }
[ "$(acts)" -ge 3 ] || ng "陽性対照: 手で打つ段の kubectl が記録されない"
grep -q '^ACT --context ctx-a -n microservices-platform patch deploy llmgateway-service ' "$KLOG" || ng "reconcile_recreate_strategy の patch が確かめた context に固定されていない"
[ "$(grep '^ACT ' "$KLOG" | grep -vc '^ACT --context ctx-a ' || true)" = 0 ] || ng "手で打つ kubectl が確かめた context（ctx-a）に固定されていない（途中で現在の context が替わった）"
[ "$(grep -c '^HELM ' "$KLOG")" = 1 ] && grep -q '^HELM --kube-context ctx-a upgrade --install msp ' "$KLOG" || ng "手で打つ helm が確かめた context に固定されていない"
for case_ in "ctx-b|https://127.0.0.1:6443|$UID_B" "ctx-a|https://10.0.0.5:6443|$UID_A" "|https://127.0.0.1:6443|$UID_A"; do
  IFS='|' read -r c s u <<< "$case_"
  target "$c" "$s" "$u"
  out="$(bash -c "$PIN_STEPS" x "$ROOT_DIR" 2>&1)"
  grep -q '^pin=4$' <<< "$out" && grep -q '^shell-alive$' <<< "$out" || { echo "$out" >&2; ng "ct_pin_shell が違う宛先（$c $s）で 4 を返さないか、シェルを閉じた"; }
  [ "$(acts)" = 0 ] && ! grep -q '^HELM ' "$KLOG" || ng "違う宛先（$c $s）で、手で打つ kubectl・helm が流れた"
  [ "$(grep -c 'ct_pin_shell] 宛先のクラスタを確かめられていない' <<< "$out")" -ge 3 ] || ng "違う宛先で、拒んだことを kubectl・helm ごとに言わない"
done
ok "手で打つ段（ct_pin_shell）: 合えばシェルの kubectl・helm（パイプ・source した関数の中も）を確かめた context に固定し、違えばシェルを閉じずに 4 を返し、以後の kubectl・helm を拒む"
# README §3 の段は、手で打つ最初の kubectl・helm より前に ct_pin_shell を流す（手順書の形を固定する）。
awk '/^## 3\. 配備の順序/{f=1} /^## 4\./{f=0} f' "$HERE/README.md" > "$W/readme3"
pin_line="$(grep -n 'ct_pin_shell readme' "$W/readme3" | head -n 1 | cut -d: -f1)"
first_bare="$(grep -nE '^(kubectl|helm|\. scripts/lib/recreate-strategy\.sh)|\| *(kubectl|helm) ' "$W/readme3" | head -n 1 | cut -d: -f1)"
[ -n "$pin_line" ] && [ -n "$first_bare" ] && [ "$pin_line" -lt "$first_bare" ] || ng "README §3 が、手で打つ最初の kubectl・helm（行 ${first_bare:-?}）より前に ct_pin_shell（行 ${pin_line:-無し}）を流さない"
ok "README §3: 手で打つ最初の kubectl・helm より前に ct_pin_shell を流す"

# --- age の識別子（ob_identity_guard） ---
target ctx-a "https://127.0.0.1:6443" "$UID_A"
set +e; OB_IDENTITY_POLICY=enforce bash "$HERE/vault/unseal.sh" --live --init-file "$W/f.age" --identity "$W/id.txt" >"$W/out" 2>&1; rc=$?; set -e
[ "$rc" -eq 2 ] && [ "$(acts)" = 0 ] && grep -q '平文の秘密鍵のまま' "$W/out" || ng "enforce で平文の識別子を拒まない（rc=$rc）"
AGE_PREFIX='AGE-SECRET-'; AGE_PREFIX+='KEY-1'   # 分けて組み立てる（deploy/ に秘密鍵らしき字面を置かない）
grep -q "$AGE_PREFIX" "$W/out" && ng "識別子の中身を表示した"
# IADR-0710 決定 4: 既定（OB_IDENTITY_POLICY を与えない）は enforce。平文の識別子では kubectl を使わずに止まり、warn の逃げ道を案内する。
: > "$KLOG"
set +e; env -u OB_IDENTITY_POLICY bash "$HERE/vault/unseal.sh" --live --init-file "$W/f.age" --identity "$W/id.txt" >"$W/out" 2>&1; rc=$?; set -e
[ "$rc" -eq 2 ] && [ "$(acts)" = 0 ] && grep -q '平文の秘密鍵のまま' "$W/out" && grep -q 'OB_IDENTITY_POLICY=warn' "$W/out" \
  || ng "既定（enforce）で平文の識別子を拒まない（rc=$rc）"
: > "$KLOG"
set +e; OB_IDENTITY_POLICY=warn bash "$HERE/vault/unseal.sh" --live --init-file "$W/f.age" --identity "$W/id.txt" >"$W/out" 2>&1; set -e
grep -q '^WARN: age の識別子' "$W/out" && [ "$(acts)" -ge 1 ] || ng "warn を明示したときに警告して進まない"
age -R "$W/recip.txt" -o "$W/id.wrapped" < "$W/id.txt"   # 包んだ識別子の代わり（平文の秘密鍵の行を持たない）
: > "$KLOG"
set +e; OB_IDENTITY_POLICY=enforce bash "$HERE/vault/unseal.sh" --live --init-file "$W/f.age" --identity "$W/id.wrapped" >"$W/out" 2>&1; set -e
grep -q '平文の秘密鍵のまま' "$W/out" && ng "包んだ識別子を平文と見なした"
[ "$(acts)" -ge 1 ] || ng "包んだ識別子で、enforce のまま先へ進まない"
set +e; OB_IDENTITY_POLICY=off bash "$HERE/vault/unseal.sh" --live --init-file "$W/f.age" --identity "$W/id.txt" >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -eq 2 ] || ng "OB_IDENTITY_POLICY の値域の外を拒まない"
ok "平文の age の識別子は、既定（enforce）と enforce で kubectl を使わずに止まり、warn を明示したときだけ警告して進む。包んだ識別子は通す。中身は表示しない"

echo "cluster-target.test.sh: ${pass} passed"
