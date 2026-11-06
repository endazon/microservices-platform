#!/usr/bin/env bash
# NFR-05, NFR-18, IADR-0689: クラウドの秘匿管理の手順（init・unseal・configure・seed・gen・put-external-secret・root-token）を、
# **実物の OpenBao**（宣言と同じ版の配布物）で通して固定する。稼働のクラスタには触れない。
#
#   bash deploy/cloud/vault/openbao-scripts.test.sh            # PATH の bao（か BAO_BIN）を使う
#
# - OpenBao は一時ディレクトリの raft・ループバックの空きポートで起こし、終わったら止めて消す。
# - kubectl は PATH のスタブ。`exec -i deploy/openbao -- sh -c <手順>` を手元の sh で走らせ（Pod の中の代わり）、引数を全部記録する。
# - 鍵・資格は試験のための使い捨て（age の識別子も一時に作る）。
# bao が無ければ失敗にする（fail-closed。CI は固定の配布物を sha256 で確かめて入れる）。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
BAO="${BAO_BIN:-$(command -v bao || true)}"
[ -n "$BAO" ] && [ -x "$BAO" ] || { echo "✗ bao が無い。本試験は実物の OpenBao で通すことが目的であり、飛ばさない（fail-closed）" >&2; exit 1; }
for t in age age-keygen node openssl; do command -v "$t" >/dev/null || { echo "✗ $t が無い" >&2; exit 1; }; done
[[ "$("$BAO" version)" == *"OpenBao v2.7.1"* ]] || { echo "✗ bao の版が宣言（2.7.1）と違う: $("$BAO" version)" >&2; exit 1; }

W="$(mktemp -d)"
PID=""
cleanup() { [ -n "$PID" ] && kill "$PID" 2>/dev/null; wait 2>/dev/null || true; rm -rf "$W"; }
trap cleanup EXIT
PORT="$(NODE_NO_WARNINGS=1 node -e 'const s=require("net").createServer().listen(0,"127.0.0.1",()=>{console.log(s.address().port);s.close();})')"
export TEST_BAO_ADDR="http://127.0.0.1:${PORT}"
mkdir -p "$W/data" "$W/bin"
cat > "$W/server.hcl" <<EOF
storage "raft" { path = "$W/data" node_id = "t0" }
listener "tcp" { address = "127.0.0.1:${PORT}" tls_disable = true }
api_addr = "$TEST_BAO_ADDR"
cluster_addr = "http://127.0.0.1:$((PORT + 1))"
EOF
"$BAO" server -config="$W/server.hcl" > "$W/server.log" 2>&1 &
PID=$!
up=0
for _ in $(seq 1 50); do rc=0; BAO_ADDR="$TEST_BAO_ADDR" "$BAO" status >/dev/null 2>&1 || rc=$?; [ "$rc" -eq 2 ] && { up=1; break; }; sleep 0.2; done
[ "$up" = 1 ] || { echo "✗ OpenBao が起動しない" >&2; cat "$W/server.log" >&2; exit 1; }

# kubectl のスタブ（引数を全部記録する。値が引数に載れば、後の確かめで見つかる）
# Pod の中の bao。試験は BAO_FAIL_ON（引数の部分文字列）で 1 回だけ失敗を差し込める（途中で落ちる経路の確かめ）。
cat > "$W/bin/bao" <<EOF
#!/usr/bin/env bash
if [ -n "\${BAO_FAIL_ON:-}" ] && [[ " \$* " == *"\$BAO_FAIL_ON"* ]] && [ ! -e "$W/fail.done" ]; then
  : > "$W/fail.done"; echo "injected failure: \$BAO_FAIL_ON" >&2; exit 1
fi
exec "$BAO" "\$@"
EOF
chmod +x "$W/bin/bao"
cat > "$W/bin/kubectl" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$KLOG"
# 試験の差し込み（1 回だけ）: KUBECTL_FAIL_ON は走らせずに落とす。KUBECTL_FAIL_AFTER は走らせてから落とす（効いたのに結果が届かない形）。
if [ -n "${KUBECTL_FAIL_ON:-}" ] && [[ "$*" == *"$KUBECTL_FAIL_ON"* ]] && [ ! -e "$FAIL_DONE" ]; then
  : > "$FAIL_DONE"; echo "injected failure (before): $KUBECTL_FAIL_ON" >&2; exit 1
