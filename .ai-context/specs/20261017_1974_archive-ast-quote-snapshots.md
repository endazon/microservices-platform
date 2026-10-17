---
title: 作業仕様書 — AST が 5 分ごとに保存した相場スナップショット（kind = Quote）の文書を、一回限りの管理操作で既存の状態「アーカイブ」へ移す（#1974）
type: spec
status: done
related_ids: [FR-06, FR-02, UC-03, SC-05, IADR-0509, IADR-0549, IADR-0551, IADR-0556]
author: claude
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-06）
  - planning:projects/microservices-platform/05_screens/01_screens.md（SC-05 文書管理。アーカイブの操作）
---

# 作業仕様書: AST の Quote の文書をアーカイブする（#1974）

> 本仕様書は実装着手前に作成した（着手 2026-10-17）。基点は MSP `origin/develop` `82f1e754`（#1973 のマージ後。駆動器の本体 `scripts/lib/admin-page-driver.js` を使う）。

## 起点

- #1974。AST は 2026-10-16 まで 5 分ごとに相場のスナップショットを KB へ保存していた（AST#1366。10-15 の 1 日で約 950 件）。
  属性 `kind = Quote`（タグ `Quote` / `finnhub`・表題 `{symbol} 現在値`・本文 `current=…;high=…;low=…;prevClose=…`）。
- AST 側は AST#1369（AST の IADR-0545）で保存をやめ、判断の RAG の取り出しから除いた。ただし旧い文書は索引に残り、銘柄の検索の関連度の候補（4×TopK）を占め続ける。
  AST の検索の絞り込みは単値の完全一致だけで「Quote でない」を書けない。
- 指示: #1951（IADR-0549）の既存のアーカイブ手段を再利用し、新しい仕組みを作らない。既定は dry-run（件数と ID）。実行は明示のフラグ。冪等。

## 着手前の調査

- AST の保存（AST の develop を読んだ）: `KnowledgeBaseWriterSink.ToDocument` が属性 `kind = item.Kind.ToString()`（`Quote`）・`source`・`publishedAt`・`symbol` を付け、
  書き手が `project = ai-stock-trading` を付ける。→ `kind` は `Quote` の完全一致で選べる（大文字小文字は区別する。他の kind は `News`・`Disclosure`・`SupplyDemand`・
  `MacroIndicator`・`SourceStatus`、報告書の `Daily` など）。
- 既存のアーカイブ手段: `Document.Archive(changeNote)`・発行の門 `PublishUpdatedIfIndexableAsync`・一括アーカイブの口 `archive-bodyless`（IADR-0549）・駆動器の本体（IADR-0556 決定 6）。
  アーカイブした文書は検索・RAG・グラフ・Wiki・MCP の一覧から外れる（IADR-0551）。
- `archive-bodyless` の口そのものは母集合が「期限より前・本文の NoSuchKey」で固定されており、選び方が違う。→ 母集合だけを変えた口を足す（下記）。

## 方式

1. 口 `POST /documents/archive-ast-quotes`（DocumentService・`AdminOnly`・`write` 群）。要求は `dryRun`（必須）・`limit`（1〜200）・`cursor`・`requestedBy`・`reason`（アーカイブは必須）。
   母集合はアーカイブ済みでない ∩ `project = ai-stock-trading` ∩ `kind = Quote` ∩ 組織文書（条件は口の側で固定）。ページ送りは `DocumentPageQuery.ReadPageAsync`。
   アーカイブは `Document.Archive(ChangeNote)` → 保存 → `PublishUpdatedIfIndexableAsync`（IADR-0549 の口と同じ並び）。本文は読まない。
   応答は `selected`・`archived`・`ids`・`nextCursor`。監査は文書ごとに 1 行（ID・作成日時・主体・`requestedBy`・`reason`）とページごとに 1 行。
2. 駆動スクリプト `scripts/archive-ast-quote-documents.js`: `lib/admin-page-driver.js` の profile。dry-run はページごとに対象の ID を並べ、最後に件数を出す。
3. IADR は新しく起こさない。IADR-0549 に日付つきの追記を足す（同じ手段の適用であり、決定は母集合の選び方だけ）。

## 対象外

- 他の kind・他の project・個人資料・アーカイブ済み。削除（Delete）。検索の契約への否定の絞り込みの追加。AST 側の変更。計画への環流（不要）。

## 規則 9・10 の母集合

- `git grep -l "archive-bodyless"` と `git grep -l "strip-untrusted-boundary"`（#1973）で、口・駆動器を並べる場所を引いた: `docs/api/openapi.yaml`・機能仕様書（口の表と節）・
  テスト仕様書・運用仕様書・`scripts/README.md`（2 つの表）・`scripts/live-scripts.json`（入口と本体の注記）・`scripts/scripts.repo.test.js`（検査器でない入口の一覧）・`DocumentEndpoints.cs`。
  すべてに足した。IADR-0549 の本文は凍結の対象外の日付つきの追記で足した。
- 規則 10: 駆動器の本体の注記（「呼ぶのは live の … で」）に新しい入口を足した。

## 受け入れ基準 → 試験

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| 1 | 管理者だけが呼べる | `ArchiveAstQuotesEndpointTests.管理者だけが…`・`口は_AdminOnly_を積む`（T-103） |
| 2 | 必須の指定が無い呼び出しを通さない | `必須の指定が無い_…`（T-104） |
| 3 | dry-run は件数と ID だけ。本番は AST の Quote だけをアーカイブし、他の kind・大文字小文字違い・kind なし・他の project・project なし・個人資料・アーカイブ済みに触らない。本文は読まない | `dry_run_は件数と対象のIDだけを…`（T-105） |
| 4 | 冪等・取りこぼしと重複が無い | `二回流しても…`（T-106） |
| 5 | 駆動器: dry-run は件数と ID・カナリアからの本番・再実行の冪等・引数と状態ファイルの取り違え | `scripts.repo.test.js` の #1974 の節（T-107） |

## 変異試験（`--filter FullyQualifiedName~ArchiveAstQuotes` の 10 件。1 つずつ入れて戻した）

| 変異 | 結果 |
| --- | --- |
| `AdminOnly` を外す | 赤（2） |
| dry-run でもアーカイブする | 赤（1） |
| アーカイブ済みを母集合から外さない | 赤（2） |
| kind の絞り込みを外す | 赤（2） |
| project の絞り込みを外す | 赤（1） |

## 残余

- 保存の後の発行が途中で失敗した文書は再実行で拾われない（IADR-0549 と同じ受容）。回収はログの ID ではなく**母集合で再発行する**: `republish-document-updated.js --attr project=ai-stock-trading --attr kind=Quote --status archived`（冪等。発行がページの途中で失敗すると、それより後の文書は台帳ではアーカイブ済みだが監査の行が出ないので、ID では拾えない。`--ids` の上限 500 件にも当たらない。#1982 のレビュー）。
- アーカイブ解除は無い（IADR-0549 決定 6）。
