---
title: 作業仕様書 — IVectorStore に文書 ID 制約つき検索を足す（#969）
type: spec
status: done
related_ids:
  - FR-03
  - FR-04
  - FR-05
  - FR-17
  - UC-01
  - UC-10
  - ADR-0009
  - ADR-0035
  - IADR-0014
  - IADR-0151
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - "ADR-0035（GraphRAG の検索戦略・二段検索）"
  - "ADR-0009（ベクトルDB = Qdrant / ポート抽象）"
issue: "#969"
---

# 作業仕様書: `IVectorStore` へ文書 ID 制約つき検索を足す（#969）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-04（検索結果を根拠にした要約・出典提示）/ FR-17（文書間の参照関係の探索）
  / FR-05（ABAC 多値 allow-list）
- ユースケース（UC）: UC-01（検索・質問する）/ UC-10（関連文書をたどる）
- 関連 ADR: ADR-0035 決定 1（統合方式は二段検索。既存検索の実装は変更せず後段を足す）/ ADR-0009（ポート抽象）
- 親 issue: #947（#916 後半）。後続: #970（グラフ近傍展開との結線）

## 目的・背景

#947 の二段検索は、グラフが返す**文書単位**の候補（`GraphNodeItemDto(DocumentId, Title)`）を
**チャンク単位の出典**（`CitationDto` は `ChunkId` / `Score` / `Snippet` を要求する）へ変換する必要がある。
解法は「文書 ID 集合に絞ったベクトル検索を二段目として走らせる」ことだが、
現在の `IVectorStore` には**文書 ID で絞る口が無い**。`filters` は ABAC の多値 allow-list であって
文書 ID の集合ではない。

本 issue の射程は**ポートと 2 実装まで**であり、グラフとの結線（#947c / #970）は対象外である。

## 対象範囲

- 対象: `IVectorStore` への口の追加、`QdrantVectorStore` / `InMemoryVectorStore` の実装、
  テスト用スタブ（`RecordingVectorStore`）の追随、単体テスト
- 対象外: グラフとの結線・再ランク・出典採番（#970）、辺の型の重み・次数上限（#947a）、
  既存 `SearchAsync` / `KeywordSearchAsync` の挙動変更

## 母集合（着手前に引いた・規則 9）

走査コマンド（作業ツリー全体・`obj/` 除く）:

```bash
grep -rn "IVectorStore" --include=*.cs src/
grep -rn "interface I[A-Za-z]*VectorStore" --include=*.cs src/
grep -rln "Mock<IVectorStore>\|Substitute.For<IVectorStore>" --include=*.cs src/
```

結果、`IVectorStore` を**実装する型は 3 つだけ**である。

| 型 | 場所 | 扱い |
| --- | --- | --- |
| `QdrantVectorStore` | `.../Composable/Adapters/QdrantVectorStore.cs` | 実装する（本番） |
| `InMemoryVectorStore` | `.../Composable/Adapters/InMemoryVectorStore.cs` | 実装する（テスト・縮退） |
| `RecordingVectorStore` | `.../tests/RetrievalService.Api.Tests/HybridSearchServiceTests.cs` | 記録用スタブ。追随のみ |

除外したもの:

- `IIngestionVectorStore`（IngestionService の**書き込み側**ポート）—— 別ポートであり検索の口を持たない。
  ただしペイロード表現（`document_id` キー）は同ポートの実装と共有しており、本作業はその表現を変えない。
- モックライブラリ由来の実装は**存在しない**（Moq / NSubstitute の `IVectorStore` 生成は 0 件）。

## 設計

### 追加する口

```csharp
// FR-04, FR-17, ADR-0035, #969: 文書 ID 集合に絞った意味検索（二段検索の後段）。
Task<List<SearchResultDto>> SearchWithinDocumentsAsync(
    float[] queryVector,
    int topK,
    IReadOnlyCollection<Guid> documentIds,
    IReadOnlyList<AttributeFilter>? filters,
    CancellationToken ct = default);
```

決定と理由:

1. **既存 `SearchAsync` の省略可能引数にせず、別メソッドとして足す。**
   ADR-0035 決定 1 が「既存検索の実装は変更せず、後段を足す」と定めており、
   既存の口の意味論（絞りなし）を触らない形がこれに合う。
   省略可能引数にすると全実装のシグネチャが変わり、**「渡し忘れ＝絞りなし」**という
   静かな縮退の口も開く（本口は「空なら該当なし」へ倒す設計なので、既定値の意味が逆になる）。
