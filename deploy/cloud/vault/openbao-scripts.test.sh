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
# 絶対のパスにする（スタブの kubectl は Pod の作業場へ cd してから bao を呼ぶので、相対のパスでは見つからず「状態を読めない」で落ちる）。
BAO="$(cd "$(dirname "$BAO")" && pwd)/$(basename "$BAO")"
for t in age age-keygen node openssl; do command -v "$t" >/dev/null || { echo "✗ $t が無い" >&2; exit 1; }; done
[[ "$("$BAO" version)" == *"OpenBao v2.7.1"* ]] || { echo "✗ bao の版が宣言（2.7.1）と違う: $("$BAO" version)" >&2; exit 1; }

W="$(mktemp -d)"
PID=""
cleanup() { [ -n "$PID" ] && kill "$PID" 2>/dev/null; wait 2>/dev/null || true; rm -rf "$W"; }
trap cleanup EXIT
PORT="$(NODE_NO_WARNINGS=1 node -e 'const s=require("net").createServer().listen(0,"127.0.0.1",()=>{console.log(s.address().port);s.close();})')"
# NFR-16, IADR-0696: 受け口は TLS だけ。証明書は tls-cert.sh が作ったもの（スタブが Secret を $W/tls へ「マウント」する）を使う。
export TEST_BAO_ADDR="https://127.0.0.1:${PORT}"
mkdir -p "$W/data" "$W/bin" "$W/tls"

# kubectl のスタブ（引数を全部記録する。値が引数に載れば、後の確かめで見つかる）
# Pod の中の bao。試験は BAO_FAIL_ON（引数の部分文字列）で 1 回だけ失敗を差し込める（途中で落ちる経路の確かめ）。
# BAO_STANDBY_STATUS=N は、最初の N 回の `bao status` を、開けた直後の standby の形（active の欄 is_self・leader_* ・
# active_time が無い。OpenBao 2.7.1 の実測）で返す（回数は $W/standby.count）。
cat > "$W/bin/bao" <<EOF
#!/usr/bin/env bash
if [ -n "\${BAO_STANDBY_STATUS:-}" ] && [ "\${1:-}" = status ]; then
  n="\$(cat "$W/standby.count" 2>/dev/null || echo 0)"
  if [ "\$n" -lt "\$BAO_STANDBY_STATUS" ]; then
    echo "\$((n + 1))" > "$W/standby.count"
    out="\$("$BAO" "\$@")"; rc=\$?
    while IFS= read -r l; do
      case "\$l" in *'"is_self"'* | *'"leader_'* | *'"active_time"'*) ;; *) printf '%s\n' "\$l" ;; esac
    done <<< "\$out"
    exit "\$rc"
  fi
fi
if [ -n "\${BAO_FAIL_ON:-}" ] && [[ " \$* " == *"\$BAO_FAIL_ON"* ]] && [ ! -e "$W/fail.done" ]; then
  : > "$W/fail.done"; echo "injected failure: \$BAO_FAIL_ON" >&2; exit 1
fi
exec "$BAO" "\$@"
EOF
chmod +x "$W/bin/bao"
cat > "$W/bin/kubectl" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$KLOG"
# IADR-0703 決定 2: 宛先のクラスタの確かめ（lib/target-cluster.sh）に答え、以後の呼び出しは確かめた context に固定されていることを求める。
case "$*" in
  "config current-context") echo cloud-test; exit 0 ;;
  "config view --minify --context cloud-test -o jsonpath={.clusters[0].cluster.server}") echo "https://127.0.0.1:6443"; exit 0 ;;
  "--context cloud-test get namespace kube-system -o jsonpath={.metadata.uid}") echo "$STUB_CLUSTER_UID"; exit 0 ;;
esac
[ "${1:-}" = --context ] && [ "${2:-}" = cloud-test ] || { echo "stub: context に固定されていない呼び出し: $*" >&2; exit 9; }
shift 2
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
      env BAO_ADDR="$POD_BAO_ADDR" BAO_SKIP_VERIFY=true HOME="$POD_HOME" PATH="$STUB_BIN:/usr/bin:/bin" sh -c "${rest[2]}" >/dev/null 2>&1 || true
      : > "$FAIL_DONE"; echo "injected failure (after): $KUBECTL_FAIL_AFTER" >&2; exit 1
    fi
    exec env BAO_ADDR="$POD_BAO_ADDR" BAO_SKIP_VERIFY=true HOME="$POD_HOME" PATH="$STUB_BIN:/usr/bin:/bin" sh -c "${rest[2]}"
  fi
