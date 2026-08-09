---
title: 作業仕様書 — AnalysisRequest に対象範囲（属性フィルタ）を追加する（#539）
type: work-spec
status: in-progress
related_ids:
  - FR-04
  - FR-05
  - FR-07
  - SC-01
  - SC-08
  - UC-01
  - UC-02
  - ADR-0043
  - IADR-0151
author: claude
created: 2026-08-09
updated: 2026-08-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0043_scoped-attribute-value-lookup.md"
related_specs:
  - "./20260809_issue-540_scoped-attribute-values.md"
  - "../adr/IADR-0151_scoped-attribute-value-facets.md"
---

# 作業仕様書 — AnalysisRequest に対象範囲（属性フィルタ）を追加する（#539）

## 起点となる計画書（トレーサビリティ）

| 種別 | ID | 何を求めているか |
| --- | --- | --- |
| 画面 | **SC-01** | 主要素に**対象範囲フィルタ（タグ／部門／プロジェクト）**。入力規則は「**権限内の**タグ／`department`／`project` のみ選択可」（L181 / L186） |
| 画面 | **SC-08** | 分析対象の指定は**タグ・部門・プロジェクトのチップ**＋検索条件による追加。**候補は権限内に限り、同じ候補 API を用いる。SC-01 と一体で扱う**（L341〜342） |
| 要求 | **FR-04** | 「対象範囲の指定」を要求として明示（裁定 Q1） |
| 要求 | **FR-05** | ABAC。**範囲指定は narrowing-only で権限を一切広げない** |

**計画の言葉（L198・裁定 Q1）**:
> `SearchRequest` は既に `AttributeFilters` を持つのに `AnalysisRequest` だけが持たない非対称を解消する。

**Q9: `folder` は用いない。新設もしない。** ABAC 属性体系に `folder` が存在せず、
フォルダは取り込み時に属性へ写像されて消える。パスの階層・序数は本属性体系が意図的に排除している。

**Q2（権限内候補 API）は #540 で着地済み**（`/bff/attribute-values`）。**本 issue の射程外**である。

## 母集合（[[IADR-0141]] 決定 1）

**着手時に実装側が引き直した。走査基準: develop `7d9b0e4`（#635 マージ直後）。**

**［#635 の教訓を適用］「コンパイルエラーで全部出る」経路だけを引かない。**
本 issue は**契約にメンバーを足す**変更なので、型検査は既存の呼び出し側を 1 つも壊さない
（既定値つきの追加は非破壊。[[IADR-0122]] 決定 2）。**したがって型検査は母集合を教えてくれない。**
**HTTP / JSON で要求を組み立てている経路（フロントエンド・統合テスト）をパスから引くこと。**

### ★ 着手前の実測で分かった、issue 本文に書かれていない前提

**1. `AttributeFilters` は既に 2 系統あり、型が違う。**

| 用途 | 場所 | 型 |
| --- | --- | --- |
| 検索 | `Knowledge.Contracts/Dtos/SearchDto.cs` | `Dictionary<string, string>?`（**単値**。コメントに「FR-03: 単値完全一致フィルタ（**後方互換**）」） |
| 分析データ範囲 | `Knowledge.Contracts/Dtos/AnalysisDto.cs`（`AnalysisDataRange`） | `Dictionary<string, List<string>>?`（**多値**） |

**2. ★ 現在の検索フィルタは `tags` を絞れない。** —— **これが本 issue の中心的な発見である。**

`QdrantVectorStore.BuildAttributeConditions` は**キーを `attributes.{f.Key}` にハードコードしている**。
`InMemoryVectorStore.MatchesFilters` も `c.Attributes` しか見ない（`c.Tags` を見ていない）。実測:

```
QdrantVectorStore.cs:123   Key = $"attributes.{f.Key}",
InMemoryVectorStore.cs:79  c.Attributes.TryGetValue(f.Key, out var v)
```

一方 **#540 が入れた「値集合の照会」側は `tags` を知っている**——
`AttributeValueKeys.ToPayloadKey` が `tags` だけを例外として扱い、他を `attributes.<key>` へ写す。

**つまり「候補は出せるが、その候補で絞れない」状態である。**
`tags` を選ばせておいて絞れないと、**画面が候補として出した値が結果に効かない**（利用者から見て壊れている）。

**3. SC-08 のチップは「planning#197 の裁定待ち」として明示的に未実装である。**
`sc08-analysis/analysisRange.ts` の冒頭コメントが、**本 issue が解く論点をそのまま書いている**:
> **タグ・フォルダのチップは実装しない**（画面仕様書 §実装しない要素 (a)）——`AnalysisDataRange` は
> 属性キー → 値集合しか取らず、**タグは属性とは別の軸**、フォルダは契約に存在しない。（中略）
> SC-01 の対象範囲フィルタと**同型の論点**であり、planning#197 の裁定を待つ。

**この注記は本 issue の完了とともに消す**（残すと「未実装」と読める）。

### 触るもの（**着手後に確定させる。現時点の想定**）