fi
args=("$@")
for i in "${!args[@]}"; do
  if [ "${args[$i]}" = "--" ]; then
    rest=("${args[@]:$((i + 1))}")
    [ "${rest[0]}" = sh ] && [ "${rest[1]}" = -c ] || { echo "stub: 想定外の exec: ${rest[*]}" >&2; exit 9; }
    cd "$POD_HOME" || exit 9
    if [ -n "${KUBECTL_FAIL_AFTER:-}" ] && [[ "${rest[2]}" == *"$KUBECTL_FAIL_AFTER"* ]] && [ ! -e "$FAIL_DONE" ]; then
      env BAO_ADDR="$TEST_BAO_ADDR" HOME="$POD_HOME" PATH="$STUB_BIN:/usr/bin:/bin" sh -c "${rest[2]}" >/dev/null 2>&1 || true
      : > "$FAIL_DONE"; echo "injected failure (after): $KUBECTL_FAIL_AFTER" >&2; exit 1
    fi
    exec env BAO_ADDR="$TEST_BAO_ADDR" HOME="$POD_HOME" PATH="$STUB_BIN:/usr/bin:/bin" sh -c "${rest[2]}"
  fi
done
case "$*" in
  *"create configmap platform-infra-ca"*) cat > "$CM_DIR/$2.crt"; echo "kind: ConfigMap" ;;
  "apply -f -") cat >/dev/null ;;
  *) echo "stub: 想定外: $*" >&2; exit 9 ;;
esac
EOF
chmod +x "$W/bin/kubectl"
export FAIL_DONE="$W/fail.done" KLOG="$W/kubectl.log" POD_HOME="$W/pod" CM_DIR="$W/cm" STUB_BIN="$W/bin" PATH="$W/bin:$PATH"
mkdir -p "$POD_HOME" "$CM_DIR"
: > "$KLOG"
age-keygen -o "$W/id.txt" 2> "$W/recipients.txt"
sed -i 's/^Public key: //' "$W/recipients.txt"

pass=0
ok() { pass=$((pass + 1)); echo "  ok  $1"; }
ng() { echo "  NG  $1" >&2; exit 1; }
run() { bash "$HERE/$1" --live "${@:2}"; }
dec() { age -d -i "$W/id.txt" "$1" | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>{const j=JSON.parse(s);let v=j[process.argv[1]];if(Array.isArray(v))v=v[0];process.stdout.write(v);})' "$2"; }
B() { BAO_ADDR="$TEST_BAO_ADDR" "$BAO" "$@"; }

# --- 明示の指定と、引数の形 ---
set +e
bash "$HERE/init.sh" --recipients "$W/recipients.txt" --out "$W/x.age" >/dev/null 2>&1; [ $? -eq 3 ] || ng "init.sh は --live が無ければ 3 で終わる"
run seed-secrets.sh --operator-file "$W/none" --identity "$W/id.txt" gen cloud/../x k 32 >/dev/null 2>&1; [ $? -eq 2 ] || ng "gen は cloud/ の外のパスを拒む"
run seed-secrets.sh --operator-file "$W/none" --identity "$W/id.txt" gen cloud/ast/a 'k;x' 32 >/dev/null 2>&1; [ $? -eq 2 ] || ng "gen はキーの形の外を拒む"
run seed-secrets.sh --operator-file "$W/none" --identity "$W/id.txt" gen cloud/ast/a k 8 >/dev/null 2>&1; [ $? -eq 2 ] || ng "gen は 16 バイト未満を拒む"
set -e
ok "明示の指定（--live）が無ければ何もせず、引数の形の外（cloud/ の外・キーの字・バイト数）を拒む"

# --- init ---
run init.sh --recipients "$W/recipients.txt" --out "$W/init.age" >/dev/null
[ "$(stat -c %a "$W/init.age")" = 600 ] || ng "init のファイルが 0600 でない"
[[ "$(B status -format=json)" == *'"initialized": true'* ]] || ng "初期化されていない"
set +e; run init.sh --recipients "$W/recipients.txt" --out "$W/init2.age" >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -ne 0 ] && [ ! -e "$W/init2.age" ] || ng "2 回目の init が止まらない"
ok "init: 1 分割・閾値 1 の出力を age で暗号化したファイル（0600）だけに書き、2 回目は止まる"

