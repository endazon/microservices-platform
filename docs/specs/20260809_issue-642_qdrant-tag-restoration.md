---
title: 作業仕様書 Qdrant の検索結果からタグを復元し、書き込み表現を取り込み側へ揃える（#642）
type: spec
status: done
related_ids: [FR-03, SC-02, UC-01, IADR-0014]
author: Claude
created: 2026-08-09
updated: 2026-08-09
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
related_specs:
  - "../adr/IADR-0014_qdrant-attribute-payload-key.md"
  - "../tests/FR-03_hybrid-search.md"
  - "../screens/SC-02_search-results.md"
  - "../tests/SC-02_search-results.md"
---

# 仕様書: Qdrant 検索結果のタグ復元（#642）

> **本作業は「画面を直す」作業ではない** —— 画面（`SearchResultsPage.tsx:175`）は最初から
> `result.tags?.map(...)` でタグを描こうとしている。**空を渡しているのはバックエンドである。**

## 起点となる ID（トレーサビリティ）

- 起点 issue: **#642** ／ 起点 ID: **FR-03**（ハイブリッド検索）・**SC-02**（検索結果一覧）・**UC-01**
- 制約: [[IADR-0014]]（Qdrant ペイロード表現。**書き込み・フィルタ・復元を一致させる**）
- 同型の先例: [[IADR-0014]] が名指しした「**テストは緑・本番は空**」——
  `MapPayload` が `Attributes` を常に空で返していた欠陥と**同じ形**が `Tags` に残っていた。
- 規約: `.claude/rules/traceability.md`

## 母集合の引き直し（[[IADR-0141]] 決定 1）

**走査基準**: `origin/develop` = **`8cc0280`**（#539 の着地後）。**issue 本文の行番号を転記せず、実ファイルから引き直した。**

> **★ 走査基準を引き直した（rebase 時）。** 着手時は `ae66549` を `origin/develop` として走査したが、
> **それはローカルの古い ref であって当時の `origin/develop` ではなかった**（8 コミット遅れ）。
> **母集合の規則 1「誤りの側から引く」以前の問題で、引く土台そのものが誤っていた。**
> 下表の行番号は `ae66549` 時点のものを残す（引いた時点の記録）。
> **結論が変わったのは判断 2 だけである**（`AttributeValueKeys` が #540／#539 で新設されていた）。
> 他の行は `8cc0280` で再走査して一致を確認した。

| 項目 | issue 本文の記述 | **実測（`ae66549`）** |
| --- | --- | --- |
| `MapPayload` の `Tags: []` | 152 行目付近 | **`QdrantVectorStore.cs:111`** |
| `UpsertAsync` | 201-233 行付近 | **`QdrantVectorStore.cs:148-175`** |
| 取り込み側の tags 書き込み | `BuildChunkPayload` 68-75 行 | **`QdrantIngestionVectorStore.cs:60-66`** |
| 画面のタグ描画 | `SearchResultsPage.tsx:186` | **`SearchResultsPage.tsx:175`** |
| `QdrantVectorStoreTests.cs` の `tags` 言及 | 0 件 | **0 件（一致）** |

**行番号は 4 件中 3 件が古かった**（`ae66549` 時点で前方にずれている）。**射程の内容は 5 件とも一致した。**

### `Tags` を落としている面の全数（`src/` 走査）

| 経路 | 実測 | 判定 |
| --- | --- | --- |
| `QdrantVectorStore.MapPayload`（本番） | `Tags: []` 固定 | **欠陥。直す** |
| `QdrantVectorStore.UpsertAsync`（本番） | ペイロードに `tags` を書かない | **欠陥。直す** |
| `InMemoryVectorStore`（テスト／ローカル） | `c.Tags` を 2 箇所とも運ぶ | 正常。**だからテストが緑のままだった** |
| `HybridSearchService`（RRF 融合） | `byId[kv.Key] with { Score = … }` で DTO ごと保持 | 素通し。変更不要 |
| BFF（`SearchBffEndpoints`） | `SearchResponse` をそのまま返す | 素通し。変更不要 |
| `IngestionService`（取り込み） | `payload["tags"] = ListValue` を**既に書いている**（テスト固定済み） | **変更不要** |

**`Tags: []` のリテラルは `src/` 全体で 3 箇所**あるが、残る 2 箇所
（`CitationMapperTests.cs:20` / `DocumentNormalizedSyncTests.cs:70`）は**テストの入力データ**であり本欠陥とは無関係。

### 引かなかった軸と理由

