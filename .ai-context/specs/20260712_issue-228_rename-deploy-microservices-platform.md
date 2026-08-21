---
title: デプロイ資産・realm・OTEL・OIDC を microservices-platform へ改名する（Issue #228・移行なし）
type: spec
status: done
related_ids:
  - FR-14
  - IADR-0056
  - IADR-0061
author: claude
created: 2026-07-12
updated: 2026-07-12
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0007_cicd-gitops-argocd.md
  - planning:projects/microservices-platform/07_adr/ADR-0008_runtime-kubernetes-k3s.md
related_specs:
  - "../adr/IADR-0061_deploy-rename-migration.md"
  - "../../docs/migration/rename-knowledge-platform.md"
---

# 仕様書: デプロイ資産の改名 `knowledge-platform` → `microservices-platform`（Issue #228）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-14／IADR-0056（ユニット第一構成・「主=プラットフォーム基盤」）
- 実装判断: [IADR-0061](../adr/IADR-0061_deploy-rename-migration.md)（改定・Accepted。移行なし・新名称で初回構築）
- Issue: #228（IADR-0056 フォローアップ 2）

## 目的・背景

デプロイ資産・realm・観測・OIDC・OTEL の命名が `knowledge-platform` のままで「主=プラットフォーム基盤」（#209）と
不整合だった。**stg/prod は未構築**のため移行（Blue/Green・データ移行・ロールバック）は不要とし、ソース／デプロイ資産の
純粋な改名として `microservices-platform` へ統一する（初回構築を新名称で行う。旧資産は撤去）。

## 対象範囲（機械置換 `knowledge-platform` → `microservices-platform` ＋ ファイル/ディレクトリ改名）

- **ディレクトリ/ファイル改名**: `deploy/helm/knowledge-platform/` → `deploy/helm/microservices-platform/`、
  `deploy/keycloak/knowledge-platform-realm.json` → `microservices-platform-realm.json`、
  `deploy/grafana/provisioning/dashboards/knowledge-platform-overview.json` → `microservices-platform-overview.json`。
- **Helm**: `Chart.yaml` `name`、全テンプレート（Namespace・ラベル `app.kubernetes.io/part-of`・istio `*-mtls`・
  NetworkPolicy・pipeline-config・drift job）、`values.yaml`（`namespace.name`・`image` 接頭辞 `microservices-platform/*`・
  Ingress ホスト `*.microservices-platform.local`）。
- **Keycloak**: realm 名 `microservices-platform`。
- **ArgoCD**: `application.yaml`/`appproject.yaml`（name・project・destination namespace・part-of）。
- **観測**: Grafana ダッシュボード（uid/tags/service_name 参照）、Prometheus `alerts.yml`（service_name 接頭辞）。
- **Compose/bootstrap/istio**: `docker-compose.yml`・`bootstrap/*`・`istio/README.md`。
- **アプリ設定・コード**: 全サービス `appsettings*.json` の OIDC authority（`/realms/microservices-platform`）、
  各 `Program.cs` の OTEL `ServiceName`（`microservices-platform.<service>`）、`Platform.Shared.Infrastructure`
  の authority 既定値、フロント `config.js`/`runtimeConfig.ts`/`Dockerfile`/`docker-entrypoint`（authority・
  コンテナ内パス `/etc/microservices-platform/`）。
- **CI/テスト**: `.github/workflows/ci.yml`（Helm パス）、`Knowledge.IntegrationTests/Deployment/*`（Namespace/realm 期待値）。
- **現行ドキュメント**: `README.md`（改名済みの注記へ）・`docs/operations`・`docs/how-to`・`docs/tech`・`docs/security`・
  `docs/functional/FR-14`・`docs/screens/SC-11`・`docs/tests/NFR-01`・[IADR-0061](../adr/IADR-0061_deploy-rename-migration.md)（→ Accepted）・移行 Runbook（完了記録へ）。

## 対象外（点在する時点記録）

- `docs/specs/`（日付付きスライス仕様）・`docs/superpowers/`・`feedback/`・過去 ADR（IADR-0020/0026/0028/0030/0050）・
  `docs/tech/20260707_wikijs-poc-record.md`・`planning/`（submodule）は**作業・決定時点の記録**のため本文は変更しない
  （リポジトリの既存方針＝#238 と同じ）。ただし過去記録内の**相対リンクが改名で破損する箇所のみ**リンクパスを是正
  （IADR-0028・20260709 実装ガイドの Helm README リンク）。

## 受け入れ基準（Issue #228）との対応

- [x] デプロイ資産の命名がユニット構成（platform 主体）と整合する → 全デプロイ資産・realm・観測・OIDC・OTEL を
  `microservices-platform` へ統一。旧 `knowledge-platform` 資産は撤去。
- [x] 移行手順が docs に記録（[IADR-0061](../adr/IADR-0061_deploy-rename-migration.md)・移行 Runbook 完了記録）。「stg で検証済み」は **stg 未構築のため対象外**
  （初回構築を新名称で行う運用に IADR-0061 を改定）。

## 検証

- `dotnet build`（platform / knowledge）→ 0 エラー。`dotnet format --verify-no-changes`（両 slnx）→ 差分なし。
- `dotnet test`: AuthorizationService.Api.Tests 51 pass、`Knowledge.IntegrationTests` の Deployment テスト 15 pass
  （改名後の Helm テンプレート/Namespace/realm を検証）。
- フロント: `npm run typecheck` / `npm run lint` / `npm run test`（120 pass）緑。
- `helm lint deploy/helm/microservices-platform` → 0 failed。`node scripts/validate-pipeline-config.js deploy/helm/microservices-platform/files/pipeline.json` → OK。
- `node scripts/check-unit-dependencies.js` 違反 0、`node scripts/check-doc-links.js` 破損 0。

## 実装判断・フォローアップ

- 移行不要の判断（stg/prod 未構築）に伴い IADR-0061 を Blue/Green から「純粋な改名・新名称で初回構築」へ改定し Accepted 化。
- 実際の stg/prod 構築は環境構築 issue で扱い、新名称での ArgoCD 同期・OIDC 疎通・ダッシュボード表示を確認する。