# --- unseal ---
run unseal.sh --init-file "$W/init.age" --identity "$W/id.txt" >/dev/null
[[ "$(B status -format=json || true)" == *'"sealed": false'* ]] || ng "開いていない"
ok "unseal: 復号した鍵を標準入力で渡して開ける"

# --- configure ---
export OPENBAO_KUBERNETES_HOST="https://127.0.0.1:1"
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj /CN=test-k8s-ca -keyout "$W/k8s-ca.key" -out "$W/k8s-ca.crt" >/dev/null 2>&1
export OPENBAO_KUBERNETES_CA_FILE="$W/k8s-ca.crt"
ROOT0="$(dec "$W/init.age" root_token)"
# 途中で落ちる経路: root は残り、資格は出ず、そのまま流し直せる（済んだものは作り直さない）
for at in "auth enable kubernetes" "-field=secret_id"; do
  rm -f "$W/fail.done"
  set +e; BAO_FAIL_ON="$at" run configure.sh --init-file "$W/init.age" --identity "$W/id.txt" --recipients "$W/recipients.txt" --operator-out "$W/operator.age" >"$W/conf.log" 2>&1; rc=$?; [ -n "${DEBUG_TEST:-}" ] && cat "$W/conf.log" >&2; set -e
  [ -e "$W/fail.done" ] || ng "陽性対照: 失敗を差し込めていない（$at）"
  [ "$rc" -ne 0 ] || ng "途中で落ちたのに configure が成功を返した（$at）"
  [ ! -e "$W/operator.age" ] || ng "途中で落ちたのに資格のファイルが残った（$at）"
  BAO_TOKEN="$ROOT0" B token lookup >/dev/null 2>&1 || ng "途中で落ちたのに初回の root を失効させた（回復できない。$at）"
done
ok "configure: 途中で落ちても初回の root を残し、資格のファイルを残さない（マウントの後・AppRole の後のどちらでも）"
conf() { run configure.sh --init-file "$W/init.age" --identity "$W/id.txt" --recipients "$W/recipients.txt" --operator-out "$W/operator.age" "$@"; }
alive() { BAO_TOKEN="$ROOT0" B token lookup >/dev/null 2>&1; }
can_login() { [ -n "$(printf '{"role_id":"%s","secret_id":"%s"}' "$(dec "$W/operator.age" role_id)" "$(dec "$W/operator.age" secret_id)" | B write -field=token auth/approle/login - 2>/dev/null)" ]; }
# 経路 2: ConfigMap の段で落ちる → root は生きていて資格はまだ渡っていない。ファイルを消し、同じコマンドで流し直せる
rm -f "$W/fail.done"
set +e; KUBECTL_FAIL_ON="create configmap" conf >"$W/conf.log" 2>&1; rc=$?; set -e
[ -e "$W/fail.done" ] || ng "陽性対照: ConfigMap の段に失敗を差し込めていない"
[ "$rc" -ne 0 ] && [ ! -e "$W/operator.age" ] && alive || ng "ConfigMap の段で落ちたのに、ファイルが残るか root を失効させた"
ok "configure: ConfigMap の段で落ちたら、資格のファイルを消して root を残す（同じコマンドで流し直せる）"
# 経路 3: 失効の段で落ちる（失効が効かなかった）→ 資格のファイルは残して有効、root も生きている
rm -f "$W/fail.done"
set +e; BAO_FAIL_ON="token revoke -self" conf >"$W/conf.log" 2>&1; rc=$?; set -e
[ -e "$W/fail.done" ] || ng "陽性対照: 失効の段に失敗を差し込めていない"
[ "$rc" -eq 3 ] || ng "失効の段の失敗が 3 で終わらない（$rc）"
[ -e "$W/operator.age" ] && can_login || ng "失効の段で落ちたのに、資格のファイルを消した（回復できない）"
alive || ng "失効の段で失効が効かなかったのに、root が無い"
grep -q -- '--revoke-only' "$W/conf.log" || ng "失効だけを流し直す口を案内しない"
set +e; conf >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -eq 2 ] && can_login && alive || ng "資格のファイルが在るときの流し直しが、止まらずに何かを変えた"
# 封印中は、root が生きていても「失効した」と言わずに止まる（fail-open にしない）。開けてから流し直すと失効して通る
BAO_TOKEN="$ROOT0" B operator seal >/dev/null
[[ "$(B status -format=json || true)" == *'"sealed": true'* ]] || ng "陽性対照: 封印できていない"
set +e; run configure.sh --revoke-only --init-file "$W/init.age" --identity "$W/id.txt" >"$W/conf.log" 2>&1; rc=$?; set -e
[ "$rc" -eq 3 ] || ng "封印中の --revoke-only が 3 で止まらない（$rc）"
grep -q '既に失効している' "$W/conf.log" && ng "封印中なのに「既に失効している」と言った（fail-open）"
run unseal.sh --init-file "$W/init.age" --identity "$W/id.txt" >/dev/null
alive || ng "封印の後に root が生きていない（封印中に失効させた）"
ok "configure --revoke-only: 封印中は失効したとも生きているとも言わずに 3 で止まり、root は生きたまま"
# 失効だけを流し直す。1 回目は効いたのに結果が届かない形で落とす → root は無く、ファイルは有効のまま
rm -f "$W/fail.done"
set +e; KUBECTL_FAIL_AFTER="token revoke -self" run configure.sh --revoke-only --init-file "$W/init.age" --identity "$W/id.txt" >/dev/null 2>&1; rc=$?; set -e
[ -e "$W/fail.done" ] && [ "$rc" -ne 0 ] || ng "陽性対照: 失効の結果を落とせていない"
! alive && can_login || ng "失効が効いた後に落ちたのに、root が生きているか資格が使えない"
run configure.sh --revoke-only --init-file "$W/init.age" --identity "$W/id.txt" >"$W/conf.log" 2>&1 || ng "失効だけの流し直しが、既に失効した root で止まった"
grep -q '既に失効している' "$W/conf.log" || ng "既に失効していることを伝えない"
ok "configure: 失効の段で落ちたら資格のファイルを有効のまま残し、--revoke-only で失効だけを流し直せる（効いたのに結果が届かない形でも）"
[ "$(stat -c %a "$W/operator.age")" = 600 ] || ng "オーナー用の資格のファイルが 0600 でない"
! alive || ng "初回の root が失効していない"
for ns in platform-infra microservices-platform; do grep -q 'BEGIN CERTIFICATE' "$CM_DIR/$ns.crt" || ng "CA の ConfigMap（$ns）が無い"; done
ok "configure: 案内どおりに流し直すと最後まで進み、初回の root は失効し、オーナー用の資格は age のファイル（0600）にだけ在り、CA の証明書を ConfigMap に置く"

