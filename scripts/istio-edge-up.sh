#!/usr/bin/env bash
# #782 / ADR-0021: 経路B のエッジを Traefik から **Istio Ingress Gateway** へ移す。
#
#   bash scripts/istio-edge-up.sh --live              # PERMISSIVE のまま入口だけ移す
#   ISTIO_MTLS_MODE=STRICT bash scripts/istio-edge-up.sh --live   # 併せて mTLS を STRICT へ
#   RESET_FLOOR=0 bash scripts/istio-edge-up.sh --live            # リセット申請の床を外す（既定は 1＝入れる。#1500）
#                                                          # 🔴 検証で床の有無を比べる用途に限る。本番の退路に使わない（#1543）
#
# 前提（満たしていなければ非 0 で落ちる）:
#   - Istio が入っていること（ISTIO=1 ./scripts/k8s-local-up.sh --live。IADR-0307）
#   - cert-manager と ClusterIssuer local-edge-ca が居ること（LOCALEDGE=1。IADR-0206）
#
# 🔴 切り戻しは `bash scripts/istio-edge-down.sh --live` の 1 コマンドである。**先に読むこと。**
#
# なぜこの順でしか当てられないか:
#   k3s の ServiceLB（klipper）は LoadBalancer Service ごとに hostPort を握る DaemonSet を作る。
#   **80/443/50000 を 2 つの Service が同時に持てない**ため、Traefik が明け渡してから
#   istio-ingressgateway を立てる。逆順だと svclb が bind に失敗して**どちらの入口も立たない**。
set -euo pipefail

# NFR, #1550: 稼働クラスタのエッジを helm と kubectl で入れ替える。明示の指定（--live か LIVE=1）が無ければ何もせずに終わる（判定は副作用より前に置く）。
. "$(dirname "$0")/lib/live-opt-in.sh" || exit 3   # 判定器が読めなければ守れない —— 黙って続けず止める
live_opt_in_scan "$@"; set -- "${LIVE_REST[@]+"${LIVE_REST[@]}"}"
live_opt_in_require "istio-edge-up.sh"

# SC-15 / NFR-13 / ADR-0097 決定 2 / IADR-0432 (#1500): リセット申請の床は**既定 1（入れる）**。
# RESET_FLOOR=0 は検証で床の有無を比べる用途に限る（［2026-09-26 / #1543］計画 ADR-0111 決定 3。本番の退路に使わない）。
# 🔴 **0 / 1 以外は入口に触る前に落とす** —— 既定が 1 になったので
#   「false と書けば外れるつもり」の取り違えが起き得る。黙って入れても外しても誤りであり、
#   [2/5] で Traefik を落とした後に気付くのが最悪である（下の前提確認と同じ理由）。
RESET_FLOOR="${RESET_FLOOR:-1}"
case "$RESET_FLOOR" in
  0|1) ;;
  *) echo "ERROR: RESET_FLOOR は 0（床を外す）か 1（床を入れる。既定）のどちらかです: '${RESET_FLOOR}'" >&2; exit 1 ;;
esac

MSP_NS="${MSP_NS:-microservices-platform}"
ISTIO_VERSION="${ISTIO_VERSION:-1.30.4}"
cd "$(dirname "$0")/.."
# #1159 / IADR-0377: mTLS モードを書く唯一の口（helm を通す）。
# shellcheck source=scripts/lib/mesh-mtls-mode.sh
. "$(dirname "$0")/lib/mesh-mtls-mode.sh"

# 前提の確認。**黙って続けない**（入口を落としてから気付くのが最悪である）。
if ! kubectl -n istio-system get deploy istiod >/dev/null 2>&1; then
  echo "ERROR: istiod が居ません。先に ISTIO=1 ./scripts/k8s-local-up.sh --live を実行してください。" >&2
  exit 1
fi
if ! kubectl get clusterissuer local-edge-ca >/dev/null 2>&1; then
  echo "ERROR: ClusterIssuer local-edge-ca が居ません。先に LOCALEDGE=1 ./scripts/k8s-local-up.sh --live を実行してください。" >&2
  exit 1
fi

echo "==> [1/5] エッジ証明書を istio-system へ発行する（Gateway は同 namespace の Secret しか読めない）"
kubectl apply -f deploy/local/edge-istio/tls/edge-certificate-istio.yaml
kubectl -n istio-system wait --for=condition=Ready certificate/edge-tls --timeout=180s

echo "==> [2/5] Traefik の Service を落として 80/443/50000 を明け渡す"
kubectl apply -f deploy/local/edge-istio/traefik-service-off.yaml
# helm-controller の reconcile は非同期（IADR-0258）。**observable な結果**（Service の消滅）を待つ。
freed=0
for _ in $(seq 1 90); do
  if ! kubectl -n kube-system get svc traefik >/dev/null 2>&1; then freed=1; break; fi
  sleep 2
done
if [ "$freed" != "1" ]; then
  echo "ERROR: kube-system/traefik svc が消えません。hostPort が空かないので Gateway を立てられません。" >&2
  kubectl -n kube-system logs job/helm-install-traefik --tail=40 >&2 || true
  exit 1
