---
title: MSP+AST 連結 ローカル k8s(k3d) dev 環境の構築（Issue #266）
type: spec
status: draft
related_ids:
  - ADR-0007
  - ADR-0008
  - IADR-0056
  - IADR-0066
author: claude
created: 2026-07-13
updated: 2026-07-13
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0008_runtime-kubernetes-k3s.md (実行基盤 k3s)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0007_cicd-gitops-argocd.md (GitOps/Helm)"
related_specs:
  - "../adr/IADR-0066_local-k8s-dev-environment.md"
  - "../operations/operations.md"
  - "../../deploy/helm/microservices-platform/values.yaml"
  - "../../deploy/bootstrap/README.md"
---

# 仕様書: MSP+AST 連結 ローカル k8s(k3d) dev 環境の構築（Issue #266）

> 本仕様書は実装着手前に作成する。計画書（`project-planning`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-14（構成変更で完結する疎結合ユニット。AST 連結の前提）
- ユースケース（UC）: —（基盤整備）
- 画面（SC）: —
- 関連 ADR: ADR-0008（実行基盤 = Kubernetes k3s）／ADR-0007（GitOps・Helm・Harbor）／ADR-0005（Istio mTLS）
- 実装判断: [[IADR-0066]]（ローカル k8s dev 環境 = k3d・in-cluster インフラ資産・mesh/NP/HPA 無効化・イメージ import）
- Issue: MSP #266（本 issue）／ AST #122（AST k8s chart）／ 主目的 AST #121（K8s CronJob）／ AST #24・#22

## 目的・背景

残りの AST issue（特に **#121 本番スケジューラ=K8s CronJob**）を実機で閉じるため、MSP を
**ローカル k8s（k3d）** 上で **MSP+AST 連結**の dev 環境として立ち上げる。

調査で判明した前提（実リポ裏取り）:

- Helm chart `deploy/helm/microservices-platform` は **app サービス + MinIO + Wiki.js のみ**をデプロイし、
  **インフラ（Postgres / RabbitMQ / Keycloak / Qdrant / otel-collector / 観測系）は DNS 参照のみ**で
  自身ではデプロイしない（クラスタ事前存在前提）。k8s マニフェストは存在せず実体は `deploy/docker-compose.yml`。
- AST（`src/ai-stock-trading`）は **k8s 資産ゼロ**（`backend/Dockerfile` ＋ 独自 `docker-compose.yml` のみ）。
- レジストリは `harbor.internal` 固定。`mesh`(Istio)/`networkPolicy`/`scaling`(HPA) はローカル k3d に不向き。

したがって本作業は「既存資産の起動」ではなく、**(A) in-cluster インフラ資産と (B) AST の k8s chart** の
新規作成を伴う。AST 側の chart は AST #122 で AST リポに実装し、本 MSP 側 issue と連結する。

## 対象範囲

**対象（MSP 側 / 本 PR 群）**
- `deploy/local/` に dev 専用 **in-cluster インフラ**（PG / RabbitMQ / Keycloak / Qdrant / otel-collector）マニフェスト。
- `deploy/local/values-local.yaml`（`mesh.enabled=false` / `networkPolicy.enabled=false` /
  `scaling.enabled=false` / `global.image.registry=<local>` 上書き）。
- イメージ build ＋ `k3d image import` スクリプト（`scripts/`）。
- bootstrap secret テンプレ拡充（`minio-credentials`・infra 資格情報）。fail-safe 既定。
- 手順ドキュメント（`docs/operations` / `docs/infra`）。
- [[IADR-0066]] と本作業仕様書。

**対象（AST 側 / AST #122・別 PR）**
- AST 10 Worker の Helm chart、DB-per-service、realm、**取引サイクル CronJob 骨子（#121）**、
  外部連携 env（Anthropic/Finnhub/Discord/moomoo **simulate**、fail-safe 既定）。

