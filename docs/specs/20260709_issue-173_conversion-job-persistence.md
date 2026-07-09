---
title: 変換ジョブ読み取りモデルの永続化（Issue #173）
type: spec
status: completed
related_ids:
  - FR-12
  - UC-06
  - SC-07
  - IADR-0042
  - IADR-0043
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-12)"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md (UC-06)"
---

# 仕様書: 変換ジョブ読み取りモデルの永続化（Issue #173）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-12（文書正規化）
- ユースケース(UC): UC-06（変換・正規化の状況確認・人手補正）
- 画面(SC): SC-07（変換ジョブ画面）
- 関連 ADR: [[IADR-0043]]（本 PR で作成・永続化＋非同期ストア）、[[IADR-0042]]（読み取りモデル・MVP インメモリ）、
  [[ADR-0002]]（DB per Service）、[[ADR-0003]]（MassTransit）、[[IADR-0029]]（ワーカー最小 HTTP）
- Issue: #173（[[IADR-0042]] フォローアップ）

## 目的・背景

[[IADR-0042]] で SC-07 実現のため導入した変換ジョブ読み取りモデル `IConversionJobStore` は MVP として
**インメモリ実装**（singleton）であり、再起動でジョブ履歴が消失し、複数インスタンス間で共有されず、
監査・長期保全ができない。本 PR で DataSourceService 準拠の **Postgres + EF Core** に永続化する
（[[IADR-0043]]）。抽象 `IConversionJobStore` は分離済みのため、実装差し替え＋非同期化に閉じ、
BFF・画面・DTO（`ConversionJobDto`）は不変とする。

## 対象範囲

- 対象:
  - ConversionService:
    - `ConversionJob` エンティティ（`Foundation/Jobs/ConversionJob.cs`）。
    - `ConversionJobDbContext`（`Foundation/Persistence/`。`Attributes`/`Tags` を jsonb 変換）。
    - `Migrations/InitialCreate`（`ConversionJobs` テーブル）。
    - `IConversionJobStore` の**非同期化**（`*Async`＋`CancellationToken`）と `EfConversionJobStore` 実装。
      インメモリ実装（`InMemoryConversionJobStore`）は削除。
    - `Program.cs`: DbContext 登録・`AddNpgSql` ヘルスチェック・起動時 `MigrateAsync`・
      ストア生存期間を scoped に変更。
    - `.csproj`: Npgsql EF / EF Design / EF Relational / NpgSql ヘルスチェック。
  - コンシューマ・`/jobs` エンドポイントの呼び出しを `await ...Async(..., ct)` へ追随。
  - deploy: `create-multiple-dbs.sh` に `conversion_svc`、`docker-compose.yml` の conversion-service に
    接続文字列・postgres 依存。
  - テスト: ジョブストア単体（EF InMemory provider へ移行）・エンドポイント（EF InMemory）・
    コンシューマ記録ハーネスの非同期追随。
  - ドキュメント: 本仕様書・データ仕様書（`docs/data/conversion-job.md`）・IADR-0043。
- 対象外:
  - BFF・SPA・`ConversionJobDto`（不変）。
  - 変換出力（Markdown）の手編集 UI（人手補正は再変換に限定・[[IADR-0042]]）。
  - デッドレター突合・履歴保持方針・水平スケール時の並行制御（[[IADR-0043]] follow-up）。

## 受け入れ基準（Issue #173）との対応

- [x] `ConversionJob` を Postgres + EF Core で永続化（エンティティ・DbContext・マイグレーション・
      起動時 `MigrateAsync`・Npgsql ヘルスチェック）。
- [x] `IConversionJobStore` の EF 実装差し替えで API・BFF・画面が不変（DTO 射影が従来同値）。
- [x] 保持項目に再変換用の原本イベント（StorageUri/ContentType/Attributes/Tags/FetchedAt）を含む。
- [x] 状態遷移（受信=processing/attempts++、成功、失敗、失敗のみ再変換=queued）が従来と同値。
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が通る。

## 実装判断・計画フィードバック

- 非同期ストア＋scoped 生存期間・EF は [[IADR-0043]] に記録。同期 EF アンチパターンを避けるため
  interface を非同期化した（呼び出し側は既に非同期文脈）。
- デッドレター突合・並行制御・履歴保持は follow-up として IADR-0043 に明記（本 PR 対象外）。

## テスト観点

- ストア単体（EF InMemory）: Start=processing/attempts=1、再 Start で attempts++、Succeed=doc/markdown、
  Fail=error、List `?status` 絞り込み・新しい順、Get 未知=null、PrepareRetry（失敗のみ queued・未知/非失敗=null）。
- エンドポイント（EF InMemory）: 一覧・絞り込み・個別 200/404・retry 202/404/409。
- コンシューマ: 成功で Succeed 記録、失敗で Fail 記録のうえ例外再送出（リトライ挙動不変）。
