#!/usr/bin/env bash
# NFR, IADR-0210 決定 7, #1688: **後から Recreate へ変えた Deployment を、既存リリースへ upgrade できるようにする。**
# `source` して `reconcile_recreate_strategy <ns> <deploy>...` を helm upgrade の**前に**呼ぶ。
#
# ## 何が起きるか
#
# RollingUpdate（既定）で作られた Deployment には、API サーバが既定値の `spec.strategy.rollingUpdate`
# （maxSurge / maxUnavailable 25%）を埋める。チャートが後から `type: Recreate` だけを宣言すると、
# Helm 4 のサーバサイド apply は**所有者の無いこの既定値を消さない**ので、`type: Recreate` と同居して
#
#   Deployment.apps "wiki-js" is invalid: spec.strategy.rollingUpdate: Forbidden:
#     may not be specified when strategy `type` is 'Recreate'
#
# で upgrade 全体が落ちる（#1688。wiki-js を #1569 で Recreate にした）。
#
# ## なぜチャートの `rollingUpdate: null` で直さないのか
#
# 2026-09-28 実測（kube-apiserver 1.34.1 / Helm v4.0.0・v4.2.1）: null は描画でも Helm でも落ちず
# API サーバまで届く（`"strategy":{"rollingUpdate":null,"type":"Recreate"}`）が、**SSA は null で既存の値を
# 消さず、同じ検証エラーになる**。ArgoCD（ServerSideApply=true）でも同じである。詳細は作業仕様書
# `.ai-context/specs/20260928_issue-1688_recreate-strategy-upgrade.md`。
#
# ## この patch が field manager を奪って後の upgrade を壊さないか（IADR-0377 の懸念）
#
# 壊さない。patch が書く値（`type: Recreate`）は**チャートと同じ値**なので SSA は共有所有として扱い、
# 以後の helm upgrade は conflict にならない（同じ実測。IADR-0377 の事故はチャートと違う値を書いた場合）。

# reconcile_recreate_strategy <ns> <deploy>...
#
# 冪等。次のいずれでも**何もせず 0 で返る**:
#   - `kubectl` が無い
#   - Deployment が無い（新規クラスタ・namespace 未作成）
#   - `spec.strategy.rollingUpdate` が空（移行済み ＝ 2 回目以降）
# 残っているときだけ strategy を `{"type":"Recreate"}` に置き換える。patch 自体の失敗は握らない
# （握っても直後の helm upgrade が同じ理由で落ちる。原因の近くで止める）。
reconcile_recreate_strategy() {
  local ns="$1"
  shift
  command -v kubectl >/dev/null 2>&1 || return 0
  local d ru
  for d in "$@"; do
    ru="$(kubectl -n "$ns" get deploy "$d" -o 'jsonpath={.spec.strategy.rollingUpdate}' 2>/dev/null)" || continue
    [ -n "$ru" ] || continue
    echo "    #1688: deploy/$d に RollingUpdate の残り（$ru）がある。helm upgrade の前に strategy を Recreate へ寄せる"
    kubectl -n "$ns" patch deploy "$d" --type=json \
      -p '[{"op":"replace","path":"/spec/strategy","value":{"type":"Recreate"}}]' || return 1
  done
  return 0
}