done
case "$*" in
  *"create configmap platform-infra-ca"*) cat > "$CM_DIR/$2.crt"; echo "kind: ConfigMap" ;;
  # IADR-0710 決定 3: オーナー用の secret_id の期限の記録（公開してよい値。引数で渡る）。
  "-n platform-infra create configmap openbao-operator-credential --from-literal=not-after="*" --dry-run=client -o yaml")
    printf '%s\n' "${6#--from-literal=not-after=}" > "$CM_DIR/operator-not-after"; echo "kind: ConfigMap" ;;
  "apply -f -") cat >/dev/null ;;
  # IADR-0696: tls-cert.sh の Secret・ConfigMap（JSON を標準入力で受ける）。Secret は $TLS_DIR へ書く（kubelet のマウントの代わり）。
  "apply --server-side --field-manager=openbao-tls-cert --force-conflicts -f -")
    node -e '
      const fs = require("fs"); const d = JSON.parse(fs.readFileSync(0, "utf8"));
      const [tls, cm] = process.argv.slice(1);
      if (d.kind === "Secret" && d.metadata.name === "openbao-tls" && d.metadata.namespace === "openbao") {
        for (const [k, v] of Object.entries(d.data)) fs.writeFileSync(`${tls}/${k}`, Buffer.from(v, "base64"), { mode: 0o600 });
      } else if (d.kind === "ConfigMap" && d.metadata.name === "openbao-ca") {
        fs.writeFileSync(`${cm}/${d.metadata.namespace}-openbao-ca.crt`, d.data["ca.crt"]);
      } else { process.stderr.write("stub: 想定外の apply\n"); process.exit(9); }' "$TLS_DIR" "$CM_DIR" ;;
  "get namespace "*" --ignore-not-found -o name")
    for n in $K8S_NAMESPACES; do [ "$3" = "$n" ] && echo "namespace/$n"; done; exit 0 ;;
  "-n openbao get secret openbao-tls --ignore-not-found -o name")
    [ -e "$TLS_DIR/tls.crt" ] && echo "secret/openbao-tls"; exit 0 ;;
  "-n openbao get secret openbao-tls --ignore-not-found -o jsonpath={.data.ca\\.crt}")
    [ -e "$TLS_DIR/ca.crt" ] && base64 -w0 < "$TLS_DIR/ca.crt"; exit 0 ;;
  *) echo "stub: 想定外: $*" >&2; exit 9 ;;
esac
EOF
chmod +x "$W/bin/kubectl"
# IADR-0710 決定 4: 識別子の既定は enforce。この試験の識別子は使い捨ての平文なので、warn を明示して流す（既定の拒みは cluster-target.test.sh が見る）。
export OB_IDENTITY_POLICY=warn
export FAIL_DONE="$W/fail.done" KLOG="$W/kubectl.log" POD_HOME="$W/pod" CM_DIR="$W/cm" STUB_BIN="$W/bin" PATH="$W/bin:$PATH"
# IADR-0703 決定 2: 手元に記録したクラスタの印（スタブが答える UID と同じ）。
export STUB_CLUSTER_UID="0b6c2f4e-1d3a-4c5b-9e8f-7a6b5c4d3e2f" CLOUD_CLUSTER_MARK_FILE="$W/cluster-mark"
printf '%s\n' "$STUB_CLUSTER_UID" > "$CLOUD_CLUSTER_MARK_FILE"
export TLS_DIR="$W/tls" K8S_NAMESPACES="openbao platform-infra microservices-platform ai-stock-trading"
# Pod の中の環境に置く宛先は平文（手順の側が TLS の宛先と CA を明示しなければ届かない）。手順の宛先は試験の OpenBao の TLS の口。
export POD_BAO_ADDR="http://127.0.0.1:${PORT}" OB_POD_ADDR="$TEST_BAO_ADDR" OB_POD_TLS_DIR="$W/tls" OB_TLS_SYNC_WAIT_S=10
mkdir -p "$POD_HOME" "$CM_DIR"
: > "$KLOG"

# --- 受け口の証明書（tls-cert.sh issue）と、TLS だけで受ける OpenBao ---
set +e
bash "$HERE/tls-cert.sh" issue >/dev/null 2>&1; [ $? -eq 3 ] || { echo "✗ tls-cert.sh は --live が無ければ 3 で終わる" >&2; exit 1; }
set -e
bash "$HERE/tls-cert.sh" --live issue >/dev/null
for f in tls.crt tls.key ca.crt; do [ -s "$W/tls/$f" ] || { echo "✗ tls-cert.sh issue が Secret の $f を置かない" >&2; exit 1; }; done
openssl verify -CAfile "$W/tls/ca.crt" "$W/tls/tls.crt" >/dev/null || { echo "✗ 受け口の証明書を CA で検証できない" >&2; exit 1; }
cat > "$W/server.hcl" <<HCL
storage "raft" { path = "$W/data" node_id = "t0" }
listener "tcp" {
  address         = "127.0.0.1:${PORT}"
  tls_cert_file   = "$W/tls/tls.crt"
  tls_key_file    = "$W/tls/tls.key"
  tls_min_version = "tls13"
}
api_addr = "$TEST_BAO_ADDR"
cluster_addr = "https://127.0.0.1:$((PORT + 1))"
HCL
"$BAO" server -config="$W/server.hcl" > "$W/server.log" 2>&1 &
PID=$!
export OB_POD_SERVER_PID="$PID"
up=0
for _ in $(seq 1 50); do rc=0; BAO_ADDR="$TEST_BAO_ADDR" BAO_CACERT="$W/tls/ca.crt" "$BAO" status >/dev/null 2>&1 || rc=$?; [ "$rc" -eq 2 ] && { up=1; break; }; sleep 0.2; done
[ "$up" = 1 ] || { echo "✗ OpenBao が起動しない" >&2; cat "$W/server.log" >&2; exit 1; }
age-keygen -o "$W/id.txt" 2> "$W/recipients.txt"
sed -i 's/^Public key: //' "$W/recipients.txt"

