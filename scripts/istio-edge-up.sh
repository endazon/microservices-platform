#!/usr/bin/env bash
# #782 / ADR-0021: 経路B のエッジを Traefik から **Istio Ingress Gateway** へ移す。
#
#   bash scripts/istio-edge-up.sh              # PERMISSIVE のまま入口だけ移す
#   ISTIO_MTLS_MODE=STRICT bash scripts/istio-edge-up.sh   # 併せて mTLS を STRICT へ
#
# 前提（満たしていなければ非 0 で落ちる）:
#   - Istio が入っていること（ISTIO=1 ./scripts/k8s-local-up.sh。IADR-0307）
#   - cert-manager と ClusterIssuer local-edge-ca が居ること（LOCALEDGE=1。IADR-0206）
#
# 🔴 切り戻しは `bash scripts/istio-edge-down.sh` の 1 コマンドである。**先に読むこと。**
#
# なぜこの順でしか当てられないか:
#   k3s の ServiceLB（klipper）は LoadBalancer Service ごとに hostPort を握る DaemonSet を作る。
#   **80/443/50000 を 2 つの Service が同時に持てない**ため、Traefik が明け渡してから
#   istio-ingressgateway を立てる。逆順だと svclb が bind に失敗して**どちらの入口も立たない**。
set -euo pipefail

MSP_NS="${MSP_NS:-microservices-platform}"
ISTIO_VERSION="${ISTIO_VERSION:-1.30.4}"
cd "$(dirname "$0")/.."
# #1159 / IADR-0377: mTLS モードを書く唯一の口（helm を通す）。
# shellcheck source=scripts/lib/mesh-mtls-mode.sh
. "$(dirname "$0")/lib/mesh-mtls-mode.sh"

# 前提の確認。**黙って続けない**（入口を落としてから気付くのが最悪である）。
if ! kubectl -n istio-system get deploy istiod >/dev/null 2>&1; then
  echo "ERROR: istiod が居ません。先に ISTIO=1 ./scripts/k8s-local-up.sh を実行してください。" >&2
  exit 1
fi
if ! kubectl get clusterissuer local-edge-ca >/dev/null 2>&1; then
  echo "ERROR: ClusterIssuer local-edge-ca が居ません。先に LOCALEDGE=1 ./scripts/k8s-local-up.sh を実行してください。" >&2
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
kubectl apply -k deploy/local/edge-istio
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
echo "    切り戻し: bash scripts/istio-edge-down.sh"
