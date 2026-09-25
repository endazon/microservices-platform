#!/usr/bin/env bash
# IADR-0066 / #1422: ローカル k8s dev 環境の破棄。
#
#   bash scripts/k8s-local-down.sh [--dry-run|--apply] [cluster-name]   # 既定 --dry-run / msp-ast-dev
#
# 🔴 既定は --dry-run である。何も変更せず、消す予定のものを順に表示するだけ。--apply で実行する。
# 🔴 --apply は AST の OpenD の PVC（ログイン状態）・Vault の保存領域・投入済みの秘密を含め、
#    アプリ名前空間の中身をすべて消す。戻せない。
#
# k3d 経路はクラスタごと削除する。Rancher Desktop（内蔵 k3s）経路は内蔵クラスタを残し、
# 基盤が入れたものを次の順で撤去する（2026-09-11 の完全クリーン実測で、この順でなければ止まった）:
#   1. アプリの Helm リリース（ast / msp / reloader）
#   2. admission webhook（external-secrets / istio / cert-manager / argocd）の設定を消す
#      —— 先に消さないと、居なくなった webhook を待って 3 の patch が失敗する
#   3. finalizer を空にする（ESO の externalsecrets / secretstores / clustersecretstores、
#      ArgoCD の applications / appprojects）—— finalizer を処理するコントローラは先に消える
#   4. 残りの Helm リリース（external-secrets / istio-*）→ 3 をもう一度
#      （ESO のコントローラは動いている間 finalizer を付け直す。2026-09-14 実測）
#   5. 名前空間（--wait=false）。待っても Terminating のままなら、その名前空間の finalizer 付き
#      オブジェクトを空にする
#   6. CRD（istio.io / external-secrets.io / cert-manager.io / argoproj.io）と Bound でない PV
#   7. Istio エッジが止めた Traefik の Service を戻す（止めたままだと次の up のエッジ待ちが
#      `services "traefik" not found` で失敗する。2026-09-14 実測）
#   8. 検証: 名前空間は default / kube-* だけ・PV 0・6 の CRD 0・Helm は kube-system の
#      traefik / traefik-crd だけ・2 の webhook 設定 0・Traefik の Service が止まっていない。
#      1 つでも残れば exit 1。クラスタを読めない・一覧を読めないときも exit 1（「0 件」に倒さない）。
#      k3d 経路はクラスタの削除に失敗したら exit 1
#
# set -e にしない。途中の 1 段が失敗しても残りを進め、最後の検証で赤緑を出す
# （set -e だと最初の Helm エラーで止まり、半分消えた状態が残る —— 旧版の実測）。
#
# dry-run が読み取り専用であることの担保: クラスタへ触る口は kc_read / helm_read / k3d_read
# （読み取りの動詞を許可リストで閉じる。dry-run でも apply でも同じ）と mutate（dry-run では
# 表示するだけで実行ファイルを起動しない）の 4 つだけである。scripts/k8s-local-down.test.sh が
# スタブで全呼び出しを記録し、dry-run で読み取り以外が 1 件も起きないこと・この 4 つ以外から
# kubectl / helm / k3d を起動していないことを固定する。
set -uo pipefail

usage() {
  sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//'
}

MODE="dry-run"
CLUSTER="msp-ast-dev"
for arg in "$@"; do
  case "$arg" in
    --apply) MODE="apply" ;;
    --dry-run) MODE="dry-run" ;;
    -h|--help) usage; exit 0 ;;
    -*) echo "unknown option: $arg" >&2; usage >&2; exit 2 ;;
    *) CLUSTER="$arg" ;;
  esac
done

cd "$(dirname "$0")/.."

# 名前空間を待つ上限（秒）と間隔。テストは 0 にする。
NS_WAIT_SECONDS="${NS_WAIT_SECONDS:-240}"
NS_POLL_INTERVAL="${NS_POLL_INTERVAL:-5}"