pass=0
ok() { pass=$((pass + 1)); echo "  ok  $1"; }
ng() { echo "  NG  $1" >&2; exit 1; }
run() { bash "$HERE/$1" --live "${@:2}"; }
dec() { age -d -i "$W/id.txt" "$1" | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>{const j=JSON.parse(s);let v=j[process.argv[1]];if(Array.isArray(v))v=v[0];process.stdout.write(v);})' "$2"; }
B() { BAO_ADDR="$TEST_BAO_ADDR" BAO_CACERT="$W/tls/ca.crt" "$BAO" "$@"; }

# --- 明示の指定と、引数の形 ---
set +e
bash "$HERE/init.sh" --recipients "$W/recipients.txt" --out "$W/x.age" >/dev/null 2>&1; [ $? -eq 3 ] || ng "init.sh は --live が無ければ 3 で終わる"
run seed-secrets.sh --operator-file "$W/none" --identity "$W/id.txt" gen cloud/../x k 32 >/dev/null 2>&1; [ $? -eq 2 ] || ng "gen は cloud/ の外のパスを拒む"
run seed-secrets.sh --operator-file "$W/none" --identity "$W/id.txt" gen cloud/ast/a 'k;x' 32 >/dev/null 2>&1; [ $? -eq 2 ] || ng "gen はキーの形の外を拒む"
run seed-secrets.sh --operator-file "$W/none" --identity "$W/id.txt" gen cloud/ast/a k 8 >/dev/null 2>&1; [ $? -eq 2 ] || ng "gen は 16 バイト未満を拒む"
# IADR-0706: rotate は seed が作る値だけ・userpass は password だけ
run seed-secrets.sh --operator-file "$W/none" --identity "$W/id.txt" rotate cloud/platform/postgres username >/dev/null 2>&1; [ $? -eq 2 ] || ng "rotate は利用者名を作り直す"
run seed-secrets.sh --operator-file "$W/none" --identity "$W/id.txt" rotate cloud/ast/ast-secrets order-approval-signing-key >/dev/null 2>&1; [ $? -eq 2 ] || ng "rotate は seed の外（gen の値）を受ける"
run seed-secrets.sh --operator-file "$W/none" --identity "$W/id.txt" rotate cloud/platform/postgres >/dev/null 2>&1; [ $? -eq 2 ] || ng "rotate はキーが無くても受ける"
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
st="$(B status -format=json || true)"
[[ "$st" == *'"sealed": false'* ]] || ng "開いていない"
[[ "$st" == *'"is_self": true'* ]] || ng "unseal.sh が、書き込みを受ける状態（active）になる前に戻った"
ok "unseal: 復号した鍵を標準入力で渡して開け、書き込みを受ける状態（active）になってから戻る"

# --- configure ---
export OPENBAO_KUBERNETES_HOST="https://127.0.0.1:1"
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj /CN=test-k8s-ca -keyout "$W/k8s-ca.key" -out "$W/k8s-ca.crt" >/dev/null 2>&1
export OPENBAO_KUBERNETES_CA_FILE="$W/k8s-ca.crt"
ROOT0="$(dec "$W/init.age" root_token)"
# 途中で落ちる経路: root は残り、資格は出ず、そのまま流し直せる（済んだものは作り直さない）
for at in "auth enable kubernetes" "write -f -format=json auth/approle/role/cloud-operator/secret-id"; do
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
# 開けた直後の standby（読み取りだけが通り、書き込みは拒まれる）が続く間: unseal.sh は待ち切って 1 で止まり（OK と言わない）、
# --revoke-only も失効させず判定もせずに 3 で止まる。実物は開いて active になっている（standby は Pod の中の bao status だけの差し込み）
rm -f "$W/standby.count"
set +e; BAO_STANDBY_STATUS=1000 OB_ACTIVE_WAIT_S=2 run unseal.sh --init-file "$W/init.age" --identity "$W/id.txt" >"$W/unseal.log" 2>&1; rc=$?; set -e
[ "$rc" -eq 1 ] || ng "standby のまま active にならないのに、unseal.sh が 1 で止まらない（$rc）"
grep -q 'active）にならない' "$W/unseal.log" || ng "unseal.sh が、active にならないことを告げない"
grep -q '^OK' "$W/unseal.log" && ng "standby のままなのに、unseal.sh が OK と言った"
[ "$(cat "$W/standby.count")" -ge 2 ] || ng "陽性対照: unseal.sh が状態を繰り返し確かめていない"
rm -f "$W/standby.count"
set +e; BAO_STANDBY_STATUS=1000 OB_ACTIVE_WAIT_S=2 run configure.sh --revoke-only --init-file "$W/init.age" --identity "$W/id.txt" >"$W/conf.log" 2>&1; rc=$?; set -e
[ "$rc" -eq 3 ] || ng "standby の間の --revoke-only が 3 で止まらない（$rc）"
grep -q '既に失効している' "$W/conf.log" && ng "standby の間に「既に失効している」と言った（fail-open）"
alive || ng "standby の間の --revoke-only が、root を失効させた"
# standby が少しの間で終わる形: unseal.sh は active になるまで待ってから OK で戻る
BAO_TOKEN="$ROOT0" B operator seal >/dev/null
rm -f "$W/standby.count"
BAO_STANDBY_STATUS=2 run unseal.sh --init-file "$W/init.age" --identity "$W/id.txt" >/dev/null || ng "standby が終われば、unseal.sh は通る"
[ "$(cat "$W/standby.count")" -eq 2 ] || ng "陽性対照: unseal.sh が standby の間を待っていない"
[[ "$(B status -format=json || true)" == *'"is_self": true'* ]] || ng "unseal.sh が、書き込みを受ける状態（active）になる前に戻った"
alive || ng "封印の後に root が生きていない（封印中に失効させた）"
ok "configure --revoke-only: 封印中と、開けた直後の standby の間は、失効したとも生きているとも言わずに 3 で止まり（unseal.sh も active になるまで OK と言わない）、root は生きたまま"
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