2. **空集合は「該当なし」。** グラフが 0 件を返したときに全文書へ広がるのを防ぐ（issue の指定）。
   両実装とも**ストアを呼ぶ前に空リストを返す**。
3. **ABAC フィルタとは AND。** 文書 ID の制約は ABAC を置き換えず、**追加の条件**である。
4. 引数型は `IReadOnlyCollection<Guid>`（`Count` で空判定するため。順序は使わない）。

### Qdrant 実装

既存の `DeleteByDocumentAsync` が使っている `document_id` の `Match` フィルタと**同じ機構**に乗せる。
単一 ID は `Match.Keyword`、集合は `Match.Keywords`（＝ `BuildAttributeConditions` が
ABAC の多値 allow-list に使っているのと同じ書き方）である。**新しい流儀を作らない。**

```text
Filter.Must = [ document_id ∈ {ids} ] ∪ BuildAttributeConditions(filters)
```

条件の組み立ては `internal static BuildDocumentScopedFilter(documentIds, filters)` に切り出す。
`BuildAttributeConditions` を `internal` にしてある理由（実機 Qdrant なしで固定できる唯一の面）と同じで、
**Docker が使えない環境でも AND 結合と `document_id` キーをテストで固定するため**である。

`document_id` のリテラルは本ファイル内に 3 か所（`BuildPayload` / `MapPayload` / `DeleteByDocumentAsync`）
あり、本作業で 4 か所目になる。`internal const string DocumentIdKey` へ寄せて**1 つの真実**にする
（#539 が絞り込みキーで踏んだ「片方だけ直すと静かに割れる」型を持ち込まないため。値は変えない）。

### InMemory 実装

`document_id` の絞り込みと ABAC（`MatchesFilters`）の AND を評価する。

🔴 **既存の `InMemoryVectorStore.SearchAsync` は `queryVector` を一切参照せず、スコアも `0.9f` 固定である**
（実測）。そのため**ベクトル側の欠陥がテストで緑のまま通り抜ける**（#995 で実際に起きた）。
本口を同じ作りにすると、#970 が二段目の順位を検証しようとした時点で同じ穴を再生産する。
したがって**本口だけはコサイン類似度で実際に採点し、降順に並べて `topK` を取る**。

- ノルムが 0（空ベクトル・零ベクトル）や次元不一致のときはスコア 0 とする
  （#995 の縮退＝空ベクトルが渡り得るため、例外にしない）。
- **既存 `SearchAsync` は本 issue の射程外なので触らない**（欠陥は報告に残す）。

## 受け入れ基準

- [x] `IVectorStore` に文書 ID 制約つき検索の口がある
- [x] `QdrantVectorStore` と `InMemoryVectorStore` の**両方**に実装がある
- [x] ABAC フィルタと**併用**でき、**AND** になることがテストで固定されている
- [x] **空集合 → 該当なし**（陽性対照つき）。両実装で固定されている
- [x] 2 実装の**意味論が一致する**（キー・AND 結合・空集合の扱い）
- [x] 文書 ID の絞り込みを外す変異でテストが落ちる（変異試験）

## テスト方針

`RetrievalService.Api.Tests` に単体テストを足す（新規テストプロジェクトは作らない）。

| # | 対象 | 内容 |
| --- | --- | --- |
| 1 | InMemory | 集合内の文書のチャンクだけが返る（集合外は返らない・否定形） |
| 2 | InMemory | ABAC で拒否される文書は集合に入れても返らない ＋ 陽性対照（許可属性なら返る）＝ AND |
| 3 | InMemory | 空集合 → 0 件 ＋ 陽性対照（同じ状態で非空集合なら返る） |
| 4 | InMemory | `queryVector` を実際に見る（近いベクトルが上位・スコアが固定値でない） |
| 5 | Qdrant | `BuildDocumentScopedFilter` が `document_id ∈ {ids}` と ABAC 条件を **Must（AND）** で並べる |
| 6 | Qdrant | 空集合 → **クライアントを呼ばずに** 0 件（到達不能なクライアントで固定する＝呼べば例外） |

実 Qdrant を要する検証（実機のフィルタ解決）は **Docker が使えないため本作業では走らせない**。
CI（integration）に委ねる。純関数へ切り出した面（#5）は実機なしで固定できる。

## 計画書との差異

- 差異: なし

## 未決事項

- なし（結線・再ランク・出典採番は #970 の射程）