# k8s-local-up.sh / istio-edge-up.sh が作る名前空間（ESO=1 / ISTIO=1 / ARGOCD=1 / LOCALEDGE=1 を含む）。
TARGET_NAMESPACES=(ai-stock-trading microservices-platform platform-infra reloader argocd external-secrets istio-system cert-manager)
# 1 段目（アプリ）と 4 段目（基盤部品）の Helm リリース。「名前空間 リリース名」。
APP_RELEASES=("ai-stock-trading ast" "microservices-platform msp" "reloader reloader")
INFRA_RELEASES=("external-secrets external-secrets" "istio-system istio-ingressgateway" "istio-system istiod" "istio-system istio-base")
WEBHOOK_PATTERN='istio|external-secrets|cert-manager|argocd'
CRD_PATTERN='(^|\.)(istio|external-secrets|cert-manager|argoproj)\.io$'
# コントローラより長生きする finalizer を持つ資源（3 段目）。
FINALIZER_RESOURCES=(
  externalsecrets.external-secrets.io
  secretstores.external-secrets.io
  clustersecretstores.external-secrets.io
  applications.argoproj.io
  appprojects.argoproj.io
)
# 8 段目で残ってよい Helm リリース（k3s の helm-controller が入れる内蔵のもの）。
ALLOWED_RELEASES_RE='^kube-system/(traefik|traefik-crd)$'
ALLOWED_NAMESPACES_RE='^(default|kube-.*)$'
FINALIZER_NULL_PATCH='{"metadata":{"finalizers":null}}'

# --- クラスタへ触る口（この 4 つ以外から kubectl / helm / k3d を起動しない） -----------------
bug() { echo "BUG: $*" >&2; exit 70; }
kc_read() {
  case "${1:-} ${2:-}" in
    "get "*|"api-resources "*|"config current-context") ;;
    *) bug "kc_read に読み取り以外の呼び出し: kubectl $*" ;;
  esac
  kubectl "$@"
}
helm_read() {
  case "${1:-}" in
    list) ;;
    *) bug "helm_read に読み取り以外の呼び出し: helm $*" ;;
  esac
  helm "$@"
}
k3d_read() {
  [ "${1:-} ${2:-}" = "cluster list" ] || bug "k3d_read に読み取り以外の呼び出し: k3d $*"
  k3d "$@"
}
mutate() {
  if [ "$MODE" = "apply" ]; then
    echo "  + $*"
    "$@" && return 0
    echo "  (失敗・続行) $*" >&2
    return 1   # 呼び出し側が失敗を拾えるように返す（段は止めない。止めるかは呼び出し側が決める）
  else
    echo "  (dry-run) $*"
  fi
}

# --- 読み取り ---------------------------------------------------------------------
# 読み取りの stderr の行き先。段 0〜7 は捨てる（在らない CRD の問い合わせ等が並ぶため）。8 段目の検証は
# cluster_readable を通した後なので捨てない —— 捨てると失敗が「0 件」に見える。
READ_ERR=/dev/null
# 以下の list_* は**読み取りの失敗を終了コードで返す**（8 段目が「数えられなかった」を残りとして数えるため）。
list_namespaces() { kc_read get namespaces -o jsonpath='{range .items[*]}{.metadata.name}{"\n"}{end}' 2>"$READ_ERR"; }
# 「名前空間/リリース名」の行。
list_releases() {
  local out
  out="$(helm_read list -A --no-headers 2>"$READ_ERR")" || return 1
  awk 'NF { print $2 "/" $1 }' <<<"$out"
}
list_crds() {
  local out
  out="$(kc_read get crd -o jsonpath='{range .items[*]}{.metadata.name}{"\n"}{end}' 2>"$READ_ERR")" || return 1
  grep -E "$CRD_PATTERN" <<<"$out" || true
}
# 「名前<TAB>phase<TAB>claimRef の名前空間」の行。
list_pvs() {
  kc_read get pv -o jsonpath='{range .items[*]}{.metadata.name}{"\t"}{.status.phase}{"\t"}{.spec.claimRef.namespace}{"\n"}{end}' 2>"$READ_ERR"
}
# 撤去対象の webhook 設定の名前。名前が部品名を含むもの**か**、呼び先の Service が撤去する名前空間に
# 居るもの（ESO の `externalsecret-validate` / `secretstore-validate` は名前に部品名を含まない）。
list_webhooks() {
  local kind="$1" targets_re out
  targets_re="(^|[[:space:]])($(IFS='|'; echo "${TARGET_NAMESPACES[*]}"))([[:space:]]|$)"
  out="$(kc_read get "$kind" -o jsonpath='{range .items[*]}{.metadata.name}{"\t"}{.webhooks[*].clientConfig.service.namespace}{"\n"}{end}' 2>"$READ_ERR")" \
    || return 1
  while IFS=$'\t' read -r name svc_ns; do
    [ -n "$name" ] || continue
    if grep -Eq "$WEBHOOK_PATTERN" <<<"$name" || [[ "$svc_ns" =~ $targets_re ]]; then echo "$name"; fi
  done <<<"$out"
}
# Traefik の Service を止める HelmChartConfig（Istio エッジが当てる `service.enabled: false`）が残っているか。
# HelmChartConfig が無ければ（エッジを当てていない構成では普通に無い）止まっていないと読む。
traefik_service_disabled() {
  local values
  values="$(kc_read get helmchartconfig traefik -n kube-system -o jsonpath='{.spec.valuesContent}' 2>/dev/null)" || return 1
  grep -Eq 'enabled:[[:space:]]*false' <<<"$values"
}
# 「名前空間|名前|finalizers」の行（finalizers が空のものは出さない）。名前空間を渡せばそこだけ。
# 区切りをタブにしない: タブは IFS の空白類で、cluster スコープの物（名前空間が空）を read すると
# 先頭の空欄が詰められ、名前が名前空間の欄へずれる。
list_finalized() {
  local resource="$1" ns="${2:-}" scope=(-A)
  [ -n "$ns" ] && scope=(-n "$ns")
  kc_read get "$resource" "${scope[@]}" \
    -o jsonpath='{range .items[*]}{.metadata.namespace}{"|"}{.metadata.name}{"|"}{.metadata.finalizers}{"\n"}{end}' 2>/dev/null \
    | awk -F '|' '$3 != "" && $3 != "[]"'
}
present_targets() {
  local present
  present="$(list_namespaces)"
  for ns in "${TARGET_NAMESPACES[@]}"; do
    grep -qx "$ns" <<<"$present" && echo "$ns"
  done
}