# IADR-0710 決定 3: オーナー用の secret_id は期限つき（lib.sh の OB_OPERATOR_SECRET_ID_TTL_HOURS）。期限の記録は作り始めの時刻＋期限（実際より早いか同じ）。
TTL_H="$(sed -n 's/^OB_OPERATOR_SECRET_ID_TTL_HOURS=\([0-9][0-9]*\)$/\1/p' "$HERE/lib.sh")"
[ -n "$TTL_H" ] && [ "$TTL_H" -gt 0 ] || ng "lib.sh の OB_OPERATOR_SECRET_ID_TTL_HOURS を読めない"
NA="$(cat "$CM_DIR/operator-not-after" 2>/dev/null || true)"
NOW_S="$(date +%s)"
[[ "$NA" =~ ^[0-9]+$ ]] && [ "$NA" -le $((NOW_S + TTL_H * 3600)) ] && [ "$NA" -gt $((NOW_S + TTL_H * 3600 - 3600)) ] \
  || ng "configure が secret_id の期限（作り始め＋${TTL_H} 時間）を ConfigMap platform-infra/openbao-operator-credential に置かない（$NA）"
ok "configure: オーナー用の secret_id の期限（${TTL_H} 時間）を ConfigMap に記録する（値ではない）"

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
for p in platform/postgres platform/postgres-backup platform/keycloak-db platform/keycloak-admin platform/rabbitmq shared/ast-db; do
  get "$p" | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>{const d=JSON.parse(s);if(Object.keys(d).sort().join()!=="password,username"||!/^[a-z]+_[0-9a-f]{6}$/.test(d.username)||!/^[0-9a-f]{48}$/.test(d.password))process.exit(1);})' \
    || ng "cloud/$p の形が違う"
done
# IADR-0695: 取引ユニットの DB のサービスごとの 7 組（利用者名は ast_<サービス>_<乱数>。サービスの - は _）
AST_DB_SVCS="audit configuration cost-control market-monitor order-execution report risk-management"
for s in $AST_DB_SVCS; do
  get "shared/ast-db-$s" | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>{const d=JSON.parse(s);if(Object.keys(d).sort().join()!=="password,username"||d.username!==`ast_${process.argv[1]}_${d.username.slice(-6)}`||!/_[0-9a-f]{6}$/.test(d.username)||!/^[0-9a-f]{48}$/.test(d.password))process.exit(1);})' "${s//-/_}" \
    || ng "cloud/shared/ast-db-$s の形が違う"
done
[ "$(for s in $AST_DB_SVCS; do get "shared/ast-db-$s" | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>{const d=JSON.parse(s);console.log(d.username);console.log(d.password)})'; done | sort -u | wc -l)" -eq 14 ] \
  || ng "サービスごとの DB の利用者名・パスワードが互いに違わない"
users="$(grep -v '^[[:space:]]*#' "$HERE/../../local/rabbitmq-setup/users.txt" | awk 'NF >= 2 && $2 == "ai-stock-trading" { print $1 }' | sort | tr '\n' ',')"
[ "$(get shared/ast-rabbitmq-users | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>process.stdout.write(Object.keys(JSON.parse(s)).sort().join(",")+","))')" = "$users" ] || ng "ブローカの利用者の集合が users.txt の取引ユニットの行と違う"
CLIENT_KEYS="$(node -e 'process.stdout.write([...require(process.argv[1]).CLOUD_CLIENTS].sort().join(","))' "$HERE/../realm-for-cloud.js")"
[ "$(printf '%s' "$CLIENT_KEYS" | tr ',' '\n' | grep -c .)" -eq 10 ] || ng "CLOUD_CLIENTS が 10 でない（宣言の 3 つとサービスごとの 7 つ。IADR-0700）"
[ "$(get shared/ast-client-secrets | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>process.stdout.write(Object.keys(JSON.parse(s)).sort().join(",")))')" = "$CLIENT_KEYS" ] || ng "client の secret のキーが CLOUD_CLIENTS の 10 の clientId でない"
get shared/ast-client-secrets | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>{const v=Object.values(JSON.parse(s));if(!v.every((x)=>/^[0-9a-f]{48}$/.test(x))||new Set(v).size!==v.length)process.exit(1);})' \
  || ng "client の secret が 24 バイトの乱数でないか、互いに同じ"
before="$(for p in platform/postgres shared/ast-db shared/ast-db-audit shared/ast-db-risk-management shared/ast-rabbitmq-users shared/ast-client-secrets; do get "$p"; done)"
run seed-secrets.sh --operator-file "$W/operator.age" --identity "$W/id.txt" >/dev/null
after="$(for p in platform/postgres shared/ast-db shared/ast-db-audit shared/ast-db-risk-management shared/ast-rabbitmq-users shared/ast-client-secrets; do get "$p"; done)"
[ "$before" = "$after" ] || ng "2 回目の seed が値を変えた"
ok "seed: 8 種とサービスごとの DB の 7 組を作り（利用者名は乱数の接尾辞・パスワードは 24 バイト・ブローカは users.txt の 11 人・client は 10）、2 回目は何も変えない"

