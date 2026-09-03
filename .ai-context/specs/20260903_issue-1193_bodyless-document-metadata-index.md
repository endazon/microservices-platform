---
title: 作業仕様書 — 本文なしの文書をメタデータで索引・検索へ載せ、SC-02 に「本文なし（原本を参照）」と示す
type: spec
status: done
related_ids:
  - FR-02
  - FR-03
  - FR-04
  - FR-05
  - UC-01
  - UC-04
  - SC-02
  - ADR-0009
  - ADR-0012
  - ADR-0016
  - ADR-0070
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - "ADR-0070 決定 4（本文なしの文書はカタログに載せ、メタデータで検索可能とする。本文由来のチャンク・埋め込みは作らない。SC-02 には『本文なし（原本を参照）』を示し結果から除外しない）"
  - "ADR-0070 決定 3（テキスト層を持たない PDF は「本文なし」で確定させる。失敗として溜めない）"
  - "planning#509（環流）/ planning#521（反映 PR）"
related_adrs:
  - IADR-0354
  - IADR-0002
  - IADR-0012
  - IADR-0014
  - IADR-0122
  - IADR-0149
  - IADR-0283
  - IADR-0339
issue: "#1193"
---

# 作業仕様書: 本文なし文書のメタデータ索引と SC-02 の縮退表示

## 起点

計画 ADR-0070（Accepted・2026-09-03）決定 4:

> **本文由来のチャンク・埋め込みは作らない**（作れない）。**タイトル・パス・データソース・更新日時などの
> メタデータで FR-03 の検索に載せる。**
> **SC-02 の検索結果では本文抜粋が出せないため、「本文なし（原本を参照）」である旨を示す。結果から除外しない。**

受け皿は #1193。**決定 1・2・3（PDF の本文抽出と「本文なしで完了」）は #1192 の射程**であり、
本 PR は**その後段**である。**#1192 の状態名には依存しない** —— 本文の有無は**本文そのもの
（の分割結果）**で判定する。

## 母集合（着手前に自分で引いた。issue 本文からは転記していない）

`git rev-parse --is-shallow-repository` = `false`（履歴の打ち切りは無い。planning#410）。

**引き方**: 「本文が空のとき何が起きるか」を、**空で 0 件に落ちる側の文字列**（`IsNullOrWhiteSpace` /
`Count == 0`）と、**本文抜粋を読む側**（`SearchResultDto.Text` の消費点）の 2 方向から走査した。
記憶では挙げていない（`traceability.repo.md` 規則 9）。

| # | 走査 | 当たり | 判定 |
| --- | --- | --- | --- |
| 1 | `IsNullOrWhiteSpace` / `IsNullOrEmpty`（Ingestion / Retrieval、Tests 除く） | 9 件 | **落ちるのは `MarkdownChunkingService.cs:13`** の 1 件（空本文 → チャンク 0 件）。他 8 件はクエリ側・属性値側で本件と無関係 |
| 2 | `chunkCount` の生産と消費 | 生産 1（`DocumentUpdatedConsumer`）・イベント 1（`IngestionCompleted.ChunkCount`） | **`IngestionCompleted` の購読者は本リポジトリに 0 件**（実測）。0 件で完了を出しても後段は壊れない |
| 3 | `SearchResultDto.Text` の消費点（backend） | 2 件 | `CitationMapper.BuildSnippet`（RAG 文脈・出典）／`InMemoryVectorStore` の突合（`c.Text.Contains`） |
| 4 | `result.text` の消費点（frontend） | 1 件 | `SearchResultsPage.tsx:182` の本文抜粋 `<p>` |
| 5 | 「本文なし」相当の表示（`sc02-results/`） | 1 件 | `useSearchQuery.ts` の「本文なし（204・空ボディ）」＝ **HTTP 応答本文の話で無関係**（issue の実測と一致） |
| 6 | 陽性対照: 同型の縮退表示 | `画像保持へ縮退済み` が SC-07 に実在 | **同じ形の表示が本文側には無い**ことを、在る側の実例で確かめた |

**除外したもの（理由つき）**

