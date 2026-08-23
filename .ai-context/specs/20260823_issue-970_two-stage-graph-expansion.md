---
title: 二段検索の段 — グラフ近傍展開と再ランク（既定オフ・opt-in）
type: spec
status: draft
related_ids: [FR-04, FR-17, FR-14, UC-10, ADR-0035, ADR-0034, ADR-0018, IADR-0242, IADR-0259]
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0035_graphrag-retrieval-strategy.md
  - planning:projects/microservices-platform/07_adr/ADR-0034_graph-traversal-abac-enforcement.md
  - planning:projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md
---

# 仕様書: 二段検索の段 — グラフ近傍展開と再ランク（#970 / 親 #947・#916b）

> 本書は**着手前**に作成した。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-04**（AI 回答と出典）/ **FR-17**（知識グラフ）/ FR-14（着脱可能な段）
- ユースケース（UC）: **UC-10**
- 関連 ADR: **ADR-0035 決定 1・2**（二段検索・探索パラメータ・既定オフ）/ ADR-0034（ホップごと ABAC・hops 裁定値）/ ADR-0018（着脱可能な段）
- 実装 ADR: **IADR-0259**（#969 の文書 ID 制約つき検索）/ IADR-0242（ホップごと ABAC の型ゲート）/ 本 issue の新規 IADR（仮番号 **IADR-0263**）

## 前提（着地済み）

| 依存 | 状態 | 本 issue での使い方 |
| --- | --- | --- |
| #947a 辺の型の重み・ハブ次数上限 | 着地（PR #977） | 重みを再ランクで使う／ハブ抑制は GraphService 側で効く |
| #969 `IVectorStore.SearchWithinDocumentsAsync` | 着地（IADR-0259） | 段③（文書 ID 制約つきベクトル検索） |
| #980 二段の上限・総数・間引き基準 | 着地 | `GET /graph/{id}/neighbors` の応答をそのまま使う |
| #916a グラフの BFF 公開（権限伝播 方式 A） | 着地 | RetrievalService → GraphService も方式 A に揃える |

## 対象範囲

### 対象（RetrievalService の中だけ）

```text
① 既存のハイブリッド検索                    → SearchResultDto（チャンク単位）
② ①の**ベクトル側**上位 N 件の文書 ID を起点にグラフ近傍探索（GraphService へ HTTP）
③ ②で到達した文書 ID に絞ったベクトル検索（#969）→ チャンク単位の Score / Snippet
④ ① と ③ を**重みつき合成**で再ランクして統合
```

- 段は **既定オフ・opt-in**（構成 `GraphExpansion:Enabled`。既定 `false`）
- 権限伝播は **`Authorization` ヘッダの伝播（方式 A）**
- 自己申告（FR-15 / イントロスペクション）に段の在否を出す＝**外から A/B の別が読める**

### 対象外

- SC-18 / SC-21 の画面、コミュニティ要約（ADR-0035 決定 3・5・6・7）
- **GraphService の実装本体**（territory 外。読むだけ）・BFF / AiAnalysisService の配線
- `docs/api/openapi.yaml`（`/search` の要求・応答契約は 1 バイトも変わらない）

## 母集合（是正・追随の対象を、着手前に自分で引く）

規則 1・2・3（誤りの側から / あり得る形を列挙 / 拡張子で絞らない）に従い、**本 issue が意味を変える語**で全追跡ファイルを走査した。

| 軸 | 走査 | ヒット | 扱い |
| --- | --- | --- | --- |
| `#970` | 全ファイル（`.git` / `obj` / `node_modules` 除く） | 7 ファイル | 下表 |
| `二段検索` | 同上 | 12 ファイル | 下表（#970 の上位集合） |
| `近傍展開` | 同上 | 4 ファイル | 上の部分集合 |

ヒットの内訳と処置:

- `.ai-context/adr/IADR-0259_*.md` / `.ai-context/specs/20260823_issue-969_*.md` / `20260822_issue-947a_*.md`
  → **凍結記録（確定済み）。書き換えない。** 「#970 が使う」という予告は本 issue の着地で真になる（記述は依然正しい）
- `src/knowledge/backend/Services/GraphService/**`（`EdgeType.cs` / `EdgeTypeSeed.cs` / `GraphDbContext.cs` / `EdgeWeightAndHubDegreeTests.cs`）
  → **territory 外。触らない。** 「重みは #970 が使う（現時点では未使用）」という注記は、本 issue の着地後は「使われている」へ更新するのが正だが、**GraphService の実装本体であり本 issue の territory 外**である。**統括側へ引き継ぐ**（§残件 1）