# IADR-0700: 在るパス（稼働中の構成。旧の 3 つの client だけ）には、無いキーだけを足す。在るキーの値は変えない。
old3="$(get shared/ast-client-secrets | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>{const d=JSON.parse(s);const o={};for(const k of ["ai-stock-trading-svc","ai-stock-trading-llm-caller","ai-stock-trading-owner"])o[k]=d[k];process.stdout.write(JSON.stringify(o));})')"
printf '%s' "$old3" | BAO_TOKEN="$OPT" B kv put -mount=cloud shared/ast-client-secrets - >/dev/null
[ "$(get shared/ast-client-secrets | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>process.stdout.write(String(Object.keys(JSON.parse(s)).length)))')" = 3 ] || ng "旧の 3 つの形に戻せない（試験の前提）"
out="$(run seed-secrets.sh --operator-file "$W/operator.age" --identity "$W/id.txt")"
[[ "$out" == *"足した: cloud/shared/ast-client-secrets"* ]] || ng "在るパスに無いキーを足さない: $out"
[ "$(get shared/ast-client-secrets | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>process.stdout.write(Object.keys(JSON.parse(s)).sort().join(",")))')" = "$CLIENT_KEYS" ] || ng "足した後のキーが 10 の clientId でない"
get shared/ast-client-secrets | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>{const d=JSON.parse(s);const o=JSON.parse(process.argv[1]);for(const k of Object.keys(o))if(d[k]!==o[k])process.exit(1);})' "$old3" \
  || ng "無いキーを足すときに、在るキーの値を変えた"
before2="$(get shared/ast-client-secrets)"
out="$(run seed-secrets.sh --operator-file "$W/operator.age" --identity "$W/id.txt")"
[ "$(get shared/ast-client-secrets)" = "$before2" ] && [[ "$out" == *"在る: cloud/shared/ast-client-secrets（触らない）"* ]] || ng "全部在るのに書いた"
unset old3 before2
ok "seed: 在るパスには無いキーだけを足し（旧の 3 つの client の値は変えない）、全部在れば触らない"

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

# --- rotate（IADR-0706）: 在るキーだけを作り直し、ほかのキー（利用者名・ほかのクライアント）は触らない。値は表示しない ---
for target in "platform/postgres password" "platform/postgres-backup password" "platform/keycloak-db password" "shared/ast-client-secrets ai-stock-trading-svc-report"; do
  set -- $target
  before_all="$(get "$1")"
  old="$(BAO_TOKEN="$OPT" B kv get -mount=cloud -field="$2" "$1")"
  out="$(run seed-secrets.sh --operator-file "$W/operator.age" --identity "$W/id.txt" rotate "cloud/$1" "$2")"
  new="$(BAO_TOKEN="$OPT" B kv get -mount=cloud -field="$2" "$1")"
  [[ "$new" =~ ^[0-9a-f]{48}$ ]] && [ "$new" != "$old" ] || ng "rotate が cloud/$1 の $2 を 24 バイトの乱数で作り直さない"
  [[ "$out" != *"$new"* && "$out" != *"$old"* ]] || ng "rotate が値を出力した"
  printf '%s' "$before_all" | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>{const b=JSON.parse(s),a=JSON.parse(process.argv[1]),k=process.argv[2];if(Object.keys(a).sort().join()!==Object.keys(b).sort().join())process.exit(1);for(const x of Object.keys(b))if(x!==k&&a[x]!==b[x])process.exit(1);})' "$(get "$1")" "$2" \
    || ng "rotate が cloud/$1 のほかのキーを変えた・消した"
done
set +e; run seed-secrets.sh --operator-file "$W/operator.age" --identity "$W/id.txt" rotate cloud/shared/ast-client-secrets no-such-client >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -eq 5 ] || ng "rotate が無いキーを作った（または 5 で止まらない: $rc）"
[ "$(BAO_TOKEN="$OPT" B kv get -mount=cloud -format=json shared/ast-client-secrets | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>process.stdout.write(String("no-such-client" in JSON.parse(s).data.data)))')" = false ] || ng "rotate が無いキーを足した"
unset old new before_all
set --
ok "rotate: seed が作った値の在るキーだけを 24 バイトの乱数で作り直し（利用者名・ほかのキーは触らない）、無いキーは作らずに止める。値は表示しない"

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
# IADR-0695: サービスごとの DB の利用者は、基盤の側（Postgres の利用者を作る Job）と取引ユニットの側の両方が名指しで読める。MSP の側は読めない。
for s in $AST_DB_SVCS; do
  [ "$(cap "$TA" "cloud/data/shared/ast-db-$s")" = read ] && [ "$(cap "$TP" "cloud/data/shared/ast-db-$s")" = read ] || ng "cloud/shared/ast-db-$s を名指しで読めない"
  [ "$(cap "$TM" "cloud/data/shared/ast-db-$s")" = deny ] || ng "MSP の側が cloud/shared/ast-db-$s を読める"