**対象外（実 external 依存・fail-safe no-op のまま）**
- AST #13 moomoo 実弾発注（dev は simulate 口座＋OpenD のみ／実資金は動かさない）。
- AST #79 実 LLM 費用・#81 実市場データ・#15 Discord は「有効化は明示設定時のみ」。
- AST #24 の Hetzner リージョン実測・Vault・実費用（実インフラ依存）。
- AST #49（相場操縦検知アルゴリズム。k8s 非依存）。

## 設計

### トポロジ

```
[k3d cluster: msp-ast-dev]  (Docker Desktop/WSL2)
  ns platform-infra          → postgres, rabbitmq, keycloak, qdrant, otel-collector (+任意 観測系)
  ns microservices-platform  → 既存 Helm chart（values-local: mesh/NP/HPA off, registry=local）
  ns ai-stock-trading        → AST chart（10 Worker + DB-per-service + CronJob 骨子）
  ingress: Traefik(k3d 同梱)  → bff / keycloak / grafana を port 公開
```

### 連結点

- インフラ共有: PG（DB-per-service）・RabbitMQ（MassTransit）・Keycloak（realm 2 本: `microservices-platform` /
  `ai-stock-trading`）・otel-collector を両 ns から参照。
- AST→MSP の s2s/LLM egress は現状未配線（#79）。fail-safe no-op のまま。`ai-stock-trading-svc` は将来値。

### イメージ配布

- ローカルレジストリより **`k3d image import`** を既定（Harbor 不要・設定単純）。全 MSP/AST Dockerfile を build → import。

### フロー

クラスタ作成 → build/import → Secret 投入 → infra 適用 → MSP chart 適用 → AST chart 適用 → health/疎通確認。

## 受け入れ基準

- [ ] `k3d cluster create` → build/import → secret → infra → MSP chart で **全 MSP Pod が Ready**
- [ ] AST 10 Worker が Ready、共有インフラへ疎通、各専有 DB へ接続（AST #122）
- [ ] `mesh/NP/HPA` 無効化でも既存 chart がレンダリング・起動する（values-local）
- [ ] BFF / Keycloak / Grafana へ到達でき health が緑
- [ ] 取引サイクル CronJob が起動でき、未設定時は in-process にフォールバック（AST #121）
- [ ] 秘密情報は Git に含まれず、fail-safe 既定（未設定=no-op / paper）で安全に起動
- [ ] 手順が `docs/` に記載される

## テスト方針

- Chart のレンダリング検証（`helm template`）を CI/ローカルで実施し、values-local で全テンプレートが有効な
  マニフェストを生成することを確認。
- infra マニフェストの `kubectl apply --dry-run=server`（可能なら kind/k3d 上）で妥当性確認。
- 疎通は health エンドポイント（BFF `/health/*`）・RabbitMQ 管理 UI・Keycloak realm 到達で確認。
- CronJob は短周期スケジュールで手動トリガー（`kubectl create job --from=cronjob/...`）し、起動と
  未設定フォールバックを確認。

## 計画書との差異

- 差異: あり（限定的）。
  - 計画（ADR-0005 Istio mTLS / networkPolicy / HPA）はローカル k3d では無効化する。これは**本番像を
    変更するものではなく、dev 環境のみの上書き**（values-local）であり、恒久像は不変。IADR-0066 に根拠を記録。
  - AST #24 の受け入れ基準のうち「k8s デプロイ構成が存在し GitOps 適用できる」はローカルで実証するが、
    Hetzner/Vault/実費用は分離（/plan-feedback で計画へ環流予定）。

## 未決事項

- ローカル観測系（prometheus/loki/tempo/grafana）を in-cluster に含めるか（既定: otel-collector までを必須、
  観測 UI は任意）。→ 段階導入とし、初期は otel-collector のみ必須で進める。
- ingress（Traefik）で公開するか port-forward に留めるか。→ 初期は port-forward、必要に応じ ingress 追加。