- `src/knowledge/backend/Services/RetrievalService/**`（`IVectorStore.cs` / `InMemoryVectorStore.cs` / `QdrantVectorStore.cs` / 既存テスト 2 本）
  → **territory 内。** 本 issue の実装で新しい呼び出し元ができるので、注記の「（後段は #970 が使う）」は事実になる。**文言の書き換えは不要**（誤りにならない）
- **除外したもの**: `docs/` 配下の文書（本 issue は `docs/` の公開文書を変えない。`/search` の外部契約が変わらないため）・`CHANGELOG.md`（生成物）・`scripts/`（territory 外）

規則 10（是正で**新たに誤りになる自分の記述**を引き直す）: 本 issue が新設する記述は「段は既定オフ」である。**これに矛盾し得る既存記述は無い**（走査で 0 件。段そのものが存在していなかった）。

## 設計

### 1. 段の入り方 —— `IHybridSearchService` のデコレータ

- `GraphExpandingSearchService : IHybridSearchService` を足す。**既存の `HybridSearchService` のアルゴリズムは 1 行も変えない**（ADR-0035 決定 1「既存検索の実装は変更せず、後段を足す」）。
- ただし `HybridSearchService` に **`SearchDetailedAsync`（internal）** を足し、`SearchAsync` はその薄いラッパにする。返すのは
  `Fused`（並び替え・切り詰め前）/ `VectorSide`（**起点の選定に要る**）/ `QueryVector` / `Filters`（ABAC）/ `Sort` / `TopK` / `CandidateK`。
  - **理由**: 段③は `SearchWithinDocumentsAsync(queryVector, …, filters)` を要求し、段②の起点は**ベクトル側の順位**である。デコレータが自前で埋め込みとフィルタ組み立てをやり直すと、**LLM ゲートウェイへの埋め込み呼び出しが 1 検索につき 2 回**になり、`BuildFilters` の真実源が 2 つに割れる。
  - **観測可能な振る舞いは同一**である（`SearchAsync` の戻り値・呼び出し回数とも）。T-11 が固定する。

### 2. opt-in の切り替え点

| 状態 | DI | 自己申告（`/internal/introspection`） |
| --- | --- | --- |
| **既定（構成なし）** | `IHybridSearchService` → `HybridSearchService`。`IGraphNeighborExpander` は**登録しない** | `graph-expansion` ポートは**現れない** |
| `GraphExpansion:Enabled=true` | `IHybridSearchService` → `GraphExpandingSearchService`（内側に `HybridSearchService`） | `graph-expansion` / `GraphServiceNeighborExpander` / `graph-service` |

**「段が付いていない」を DI の構造そのもので表す**（フラグを見て中で分岐するのではなく、**型として存在しない**）。これが ADR-0018 / FR-14 の「着脱可能な段」の本リポジトリでの実現形である。

> 🔴 **`pipeline.json` の段（`AddPlatformWolverineStep`）には載せない。** あの機構は**入力イベント型を持つ購読段**専用であり（`IPipelineStep<TIn>` の導出に失敗すると起動失敗にする、という規則 5 がその前提）、**同期の検索経路には入力イベントが無い**。載せるには存在しないイベント型を捏造することになり、`input` 照合が意味を失う。判断の記録は仮 IADR-0263 決定 2。

### 3. 権限伝播（方式 A・`Authorization` ヘッダ）

- `GraphServiceNeighborExpander` は `IHttpContextAccessor` から**呼び出し元の `Authorization` ヘッダをそのまま**下流へ載せる。GraphService は自分で ABAC を解決する型である（#916a の判断規則）。**解決済み scope を本文で渡す方式 B は採らない。**
- 🔴 **ヘッダが無ければ GraphService を呼ばない。** 呼ぶと `GraphAccessResolver` が `anonymous` → `Granted=false` へ縮退し、**全部 404**（＝「グラフには何も無い」と読める形）で静かに壊れる。**呼ばずに警告ログを出す**（`WarnEmbeddingUnavailable` と同じ作法。静かに縮退しない）。
- 🔴 **現在の呼び出し元（BFF `/bff/search`・AiAnalysisService `RagOrchestrator`）は RetrievalService へ `Authorization` を伝播していない**（実測。両者とも `PostAsJsonAsync` のみ）。したがって**段を有効化しても、呼び出し元を直すまで展開は 0 件**になる。**これは本 issue の territory 外**であり、§残件 2 として引き継ぐ。**「動いているふり」をさせないため、警告ログとテスト（T-05 否定形）で見える形にする。**

