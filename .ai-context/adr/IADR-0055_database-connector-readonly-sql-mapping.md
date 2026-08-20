---
title: IADR-0055 業務DB コネクタは参照専用の設定駆動 SQL（id/updated/content 別名）で「行→文書」化する
type: impl-adr
status: Accepted
related_ids:
  - FR-01
  - UC-04
  - IADR-0051
author: claude
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md (fixed・優先4 業務DB)
---

# IADR-0055: 業務DB コネクタは参照専用の設定駆動 SQL で「行→文書」化する

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-01／UC-04／`09_datasource-connectors.md`（fixed・優先4 業務DB: SQL 読取（参照専用）・
  更新列/CDC・参照専用ユーザー・「行→文書」マッピング）
- 関連 ADR: [IADR-0051](./IADR-0051_datasource-connector-port-and-filesystem.md)（コネクタ抽象・同期基盤）／[IADR-0053](./IADR-0053_wiki-connector-generic-rest-contract.md)・[IADR-0054](./IADR-0054_saas-connector-pagination-and-rate-limiting.md)（Wiki/SaaS 汎用契約の先例）
- 関連仕様書: `docs/specs/20260710_issue-219_database-connector.md`
- Issue: #219（親 #195）

## コンテキストと課題

業務DB はスキーマ・粒度が多様で「どの行を 1 文書とするか」はデータソース固有。計画は「参照専用」「更新列/CDC」
「行→文書マッピングを構成として持つ」を求める。どの DB プロバイダに、どのようなマッピング契約で実装するかを決める。

## 検討した選択肢

1. **設定駆動 SQL（`id`/`updated`/`content` 別名）＋インメモリ増分＋参照専用ユーザー（本決定）**: 管理者が「行→文書」を
   SELECT で定義し、コネクタはそれを派生表として包んで列挙・取得する。プロバイダは PostgreSQL（Npgsql）を第一実装とし、
   接続は `IDbConnectionFactory` で抽象化（テストはハンドロール ADO.NET フェイク）。他プロバイダは後続アダプタ。
2. **ORM/スキーマ自動マッピング**: スキーマからテーブル→文書を自動生成。汎用性が低く、粒度・機微列除外の制御が難しい。
3. **CDC（論理レプリケーション等）を最初から採用**: 低遅延だが DB 側設定・権限・運用が重く、優先4 の初期実装には過剰。

## 決定

**選択肢 1 を採用する。** `DatabaseConnector`（`Composable/Adapters`・`SourceType="db"`）は以下の契約を用いる。

- **マッピング（構成）**: `Config["query"]` に、列を `id`（テキスト）/`updated`（日時）/`content`（テキスト）に
  別名付けした SELECT を与える（例 `SELECT id::text AS id, updated_at AS updated, body AS content FROM public.articles`）。
- **Discover**: `SELECT id, updated FROM ( {query} ) AS src` を実行し、`updated > since` を**インメモリ**で増分（初回=全件）。
  他コネクタと一貫（DB 側 WHERE 増分は効率化 follow-up）。プロバイダ差の少ない ANSI SELECT/派生表を用いる。
- **Fetch**: `SELECT content FROM ( {query} ) AS src WHERE id = @id`（**パラメータ化**・SQL インジェクション回避）→ 本文バイト
  （UTF-8）＋content-type（`Config["contentType"]` 既定 `text/markdown`）。
- **参照専用**: 接続情報は `ConnectionUri`（**パスワードを含めない**）＋`Config["password"]`（GET 応答で [IADR-0053](./IADR-0053_wiki-connector-generic-rest-contract.md) の
  `RedactSecrets` によりマスク）。**参照専用 DB ユーザー（最小権限）を前提**とし、コードは SELECT のみ発行して書き込みしない。
- **プロバイダ抽象**: `IDbConnectionFactory`（`Foundation/Ports`）で `DbConnection` を生成。第一実装は
  `NpgsqlConnectionFactory`（PostgreSQL）。コネクタは ADO.NET 基底クラス（`DbConnection`/`DbCommand`/`DbDataReader`）と
  名前付きパラメータで実装し、単体テストは**ハンドロール ADO.NET フェイク**で差し替える（下記「テストの選択」）。
- **失敗/縮退**: DB エラーは例外送出（[IADR-0051](./IADR-0051_datasource-connector-port-and-filesystem.md) 決定3a → watermark 非前進・継続失敗アラート）。`ConnectionUri`／`query`
  未設定は空列挙で縮退。

**他 DB プロバイダ（SQL Server/MySQL 等）・CDC・DB 側 WHERE 増分は本 PR の対象外**（後続）。

## 理由

- **プラグイン方針との一貫性**（[IADR-0051](./IADR-0051_datasource-connector-port-and-filesystem.md)）: `IDataSourceConnector` 追加のみ、Map/格納/発行/定期同期は既存を共用。
- **計画要件の充足**: 参照専用（ユーザー権限＋SELECT のみ）、更新列（`updated`）増分、行→文書マッピング（構成 SQL）。
- **CI 緑と実測の切り分け**: `IDbConnectionFactory` 抽象＋ハンドロール ADO.NET フェイクで単体テスト（実 DB 不要・
  外部依存追加なし）。実 SQL の正しさ・実 PostgreSQL 結合は `PostgresFixture`/DockerFact の follow-up。
- **安全性**: Fetch はパラメータ化で SQL インジェクションを避ける。参照専用ユーザーで書き込みを構造的に防ぐ。

## テストの選択（SQLite を採用しない理由）

当初 SQLite（インメモリ）での単体テストを検討したが、`Microsoft.Data.Sqlite` の推移依存 `SQLitePCLRaw.lib.e_sqlite3`
に**未修正の高深刻度脆弱性 CVE-2025-6965（NU1903。SQLite < 3.50.2）があり、パッチ版が未リリース**である。CI の
「Vulnerable transitive dependencies」スキャン（IADR-0018）を通せず、既知脆弱性をリポジトリへ持ち込むことになるため
**採用しない**。代わりに `DbConnection`/`DbCommand`/`DbDataReader` のハンドロール・フェイク（外部依存なし）で
マッピング・増分・パラメータ化・エラー伝播を検証し、**実 SQL の正しさは実 PostgreSQL 統合テスト（follow-up）**で担保する。

## 影響

- `DataSourceService.Api`: `Foundation/Ports/IDbConnectionFactory.cs`、`Composable/Adapters/{NpgsqlConnectionFactory,DatabaseConnector}.cs`（新規）、
  `Program.cs`（DI 登録）。
- テスト: `DatabaseConnectorTests`（ハンドロール ADO.NET フェイク・行→文書化/増分/取得/縮退/不正クエリ例外/NULL updated スキップ/該当なし縮退）。外部依存の追加なし（上記「テストの選択」参照）。
- ドキュメント: 本 IADR・作業仕様書・FR-01 機能/テスト仕様。

## フォローアップ

- 他 DB プロバイダ（SQL Server/MySQL/Oracle 等）アダプタ・CDC・DB 側 WHERE による増分（効率化）。
- 機微列のマスキング/除外方針、行→文書の粒度（複数行結合等）の高度化。
- 実 PostgreSQL に対する統合テスト（参照専用ユーザー権限の実機確認・DockerFact）／Vault 連携（参照専用資格情報の集中管理。**一元追跡: #310** — `docs/security/security.md` §データソースのコネクタ資格情報）。

## 関連

- Supersedes: なし
- Superseded by: なし
