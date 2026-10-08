---
title: IADR-0512 検索の出口は検索の用途の露出属性で落とし、AI 入力の用途は信頼された中継者が gRPC で指定したときだけ受ける
type: impl-adr
status: Accepted
related_ids:
  - FR-03
  - FR-04
  - FR-19
  - FR-21
  - NFR-09
  - ADR-0061
  - ADR-0086
  - ADR-0089
  - ADR-0127
  - IADR-0283
  - IADR-0379
  - IADR-0396
  - IADR-0426
  - IADR-0497
  - IADR-0498
author: claude
created: 2026-10-08
updated: 2026-10-08
---

# IADR-0512: 検索の出口は検索の用途の露出属性で落とし、AI 入力の用途は信頼された中継者が gRPC で指定したときだけ受ける

- 状態: Accepted
- 日付: 2026-10-08
- 決定者: 実装エージェント（worker）／#1752

## 起点・関連

- 関連する計画書: `FR-19`（露出 3 トグル〔横断検索／ナレッジグラフ／AI 入力〕は**独立に設定できる**）、
  `FR-21` 受け入れ基準 ⑨（「横断検索に含める」ON・「AI の入力に含める」OFF は検索結果に出て RAG の文脈に入らない）、
  `ADR-0061` 決定 3（用途の別は索引を分けずに文書属性で表し、**消費側の各経路が自分の属性を見て弾く**）、
  `ADR-0086` 決定 1（east-west gRPC は利用者文脈を本文で運び、呼び出し先が自分で判定する）、`ADR-0089` 決定 1（REST 実装の退役で経路が解ける）
- 関連する実装ADR: IADR-0396 決定 6（`HybridSearchService.Finish` を「横断検索に含める」の評価点にした）、
  IADR-0283 決定 3（AI 入力の除外は `RagOrchestrator` の 1 点）、IADR-0426（利用者文脈は入口が決めて段まで引数で運ぶ。追記 1: 信頼された中継者に限る）、
  IADR-0498（再順位付けの段は `FinishAsync` に挟まる）、IADR-0497（残余に本件が記録されている）。いずれも本文は書き換えない
- 関連する実装仕様書: [20261008 作業仕様書](../specs/20261008_1752_rag-ai-input-exposure-purpose.md)
- 起点 issue: #1752（#1746 / PR #1751 の独立監査 🟡4）

## コンテキストと課題

RAG（AI 分析の `RagOrchestrator`）は、文脈の候補を**横断検索と同じ口**（REST `POST /search`・gRPC `DocumentSearch/Search`）から取る。
その出口 `HybridSearchService.Finish`（と再順位付けの手前の `FinishAsync`）は IADR-0396 決定 6 で**常に `search_exposure`** で落とす形になった。
その結果、「横断検索に含める」OFF・「AI の入力に含める」ON の個人資料は、RAG 側の選別（`AiInputExposure.IsAllowed`）に届く前に消える。
FR-19 の独立性・ADR-0061 決定 3 の「各経路が**自分の**属性を見る」に反する（fail-closed の向きなので漏れは無い）。

計画の意味は曖昧ではない（作業仕様書 §計画の裁定が要るか）。決めるのは**用途をどう検索の出口まで届けるか**である。

## 検討した選択肢

| # | 案 | 採否 |
| --- | --- | --- |
| A | 出口の絞りを外し、各呼び出し元に任せる | 不採用。一覧の出口（BFF・MCP・REST）がそれぞれ `search_exposure` を書く形になり、IADR-0396 決定 6 が避けた「経路ごとに書いて落とし忘れる」へ戻る |
| B | **検索の用途を入口が決め、利用者文脈の器で出口まで運び、出口が用途の属性で落とす** | **採用** |
| C | RAG 用に別の検索の口（別 rpc・別端点）を設ける | 不採用。検索が 2 つになり、ABAC・二段検索・再順位付けの段を両方に通す必要が生じる（IADR-0426 決定 3「検索を 2 つにしない」に反する） |
| D | gRPC の面に来た要求は暗黙に AI 入力の用途とする（面の呼び出し元は AI 分析だけ） | 不採用。許可集合（`DocumentSearch:TrustedUserContextClients`）は構成で開く。一覧のための中継者を足した日に、一覧へ `search_exposure` OFF の資料が**黙って**出る |

### B の中の分岐: 用途を運ぶ器

| # | 案 | 採否 |
| --- | --- | --- |
| B1 | `SearchRequest`（DTO）に項目を足す | 不採用。REST の本文にそのまま束縛される型であり、利用者が用途を名乗れる |
| B2 | `IHybridSearchService.SearchAsync` に引数を足す | 不採用。ポートの全実装と多数の試験の呼び出しが変わる。器 B3 と同じ情報を 2 つの引数に分ける理由が無い |
| B3 | **`SearchUserContext` に持たせる**（入口が決めて段まで必須引数で運ぶ器。IADR-0426 決定 2） | **採用** |