done
[ "$(cap "$TA" cloud/data/shared/ast-db-other)" = deny ] && [ "$(cap "$TP" cloud/data/shared/ast-db-other)" = deny ] || ng "名指しの外の ast-db-* を読める（ワイルドカード）"
[ "$(cap "$TM" pki_cloud/issue/llmgateway-service)" = update ] && [ "$(cap "$TM" pki_cloud/issue/postgres)" = deny ] || ng "MSP の側の発行の範囲が違う"
# IADR-0693: 認可サーバーの証明書は基盤の側だけが発行する。MSP・取引ユニットの側は発行できない（取引ユニットは CA の証明書を KV で読むだけ）。
[ "$(cap "$TP" pki_cloud/issue/keycloak)" = update ] || ng "基盤の側が keycloak の証明書を発行できない"
[ "$(cap "$TM" pki_cloud/issue/keycloak)" = deny ] && [ "$(cap "$TA" pki_cloud/issue/keycloak)" = deny ] || ng "基盤の外の側が keycloak の証明書を発行できる"
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
TKC="$(BAO_TOKEN="$TP" B write -format=json pki_cloud/issue/keycloak common_name=keycloak alt_names=keycloak.platform-infra,keycloak.platform-infra.svc,keycloak.platform-infra.svc.cluster.local ttl=8760h)"
[[ "$TKC" == *"PRIVATE KEY"* ]] || ng "keycloak の証明書を発行できない（IADR-0693）"
printf '%s' "$TKC" | node -e 'let s="";process.stdin.on("data",c=>s+=c).on("end",()=>process.stdout.write(JSON.parse(s).data.certificate))' > "$W/kc.crt"
BAO_TOKEN="$R1" B read -field=certificate pki_cloud/cert/ca > "$W/ca.crt"
openssl verify -CAfile "$W/ca.crt" "$W/kc.crt" >/dev/null || ng "keycloak の証明書が CA（ConfigMap platform-infra-ca に置くもの）で検証できない"
[[ "$(openssl x509 -in "$W/kc.crt" -noout -ext subjectAltName)" == *"DNS:keycloak,"* ]] || ng "keycloak の証明書の SAN に短い名前 keycloak が無い（受け手の URL の名前）"
rm -f "$W/kc.crt"; unset TKC
set +e; BAO_TOKEN="$TP" B write pki_cloud/issue/keycloak common_name=postgres >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -ne 0 ] || ng "keycloak の role が別の名前（postgres）を発行した"
ok "PKI: role は名指しの名前だけを発行し、CA の鍵は OpenBao の外へ出ない（internal）。keycloak の証明書は CA で検証できる"
BAO_TOKEN="$R1" B token revoke -self >/dev/null

# --- root-token.sh: 作り直して操作し、失効させる ---
run root-token.sh --init-file "$W/init.age" --operator-file "$W/operator.age" --identity "$W/id.txt" apply-policies >/dev/null
run root-token.sh --init-file "$W/init.age" --operator-file "$W/operator.age" --identity "$W/id.txt" rotate-operator --recipients "$W/recipients.txt" --operator-out "$W/operator2.age" >/dev/null
set +e; printf '{"role_id":"%s","secret_id":"%s"}' "$(dec "$W/operator.age" role_id)" "$(dec "$W/operator.age" secret_id)" | B write -field=token auth/approle/login - >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -ne 0 ] || ng "作り直した後も古い secret_id で入れる"
cp "$W/operator2.age" "$W/operator.age"; OPT="$(login)"; [ -n "$OPT" ] || ng "新しい資格で入れない"
ok "root-token: 作り直した root で policy を当て直し、オーナーの secret_id を回す（古いものは入れない）"
# IADR-0710 決定 3: 稼働中の構成の期限なしの secret_id（従前の role の設定）も、rotate-operator が期限つきへ替える。
R4="$(mkroot)"
BAO_TOKEN="$R4" B write auth/approle/role/cloud-operator secret_id_ttl=0 >/dev/null
SA="$(BAO_TOKEN="$R4" B write -f -field=secret_id_accessor auth/approle/role/cloud-operator/secret-id)"
[ "$(printf '{"secret_id_accessor":"%s"}' "$SA" | BAO_TOKEN="$R4" B write -field=secret_id_ttl auth/approle/role/cloud-operator/secret-id-accessor/lookup -)" = 0 ] \
  || ng "陽性対照: 期限なしの secret_id を作れていない"
BAO_TOKEN="$R4" B token revoke -self >/dev/null
rm -f "$CM_DIR/operator-not-after"
run root-token.sh --init-file "$W/init.age" --operator-file "$W/operator.age" --identity "$W/id.txt" rotate-operator --recipients "$W/recipients.txt" --operator-out "$W/operator3.age" >/dev/null
cp "$W/operator3.age" "$W/operator.age"; OPT="$(login)"; [ -n "$OPT" ] || ng "期限つきへ回した資格で入れない"
R5="$(mkroot)"
ids="$(BAO_TOKEN="$R5" B list -format=json auth/approle/role/cloud-operator/secret-id | tr -d '[]", ' | grep -E '^[0-9a-f-]{36}$' || true)"
[ "$(printf '%s\n' "$ids" | grep -c .)" = 1 ] || ng "rotate-operator の後の secret_id が 1 つでない（期限なしのものが残った）"
[ "$(printf '{"secret_id_accessor":"%s"}' "$ids" | BAO_TOKEN="$R5" B write -field=secret_id_ttl auth/approle/role/cloud-operator/secret-id-accessor/lookup -)" = $((TTL_H * 3600)) ] \
  || ng "rotate-operator の後の secret_id の期限が ${TTL_H} 時間でない"
