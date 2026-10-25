---
title: 作業仕様書 — 文書の表題（個人資料を含む）を Information ログへ出さない（#2131）
type: spec
status: done
related_ids: [NFR-28, NFR-19, FR-02, FR-12, FR-19, IADR-0541, IADR-0603, IADR-0295]
author: claude
created: 2026-10-25
updated: 2026-10-25
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（NFR-28「対象は LLM の生出力・外部 API の応答本文・利用者が入力した文書メタデータなど」・NFR-19）
---

# 仕様書: 文書の表題を Information ログへ出さない（#2131）

## 起点となる計画書（トレーサビリティ）

- 要求: NFR-28（ログ・監査記録の完全性。対象に「利用者が入力した文書メタデータ」を含む）、NFR-19（可観測性。ログは Loki へ集約され保持される）
- 関係する機能: FR-02（取り込み）、FR-12（変換）、FR-19（個人資料。表題が個人の資料の題名になる）
- 親: #2047（指摘 M-26）
- 実装 ADR: **新規 IADR は作らない。** 判断は既存の [IADR-0541](../adr/IADR-0541_wiki-slug-unique-and-permanent-sync-failures.md) の結果の注記
  （「成功時のログに slug を出さない —— slug は題名から作るので、機密文書の題名が毎回ログに入る。slug を出すのは異常時の行だけ」）と
  `Knowledge.Bff.Endpoints/Documents/DocumentReadFailureLog.cs` の規約（「文書の本文や表題も載せない」）の適用であり、新しい選択肢の比較を伴わない。

## 目的・背景

IngestionService（`DocumentUpdatedConsumer`）と DocumentService（`DocumentNormalizedConsumer`）が、文書を 1 件処理するたびに
`title={Title}` を **Information** で出していた。Information は既定で出力・集約される水準であり、表題（個人資料の題名を含む）が
ログ基盤へ恒久に積もる。表題は利用者が入力した文書メタデータであり、NFR-28 が無害化を求める「系の外から来る文字列」でもある。
自リポの規約（`DocumentReadFailureLog`・IADR-0541）は既に「表題を載せない」側にあり、この 2 行はそれと矛盾していた。

## 母集合（規則 9: 誤りの側の文字列で全数を引く）

非試験の `src/**/*.cs` にあるログ呼び出し（`Log{Information,Warning,Error,Debug,Trace,Critical}(` / `.Log(`）を**呼び出しの括弧の終わりまで**
切り出して走査した（1 行 grep は複数行の呼び出しを取りこぼすため）。走査器は 2 本（作業用・コミットしない）:

1. **置換子の名前**の全数（`{Title}` 等。全 280 種余り）から、表題・本文・利用者入力を運び得る名前を抜き出した:
   `Title` `Path` `Name` `Input` `Detail` `Slug` `Tag(s)` `Value` `Field` `Subject` `AuditDetail` `Error` `Url` `File` `Claim` `Section` `Key` `Root` `Dir` `Uri`。
2. **引数の式**に `.Title` / `OriginalPath` / `.Body` / `.Content` / `markdown` / `.Text` / `.Query` / `.Question` / `.Prompt` / `.Instruction` / `.Answer` / `Snippet` / `.Name` / `.Message` を含む呼び出し（`.Length` だけを取るものと、`Redact` を通した `.Message` は除く）。

結果:

