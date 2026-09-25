#!/usr/bin/env bash
# #1422: k8s-local-down.sh の撤去順・dry-run の読み取り専用性・最後の検証を、kubectl / helm / k3d /
# nerdctl を PATH 上のスタブへ差し替えて固定する。実クラスタは要らない。
#
# スタブは全呼び出しを $STUB_LOG へ記録し、$STATE 配下のファイルを「クラスタ」として読み書きする:
#   namespaces        … 名前空間（1 行 1 つ）
#   releases          … Helm リリース「名前空間 名前」
#   crds              … CRD 名
#   pvs               … 「名前<TAB>phase<TAB>claimRef の名前空間」
#   pvcs              … 「名前空間<TAB>名前」
#   vwh / mwh         … validating / mutating webhook 設定「名前<TAB>呼び先 Service の名前空間」
#   fin/<resource>    … 「名前空間|名前|finalizers」（cluster スコープは名前空間が空）
#   api-resources     … namespaced で list できる資源名
#   traefik-values    … HelmChartConfig traefik の valuesContent（無ければ HelmChartConfig が無い）
#   deleting/<ns>     … delete namespace を受けた印。finalizer 付きの物が中に残る間は消えない
#   k3d-cluster       … k3d のクラスタが在る印
# ESO のコントローラ（リリース external-secrets）が居る間は externalsecrets の finalizer を空にしても
# 付け直す（2026-09-14 実測の挙動）。
#
#   bash scripts/k8s-local-down.test.sh
set -u

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SCRIPT="$ROOT/scripts/k8s-local-down.sh"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
STATE="$WORK/state"
STUB_LOG="$WORK/calls.log"
export STATE STUB_LOG
PASSED=0; FAILED=0
ok() { PASSED=$((PASSED + 1)); printf '  ok    %s\n' "$1"; }
ng() { FAILED=$((FAILED + 1)); printf '  NG    %s\n        %s\n' "$1" "$2"; }
assert_eq() { [ "$2" = "$3" ] && ok "$1" || ng "$1" "expected: $3 / actual: $2"; }
assert_contains() { case "$2" in *"$3"*) ok "$1" ;; *) ng "$1" "expected to contain: $3" ;; esac; }
assert_missing() { case "$2" in *"$3"*) ng "$1" "expected NOT to contain: $3" ;; *) ok "$1" ;; esac; }
# 行番号（最初に現れた行）。無ければ 0。
line_of() { grep -nF -- "$2" <<<"$1" | head -n 1 | cut -d: -f1 | sed 's/^$/0/'; }
line_of_last() { grep -nF -- "$2" <<<"$1" | tail -n 1 | cut -d: -f1; }
assert_before() {
  local a b
  a="$(line_of "$2" "$3")"; b="$(line_of "$2" "$4")"
  if [ "${a:-0}" -gt 0 ] && [ "${b:-0}" -gt 0 ] && [ "$a" -lt "$b" ]; then ok "$1"; else ng "$1" "'$3'(line ${a:-0}) が '$4'(line ${b:-0}) より前にない"; fi
}

mkdir -p "$WORK/bin"
cat > "$WORK/bin/kubectl" <<'STUB'
#!/usr/bin/env bash
echo "kubectl $*" >> "$STUB_LOG"
S="$STATE"
# 到達不能なクラスタ（apiserver が応答しない）を模す。
[ -f "$S/unreachable" ] && { echo "The connection to the server localhost:8080 was refused" >&2; exit 1; }
ns_arg=""; all=0; args=()
while [ $# -gt 0 ]; do
  case "$1" in
    -n) ns_arg="$2"; shift 2 ;;
    -A) all=1; shift ;;
    -o|--type|-p|-f|--verbs|--timeout) args+=("$1" "$2"); shift 2 ;;
    *) args+=("$1"); shift ;;
  esac
