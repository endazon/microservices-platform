---
title: Helm/k8s/realm 名の改名 — 移行手順の起草（Issue #228・実行は stg 検証後）
type: spec
status: done
related_ids:
  - FR-14
  - ADR-0007
  - ADR-0008
  - IADR-0056
  - IADR-0061
author: claude
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0007 (ArgoCD + Helm)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0008 (k3s)"
related_specs:
  - "../adr/IADR-0061_deploy-rename-migration.md"
  - "../migration/rename-knowledge-platform.md"
---

# 仕様書: デプロイ資産の改名 — 移行手順の起草（Issue #228）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-14／関連 ADR-0007（ArgoCD+Helm）・ADR-0008（k3s）・IADR-0056（ユニット第一構成）
- 実装判断: [[IADR-0061]]（Blue/Green 移行・新名称パラメータ化・実行繰延）
- Issue: #228（フォローアップ 2）

## 目的・背景

デプロイ資産（Helm チャート名・k8s Namespace・Keycloak realm・イメージ接頭辞・Ingress ホスト・ArgoCD・
観測・アプリ OIDC 設定）が `knowledge-platform` を名乗り、「主=プラットフォーム基盤」の位置づけ（#209）と
不整合。改名は**デプロイ済み環境への影響が大きく、受け入れ基準が「stg で検証済み」**のため、本リポジトリ内で
完結できる範囲＝**移行方式の決定（IADR）と Runbook の起草**までを行う（実改名は行わない）。

## 対象範囲

- 対象（新規）:
  - `docs/adr/IADR-0061`（移行方式・新名称・ロールバック。status: Proposed）。
  - `docs/migration/rename-knowledge-platform.md`（全インベントリ・Blue/Green 手順・ロールバック・検証チェックリスト）。
  - `docs/specs/20260711_issue-228`（本仕様書）。
- 対象外（[[IADR-0061]] フォローアップ・#228 に残す。実環境が必要）:
  - **実ファイルの改名**（Helm/realm/argocd/compose/appsettings/CI パス等）。
  - stg での Blue/Green 適用・データ移行・OIDC 疎通・ingress 切替・検証、prod 展開。
  - 新名称の確定（プロダクト判断。推奨 `microservices-platform`）。

## 実装方針

- 改名対象を `grep -rn knowledge-platform` で網羅し分類（Runbook §1）。
- Namespace/realm は in-place 改名不可のため **Blue/Green**（新 Namespace＋新 realm＋新イメージを並行構築し
  ingress/DNS で切替、旧を保持しロールバック可能に）を基本とする（IADR-0061）。
- 新名称は製品判断のため `<new-name>` でパラメータ化し、実行前に確定する。

## 受け入れ基準（Issue #228）との対応

- [~] デプロイ資産の命名がユニット構成（platform 主体）と整合する
  → **方式・対象・手順を確定（起草）**。実改名は stg 検証を伴うため未実施（#228 に残す）。
- [x] 移行手順が docs/operations に記録され、stg で検証済み
  → **移行手順を `docs/migration/` に記録（起草）**。**stg 検証は実環境が必要のため未実施**（#228 に残す）。
  本 PR は `Refs #228`（Closes ではない）。

## 検証

- `node scripts/check-doc-links.js` → 破損 0（IADR/Runbook/spec の相互リンク実在）。
- インベントリの網羅性を `grep -rc knowledge-platform deploy/` の結果と突合。

## 実装判断・フォローアップ

- 方式（Blue/Green・realm export/import・ArgoCD 新 Application・イメージ再タグ）は [[IADR-0061]] に記録。
- 実改名・stg 検証・新名称確定は #228 に残す（実環境/プロダクト判断）。必要なら実行専用の別 issue 化を検討。