# 試験の側で、オーナー用の資格と unseal の鍵から root を作り直す（root-token.sh と同じ道筋の独立な確かめ）
login() { printf '{"role_id":"%s","secret_id":"%s"}' "$(dec "$W/operator.age" role_id)" "$(dec "$W/operator.age" secret_id)" | B write -field=token auth/approle/login -; }
OPT="$(login)"
[ -n "$OPT" ] || ng "オーナー用の資格で入れない"
mkroot() {
  local otp nonce enc
  otp="$(BAO_TOKEN="$OPT" B operator generate-root -generate-otp)"
  BAO_TOKEN="$OPT" B operator generate-root -cancel >/dev/null 2>&1 || true
  BAO_TOKEN="$OPT" B operator generate-root -init -otp="$otp" >/dev/null
  nonce="$(BAO_TOKEN="$OPT" B operator generate-root -status -format=json | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>process.stdout.write(JSON.parse(s).nonce))')"
  enc="$(dec "$W/init.age" unseal_keys_b64 | BAO_TOKEN="$OPT" B operator generate-root -nonce="$nonce" -format=json - | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>process.stdout.write(JSON.parse(s).encoded_token))')"
  printf '%s' "$enc" | BAO_TOKEN="$OPT" B operator generate-root -decode=- -otp="$otp"
}

# --- seed ---
run seed-secrets.sh --operator-file "$W/operator.age" --identity "$W/id.txt" >/dev/null
get() { BAO_TOKEN="$OPT" B kv get -mount=cloud -format=json "$1" | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>process.stdout.write(JSON.stringify(JSON.parse(s).data.data)))'; }
for p in platform/postgres platform/keycloak-db platform/keycloak-admin platform/rabbitmq shared/ast-db; do
  get "$p" | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>{const d=JSON.parse(s);if(Object.keys(d).sort().join()!=="password,username"||!/^[a-z]+_[0-9a-f]{6}$/.test(d.username)||!/^[0-9a-f]{48}$/.test(d.password))process.exit(1);})' \
    || ng "cloud/$p の形が違う"