| 軸 | 引いたか | 理由 |
| --- | --- | --- |
| `MapPayload` が落としている**他の**項目 | ❌ | 実測して確認したが、`Tags` 以外に固定値を返している項目は無い（`chunk_index` は DTO に無い） |
| フロントエンド（SC-02 画面） | ❌ | **既にタグを描く実装になっている**。渡っていないだけである |
| 契約（`openapi.yaml` / `contract-schema-baseline.json`） | ❌ | `tags` は既に required。**実測で差分 0 件**（§検証） |
| 取り込み側（IngestionService） | ❌ | 既に正しい。**表現を合わせる側は Retrieval である** |
| 既存データの移行 | ❌ | [[IADR-0014]] と同じ扱い —— 再取込まで旧データは `tags` を持たず、**空欄で出る**（欠落方向であり誤表示ではない） |

## 原因

`MapPayload` は `Attributes` を [[IADR-0014]] で復元するようになったが、**`Tags` は空リテラルのまま残った**。
`UpsertAsync` も同様に `tags` をペイロードへ書いていない。
一方 `InMemoryVectorStore` は `ChunkPayload.Tags` をそのまま DTO へ運ぶため、
`HybridSearchEndpointTests` などの結合テストは**すべて緑**になる。
**本番（Qdrant）とテスト（InMemory）で通る面が違う** —— [[IADR-0014]] が記録した欠陥と同型である。

## やること

1. **`ExtractTags` を `internal static` の純関数として新設**し、`MapPayload` から呼ぶ。
   `ExtractAttributes` と同じ位置づけ —— **実機 Qdrant なしで固定できる面を作る**（issue 射程 3）。
2. **`BuildPayload` を `internal static` の純関数として切り出す**（`UpsertAsync` の本体）。
   取り込み側の `BuildChunkPayload` と同じ形にし、**書いた表現をそのまま復元できること（往復）を
   テストで固定する**。§判断 1 を見ること。
3. `UpsertAsync` が `tags` を**取り込み側と同じ表現**（`payload["tags"] = new Value { ListValue = … }`）で書く。

### 表現（取り込み側と一致させる）

```
tags: ListValue { Values: [ StringValue("経理"), StringValue("規程") ] }
```

- **タグが 0 件のときはキー自体を書かない**（`QdrantIngestionVectorStore.BuildChunkPayload` と同じ。
  `attributes` の扱いとも揃う）。
- 復元は**キーが無い／リストでない**とき空リストを返す（画面は空欄になる）。

## 判断（仕様書＝本書が正）

### 判断 1: 書き込み側も純関数へ切り出した（issue 射程 3 は復元だけを求めている）

**射程を広げたのではなく、射程 2 を検証可能にした。** issue 射程 2 は「`UpsertAsync` が `tags` を書く」ことを
求めているが、`UpsertAsync` は `QdrantClient` を要求するため**実機なしでは一行も固定できない**。
`BuildPayload` を切り出すと、射程 3 が復元側に与えたのと同じ性質（実機なしで固定できる面）が書き込み側にも立つ。
**変更はメソッド本体の移動のみで、`UpsertAsync` の外部から見た挙動は変えていない。**

### 判断 2: キーは `AttributeValueKeys.Tags` を使う（issue 射程 4 のとおり）

**★ この判断は rebase で覆った。着手時の結論（リテラル `"tags"`）を撤回する。**

着手時は「`Knowledge.Contracts.Dtos.AttributeValueKeys` はリポジトリ全体で 0 件」と実測し、
新設は射程外としてリテラルを採った。**その実測は `ae66549` に対しては正しかったが、当時の
`origin/develop` に対しては誤っていた**（§母集合の走査基準を参照）。

**`8cc0280` では `AttributeValueKeys` は存在する。** #540 が
`src/knowledge/backend/Shared/Knowledge.Contracts/Dtos/AttributeValueDto.cs` に新設し、
**#539 が絞り込み側（`QdrantVectorStore.BuildAttributeConditions`）を `ToPayloadKey` へ寄せた**
——「候補に出る値と、その候補で絞れる値を 1 つの関数に持たせる」ためである。

したがってリテラルを採ると、**同じ 1 つのキーの真実が 3 つに割れる**。

| 面 | `ae66549` 時点 | **`8cc0280` 時点** |
| --- | --- | --- |
| 絞り込み（`BuildAttributeConditions`） | `attributes.{key}` をハードコード（`tags` を絞れない欠陥） | **`AttributeValueKeys.ToPayloadKey`**（#539） |
| 値集合の照会（`ListAttributeValuesAsync`） | `ToPayloadKey`（#540） | 同左 |
| 書き込み・復元（本作業） | 参照先が無いのでリテラル | **`AttributeValueKeys.Tags`** |