- `DocumentUpdatedConsumer.cs:31` の `MarkdownUri is null` による早期復帰 —— **本 PR の射程外**。
  これは「本文の**所在**がまだ無い」（下書き・変換前）であり、ADR-0070 決定 3 が言う
  「**抽出結果が空であることを確認したうえで**完了させる」文書とは別物である。所在の無い文書まで
  索引へ載せると、本文投入前の下書きが検索に現れる。**陽性対照**: 正規化を経た文書は
  `DocumentNormalized.MarkdownUri`（非 null）を必ず持つ（契約が `string`）。
- `src/ai-stock-trading`（別プロジェクトの submodule）。

## 現状（実測・`develop` `45853885`）

1. **本文が空 → チャンク 0 件**（`MarkdownChunkingService.cs:13`）。
2. **チャンクが 0 件 → Qdrant の点が 1 つも作られない**（`DocumentUpdatedConsumer` の `foreach` が回らない）。
3. 検索結果は**点（チャンク）単位**（`QdrantVectorStore.MapPayload`）。**したがって本文なしの文書が
   検索結果に現れる経路そのものが無い。**
4. `SearchResultDto` は「本文なし」を表す欄を持たない。SC-02 は `result.text` をそのまま抜粋として描く。

## 方針（詳細と根拠は `IADR-0354`）

1. **本文なしの文書には「メタデータ点」を 1 つだけ作る。** 本文由来のチャンク・埋め込みは 0 件のまま。
2. **索引テキストは題名とタグから作る**（`MetadataIndexText`）。ベクトルは**その索引テキスト**から
   作る（本文由来ではない）。埋め込みの機密区分ルーティング（ADR-0016）は本文チャンクと同一に扱う。
3. **点は `has_body = false` を持つ。** 検索側は復元時にこれを読み、**`SearchResultDto.Text` を空にする**
   —— メタデータをサービスの外へ本文として出さない。
4. **`SearchResultDto` へ `HasBody`（既定 `true`）を末尾追加**（IADR-0122 決定 2 の非破壊追加）。
5. **RAG の文脈からは外す**（`RagContextPolicy`）。検索結果には出す。FR-21 ⑨ が既に持つ
   「検索結果の集合 ≠ 文脈の集合」の構造をそのまま使う。
6. **SC-02 は本文抜粋の位置へ「本文なし（原本を参照）」を出す**（アイコン＋テキスト。色だけで意味を
   持たせない）。原本の所在は SC-03 が既に `sourceUri` で持つため、**文言から SC-03 へ導線を張る**。
7. **ABAC の判定軸は変えない。** メタデータ点も同じ `attributes` ペイロードを持ち、同じフィルタで絞られる。

## 変更するファイル（宣言領域）

- `src/knowledge/backend/Shared/Knowledge.Contracts/Dtos/SearchResultDto.cs`（`HasBody` 追加）
- `src/knowledge/backend/Shared/Knowledge.Contracts/Indexing/DocumentBodyPresence.cs`（新規・ペイロードキーと射影）
- `src/knowledge/backend/Shared/Knowledge.Contracts/Dtos/RagContextDto.cs`（本文なしを文脈から外す）
- `src/knowledge/backend/Services/IngestionService/**`（`MetadataIndexText`・`ChunkId.DeriveMetadata`・
  ポート `UpsertMetadataPointAsync`・Qdrant 実装・`DocumentUpdatedConsumer`）
- `src/knowledge/backend/Services/RetrievalService/**`（`ChunkPayload.HasBody`・Qdrant/InMemory の復元）
- `src/knowledge/frontend/src/features/sc02-results/**`（表示）＋ Lingui カタログ
- `docs/api/openapi.yaml` ＋ `src/platform/frontend/src/lib/api/generated/**`（再生成）
- `docs/functional/FR-02_*`, `docs/functional/FR-03_*`, `docs/screens/SC-02_*`, `docs/tests/*`
- `scripts/contract-schema-baseline.json`（非破壊追加の差分）

## 受け入れ基準 → テストの写像