[ "$(BAO_TOKEN="$R5" B read -field=secret_id_ttl auth/approle/role/cloud-operator)" = $((TTL_H * 3600)) ] || ng "rotate-operator が role の secret_id_ttl を書き直さない"
BAO_TOKEN="$R5" B token revoke -self >/dev/null
OPT="$(login)"
[[ "$(cat "$CM_DIR/operator-not-after" 2>/dev/null || true)" =~ ^[0-9]+$ ]] || ng "rotate-operator が期限を ConfigMap に記録しない"
ok "root-token rotate-operator: 期限なしの secret_id を消し、期限つき（${TTL_H} 時間）の 1 つへ替え、期限を記録する"
# IADR-0693: 稼働中の構成（configure の後・root は失効済み）へ role を足す口。keycloak の role が無い状態から作り直す。
#   基盤の側のトークンは親の root の失効で消えないよう orphan で作る（上の TP は R1 の失効と一緒に消えている）。
R3="$(mkroot)"; BAO_TOKEN="$R3" B delete pki_cloud/roles/keycloak >/dev/null
TPX="$(BAO_TOKEN="$R3" B token create -orphan -ttl=10m -policy=platform-infra-eso -no-default-policy -field=token)"
BAO_TOKEN="$R3" B token revoke -self >/dev/null
set +e; BAO_TOKEN="$TPX" B write pki_cloud/issue/keycloak common_name=keycloak >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -ne 0 ] || ng "陽性対照: role を消したのに keycloak の証明書を発行できる"
run root-token.sh --init-file "$W/init.age" --operator-file "$W/operator.age" --identity "$W/id.txt" apply-pki-roles >"$W/rt.log" 2>&1 || { cat "$W/rt.log" >&2; ng "apply-pki-roles が失敗した"; }
grep -q '書いた: PKI の role keycloak' "$W/rt.log" || ng "apply-pki-roles が keycloak の role を書いたと言わない"
grep -q '作り直した root を失効させた' "$W/rt.log" || ng "apply-pki-roles の後に root を失効させない"
BAO_TOKEN="$TPX" B write pki_cloud/issue/keycloak common_name=keycloak >/dev/null 2>&1 || ng "apply-pki-roles の後に keycloak の証明書を発行できない"
unset TPX   # 10 分で切れる（default の policy を持たないので自分では失効できない）
ok "root-token apply-pki-roles: 稼働中の構成へ lib.sh の role の一覧を書き、root を失効させる（IADR-0693）"

# --- 受け口の TLS（IADR-0696）: 平文は受けず、手順は CA で検証して話し、作り直しは届かない時間を作らない ---
pem_count() { grep -c 'BEGIN CERTIFICATE' "$1"; }
set +e
BAO_ADDR="http://127.0.0.1:${PORT}" "$BAO" status >/dev/null 2>&1; rc=$?
set -e
[ "$rc" -eq 1 ] || ng "受け口が平文の要求に応えた（rc=$rc）"
set +e
OB_POD_ADDR="http://127.0.0.1:${PORT}" run unseal.sh --init-file "$W/init.age" --identity "$W/id.txt" >/dev/null 2>&1; rc=$?
set -e
[ "$rc" -eq 2 ] || ng "手順が平文の宛先（OB_POD_ADDR=http://）を受けた（rc=$rc）"
mkdir -p "$W/badtls"
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -days 1 -subj /CN=other-ca -keyout /dev/null -out "$W/badtls/ca.crt" >/dev/null 2>&1
set +e
OB_POD_TLS_DIR="$W/badtls" run unseal.sh --init-file "$W/init.age" --identity "$W/id.txt" >/dev/null 2>&1; rc=$?
set -e
[ "$rc" -ne 0 ] || ng "手順が別の CA でも受け口へ届いた（検証していない。Pod の環境の BAO_SKIP_VERIFY を消していない）"
for ns in platform-infra microservices-platform ai-stock-trading; do
  cmp -s "$CM_DIR/$ns-openbao-ca.crt" "$W/tls/ca.crt" || ng "issue: ConfigMap ${ns}/openbao-ca が受け口の CA と同じでない"
done
set +e; run tls-cert.sh issue >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -ne 0 ] || ng "2 回目の issue が止まらない（作り直しは renew）"
ok "TLS: 受け口は平文の要求に応えず、手順は https の宛先だけを受け、受け口の CA で検証する（別の CA では届かない）。CA は 3 つの名前空間の ConfigMap に在る"

old_ca="$(cat "$W/tls/ca.crt")" old_crt="$(cat "$W/tls/tls.crt")"
# 途中で落ちる経路: 受け口に読み直させる段で落ちても、CA は併記のまま（古い証明書のまま ESO は届く）。流し直せば終わる。
rm -f "$W/fail.done"
set +e; KUBECTL_FAIL_ON="kill -HUP" run tls-cert.sh renew >"$W/renew.log" 2>&1; rc=$?; set -e
[ -e "$W/fail.done" ] || ng "陽性対照: renew の SIGHUP の段に失敗を差し込めていない"
[ "$rc" -ne 0 ] || ng "renew が途中で落ちたのに成功を返した"
for ns in platform-infra microservices-platform ai-stock-trading; do
  [ "$(pem_count "$CM_DIR/$ns-openbao-ca.crt")" = 2 ] || ng "renew の途中: ConfigMap ${ns}/openbao-ca が古い CA と新しい CA の併記でない"