**着手時の理由 2「ペイロードキーは全部リテラルだから揃える」は、`tags` については既に破れていた。**
`document_id` 等がリテラルなのは今も変わらないが、それらは**照会の入口を持たない**キーである。
`tags` だけは利用者が指定でき、**指定した値で絞れなければ画面から見て壊れている**（#539 が直した型）。

理由 1（契約プロジェクトへ公開型を足すのは射程外）は**そもそも不要になった**——足す必要が無く、参照するだけである。

**テスト側はリテラル `"tags"` のままにする（意図的）。** 本体を定数へ寄せた上でテストも同じ定数を使うと、
**定数の値を書き換えたときテストが一緒に動いて緑のまま通る**。テストが固定すべきは
「ペイロードに載る**実際の文字列**」なので、`ContainsKey("tags")` のようにリテラルで主張する
（既存の `TagFilteringTests` が `Field.Key.Should().Be("tags")` と書いているのと同じ作法）。

> **申し送りの取り下げ**: 「定数化は Ingestion / Retrieval を同時に移す別 issue」も撤回する。
> Retrieval 側は本作業で移った。**Ingestion 側（`QdrantIngestionVectorStore`）は別ユニットの
> サービスで本作業の射程外**なので、リテラルのまま残る。両側の一致は引き続き**テストが担保する**
> ——取り込み側テスト（`QdrantIngestionVectorStoreTests.cs:67`）と本作業の復元側テストが
> **同じ形**（`ListValue` の `StringValue`）を主張しており、片方を変えればもう片方が赤くなる。

### 判断 3: 非文字列のリスト要素は `AsString` で文字列化する

`AsString`（既存）を再利用し、数値・真偽値のタグも安全に文字列化する。
構造体・入れ子リストは `null` になるので**読み飛ばす**（例外を投げない）。
取り込み側は文字列しか書かないため通常は経由しない経路だが、**手で投入されたデータで落ちない**ようにする。

### 判断 4: 新 IADR は**起こさない**

**既存の設計判断を変えていない。** [[IADR-0014]] が「書き込み・フィルタ・復元の表現を一致させる」と決めており、
本作業は**その決定が `tags` に適用されていなかった箇所を適用する**だけである。
新 IADR を起こすと「Qdrant ペイロード表現の正」が 2 つに割れる。

## テストの写像（受け入れ基準 → `[Fact]`）

すべて `RetrievalService.Api.Tests.QdrantVectorStoreTests` へ足す（実機 Qdrant 不要）。

| # | 受け入れ基準 | テスト | 期待 |
| --- | --- | --- | --- |
| 1 | ペイロードの `tags` から `Tags` を復元する | `ExtractTags_RestoresFromListValue` | `["経理","規程"]` を順序どおり |
| 2 | `tags` を持たないペイロードは空（画面は空欄） | `ExtractTags_WhenNoTags_ReturnsEmpty` | `[]` |
| 3 | `tags` がリストでない（旧データ・手投入）ときも落ちない | `ExtractTags_WhenTagsNotAList_ReturnsEmpty` | `[]` |
| 4 | 非文字列スカラーは文字列化、非スカラーは読み飛ばす | `ExtractTags_CoercesScalarsAndSkipsNonScalars` | `["42","true"]` |
| 5 | 書き込みが取り込み側と同じ表現である | `BuildPayload_WritesTagsInIngestionRepresentation` | `payload["tags"].ListValue` の `StringValue` 列 |
| 6 | タグ 0 件ではキーを書かない | `BuildPayload_WhenNoTags_OmitsTagsKey` | `ContainsKey("tags") == false` |
| 7 | **書いた表現をそのまま復元できる**（本番経路の往復） | `BuildPayloadThenExtractTags_RoundTrips` | 入力タグと一致 |
| 8 | 既存の属性復元を壊していない | 既存 3 件（`ExtractAttributes_*`） | 変更なしで緑 |

**#7 が本欠陥の核心である** —— 書き込みと復元のどちらかだけを直しても、本番では依然としてタグが出ない。

## 追随させた仕様書

| 文書 | 追記内容 |
| --- | --- |
| `../tests/FR-03_hybrid-search.md` | T-16〜T-22（上表 #1〜#7）を追加。対象範囲に `ExtractTags` / `BuildPayload` を明記 |
| `../screens/SC-02_search-results.md` | §hi-fi 対応表 #6（タグ列）に「**本番経路でタグが渡っていなかった**」旨と是正を追記 |
| `../tests/SC-02_search-results.md` | 画面テストは**タグが渡ってくる前提**であり、その前提を固定するのは backend 側であることを明記 |