# --- 段 ------------------------------------------------------------------------------
uninstall_releases() {
  local current
  current="$(list_releases)"
  for entry in "$@"; do
    local ns="${entry%% *}" name="${entry##* }"
    grep -qx "$ns/$name" <<<"$current" && mutate helm uninstall "$name" -n "$ns"
  done
  return 0
}

delete_webhooks() {
  for kind in validatingwebhookconfigurations mutatingwebhookconfigurations; do
    while IFS= read -r name; do
      [ -n "$name" ] && mutate kubectl delete "$kind" "$name" --ignore-not-found
    done < <(list_webhooks "$kind")
  done
}

# resource の finalizer 付きオブジェクトを空にする（名前空間を渡せばそこだけ）。
# 3 つ目に skip-targets を渡すと、撤去対象の名前空間に居る物を飛ばす（5 段目で名前空間ごと消えるため）。
unfinalize() {
  local resource="$1" ns_filter="${2:-}" skip_targets="${3:-}" targets_re
  targets_re="^($(IFS='|'; echo "${TARGET_NAMESPACES[*]}"))$"
  while IFS='|' read -r ns name _; do
    [ -n "$name" ] || continue
    [ -n "$skip_targets" ] && [[ "$ns" =~ $targets_re ]] && continue
    if [ -n "$ns" ]; then
      mutate kubectl patch "$resource" "$name" -n "$ns" --type=merge -p "$FINALIZER_NULL_PATCH"
    else
      mutate kubectl patch "$resource" "$name" --type=merge -p "$FINALIZER_NULL_PATCH"
    fi
  done < <(list_finalized "$resource" "$ns_filter")
}

clear_finalizers() {
  for resource in "${FINALIZER_RESOURCES[@]}"; do unfinalize "$resource"; done
}

# 名前空間の中の finalizer 付きオブジェクトを種類を問わず空にする（5 段目の救済）。
clear_namespace_finalizers() {
  local ns="$1"
  while IFS= read -r resource; do
    [ -n "$resource" ] && unfinalize "$resource" "$ns"
  done < <(kc_read api-resources --verbs=list --namespaced -o name 2>/dev/null)
}

# 対象の名前空間が消えるまで最大 $1 秒待つ。残った名前空間を標準出力へ返す。
wait_namespaces_gone() {
  local limit="$1" waited=0 remaining
  while :; do
    remaining="$(present_targets)"
    [ -z "$remaining" ] && return 0
    { [ "$waited" -ge "$limit" ] || [ "$NS_POLL_INTERVAL" -le 0 ]; } && break
    sleep "$NS_POLL_INTERVAL"
    waited=$((waited + NS_POLL_INTERVAL))
  done
  echo "$remaining"
}