| # | 箇所 | 水準 | 運ぶもの | 扱い |
| --- | --- | --- | --- | --- |
| 1 | `IngestionService/.../Ingest/DocumentUpdatedConsumer.cs:152` `Ingesting document {Id} title={Title}` | Information | 表題 | **直す**（文書 ID だけにする） |
| 2 | `DocumentService/.../Catalog/DocumentNormalizedConsumer.cs:104` `Cataloged normalized document {Id} title={Title}` | Information | 表題 | **直す**（同上） |
| 3 | `ConversionService/.../Normalize/RawDocumentFetchedConsumer.cs:33` `Converting raw document: ... Path={Path}` | Information | 原本の所在（`OriginalPath`）。**表題はこの所在のファイル名から作られる**（`RawDocumentFetchedConsumerTests` が `/docs/test.docx` → 表題 `test` を固定） | **直す**（取得 ID `FetchId` に置き換える。所在は変換ジョブの台帳が持つ） |
| 4 | 同 `:85` `Conversion rejected for {FetchId}: {Path} ...` | Warning | 原本の所在 | 残す（異常時の行。IADR-0541 の線引き） |
| 5 | `DataSourceService/.../Sync/DataSourceSyncService.cs:202` `原本の取得/発行に失敗: {Path}` | Warning | 原本の所在 | 残す（同上） |
| 6 | `DataSourceService/.../FileSystemConnector.cs:93` `ファイル '{File}' の情報取得に失敗` | Warning | ファイルの所在 | 残す（同上） |
| 7 | `WikiService/.../DocumentSyncConsumer.cs:323` `slug {Slug} was taken concurrently` / `:350` `slug {Slug} still violates` | Information / Warning | 題名由来の slug | 残す（IADR-0541 が「異常時の行だけに出す」と決めた行そのもの。:323 は並行競合の稀な分岐） |
| 8 | `DocumentService/.../Catalog/DocumentNormalizedConsumer.cs:154` `辞書に無いタグが現れた: {Tag}` | Warning | タグ名 | 残す（異常時の行。表題でも本文でもない。規定上 0 件が正常の検出口） |
| 9 | `WikiService` の `{Path}`（`WikiJsGraphQlClient` 8 行・`DocumentSyncConsumer:231,243`） | Information | 正準パス `doc/<文書 ID>`（`WikiPage.PathFor`） | 対象外（ID 由来で表題を含まない） |
| 10 | `{Uri}` 28 行（`S3ObjectStorageClient` ほか） | Information | オブジェクトの鍵 | 対象外（鍵は `{source}/{fetch}/raw.<拡張子>`・`documents/{id}/body.md`・`{id}/assets/...` の ID 由来。拡張子だけが原本から来る） |
| 11 | `.Name` 22 行（Vault・ExternalSecret・ルーティング・パイプライン段・データソース名） | 各種 | 設定名・型名・管理者が付けたデータソース名 | 対象外（文書の表題・本文・利用者の入力ではない） |
| 12 | 本文（`.Body` / `.Content` / `markdown` / `.Text`） | — | — | **0 件**（長さ・件数だけを出している） |

**今回やらないものとその理由**:

- #4〜#8（異常時の行の所在・slug・タグ）: IADR-0541 の結果の注記が「成功時の行に出さず、異常時の行にだけ出す」と線を引いており、
  運用者が原本を直すには所在が要る。異常時の行からも外すかは別の判断（新 IADR）であり、本 issue の射程（恒久に積もる Information）を超える。
- NFR-28 の恒久統制（外部由来の文字列を発生源で無害化する）の全面配備: 本 PR は表題を**出さなくする**ので、当該 3 行については無害化の対象自体が消える。全面配備は別の作業。
- AST（submodule）: 別リポジトリであり本 issue の射程外。

## 受け入れ基準

- [x] A1: `DocumentUpdatedConsumer` が文書を取り込むとき、**どの水準のログ**（書式化後の文言と構造化の値の両方）にも表題が現れない。文書 ID は Information の行に現れる（行そのものは残る＝陰性対照）。
- [x] A2: `DocumentNormalizedConsumer` が新規の文書を台帳に載せるとき、同上。
- [x] A3: `RawDocumentFetchedConsumer` が原本を変換するとき、**どの水準のログ**にも原本の所在と、そこから作られる表題が現れない。取得 ID は Information の行に現れる。
- [x] A4（境界）: 表題に改行・制御文字・`U+2028` を含めても A1〜A3 が成り立つ（表題を出さないので、ログ行の偽装も起こらない）。
- [x] A5（変異）: 3 箇所のいずれかを元の形（表題・所在を出す）へ戻すと、対応する試験が赤になる。
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes`（knowledge ユニット）が通る。

## PoC の事前手順

**不要。** ログの文言だけの変更で、スキーマ・設定・配備物は変えない。既に Loki に積もった過去の表題は本変更では消えない（保持期間の経過で消える）。

## 結果（実施後に記入）

- 変更: 3 行（`DocumentUpdatedConsumer` / `DocumentNormalizedConsumer` は文書 ID だけ、`RawDocumentFetchedConsumer` は所在を取得 ID に置換）。
- 試験: 3 本（`DocumentUpdatedConsumerTests.Consumer_ShouldNotLogTitleOrOriginalPath_AtAnyLevel`・
  `CatalogTitleNotLoggedTests`・`RawDocumentFetchedConsumerTests.Consumer_does_not_log_original_path_or_title_at_any_level`）。
  全水準・書式化後の文言と構造化の値の両方を見る。境界は改行・`U+2028`・BEL を含む表題と、印を含む所在。陰性対照は鍵（文書 ID・取得 ID）が Information に残ること。
- 変異（A5）: 3 箇所それぞれを元の形へ戻すと、対応する試験が 1 本ずつ赤になった（Failed: 1）。戻して全緑。
- 全数: IngestionService 253 件・ConversionService 248 件（skip 9 は既存の外部ツール依存）・DocumentService 1212 件が緑。`dotnet format --verify-no-changes` 3 プロジェクト緑。