### 4. 再ランク —— 近接度を `Score` に混ぜない

**合成は 1 か所（`GraphRerank.Compose`）にだけ書く。**

```text
Composite(chunk) = w_search × RankScore(chunk) + w_graph × Proximity(document)

RankScore(c) = (K + 1) / (K + rank + 1)     K = RrfK = 60、rank は統合順位（0 始まり）
  - ① に居るチャンク    : rank = ① 内の順位
  - ③ だけのチャンク    : rank = |①| + ③ 内の順位（後段は①の後ろから入る。順位の軸を 1 本に保つ）
Proximity(d) ∈ [0,1] = 起点からの経路の**辺の型の重みの積の最大値**（起点自身と未到達は 0）
```

- **`SearchResultDto.Score` には一切書き戻さない。** ①のチャンクは融合スコアのまま、③のチャンクは**ベクトルストアが返したコサイン類似度のまま**返す。合成値は並べ替えにしか使わない（T-04 が固定）。
- **重みの積が近接度である**理由: `supersedes`(1.0) は何ホップ辿っても減衰せず「最新版へ**強く**誘導」、`related`(0.3) は 1 ホップで 0.3・2 ホップで 0.09 と急速に減衰し「**弱く**扱う」——ADR-0035 決定 2 の 2 つの名指しが、そのまま式の性質になる。ホップ数を別係数で持たない（重みが減衰を兼ねる）。
- 既定 `w_search = 1.0` / `w_graph = 0.35`。🔴 **実測値ではない**（実データが無い。#947a と同じ理由）。構成で変えられる形にし、A/B で測って決め直す。

### 5. 探索パラメータ（ADR-0035 決定 2 をそのまま守る）

| パラメータ | 実装 |
| --- | --- |
| 展開の起点 | **ベクトル側**上位 N（`SeedCount` 既定 5）。**全文検索側は起点にしない**（T-09） |
| ホップ数 | 既定 2・上限 3。構成の範囲外は既定へ縮退（`SearchModes` と同じ作法）。GraphService へ `?hops=` で渡す |
| 辺の型の重み | 段②が辺ごとに持ち帰る（§残件 1 も参照） |
| 打ち切り | ノード 200 / 辺 500・ハブ次数上限は **GraphService 側で既に効く**（#980 / #947a）。RetrievalService で二重に持たない |
| 導入 | **既定オフ・opt-in**（§2） |

### 6. 🔴 辺の型の重みが API から取れない（判明した制約・隠さない）

`edge_types.Weight` は #947a が入れたが、**GraphService はどの口からも公開していない** ——
`GraphEdgeDto`（近傍探索の応答）も `EdgeTypeCatalogItemDto`（型カタログ）も重みを持たない（実測）。

- **公開は GraphService 側の変更**であり、本 issue の territory 外である。
- したがって **HTTP アダプタは全辺に既定重み `0.5` を当て、要求ごとに 1 度警告を出す**（重み差が効いていないことを運用から読めるようにする）。
- **再ランク側は重みを一級の入力として完全に実装し、テストで固定する**（ポート経由の偽物で測る）。重みが公開された時点でアダプタの 1 行が変わるだけで効き始める。
- **「テストは緑・本番は無差別」を承知のうえで置く**（IADR-0014 が記録した型）。**承知していることを記録に残すのが本節の目的である。** §残件 1。

## 受け入れ基準（issue #970「必ず満たすこと」の写像）

- [ ] 🔴 **既定オフ**。構成を与えない状態で、段が DI に存在せず・自己申告にも現れず・検索結果が現行と一致する
- [ ] 🔴 **グラフ経由の根拠が利用者のスコープ内に限られる**ことを、**否定形＋陽性対照の対**で RetrievalService 側から改めて測る（後段で効いているから効く、は測った証拠にならない）
- [ ] 🔴 **未承認（pending / rejected）の AI 提案が根拠に現れない**。**構造的に不要であること**（候補は辺で到達した文書だけ）をテストに残す
- [ ] 🔴 **グラフの近接度を `Score` に混ぜない**。再ランクは重みつきの合成として 1 か所に明示的に書く
- [ ] 展開の起点は**ベクトル側**上位 N のみ（全文検索側は起点にしない）
- [ ] グラフが 0 件のとき、検索が全文書へ広がらない（#969 の「空＝該当なし」が効く）
- [ ] `CitationDto` の契約を変えない（グラフ由来も ChunkId / Score / Snippet を正規に持つ）
- [ ] 権限伝播は `Authorization` ヘッダ方式。**本文で scope を渡す方式を採らない**