delete_namespaces() {
  local targets remaining
  mapfile -t targets < <(present_targets)
  if [ "${#targets[@]}" -eq 0 ]; then
    echo "  （対象の名前空間は既に無い）"
    return 0
  fi
  mutate kubectl delete namespace "${targets[@]}" --wait=false --ignore-not-found
  if [ "$MODE" != "apply" ]; then
    echo "  (dry-run) ${NS_WAIT_SECONDS} 秒待って Terminating のまま残る名前空間は、その中の finalizer 付きオブジェクトを空にする"
    return 0
  fi
  remaining="$(wait_namespaces_gone "$NS_WAIT_SECONDS")"
  [ -z "$remaining" ] && return 0
  for ns in $remaining; do
    echo "  名前空間 $ns が ${NS_WAIT_SECONDS} 秒で消えない。中の finalizer を空にする。"
    clear_namespace_finalizers "$ns"
  done
  # 空にした後の後片付けを少し待つ（残れば 8 段目が赤にする）。
  wait_namespaces_gone 60 >/dev/null
}

delete_crds_and_pvs() {
  local crds
  mapfile -t crds < <(list_crds)
  # CRD の削除は、その CR が finalizer を持つ限り終わらない（処理するコントローラは既に居ない）。
  # 先に CR の finalizer を空にする（cluster スコープの CR —— clusterexternalsecrets 等 —— と、撤去対象の外の
  # 名前空間に居る CR は 5 段目で拾えない。対象の名前空間に居る物は 5 段目で消えているので飛ばす）。
  for crd in "${crds[@]}"; do
    [ -n "$crd" ] && unfinalize "$crd" "" skip-targets
  done
  for crd in "${crds[@]}"; do
    [ -n "$crd" ] && mutate kubectl delete crd "$crd" --ignore-not-found --timeout=60s
  done
  local targets_re
  targets_re="^($(IFS='|'; echo "${TARGET_NAMESPACES[*]}"))$"
  while IFS=$'\t' read -r name phase claim_ns; do
    [ -n "$name" ] || continue
    if [ "$phase" != "Bound" ]; then
      mutate kubectl delete pv "$name" --ignore-not-found
    elif [ "$MODE" != "apply" ] && [[ "$claim_ns" =~ $targets_re ]]; then
      # dry-run では名前空間がまだ在るので Bound のまま見える。5 段目の後に解放される見込みのものを示す。
      echo "  (dry-run) kubectl delete pv $name --ignore-not-found   # 現在 Bound（$claim_ns）。名前空間の削除後に解放されたら消す"
    fi
  done < <(list_pvs)
}

restore_traefik_service() {
  if traefik_service_disabled; then
    # 失敗しても続行する（8 段目が HelmChartConfig を読み直し、止まったままなら残りとして数える）。
    mutate kubectl apply -f deploy/local/edge/traefik-entrypoint.yaml \
      || echo "  Traefik の Service を戻せなかった。次の up のエッジ待ちが落ちる（8 段目で赤にする）。" >&2
  else
    echo "  （Traefik の Service は止められていない）"
  fi
}

# クラスタを読めることを確かめる。読み取りの口は stderr を捨てて「0 件」に倒れるので、
# **読めないクラスタでは残りも 0 件に見える**（到達不能なのに OK と出す）。8 段目はこれを先に通す。
cluster_readable() {
  local names
  names="$(kc_read get namespaces -o jsonpath='{range .items[*]}{.metadata.name}{"\n"}{end}')" || return 1
  grep -qx default <<<"$names"
}

# 残っているものを数えて表示する。件数は LEFTOVER_COUNT に置き、0 件のときだけ真を返す
# （`return <件数>` は 256 で 0 に巻き戻るので使わない）。
LEFTOVER_COUNT=0
report_leftovers() {
  local bad=0 out line kind
  # 読めなかった一覧は「0 件」ではなく「数えられなかった」として 1 件に数える（黙って緑にしない）。
  unreadable() { echo "  残: $1 を読めない（数えられない）"; bad=$((bad + 1)); }

  if out="$(list_namespaces)"; then
    while IFS= read -r line; do
      [ -n "$line" ] || continue
      [[ "$line" =~ $ALLOWED_NAMESPACES_RE ]] && continue
      echo "  残: namespace $line"; bad=$((bad + 1))
    done <<<"$out"
  else unreadable namespaces; fi

  if out="$(list_pvs)"; then
    while IFS= read -r line; do
      [ -n "$line" ] || continue
      echo "  残: pv ${line%%$'\t'*}"; bad=$((bad + 1))
    done <<<"$out"
  else unreadable pv; fi

  if out="$(list_crds)"; then
    while IFS= read -r line; do
      [ -n "$line" ] || continue
      echo "  残: crd $line"; bad=$((bad + 1))
    done <<<"$out"
  else unreadable crd; fi

  if out="$(list_releases)"; then
    while IFS= read -r line; do
      [ -n "$line" ] || continue
      [[ "$line" =~ $ALLOWED_RELEASES_RE ]] && continue
      echo "  残: helm $line"; bad=$((bad + 1))
    done <<<"$out"
  else unreadable "helm のリリース一覧"; fi

  # 2 段目で消せなかった webhook 設定（呼び先が居なくなった webhook は、以後の同種の資源の作成・更新を止める）。
  for kind in validatingwebhookconfigurations mutatingwebhookconfigurations; do
    if out="$(list_webhooks "$kind")"; then
      while IFS= read -r line; do
        [ -n "$line" ] || continue
        echo "  残: ${kind%s} $line"; bad=$((bad + 1))
      done <<<"$out"
    else unreadable "$kind"; fi
  done

  # 7 段目で戻せなかった Traefik の Service（止まったままだと次の up のエッジ待ちが落ちる）。
  if traefik_service_disabled; then
    echo "  残: Traefik の Service が止まったまま（HelmChartConfig traefik が service.enabled: false）"; bad=$((bad + 1))
  fi

  LEFTOVER_COUNT="$bad"
  [ "$bad" -eq 0 ]
}

