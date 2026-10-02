#!/usr/bin/env bash
# NFR / ADR-0005・ADR-0021・ADR-0026, #1159（IADR-0377）:
# **稼働クラスタの mTLS モードを書く唯一の口。** `source` して `set_mesh_mtls_mode <MODE>` を呼ぶ。
#
# ## なぜ関数を 1 本に閉じるのか
#
# `PeerAuthentication <ns>-mtls` は helm チャート（`templates/istio-mtls.yaml`）の描画物であり、
# **所有者は helm ただ 1 つ**である。Helm 4 はサーバサイド apply（`manager: helm` / `operation: Apply`）を
# 使うので、同じフィールドを `kubectl patch`（`operation: Update`）で書くと **field manager が奪われる**。
#
# 奪われたあとに起きること（2026-09-04 実測。k3s v1.35.4 / Helm v4.2.1。全文は IADR-0377）:
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

# ---------------------------------------------------------------------------
# NFR-16 / IADR-0487 (#1710): **現行の mTLS モードを読む口。** 書く口（上）と同じく helm の宣言だけを見る。
#
# `k8s-local-up.sh` の [6/7] は `--reuse-values` 無しで `--set mesh.mtlsMode=…` を渡す。`ISTIO_MTLS_MODE` を
# 付けずに再実行すると、以前は `${ISTIO_MTLS_MODE:-PERMISSIVE}` で **STRICT のクラスタを黙って PERMISSIVE へ戻していた**。
# 起動器は未指定のときここで現行の値を読み、引き継ぐ。
# ［2026-10-02 / #1713, IADR-0488］`ISTIO` 未指定の再実行も同じ 1 回の読みで `mesh.enabled` を引き継ぐ（終了コード 0＝メッシュ宣言あり。
#   それ以外の値の意味は下の関数の注記のまま）。関数は増やさない —— 終了コードが既に「宣言あり／無い／読めない」を分けている。
#
# 読むのは `helm get values`（利用者が与えた値 = values-local.yaml と --set の和）であって `--all` ではない ——
# `--all` はチャートの既定（values.yaml の `mtlsMode: STRICT`）を混ぜるので、メッシュを一度も宣言していない
# リリースを「STRICT だった」と読んでしまう。稼働の PeerAuthentication でもない —— 書く口が helm ただ 1 つなので
# helm の宣言が正であり、乖離は check-stack-ready.js の G12 が別に落とす。
# ---------------------------------------------------------------------------

# mesh_values_mtls_mode <`helm get values -o yaml` の出力>   純関数
#   0: メッシュ宣言あり（トップレベル `mesh:` 直下の `enabled: true`）で、`mtlsMode` が値域内。モードを標準出力へ
#   1: メッシュ宣言なし（`mesh:` が無い・`enabled` が true でない）。引き継ぐものが無い
#   2: メッシュ宣言ありなのに `mtlsMode` が無い・値域外。読めないのと同じに扱う（呼び出し側が止める）
mesh_values_mtls_mode() {
  local parsed enabled mode
  parsed="$(printf '%s\n' "${1:-}" | awk '
    { line = $0; sub(/\r$/, "", line) }
    line ~ /^[ \t]*#/ { next }
    line ~ /^[ \t]*$/ { next }
    line ~ /^[^ \t]/ { inmesh = (line ~ /^mesh:[ \t]*$/); cind = -1; next }
    inmesh {
      match(line, /^[ \t]*/); ind = RLENGTH
      if (cind < 0) cind = ind                       # 直下の子の字下げ（最初の子で決める）
      if (ind != cind) next                          # 孫（backchannelLogout.* 等）は見ない
      v = line; sub(/^[ \t]*[A-Za-z]+:[ \t]*/, "", v); sub(/[ \t]+#.*$/, "", v); gsub(/"/, "", v); gsub(sprintf("%c", 39), "", v)
      if (line ~ /^[ \t]*enabled:/) enabled = v
      if (line ~ /^[ \t]*mtlsMode:/) mode = v
    }
    END { printf "%s %s\n", (enabled == "" ? "-" : enabled), (mode == "" ? "-" : mode) }')"
  enabled="${parsed%% *}"
  mode="${parsed#* }"
  [ "$enabled" = "true" ] || return 1
  case "$mode" in
    STRICT | PERMISSIVE | DISABLE) printf '%s\n' "$mode"; return 0 ;;
    *) return 2 ;;
  esac
}

# current_mesh_mtls_mode   クラスタ（helm）を読む
#   0: 引き継ぐモードを標準出力へ / 1: リリースが無い・メッシュ未宣言（新規の扱い） / 2: 読めない（fail-closed の材料）
#   🔴 「読めない」を「無い」へ倒さない —— 倒すと STRICT のクラスタを PERMISSIVE へ黙って戻す（#1710 そのもの）。
#   リリースの有無は `helm list`（無ければ空で 0）で確かめ、`helm get values` の失敗は常に「読めない」と読む。
#
# ［2026-10-02 / #1722, IADR-0491］**状態の絞りは 6 つのフラグの和で明示する（`-a` / `--all` は使わない）。**
#   helm v4 は list の `-a` / `--all` を廃し（`Error: unknown shorthand flag: 'a' in -a`・終了コード 1）、既定で全状態を返す。
#   helm v3 は `-a` を持つが、既定は deployed / failed だけで pending-* を落とす。`-a` を外すだけだと v3 で pending-upgrade 等の
#   リリースを「無い」と読み、初回の扱い（メッシュ無し・PERMISSIVE）へ倒れる。6 つのフラグの和は v3（3.12.3 / 3.22.0）と
#   v4（4.2.1）で同じ集合（v3 の `-a` と同じ。deployed・failed・pending-install/upgrade/rollback・superseded・uninstalling・
#   uninstalled）を返すことを実測した。`helm status` の終了コードは「無い」と「届かない」がどちらも 1 で分けられない。
current_mesh_mtls_mode() {
  local ns="${MSP_NS:-microservices-platform}"
  local release="${MSP_HELM_RELEASE:-msp}"
  local names values rc
  names="$(helm list -n "$ns" -q --filter "^${release}\$" \
    --deployed --failed --pending --superseded --uninstalling --uninstalled 2>/dev/null)" || return 2
  printf '%s\n' "$names" | grep -qx "$release" || return 1
  values="$(helm get values "$release" -n "$ns" -o yaml 2>/dev/null)" || return 2
  rc=0
  mesh_values_mtls_mode "$values" || rc=$?
  return "$rc"
}