done
users="$(grep -v '^[[:space:]]*#' "$HERE/../../local/rabbitmq-setup/users.txt" | awk 'NF >= 2 && $2 == "ai-stock-trading" { print $1 }' | sort | tr '\n' ',')"
[ "$(get shared/ast-rabbitmq-users | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>process.stdout.write(Object.keys(JSON.parse(s)).sort().join(",")+","))')" = "$users" ] || ng "ブローカの利用者の集合が users.txt の取引ユニットの行と違う"
[ "$(get shared/ast-client-secrets | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>process.stdout.write(Object.keys(JSON.parse(s)).sort().join(",")))')" = "ai-stock-trading-llm-caller,ai-stock-trading-owner,ai-stock-trading-svc" ] || ng "client の secret のキーが 3 つの clientId でない"
before="$(for p in platform/postgres shared/ast-db shared/ast-rabbitmq-users shared/ast-client-secrets; do get "$p"; done)"
run seed-secrets.sh --operator-file "$W/operator.age" --identity "$W/id.txt" >/dev/null
after="$(for p in platform/postgres shared/ast-db shared/ast-rabbitmq-users shared/ast-client-secrets; do get "$p"; done)"
[ "$before" = "$after" ] || ng "2 回目の seed が値を変えた"
ok "seed: 7 種を作り（利用者名は乱数の接尾辞・パスワードは 24 バイト・ブローカは users.txt の 11 人・client は 3 つ）、2 回目は何も変えない"

# --- gen ---
run seed-secrets.sh --operator-file "$W/operator.age" --identity "$W/id.txt" gen cloud/ast/ast-secrets order-approval-signing-key 32 >/dev/null
v="$(BAO_TOKEN="$OPT" B kv get -mount=cloud -field=order-approval-signing-key ast/ast-secrets)"
[[ "$v" =~ ^[A-Za-z0-9+/]{43}=$ ]] || ng "gen の出力が標準の base64（32 バイト → 44 字・パディングつき）でない"
[ "$(printf '%s' "$v" | base64 -d | wc -c)" -eq 32 ] || ng "gen の元が 32 バイトでない"
run seed-secrets.sh --operator-file "$W/operator.age" --identity "$W/id.txt" gen cloud/ast/ast-secrets other 48 >/dev/null
[ "$(BAO_TOKEN="$OPT" B kv get -mount=cloud -field=other ast/ast-secrets | base64 -d | wc -c)" -eq 48 ] || ng "gen の 48 バイト"
[ "$(BAO_TOKEN="$OPT" B kv get -mount=cloud -field=order-approval-signing-key ast/ast-secrets)" = "$v" ] || ng "在るパスへの gen が、ほかのキーを消した"
set +e; run seed-secrets.sh --operator-file "$W/operator.age" --identity "$W/id.txt" gen cloud/ast/ast-secrets order-approval-signing-key 32 >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -ne 0 ] && [ "$(BAO_TOKEN="$OPT" B kv get -mount=cloud -field=order-approval-signing-key ast/ast-secrets)" = "$v" ] || ng "在るキーへの gen が止まらない（上書きした）"
ok "gen: 指定のバイト数の乱数を標準の base64 で書き、在るパスでは patch（ほかのキーを残す）、在るキーは上書きしない"