# --- 本体 ----------------------------------------------------------------------------
RUNTIME="${K8S_LOCAL_RUNTIME:-auto}"
if [ "$RUNTIME" = "auto" ]; then
  if command -v nerdctl >/dev/null 2>&1; then RUNTIME="rancher"; else RUNTIME="k3d"; fi
fi

echo "mode: $MODE（--apply で実行。既定は --dry-run で何も変更しない）"

if [ "$RUNTIME" = "k3d" ] && command -v k3d >/dev/null 2>&1 && k3d_read cluster list "$CLUSTER" >/dev/null 2>&1; then
  if ! mutate k3d cluster delete "$CLUSTER"; then
    echo "NG: k3d cluster '$CLUSTER' を削除できなかった。" >&2
    exit 1
  fi
  [ "$MODE" = "apply" ] && echo "deleted k3d cluster '$CLUSTER'."
  exit 0
fi

echo "Rancher Desktop 経路（内蔵 k3s は残す）。context: $(kc_read config current-context 2>/dev/null || echo '?')"
echo "🔴 --apply は AST の OpenD の PVC（ログイン状態）・Vault の保存領域・投入済みの秘密を含めて消す。戻せない。"

echo "==> [0/8] 現状"
report_leftovers
echo "  （上の「残」は 8 段目で赤になるもの。PVC は名前空間ごと消える）"
for ns in $(present_targets); do
  kc_read get pvc -n "$ns" -o jsonpath='{range .items[*]}{"  pvc "}{.metadata.namespace}{"/"}{.metadata.name}{"\n"}{end}' 2>/dev/null
done

echo "==> [1/8] アプリの Helm リリース"
uninstall_releases "${APP_RELEASES[@]}"
echo "==> [2/8] admission webhook の設定（finalizer の patch より先）"
delete_webhooks
echo "==> [3/8] コントローラより長生きする finalizer を空にする"
clear_finalizers
echo "==> [4/8] 残りの Helm リリース → finalizer をもう一度"
uninstall_releases "${INFRA_RELEASES[@]}"
if [ "$MODE" = "apply" ]; then
  clear_finalizers
else
  echo "  (dry-run) 3 段目をもう一度（ESO のコントローラが消えるまでに付け直した finalizer を空にする）"
fi
echo "==> [5/8] 名前空間"
delete_namespaces
echo "==> [6/8] CRD と Bound でない PV"
delete_crds_and_pvs
echo "==> [7/8] Traefik の Service"
restore_traefik_service

echo "==> [8/8] 検証"
if [ "$MODE" != "apply" ]; then
  echo "  (dry-run) 何も変更していない。--apply で実行すると、ここで残りを数えて 1 つでもあれば exit 1 にする。"
  exit 0
fi
if ! cluster_readable; then
  echo "NG: クラスタを読めない（namespaces の取得に失敗したか default が無い）。残りを数えられないので緑にしない。" >&2
  exit 1
fi
READ_ERR=/dev/stderr   # ここからは読み取りの失敗を捨てない
if ! report_leftovers; then
  echo "NG: $LEFTOVER_COUNT 件が残った（上の「残」）。" >&2
  exit 1
fi
echo "OK: 名前空間は default / kube-* だけ・PV 0・アプリの CRD 0・Helm は kube-system の traefik だけ・webhook 設定 0・Traefik の Service は動いている。"
