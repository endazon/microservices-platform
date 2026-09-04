#!/usr/bin/env bash
# #782 / ADR-0021: Istio エッジ（＋ STRICT mTLS）からの **1 コマンド切り戻し**。
#
#   bash scripts/istio-edge-down.sh
#
# 何を戻すか（この順でしか戻せない）:
#   1. PeerAuthentication を PERMISSIVE へ  … 先に緩める。ここが STRICT のままだと、
#      Traefik を戻した瞬間に「入口は復活したのに 502」という中途半端な状態になる。
#   2. Istio エッジ資材と ingressgateway を撤去 … **hostPort 80/443/50000 を先に空ける。**
#      k3s の ServiceLB は LoadBalancer ごとに hostPort を握る DaemonSet を作るので、
#      Traefik を先に戻すと svclb が bind に失敗して**どちらの入口も立たない**。
#   3. Traefik の HelmChartConfig と Ingress / CoreDNS を当て直す。
#   4. Traefik の Service が 80/443/50000 を取り戻すまで待つ（fail-closed。IADR-0258 と同じ形）。
#
# 🔴 冪等である。 Istio エッジを一度も当てていない状態で走らせても、既定経路（Traefik）を
#   当て直すだけで何も壊れない。**本番の変更を当てる前に、まずこれを走らせて確かめること。**
#
# Istio 本体（istiod / サイドカー）は落とさない。mTLS を PERMISSIVE に戻した時点で
# エッジは平文で入れるようになり、それが本スクリプトの目的である。istiod ごと撤去する手順は
# .ai-context/adr/IADR-0307_istio-optin-and-staged-mtls.md §現在のクラスタの状態 にある。
set -euo pipefail

MSP_NS="${MSP_NS:-microservices-platform}"
cd "$(dirname "$0")/.."
# #1159 / IADR-0377: mTLS モードを書く唯一の口（helm を通す）。
# shellcheck source=scripts/lib/mesh-mtls-mode.sh
. "$(dirname "$0")/lib/mesh-mtls-mode.sh"

echo "==> [1/4] PeerAuthentication -> PERMISSIVE"
# 🔴 **`kubectl patch` で書かない**（#1159 / IADR-0377）。helm が所有するフィールドを奪うと、
#   以後の `helm upgrade` が conflict で恒久的に失敗する（詳細は lib/mesh-mtls-mode.sh の冒頭）。
set_mesh_mtls_mode "PERMISSIVE"

echo "==> [2/4] Istio エッジ資材の撤去（hostPort を空ける）"
kubectl delete -k deploy/local/edge-istio --ignore-not-found=true || true
kubectl delete -f deploy/local/edge-istio/tls/edge-certificate-istio.yaml --ignore-not-found=true || true
helm uninstall istio-ingressgateway -n istio-system >/dev/null 2>&1 || true
# svclb の DaemonSet が消えるまで待つ。消えないうちに Traefik を戻すと bind が衝突する。
# 🔴 svclb の DaemonSet は **k3s の --servicelb-namespace（既定 kube-system）**に作られる。
#   Service と同じ namespace ではない。全 namespace から名前で引く。
for _ in $(seq 1 60); do
  if ! kubectl get ds -A -o name 2>/dev/null | grep -q "svclb-istio-ingressgateway"; then break; fi
  sleep 2
done

echo "==> [3/4] Traefik（既定経路）を当て直す"
kubectl apply -f deploy/local/edge/traefik-entrypoint.yaml
kubectl apply -k deploy/local/edge
kubectl apply -f deploy/local/aliases/coredns-edge-hosts.yaml
kubectl -n kube-system rollout restart deploy/coredns
kubectl -n kube-system rollout status deploy/coredns --timeout=120s
if kubectl get namespace argocd >/dev/null 2>&1; then
  kubectl apply -f deploy/local/edge/argocd-ingress.yaml
fi

echo "==> [4/4] Traefik Service が 80/443/50000 を取り戻すのを待つ"
# helm-controller の reconcile は非同期（IADR-0258）。**observable な結果**（Service のポート）を見る。
#
# 🔴 `kubectl wait` は **対象が存在しないと待たずに即エラーになる**（"services \"traefik\" not found"）。
#   ここでは Service ごと消してから戻すので、**存在を待つループが先に要る**。
#   これは机上の心配ではない —— 2026-08-30 の切り戻し実測で実際にこの race を踏み、
#   Traefik は正しく復活したのにスクリプトだけが非 0 で終わった。
for _ in $(seq 1 90); do
  if kubectl -n kube-system get svc traefik >/dev/null 2>&1; then break; fi
  sleep 2
done
for port_name in web websecure admin; do
  if ! kubectl -n kube-system wait --for=jsonpath="{.spec.ports[?(@.name==\"$port_name\")].name}=$port_name" \
       svc/traefik --timeout=180s; then
    echo "ERROR: kube-system/traefik svc に $port_name が戻りません。helm-controller の reconcile を確認してください。" >&2
    kubectl -n kube-system get svc traefik -o jsonpath='{range .spec.ports[*]}{.name}={.port}{"\n"}{end}' >&2 || true
    kubectl -n kube-system logs job/helm-install-traefik --tail=40 >&2 || true
    exit 1
  fi
done

echo "OK: 既定経路（Traefik）へ戻りました。mTLS は PERMISSIVE です。"
echo "    疎通確認（証明書検証を切らないこと。-k は使わない）:"
echo "      kubectl -n cert-manager get secret local-edge-root-ca -o jsonpath='{.data.ca\\.crt}' | base64 -d > /tmp/root-ca.pem"
echo "      curl --cacert /tmp/root-ca.pem https://localhost/ -o /dev/null -w '%{http_code}\\n'"
