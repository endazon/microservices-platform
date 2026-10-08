---
title: 作業仕様書 — 「横断検索に含める」OFF・「AI の入力に含める」ON の個人資料を RAG の文脈へ届ける（検索の用途を出口で選ぶ。#1752）
type: spec
status: done
related_ids: [FR-19, FR-21, FR-03, FR-04, ADR-0061, ADR-0086, IADR-0283, IADR-0396, IADR-0426, IADR-0498, IADR-0512]
author: claude
created: 2026-10-08
updated: 2026-10-08
issue: "#1752"
---

# 作業仕様書 — RAG の候補は「AI の入力に含める」で選ぶ（#1752）

> 本仕様書は実装着手前に作成した（着手 2026-10-08）。基点は MSP `origin/develop` `6b5e6abf`。
> 計画は project-planning `main` `b5b584f`（GitHub API で読み取り専用に参照）。

## 起点（トレーサビリティ）

- **FR-19**: 「横断検索への包含・ナレッジグラフへの表示・AI 入力（RAG 回答・要約）への包含の 3 つを**独立に設定でき**、既定はいずれも OFF」。
- **FR-21 受け入れ基準 ⑨**: 「横断検索に含める」ON・「AI の入力に含める」OFF の個人資料は検索結果に現れ RAG の文脈に入らない（逆向きは基準に無い）。
- **計画 ADR-0061 決定 3**: 「用途の別は索引を分けずに文書属性で表し、**消費側の各経路が自分の属性を見て弾く**」。
- 計画 ADR-0086 決定 1（east-west gRPC は利用者文脈を本文で運び、呼び出し先が自分で判定する）。
- 実装: IADR-0396 決定 6（`Finish` を横断検索の評価点にした）・IADR-0283 決定 3（AI 入力の除外は `RagOrchestrator` の 1 点）・
  IADR-0426（利用者文脈は入口が決めて段まで引数で運ぶ。追記 1 で信頼された中継者に限る）・IADR-0498（再順位付けの段は `FinishAsync` に挟まる）。
  いずれも凍結記録として書き換えない。新しい判断は IADR-0512 に置く。

### 計画の裁定が要るか（確認結果: 要らない）

FR-19 は 3 トグルを**独立**と定め、ADR-0061 決定 3 は**各経路が自分の属性を見る**と定めている。
RAG の経路が見るべき属性は `ai_input` であり、`search_exposure` を RAG の経路で見る根拠は計画のどこにも無い。
したがって「`search_exposure` OFF・`ai_input` ON の資料は RAG の文脈に入る」が計画の意味であり、現状は実装の不具合である。
FR-21 ⑨ が逆向きを書いていないのは ⑨ が FR-21（本文の直接受け入れ）の基準だからで、FR-19 の独立性と矛盾しない。**環流は不要**と判断した。

## 現状（実測）

| `search_exposure` | `ai_input` | 横断検索 | RAG の文脈 | 計画の期待 |
| --- | --- | --- | --- | --- |
| ON | OFF | 出る | 出ない | 一致 |
| ON | ON | 出る | 出る | 一致 |
| **OFF** | **ON** | 出ない | **出ない** | 🔴 不一致 |

原因: RAG（`RagOrchestrator`）は横断検索と同じ口（REST `POST /search`・gRPC `DocumentSearch/Search`）から候補を取り、
その出口 `HybridSearchService.Finish` / `FinishAsync` が常に `DocumentExposure.IsSearchAllowed` で落とす。

## 受け入れ基準

- AC1: gRPC `DocumentSearch/Search` に**用途 AI 入力**を指定した検索は、`search_exposure` OFF・`ai_input` ON の個人資料を返す。
- AC2: 同じ検索は `ai_input` OFF の個人資料を返さない（`search_exposure` ON でも）。組織文書（露出キーなし）は返す。
- AC3: 用途を指定しない（未指定・既定・未知の値）gRPC 検索と REST `POST /search` は従来どおり `search_exposure` で落とす
  —— 横断検索の一覧に `search_exposure` OFF の資料は出ない。**REST は用途を受け付けない**（本文に何を書いても横断検索の用途）。
- AC4: 用途は**信頼された中継者**（`DocumentSearch:TrustedUserContextClients`）からしか受けない。信頼されない呼び出し元は用途の解釈より前に
  PERMISSION_DENIED になる（既存の門。用途 AI 入力を付けても変わらない）。
- AC5: 二段検索（`GraphExpandingSearchService`）の 3 つの出口と、再順位付けの段の手前の絞り込みも同じ用途で落とす（出口は 1 つのまま）。
- AC6: AI 分析の gRPC 輸送（`GrpcRagSearchTransport`）は用途 AI 入力を付けて送る。RAG 側の `AiInputExposure` による選別（IADR-0283）は残す。
- AC7: AC1〜AC6 を xUnit で固定し、出口の述語を `IsSearchAllowed` 固定へ戻す変異で AC1 の試験が落ちることを確かめる。
- AC8: 母集合（下）の live な文書・コメントを新しい挙動へ揃える。`docs/` は trace ブロックを守り `updated:` を進める。

