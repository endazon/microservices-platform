---
title: IADR-0066 ローカル k8s dev 環境は k3d ＋ dev 専用 in-cluster インフラ資産で構成し、mesh/NP/HPA を無効化する
type: impl-adr
status: Accepted
related_ids:
  - ADR-0007
  - ADR-0008
  - ADR-0005
  - IADR-0056
author: claude
created: 2026-07-13
updated: 2026-07-15
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0008_runtime-kubernetes-k3s.md (k3s)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0007_cicd-gitops-argocd.md (GitOps/Helm/Harbor)"
---

# IADR-0066: ローカル k8s dev 環境は k3d ＋ dev 専用 in-cluster インフラ資産で構成する

- 状態: Accepted
- 日付: 2026-07-13
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: ADR-0008（実行基盤 = k3s）／ADR-0007（GitOps・Helm・Harbor）／ADR-0005（Istio mTLS）
- 関連仕様書: `docs/specs/20260713_issue-266_local-k8s-dev-env.md`
- Issue: MSP #266（本 issue）／ AST#122（AST chart）／ AST#121（K8s CronJob）

## コンテキストと課題

残りの AST issue（特に #121 = 取引サイクルを **K8s CronJob** で駆動）を実機で閉じるには、MSP+AST を
連結した **ローカル k8s dev 環境**が要る。現状の資産は次の制約を持つ:

1. Helm chart `deploy/helm/microservices-platform` は **app サービス + MinIO + Wiki.js のみ**をデプロイし、
   Postgres / RabbitMQ / Keycloak / Qdrant / otel-collector を **DNS 参照するだけで自身では起動しない**
   （in-cluster に事前存在する前提）。だが in-cluster インフラの k8s マニフェストは存在せず、実体は
   `deploy/docker-compose.yml` のみ。
2. レジストリは `harbor.internal` 固定（ADR-0007）。ローカルに Harbor は無い。
3. `mesh.enabled`（Istio・ADR-0005）/`networkPolicy`/`scaling`（HPA・metrics-server 依存）は本番前提で、
   素の k3d には Istio が無く、ローカルでは阻害要因になる。
4. AST には k8s 資産が無い（別途 AST#122 で chart 化）。

## 決定

1. **ランタイム = k3s**（ローカル実体）。ADR-0008（k3s）・AST ADR-0006（Hetzner k3s）に忠実。導線は
   **2 経路をサポート**し、スクリプトが自動判定する（`K8S_LOCAL_RUNTIME` で明示可）:
   (a) **Rancher Desktop（推奨）**: 内蔵 k3s をそのまま使う（containerd + `nerdctl`）。Docker Desktop も
   k3d も不要で、最も k3s に忠実。(b) **Docker Desktop + k3d**: k3d が k3s-in-docker を作成。
   いずれも metrics-server 同梱・Windows 対応。kind は k3s 非準拠のため採らない。
2. **dev 専用 in-cluster インフラ資産を新設**する（`deploy/local/`）。`deploy/docker-compose.yml` の設定を
   k8s（Deployment/StatefulSet + Service + ConfigMap/Secret）へ写像し、`platform-infra` namespace に配備する。
   構成要素は Postgres / RabbitMQ / **Redis（BFF の health check・キャッシュ依存。compose の redis を写像）** /
   Keycloak / Qdrant / otel-collector。本番の恒久像（マネージド/専用構成）を規定するものではなく、
   **dev のための最小構成**である。
3. **イメージ配布はランタイム別**（Harbor 不使用）。Rancher 経路は `nerdctl --namespace k8s.io build` で
   k3s の containerd へ直接ビルド（import 不要）、k3d 経路は `docker build` → `k3d image import`。
   `global.image.registry` は values-local でローカル接頭辞へ上書きする（`pullPolicy=IfNotPresent`）。
4. **ローカルでは `mesh.enabled=false` / `networkPolicy.enabled=false` / `scaling.enabled=false`**
   （`deploy/local/values-local.yaml`）。Istio・metrics-server 依存を外す。**本番像（STRICT mTLS・NP・HPA）は
   不変**で、これは dev のみの上書き。
5. 生成物は既存資産を**破壊せず追加**する。`deploy/docker-compose.yml`・chart の templates は変更しない。
   例外として chart の `values.yaml` には下流サービス接続先（`Services__*` の extraEnv）を追記した — これは
   appsettings 既定がローカル用ポート（5001-5009）のままで **k8s 一般で欠落していた接続設定の補完**であり、
   dev 専用値ではない（値は k8s Service の正準 DNS）。dev 専用の上書きは values-local に隔離する。

## 根拠・トレードオフ

- k3d は k3s と同一ディストリで本番差分が小さく、学習・#121 検証の再現性が高い。Docker Desktop k8s より
  軽量で、kind より k3s 忠実。
- in-cluster インフラを compose と別に持つのは二重管理コストがあるが、compose は k8s を代替できない
  （CronJob/Deployment/Service 疎通が #121 の検証対象そのもの）。dev 専用と明示し肥大化を抑える。
- mesh/NP/HPA 無効化は本番のセキュリティ姿勢を弱めない（dev スコープ限定・values 分離）。

## 影響

- 追加: `deploy/local/`（infra マニフェスト（PG/RabbitMQ/Redis/Keycloak/Qdrant/otel）・values-local・
  ExternalName エイリアス）、`scripts/`（up/images/down）。
- 変更: `deploy/helm/microservices-platform/values.yaml`（`Services__*` extraEnv の追記のみ。決定 5 参照）、
  `deploy/keycloak/microservices-platform-realm.json`（dev ユーザー `developer` 追加。dev 専用導線のみが参照）。
- 変更なし: chart の `templates/**`、`deploy/docker-compose.yml`、`deploy/argocd/**`。
- ドキュメント: `docs/operations` / `docs/infra` に手順を追記。

## 代替案

- **Docker Desktop 内蔵 k8s**: 追加ツール不要だが k3s 非準拠でリソース消費が大きい。→ 不採用（k3s 忠実性優先）。
- **compose のまま #121 を検証**: CronJob/Service 疎通が k8s 固有で代替不能。→ 不採用。
- **infra も本番相当の Helm（Operator 等）で導入**: dev には過剰。→ 不採用（dev は最小構成）。

## 補足（後続の部分的見直し）

- **経路B infra の永続化（#324 / [[IADR-0082]]）**: 本 ADR は経路B の infra を `emptyDir`（Pod 再起動で再 init）と
  割り切った。[[IADR-0082]] はこの割り切りを **opt-in（`PERSIST=1`）で部分的に見直し**、Keycloak（realm+runtime state）/
  Postgres を `local-path` PVC で永続化できる選択肢を追加した。**既定は本 ADR どおり `emptyDir` のまま不変**であり、
  本 ADR を Supersede するものではない（全面的な決定の覆しではなく、opt-in の追加）。