## 決定

### 決定 1: 出口は `SearchUserContext.ExposureKey` の露出属性で落とす

- `SearchUserContext.ExposureKey` は露出の投影キーそのもの（`DocumentExposure.SearchKey` / `AiKey`）。
  `FinishAsync`（段の手前の絞り）と `Finish`（切り詰め前の絞り）は `DocumentExposure.IsAllowed(attributes, user.ExposureKey)` を呼ぶ。
  **述語の単一情報源（`DocumentExposure`）は変えない。** 出口は 1 つのまま（素の検索・二段検索の 3 つの return がすべて通る）。
- 🔴 **既定は `SearchKey`**。AI 入力へ切り替える口は `ForAiInput()` の 1 つだけ（`private init`）。
  入口が何も言わなければ一覧の用途へ倒れる —— 取り違えたときに「一覧に出てはならない資料が出る」側ではなく
  「RAG に入るべき資料が入らない」側（従前と同じ fail-closed）になる。

### 決定 2: AI 入力の用途は、信頼された中継者が gRPC で指定したときだけ受ける

- 用途を受け付けるのは gRPC `DocumentSearch/Search` の受け口だけで、`EnsureTrustedRelay`（IADR-0426 追記 1）を通った後に読む。
  **用途は利用者文脈と同じ信頼の下で運ばれる**（ADR-0086 §結果が受け入れた「中継者が正直であること」への依存の範囲を広げない）。
- MCP のツール実行（`retrieval.search_documents`）は用途を変えない（一覧の用途のまま）。

### 決定 3: proto に `purpose`（列挙）を非破壊で足し、`AI_INPUT` だけを AI 入力へ写す

- `knowledge.retrieval.v1.SearchRequest.purpose = 5`（`ExposurePurpose`: `UNSPECIFIED = 0` / `SEARCH = 1` / `AI_INPUT = 2`）。
- 🔴 **`AI_INPUT` 以外（未指定・`SEARCH`・知らない番号）は一覧の用途**。proto3 の enum は開いており、知らない番号もそのまま届く。
  旧い呼び出し元（`purpose` を知らない）は従来どおり動く。
- AI 分析の gRPC 輸送（`GrpcRagSearchTransport`）は `AI_INPUT` を付けて送る。RAG 側の `AiInputExposure` による選別（IADR-0283 決定 3）は残す
  —— 出口が AI 入力で落とすので選別は冗長になるが、輸送を取り違えたとき（REST 輸送・旧い検索サービス）の最後の守りである。

### 決定 4: REST の面は用途を受けない

- REST 輸送（`HttpRagSearchTransport`）は**利用者の JWT を転送する**。受け口から見た呼び出し元は利用者本人であり、
  「AI 分析が中継している」ことを区別できない。用途を受けると、利用者が AI 入力を名乗って `search_exposure` OFF の資料を一覧で見られる。
- したがって REST 輸送では本件は直らない（従来どおり fail-closed）。配備（compose・helm）は AI 分析に `Services__RetrievalServiceGrpc` を
  構成しており gRPC 輸送で直る。REST 輸送の退役（ADR-0089 決定 1）で解消する。

## 結果

- 「横断検索に含める」OFF・「AI の入力に含める」ON の個人資料が、gRPC 輸送の RAG の文脈に入る（FR-19 の独立性が RAG の経路でも成り立つ）。
- RAG の候補から「AI の入力に含める」OFF の資料が出口で落ちるので、`topK` の枠がそれらに使われなくなる（文脈の件数が従前以上になる）。
  RAG 側の除外ログ（`RAG context: … excluded`）に ai_input 起因の除外がほぼ現れなくなる。
- 再順位付けの段（有効時）は RAG の検索で AI 入力 ON の候補を受け取る。段は元々 `ai_input` が許す候補だけを送るので、送る集合の規則は変わらない。
- 一覧（REST・MCP・用途を言わない gRPC）の挙動は 1 ビットも変わらない。

### 試験（変異で確かめた）

- `PrivateNoteAiInputPurposeTests`（単体）・`DocumentSearchExposurePurposeTests`（実 Kestrel の gRPC 往復）・
  `GrpcRagSearchTransportTests`（`用途をAIの入力として送る`）。
- 出口の述語を `IsSearchAllowed` 固定へ戻す変異で 5 件が赤。`WithPurpose` を「`SEARCH` 以外は AI 入力」へ変える変異で 4 件が赤（未指定・未知の値）。

### 残るもの

1. **REST 輸送では直らない**（決定 4）。REST 輸送へ切り戻した環境では従前どおり RAG に入らない。
2. 要約（FR-19 の「AI 入力（RAG 回答・要約）」の要約側）は本件の経路と別であり、本決定の対象外。
3. MCP のツールの検索結果は外部の AI クライアントへ渡るが、一覧の用途のまま扱う（決定 2）。AI 入力として扱うべきかは計画の裁定事項であり、本件では変えない。
