---
title: 検索が読むコレクションを複数にし、コレクションごとに引いた順位を RRF で 1 つの並びへ束ねる（ティア A 有効化の前提）
type: spec
status: done
related_ids: [FR-02, FR-03, FR-05, UC-01, ADR-0009, ADR-0016, ADR-0017, ADR-0035, ADR-0043, ADR-0057, ADR-0092, IADR-0012, IADR-0085, IADR-0151, IADR-0256, IADR-0422, IADR-0467]
author: Claude（実装）
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0092_multi-collection-search-fusion-and-query-egress.md (決定 1〜3・フォローアップ 1〜3)
  - planning:projects/microservices-platform/07_adr/ADR-0016_embedding-provider-voyage.md (モデル別コレクション分離)
---

# 仕様書: 分離したコレクションを 1 回の検索で束ねる（#336 / ADR-0092 フォローアップ 1〜3）

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-03（横断検索）／FR-02（埋め込み・取り込み）／FR-05（ABAC）
- ユースケース: UC-01（検索・質問する）
- 計画 ADR: **ADR-0092**（Accepted・2026-09-09）決定 1〜3 とフォローアップ 1〜3。前提として ADR-0016（コレクションのモデル別分離）・
  ADR-0017（ティア A は Ruri v3）・ADR-0009（Qdrant）・ADR-0035（二段検索）・ADR-0043（権限内属性値）・ADR-0057（削除の伝播）
- 実装 ADR: [[IADR-0422]]（クエリ埋め込みプロファイルと照合。**本作業はその決定 4「残る問い」への実装**）／[[IADR-0012]]（deny-by-default）／
  [[IADR-0151]]（属性値は検索と同じ制約で引く）／[[IADR-0256]]（輸送の失敗は潰さない）／**[[IADR-0467]]（本作業で新設）**
- issue: #336（`blocked` のまま。本作業は `Refs`。TEI 配備・実モデル・nDCG 実測・Voyage のゼロ保持認定は所有者の手に残る）

## 目的・背景

ADR-0092 フォローアップ 1 は「**ティア A（`embedding.enabled: true`）を有効化する前に入れること**」と書く。順序を逆にすると
「索引されるが検索されない」が実際に起きる（同 ADR 実測 6: 全文検索も同じ 1 本のコレクションを読むので、**どの検索モードでも見つからない**）。

実測（`origin/develop` `b54b719c`・`git rev-parse --is-shallow-repository` → `false`）:

```console
$ git grep -n "ADR-0092" -- src
（0 件）
$ git grep -n "ResolveCollectionName" -- src/knowledge/backend/Services/RetrievalService ':!*/Tests/*'
.../QdrantCjkNgramIndexHealthCheck.cs:26:    private readonly string _collection = QdrantVectorStore.ResolveCollectionName(config);
.../QdrantFullTextIndexHealthCheck.cs:33:    private readonly string _collection = QdrantVectorStore.ResolveCollectionName(config);
.../QdrantVectorStore.cs:21:    private readonly string _collection = ResolveCollectionName(config);
.../Program.cs:72:    new QueryEmbeddingTarget(QdrantVectorStore.ResolveCollectionName(builder.Configuration)));
```

検索サービスは **`Qdrant:CollectionName` の 1 本しか読まない。** ゲートウェイは検索クエリを `Public` へ倒して優先度順に 1 つ選ぶので、
**ティア A のコレクションを引くためのクエリ埋め込みを頼む口も無い。**

## 対象範囲

- 対象: ADR-0092 決定 1（RRF で束ねる）・決定 2（ティア A のコレクション用のクエリ埋め込み経路。絞り込みは越境判定の後）・
  決定 3（ABAC は全コレクションに常に掛ける）・フォローアップ 3（定数を IADR へ・nDCG で測れる手順）
- 対象外（所有者の手に残る）: TEI 実配備・実モデル load・768 次元疎通・nDCG@10 の実測・Voyage のゼロ保持契約の認定・
  フォローアップ 4（`confidentiality` を持たない文書の件数報告。稼働データが要る）

## 設計（詳細と論拠は [[IADR-0467]]）

### 1. 検索が読むコレクション = 主コレクション ＋ 束ねるコレクション

- 主: 従来どおり `Qdrant:CollectionName`（**意味も値も変えない**）。
- 追加: 新設 `Qdrant:FusedCollections`（配列。**既定は空**）。空・空白・主と同名・重複は捨てる。
- 🔴 **既定は空なので、既定構成の検索は 1 バイトも変わらない**（下の「不変の証明」）。
- 有効化の配線: Helm は `embedding.enabled=true` のとき retrieval に `Qdrant__FusedCollections__0=<embedding.collection>` を
  描画する（既定 `false` では非描画＝manifest 不変）。compose は `SEARCH_FUSED_COLLECTION`（既定空）で与える。

