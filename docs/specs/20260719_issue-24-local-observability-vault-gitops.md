---
title: ローカル（経路B）可観測性スタック・Vault・GitOps の opt-in オーバーレイ（AST #24 の MSP 分）
type: work
status: draft
related_ids:
  - ADR-0006
  - NFR
  - IADR-0066
  - IADR-0077
author: endazon (with Claude Code)
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
---

# 作業仕様書: ローカル（経路B）可観測性・Vault・GitOps の opt-in オーバーレイ（AST #24 の MSP 分）

> AST [ai-stock-trading#24](https://github.com/endazon/ai-stock-trading/issues/24)（ADR-0006 インフラ・デプロイ構成）の
> ローカル（経路B）分のうち、**共有インフラの stand-up は MSP 側**で行う（AST 側は AST PR で完結）。
> すべて **opt-in / 既定オフ**で、既存の経路B 起動（`deploy/local/infra`・`k8s-local-up.sh`）は**追加のみ・不変**。
> `deploy/keycloak/*realm*.json`（realm-fix）・`docker-compose.yml` は**触らない**。**平文の秘密をコミットしない。**

## 起点・関連

- 起点: **ADR-0006**（Hetzner・Vault 秘匿・OTel/Prometheus/Loki 可観測性）/ **NFR**（可観測性・認証情報 Vault 秘匿）
- 実装 ADR: [IADR-0066](../adr/IADR-0066_local-k8s-dev-environment.md)（経路B ローカル k8s dev 環境）、本作業の [IADR-0077](../adr/IADR-0077_local-observability-vault-gitops-overlays.md)
- 対象 Issue: AST #24（`Refs`）。AST 側 = ai-stock-trading PR（IADR-0094）。

## 対象範囲（追加のみ・opt-in）

1. **可観測性オーバーレイ** `deploy/local/observability/`: Prometheus / Loki / Tempo / Grafana の k8s manifest
   （`platform-infra` namespace）と、otel-collector を forwarding 構成へ差し替える ConfigMap。**既存 compose の設定
   （`deploy/prometheus.yml`・`loki-config.yaml`・`tempo.yaml`・`otel-collector-config.yaml`・`grafana/provisioning`）
   と同内容を inline**（kustomize の root 外参照制約に従う・経路B 既存 otel manifest と同じ方針）。
2. **Vault オーバーレイ** `deploy/local/vault/`: Vault **dev モード**（インメモリ・単一 Pod）と External Secrets Operator
   用の `ClusterSecretStore`。ESO 本体 install は URL 適用（script/docs）。AST の `ast-secrets`/`moomoo-*` の
   `ExternalSecret`（AST chart 側・opt-in）が同期できる状態を作る。**dev トークンは固定 dev 既定・env で上書き可・平文コミットしない。**
3. **GitOps オーバーレイ** `deploy/local/argocd/`: ArgoCD install（URL 適用）と、MSP/AST の `Application`/`AppProject` を
   登録するブートストラップ手順。
4. **`scripts/k8s-local-up.sh`**: `OBSERVABILITY` / `VAULT` / `ARGOCD` の env ゲート（既定オフ）で上記を**追加のみ**適用する
   ステップを増設。既定（未設定）の起動は現状と完全一致。
5. docs: 本作業仕様書・[IADR-0077](../adr/IADR-0077_local-observability-vault-gitops-overlays.md)・`docs/security/security.md`
   （Vault dev トークンの dev secret 追記）・`deploy/local/README.md` の opt-in 節。

## 対象外（Tier 3・後続）

- Hetzner 実 k3s での本番 stand-up・Vault 本番運用（unseal/監査/ローテーション）・ArgoCD 実同期・稼働率99%実測。
- `deploy/keycloak/*realm*.json`・`docker-compose.yml`・`values-local.yaml`/`k8s-local-up.sh` の既存ステップの改変。

## 設計

- 可観測性オーバーレイは**独立 kustomization**（`deploy/local/infra/kustomization.yaml` には含めない）。opt-in 時のみ
  `kubectl apply -k deploy/local/observability` で適用し、otel-collector を forwarding 構成へ更新して rollout restart する。
  既定（infra のみ適用）は debug exporter のまま＝外部送信なし（fail-safe）。
- メトリクス経路は既存 compose と同じ push モデル（アプリ→OTLP→collector→prometheusremotewrite/otlp tempo/loki push）。
- Vault は dev モード（`-dev`・インメモリ）。**本番充足ではない**（dev 検証専用）。`ClusterSecretStore` は
  `vault-backend`（AST chart の既定 `externalSecrets.secretStoreRef.name` と一致）。

## 受け入れ基準

- [ ] `kubectl kustomize deploy/local/observability` / `deploy/local/vault` が妥当（オフライン build 成功）
- [ ] 既定（env 未設定）の `k8s-local-up.sh` の適用対象・順序が現状と一致（追加ステップは env ゲート内のみ）
- [ ] Grafana datasource が Prometheus/Loki/Tempo を指し、collector forwarding 構成が既存 compose と同経路
- [ ] `ClusterSecretStore` 名が AST 既定 `vault-backend` と一致し、AST の `ExternalSecret` が解決できる形
- [ ] 平文の秘密が manifest・values・docs に無い（Vault dev トークンは dev 既定・env 上書き可）
- [ ] Hetzner 実 stand-up・実同期・本番 NFR は **Tier 3** として明示分離
