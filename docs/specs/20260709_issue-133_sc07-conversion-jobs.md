---
title: SC-07 変換ジョブ画面実装（Issue #133）
type: spec
status: completed
related_ids:
  - SC-07
  - UC-06
  - FR-12
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
---

# 仕様書: SC-07 変換ジョブ（Issue #133）

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: SC-07 変換ジョブ画面
- ユースケース（UC）: UC-06（変換・正規化の状況確認・人手補正）
- 機能要求（FR）: FR-12（文書正規化）
- 関連 ADR: [[IADR-0042]]（本 PR で作成・変換ジョブ読み取りモデル）、[[IADR-0039]]（管理系ロール）、[[IADR-0029]]（ワーカー最小 HTTP）、[[ADR-0003]]（MassTransit）
- Issue: #133（親 #121）

## 目的・背景

SPA 上に SC-07 を実装する。**ConversionService は fire-and-forget ワーカーで変換ジョブの照会 API を持たなかった**（計画の「バックエンド API 実装済み」前提が変換ジョブでは不成立）。Wave B 方針に従い、本 PR でワーカー側の読み取りモデル・照会/再変換 API・BFF 集約・画面を一貫して実装する。

## 対象範囲

- 対象:
  - ConversionService: `IConversionJobStore`＋インメモリ実装（[[IADR-0042]]。MVP・永続化は follow-up）、コンシューマでの成功／失敗記録（失敗は再送出しリトライ挙動を保持）、`/jobs`（一覧 `?status=`・個別・retry 再変換）エンドポイント。
  - 契約: `Shared.Contracts` に `ConversionJobDto` / `ConversionJobStatus`。
  - BFF: `ConversionBffEndpoints`（admin/operator 限定・Authorization 伝播・後段中継）＋ named client。
  - フロント: `features/sc07-conversions`（`/conversions`・`RequireRole(admin, operator)`・ナビ）。状況一覧・フィルタ・失敗の再変換・成功→SC-03 遷移。
  - テスト: ジョブストア単体（7）、ジョブ照会/再変換エンドポイント（4）、コンシューマ記録ハーネス（成功／失敗 2）、BFF（8）、Vitest（4）。既存コンシューマ／パイプライン登録テストへストア依存を反映。
  - ドキュメント: 本仕様書・画面仕様書・テスト仕様書・IADR-0042。
- 対象外:
  - 変換ジョブの永続化（Postgres+EF）・複数インスタンス共有（[[IADR-0042]] follow-up。別 issue）。
  - 変換出力（Markdown）の手編集 UI（人手補正は再変換に限定）。

## 受け入れ基準（Issue #133）との対応

- [x] 画面仕様書を作成（[SC-07_conversion-jobs.md](../screens/SC-07_conversion-jobs.md)）— 計画・UC-06 と整合。
- [x] 変換ジョブの状況・失敗一覧が表示され、人手補正（再変換）のフローが行える。
- [x] 権限外の情報が表示されない（admin/operator 限定・RequireRole 存在秘匿・BFF 403/401）。
- [x] テスト観点を `docs/tests/SC-07_conversion-jobs.md` へ展開。

## 実装判断・計画フィードバック

- 変換読み取りモデルはインメモリ MVP（[[IADR-0042]] §決定 2）。失敗記録後に例外再送出でリトライ挙動不変（[[ADR-0003]]）。
- **計画ギャップを環流**: 変換ジョブ照会・再変換 API の不在（計画前提の誤り）を IADR-0042 フォローアップに記録。UC-06/05_screens への API 明示を提案する。
