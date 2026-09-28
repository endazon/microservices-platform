#!/usr/bin/env bash
# NFR, ADR-0021, IADR-0317 (#1691): **経路B の入口が Istio Ingress Gateway へ移っているかの判定（単一の口）。**
# `source` して使う。sourcing は関数を定義するだけで何もしない。
#
#   edge_traefik_service_off [reader]        # クラスタを読む。reader の既定は kubectl（k8s-local-down.sh は kc_read を渡す）
#   edge_values_traefik_service_off <values> # 純関数。HelmChartConfig の valuesContent だけを見る
#
# 移行済みの印は、istio-edge-up.sh が当てる HelmChartConfig kube-system/traefik
# （deploy/local/edge-istio/traefik-service-off.yaml）の `service.enabled: false` である。
# 読むのは k8s-local-up.sh（再実行で Traefik へ戻さない。#1691）と k8s-local-down.sh（Traefik の Service を戻す）。
# 🔴 **判定をそれぞれのスクリプトへ複写しない。** 以前は up が「service: の直下」、down が「どこかに enabled: false」と
#   厳しさが違い、今の 2 本の宣言では同じ結論でも、宣言が増えるとずれる形だった（監査 #1694）。
#
# 判定（k8s-local-up.test.js が表で固定する）:
#   - **移行済み**: トップレベルの `service:` ブロックの直下の子に `enabled: false` がある
#     （`service: {enabled: false}` の flow 形も同じ）。直下の子であれば他のキーが先にあってもよい。
#   - **未移行**: HelmChartConfig が無い・読めない（kubectl の失敗は出力があっても未移行）・valuesContent が空・
#     `service.enabled: true`・`service:` 以外のキーの `enabled: false`（例 `ingressRoute.dashboard.enabled`）・
#     入れ子の `x.service.enabled: false`（例 `metrics.service`。Traefik の LoadBalancer Service を消すのはトップレベルの
#     `service.enabled` だけである）・引用符つきの `"false"`（Go テンプレートでは空でない文字列は真であり、Service は消えない）。
#   - 読めないときに未移行へ倒すのは、移行済みを見落としても Traefik の反映待ちが**非 0 で止まる**（黙らない）からである。
#     移行済みへ倒すと、Traefik がエッジのクラスタで反映待ちの門（IADR-0258）を黙って飛ばすことになる。

# edge_values_traefik_service_off <valuesContent>  → 移行済みなら 0、そうでなければ 1
edge_values_traefik_service_off() {
  printf '%s\n' "${1:-}" | awk '
    { line = $0; sub(/\r$/, "", line) }                 # CRLF でも同じに読む
    line ~ /^[ \t]*#/ { next }                          # 行コメント
    { sub(/[ \t]+#.*$/, "", line) }                     # 行末コメント
    line ~ /^[ \t]*$/ { next }
    line ~ /^[^ \t]/ {                                  # トップレベルのキー: ブロックの出入りだけを決める
      insvc = 0
      if (line ~ /^service:[ \t]*\{.*\}[ \t]*$/) {
        if (line ~ /[{,][ \t]*enabled:[ \t]*false[ \t]*[,}]/) found = 1
        next
      }
      if (line ~ /^service:[ \t]*$/) { insvc = 1; cind = -1 }
      next
    }
    insvc {
      match(line, /^[ \t]*/); ind = RLENGTH
      if (cind < 0) cind = ind                          # 直下の子の字下げ（最初の子で決める）
      if (ind == cind && line ~ /^[ \t]*enabled:[ \t]*false[ \t]*$/) found = 1
    }
    END { exit(found ? 0 : 1) }'
}

# edge_traefik_service_off [reader]  → 移行済みなら 0。読めなければ 1（未移行）
edge_traefik_service_off() {
  local values
  values="$("${@:-kubectl}" get helmchartconfig traefik -n kube-system -o jsonpath='{.spec.valuesContent}' 2>/dev/null)" || return 1
  edge_values_traefik_service_off "$values"
}