### 2. 束ね方（決定 1）

- **コレクションごとに**ベクトル系統と全文系統を引き、**全系統を 1 回の RRF に平らに入れる**（入れ子にしない）。
  並びは「主のベクトル → 主の全文 → 追加 1 のベクトル → 追加 1 の全文 → …」。
- 定数: `k = 60`（既存の `RrfK` を共用）・候補幅は各系統 `max(TopK*4, TopK)`（既存と同じ。単一モードは従来の `singleModeK`）・**重みは全系統 1**。
- 🔴 **スコアを比べない。** 融合は順位だけを見る。結果の `Score` は RRF の合算値である。
- **同点は先に現れた方が先**（上の並び順）。従来は `Dictionary` の列挙順に暗黙依存していたので、**明示の二次キー（初出順）**を足す
  （追加が無い構成では並びが従来と同一であることを試験で固定する）。
- 平らにする理由: 追加が空のとき `RRF(主ベクトル, 主全文)` となり**従来と式が同一**になる（入れ子だと主だけでも `Score` が変わる）。

モード別:

| モード | 追加なし（既定） | 追加あり |
| --- | --- | --- |
| keyword | 従来どおり（主の全文の並びをそのまま返す。RRF しない） | 各コレクションの全文を RRF |
| semantic | 従来どおり（主の意味検索の並びをそのまま返す。埋め込み不可なら 0 件） | 埋め込めたコレクションだけ RRF。**全部埋め込めなければ 0 件**（従来と同じ意味） |
| hybrid | 従来どおり `RRF(主ベクトル, 主全文)` | 各コレクションのベクトル（埋め込めたものだけ）＋全文を 1 回の RRF |

### 3. クエリ埋め込み（決定 2）

- `EmbedApiRequest` に **`TargetCollection`（任意・既定 null）** を足す（proto は `target_collection = 4`・空文字＝未指定）。
- ゲートウェイの `EmbeddingRouter` は `Purpose=Query` のときだけ、**越境判定（`AllowedTiers`）と `Enabled` の篩を通った後で**
  候補を `Collection == TargetCollection` に**絞る**。`QueryProfile` と併存するときは**両方で絞る（積）**。候補が消えたら既定へ落とさず拒否。
- 🔴 **`Index` には効かない**（文書の送信先は機密区分が決める。呼び出し側がコレクションを選べてはならない）。
- **主コレクション用の要求は `TargetCollection` を送らない**（REST は `targetCollection: null`＝受け側は未指定として扱う。gRPC は空文字で線に載らない）。送るのは追加コレクション用だけ。
- 検索側の照合（`QueryEmbeddingCollection.Matches`）は**コレクションごとに**そのまま効かせる（答えたコレクションが違えばその系統を捨てる）。
- ティア A は社外送信の無い側であり、クエリをそちらへ送ることは露出を増やさない（ADR-0092 決定 2）。**主コレクションのクエリは従来どおりティア B が既定。**

### 4. ABAC（決定 3）

- `BuildFilters(request)` の 1 本を**全コレクションの全系統**（ベクトル・全文）へ渡す。deny-by-default（`GrantsAccess`）の早期 0 件は従来どおり**全コレクションの手前**で効く。
- 🔴 **問い合わせの省略は実装しない。** ADR-0092 決定 3 は費用最適化として許すが、入れる理由（計測された費用）がまだ無い。
  入れるときも**フィルタは外さない**（統制はフィルタが担う）。この判断は [[IADR-0467]] に残す。

## 🔴 母集合: 単一コレクションを前提にした箇所の全列挙と判断

引き方（規則 9: 記憶で挙げない。誤りの側の語で走査する）:

```console
$ git grep -n "Qdrant:CollectionName\|Qdrant__CollectionName\|ResolveCollectionName\|QueryEmbeddingTarget" -- ':!src/ai-stock-trading' ':!.ai-context'
$ git grep -n "knowledge_chunks_" -- ':!*.md' ':!src/ai-stock-trading'
$ git grep -n "_collection" -- src/knowledge/backend/Services/RetrievalService ':!*/Tests/*'   # 18 行
$ git grep -n "IVectorStore\b" -- src/knowledge/backend/Services/RetrievalService ':!*/Tests/*'
```

