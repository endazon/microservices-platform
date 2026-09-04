#!/usr/bin/env bash
# NFR / ADR-0005・ADR-0021・ADR-0026, #1159（IADR-0374）:
# **稼働クラスタの mTLS モードを書く唯一の口。** `source` して `set_mesh_mtls_mode <MODE>` を呼ぶ。
#
# ## なぜ関数を 1 本に閉じるのか
#
# `PeerAuthentication <ns>-mtls` は helm チャート（`templates/istio-mtls.yaml`）の描画物であり、
# **所有者は helm ただ 1 つ**である。Helm 4 はサーバサイド apply（`manager: helm` / `operation: Apply`）を
# 使うので、同じフィールドを `kubectl patch`（`operation: Update`）で書くと **field manager が奪われる**。
#
# 奪われたあとに起きること（2026-09-04 実測。k3s v1.35.4 / Helm v4.2.1。全文は IADR-0374）:
#
#   managers=helm/Apply,kubectl-patch/Update
#   Error: UPGRADE FAILED: conflict occurred while applying object ... PeerAuthentication:
#     Apply failed with 1 conflict: conflict with "kubectl-patch" using security.istio.io/v1: .spec.mtls.mode
#
# 🔴 **`--set` で同じ値を渡しても、`--take-ownership` を付けても、`--force` を付けても直らない**
#   （`--force` は「server-side apply と force replace は併用できない」で落ちる）。
#   つまり **書き換え 1 回で `k8s-local-up.sh` が恒久的に壊れる** —— [6/7] の `helm upgrade` が
#   そこで落ち、`set -euo pipefail` の下で up 全体が止まる。「もう一度流せば収束する」は成り立たない。
#
# 復旧（人手が要る。手順は docs/operations/operations.md）: 対象を delete して helm に作り直させる。
#
# **だから mode は helm を通してしか書かない。** 乖離の検知は `scripts/check-stack-ready.js` の門 G12。

# set_mesh_mtls_mode <STRICT|PERMISSIVE|DISABLE>
#
# `msp` リリースが無ければ **何もせず 0 で返る**（切り戻しスクリプトの冪等性のため。
# メッシュ未導入のクラスタで走らせても壊さない）。
set_mesh_mtls_mode() {
  local mode="$1"
  local ns="${MSP_NS:-microservices-platform}"
  local release="${MSP_HELM_RELEASE:-msp}"
  local chart="${MSP_HELM_CHART:-deploy/helm/microservices-platform}"

  case "$mode" in
    STRICT | PERMISSIVE | DISABLE) ;;
    *)
      echo "ERROR: set_mesh_mtls_mode: 未知のモード '$mode'（STRICT / PERMISSIVE / DISABLE のいずれか）" >&2
      return 1
      ;;
  esac

  if ! helm status "$release" -n "$ns" >/dev/null 2>&1; then
    echo "    （helm リリース $release が無い。メッシュ未導入とみなして飛ばす）"
    return 0
  fi

  echo "    helm 経由で mesh.mtlsMode=$mode を宣言する（kubectl patch では書かない / #1159）"
  helm upgrade "$release" "$chart" -n "$ns" --reuse-values --set "mesh.mtlsMode=$mode" >/dev/null
}
