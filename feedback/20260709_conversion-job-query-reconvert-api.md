---
title: 変換ジョブ照会/再変換 API と状態モデルを計画（UC-06 / SC-07）へ明記
type: plan-feedback
status: accepted
category: 要求の不足
related_ids: [FR-12, UC-06, SC-07, ADR-0002, ADR-0003, IADR-0042, IADR-0043]
source_repo: microservices-platform
source_ref: "PR #172 / IADR-0042 / IADR-0043 / docs/screens/SC-07_conversion-jobs.md"
author: claude
created: 2026-07-09
---

> **［2026-08-04］反映済み。** 計画側が planning#189 / planning#191 のトリアージで本記録を受理し、
> [05_screens/01_screens.md](../planning/projects/microservices-platform/05_screens/01_screens.md) の
> §変更履歴 が本記録を名指しして SC-07（変換ジョブ）の記述へ反映した
> （planning `d980a01` / PR planning#194）。**実装側に残作業は無い。**
# フィードバック: 変換ジョブ照会/再変換 API と状態モデルを計画（UC-06 / SC-07）へ明記

## 種別

要求の不足（Wave B 前提「バックエンド API 実装済み」が変換ジョブでは不成立）。

## 起点となる計画書

- 機能要求（FR）: FR-12（文書正規化・変換）
- ユースケース（UC）: UC-06（変換・正規化の状況確認・人手補正）
- 画面（SC）: SC-07（変換ジョブ）
- 関連 ADR: ADR-0003（MassTransit）／ADR-0002（サービスごとの DB）
- 計画書リンク: `03_usecases/01_usecases.md (UC-06)` / `05_screens/01_screens.md (SC-07)`

## 現状（計画書の記述 / As-Is）

- SC-07 の画面計画は「バックエンド API は実装済み」を Wave B 前提としていたが、`ConversionService` は
  **イベント駆動の fire-and-forget ワーカー**で、変換ジョブの**照会/再変換 API を持っていなかった**
  （失敗はデッドレターのみで、状況一覧・失敗一覧・人手補正の手段が無い）。
- `03_usecases`（UC-06）・`05_screens`（SC-07 のデータソース＝API）に、変換ジョブの照会/再変換 API と
  ジョブ状態モデル（queued/processing/succeeded/failed）が**未記載**。

## 問題点 / あるべき姿（To-Be）

- UC-06（状況確認・人手補正）を満たすには、変換ジョブの**読み取りモデル**（状況一覧・失敗一覧）と
  **再変換（retry）**の API がバックエンドに必要。計画にこの IF と状態モデルを明記すべき。
- 「Wave B のバックエンド API は実装済み」という前提が、少なくとも変換ジョブでは成立しなかった点を記録する。

## 実装で判明した経緯

- SC-07（#133 / PR #172）実装時に、照会/再変換手段の不在が判明。ワーカー側に読み取りモデル
  `IConversionJobStore` と `/jobs` 照会・`retry` API を新設して対応した（[[IADR-0042]]）。
- 当初 MVP はインメモリ実装だったが、永続性・水平スケール・監査の制約から Postgres+EF へ永続化した
  （#173 / PR #180 / [[IADR-0043]]）。

## 提案（計画への反映案）

- 反映先候補: **UC・画面更新**（UC-06 / SC-07 のデータソース節）＋必要に応じて要求（FR-12）の補足。
- 提案内容:
  1. UC-06 に「変換ジョブの状況照会・失敗一覧・再変換（人手補正）」のフローと、そのための API 依存を明記。
  2. SC-07 のデータソースに、変換ジョブ照会 API（`GET /jobs` 相当）・再変換 API（`retry` 相当）と、
     ジョブ状態モデル **queued / processing / succeeded / failed** を記載。
  3. 「Wave B の各バックエンド API 実装済み」前提の例外として、fire-and-forget ワーカー（ConversionService）は
     読み取り/操作 API を別途要すると注記。

## 影響範囲

- 計画の UC-06 / SC-07 記述の追補のみ（実装は PR #172/#180 で完了済み）。他 UC・画面への波及は無い。
- 実装との整合: [[IADR-0042]]（読み取りモデル）／[[IADR-0043]]（永続化）が対応 ADR。