| # | 受け入れ基準（issue） | テスト |
| --- | --- | --- |
| 1 | 本文が空でも文書は索引へ載り、**本文由来のチャンク・埋め込みは 0 件** | `DocumentUpdatedConsumerTests`（xUnit）: 埋め込み呼び出しは 1 回・引数は本文ではなく題名由来／`UpsertChunkAsync` は 0 回 |
| 2 | 題名で検索して**結果に現れる** | `HybridSearchEndpointTests` / `InMemoryVectorStore` 経路で題名一致がヒットする |
| 3 | 当該行に「本文なし（原本を参照）」が出て、**壊れた抜粋が出ない** | `SearchResultsPage.test.tsx` |
| 4 | **陽性対照**: 本文ありは従来どおり抜粋が出る | 同上（同じ描画で 2 行を比べる） |
| 5 | **ABAC** は本文の有無に関わらず効く | `HybridSearchServiceTests` / ストアのフィルタ試験（メタデータ点にも `attributes` が載る） |
| 6 | 原本へ辿れる | `SearchResultsPage.test.tsx`（文言が `/docs/{id}` へのリンクである） |
| 7 | RAG の文脈に本文なし文書が入らない | `RagContextPolicyTests` |
| 8 | ビルド・テスト・lint | `/verify` 相当 |

## 変異試験（1 本）

`DocumentBodyPresence.Excerpt` を `hasBody ? indexedText : string.Empty` から
`indexedText`（＝常に索引テキストを返す）へ変異させると、**メタデータ（題名）が本文抜粋として
利用者と LLM へ出る**。この変異を殺す試験を置く（復元試験で `Text` が空であること）。

## 実測（稼働 k3s・2026-09-03）

**差し替えたのは Ingestion / Retrieval のイメージだけ**（`kubectl set image` で `:issue-1193`）。
他の Pod は再起動していない。投入した文書 2 件と Qdrant へ置いた点 1 件は実測後に削除し、
検索側の一時的な構成上書き（後述）も戻した。

### ① 本文なしの文書は「メタデータの経路」へ入る（配備済みイメージで確認）

本文が空白だけの文書（テキスト層の無い PDF 相当）を `POST /documents` で 1 件投入した。

```console
created id=132741eb-5d38-4e4b-bc65-a0335a9a444d
markdownUri=storage://knowledge-normalized/documents/132741eb-.../body.md
```

取り込みのログ（`ingestion-service`）:

```console
Ingesting document 132741eb-... title=スキャン版 就業規則 msp1193bodyless
Fetched markdown body from object storage storage://... (7 chars)
Ingestion 132741eb-... metadata point: transient embedding failure, retrying via broker (confidentiality=public)
IngestionService...EmbeddingTransientException: Transient embedding failure for document 132741eb-... chunk -1
   at ...DocumentUpdatedConsumer.IndexMetadataOnlyAsync(...)
```

**本文（7 文字の空白）を読んだうえで `IndexMetadataOnlyAsync` へ入り、`chunk -1`（メタデータ点）で
埋め込みを要求している** —— 従前は本文が空だと何もせず完了していた経路である。

### ② 索引まで届かない理由は**本 PR の外**にある（陽性対照つき）

このクラスタは埋め込みゲートウェイが恒久的に不調である。

```console
$ curl -s -X POST http://localhost:.../embed -d '{"text":"probe","confidentiality":"public","purpose":0}'
{"vector":[],"dimensions":0,"model":"voyage-3.5","collection":"knowledge_chunks_voyage_3_5",
 "embedded":false,"endpoint":"voyage-managed","routingReason":"送信先 voyage-managed が現在利用できません。","retryable":true}
```

🔴 **陽性対照**: **本文を持つ**文書を同じ経路で投入すると、**同じ形で落ちる**。

```console
Ingesting document 7a8fbfc9-... title=msp1193withbody 陽性対照（本文あり）
Ingestion 7a8fbfc9-... chunk 0: transient embedding failure, retrying via broker (confidentiality=public)
```

