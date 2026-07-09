---
title: IADR-0042 変換ジョブ読み取りモデル（インメモリ）と状況照会・人手補正 API
type: impl-adr
status: Accepted
related_ids:
  - SC-07
  - UC-06
  - FR-12
  - ADR-0003
  - ADR-0012
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
---

# IADR-0042: 変換ジョブ読み取りモデル（インメモリ）と状況照会・人手補正 API

- 状態: Accepted
- 日付: 2026-07-09
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: SC-07（変換ジョブ）／ UC-06 ／ FR-12（文書正規化）
- 関連 ADR: ADR-0003（MassTransit）／ ADR-0012（変換・pandoc）／ [[IADR-0039]]（管理系ロール）／ [[IADR-0029]]（ワーカーの最小 HTTP サーフェス）
- 関連仕様書: `docs/screens/SC-07_conversion-jobs.md`

## コンテキストと課題

SC-07 は変換状況・失敗ジョブの一覧と人手補正（再変換）を提供する。しかし **ConversionService はイベント駆動の fire-and-forget ワーカー**であり、変換状況を問い合わせる手段が無い（成功は `DocumentNormalized` を発行するのみ、失敗は MassTransit のデッドレターへ送られるが照会 API は無い）。Issue #133 の前提「バックエンド API は実装済み」は変換ジョブに関しては成立していなかった（計画とのギャップ）。

決めること:
1. 変換状況をどこに・どう保持するか。
2. 照会・人手補正の API 形。
3. 認可。

## 決定

1. **ConversionService に変換ジョブの読み取りモデルを追加する。** 変換コンシューマ（`RawDocumentFetchedConsumer`）が受信・成功・失敗の各ライフサイクルを `IConversionJobStore` に記録する。失敗時は記録後に例外を**再送出**し、MassTransit の再試行→デッドレター挙動は変えない（記録は可視化・人手補正のためのサイドカー）。
2. **MVP はインメモリ実装（`InMemoryConversionJobStore`・singleton）**とする。永続化（Postgres+EF）・複数インスタンス共有は follow-up（下記）。理由: 本 PR の主眼は SC-07 画面のエンドツーエンド実現であり、新規 DB スキーマ＋マイグレーション導入は画面実装フェーズとしては過剰・高リスク。ワーカーは現状単一インスタンス（dev）で、状況ビューの MVP としてインメモリで十分機能する。
3. **API（メッシュ内部・ワーカー上）**: `GET /jobs`（`?status=` 絞り込み）・`GET /jobs/{id}`・`POST /jobs/{id}/retry`（原本イベント `RawDocumentFetched` を再発行して再変換）。ワーカー自身は最小 HTTP サーフェスに留め認可は課さない（[[IADR-0029]] と同方針。ingress 非公開）。**認可は BFF で管理者・運用者に限定**（`/bff/conversion/jobs`。[[IADR-0039]]）。フロントは `RequireRole` で存在秘匿。

## 根拠 / 代替案

- **DB 永続化を今回見送る**: 正しい最終形だが、マイグレーション生成・EF 配線・ワーカーのテスト整備を伴い本フェーズには重い。インメモリ MVP＋明確な follow-up が費用対効果に優れる。挙動（状態遷移・絞り込み・再変換）は `IConversionJobStore` の抽象で表現し、後日 EF 実装へ差し替え可能に設計した。
- **デッドレター直接照会を採らない**: RabbitMQ の `_error` キューは運用ツール向けで、画面の状況一覧・再変換 UX には不向き。読み取りモデルの方が UC-06 に合致する。
- **リトライ挙動は不変**: 失敗記録後に必ず再送出し、既存の再試行→デッドレター（[[ADR-0003]]）を保持する。

## 影響

- `Shared.Contracts` に `ConversionJobDto` / `ConversionJobStatus`。
- ConversionService に `IConversionJobStore` + インメモリ実装、コンシューマの記録、`/jobs` エンドポイント。
- BFF に `ConversionBffEndpoints`（admin/operator 限定）＋ `ConversionService` named client。
- フロント `features/sc07-conversions`（`/conversions`・admin/operator 限定・状況一覧・フィルタ・再変換）。

## フォローアップ（計画へのフィードバック）

- **計画ギャップ**: SC-07 の前提「変換ジョブのバックエンド API は実装済み」は成立していなかった（ConversionService は照会 API 無し）。計画側 05_screens/03_usecases（UC-06）に「変換ジョブ照会・再変換 API」を明示する提案を環流する。
- **永続化 follow-up**: インメモリ→Postgres+EF（DataSourceService 準拠）へ差し替え、再起動耐性・複数インスタンス共有・監査保存を得る。別 issue 化する。
