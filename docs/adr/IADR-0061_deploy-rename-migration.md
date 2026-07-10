---
title: IADR-0061 デプロイ資産（Helm/k8s/realm/イメージ）の改名は Blue/Green 移行で行う（新名称は要確認・実行は stg 検証後）
type: impl-adr
status: Proposed
related_ids:
  - FR-14
  - ADR-0007
  - ADR-0008
  - IADR-0056
author: claude
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0007_cicd-gitops-argocd.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0008_runtime-kubernetes-k3s.md"
---

# IADR-0061: デプロイ資産の改名は Blue/Green 移行で行う（移行手順の起草）

- 状態: Proposed（新名称の確定と stg 検証をもって Accepted 化する）
- 日付: 2026-07-11
- 決定者: claude（実装・提案）

## 起点・関連

- 関連する計画書 ID: FR-14／ADR-0007（ArgoCD+Helm）／ADR-0008（k3s）／[[IADR-0056]]（ユニット第一構成）
- 関連仕様書: `docs/specs/20260711_issue-228_rename-migration.md`、`docs/migration/rename-knowledge-platform.md`（Runbook）
- Issue: #228（IADR-0056 フォローアップ 2）

## コンテキストと課題

Helm チャート名 `knowledge-platform`・k8s Namespace `knowledge-platform`・Keycloak realm `knowledge-platform`・
コンテナイメージのプロジェクト接頭辞 `knowledge-platform/*`・Ingress ホスト `*.knowledge-platform.local`・
ArgoCD Application/releaseName・観測資産（Grafana/Prometheus）・アプリ設定（OIDC realm/authority）が、
「主=プラットフォーム基盤」の位置づけ（#209 / [[IADR-0056]]）と不整合のまま `knowledge-platform` を名乗る。

改名は**デプロイ済み環境への影響が大きい**（Namespace は in-place 改名不可＝再作成、PVC のデータ移行、
Keycloak realm 変更に伴う issuer/authority の総入替、イメージ再タグ、ArgoCD の付け替え）。受け入れ基準は
「stg で検証済み」であり、**実行には実環境が要る**。本 IADR は移行方式を定め、Runbook を起草する（実改名は行わない）。

## 検討した選択肢

### 新名称
- **候補A: `microservices-platform`（リポジトリ名に一致・本 IADR の推奨）** — 製品全体＝プラットフォーム基盤である
  ことを明示。曖昧さがなく、k8s Namespace/realm 名（63 文字制限）にも収まる。
- 候補B: `platform` — 短いが汎用的すぎ、他システムと衝突しやすい。
- 候補C: 現状維持 — #209 の位置づけと不整合が残る。

> **新名称はプロダクト/ブランドの決定事項**のため、Runbook は `<new-name>` でパラメータ化し、実行前に確定する
> （推奨は `microservices-platform`）。

### 移行方式
- **選択肢1: in-place（同一 Namespace 内で値だけ差し替え）** — Namespace/realm 名は in-place 改名できず、
  ダウンタイムとデータ移行リスクが高い。低リスク環境（dev）向け。
- **選択肢2: Blue/Green（新 Namespace＋新 realm＋新イメージを並行構築し、ingress/DNS で切替。旧を保持しロール
  バック可能に）（本推奨）** — ステートフル（postgres/qdrant/minio/wikijs）のデータ移行を計画的に行え、
  無停止・即時ロールバックが可能。stg→prod の段階適用に適する。

### Keycloak realm 改名
- realm は Keycloak 管理 API で改名可能だが **issuer URL（`/realms/<name>`）が変わり、既存トークンは失効**、
  全クライアント（`spa-web` public client・各サービスの authority）の設定更新が必要。
- 本推奨: **新 realm を export/import で新名称にて構築**（Blue/Green と整合。旧 realm は切替まで保持）。

## 決定（提案）

1. **新名称は `<new-name>`（推奨 `microservices-platform`）でパラメータ化**し、実行前に確定する。
2. **移行方式は Blue/Green を基本**とする（stg/prod）。dev は in-place を許容する。
3. **Keycloak は新 realm を新名称で構築**（export/import）し、OIDC authority（Helm `values.yaml` の
   `oidc.authority`・各サービス `appsettings.json`・SPA `config.js`）を新 issuer へ更新する。
4. **ArgoCD は新 Application**（新 releaseName・新 destination namespace・改名後チャートパス）を作成し、
   ingress 切替後に旧 Application を削除する。
5. **イメージは新プロジェクト接頭辞 `<new-name>/*`** で再タグ・再 push し、`values.yaml` の `image` を更新する。
6. 上記の全対象・手順・ロールバック・検証チェックリストを Runbook（`docs/migration/rename-knowledge-platform.md`）に
   起草する。**本 PR では実ファイルの改名は行わない**（stg 検証を伴う実行は別途）。

## 理由

- **影響の大きさに見合う安全性**: Namespace 再作成・realm 変更・データ移行を伴うため、無停止・ロールバック可能な
  Blue/Green が妥当（in-place はダウンタイムと不可逆リスク）。
- **受け入れ基準との整合**: 「stg で検証済み」が条件のため、起草（本 PR）と実行（stg での適用・検証）を分離する。
- **名称のパラメータ化**: 新名称は製品判断のため、方式・手順を先に固め、名称確定後に機械置換で実行できるようにする。

## 結果（本 PR の範囲）

- `docs/adr/IADR-0061`（本書・Proposed）。
- `docs/migration/rename-knowledge-platform.md`: 改名対象の全インベントリ・Blue/Green 手順・ロールバック・
  stg 検証チェックリスト。
- 実ファイル（Helm/realm/argocd/compose/appsettings 等）の改名は**未実施**（#228 に残す）。

## フォローアップ（#228・実環境が必要）

- 新名称の確定（プロダクト判断）。
- stg での Blue/Green 適用・データ移行・OIDC 疎通・ingress 切替・検証、続いて prod。
- 実行後に本 IADR を Accepted 化し、旧名称資産の撤去を記録。

## 関連

- Supersedes: なし
- Superseded by: なし
