#!/usr/bin/env bash
# IADR-0066: MSP+AST 連結ローカル k8s(k3d) dev 環境の起動オーケストレーション。
# 冪等（再実行可）。fail-safe: 機密は未設定なら dev 既定/空（no-op）で作成する。
#
#   bash scripts/k8s-local-up.sh [cluster-name]
#
# 前提ツール: docker / k3d / kubectl / helm（scripts/README や docs/operations 参照）。
# 機密の上書きは環境変数で: PG_PASSWORD / RABBITMQ_PASSWORD / KEYCLOAK_ADMIN_PASSWORD /
#   MINIO_ACCESS_KEY / MINIO_SECRET_KEY / WIKIJS_DB_PASSWORD / WIKIJS_SYNC_APIKEY / ANTHROPIC_API_KEY
set -euo pipefail

CLUSTER="${1:-msp-ast-dev}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
INFRA_NS="platform-infra"
MSP_NS="microservices-platform"

apply_secret() { # ns name key=val [key=val...]
  local ns="$1"; local name="$2"; shift 2
  local args=(); for kv in "$@"; do args+=(--from-literal="$kv"); done
  kubectl create secret generic "$name" -n "$ns" "${args[@]}" \
    --dry-run=client -o yaml | kubectl apply -f -
}

echo "==> [1/7] cluster"
# ランタイム自動判定: Rancher Desktop（内蔵 k3s・nerdctl）か、docker+k3d か。
RUNTIME="${K8S_LOCAL_RUNTIME:-auto}"
if [ "$RUNTIME" = "auto" ]; then
  if command -v nerdctl >/dev/null 2>&1; then RUNTIME="rancher";
  elif command -v k3d >/dev/null 2>&1 && command -v docker >/dev/null 2>&1; then RUNTIME="k3d";
  else echo "ERROR: Rancher Desktop(containerd) か docker+k3d が必要です。" >&2; exit 1; fi
fi
export K8S_LOCAL_RUNTIME="$RUNTIME"
echo "    runtime: $RUNTIME"
if [ "$RUNTIME" = "k3d" ]; then
  if ! k3d cluster list "$CLUSTER" >/dev/null 2>&1; then
    k3d cluster create "$CLUSTER" --agents 1 \
      -p "8080:80@loadbalancer" -p "8443:443@loadbalancer"
  else
    echo "    cluster '$CLUSTER' exists — reuse"
  fi
else
  # Rancher Desktop: 内蔵 k3s を使う（Preferences → Kubernetes を有効化しておくこと）。
  if ! kubectl cluster-info >/dev/null 2>&1; then
    echo "ERROR: k8s に到達できません。Rancher Desktop の Kubernetes を有効化し、" >&2
    echo "       kubectl の context を rancher-desktop にしてください。" >&2
    exit 1
  fi
  echo "    Rancher Desktop 内蔵 k3s を使用（context: $(kubectl config current-context))"
fi

echo "==> [2/7] build & import images"
bash "$ROOT/scripts/k8s-local-images.sh" "$CLUSTER"

echo "==> [3/7] infra namespace, secrets & realm ConfigMap (dev 既定; env で上書き可)"
kubectl create namespace "$INFRA_NS" --dry-run=client -o yaml | kubectl apply -f -
apply_secret "$INFRA_NS" postgres        "password=${PG_PASSWORD:-postgres}"
apply_secret "$INFRA_NS" rabbitmq        "password=${RABBITMQ_PASSWORD:-guest}"
apply_secret "$INFRA_NS" keycloak-admin  "password=${KEYCLOAK_ADMIN_PASSWORD:-admin}"

# Keycloak realm import 用 ConfigMap（実 realm ファイル＝単一情報源）。
# AST realm（submodule）が存在すれば同一 Keycloak へ併せて import する（MSP+AST 連結）。
realm_args=(--from-file=microservices-platform-realm.json=deploy/keycloak/microservices-platform-realm.json)
ast_realm="src/ai-stock-trading/infra/keycloak/realm-export.json"
if [ -f "$ast_realm" ]; then
  realm_args+=(--from-file=ai-stock-trading-realm.json="$ast_realm")
  echo "    + AST realm を同梱 import します"
fi
kubectl create configmap keycloak-realms -n "$INFRA_NS" "${realm_args[@]}" \
  --dry-run=client -o yaml | kubectl apply -f -

