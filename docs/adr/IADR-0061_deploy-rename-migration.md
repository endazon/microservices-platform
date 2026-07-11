---
title: IADR-0061 デプロイ資産（Helm/k8s/realm/イメージ/OIDC）を microservices-platform へ改名する（stg 未構築のため移行なし・初回構築を新名称で行う）
type: impl-adr
status: Accepted
related_ids:
  - FR-14
  - ADR-0007
  - ADR-0008
  - IADR-0056
author: claude
created: 2026-07-11
updated: 2026-07-12
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0007_cicd-gitops-argocd.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0008_runtime-kubernetes-k3s.md"
---

# IADR-0061: デプロイ資産を microservices-platform へ改名する（移行なし・新名称で初回構築）

- 状態: Accepted（2026-07-12。新名称 `microservices-platform` を確定し、改名を実施）
- 日付: 2026-07-11（初版 Proposed・Blue/Green 起草）／2026-07-12（Accepted・移行なし方針へ改定）
- 決定者: ユーザー（プロダクト判断）＋ claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-14／ADR-0007（ArgoCD+Helm）／ADR-0008（k3s）／[[IADR-0056]]（ユニット第一構成）
- Issue: #228（IADR-0056 フォローアップ 2）

## コンテキストと課題

Helm チャート名 `knowledge-platform`・k8s Namespace `knowledge-platform`・Keycloak realm `knowledge-platform`・
コンテナイメージのプロジェクト接頭辞 `knowledge-platform/*`・Ingress ホスト `*.knowledge-platform.local`・
ArgoCD Application/releaseName・観測資産（Grafana/Prometheus の service_name 接頭辞・ダッシュボード名）・
アプリ設定（OIDC realm/authority）・OTEL サービス名接頭辞（`knowledge-platform.<service>`）が、
「主=プラットフォーム基盤」の位置づけ（#209 / [[IADR-0056]]）と不整合のまま `knowledge-platform` を名乗っていた。

## 決定（改定後・2026-07-12）

初版（Proposed）は「デプロイ済み環境がある」前提で Blue/Green 移行を起草していた。しかし **stg/prod は未構築**で
あり、移行対象の稼働資産は存在しない。したがって移行（Blue/Green・データ移行・ロールバック）は不要であり、
**ソース／デプロイ資産の純粋な改名**として実施し、初回構築を新名称で行う。

1. **新名称は `microservices-platform`（リポジトリ名に一致）で確定**する。製品全体＝プラットフォーム基盤である
   ことを明示し、k8s Namespace/realm（63 文字制限）にも収まる。
2. **移行は行わない**。stg/prod 未構築のため稼働データ・トークン失効・ingress 切替の考慮は不要。旧 `knowledge-platform`
   資産（チャートディレクトリ・realm ファイル・ダッシュボード等）は**すべて撤去**し、新名称で作り直す。
3. 改名対象（本 PR で機械置換・ファイル/ディレクトリ改名を実施）:
   - Helm チャート: ディレクトリ `deploy/helm/knowledge-platform/` → `deploy/helm/microservices-platform/`、
     `Chart.yaml` `name`、全テンプレート（Namespace・ラベル `app.kubernetes.io/part-of`・PeerAuthentication/
     DestinationRule `*-mtls`・NetworkPolicy・pipeline-config・drift job）、`values.yaml`（`namespace.name`・
     `image` 接頭辞・Ingress ホスト）。
   - Keycloak realm: `deploy/keycloak/knowledge-platform-realm.json` → `microservices-platform-realm.json`、
     realm 名 `knowledge-platform` → `microservices-platform`。
   - ArgoCD: `deploy/argocd/{application,appproject}.yaml`（name・project・destination namespace・part-of ラベル）。
   - 観測: Grafana ダッシュボード `knowledge-platform-overview.json` → `microservices-platform-overview.json`
     （uid/tags/service_name 参照）、Prometheus `alerts.yml`（service_name 接頭辞）。
   - Compose/bootstrap/istio: `deploy/docker-compose.yml`・`deploy/bootstrap/*`・`deploy/istio/README.md`。
   - アプリ設定・コード: 全サービス `appsettings*.json` の OIDC authority（`/realms/microservices-platform`）、
     各 `Program.cs` の OTEL `ServiceName`（`microservices-platform.<service>`）、`Platform.Shared.Infrastructure`
     の authority 既定値、フロント `config.js`/`runtimeConfig.ts`/`Dockerfile`/`docker-entrypoint`（authority・
     コンテナ内パス `/etc/microservices-platform/`）。
   - CI: `.github/workflows/ci.yml`（Helm パス）。デプロイ検証統合テスト（`Knowledge.IntegrationTests/Deployment/*`）の
     Namespace/realm 期待値。

## 理由

- **移行対象が存在しない**: stg/prod 未構築のため、無停止・ロールバックの考慮は不要。純粋な改名として最小コストで
  一貫適用できる。
- **一貫性**: OTEL service_name 接頭辞・Grafana/Prometheus クエリ・OIDC issuer を同時に改名し、観測と認証の整合を保つ。
- **#209 / IADR-0056 との整合**: 「主=プラットフォーム基盤」の位置づけにデプロイ資産の命名を合わせる。

## 結果

- 旧 `knowledge-platform` のデプロイ資産・realm・ダッシュボード・OTEL 名・OIDC issuer をすべて `microservices-platform`
  へ改名し、旧名称の資産は撤去した（本 PR）。Blue/Green 移行 Runbook（`docs/migration/rename-knowledge-platform.md`）は
  移行を行わない方針により不要となったため、完了記録へ差し替えた。
- 受け入れ基準「デプロイ資産の命名がユニット構成（platform 主体）と整合する」を満たす。「stg で検証済み」は stg 未構築の
  ため対象外（初回構築を新名称で行う運用に変更）。

## フォローアップ

- 実際の stg/prod 構築時に、新名称で ArgoCD 同期・OIDC 疎通・観測ダッシュボードの表示を確認する（環境構築 issue で扱う）。

## 関連

- Supersedes: なし
- Superseded by: なし