## 設計（判断は IADR-0512）

1. **用途は検索の入口が決め、利用者文脈の器（`SearchUserContext`）で段と出口まで運ぶ。**
   値は露出の投影キー（`DocumentExposure.SearchKey` / `AiKey`）そのもの。既定は `SearchKey`、AI 入力へ切り替える口は `ForAiInput()` の 1 つだけ。
2. **出口（`FinishAsync` → `Finish`）は `DocumentExposure.IsAllowed(attributes, user.ExposureKey)` で落とす。** 述語の単一情報源は変えない。
3. **gRPC 面に `purpose`（列挙。0 = 未指定 = 横断検索）を足す。** 非破壊の追加（番号 5）。
   受け口は信頼された中継者の門を通った後でだけ解釈し、`AI_INPUT` 以外（未指定・`SEARCH`・未知の値）は横断検索の用途へ倒す。
4. **REST 面には足さない。** REST 輸送は利用者の JWT を転送するので、受け口から見て呼び出し元は利用者本人であり、
   「AI 分析が中継している」ことを区別できない。用途を受けると、利用者が自分で AI 入力を名乗って `search_exposure` OFF の資料を一覧で見られる。
5. MCP のツール実行（`retrieval.search_documents`）は用途を変えない（横断検索のまま）。

## 母集合（規則 9・10。誤りの側の文字列で走査した結果）

走査語: `IsSearchAllowed` / `露出の用途` / `用途 \`search\`` / `横断検索に含める.*OFF` / `「横断検索に含める」で落とした後`（`git grep`。specs/superpowers 除外）。

| 対象 | 扱い |
| --- | --- |
| `HybridSearchService.cs`（`FinishAsync` / `Finish` と注記） | 直す（本体） |
| `GraphExpandingSearchService.cs`（3 つの `FinishAsync`） | `user` を渡す |
| `ISearchReranker.cs` の注記（「露出の用途 `search` で落とした後」） | 用途に読み替える |
| `GrpcService.cs`（受け口）・`document_search.proto` | 用途を足す |
| `GrpcRagSearchTransport.cs`（AI 分析の輸送） | 用途 AI 入力を付ける |
| `SearchUserContext.cs` | 用途を持たせる |
| `docs/functional/FR-03_hybrid-search.md`（再順位付けの候補の説明 2 か所） | 用途に読み替える |
| `docs/functional/FR-19_private-notes.md` §露出の判定 2 | 逆向きの組み合わせを書き足す |
| `docs/api/east-west-grpc.md` 11 面 | `purpose` を書き足す |
| `docs/tests/FR-21_direct-body-intake.md` | T-29〜を足す |
| `docs/tests/FR-03_hybrid-search.md` T-102 / T-103 | **直さない**（横断検索の用途の記述として真のまま） |
| `.ai-context/adr/IADR-0396` / `0497` / `0498` | **直さない**（凍結記録。IADR-0497 の残余に本件が記録されている） |
| 試験の注記（`ClaudeRerankTests` の「露出の用途 search」） | **直さない**（横断検索の用途の試験として真のまま） |

## 試験

- `RetrievalService.Tests`: 新規 `PrivateNoteAiInputPurposeTests`（出口の用途別の落とし方・二段検索・再順位付けの手前）、
  `DocumentSearchTrustedRelayTests` / `GrpcDocumentSearchTests` の流儀で gRPC 面の用途の写し（`AI_INPUT` / 未指定 / 未知の値・信頼されない呼び出し元）。
- `AiAnalysisService.Tests`: `GrpcRagSearchTransport` が `purpose = AI_INPUT` を送ること。
- ［2026-10-08 追記 / 独立監査 🟡］AC3 の REST の半分: `DocumentSearchExposurePurposeTests` で REST `POST /search` の本文に用途を名乗る項目を足しても
  「横断検索に含める」OFF の資料が返らないこと（T-34）と、`SearchRequest` に用途の項目が無いことを固定する。

## 射程外・残余

- **REST 輸送（`HttpRagSearchTransport`）では直らない。** compose・helm とも AI 分析は gRPC 輸送を構成しており、配備の経路では直る。
  REST 輸送は構成を外したときの切り戻し先であり、そこでは従来どおり「`search_exposure` OFF の資料は RAG に入らない」（fail-closed）。
  REST 輸送の退役（ADR-0089 決定 1）で解消する。
- 要約（FR-19 の「AI 入力（RAG 回答・要約）」の要約側）は本件の経路と別であり触らない。