| # | 箇所 | 単一の前提 | 判断 |
| --- | --- | --- | --- |
| 1 | `Qdrant:CollectionName`（appsettings / Helm / compose / 試験） | 検索が読む 1 本 | **意味を変えない**（主のまま）。追加は別キー `Qdrant:FusedCollections` |
| 2 | `QdrantVectorStore.ResolveCollectionName` | 1 本を解決 | **変えない**。追加の解決は新関数 `ResolveFusedCollectionNames`（主と同名・空・重複を捨てる） |
| 3 | `QueryEmbeddingTarget` | 照合先 1 本 | **コレクションごとに 1 つ**。追加用は `NamedInRequest=true` で要求にも名前を載せる |
| 4 | `QdrantVectorStore.SearchAsync` / `KeywordSearchAsync`（**全文の scroll**） | `_collection` 1 本 | **ストアをコレクションごとに 1 つ**作り、検索は両方を引く。**全文も両方を引く**（ADR-0092 実測 6。束ねないとティア A の文書は全文でも見つからない） |
| 5 | `ListAttributeValuesAsync`（権限内属性値） | 1 本の facet | **全コレクションの和集合**。件数は従来どおり捨てる。束ねないと「検索には出るが候補に無い値」が生まれる（[[IADR-0151]] 決定 1 の禁止形） |
| 6 | `DocumentDeletedConsumer`（削除の伝播） | 1 本から消す | **全コレクションから消す**（ADR-0057 決定 1「ベクトルストアに残っていない」）。取り込み側は既に全コレクションから消している（`DeleteByDocumentFromAllAsync`） |
| 7 | `SearchWithinDocumentsAsync`（二段検索の段③）／`HybridSearchOutcome.VectorSide`・`QueryVector` | 主の 1 本 | **主のままにする**（対象外）。段は既定オフ・opt-in。段③は起点ベクトルと同じ空間でしか採点できず、コレクションごとに段を持たせると ADR-0035 の設計そのものの改定になる。**段①の融合結果（`Fused`）には追加コレクションのヒットが入る**ので、段を有効にしても束ねた結果が消えるわけではない。限界として IADR に残す |
| 8 | `QdrantFullTextIndexHealthCheck` / `QdrantCjkNgramIndexHealthCheck` | 主の索引だけ見る | **主のままにする**（対象外）。索引は取り込み側が `Embedding:Collections` の全コレクションへ同じコードで張る。追加コレクションの索引欠落は検出されないので、**ティア A 有効化時の所有者確認項目**に挙げる |
| 9 | `UpsertAsync`（本番の呼び出し元 0 件） | 主の 1 本 | **変えない** |
| 10 | Helm `deployment.yaml` の retrieval 分岐 | 決定的ローカルで主を差し替える | 既存はそのまま。`embedding.enabled=true` の分岐を**別に**足す |
| 11 | compose の retrieval | 主のみ | `Qdrant__FusedCollections__0: ${SEARCH_FUSED_COLLECTION:-}` を足す（既定空＝無視される） |
| 12 | `scripts/check-stack-ready.js` G13（検索側の読み先に点があるか） | 主の 1 本 | **変えない**（稼働クラスタ用。目的は #1215 の主の食い違いの検出） |
| 13 | `LlmGateway` `EmbeddingRouter`（`Purpose=Query` を `Public` へ倒す） | 選ぶのは 1 つ | 倒し方は変えない。`TargetCollection` による**絞り込みを篩の後に**足す |
| 14 | `IngestionService` | — | **変えない**（書き込み先はゲートウェイの答えるコレクション。既に機密区分どおり分かれている） |

## 受け入れ基準

- [x] **不変**: `Qdrant:FusedCollections` が空（既定）のとき、3 モードとも**戻り値・`Score`・並び・埋め込みの呼び出し回数・ストアの呼び出し回数**が従来と同一（試験）
- [x] **不変**: 主コレクション用の埋め込み要求は `TargetCollection` を持たない（REST / gRPC とも。試験）
- [x] 決定 1: 追加コレクションのヒットが 1 つの並びに入る。**生スコアではなく順位で**並ぶ（生スコアで比べる変異で赤）
- [x] 決定 1: 同点は並び順（主 → 追加）で決まる
- [x] 決定 1: keyword モードで追加コレクションの文書が見つかる（全文も束ねる）
- [x] 決定 2: 追加コレクション用の要求は `TargetCollection` を載せ、ゲートウェイは越境判定と `Enabled` の篩の後でそのコレクションのエンドポイントへ絞る。`Index` には効かない。無効・不在なら拒否（既定へ落ちない）
- [x] 決定 2: 追加コレクションでゲートウェイが別コレクションを答えたら、その系統を捨てる（照合）
- [x] 決定 3: 追加コレクションの**全系統**に主と同じ ABAC フィルタが渡る。スコープが高機密を許さない利用者でも問い合わせは省かれず、フィルタが落とす（追加側のフィルタを外す変異で赤）
- [x] 決定 3: deny-by-default では追加コレクションも呼ばれない
- [x] 権限内属性値は全コレクションの和集合。削除は全コレクションから
- [x] 合成点: `Qdrant:FusedCollections` を与えると追加コレクションが組み上がる（REST の名前つきクライアントが主と同じ宛先）
- [x] Helm: 既定では retrieval に `Qdrant__FusedCollections` を描画しない。`embedding.enabled` の分岐で描画し、値はゲートウェイ／取り込みの Ruri コレクション名と一致（静的試験）
- [x] フォローアップ 3: 定数を [[IADR-0467]] に記録し、`perf/ndcg/README.md` に「voyage のみ」と「束ねた」の比較手順を書く

