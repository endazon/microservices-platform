---
title: IADR-0043 変換ジョブ読み取りモデルの永続化（Postgres+EF）と非同期ストア
type: impl-adr
status: Accepted
related_ids:
  - SC-07
  - UC-06
  - FR-12
  - ADR-0002
  - ADR-0003
  - ADR-0027
  - IADR-0042
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
---

# IADR-0043: 変換ジョブ読み取りモデルの永続化（Postgres+EF）と非同期ストア

- 状態: Accepted
- 日付: 2026-07-09
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: SC-07（変換ジョブ）／ UC-06 ／ FR-12（文書正規化）
- 関連 ADR: [[IADR-0042]]（変換ジョブ読み取りモデル・MVP インメモリ）／ [[ADR-0002]]（サービスごとの DB）／ [[ADR-0003]]（MassTransit。Superseded by ADR-0027）／ [[IADR-0029]]（ワーカーの最小 HTTP サーフェス）
- 関連仕様書: `docs/data/conversion-job.md` / `docs/screens/SC-07_conversion-jobs.md`
- Issue: #173（[[IADR-0042]] フォローアップ）

## コンテキストと課題

[[IADR-0042]] で、SC-07（変換状況・失敗一覧・人手補正）を実現するため ConversionService に
変換ジョブ読み取りモデル `IConversionJobStore` を新設した。ただし MVP としてプロセス内の
**インメモリ実装**（`InMemoryConversionJobStore`・singleton）を採り、永続化は follow-up とした。

インメモリ実装には以下の制約がある。

- **再起動でジョブ履歴が消失**する（永続性なし）。
- **複数インスタンス間で共有されない**（水平スケール時に一貫しない）。
- 監査・長期保全ができない。

## 決定

1. **変換ジョブを Postgres + EF Core で永続化する**（[[ADR-0002]] のサービスごと DB 方針・
   DataSourceService 準拠）。`ConversionJob` エンティティ・`ConversionJobDbContext`・InitialCreate
   マイグレーション・起動時 `MigrateAsync`・Npgsql ヘルスチェックを追加する。DB は `conversion_svc`。
2. **`IConversionJobStore` を非同期 API に変更する**（`StartAsync` / `SucceedAsync` / `FailAsync` /
   `ListAsync` / `GetAsync` / `PrepareRetryAsync`、いずれも `CancellationToken` を受ける）。EF の I/O は
   非同期が正道であり、同期 EF 呼び出し（`SaveChanges`/`ToList`）はスレッドプール枯渇を招くため採らない。
   呼び出し側（コンシューマ・`/jobs` エンドポイント）は既に非同期文脈のため追随は軽微。
3. **ストアの生存期間を singleton → scoped に変更する**。EF の `DbContext` は scoped であり、
   MassTransit はメッセージ消費ごとに DI スコープを張るため、コンシューマ・エンドポイント双方で
   scoped ストアが解決できる。
4. **再変換用に原本イベントを列として保持する**。`RawDocumentFetched` を再構成できるよう
   `StorageUri` / `ContentType` / `Attributes`(jsonb) / `Tags`(jsonb) / `FetchedAt` を保存する
   （id=`FetchId`・`SourceId`・`SourceType`・`OriginalPath` は DTO 項目として既に保持）。
5. **API・BFF・画面・DTO（`ConversionJobDto`）は不変**。[[IADR-0042]] で抽象化した `IConversionJobStore`
   の実装差し替えに閉じる（インメモリ実装は削除）。

## 根拠 / 代替案

- **同期 EF 実装で interface を据え置く案を採らない**: 変更差分は小さいが、同期 EF は既知の
  アンチパターン（スレッドプール枯渇・アナライザ警告）。呼び出し側が既に非同期のため、非同期化の
  追随コストは低く、正しい形に寄せる方が費用対効果に優れる。
- **`Start` の上書き（attempts++）の並行性**: 単一インスタンス（dev）前提のため、同一 `FetchId` の
  並行受信は考慮外とする（read-modify-write は楽観的競合制御なし）。水平スケール時は行ロックまたは
  楽観的並行トークンの導入を要する（follow-up として本 ADR に明記）。
- **デッドレターとの突合**: 失敗ジョブの網羅性向上（`<queue>_error` との突合）は本 PR の対象外とし、
  読み取りモデル（コンシューマが記録）で UC-06 の状況一覧・再変換 UX を満たす。

## 影響

- ConversionService: `ConversionJob` エンティティ・`ConversionJobDbContext`・Migrations・
  `EfConversionJobStore`、`Program.cs`（DbContext/ヘルスチェック/MigrateAsync/DI 生存期間）、
  `.csproj`（Npgsql EF・EF Design・Relational・NpgSql ヘルスチェック）。
- `IConversionJobStore` の非同期化に伴い、コンシューマ・`/jobs` エンドポイント・テストを追随更新。
- deploy: `create-multiple-dbs.sh` に `conversion_svc`、`docker-compose.yml` の conversion-service に
  接続文字列・postgres 依存を追加。

## フォローアップ

- 水平スケール時の `Start`（attempts++）並行性（行ロック / 楽観的並行）。
- デッドレター（`<queue>_error`）との突合による失敗ジョブ網羅性の向上。
- ジョブ履歴の保持期間・アーカイブ方針（監査・長期保全）。