fi

echo "==> [3/5] istio-ingressgateway を立てる"
helm repo add istio https://istio-release.storage.googleapis.com/charts >/dev/null 2>&1 || true
helm repo update istio >/dev/null
helm upgrade --install istio-ingressgateway istio/gateway \
  -n istio-system --version "$ISTIO_VERSION" \
  -f deploy/istio/ingressgateway-values-local.yaml --wait --timeout 5m

echo "==> [4/5] Gateway / VirtualService と CoreDNS の転送先を当てる"
# SC-15 / NFR-13 / ADR-0094 決定 2 / ADR-0097 決定 2 / IADR-0432 (#1410 / #1500): リセット申請の**床**（最小応答時間）。
# 🔴 **既定は 1（入れる）。** ［2026-09-26 / #1500］計画 ADR-0097 決定 2 が IADR-0432 決定 4（opt-in・既定 0）を
#   覆した。既定 OFF のままだと go-live の経路で所要時間の統制が 1 つも効かない。
#   🔴 **経路を入れた後に器が落ちると、リセット申請の POST はすべて 503 になる**（経路は器だけを向き、
#   予備の route は無い）。器は k8s-local-up.sh が rollout を待ってから立てている。
#   RESET_FLOOR=0 は素の edge-istio を当てる（経路だけが外れ、器は infra の持ち物として誰も通らないまま居る）。
#   🔴 ［2026-09-26 / #1543］**RESET_FLOOR=0 は本番の退路ではない**（計画 ADR-0111 決定 3）。外している間は
#   回数制限の無い申請で実在する利用者名を列挙できる。器がすべて落ちたときの 503 は「申請を閉じた状態」として
#   保ち、器を戻して復旧する（利用者は管理者の一時パスワード発行で復旧する）。器は 2 レプリカ ＋ PDB で動く（決定 1）。
if [ "$RESET_FLOOR" = "1" ]; then
  echo "    RESET_FLOOR=1（既定）: リセット申請の床を入れる（POST の応答を床まで返さない）"
  # 器の本体は ConfigMap 化する（kustomize は root 外ファイルを参照できない。門と同型）。
  # 器は k8s-local-up.sh の [4/7] で既に立っており、ConfigMap もそこで作られている。ここでも作るのは
  # 単独実行でもリポジトリの版を当てるためである（冪等）。🔴 overlay の apply より**前**に作る。
  kubectl create configmap reset-floor-script -n platform-infra \
    --from-file=reset-floor.js=deploy/mail-relay/reset-floor.js \
    --dry-run=client -o yaml | kubectl apply -f -
  kubectl apply -k deploy/local/edge-istio-reset-floor
else
  echo "    RESET_FLOOR=0: リセット申請の床を外す（経路を足さない。所要時間で利用者名を判別できる状態に戻る）" >&2
  echo "    🔴 検証で床の有無を比べる用途に限る。本番の退路に使わない（器が落ちたときは 503 のまま器を戻し、利用者は管理者の一時パスワード発行で復旧する）" >&2
  kubectl apply -k deploy/local/edge-istio
fi
# import 先の追加は Corefile 自体の変更ではないため reload プラグインが拾わない（IADR-0227 と同じ）。
kubectl -n kube-system rollout restart deploy/coredns
kubectl -n kube-system rollout status deploy/coredns --timeout=120s

echo "==> [5/5] mTLS モード: ${ISTIO_MTLS_MODE:-（変更しない）}"
if [ "${ISTIO_MTLS_MODE:-}" = "STRICT" ]; then
  # 🔴 入口が Envoy になった**後**でしか STRICT にしない。順序を入れ替えると 502 になる
  #   （#1072 / IADR-0307 が実測した形）。
  #
  # 🔴 **helm を通す。`kubectl patch` で書かない**（#1159 / IADR-0377）。
  #   `PeerAuthentication` を所有しているのは helm（Helm 4 はサーバサイド apply）であり、
  #   `kubectl patch` は `.spec.mtls.mode` の field manager を `kubectl-patch` へ奪う。
  #   奪われると**以後の `helm upgrade` が conflict で恒久的に失敗する** ——
  #   `--take-ownership` も `--force` も効かず（後者は SSA と併用できない）、
  #   復旧には対象を delete して helm に作り直させる人手が要る（実測）。
  #   ここが #1159 の「手動 patch によるドリフト」の出どころそのものである。
  set_mesh_mtls_mode "STRICT"
fi

echo "OK: エッジは istio-ingressgateway です。"
echo "    疎通確認（証明書検証を切らないこと。-k は使わない）:"
echo "      kubectl -n cert-manager get secret local-edge-root-ca -o jsonpath='{.data.ca\\.crt}' | base64 -d > /tmp/root-ca.pem"
echo "      curl --cacert /tmp/root-ca.pem https://localhost/ -o /dev/null -w '%{http_code}\\n'"
echo "    切り戻し: bash scripts/istio-edge-down.sh --live"