## テスト方針

- `HybridSearchServiceTests` に隣接する新ファイル `MultiCollectionFusionTests.cs`（記録つきの偽ストア・偽埋め込み ＋ `InMemoryVectorStore`）
- `LlmGatewayEmbeddingServiceTests`（REST）/ `LlmGatewayGrpcEmbeddingServiceTests`（gRPC）に `TargetCollection` の有無
- `EmbeddingRouterTests` に `TargetCollection` の絞り込み・`Index` 不適用・篩の後・`QueryProfile` との積
- `GrpcEmbedTests`（ゲートウェイ）で proto → DTO の写し
- `DocumentDeletedConsumerTests` / 属性値の和集合 / 合成点
- `scripts/k8s-local-up.test.js` に Helm 配線の静的試験
- 🔴 変異試験（手で入れて赤を確かめ、戻す）: M-1 追加コレクションのフィルタを `null` に／M-2 融合を生スコアの降順に／
  M-3 絞り込みを越境の篩の前へ／M-4 `TargetCollection` を `Index` にも効かせる／M-5 主の要求にも `TargetCollection` を載せる

## 計画書との差異

- 差異: なし。ADR-0092 決定 3 の「省いてよい」は実装しない（許可であって要求ではない）。
- [[IADR-0422]] 決定 2 の「契約は 1 バイトも変えない」を**内部契約 `EmbedApiRequest` について部分改定**する
  （公開契約 `SearchRequest` は変えない）。理由は同 IADR の代替案が退けた形（検索側がエンドポイントを指示する）と違い、
  検索側は**自分が読むコレクション**を名乗るだけで、選ぶのは篩の後のゲートウェイであること。[[IADR-0467]] と IADR-0422 の追記に残す。

## 未決事項

- なし（ADR-0092 と既存 IADR の範囲で決められた）。所有者に残るものは PR 本文に列挙する。

## 検証（2026-09-26・`origin/develop` `0a945ba3` へ rebase 後）

| 検査 | 結果 |
| --- | --- |
| `dotnet test src/knowledge/backend/backend.slnx` | 全プロジェクト成功（RetrievalService.Tests 297 件を含む。失敗 0） |
| `dotnet test LlmGateway.Tests` | 成功 314 件（失敗 0） |
| `dotnet format <slnx> --verify-no-changes`（両ユニット） | exit 0 |
| `check-contract-schema` / `check-proto-contracts` / `check-openapi-dto-drift` | OK（baseline は `--update` で非破壊の追加 1 件ずつ） |
| `check-trace-blocks` / `gen-knowledge-graph --check` | OK |
| `k8s-local-up.test.js` | 187 件成功（本作業の 3 件を含む） |
| `check-adr-numbering` | **IADR-0466 が欠番**（開いている #1539 が保持。同 PR のマージ後に解消）。0466 を一時的に作業ツリーへ置いた状態では OK |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 上と同じく 0466 を一時的に置いた状態で 800 件成功（置かないと採番検査で止まる） |

### 変異試験（手で入れて赤を確かめ、戻した）

| # | 変異 | 赤になった試験 |
| --- | --- | --- |
| M-1 | 追加コレクションの 4 つの問い合わせへ `null` のフィルタを渡す | `MultiCollectionFusionTests` 6 件（全系統に主と同じフィルタ ×3・権限外は落ち権限内は出る ×3） |
| M-2 | RRF の並べ替えを入力の生スコアの降順にする | 同 3 件（生スコアではなく順位で並ぶ・同点は主が先・追加が空なら従来の RRF と同じ） |
| M-3 | `Enabled` の篩を「読み先を名乗れば無効でも通す」にする（篩の前へ移したのと同じ） | `EmbeddingRouterTargetCollectionTests` 1 件・`EmbedTargetCollectionTransportTests` 1 件 |
| M-4 | 読み先の絞り込みを Index にも効かせる | `EmbeddingRouterTargetCollectionTests` 1 件（Index では読み先の指定を無視する） |
| M-5 | 主コレクションの要求にも名前を載せる | `FusedQueryEmbeddingTests` 3 件 |

### 採番

IADR-0468 で起こしたが、#1378 の PR（#1540）が IADR-0467 を使わずに開いたこと・develop と開いている PR のどれも 0467 を持たないことを確かめ、
欠番を作らないよう **IADR-0467 へ改番した**（ファイル名・本文・索引・仕様書・コード内コメント・docs の trace ブロック・Helm / compose のコメント）。