## 受け入れ基準（#642）

- [x] `MapPayload` がペイロードの `tags` から `Tags` を復元する
- [x] `UpsertAsync` が `tags` を**取り込み側と同じ表現**で書く
- [x] 復元は `internal` の純関数として切り出され、**実機 Qdrant なしで直接テストされている**
- [x] `QdrantVectorStoreTests.cs` に tags のテストがある（`[Fact]`/`[Theory]` **6 件 → 13 件**。tags の言及 **0 件 → 7 ケース**）
- [x] 取り込み側・契約・フロントエンドを変更していない（**実測で差分 0**）
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が緑

## 検証（実走した結果）

| コマンド | 結果 |
| --- | --- |
| `cd src && /opt/dotnet/dotnet build knowledge/backend/backend.slnx` | **Build succeeded**（0 Error / 2 Warning。警告は既存の `MinioBuilder` obsolete で本作業と無関係） |
| `/opt/dotnet/dotnet test knowledge/backend/backend.slnx` | **Failed 0 / Passed 527 / Skipped 22 / Total 549**（11 アセンブリの合計）。うち `RetrievalService.Api.Tests` は **64 → 71**（**+7**） |
| `/opt/dotnet/dotnet format knowledge/backend/backend.slnx --verify-no-changes` | **exit 0**（差分なし） |
| `node scripts/check-doc-links.js` | **OK: 497 件**（未 populate の submodule 配下は対象外） |
| `node scripts/check-test-spec-coverage.js` | 初回は **違反 1 件（床の上げ忘れ）** → `--update` 後 **OK**（§下記）。`check-commit-messages` / `check-landed-subjects` も OK |
| `node scripts/check-cross-repo-refs.js` | **OK: 575 件** |
| `node scripts/check-contract-schema.js` | **OK: 2 プロジェクト / 20 ファイル / 59 型が baseline と一致**（＝射程どおり契約は変わっていない） |
| `node scripts/check-plan-id-qualification.js` | **OK: 1218 件** |

### 変異試験（**テストが本当に欠陥を捕まえるか**）

対象は `RetrievalService.Api.Tests`（**71 件**。rebase 後の base = `c614c34` で再実走した）。

| 変異 | 結果 |
| --- | --- |
| `ExtractTags` を早期 return で `[]` 固定へ戻す（＝復元欠陥の再現） | **Failed 3 / Passed 68** —— `ExtractTags_RestoresFromListValue` / `ExtractTags_CoercesScalarsAndSkipsNonScalars` / `BuildPayloadThenExtractTags_RoundTrips` |
| `BuildPayload` の `if (chunk.Tags.Count > 0)` を `if (false)` にする（＝書き込み欠陥の再現） | **Failed 2 / Passed 69** —— `BuildPayload_WritesTagsInIngestionRepresentation` / `BuildPayloadThenExtractTags_RoundTrips` |
| 両方を元に戻す | **Failed 0 / Passed 71**（`git diff` も差分 0 で、変異が残っていないことを確認した） |

**どちらの変異でも `BuildPayloadThenExtractTags_RoundTrips` が落ちた** ——
書き込み・復元のどちらが欠けても赤になる。**これが作業前の状態（両方欠けている）を捕まえる面である。**

### `test-spec-coverage-baseline.json` の更新

`docs/tests/SC-02_search-results.md` から `QdrantVectorStoreTests` を参照したため、
対（`docs/tests/SC-02_search-results.md::QdrantVectorStoreTests`）が 1 件増えた。
`node scripts/check-test-spec-coverage.js --update` で床を上げた（**差分は本 PR に載る**）。

## 申し送り

- **既存データ（本修正より前に Retrieval 側の `UpsertAsync` で書かれたチャンク）は `tags` を持たない。**
  [[IADR-0014]] の属性と同じく、**再取込（`DocumentUpdated` 再発行）まで空欄で出る**。
  誤ったタグが出るわけではないので、移行作業は本 issue の射程に含めない。
  なお**通常の取り込み経路は IngestionService** であり、そちらは既に `tags` を書いている。
- **`AttributeValueKeys` は既にある**（判断 2）。**Retrieval 側は本作業で全面的に寄せた**（絞り込み＝#539・
  値集合＝#540・書き込み／復元＝本作業）。**残るリテラルは取り込み側（`QdrantIngestionVectorStore`）だけ**で、
  そちらは別ユニットのサービスなので本作業の射程に含めない。移すなら別 issue とする。