done
set -- "${args[@]}"
# 名前空間の削除を進める: 印があり、中に finalizer 付きの物が無ければ消す（PVC も消え、PV は Released）。
settle() {
  for mark in "$S"/deleting/*; do
    [ -e "$mark" ] || continue
    local ns; ns="$(basename "$mark")"
    if ! cat "$S"/fin/* 2>/dev/null | awk -F '|' -v ns="$ns" '$1 == ns && $3 != "" && $3 != "[]" { found = 1 } END { exit !found }'; then
      grep -vx "$ns" "$S/namespaces" > "$S/tmp" || true; mv "$S/tmp" "$S/namespaces"
      awk -F '\t' -v ns="$ns" '$1 != ns' "$S/pvcs" > "$S/tmp"; mv "$S/tmp" "$S/pvcs"
      awk -F '\t' -v OFS='\t' -v ns="$ns" '$3 == ns { $2 = "Released" } { print }' "$S/pvs" > "$S/tmp"; mv "$S/tmp" "$S/pvs"
      for f in "$S"/fin/*; do [ -e "$f" ] || continue; awk -F '|' -v ns="$ns" '$1 != ns' "$f" > "$S/tmp"; mv "$S/tmp" "$f"; done
      rm -f "$mark"
    fi
  done
}
remove_line() { grep -vx -- "$2" "$1" > "$S/tmp" || true; mv "$S/tmp" "$1"; }
remove_first_field() { awk -F '\t' -v n="$2" '$1 != n' "$1" > "$S/tmp"; mv "$S/tmp" "$1"; }
case "$1" in
  config) echo "stub-context"; exit 0 ;;
  api-resources) cat "$S/api-resources"; exit 0 ;;
  get)
    settle
    case "$2" in
      namespaces) cat "$S/namespaces" ;;
      crd) cat "$S/crds" ;;
      pv) cat "$S/pvs" ;;
      pvc) awk -F '\t' -v ns="$ns_arg" '$1 == ns { print "  pvc " $1 "/" $2 }' "$S/pvcs" ;;
      validatingwebhookconfigurations) cat "$S/vwh" ;;
      mutatingwebhookconfigurations) cat "$S/mwh" ;;
      helmchartconfig) [ -f "$S/traefik-values" ] || exit 1; cat "$S/traefik-values" ;;
      *)
        f="$S/fin/$2"
        [ -f "$f" ] || { echo "error: the server doesn't have a resource type \"$2\"" >&2; exit 1; }
        if [ "$all" = 1 ]; then cat "$f"; else awk -F '|' -v ns="$ns_arg" '$1 == ns' "$f"; fi ;;
    esac
    exit 0 ;;
  delete)
    case "$2" in
      namespace) shift 2; for a in "$@"; do case "$a" in --*) ;; *) mkdir -p "$S/deleting"; : > "$S/deleting/$a" ;; esac; done; settle ;;
      crd) remove_line "$S/crds" "$3" ;;
      pv) remove_first_field "$S/pvs" "$3" ;;
      validatingwebhookconfigurations) remove_first_field "$S/vwh" "$3" ;;
      mutatingwebhookconfigurations) remove_first_field "$S/mwh" "$3" ;;
    esac
    exit 0 ;;
  patch)
    f="$S/fin/$2"; [ -f "$f" ] || exit 1
    # ESO のコントローラが居る間は externalsecrets の finalizer を付け直す。
    if [ "$2" = "externalsecrets.external-secrets.io" ] && grep -qx "external-secrets external-secrets" "$S/releases"; then exit 0; fi
    awk -F '|' -v OFS='|' -v ns="$ns_arg" -v n="$3" '$1 == ns && $2 == n { $3 = "" } { print }' "$f" > "$S/tmp"; mv "$S/tmp" "$f"
    exit 0 ;;
  apply)
    printf 'ports:\n  admin:\n    port: 50000\n' > "$S/traefik-values"; exit 0 ;;
esac
exit 0
STUB
cat > "$WORK/bin/helm" <<'STUB'
#!/usr/bin/env bash
echo "helm $*" >> "$STUB_LOG"
[ -f "$STATE/unreachable" ] && { echo "Error: Kubernetes cluster unreachable" >&2; exit 1; }
case "$1" in
  list) awk '{ print $2 "\t" $1 "\t1\t2026-09-25\tdeployed\tchart-1.0.0\t1.0.0" }' "$STATE/releases" ;;
  uninstall) grep -vx "$4 $2" "$STATE/releases" > "$STATE/tmp" || true; mv "$STATE/tmp" "$STATE/releases" ;;
esac
exit 0
STUB
cat > "$WORK/bin/k3d" <<'STUB'
#!/usr/bin/env bash
echo "k3d $*" >> "$STUB_LOG"
[ "$1 $2" = "cluster list" ] && { [ -f "$STATE/k3d-cluster" ] && exit 0 || exit 1; }
exit 0
STUB
cat > "$WORK/bin/nerdctl" <<'STUB'
#!/usr/bin/env bash
echo "nerdctl $*" >> "$STUB_LOG"
exit 0
STUB
chmod +x "$WORK/bin/"*
export PATH="$WORK/bin:$PATH"
export NS_WAIT_SECONDS=0 NS_POLL_INTERVAL=0
# 🔴 スタブが外れても実クラスタへ届かないように、kubeconfig を存在しないパスへ向ける（--apply を走らせるため）。
export KUBECONFIG=/nonexistent
# スタブが本当に先に解決されることを、何かを走らせる前に確かめる（外れていれば 1 つも走らせない）。
for bin in kubectl helm k3d nerdctl; do
  if [ "$(command -v "$bin")" != "$WORK/bin/$bin" ]; then
    echo "ABORT: $bin がスタブ（$WORK/bin/$bin）ではなく $(command -v "$bin") に解決される。試験を走らせない。" >&2
    exit 2
  fi
done

T=$'\t'
# 2026-09-11 に実測した「汚れたクラスタ」の縮図。
dirty_cluster() {
  rm -rf "$STATE"; mkdir -p "$STATE/fin"
  : > "$STUB_LOG"
  printf '%s\n' default kube-system kube-public kube-node-lease ai-stock-trading microservices-platform \
    platform-infra reloader argocd external-secrets istio-system cert-manager > "$STATE/namespaces"
  printf '%s\n' "ai-stock-trading ast" "microservices-platform msp" "reloader reloader" \
    "external-secrets external-secrets" "istio-system istiod" "istio-system istio-base" \
    "kube-system traefik" "kube-system traefik-crd" > "$STATE/releases"
  printf '%s\n' externalsecrets.external-secrets.io secretstores.external-secrets.io \
    clustersecretstores.external-secrets.io applications.argoproj.io appprojects.argoproj.io \
    certificates.cert-manager.io virtualservices.networking.istio.io addons.k3s.cattle.io > "$STATE/crds"
  printf '%s\n' "pvc-opend${T}Bound${T}ai-stock-trading" "pvc-old${T}Released${T}microservices-platform" > "$STATE/pvs"
  printf '%s\n' "ai-stock-trading${T}opend-data" > "$STATE/pvcs"
  printf '%s\n' "externalsecret-validate${T}external-secrets" "istio-validator-istio-system${T}istio-system" \
    "cert-manager-webhook${T}cert-manager" "unrelated-webhook${T}other-system" > "$STATE/vwh"
  printf '%s\n' "istio-sidecar-injector${T}istio-system" > "$STATE/mwh"
  printf '%s\n' 'platform-infra|msp-secrets|["externalsecret-cleanup"]' \
    'ai-stock-trading|ast-secrets|["externalsecret-cleanup"]' > "$STATE/fin/externalsecrets.external-secrets.io"
  printf '%s\n' 'platform-infra|vault|' > "$STATE/fin/secretstores.external-secrets.io"
  printf '%s\n' '|vault-cluster|["finalizer.x"]' > "$STATE/fin/clustersecretstores.external-secrets.io"
  printf '%s\n' 'argocd|msp|["resources-finalizer.argocd.argoproj.io"]' > "$STATE/fin/applications.argoproj.io"
  : > "$STATE/fin/appprojects.argoproj.io"
  : > "$STATE/fin/certificates.cert-manager.io"
  : > "$STATE/fin/virtualservices.networking.istio.io"
  printf '%s\n' pods configmaps > "$STATE/api-resources"
  printf 'service:\n  enabled: false\n' > "$STATE/traefik-values"
}
snapshot() { (cd "$STATE" && find . -type f | sort | xargs cat) | cksum; }
run_down() { K8S_LOCAL_RUNTIME="${RUNTIME_OVERRIDE:-rancher}" bash "$SCRIPT" "$@" 2>&1; }
# スタブの記録のうち、読み取り以外の呼び出し（dry-run では 0 件でなければならない）。
non_read_calls() { grep -Ev '^(kubectl (get|api-resources|config current-context)( |$)|helm list( |$)|k3d cluster list( |$))' "$STUB_LOG"; }

# ---- T-1422-01: 既定（引数なし）は dry-run で、クラスタを一切変えない ----
dirty_cluster
BEFORE="$(snapshot)"
OUT="$(run_down)"; RC=$?
assert_eq 'T-1422-01 既定は dry-run: 正常終了する' "$RC" "0"
assert_contains 'T-1422-01 既定は dry-run: mode を表示する' "$OUT" 'mode: dry-run'
assert_eq 'T-1422-01 既定は dry-run: スタブのクラスタ状態が 1 バイトも変わらない' "$(snapshot)" "$BEFORE"

# ---- T-1422-02: dry-run が起動するのは読み取りだけ（呼び出しの全数を動詞で判定する） ----
dirty_cluster
run_down --dry-run >/dev/null
assert_eq 'T-1422-02 dry-run: 読み取り以外の呼び出しが 0 件' "$(non_read_calls)" ""
assert_contains 'T-1422-02 dry-run: 呼び出しは記録されている（0 件を緑にしない）' "$(cat "$STUB_LOG")" 'kubectl get namespaces'

# ---- T-1422-03: dry-run の計画が実測の順序どおりに並ぶ ----
dirty_cluster
OUT="$(run_down --dry-run)"
assert_before 'T-1422-03 順序: アプリの Helm → webhook' "$OUT" '(dry-run) helm uninstall ast' '(dry-run) kubectl delete validatingwebhookconfigurations'
assert_before 'T-1422-03 順序: webhook → finalizer の patch' "$OUT" '(dry-run) kubectl delete validatingwebhookconfigurations' '(dry-run) kubectl patch externalsecrets.external-secrets.io'
assert_before 'T-1422-03 順序: finalizer → 残りの Helm（external-secrets）' "$OUT" '(dry-run) kubectl patch applications.argoproj.io' '(dry-run) helm uninstall external-secrets'
assert_before 'T-1422-03 順序: 残りの Helm → 名前空間' "$OUT" '(dry-run) helm uninstall istio-base' '(dry-run) kubectl delete namespace'
assert_before 'T-1422-03 順序: 名前空間 → CRD' "$OUT" '(dry-run) kubectl delete namespace' '(dry-run) kubectl delete crd'
assert_before 'T-1422-03 順序: CRD → PV' "$OUT" '(dry-run) kubectl delete crd' '(dry-run) kubectl delete pv pvc-old'
assert_before 'T-1422-03 順序: PV → Traefik の Service を戻す' "$OUT" '(dry-run) kubectl delete pv' '(dry-run) kubectl apply -f deploy/local/edge/traefik-entrypoint.yaml'
assert_contains 'T-1422-03 OpenD の PVC が消えることを見せる' "$OUT" 'pvc ai-stock-trading/opend-data'
assert_contains 'T-1422-03 名前空間の削除後に解放される Bound の PV も見せる' "$OUT" 'kubectl delete pv pvc-opend --ignore-not-found   # 現在 Bound'

# ---- T-1422-04: 撤去する webhook の選び方（名前に部品名が無くても呼び先の名前空間で拾う） ----
assert_contains 'T-1422-04 webhook: ESO の externalsecret-validate を拾う（呼び先 external-secrets）' "$OUT" 'delete validatingwebhookconfigurations externalsecret-validate'
assert_contains 'T-1422-04 webhook: mutating も拾う' "$OUT" 'delete mutatingwebhookconfigurations istio-sidecar-injector'
assert_missing 'T-1422-04 webhook: 無関係なものは拾わない' "$OUT" 'unrelated-webhook'
assert_missing 'T-1422-04 CRD: アプリ以外（k3s 内蔵）は消さない' "$OUT" 'delete crd addons.k3s.cattle.io'
assert_missing 'T-1422-04 finalizer: 空の物は patch しない' "$OUT" 'patch secretstores.external-secrets.io vault'
assert_contains 'T-1422-04 finalizer: cluster スコープは -n なしで patch する' "$OUT" 'patch clustersecretstores.external-secrets.io vault-cluster --type=merge'
assert_missing 'T-1422-04 Helm: 内蔵の traefik は消さない' "$OUT" 'helm uninstall traefik'

# ---- T-1422-05: --apply は同じ順序で実際に呼ぶ ----
dirty_cluster
OUT="$(run_down --apply)"; RC=$?
LOG="$(cat "$STUB_LOG")"
assert_before 'T-1422-05 apply 順序: アプリの Helm → webhook' "$LOG" 'helm uninstall ast' 'kubectl delete validatingwebhookconfigurations'
assert_before 'T-1422-05 apply 順序: webhook をすべて消してから finalizer の patch' "$LOG" 'kubectl delete mutatingwebhookconfigurations' 'kubectl patch'
assert_before 'T-1422-05 apply 順序: ArgoCD の finalizer → 残りの Helm' "$LOG" 'kubectl patch applications.argoproj.io msp -n argocd' 'helm uninstall external-secrets'
assert_before 'T-1422-05 apply 順序: 名前空間 → CRD' "$LOG" 'kubectl delete namespace' 'kubectl delete crd'
EXT_UNINSTALL="$(line_of "$LOG" 'helm uninstall external-secrets')"
LAST_ES_PATCH="$(line_of_last "$LOG" 'kubectl patch externalsecrets.external-secrets.io')"
if [ "${LAST_ES_PATCH:-0}" -gt "${EXT_UNINSTALL:-0}" ]; then ok 'T-1422-05 apply: ESO の撤去後に externalsecrets の finalizer をもう一度空にする'; else ng 'T-1422-05 apply: ESO の撤去後に externalsecrets の finalizer をもう一度空にする' "uninstall=$EXT_UNINSTALL last-patch=$LAST_ES_PATCH"; fi

# ---- T-1422-06: 撤去し切れば検証が緑（exit 0） ----
assert_eq 'T-1422-06 apply: 撤去し切れば exit 0' "$RC" "0"
assert_contains 'T-1422-06 apply: OK を出す' "$OUT" 'OK: 名前空間は default / kube-* だけ'
assert_eq 'T-1422-06 apply: 名前空間は default / kube-* だけ' "$(grep -Evc '^(default|kube-.*)$' "$STATE/namespaces")" "0"
assert_eq 'T-1422-06 apply: PV 0' "$(grep -c . "$STATE/pvs")" "0"
assert_eq 'T-1422-06 apply: 残る CRD は k3s 内蔵だけ' "$(cat "$STATE/crds")" "addons.k3s.cattle.io"
assert_eq 'T-1422-06 apply: Helm は kube-system の traefik だけ' "$(tr '\n' ',' < "$STATE/releases")" "kube-system traefik,kube-system traefik-crd,"
assert_contains 'T-1422-06 apply: Traefik の Service を戻す' "$LOG" 'kubectl apply -f deploy/local/edge/traefik-entrypoint.yaml'

# ---- T-1422-07: 1 つでも残れば赤（exit 1） ----
dirty_cluster
echo "leftover-ns" >> "$STATE/namespaces"
echo "default someone-else" >> "$STATE/releases"
OUT="$(run_down --apply)"; RC=$?
assert_eq 'T-1422-07 apply: 残りがあれば exit 1' "$RC" "1"
assert_contains 'T-1422-07 apply: 残った名前空間を名指しする' "$OUT" '残: namespace leftover-ns'
assert_contains 'T-1422-07 apply: 残った Helm リリースを名指しする' "$OUT" '残: helm default/someone-else'
assert_missing 'T-1422-07 apply: 対象外の名前空間は消さない' "$(cat "$STUB_LOG")" 'leftover-ns'

# ---- T-1422-08: 待っても消えない名前空間は、中の finalizer 付きの物を種類を問わず空にする ----
dirty_cluster
echo "widgets.example.com" >> "$STATE/api-resources"
printf '%s\n' 'argocd|w1|["example.com/cleanup"]' > "$STATE/fin/widgets.example.com"
OUT="$(run_down --apply)"; RC=$?
assert_contains 'T-1422-08 apply: 残った名前空間を名指しする' "$OUT" '名前空間 argocd が 0 秒で消えない'
assert_contains 'T-1422-08 apply: 中の finalizer 付きの物を patch する' "$(cat "$STUB_LOG")" 'kubectl patch widgets.example.com w1 -n argocd'
assert_eq 'T-1422-08 apply: 救済の後は緑' "$RC" "0"

# ---- T-1422-09: k3d 経路もクラスタ削除は --apply のときだけ ----
dirty_cluster
: > "$STATE/k3d-cluster"
OUT="$(RUNTIME_OVERRIDE=k3d run_down)"; RC=$?
assert_eq 'T-1422-09 k3d dry-run: 正常終了する' "$RC" "0"
assert_contains 'T-1422-09 k3d dry-run: 削除を表示する' "$OUT" '(dry-run) k3d cluster delete msp-ast-dev'
assert_missing 'T-1422-09 k3d dry-run: 削除を起動しない' "$(cat "$STUB_LOG")" 'k3d cluster delete'
assert_eq 'T-1422-09 k3d dry-run: 読み取り以外の呼び出しが 0 件（k3d の分岐でも同じ判定）' "$(non_read_calls)" ""
assert_contains 'T-1422-09 k3d dry-run: 呼び出しは記録されている（0 件を緑にしない）' "$(cat "$STUB_LOG")" 'k3d cluster list'
: > "$STUB_LOG"
OUT="$(RUNTIME_OVERRIDE=k3d run_down --apply my-cluster)"
assert_contains 'T-1422-09 k3d apply: 指定したクラスタを削除する' "$(cat "$STUB_LOG")" 'k3d cluster delete my-cluster'

# ---- T-1422-10: kubectl / helm / k3d を起動するのは 4 つの口だけ（静的） ----
# 「コマンド位置」を列挙すると漏れる（`if kubectl …` / `then` / `!` / `command` / `xargs` / バッククォート）。
# 逆に**許す形だけを消してから、語として残る kubectl / helm / k3d をすべて数える**:
#   引用符の中（表示文言・jsonpath）とコメントを消す → `mutate <bin>`（dry-run では起動しない口）と
#   `command -v <bin>`（在るかを見るだけ）を消す → 読み取りの口の本体 3 行（行番号で特定）を除く。
scan_direct() { # file → 残った行（行番号つき）
  local file="$1" bodies
  bodies="$(grep -nE '^  (kubectl|helm|k3d) "\$@"$' "$file" | cut -d: -f1 | paste -sd'|' -)"
  # 二重引用符は「コマンド置換（`$(` / バッククォート）を含まないもの」だけ消す —— `"$(kubectl …)"` は起動である。
  sed -E -e "s/'[^']*'//g" -e 's/"([^"\\$`]|\\.|\$[^("`])*"//g' -e 's/(^|[[:space:]])#.*$//' \
         -e 's/mutate[[:space:]]+(kubectl|helm|k3d)//g' -e 's/command -v (kubectl|helm|k3d)//g' "$file" \
    | grep -nE '(^|[^[:alnum:]_./-])(kubectl|helm|k3d)([^[:alnum:]_-]|$)' \
    | grep -Ev "^(${bodies:-0}):"
}
assert_eq 'T-1422-10 静的: 読み取りの口の本体以外から kubectl / helm / k3d を直接起動しない' "$(scan_direct "$SCRIPT")" ""
# 検出力の対照: 監査で素通りした形（`if kubectl delete ns bogus` を k3d の分岐へ差し込む）を実物の写しで捕まえる。
sed '/mutate k3d cluster delete/a\  if kubectl delete ns bogus; then :; fi' "$SCRIPT" > "$WORK/down-mutant.sh"
assert_contains 'T-1422-10 静的（陰性対照）: k3d の分岐へ差し込んだ `if kubectl delete` を捕まえる' "$(scan_direct "$WORK/down-mutant.sh")" 'kubectl delete ns bogus'
cat > "$WORK/scan-fixture.sh" <<'FIXTURE'
if kubectl delete ns a; then :; fi
  then helm uninstall b
! kubectl get c
command kubectl delete d
echo e | xargs kubectl delete ns
k3d cluster delete f && :
x="$(kubectl delete ns g)"
y=`helm uninstall h`
mutate kubectl delete ns ok1
command -v kubectl >/dev/null
echo "  (dry-run) kubectl delete ns ok2"
# kubectl delete ns ok3
kc_read get ns ok4; helm_read list; k3d_read cluster list ok5
FIXTURE
assert_eq 'T-1422-10 静的（検出力）: 起動する 8 形をすべて捕まえ、許す 5 形は捕まえない' \
  "$(scan_direct "$WORK/scan-fixture.sh" | cut -d: -f1 | paste -sd, -)" "1,2,3,4,5,6,7,8"
assert_eq 'T-1422-10 静的: 読み取りの口の本体はちょうど 3 行' "$(grep -cE '^  (kubectl|helm|k3d) "\$@"$' "$SCRIPT")" "3"
assert_contains 'T-1422-10 静的: mutate は dry-run で実行ファイルを起動しない' "$(sed -n '/^mutate() {/,/^}/p' "$SCRIPT")" 'echo "  (dry-run) $*"'

# ---- T-1422-11: 未知の引数は拒否する（--apply の打ち間違いを実行に倒さない） ----
dirty_cluster
OUT="$(run_down --aply)"; RC=$?
assert_eq 'T-1422-11 未知のオプションは exit 2' "$RC" "2"
assert_eq 'T-1422-11 未知のオプションではクラスタへ触らない' "$(cat "$STUB_LOG")" ""

# ---- T-1422-12: CRD を消す前に、5 段目で拾えない CR（対象外の名前空間・cluster スコープ）の finalizer を空にする ----
dirty_cluster
printf '%s\n' 'default|stray-cert|["cert-manager/x"]' > "$STATE/fin/certificates.cert-manager.io"
OUT="$(run_down --dry-run)"
assert_before 'T-1422-12 対象外の名前空間の CR は CRD の削除より前に patch する' "$OUT" \
  '(dry-run) kubectl patch certificates.cert-manager.io stray-cert -n default' '(dry-run) kubectl delete crd certificates.cert-manager.io'
assert_eq 'T-1422-12 対象の名前空間の CR は 6 段目で繰り返さない（5 段目で名前空間ごと消える）' \
  "$(grep -cF 'patch externalsecrets.external-secrets.io ast-secrets' <<<"$OUT")" "1"

# ---- T-1422-13: クラスタを読めないときは緑にしない（読み取りの口は stderr を捨てて 0 件に倒れる） ----
dirty_cluster
: > "$STATE/unreachable"
OUT="$(run_down --apply)"; RC=$?
assert_eq 'T-1422-13 apply: 到達不能なら exit 1' "$RC" "1"
assert_contains 'T-1422-13 apply: 読めないことを名指しする' "$OUT" 'NG: クラスタを読めない'
assert_missing 'T-1422-13 apply: 到達不能で OK を出さない' "$OUT" 'OK: 名前空間は'
dirty_cluster
grep -vx default "$STATE/namespaces" > "$STATE/ns.tmp"; mv "$STATE/ns.tmp" "$STATE/namespaces"
OUT="$(run_down --apply)"; RC=$?
assert_eq 'T-1422-13 apply: 読めても default が無ければ exit 1（別のクラスタ・空の応答を緑にしない）' "$RC" "1"
assert_contains 'T-1422-13 apply: default が無いことも同じ文言で名指しする' "$OUT" 'NG: クラスタを読めない'

# ---- T-1422-14: 残りの件数が 256 でも赤（終了コードへ件数を入れると 256 で 0 に巻き戻る） ----
dirty_cluster
for i in $(seq 1 256); do echo "leftover-$i"; done >> "$STATE/namespaces"
OUT="$(run_down --apply)"; RC=$?
assert_eq 'T-1422-14 apply: 残り 256 件でも exit 1' "$RC" "1"
assert_contains 'T-1422-14 apply: 件数を正しく出す' "$OUT" 'NG: 256 件が残った'

echo
echo "k8s-local-down.test.sh: ${PASSED} passed / ${FAILED} failed"
[ "$FAILED" -eq 0 ]