echo "==> [4/7] apply in-cluster infra"
kubectl apply -k deploy/local/infra
echo "    waiting for infra to become Ready..."
kubectl -n "$INFRA_NS" rollout status deploy/postgres --timeout=180s
kubectl -n "$INFRA_NS" rollout status deploy/rabbitmq --timeout=180s
kubectl -n "$INFRA_NS" rollout status deploy/redis --timeout=120s
kubectl -n "$INFRA_NS" rollout status deploy/keycloak --timeout=300s
kubectl -n "$INFRA_NS" rollout status deploy/qdrant --timeout=120s
kubectl -n "$INFRA_NS" rollout status deploy/otel-collector --timeout=120s

echo "==> [5/7] MSP namespace & app secrets (dev 既定; fail-safe 空 = no-op)"
kubectl create namespace "$MSP_NS" --dry-run=client -o yaml | kubectl apply -f -
apply_secret "$MSP_NS" minio-credentials \
  "accessKey=${MINIO_ACCESS_KEY:-minioadmin}" "secretKey=${MINIO_SECRET_KEY:-minioadmin}"
apply_secret "$MSP_NS" wikijs-db "password=${WIKIJS_DB_PASSWORD:-kp}"
apply_secret "$MSP_NS" wikijs-sync "apiKey=${WIKIJS_SYNC_APIKEY:-}"
# fail-safe: 空=外部 LLM を呼ばない（ADR-0010 ルーティングは明示設定時のみ有効）。
apply_secret "$MSP_NS" llm-provider-credentials \
  "anthropic-api-key=${ANTHROPIC_API_KEY:-}" "openai-api-key=${OPENAI_API_KEY:-}"

echo "==> [6/7] helm upgrade --install (values-local)"
helm upgrade --install msp deploy/helm/microservices-platform \
  -n "$MSP_NS" -f deploy/local/values-local.yaml

echo "==> [7/7] ExternalName aliases (素のサービス名 -> platform-infra FQDN)"
kubectl apply -f deploy/local/aliases/microservices-platform-externalnames.yaml

# ADR-0006, IADR-0077 (AST #24): opt-in オーバーレイ（既定オフ・fail-safe）。
# 既定（env 未設定）では以下は一切実行されず、上記 [1/7]..[7/7] の挙動は不変。
if [ "${OBSERVABILITY:-}" = "1" ]; then
  echo "==> [opt-in] observability stack (Prometheus/Loki/Tempo/Grafana)"
  kubectl apply -k deploy/local/observability
  # otel-collector を forwarding 構成（debug-only から切替）へ反映。
  kubectl -n "$INFRA_NS" rollout restart deploy/otel-collector
  echo "    Grafana: kubectl -n $INFRA_NS port-forward svc/grafana 3000:3000  # http://localhost:3000"
fi

if [ "${VAULT:-}" = "1" ]; then
  echo "==> [opt-in] Vault dev + ClusterSecretStore (要 External Secrets Operator CRD)"
  # dev root トークン（dev 既定 or env 上書き・平文は Git に載せない）。
  apply_secret "$INFRA_NS" vault-dev-token "token=${VAULT_DEV_ROOT_TOKEN:-devroot}"
  if kubectl get crd clustersecretstores.external-secrets.io >/dev/null 2>&1; then
    kubectl apply -k deploy/local/vault
  else
    echo "    WARN: external-secrets.io CRD 未導入のため ClusterSecretStore/Vault は skip。" >&2
    echo "          先に ESO を導入する（deploy/local/vault/README.md）。Vault dev のみ適用:" >&2
    kubectl apply -f deploy/local/vault/vault-dev.yaml
  fi
fi

if [ "${ARGOCD:-}" = "1" ]; then
  echo "==> [opt-in] ArgoCD bootstrap (手順は deploy/local/argocd/README.md)"
  kubectl create namespace argocd --dry-run=client -o yaml | kubectl apply -f -
  kubectl apply -n argocd -f https://raw.githubusercontent.com/argoproj/argo-cd/stable/manifests/install.yaml
  kubectl apply -f deploy/argocd/appproject.yaml -f deploy/argocd/application.yaml
  if [ -d src/ai-stock-trading/deploy/argocd ]; then
    kubectl apply -f src/ai-stock-trading/deploy/argocd/appproject.yaml -f src/ai-stock-trading/deploy/argocd/application.yaml
  fi
fi

echo ""
echo "done. 状態確認:"
echo "  kubectl get pods -A"
echo "  kubectl -n $MSP_NS port-forward svc/bff-service 5080:8080   # http://localhost:5080/health"
echo "AST 連結は AST chart(#122) 適用後に scripts/... で行う。"
