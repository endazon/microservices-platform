---
title: 運用仕様書
type: operations-spec
status: draft
related_ids: []
author: <作成者>
created: <YYYY-MM-DD>
updated: <YYYY-MM-DD>
plan_refs: []
---

# 運用仕様書

> 必須ドキュメント（リポジトリ単位）。本リポジトリの運用を定める。雛形は `docs/templates/operations_spec_template.md`。
> **未記入のまま放置しない**。デプロイ・監視・バックアップ・障害対応を埋めること。

## 起点となる計画書（トレーサビリティ）

- 非機能要件（NFR・運用/可用性）:
- 関連 ADR / 技術検討:

## デプロイ

| 項目 | 内容 |
| --- | --- |
| 環境 | dev / stg / prod |
| 手順 |  |
| ロールバック |  |

### サービス構成に関する運用注記

- **WikiService と Wiki.js**（FR-13 / UC-07 / [IADR-0013](../adr/IADR-0013_wiki-selfhosted-read-api-supersedes-adr-0011.md)）:
  Wiki 閲覧は `WikiService` が自前 DB（`wiki_svc`）で提供する**自前の軽量読み取り専用 API** である。
  外部 OSS の **Wiki.js は意図的に配備しない**（`deploy/docker-compose.yml`・`deploy/helm/` に含めない）。
  計画 ADR-0011 は当初 Wiki.js 採用を決定していたが、認可（ABAC）の二重管理回避・要件（閲覧のみ）への適合の
  ため自前 API を採用し、ADR-0011 の Supersede を `/plan-feedback`
  （[記録](../../feedback/20260703_wiki-selfhosted-supersedes-adr-0011.md)）で提案済み。
  監査（`adr-guardian`）で Wiki.js 不在を検出した場合は、逸脱ではなく本設計判断である点に留意する。
  WikiService は独立サービス（独自 DB・Dockerfile）として個別デプロイ・ロールバック可能（受け入れ基準④）。

## 監視・アラート

| 監視対象 | 指標 | 閾値 | 通知先 |
| --- | --- | --- | --- |
|  |  |  |  |

## バックアップ・リストア

<!-- 対象・頻度・保管期間・リストア手順・RPO/RTO -->

## 障害対応（Runbook）

| 事象 | 検知 | 一次対応 | エスカレーション |
| --- | --- | --- | --- |
|  |  |  |  |

## 未決事項
