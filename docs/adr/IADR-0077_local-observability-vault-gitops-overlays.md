---
title: IADR-0077 経路B（ローカル k8s）の可観測性スタック・Vault・GitOps は deploy/local の opt-in オーバーレイ＋k8s-local-up.sh の env ゲートで追加のみ配線し、既定は現状不変（外部送信なし・fail-safe）とする
type: impl-adr
status: Accepted
related_ids:
  - ADR-0006
  - NFR
  - IADR-0066
author: claude
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
---

# IADR-0077: 経路B の可観測性・Vault・GitOps は deploy/local の opt-in オーバーレイ＋env ゲートで追加のみ配線する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-19
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **ADR-0006**（observability: OTel/Prometheus/Loki）、**NFR**（可観測性・認証情報秘匿・可用性）、
  基盤の [IADR-0066](IADR-0066_local-k8s-dev-environment.md)（経路B ローカル k8s dev 環境）
- 対象 Issue: **AST#24**（ADR-0006 インフラ・デプロイ構成）の MSP 分。`Refs`。
  AST 側は ai-stock-trading の PR（AST IADR-0094）で完結する。
- 関連する実装仕様書: [20260719_issue-24-local-observability-vault-gitops](../specs/20260719_issue-24-local-observability-vault-gitops.md)

> **参照上の注意（ADR 番号の跨ぎ）**: 本 IADR は本リポの採番。AST 側 `IADR-0094` は下流 ai-stock-trading の別採番。

## 背景・課題

AST #24（ADR-0006）はローカル（経路B）で可観測性 UI（Prometheus/Grafana/Loki/Tempo）・Vault 秘匿・GitOps(ArgoCD)を
立てて配線検証する分を求める。経路B のハーネス（`scripts/k8s-local-up.sh`・`deploy/local/`）と共有インフラ
（otel-collector 等）は本リポにあるが、これらのバックエンドは **compose（経路A）専用**で経路B(k8s) には無い
（route B infra は postgres/rabbitmq/redis/keycloak/qdrant/otel-collector のみ・otel は debug exporter だけ）。

課題: (1) 既存の経路B 起動・CI を壊さずに追加する、(2) 平文の秘密をコミットしない、(3) 実基盤依存（Hetzner 実
stand-up・本番 NFR）を明示分離する、(4) 並行作業（realm-fix・`docker-compose.yml` の #282）と衝突しない。

## 決定

### 決定1: opt-in オーバーレイ（`deploy/local/{observability,vault,argocd}/`）＋env ゲート

- 3 種のバックエンドを **独立ディレクトリ**の追加 manifest として置く。`deploy/local/infra/kustomization.yaml` には
  **含めない**（既定の経路B 起動に出てこない）。`scripts/k8s-local-up.sh` に `OBSERVABILITY` / `VAULT` / `ARGOCD` の
  **env ゲート（既定オフ）** を増設し、既定（env 未設定）の適用対象・順序は現状と完全一致させる（追加のみ）。
- `deploy/keycloak/*realm*.json`（realm-fix）・`docker-compose.yml`（#282）・`values-local.yaml` の既存ステップは
  **触らない**。

### 決定2: 可観測性は既存 compose と同経路・既定は debug-only（fail-safe）

- Prometheus/Loki/Tempo/Grafana を `platform-infra` に立て、config は compose（`deploy/prometheus.yml` 他）と**同内容を
  inline**する（kustomize の root 外参照制約に従う。経路B の `otel-collector.yaml` と同じ二重管理方針）。
- メトリクスは push モデル（アプリ→OTLP→collector→prometheusremotewrite/otlp tempo/loki push）。opt-in 時のみ
  otel-collector の ConfigMap を forwarding 構成へ**同名上書き**し rollout restart する。**既定（infra のみ）は
  debug exporter のまま＝外部送信なし**（fail-safe）。

### 決定3: Vault は dev モード・平文秘密なし・ストア名は AST 既定と一致

- Vault は `-dev`（インメモリ・単一 Pod・unseal 不要）。**本番の Vault 化充足ではない**（dev 検証専用）。
- ESO の `ClusterSecretStore` を **`vault-backend`**（AST chart の既定 `externalSecrets.secretStoreRef.name`）で作り、
  AST の `ExternalSecret`（`ast-secrets`/`moomoo-*`）が Vault dev から同期できる形にする。
- root トークンは **Secret `vault-dev-token`**（dev 既定 `devroot` or `VAULT_DEV_ROOT_TOKEN` 上書き）から注入し、
  **manifest に平文で置かない**。dev 既定値は postgres/rabbitmq 等の既存 dev secret と同位置づけ（`docs/security/security.md`）。

### 決定4: GitOps はブートストラップ手順＋既存 Application の適用（再 vendoring しない）

- ArgoCD 本体は公式 install manifest を URL 適用。`deploy/local/argocd/` は**手順（README）**のみ置き、既存の
  `deploy/argocd/` の `Application`/`AppProject`（MSP）と、連結時の AST 側（`src/ai-stock-trading/deploy/argocd`）を
  適用する。kustomize の root 外参照制約により既存 manifest の kustomize 取り込みは不可のため、`kubectl apply -f` で行う。

### 決定5: Tier 境界の明示

- Hetzner 実 k3s の本番 stand-up・Vault 本番運用・ArgoCD 実同期・稼働率99%実測は **Tier 3**（対象外）とし、
  本 PR/spec/README に明記する。

## 理由

- 独立オーバーレイ＋env ゲートは「既存を壊さず追加のみ・既定オフ」を構造的に満たし、`kubectl kustomize` で
  オフライン検証できる。二重管理（compose と inline）は kustomize の制約由来で、既存 route-B otel manifest の先例に倣う。
- Vault dev＋`vault-backend` 名一致で、AST の opt-in 秘匿参照が経路B で end-to-end に検証可能になる（平文を出さずに）。

## 結果

- 良い影響: 経路B で観測 UI・Vault・GitOps を opt-in で立てられ、AST #24 のローカル配線検証が成立する。既定は不変。
- 悪い影響 / トレードオフ: compose と config の二重管理が増える（inline）。Vault dev は再起動で揮発（dev 専用）。
  ESO/ArgoCD 本体 install は URL 適用（vendoring しない）ため、CRD 未導入では該当ステップを skip/警告する。
- フォローアップ: Tier 3 の Hetzner 実 stand-up・本番 Vault・実同期。config 二重管理の単一情報源化（将来検討）。