| # | 対象 | 何をするか |
| --- | --- | --- |
| 1 | `Knowledge.Bff.Endpoints/AnalysisBffEndpoints.cs` | `AnalysisRequest` へ対象範囲を足す |
| 2 | `AiAnalysisService/.../Endpoints/AnalysisEndpoints.cs` | `AskRequest` へ同じものを足す（**BFF だけでは後段へ届かない**） |
| 3 | `.../Services/IRagOrchestrator.cs` ＋ `RagOrchestrator.cs` | `AskAsync` / `AskStreamAsync` が範囲を受け、**`DataRangeScopeResolver` で ABAC と交差**させる（`AnalyzeAsync` が既に採っている形） |
| 4 | `RetrievalService/.../QdrantVectorStore.cs` ＋ `InMemoryVectorStore.cs` | **`tags` を絞れるようにする**（上記 2。写像は `AttributeValueKeys.ToPayloadKey` に寄せ、照会側と 1 つの真実にする） |
| 5 | `docs/api/openapi.yaml` ＋ orval 生成物 | 契約の追随（生成物はコミットし CI が再生成差分を検査する） |
| 6 | `knowledge/frontend/.../sc01-search/useAskStream.ts` ＋ `SearchChatPage.tsx` | SC-01 の対象範囲フィルタ |
| 7 | `knowledge/frontend/.../sc08-analysis/analysisRange.ts` ＋ `AnalysisDashboardPage.tsx` | SC-08 のチップ（上記 3 の注記を消す） |
| 8 | 上記それぞれのテスト ＋ `docs/functional/FR-04*` / `docs/tests/FR-04*` / `docs/tests/SC-01*` / `docs/tests/SC-08*` | 追随 |

### 触らないもの

| 対象 | 理由 |
| --- | --- |
| `SearchRequest.AttributeFilters`（単値） | **後方互換のために残っている口**であり、本 issue は分析側の欠落を埋めるもの。単値の口を壊さない |
| `/bff/attribute-values`（#540） | **候補 API は着地済み**（裁定 Q2）。本 issue は「候補で絞る」側だけを足す |
| `folder` に相当するキー | **裁定 Q9 で明確に否定されている**（新設もしない） |
| `src/ai-stock-trading` | 別プロジェクトの submodule（`KnowledgeModels.cs` に `AttributeFilters` が出るが対象外） |

## 決めたこと（着手時の判断。IADR に残す）

### 判断 1: **型は多値（`Dictionary<string, List<string>>`）にする**

**計画の言う「非対称の解消」は「能力を持たせること」であって、単値という形を写すことではない。**根拠:

1. **画面が多値を要求する。** SC-01 は「対象範囲フィルタ（タグ／部門／プロジェクト）」、
   SC-08 は「**チップ**」であり、**利用者は複数のタグを選ぶ**。単値だと「経理」か「規程」の一方しか選べない。
2. **単値の口は自ら「後方互換」と名乗っている**（`SearchDto.cs` のコメント）。
   **後方互換のために残っている形を、新しい口の手本にしない。**
3. **交差の機構が既に多値で在る。** `DataRangeScopeResolver` は
   `AnalysisDataRange.AttributeFilters`（多値）と ABAC の多値 allow-list を交差させる。
   多値で足せば**この機構をそのまま使える**——単値にすると変換層が 1 枚増える。
4. **ABAC 側（`AccessScope` / `AttributeFilter`）が多値である。** 実効境界の表現に合わせるほうが素直である。

### 判断 2: **`tags` を絞れるようにするのは本 issue の射程に含める**

**含めないと受け入れ基準を満たせない**——SC-01 / SC-08 はどちらも第一に「タグ」を挙げており、
候補（#540）が既に `tags` を返している以上、**絞れないまま出すと画面が壊れる**。

**写像は `AttributeValueKeys.ToPayloadKey` へ寄せる**（照会側と同じ関数を使う）。
2 か所に同じ知識を持たせると、**片方だけ直したときに「候補には出るが絞れない」が再発する**。

## テスト（受け入れ基準の写像）

| # | 確かめること |
| --- | --- |
| 1 | `/bff/analysis/ask`・`/ask/stream` が対象範囲を受け取り、後段へ渡す |
| 2 | **範囲は ABAC と交差する（narrowing-only）**。権限外の値を指定しても広がらない |
| 3 | **権限の外だけを指す範囲は全体 deny** へ倒れる（`DataRangeScopeResolver` の既存規則） |
| 4 | **`tags` で絞れる**（Qdrant / InMemory の双方） |
| 5 | `department` / `project` で絞れる（属性経路の回帰） |
| 6 | 範囲を指定しないときの挙動が従来と同じ（既定値つき追加は非破壊） |
| 7 | 画面（SC-01 / SC-08）が候補 API の値でチップを組み立て、要求へ載せる |
| 8 | **候補に出る値と、絞れる値が一致する**（`ToPayloadKey` を 1 つの真実にしたこと） |

## 実装中に決めたこと（仕様書からの差分）

（着手後に追記する）

## 検証記録（実測）

（着手後に追記する）