## テスト方針

| ID | 内容 | 種別 |
| --- | --- | --- |
| T-01 | 🔴 構成なし＝**既定オフ**（`IHybridSearchService` は素の `HybridSearchService`・`IGraphNeighborExpander` 未登録） | サービス |
| T-02 | `Enabled=true` で段が入り、自己申告に `graph-expansion` が現れる（T-01 の陽性対照） | サービス |
| T-03 | グラフ由来の文書が**チャンク単位の出典**として結果に現れる（ChunkId / Score / Snippet を持つ） | 単体 |
| T-04 | 🔴 グラフ由来チャンクの `Score` は**ベクトルストアが返した値そのまま**（近接度が混ざっていない） | 単体 |
| T-05 | 🔴 **権限伝播（否定形）**: `Authorization` 無しの検索では GraphService を呼ばず、グラフ由来の結果が 1 件も出ない | エンドポイント |
| T-06 | 🔴 **同（陽性対照）**: `Authorization` 付きなら同じヘッダが下流へ載り、グラフ由来の結果が出る | エンドポイント |
| T-07 | 🔴 **ABAC と AND**: グラフが返した文書でも、ABAC スコープ外なら結果に出ない（多層防御。段③の filters） | 単体 |
| T-08 | 🔴 **候補は辺で到達した文書だけ**。応答のノード一覧に居るだけの文書は候補にならない（未承認提案は辺にならない＝構造的に到達しない） | 単体 |
| T-09 | 起点は**ベクトル側**上位 N のみ（全文検索側のヒットは起点にならない） | 単体 |
| T-10 | グラフが 0 件 → 段③を空集合で呼ばない・結果は①と一致（全文書へ広がらない） | 単体 |
| T-11 | 段オフ時の `SearchAsync` の結果・埋め込み呼び出し回数が現行と一致（既存検索を変えていない） | 単体 |
| T-12 | 再ランクの合成が重みつきで、`supersedes`(1.0) 経由が `related`(0.3) 経由より上位（重みが効く） | 単体（純関数） |
| T-13 | ホップ数の構成: 範囲外（0 / 4）は既定 2 へ縮退し、有効値はそのまま GraphService へ渡る | 単体 |

### 変異試験の設計

| 変異 | 落ちるべき |
| --- | --- |
| **M-1** 近傍展開を無効化する（デコレータが内側の結果をそのまま返す） | T-02 以外の陽性系（T-03 / T-06 / T-12 の経路） |
| **M-2** `Authorization` を下流へ載せない | **T-06**（陽性対照。T-05 だけでは「常に呼ばない実装」を通す） |
| **M-3** 段③へ ABAC フィルタを渡さない | **T-07** |
| **M-4** 合成値を `Score` へ書き戻す | **T-04** |
| **M-5** 既定を `Enabled=true` にする | **T-01** |
| **M-6** 起点を融合結果（全文側込み）から採る | **T-09** |

## 計画書との差異

- 差異なし。ただし**計画が求める「辺の型の重みを再ランクで使う」が、GraphService の公開面の欠落により本番では無差別になる**（§6）。これは計画の誤りではなく実装側の未配線であり、planning への環流ではなく本リポジトリの追随 issue が正しい。

## 未決事項・残件

1. **辺の型の重みの公開**（GraphService 側）。公開されるまで再ランクは全辺 0.5 で動く。**追随 issue が要る**（territory 外のため本 PR では起票しない。統括へ引き継ぐ）
2. **呼び出し元（BFF / AiAnalysisService）から RetrievalService への `Authorization` 伝播**。無いと段は 0 件のまま。**追随 issue が要る**（同上）
3. `scripts/check-bff-downstreams.js` の CALLERS に RetrievalService が入っていない（新たに service → service の呼び出し元になった）。`scripts/` は territory 外
4. `w_graph = 0.35` / `SeedCount = 5` は**実測値ではない**。A/B（既定オフの構成が可能にしたもの）で測って決め直す
5. 実 Qdrant / 実 GraphService を要する結線検証は Docker 不在のため実行できず、CI に委ねる
