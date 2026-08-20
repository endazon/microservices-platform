---
title: SC-04 Wiki 閲覧導線 テスト仕様書
type: test-spec
status: draft
created: 2026-07-08
updated: 2026-08-21
author: claude
---
<!-- trace:
ids: [FR-13, SC-04, UC-07]
adrs: []
iadrs: []
specs: [01_screens, 20260708_issue-130_sc04-wiki-access, SC-04_wiki-access]
issues: []
-->

# テスト仕様書: Wiki 閲覧導線

> 導線・実行時 config・認証ガードを写像する。Wiki.js 本体の SSO/ABAC は Wiki.js/ゲートウェイ側（#118 実測）で担保。

## 起点となる計画書（トレーサビリティ）

- 機能要求: 正規化文書を Wiki サービスで閲覧できること ／ ユースケース: Wiki で閲覧する
- 受け入れ基準の所在: Issue #130 ／ `docs/specs/20260708_issue-130_sc04-wiki-access.md`

## テスト対象・範囲

- 対象: `features/sc04-wiki`（導線表示）、`runtimeConfig.wikiBaseUrl` の注入。
- 対象外: Wiki.js 本体・SSO/ABAC ゲートウェイ（既存・#118）。

## テスト観点

- 導線: `wikiBaseUrl` 設定時のみ「Wiki を開く」リンク（新規タブ）。未設定は注意書き（リンク無し）。
- 実行時 config: `wikiBaseUrl` の注入・空文字→undefined。
- 認証: 未認証 `/wiki`→`/login`。

## テストケース一覧

| ID | 前提 | 期待結果 | 対応 | 区分 |
| --- | --- | --- | --- | --- |
| T-01 | wikiBaseUrl 設定 | 「Wiki を開く」リンク（href=wikiBaseUrl・別タブ） | 導線 | 自動(単体) |
| T-02 | wikiBaseUrl 未設定 | リンク無し・`role="note"` 注意書き | 導線 | 自動(単体) |
| T-03 | config 注入 | `wikiBaseUrl` を読み、空文字は undefined | 実行時config | 自動(単体) |
| T-04 | 未認証 | `/wiki`→`/login` | 認証ガード | 自動(E2E) |

## 未決事項

- なし
