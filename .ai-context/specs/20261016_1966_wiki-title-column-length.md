---
title: Wiki 同期で Wiki.js の列の長さ（表題 varchar(255)）を超える表題を文字の境界で切り詰め、それでも Wiki.js が入力の長さで拒んだものは再試行・DLQ に回さず記録して ack する（#1966）
type: spec
status: done
related_ids: [FR-13, UC-07, NFR-06, ADR-0011, ADR-0027, IADR-0021, IADR-0541, IADR-0554]
author: claude
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/microservices-platform/02_requirements（FR-13 Wiki 閲覧 / NFR-06 障害時の縮退運転）
issue: "#1966"
---

# 仕様書: Wiki 同期の表題の長さ（#1966）

## 起点となる計画書（トレーサビリティ）

- FR-13（Wiki 閲覧・同期）・UC-07・NFR-06（障害時の縮退運転）。
- ADR-0011（Wiki.js。ABAC は本システム）・ADR-0027（Wolverine。共通既定の再試行・DLQ）。
- 実装判断の記録: [IADR-0554](../adr/IADR-0554_wiki-title-column-length-and-value-too-long.md)。恒久的な失敗の型は [IADR-0541](../adr/IADR-0541_wiki-slug-unique-and-permanent-sync-failures.md)（#1934。issue 本文の「#1936」は同じ件を指す）に揃える。
- 起点: #1966（PoC・2026-10-16。`wolverine-dead-letter-queue` の 14 件がすべて `wiki-service.DocumentUpdated` の
  `Wiki.js pages.create failed ... (code=1): ... value too long for type character varying(255)`）。

## 根本原因（コードと Wiki.js 2.5 のスキーマで確かめた）

- `DocumentSyncConsumer` は `ev.Title` をそのまま `WikiJsPage.Title` に入れ、`WikiJsGraphQlClient` が `pages.create` / `pages.update` の `title` に送る。
- Wiki.js 2.x の `pages.title` / `pages.description`（と `pageTree.title`・`pageHistory.title`）は Knex の `string()` ＝ **varchar(255)**。
  PostgreSQL の varchar(n) は**文字（UTF-8 DB では符号位置）**で数える（バイトでも UTF-16 の単位でもない）。
- Wiki.js は DB の例外を `responseResult { succeeded:false, errorCode:1, message:"<SQL> - value too long for type character varying(255)" }` に平たくして返す。
  クライアントはこれを `WikiJsSyncException`（一時障害扱い）にするので、再試行を使い切って DLQ へ入る。
- 長い表題の出所: AST の保存文書の `<<<UNTRUSTED_DATA ... UNTRUSTED_DATA>>>` の囲み（約 37 文字。AST 側の別 issue）と英文ニュースの長い見出し。

## 母集合（規則 9・10）

- Wiki.js へ文字列を送る箇所: `git grep -n "new WikiJsPage(" -- src` → 受け口（`DocumentSyncConsumer`）とアーカイブ（`ArchivePageAsync`。Wiki.js から読んだ今の表題を送り返すだけ＝既に 255 以内）。
- `pages.create` / `pages.update` の変数: `title`（受け口の表題）・`description`（**常に空文字の定数**）・`path`（`WikiPage.PathFor` = `doc/<GUID>` の **40 文字固定**）・`tags`（文書のタグ。
  Wiki.js の `tags.tag` も varchar(255)）・`content`（text。上限なし）・`locale`/`editor`（定数）。
- 本サービスの台帳（`wiki_svc.Pages`）: `Title` varchar(500)・`Slug` varchar(500)。**500 を超える表題は台帳の保存（22001）でも同じく DLQ になる**
  —— `SlugCandidates` の先頭の候補（題名だけ由来）は切られていない（接尾辞付きの候補だけ切っていた）。同じ型の欠陥なので本件で塞ぐ。

## 決めたこと（詳細は IADR）

