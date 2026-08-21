---
title: 業務DB データソースコネクタ（優先4）（Issue #219）
type: spec
status: done
related_ids:
  - FR-01
  - UC-04
  - IADR-0051
  - IADR-0055
author: claude
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md (fixed・優先4 業務DB)
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-01)
  - planning:projects/microservices-platform/03_usecases (UC-04)
---

# 仕様書: 業務DB データソースコネクタ（優先4）（Issue #219）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-01 ／ ユースケース(UC): UC-04
- 技術検討: `09_datasource-connectors.md`（fixed・優先4 業務DB）— 取得=SQL 読取（**参照専用**）、変更検知=更新列/CDC、
  認証=**参照専用 DB ユーザー（最小権限）**、「行→文書」化のマッピング定義を構成として持つ。
- 関連 ADR: [IADR-0051](../adr/IADR-0051_datasource-connector-port-and-filesystem.md)（コネクタ抽象・同期基盤）、[IADR-0053](../adr/IADR-0053_wiki-connector-generic-rest-contract.md)/[IADR-0054](../adr/IADR-0054_saas-connector-pagination-and-rate-limiting.md)（Wiki/SaaS 汎用契約の先例）、
  [IADR-0055](../adr/IADR-0055_database-connector-readonly-sql-mapping.md)（本 PR で作成・業務DB コネクタの設計）
- Issue: #219（親 #195）

## 目的・背景

コネクタ抽象・同期基盤（[IADR-0051](../adr/IADR-0051_datasource-connector-port-and-filesystem.md)）の上に、優先4 業務DB コネクタを追加する。業務DB のスキーマは多様なため、
**「行→文書」化のマッピングを構成（SQL クエリ）として持つ**設定駆動方式で 1 コネクタを提供する（[IADR-0055](../adr/IADR-0055_database-connector-readonly-sql-mapping.md)）。
参照専用（読み取り専用）を DB ユーザー権限＋SELECT のみのコードで担保する。

## 対象範囲（本 PR）

- 対象:
  - **DatabaseConnector**（`Composable/Adapters`・`SourceType="db"`）:
    - 構成: `Config["query"]`＝列を `id`（テキスト）/`updated`（日時）/`content`（テキスト）に別名付けした SELECT。
      （例 `SELECT id::text AS id, updated_at AS updated, body AS content FROM public.articles`）
    - Discover: `SELECT id, updated FROM ( {query} ) AS src` を実行し行を取得、`updated > since` を**インメモリ**で増分。
    - Fetch: `SELECT content FROM ( {query} ) AS src WHERE id = @id`（パラメータ化）→ 本文バイト（UTF-8）＋content-type。
    - 認証/権限: 接続情報は `ConnectionUri`（パスワードを含めない）＋`Config["password"]`（GET 応答でマスク）。
      **参照専用 DB ユーザー**（最小権限）を前提とし、コードは SELECT のみ発行する（書き込みしない）。
    - 失敗: DB エラーは例外送出 → オーケストレータ（[IADR-0051](../adr/IADR-0051_datasource-connector-port-and-filesystem.md) 決定3a）が watermark 非前進・継続失敗アラートに載せる。
    - 縮退: `ConnectionUri`／`query` 未設定は空列挙。
  - **IDbConnectionFactory**（`Foundation/Ports`）＋`NpgsqlConnectionFactory`（`Composable/Adapters`）: プロバイダ抽象
    （本 PR は PostgreSQL＝Npgsql。テストはハンドロール ADO.NET フェイクで差し替え）。
  - DI 登録（`AddSingleton<IDbConnectionFactory, NpgsqlConnectionFactory>` ＋ `AddSingleton<IDataSourceConnector, DatabaseConnector>`）。
  - 単体テスト（**ハンドロール ADO.NET フェイク**で行→文書化・更新列による増分・本文取得・縮退・DB エラー伝播）。
    ※ SQLite は SQLitePCLRaw の**未修正 CVE-2025-6965（NU1903 高）**を持ち込むため採用しない（依存追加を避ける）。
  - ドキュメント: 本仕様書・[IADR-0055](../adr/IADR-0055_database-connector-readonly-sql-mapping.md)・`docs/functional/FR-01`・`docs/tests/FR-01`。
- 対象外（follow-up）:
  - **他 DB プロバイダ（SQL Server/MySQL 等）アダプタ**・**CDC**（本 PR は更新列＋インメモリ差分）。
  - **DB 側 WHERE による増分**（効率化。本 PR はインメモリ差分＝他コネクタと一貫）。
  - Vault 連携（秘密の集中管理）。API 応答マスクは既存 [IADR-0053](../adr/IADR-0053_wiki-connector-generic-rest-contract.md) の `RedactSecrets` を共用（`password` キーをマスク）。
  - **実 PostgreSQL に対する統合テスト**（実 DB/コンテナ前提。既存 `PostgresFixture`/DockerFact パターン）。

## CI で緑にできる範囲 / 実 DB・コンテナ前提の切り分け

- **CI 緑（本 PR）**: DatabaseConnector 単体テスト（**ハンドロール ADO.NET フェイク**・実 DB 不要・外部依存追加なし）。
  行→SourceItem マッピング・更新列増分・本文取得・パラメータ化・縮退・DB エラー伝播を検証する。
- **実 DB/コンテナ前提（follow-up）**: **実 SQL の正しさ**（派生表ラップ・`WHERE id=@id`・参照専用ユーザー権限）は
  実 PostgreSQL の統合テスト（`DockerFact`／`PostgresFixture`）で別途確認する。

## 受け入れ基準（Issue #219）との対応

- [x] `sourceType=db` の同期が参照専用で行を取得し、マッピング定義（`Config["query"]` の id/updated/content）に基づき `RawDocumentFetched` を発行する。
- [x] 更新列（`updated`）による増分同期（`updated > since`）。
- [x] 参照専用・最小権限を担保（コードは SELECT のみ・書き込みしない。DB ユーザーは参照専用前提）。
- [x] 継続失敗アラート経路（[IADR-0051](../adr/IADR-0051_datasource-connector-port-and-filesystem.md)）に載る（DB エラーは例外送出）。
- [x] `IDataSourceConnector` 追加のみでコア改修不要（プラグイン方式）。
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が通る。
