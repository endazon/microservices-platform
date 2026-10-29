---
title: 作業仕様書 — 本文の応答 DocumentContentDto から SourceUri（中身は markdownUri）を外す（#2289）
type: spec
status: done
related_ids: [FR-06, SC-03, IADR-0122, IADR-0544, IADR-0561]
author: claude
created: 2026-10-29
updated: 2026-10-29
issue: "#2289"
---

# 作業仕様書 — DocumentContentDto.SourceUri を契約から外す（#2289）

> 本仕様書は実装着手前に作成した（着手 2026-10-29。develop `bab977e0f`）。以前の作業は環境の入れ替えで失われたので、調査からやり直した。

## 現状（実測）

- 契約: `Knowledge.Contracts/Dtos/DocumentDto.cs` の `DocumentContentDto(Guid Id, string Title, string Markdown, string? SourceUri)`。
- 書き手は BFF の `Documents/DocumentBodyProblems.cs` だけ（`new DocumentContentDto(doc.Id, doc.Title, content.Markdown, doc.MarkdownUri)`）。
  - `SourceUri` に正規化済み Markdown の参照（`storage://…`）が入る。名前は「出典元」と読めるが、中身は原本ではない。
  - 原本の所在は `DocumentDto.OriginalUri`（台帳。IADR-0561）が持つ。
- SC-03 は #2288 以降 `doc.originalUri` だけを読む（`DocumentDetailPage.tsx`）。

## 読み手の走査（規則 9。誤りの側の文字列 `DocumentContentDto` / `sourceUri` / `SourceUri` / `/content` で引いた）

| 範囲 | 走査 | 結果 |
| --- | --- | --- |
| MSP 全体 | `git grep -n DocumentContentDto` | 契約・BFF の書き手・BFF の試験 3 本（`BffDocumentEndpointTests`・`BffDocumentContentTests`・`BffBreakGlassEndpointTests`。後 2 本は `Markdown` 等だけを読む）・openapi・orval 生成物・frontend の型参照（SC-03 `useDocumentQueries.ts` / `DocumentDetailPage.tsx`、SC-05 `DocumentPreviewPanel.tsx`。いずれも `markdown` 等だけを読む）・e2e の型付きの写し 3 本（`sourceUri` を持たない）・文書・凍結記録 |
| frontend の読み（本番コード） | `git grep -n -i sourceUri -- src/knowledge/frontend src/platform/frontend ':!*generated*'` で `Citation` 以外 | **本文の `sourceUri` を読む本番コードは 0**。`DocumentDetailPage.tsx` の変数名 `sourceUri` は `doc.originalUri` を入れる局所変数。SC-02 `SearchResultsPage.tsx` と `citations.ts` は出典（`CitationDto.sourceUri`。別の契約で対象外） |
| frontend の試験の写し | 同上 | 本文の写しに `sourceUri` を書いた試験 4 本（`DocumentDetailPage.test.tsx`・`adminFlow.test.tsx`・`searchFlow.test.tsx` の `CONTENT`・`DocumentManagementPage.test.tsx` の 2 か所）。読む主張は無い（#2121 の陰性対照は「正規化済み Markdown を原本に出さない」で、外したあとも主張は成り立つ）→ 写しから外す |
| orval 生成物 | `bff.schemas.ts` の `DocumentContentDto.sourceUri`・`documents.faker.ts` のモック | `pnpm run codegen` で再生成する |
| BFF | `git grep DocumentContentDto -- src/*/backend` | 書き手 1・試験 3（上）。`SourceUri` を読むのは `GetContent_WhenAuthorized_ReturnsMarkdownAndSourceUri` だけ |
| MCP | `document.get_document`（`DocumentService/Features/McpTools/Execute/GrpcService.cs`） | 文書サービスの `DocumentDto` を返す。`DocumentContentDto` は BFF だけが作るので無関係 |
| docs/api/openapi.yaml | `DocumentContentDto.sourceUri` | 外す（下の「openapi の扱い」） |
| AST（submodule pin `a003a190` と AST origin/develop の両方） | `git grep -i "DocumentContentDto\|/content\|SourceUri"` | AST は文書サービスの `/documents`・`/documents/{id}/body` を直接呼ぶ（`HttpKnowledgeDocumentCatalog.cs` / `HttpKnowledgeBaseWriter.cs`）。BFF の `/content` も `DocumentContentDto` も読まない。`SourceUri` の一致は AST 自身の型（検索の当たり・書き込み要求）だけ |
| 生きた文書 | `docs/screens/SC-03_document-detail.md` の `DocumentContentDto = { id, title, markdown, sourceUri? }` | 追随して外す |

- 除外: `.ai-context/specs/`（凍結記録。#2121・#506・#129 の記述は当時の事実）と `CHANGELOG.md`（生成物）。
- **外部公開の契約か**: `/bff/documents/{id}/content` は BFF（SPA 専用の north-south。BFF セッション）の口で、読み手はこのリポジトリの SPA だけである。AST も MCP も読まない。外部へ公開して版を約束した契約ではないので、削除を止める条件には当たらない。

## 方針

- **(a) 外す**を採る。改名（b）は、中身（正規化済み Markdown の参照）が要る読み手が 0 なので、使われない項目を名前だけ直して残すことになる。`DocumentDto.MarkdownUri` が同じ値を既に持っている。
- 破壊的変更の承認は既存の仕組み（`scripts/contract-breaking-allowlist.json` に承認エントリを書き、`check-contract-schema.js --update` で baseline の `$acceptedBreakingChanges` へ移す。IADR-0122 決定 3）に従う。
- **openapi の扱い**: `docs/api/openapi.yaml` は手書きの仕様である（`openapi.yml` の注記どおり、コードからの生成コマンド `scripts/generate-openapi.sh` は存在しない）。生成手段が無いので手で外し、`check-openapi-dto-drift.js` で DTO との一致を確かめる。orval の生成物は `pnpm run codegen` で再生成する（手で書かない）。

## 受け入れ基準

- [x] 本文の応答の JSON に `sourceUri` が載らない（BFF の試験。修正前は `storage://bucket/expense.md` が載って赤）
- [x] 本文の応答は引き続き `id` / `title` / `markdown` を返す
- [x] 契約の検査器の baseline が更新され、承認の記録が `$acceptedBreakingChanges` に残る
- [x] openapi・orval の生成物・frontend の試験の写し・SC-03 の画面設計書が追随している（`check-openapi-dto-drift`・codegen の差分なし・typecheck）

## 規則 10（この変更で新たに誤りになる自分の記述）

- `DocumentDetailPage.tsx` のコメント「本文の応答の `sourceUri` の中身は `markdownUri`」→ 項目が無くなるので書き直す。
- `DocumentDetailPage.test.tsx` の `CONTENT` のコメント「BFF は本文の応答の sourceUri に markdownUri を入れる」→ 外す。
- BFF の試験名 `GetContent_WhenAuthorized_ReturnsMarkdownAndSourceUri` → 名前を主張に合わせる。

## 規則 11（窓）

- 時間差を扱う是正ではない。表は描かない。