# --- put-external-secret.sh: 外から持ち込む値を標準入力からだけ受けて書く ---
put() { run put-external-secret.sh --operator-file "$W/operator.age" --identity "$W/id.txt" "$@"; }
set +e
printf 'x' | bash "$HERE/put-external-secret.sh" --operator-file "$W/operator.age" --identity "$W/id.txt" cloud/msp/a k >/dev/null 2>&1; [ $? -eq 3 ] || ng "put は --live が無ければ 3 で終わる"
printf 'x' | put cloud/shared/ast-db password >/dev/null 2>&1; [ $? -eq 2 ] || ng "put は cloud/shared へ書かない"
printf 'x' | put cloud/msp/a 'k;x' >/dev/null 2>&1; [ $? -eq 2 ] || ng "put はキーの形の外を拒む"
printf '' | put cloud/msp/llm-provider-credentials anthropic-api-key >/dev/null 2>&1; [ $? -eq 2 ] || ng "put は空の値を書かない"
set -e
k1="$(head -c 30 /dev/urandom | base64 | tr -d '\n=+/')"
printf '%s' "$k1" | put cloud/msp/llm-provider-credentials anthropic-api-key >/dev/null
[ "$(BAO_TOKEN="$OPT" B kv get -mount=cloud -field=anthropic-api-key msp/llm-provider-credentials)" = "$k1" ] || ng "put が値をそのまま書かない"
k2="$(head -c 30 /dev/urandom | base64 | tr -d '\n=+/')"
set +e; printf '%s' "$k2" | put cloud/msp/llm-provider-credentials anthropic-api-key >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -eq 5 ] && [ "$(BAO_TOKEN="$OPT" B kv get -mount=cloud -field=anthropic-api-key msp/llm-provider-credentials)" = "$k1" ] || ng "在るキーへの put が 5 で止まらない（$rc）か、上書きした"
printf '%s' "$k2" | put --overwrite cloud/msp/llm-provider-credentials anthropic-api-key >/dev/null
[ "$(BAO_TOKEN="$OPT" B kv get -mount=cloud -field=anthropic-api-key msp/llm-provider-credentials)" = "$k2" ] || ng "--overwrite で上書きしない"
k3="$(head -c 30 /dev/urandom | base64 | tr -d '\n=+/')"
printf '%s' "$k3" | put cloud/msp/llm-provider-credentials voyage-api-key >/dev/null
[ "$(BAO_TOKEN="$OPT" B kv get -mount=cloud -field=anthropic-api-key msp/llm-provider-credentials)" = "$k2" ] || ng "在るパスへの put が、ほかのキーを消した"
# moomoo-rsa: 手元の openssl の出力をそのまま流す（1024 ビット・PKCS#1。末尾の改行も変えない）
openssl genrsa -traditional 1024 2>/dev/null | tee "$W/rsa.expected" | put cloud/ast/moomoo-rsa opend_rsa.pem >/dev/null
BAO_TOKEN="$OPT" B kv get -mount=cloud -field=opend_rsa.pem ast/moomoo-rsa > "$W/rsa.got"
# kv get -field は値をそのまま出す（末尾の改行も含めて比べる）
cmp -s "$W/rsa.expected" "$W/rsa.got" || ng "PEM がそのまま書かれない"
rsa_text="$(openssl rsa -in "$W/rsa.got" -noout -text 2>/dev/null || true)"
[[ "$rsa_text" == *"1024 bit"* ]] || ng "PEM が 1024 ビットの RSA として読めない"
# 取引ユニットの呼び方（前置きを空白で分けて読み、末尾に パス キー を足し、値は標準入力。AST の ast-owner-totp-enroll.sh --openbao）
read -r -a PUT_CMD <<< "bash $HERE/put-external-secret.sh --live --operator-file $W/operator.age --identity $W/id.txt"
head -c 20 /dev/urandom | base64 | tr -d '\n' > "$W/totp"
"${PUT_CMD[@]}" "cloud/ast/ast-owner-totp" secret < "$W/totp" >/dev/null
[ "$(BAO_TOKEN="$OPT" B kv get -mount=cloud -field=secret ast/ast-owner-totp)" = "$(cat "$W/totp")" ] || ng "取引ユニットの呼び方で書けない"
set +e; "${PUT_CMD[@]}" "cloud/ast/ast-owner-totp" secret < "$W/totp" >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -eq 5 ] || ng "取引ユニットの呼び方で、在るキーが 5 で止まらない（$rc）"
"${PUT_CMD[@]}" --overwrite "cloud/ast/ast-owner-totp" secret < "$W/totp" >/dev/null || ng "取引ユニットの呼び方で、--overwrite（前置きの後ろ）が効かない"
rm -f "$W/rsa.expected" "$W/rsa.got" "$W/totp"
ok "put: 値を標準入力からだけ受けてそのまま書き（PEM の改行も変えない）、在るキーは 5 で止まり、--overwrite でだけ上書きし、cloud/shared へは書かない。取引ユニットの呼び方で通る"
# 読めない状態（オーナー用の policy から読み取りを外す）では、書かずに 1 で止まり、在る値もほかのキーも変えない
RP="$(mkroot)"
policy_without() { grep -v -- "$1" "$HERE/policies/cloud-operator.hcl" | BAO_TOKEN="$RP" B policy write cloud-operator - >/dev/null; }
before="$(BAO_TOKEN="$RP" B kv get -mount=cloud -format=json msp/llm-provider-credentials | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>process.stdout.write(JSON.stringify(JSON.parse(s).data.data)))')"
for cut in 'cloud/metadata/' 'cloud/data/'; do
  if [ "$cut" = 'cloud/data/' ]; then
    # データの読み取りだけを外す（書き込みは残す）: パスの有無は読めて、キーの有無が読めない
    sed 's/"create", "read", "update", "patch"/"create", "update", "patch"/' "$HERE/policies/cloud-operator.hcl" | BAO_TOKEN="$RP" B policy write cloud-operator - >/dev/null
  else
    policy_without "$cut"
  fi
  set +e; printf '%s' "$(head -c 30 /dev/urandom | base64 | tr -d '\n=+/')" | put cloud/msp/llm-provider-credentials new-key >"$W/put.log" 2>&1; rc=$?; set -e
  [ "$rc" -eq 1 ] || ng "読めない状態（$cut を外した）の put が 1 で止まらない（$rc）"
  grep -q '読めない' "$W/put.log" || ng "読めない状態の put が理由を言わない（$cut）"
  BAO_TOKEN="$RP" B policy write cloud-operator "$HERE/policies/cloud-operator.hcl" >/dev/null
  after="$(BAO_TOKEN="$RP" B kv get -mount=cloud -format=json msp/llm-provider-credentials | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>process.stdout.write(JSON.stringify(JSON.parse(s).data.data)))')"
  [ "$after" = "$before" ] || ng "読めない状態の put が、在るパスの値を変えた（$cut を外した。ほかのキーが消えた）"
