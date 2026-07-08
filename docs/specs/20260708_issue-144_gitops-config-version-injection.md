---
title: 作業仕様書 — GitOps 構成バージョン注入（Config:GitCommit/AppliedAt/AppliedBy）
type: spec
status: done
related_ids:
  - FR-15
  - ADR-0018
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
related_specs:
  - ../adr/IADR-0029_config-info-api-placement-and-drift-granularity.md
  - ../operations/operations.md
---

# 作業仕様書: GitOps 構成バージョン注入

Issue: #144（親: #123 ／ IADR-0029 フォローアップ 3 ／ #118 監査論点 3: 現状 gitCommit 空）。

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-15（構成バージョン＝Git コミット ID・適用日時・適用者の取得）
- 関連 ADR: ADR-0018・ADR-0007（GitOps: ArgoCD + Helm）・IADR-0029

## 目的・背景

FR-15 は構成バージョン（Git コミット ID・適用日時・適用者）を要求するが、注入が未配線で
`gitCommit` が常に空（#118 監査）。**コード側の消費は実装・テスト済み**（`ConfigVersionOptions` →
`ConfigInspectionService.BuildVersion` → `/bff/admin/config` の `Version`。`ConfigBffEndpointTests`
が `GitCommit`/`AppliedBy` を検証）。本 issue は **GitOps（Helm/ArgoCD）と compose での注入配線と文書化**を行う。

## 方針（決定）

- **Helm/k8s（本番・stg）**: values に `config.gitCommit/appliedAt/appliedBy` を設け、BFF Deployment へ
  `Config__GitCommit/AppliedAt/AppliedBy` として注入する。実値は ArgoCD Application の `helm.parameters`
  および CD パイプラインが適用リビジョンから供給する（`appliedBy: argocd` を既定）。
- **compose（dev）**: compose 起動時に**環境変数で実 Git コミット ID を注入**する
  （`Config__GitCommit=${GIT_COMMIT:-dev-local}` ほか）。ヘルパ `scripts/compose-up.sh` が
  `GIT_COMMIT`/`GIT_COMMIT_DATE`/`GIT_COMMIT_BY` を自動注入。未設定時は `dev-local` へフォールバック。

## 対象範囲

- 対象:
  1. `values.yaml` に `config:` ブロック追加、`bff` に `configVersion: true`。
  2. `templates/deployment.yaml` に `configVersion` 条件で `Config__*` を注入。
  3. `deploy/argocd/application.yaml` に `helm.parameters`（`config.appliedBy=argocd`）と、gitCommit/appliedAt を
     適用リビジョンから供給する運用手順の参照を追加。
  4. `deploy/docker-compose.yml` の BFF に**環境変数で実 Git コミット ID を注入**（`Config__GitCommit=${GIT_COMMIT:-dev-local}`
     ほか）。ヘルパ `scripts/compose-up.sh` が `GIT_COMMIT`/`GIT_COMMIT_DATE`/`GIT_COMMIT_BY` を自動注入。
  5. `docs/operations/operations.md` に注入方法（Helm/ArgoCD/compose）を記録。
- 非対象: コード変更（消費は実装済み）・即時検出（#145）。

## 受け入れ基準

- [x] Helm デプロイで構成バージョンが実値（`--set config.*` / ArgoCD parameters）で返る配線がある。
- [x] dev（compose）での挙動が決定・文書化されている（起動時に環境変数で実 Git コミット ID を注入。未設定は
      `dev-local` フォールバック）。
- [x] 注入方法が運用仕様書（operations.md）に記録されている。
- [x] `helm template` で BFF に `Config__*` が注入され、既存 BFF テストが緑。

## テスト

- 消費側は既存 `ConfigBffEndpointTests.GetConfig_AsAdmin_ReturnsEffectiveConfigWithVersion` が検証済み。
- `helm template --set config.gitCommit=deadbeef` で BFF env に反映されることを確認する。
