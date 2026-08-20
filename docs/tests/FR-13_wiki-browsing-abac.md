---
title: FR-13 Wiki 閲覧の ABAC 適用 テスト仕様書
type: test-spec
status: draft
created: 2026-07-03
updated: 2026-07-07
author: claude
---
<!-- trace:
ids: [FR-05, FR-13, UC-07]
adrs: [ADR-0004, ADR-0011]
iadrs: [IADR-0023]
specs: [01_requirements, 01_usecases, 20260703_FR-13_wiki-browsing-abac, FR-05_abac-access-control]
issues: []
-->

# テスト仕様書: FR-13 Wiki 閲覧の ABAC 適用

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-13
- ユースケース（UC）: UC-07（Wikiで閲覧する）
- 関連 ADR: ADR-0011（Wiki 採用・ABAC は本システムが真実源）/ ADR-0004（ABAC deny-by-default）

## テスト対象・範囲

- 対象: `AbacPageFilter`（許可スコープの評価意味論）、`WikiEndpoints`（一覧・個別の ABAC 適用）、
  `DocumentSyncConsumer`（更新イベント → ページ同期）。
- 対象外: 横断検索・AI 回答本体（FR-03/04/07 で検証済み）、負荷/p95、Wiki.js 本体・OIDC 連携。

## テスト観点

- deny-by-default: `Granted=false` で一覧が空・個別が 404。
- 評価意味論: フィルタ間 AND・値集合内 OR・属性欠落は不一致。
- セキュリティ: 権限外文書が一覧・本文のいずれにも現れない（受け入れ基準②・UC-07 例外フロー）。
- 反映: `DocumentUpdated` の受信で作成・更新される（受け入れ基準③）。

## テストケース一覧

| ID | 前提条件 | 手順 | 期待結果 | 対応受け入れ基準 | 区分 |
| --- | --- | --- | --- | --- | --- |
| T-01 | `Granted=false` | `AbacPageFilter.Matches` | false（不可視） | 権限外を出さない | 自動 |
| T-02 | `Granted=true`・フィルタ空 | 同上 | true（全件可） | 閲覧 | 自動 |
| T-03 | 許可値に一致する属性値 | 同上 | true（OR 一致） | 閲覧 | 自動 |
| T-04 | 許可値外の属性値 | 同上 | false | 権限外を出さない | 自動 |
| T-05 | 複数フィルタの一部が不一致 | 同上 | false（AND） | 権限外を出さない | 自動 |
| T-06 | 属性キー欠落 | 同上 | false（欠落は安全側） | 権限外を出さない | 自動 |
| T-07 | `Granted=false` | `GET /wiki/pages` | 空配列 | 権限外を出さない | 自動 |
| T-08 | public/restricted の 2 ページ・許可=public | `GET /wiki/pages` | public のみ返る | 権限外を出さない | 自動 |
| T-09 | 許可=public | `GET /wiki/pages/by-doc/{restricted}` | 404（存在秘匿） | 権限外を出さない | 自動 |
| T-10 | 許可=public | `GET /wiki/pages/by-doc/{public}` | 200 | 閲覧 | 自動 |
| T-11 | 許可=public | `GET /wiki/pages/{restricted-slug}` | 404 | 権限外を出さない | 自動 |
| T-12 | status=normalized の更新イベント | 発行→消費 | ページ作成・属性保持 | 更新反映 | 自動 |
| T-13 | status=draft の更新イベント | 発行→消費 | 同期されない | 更新反映 | 自動 |
| T-14 | 同一 DocumentId で 2 回発行 | 発行→消費 | 1 ページに更新（タイトル/属性が最新） | 更新反映 | 自動 |
| T-15 | 同期済みページ・status=archived 受信 | 発行→消費 | Wiki.js 非公開化＋メタデータ Archived | 削除/アーカイブ伝播 | 自動 |
| T-16 | 未同期 ID の archived 受信 | 発行→消費 | 例外なし（冪等）・Wiki.js 非公開化のみ試行 | 同上 | 自動 |
| T-17 | アーカイブ後に published 再受信 | 発行→消費 | メタデータ Active へ復帰（可逆） | 同上 | 自動 |
| T-18 | 同期済みページ・DocumentDeleted 受信 | 発行→消費 | Wiki.js 実体撤去＋メタデータ行削除 | 同上 | 自動 |
| T-19 | 未同期 ID / 再送の DocumentDeleted | 発行→消費 | 例外なし（冪等） | 同上 | 自動 |
| T-20 | Archived ページ・権限あり | `GET /wiki/pages` / by-doc | 一覧に出ない・個別 404（存在秘匿維持） | 同上 | 自動 |
| T-21 | 未存在ページの singleByPath（errors 6003） | `UpsertPageAsync` | 例外化せず create へ進む（稼働実測整合） | 更新反映 | 自動 |
| T-22 | アーカイブの Wiki.js 送信シェイプ | `ArchivePageAsync` | content/title/tags を含む全項目 update＋unpublish | 削除/アーカイブ伝播 | 自動 |

## 実装マッピング

- `AbacPageFilterTests`（T-01〜T-06）
- `WikiEndpointsAbacTests`（T-07〜T-11、T-20）
- `DocumentSyncConsumerTests`（T-12〜T-14）
- `DocumentDeleteArchiveSyncTests`（T-15〜T-19）
- `WikiJsGraphQlClientTests`（T-21〜T-22。稼働 Wiki.js 2.5.314 の実測応答を再生）
- `DocumentLifecycleEventTests`（DocumentService 側: archive/delete のイベント発行）