done
BAO_TOKEN="$RP" B token revoke -self >/dev/null
ok "put: パスやキーの有無を読めないとき（読み取りを外した資格）は、書かずに 1 で止まり、在るパスの値もほかのキーも変えない"

R1="$(mkroot)"

# --- policy の境界（試験の側で作り直した root で、各 policy のトークンの能力を引く） ---
cap() { BAO_TOKEN="$R1" B token capabilities "$1" "$2"; }
tok() { BAO_TOKEN="$R1" B token create -policy="$1" -no-default-policy -field=token; }
TP="$(tok platform-infra-eso)" TM="$(tok msp-eso)" TA="$(tok ast-eso)"
[ "$(cap "$TP" cloud/data/platform/postgres)" = read ] || ng "基盤の側が cloud/platform を読めない"
[ "$(cap "$TP" cloud/data/ast/ast-secrets)" = deny ] || ng "基盤の側が cloud/ast を読める"
[ "$(cap "$TA" cloud/data/ast/ast-secrets)" = read ] || ng "取引ユニットの側が cloud/ast を読めない"
[ "$(cap "$TA" cloud/data/platform/postgres)" = deny ] || ng "取引ユニットの側が cloud/platform を読める"
for p in ast-db ast-rabbitmq-users ast-client-secrets platform-infra-ca; do [ "$(cap "$TA" "cloud/data/shared/$p")" = read ] || ng "取引ユニットの側が cloud/shared/$p を読めない"; done
[ "$(cap "$TA" cloud/data/shared/other)" = deny ] || ng "取引ユニットの側が名指しの外の cloud/shared を読める"
[ "$(cap "$TM" pki_cloud/issue/llmgateway-service)" = update ] && [ "$(cap "$TM" pki_cloud/issue/postgres)" = deny ] || ng "MSP の側の発行の範囲が違う"
for p in platform-backup-s3 alertmanager-discord alertmanager-heartbeat; do [ "$(cap "$TP" "cloud/data/platform/$p")" = read ] || ng "基盤の側が cloud/platform/$p を読めない"; done
[ "$(cap "$TM" cloud/data/msp/llm-provider-credentials)" = read ] || ng "MSP の側が LLM の鍵を読めない"
[ "$(cap "$TP" cloud/data/msp/llm-provider-credentials)" = deny ] && [ "$(cap "$TA" cloud/data/msp/llm-provider-credentials)" = deny ] || ng "MSP の外の側が LLM の鍵を読める"
[ "$(cap "$TM" cloud/data/platform/platform-backup-s3)" = deny ] && [ "$(cap "$TA" cloud/data/platform/alertmanager-discord)" = deny ] || ng "基盤の外の側が外から持ち込む基盤の値を読める"
[ "$(cap "$OPT" sys/policies/acl/x)" = deny ] && [ "$(cap "$OPT" sys/auth/x)" = deny ] || ng "オーナー用の資格が policy・認証を変えられる"
ok "policy: 基盤は cloud/ast を、取引ユニットは cloud/platform と名指しの外の cloud/shared を読めず、オーナー用の資格は policy・認証を変えられない"