**再試行と DLQ の規則は本文の有無で変わっていない**（本 PR は変えていない）。
`ADR-0070 決定 3` の「失敗として溜めない」は**再試行しても結果が変わらない**ものについての規定であり、
埋め込みの一時障害は再試行で解消し得るので、本文チャンクと同じ扱いのままにしてある。

### ③ 検索側の実測（陽性・陰性対照）

**このクラスタでは検索が全件 0 件になる**。理由は本 PR の外にある構成のずれである（実測）:

```console
$ curl -s .../collections            → knowledge_chunks_deterministic_v1 / _ruri_v3 / _voyage_3_5
$ curl -s .../collections/knowledge_chunks_voyage_3_5            → points_count: 0   ← 検索が読む先
$ curl -s .../collections/knowledge_chunks_deterministic_v1      → points_count: 3   ← 実際に点が在る先
```

そこで検索側だけ一時的に `Qdrant__CollectionName=knowledge_chunks_deterministic_v1` を与え
（**実測後に戻した**）、取り込み側が書くのと同じ形のメタデータ点（`has_body=false` /
`chunk_index=-1` / `text`＝題名 / `text_ngram`＝題名の 2-gram / 同じ ABAC 属性、点 ID は
`ChunkId.DeriveMetadata` と同じ導出）を Qdrant へ 1 点置いて、**配備済みの RetrievalService** に問い合わせた。

```console
[陽性（英数の合言葉）] query="msp1193bodyless" hits=1
    title="スキャン版 就業規則 msp1193bodyless" hasBody=false text="" chunkId=5af4af2d-...
[陽性（日本語の題名）] query="就業規則" hits=1
    title="スキャン版 就業規則 msp1193bodyless" hasBody=false text="" chunkId=5af4af2d-...
[陰性対照（本文あり seed）] query="msp-searchseed-tanpopo" hits=1
    title="msp-searchseed-tanpopo 検索導線の検証用文書" hasBody=true text="msp-searchseed-tanpopo 検索導線の検証用文書\n\nこの文書は"
```

**本文なしの文書は題名（英数・日本語のいずれでも）で結果に現れ、`hasBody=false` かつ抜粋は空**である。
**本文ありの文書は従来どおり `hasBody=true` で抜粋が返る**（本文なしの印が全件に付いていない）。

### 実測で分かった、本 PR の射程外の事実

1. **埋め込みゲートウェイが恒久的に不調**（`voyage-managed` へ到達できない）。本文の有無に関わらず
   取り込みは索引まで到達しない。
2. **検索が読むコレクションと、点が在るコレクションが違う**
   （`knowledge_chunks_voyage_3_5` = 0 点 / `knowledge_chunks_deterministic_v1` = 3 点）。
   **1 の結果として埋め込みの経路が変わったまま、検索側の既定が追随していない**形である。
3. `knowledge_chunks_deterministic_v1` には**全文ペイロード索引が 1 つも無い**（`payload_schema` が空）。
   Qdrant v1.18.1 は索引が無くても部分文字列の全走査へ落ちるため上の実測は当たったが、
   これは全文検索としては劣化している。

いずれも**このクラスタの状態**であって本 PR の変更ではない。別 issue の対象である。

## 限界・積み残し

- **パスとデータソース名は索引できない。** `DocumentUpdated` は `Title` / `MarkdownUri` /
  `Attributes` / `Tags` / `UpdatedAt` しか運ばず、**取り込み元のパス（`RawDocumentFetched.OriginalPath`）は
  ConversionService で題名（拡張子なしファイル名）へ畳まれてそこで終わる**（実測）。
  ADR-0070 決定 4 が挙げる「パス・データソース」を索引に載せるにはイベント契約の変更が要るため、
  **本 PR の射程外**とし `IADR-0354` §フォローアップ へ残す。**題名は原本のファイル名であり、
  決定 4 の「タイトル」は満たす。**
- **既存の点に `has_body` を後付けしない**（backfill 不要）。**現存する点はすべて本文チャンク**であり、
  キーが無いことは「本文あり」を正しく表す（IADR-0339 が `text_ngram` で backfill を要したのとは
  事情が違う——あちらは**既存の点に無いと検索が 0 件へ落ちる**値だった）。