1. **切り詰めの単位は「符号位置（Rune）」で数え、切る位置は書記素クラスタ（テキスト要素）の境界に限る**。サロゲートペア・結合文字・異体字セレクタ・ZWJ 連結の絵文字を途中で切らない。
2. **印**: 切ったら末尾に `…`（U+2026。1 文字）を付け、印を含めて上限に収める。本文は書き換えない（元の表題は台帳と文書サービスに残る）。
3. Wiki.js の表題は 255、台帳の表題は 500、slug の題名だけの候補は 500（印なし）。説明は空の定数・パスは 40 文字固定で、切り詰めは不要（試験で固定）。
4. 切ったら計数 `wiki.sync.truncated{field=title}` を 1。
5. **恒久的な失敗**: Wiki.js が `value too long for type`（PostgreSQL 22001）で拒んだら `WikiJsInputRejectedException` へ写し、受け口は Warning＋計数
   `wiki.sync.skipped{reason=value_too_long}` で ack する（IADR-0541 の型）。他の Wiki.js の失敗（transport・GraphQL errors・他のエラー）は従来どおり例外（再試行）。
6. DLQ の 14 件: 運用仕様書に「DLQ の ID を抜き出して `republish-document-updated.js --ids` で再発行する」手順を足す（DLQ の写しは再投入しない既定の方針を守る）。

## 受け入れ基準と試験の写像（`WikiSyncPermanentFailureTests`・テスト仕様書 T-30〜T-34）

| 受け入れ基準 | 試験 |
| --- | --- |
| T-30 255 ちょうどは切らずに通る（数えない） | `表題が255文字ちょうどなら切らずに送る` |
| T-31 256 文字は 255 に切り詰め印を付け、計数する／台帳の表題は 500 まで・slug の候補は 500 に収まる | `表題が256文字なら255文字へ切り詰め印を付けて数える`・`台帳の表題とslugの候補は列の長さに収まる` |
| T-32 マルチバイト・サロゲート・結合文字の境界で切れない | `切り詰めはサロゲートペアと結合文字の途中で切らない`（Theory）・`符号位置で数える` |
| T-33 恒久的な失敗（value too long）は Warning＋計数で ack・何も書かない／実配送で DLQ へ行かない | `Wikijsが長さで拒めば記録してackする`・`実配送で恒久的な失敗は一回の試行で終わりDLQへ行かない`（value-too-long）・クライアントの分類 |
| T-34 一時的な失敗（他の Wiki.js の失敗）は再試行する | `Wikijsの一時障害は例外のまま上げる`・`実配送でWikijsの一時障害は再試行される`・クライアントの分類 |

## 変異試験（すべて赤になることを確かめた）

| 変異 | 赤になった試験 |
| --- | --- |
| M1 数える単位を `string.Length`（UTF-16）にする | `符号位置で数える` |
| M2 境界を見ずに `value[..budget]` で切る | `切り詰めはサロゲートペアと結合文字の途中で切らない`（5 例すべて） |
| M3 受け口で切り詰めない | `表題が256文字なら255文字へ切り詰め印を付けて数える` |
| M4 クライアントが value too long を分類しない | `UpsertPageAsync_MapsValueTooLongToInputRejected`（create / update） |
| M5 クライアントが失敗をすべて恒久とみなす | `UpsertPageAsync_KeepsOtherFailuresTransient`（3 例） |
| M6 切り詰めを数えない | `表題が256文字なら255文字へ切り詰め印を付けて数える` |
| M7 受け口で `WikiJsInputRejectedException` を捕まえない | `Wikijsが長さで拒めば記録してackする`・`実配送で恒久的な失敗は一回の試行で終わりDLQへ行かない`（value-too-long） |
| M8 台帳の表題を切らない | `台帳の表題とslugの候補は列の長さに収まる` |
| M9 slug の題名だけの候補を切らない | `台帳の表題とslugの候補は列の長さに収まる` |

## 検証

- `dotnet test src/knowledge/backend/Services/WikiService/Tests/WikiService.Tests.csproj` → 175 件成功。
- `dotnet format`（WikiService・WikiService.Tests）`--verify-no-changes` → 差分なし。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → 1095 件成功。`check-trace-blocks` / `check-doc-links` / `check-test-spec-coverage`（床を `--update`）/ `gen-knowledge-graph --check` → 0。

## 残るもの

- タグが 255 文字を超える文書は記録して ack（Wiki に載らない）。現状の入力では起きていない。
- AST 側の `<<<UNTRUSTED_DATA ...>>>` で表題が伸びる件は AST 側の別 issue。
- 一括操作時の Wiki 同期の律速（1 件約 8.4 秒）は別 issue（本件の範囲外）。