# --- PKI: 名指しの名前だけを発行し、CA の鍵を出さない ---
TPI="$(BAO_TOKEN="$TP" B write -format=json pki_cloud/issue/postgres common_name=postgres alt_names=postgres.platform-infra,postgres.platform-infra.svc,postgres.platform-infra.svc.cluster.local ttl=8760h)"
[[ "$TPI" == *"PRIVATE KEY"* ]] || ng "postgres の証明書を発行できない"
set +e; BAO_TOKEN="$TP" B write pki_cloud/issue/postgres common_name=evil.example >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -ne 0 ] || ng "名指しの外の名前を発行した"
set +e; BAO_TOKEN="$R1" B read pki_cloud/issuer/default/json >/dev/null 2>&1; BAO_TOKEN="$R1" B read -field=private_key pki_cloud/key/default >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -ne 0 ] || ng "CA の鍵を読み出せる"
ok "PKI: role は名指しの名前だけを発行し、CA の鍵は OpenBao の外へ出ない（internal）"
BAO_TOKEN="$R1" B token revoke -self >/dev/null

# --- root-token.sh: 作り直して操作し、失効させる ---
run root-token.sh --init-file "$W/init.age" --operator-file "$W/operator.age" --identity "$W/id.txt" apply-policies >/dev/null
run root-token.sh --init-file "$W/init.age" --operator-file "$W/operator.age" --identity "$W/id.txt" rotate-operator --recipients "$W/recipients.txt" --operator-out "$W/operator2.age" >/dev/null
set +e; printf '{"role_id":"%s","secret_id":"%s"}' "$(dec "$W/operator.age" role_id)" "$(dec "$W/operator.age" secret_id)" | B write -field=token auth/approle/login - >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -ne 0 ] || ng "作り直した後も古い secret_id で入れる"
cp "$W/operator2.age" "$W/operator.age"; OPT="$(login)"; [ -n "$OPT" ] || ng "新しい資格で入れない"
ok "root-token: 作り直した root で policy を当て直し、オーナーの secret_id を回す（古いものは入れない）"

# --- 値が引数・ディスクに残らない ---
R2="$(mkroot)"
vals="$(for p in platform/postgres platform/keycloak-db platform/keycloak-admin platform/rabbitmq shared/ast-db shared/ast-rabbitmq-users shared/ast-client-secrets ast/ast-secrets msp/llm-provider-credentials ast/moomoo-rsa ast/ast-owner-totp; do
  BAO_TOKEN="$R2" B kv get -mount=cloud -format=json "$p" | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>{for(const v of Object.values(JSON.parse(s).data.data))if(!/^[a-z]+_[0-9a-f]{6}$/.test(v))console.log(v)})'; done
  dec "$W/init.age" unseal_keys_b64; echo; dec "$W/operator.age" secret_id; echo)"
BAO_TOKEN="$R2" B token revoke -self >/dev/null
n=0
while IFS= read -r v; do
  [ -n "$v" ] || continue
  n=$((n + 1))
  grep -qF -- "$v" "$KLOG" && ng "値が kubectl の引数に載った"
  if grep -rqF -- "$v" "$POD_HOME" "$CM_DIR" 2>/dev/null; then ng "値がディスク（Pod の代わりの作業場・ConfigMap）に残った"; fi
done <<< "$vals"
[ "$n" -ge 20 ] || ng "陽性対照: 確かめた値が少ない（$n）"
grep -q -- 'exec -i deploy/openbao -c openbao -- sh -c' "$KLOG" || ng "陽性対照: 引数の記録が無い"
ok "値（作った ${n} 件・unseal の鍵・オーナーの secret_id）は kubectl の引数にも、手元の作業場にも残らない"

echo "${pass} passed"