done
B status >/dev/null 2>&1 || [ $? -eq 2 ] || ng "renew の途中: 併記の CA で受け口へ届かない"
printf '%s\n' "$old_ca" > "$W/old-ca.crt"
BAO_ADDR="$TEST_BAO_ADDR" BAO_CACERT="$W/old-ca.crt" "$BAO" status >/dev/null 2>&1 || ng "renew の途中: 受け口が古い証明書を出していない（読み直させる前に替わった）"
# 途中で落ちる経路 2: 読み直させても新しい CA だけでは届かない（読み直しが効かない形。SIGHUP の宛先を bao でない使い捨ての
# プロセスにする）→ 古い CA を外さず、束のまま 1 で止まる（受け口は古い証明書のまま ESO は届く）。手順書 §4 の「途中で落ちたら」。
sleep 300 & decoy=$!
set +e; OB_POD_SERVER_PID="$decoy" run tls-cert.sh renew >"$W/renew2.log" 2>&1; rc=$?; set -e
kill "$decoy" 2>/dev/null || true
[ "$rc" -ne 0 ] || ng "renew が新しい CA で届かないのに成功を返した"
grep -q '新しい CA だけでは受け口へ届かない' "$W/renew2.log" || ng "陽性対照: renew の確かめの段（新しい CA だけで届くか）で止まっていない"
for ns in platform-infra microservices-platform ai-stock-trading; do
  [ "$(pem_count "$CM_DIR/$ns-openbao-ca.crt")" -ge 2 ] || ng "renew の確かめで落ちた後: ConfigMap ${ns}/openbao-ca から古い CA を外した"
done
[ "$(pem_count "$W/tls/ca.crt")" -ge 2 ] || ng "renew の確かめで落ちた後: Secret の ca.crt から古い CA を外した"
BAO_ADDR="$TEST_BAO_ADDR" BAO_CACERT="$W/old-ca.crt" "$BAO" status >/dev/null 2>&1 || [ $? -eq 2 ] || ng "renew の確かめで落ちた後: 古い CA で受け口へ届かない"
run tls-cert.sh renew >/dev/null
[ "$(pem_count "$W/tls/ca.crt")" = 1 ] && [ "$(cat "$W/tls/ca.crt")" != "$old_ca" ] && [ "$(cat "$W/tls/tls.crt")" != "$old_crt" ] || ng "renew の後: Secret が新しい CA の 1 つと新しい証明書になっていない"
for ns in platform-infra microservices-platform ai-stock-trading; do
  cmp -s "$CM_DIR/$ns-openbao-ca.crt" "$W/tls/ca.crt" || ng "renew の後: ConfigMap ${ns}/openbao-ca が新しい CA だけでない"
done
set +e; BAO_ADDR="$TEST_BAO_ADDR" BAO_CACERT="$W/old-ca.crt" "$BAO" status >/dev/null 2>&1; rc=$?; set -e
[ "$rc" -eq 1 ] || ng "renew の後: 受け口がまだ古い CA の証明書を出す（SIGHUP で読み直していない）"
st="$(B status -format=json || true)"
[[ "$st" == *'"sealed": false'* ]] || ng "renew の後: 封印された（読み直しは封印しない）"
run seed-secrets.sh --operator-file "$W/operator.age" --identity "$W/id.txt" >/dev/null || ng "renew の後: 手順が新しい CA で受け口へ届かない"
run tls-cert.sh publish-ca --namespace ai-stock-trading >/dev/null
cmp -s "$CM_DIR/ai-stock-trading-openbao-ca.crt" "$W/tls/ca.crt" || ng "publish-ca が CA を写さない"
ok "TLS renew: 古い CA と新しい CA を併記してから受け口を読み直させ（封印しない）、新しい CA で届くことを確かめて古い CA を外す。読み直しの段・確かめの段のどちらで落ちても併記のまま流し直せる"

key_line="$(sed -n 2p "$W/tls/tls.key")"
[ -n "$key_line" ] || ng "陽性対照: 受け口の鍵を読めない"
grep -qF -- "$key_line" "$KLOG" && ng "受け口の鍵が kubectl の引数に載った"
if grep -rqF -- "$key_line" "$POD_HOME" "$CM_DIR" 2>/dev/null; then ng "受け口の鍵が手元の作業場・ConfigMap に残った"; fi
ok "受け口の鍵は kubectl の引数にも、手元の作業場・ConfigMap にも出ない（標準入力で Secret へだけ渡る）"

# --- 値が引数・ディスクに残らない ---
R2="$(mkroot)"
vals="$(for p in platform/postgres platform/postgres-backup platform/keycloak-db platform/keycloak-admin platform/rabbitmq shared/ast-db $(for s in $AST_DB_SVCS; do printf 'shared/ast-db-%s ' "$s"; done) shared/ast-rabbitmq-users shared/ast-client-secrets ast/ast-secrets msp/llm-provider-credentials ast/moomoo-rsa ast/ast-owner-totp; do
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
