---
title: IADR-0497 高機密文書（confidential・restricted・機密区分が未指定・未知）は埋め込みを呼ぶ前に分け、ベクトルを持たない専用のコレクション（語彙索引）へ全文索引だけで書く。検索は語彙索引を全文の系統だけで束ね、意味検索のモードには入れない
type: impl-adr
status: Accepted
related_ids: [FR-02, FR-03, FR-05, FR-19, UC-01, UC-04, SC-02, ADR-0127, ADR-0092, ADR-0016, ADR-0017, ADR-0070, ADR-0057, ADR-0061, IADR-0025, IADR-0085, IADR-0467, IADR-0422, IADR-0318, IADR-0339, IADR-0358, IADR-0014, IADR-0012]
author: claude
created: 2026-10-05
updated: 2026-10-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0127_high-confidentiality-lexical-only-and-claude-rerank.md (決定 1・2・4・7。フォローアップ 1・2)
  - planning:projects/microservices-platform/07_adr/ADR-0092_multi-collection-search-fusion-and-query-egress.md (決定 1・3)
related_specs:
  - ../specs/20261005_1746_high-confidentiality-lexical-index.md
---

# IADR-0497: 高機密文書は埋め込まず、ベクトルを持たない専用のコレクション（語彙索引）にだけ載せる（#1746 段 S1）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-05
- 決定者: claude（#1746 段 S1。計画 ADR-0127 フォローアップ 1・2 の実装）

## 起点・関連

- 起点 issue: #1746（段 S1: 語彙索引）
- 関連する計画書 ID: FR-02（取り込み）・FR-03（横断検索・検索モード 3 値）・FR-05（ABAC）・FR-19（個人資料）・UC-01・UC-04・SC-02
- 関連する計画 ADR: **ADR-0127**（決定 1: 高機密は埋め込まず語彙索引だけ・置き場所は実測して IADR に残す・意味の無いベクトルは採らない／
  決定 2: キーワードとハイブリッドでだけ RRF に入り、意味検索には現れない・ABAC は全系統／決定 4: `restricted` と未指定・未知も同じに扱う）、
  ADR-0092（決定 1: 束ねて RRF／決定 3: ABAC は束ね方と独立）、ADR-0016（コレクションのモデル別分離）、
  ADR-0017（Superseded by ADR-0127・注記は #1746）、ADR-0070 決定 4（本文なしの文書はメタデータで索引）、ADR-0057 決定 1（削除の伝播）、ADR-0061（個人資料の露出）
- 関連する実装 ADR: [IADR-0025](./IADR-0025_embedding-provider-routing-and-model-collections.md)（埋め込みの機密区分ルーティング。**本 IADR が取り込み側の高機密の経路を改める**。同 IADR に追記）・
  [IADR-0085](./IADR-0085_selfhosted-embedding-optin-deploy.md)（Ruri の opt-in 配備。**opt-in のまま残る**。同 IADR に追記）・
  [IADR-0467](./IADR-0467_multi-collection-rrf-fusion-and-per-collection-query-embedding.md)（束ねる追加コレクション。**既定が空でなくなる**。同 IADR に追記）・
  [IADR-0318](./IADR-0318_qdrant-fulltext-payload-index.md) / [IADR-0339](./IADR-0339_japanese-fulltext-app-side-bigram.md)（全文索引 `text` / `text_ngram`）・
  [IADR-0358](./IADR-0358_bodyless-document-metadata-point.md)（本文なしのメタデータ点）・IADR-0014（ABAC 属性のネスト構造体）・IADR-0012（deny-by-default）・
  [IADR-0422](./IADR-0422_ndcg-harness-and-query-embedding-profile.md)（nDCG のハーネス。段 S3 が使う）
- 関連する実装仕様書: `.ai-context/specs/20261005_1746_high-confidentiality-lexical-index.md`（実測の生の出力・母集合・変異試験はそちら）
- 採番: `origin/develop` `5192a7a4` の最大は IADR-0496。開いている PR（#1726 CHANGELOG の自動更新）は IADR を足さない。本件は **0497**。

## コンテキストと課題

計画 ADR-0127 は、`confidential`・`restricted`・機密区分が未指定・未知の文書を「埋め込みを作らず、全文索引（語彙索引）にだけ載せる」と決めた
（Ruri v3 を稼働 PoC に配備しないため。MSP#336 は not planned）。実装の現状（`origin/develop` `5192a7a4`）:

- 取り込み（`DocumentUpdatedConsumer`）は高機密のチャンクも埋め込みへ送り、ゲートウェイの拒否（`Embedded=false`・`Retryable=false`）を見て
  `skipped++; continue;` する。**どこにも書かない。** 本文の無い文書のメタデータ点も同じ（計画 ADR-0127 実測 3）。
- 全文索引は**モデル別のベクトルのコレクションの上のペイロード索引**であり、点はベクトルとペイロードを 1 つで書く（同 実測 4）。
  したがって高機密文書は**どの検索モードでも見つからない**。
- 検索側には、追加のコレクションを束ね、クエリを埋められなければベクトルの系統だけを落として全文は引く経路が既にある（IADR-0467。同 実測 5）。

置き場所は「(a) ベクトルを持たない専用のコレクション」か「(b) 既存のコレクションにベクトルの無い点を書く」かを、Qdrant の版で実測して決めよ、というのが ADR-0127 決定 1 である。

## 実測（2026-10-05。生の出力は作業仕様書 §実測）

配備の版 **v1.18.1**（`deploy/docker-compose.yml`・`deploy/local/infra/qdrant.yaml`）と、Testcontainers 4.12.0 の既定 **v1.13.4** の両方で、
REST（curl）と gRPC（`Qdrant.Client` 1.18.1。本番の取り込み・検索と同じクライアント）の両方を当てた。

| # | 問い | v1.18.1 | v1.13.4 |
| --- | --- | --- | --- |
| a-1 | ベクトルの設定が空のコレクションを作れるか（REST `PUT {}` / gRPC `CreateCollectionAsync(name, new VectorParamsMap())`） | **作れる**（`vectors: {}`。gRPC は `paramsMap: {}`） | 同じ |
| a-2 | そこへ `text`（multilingual）・`text_ngram`（prefix）の全文索引を張れるか | **張れる** | 同じ |
| a-3 | ベクトルの無い点を書けるか | **空の名前つきベクトル（REST `"vector": {}` / gRPC `Vectors = { Vectors_ = new NamedVectors() }`）なら書ける**。`vector` を省くと REST は `missing field vector`、gRPC は `Expected some vectors` で拒む | 同じ |
| a-4 | 全文の Match と ABAC（`attributes.confidentiality` の `any`）で scroll できるか | **できる**（陽性・陰性とも期待どおり。日本語は `text_ngram` で当たる） | 同じ |
| a-5 | ベクトル検索を投げるとどうなるか | **拒まれる**（`Not existing vector name`） | 拒まれる（`Vectors are not configured in this collection`） |
| a-6 | `document_id` で削除できるか | **できる**（件数 2 → 1。gRPC で 1 → 0） | 同じ |
| b-1 | 既存の**無名ベクトル**のコレクション（`size: 4`）へベクトルの無い点を書けるか | **空の名前つきベクトルなら書ける**（点数 1・ベクトル検索は 0 件）。`vector: []` は `dense vector must not be empty` で拒む | 空の名前つきベクトルなら書ける。`[]` は `expected dim: 4, got 0` |
| 参考 | facet（`ListAttributeValuesAsync`）はキーワード索引の無いキーで動くか | **動かない**（`No appropriate index for faceting`）。**ベクトルのコレクションでも同じ**（`vec_f` で実測） | 同じ |

🔴 **(a)・(b) のどちらも両版で成立する。** 決め手は実測ではなく、次の「検討した選択肢」の比較である。
🔴 **「参考」の行は本件と独立の既存の欠陥である**（権限内属性値の照会は、キーワード索引を張っていないので実 Qdrant では例外になる。
IADR-0151 §結果 が「facet の payload index の要否は未検証」と残した事項の実測）。語彙索引を束ねても悪化しない（主の照会が先に同じ理由で落ちる）。別 issue に切り出す。

## 検討した選択肢

| 案 | 評価 |
| --- | --- |
| **(a) ベクトルを持たない専用のコレクション（語彙索引）を、検索側の「束ねる追加コレクション」として足す** | **採用**。①**置き場所が埋め込みのモデルに依らない** —— 取り込みは書き先をゲートウェイの応答（`EmbedApiResponse.Collection`）から知るが、高機密文書はゲートウェイを呼ばないので、(b) では「どのベクトルのコレクションへ書くか」を別途決める必要があり、決定的ローカル埋め込み（IADR-0313）や nDCG の A/B（`Qdrant:CollectionName` を切り替える。IADR-0422）で**主が切り替わると高機密文書だけが置き去りになる**。②**意味検索に出ないことが構造で決まる** —— コレクションにベクトルが無いので、ベクトル検索は Qdrant が拒む（a-5）。③削除・全文索引・後付け・ABAC・束ね方（IADR-0467）は既存の経路がそのまま効く。④ベクトルのコレクションの `points_count` と `indexed_vectors_count` を食い違わせない（監視・nDCG の母集合を濁さない） |
| (b) 既存のベクトルのコレクションに、ベクトルの無い点を書く | 採らない。成立はする（b-1）が、上の ① を解けない（主が切り替わると置き去り）。加えて、**無名ベクトルのコレクションへ空の名前つきベクトルを受け入れる挙動**は、REST の `[]` も gRPC の未設定も拒む中で 1 つだけ通る形であり、版で変わり得る受理に索引の正しさを預けることになる |
| (c) 決定的なハッシュ等の意味の無いベクトルで点を作る | **採らない**（ADR-0127 決定 1 が退けた。ベクトルの系統の順位を汚す）。語彙索引の点は空の名前つきベクトルで書き、零ベクトルも入れない |

取り込みの分け方:

| 案 | 評価 |
| --- | --- |
| **埋め込みを呼ぶ前に、文書の機密区分で分ける（allow-list: `public` / `internal` だけが埋め込みへ進む）** | **採用**。ADR-0127 は「埋め込まない」であり「ティア A が無ければ埋め込まない」ではない。ゲートウェイの拒否を見てから分ける形は、Ruri を opt-in で有効にした配備では**断られずに埋め込まれる**（IADR-0085 の配備物は残る）うえ、本文がゲートウェイへ 1 度渡る |
| ゲートウェイの恒久的な拒否（`Embedded=false`・`Retryable=false`）を見て語彙索引へ回す | 採らない。上のとおり。加えて、`public` / `internal` の恒久的な拒否（Voyage 経路の無効化・次元不整合）まで語彙索引へ回り、**埋め込めるはずの文書の不調を覆い隠す** |
| ゲートウェイ（`EmbeddingEgress` / `EmbeddingRouter`）を改め、高機密を「語彙索引で書け」と答えさせる | 採らない。ゲートウェイの越境判定は**強める向きにしか変えない**（ADR-0127 決定 1）。現状の fail-closed（高機密はティア A だけ・無ければ拒否）がそのまま二重の守りになり、改める理由が無い |

## 決定

### 決定 1: 語彙索引はベクトルを持たない専用のコレクション `knowledge_chunks_lexical` に置く

- **作り方**: `CreateCollectionAsync(name, new VectorParamsMap())`（名前つきベクトルを 1 つも持たない）。`text`（multilingual）と `text_ngram`（prefix）の全文索引を、
  ベクトルのコレクションと同じ作法（存在の有無によらず毎回張る。冪等）で張る。起動時のブートストラップ（`QdrantBootstrapHostedService`）が作る。
- **点の書き方**: `Vectors = new Vectors { Vectors_ = new NamedVectors() }`（空）。`BuildLexicalPoint` の 1 か所に閉じる。零ベクトル・ハッシュは入れない。
- **名前**: 取り込みと検索が**同じ設定キー `Qdrant:LexicalCollection`** を読む。空・未設定は既定名 `knowledge_chunks_lexical` へ倒す。
  🔴 **無効化の口は持たない**（載せない構成を設定 1 つで作れると、以前の「どの検索でも見つからない」へ黙って戻る）。
  ベクトルのコレクション（取り込みの `Embedding:Collections`／検索の主・`Qdrant:FusedCollections`）と同名なら**起動を止める**。
- **配備**: Helm の `lexicalIndex.collection`（必須。空なら描画が失敗する）を ingestion と retrieval の両方へ `Qdrant__LexicalCollection` として描画する。
  compose は両サービスに `${SEARCH_LEXICAL_COLLECTION:-knowledge_chunks_lexical}`。6 か所の一致は `scripts/k8s-local-up.test.js` が静的に固定する。

### 決定 2: 取り込みは、埋め込みを呼ぶ前に機密区分で分ける。高機密文書は埋め込みを 1 回も呼ばない

- 判定は `LexicalIndexPolicy.IsLexicalOnly`。**`ConfidentialityLevels.FromAttributes`（知識ユニット共通の正規化。未知・未指定は `restricted`）の結果が
  `public` / `internal` でなければ語彙索引だけ**（allow-list）。
  - ゲートウェイの `SensitivityClasses.Parse` は前後の空白を落とすが、こちらは落とさない。**食い違うときは埋め込まない側へ倒れる**（`" public "` は語彙索引）。
    逆向き（ここで埋めるのにゲートウェイが拒む）は起きない —— 埋め込みへ進む集合はゲートウェイがティア B を許す集合の部分集合である。
- 高機密文書は、本文チャンクを `UpsertLexicalChunkAsync`、本文の無い文書をメタデータ点 1 つ（`UpsertLexicalMetadataPointAsync`。`has_body = false`）で書く。
  **埋め込みの総枠（`EmbeddingBudget`）と一時障害の分岐を通らない**（埋め込みを呼ばないため）。Qdrant への書き込みは本文チャンクと同じ期限の下で行い、失敗は例外のままブローカの再試行へ委ねる。
  `IngestionCompleted` のチャンク数は語彙索引へ書いた数である。
- **`public` / `internal` の経路は 1 ビットも変えない。** 一時障害（`Retryable=true`）は従来どおり例外で再試行、恒久的な拒否は従来どおりスキップ（**語彙索引へは回さない**）。
- **ゲートウェイ（`EmbeddingEgress` / `EmbeddingRouter`）は変えない。** 高機密はティア A だけ・無ければ拒否、のまま二重の守りとして残る。

### 決定 3: 語彙索引のペイロードは、ベクトルのコレクションの点と同じ表現にする

- `BuildChunkPayload` をそのまま使う（`document_id`・`document_title`・`text`・`text_ngram`・`markdown_uri`・`chunk_index`・`updated_at`・`tags`・`shared_with`・`attributes`。本文なしは `has_body = false`）。
  **ABAC・削除・並び順・全文の判定軸を、埋め込みの有無で割らない。** チャンク ID も本文チャンクと同じ規則（`ChunkId.Derive` / `DeriveMetadata`）。
- 取り込みのポートは**コレクションもベクトルも引数に取らない**専用の口（`UpsertLexicalChunkAsync` / `UpsertLexicalMetadataPointAsync`）にする。
  呼び出し側がベクトルのコレクションを選べると、ベクトル無しの点をそこへ書けてしまう。

### 決定 4: 文書単位の削除・全文索引・後付けは、語彙索引を含む全コレクションへ効かせる

- 取り込みの `DeleteByDocumentFromAllAsync` は語彙索引からも消す（機密区分が `confidential` → `public` へ下がった文書の古い点を残さない。逆向きは従来から全消し）。
- 検索の文書削除の購読（`DocumentDeletedConsumer`）は、束ねる追加コレクション（決定 5 で語彙索引を含む）からも消す（既存の経路。ADR-0057 決定 1）。
- **語彙索引のコレクションが無いとき（`NotFound`）の削除は no-op にする**（監査 🟡3）。語彙索引は**取り込みサービスの起動時のブートストラップだけ**が作るので、
  検索サービスが先に上がった・取り込みのブートストラップが失敗した間は存在しない。無いコレクションには消す点も無い。
  検索側は語彙索引の `QdrantVectorStore` だけを `missingCollectionIsEmpty: true` で組み、取り込み側は語彙索引の削除だけを `NotFound` で握る。
  **主・ベクトルのコレクションの `NotFound` と、`NotFound` 以外の失敗は従来どおり例外**（再試行・デッドレターへ）。
- 削除の時間の上限は本数を 1 本増やす（取り込み: 既定 2 → 3 本。検索: 既定 1 → 2 本。いずれも受け口の実行期限に収まる）。
- `text_ngram` の索引と後付け（`BackfillCjkNgramAsync`）は語彙索引も対象にする。

### 決定 5: 検索は語彙索引を全文の系統だけで束ねる。意味検索のモードには入れない

- 語彙索引は**常に**束ねる追加コレクションとして登録する（`FusedCollection(..., LexicalOnly: true)`。埋め込みの客体は常に空を返す `NoQueryEmbedding`。追加コレクションの**最後**に置く）。
- **キーワードのモード**: 主と語彙索引（と他の追加コレクション）の全文の並びを RRF で合成する。
- **ハイブリッドのモード**: 語彙索引は全文の系統 1 本として、IADR-0467 の 1 回の RRF に平らに入る。**クエリを埋めない**（客体も呼ばない）・**ベクトル検索を引かない**・**縮退の警告を出さない**（設計どおりの欠落である）。
- **意味検索のモード**: 語彙索引を**問い合わせない**。意味検索の経路を選ぶ判定は「ベクトルの系統を持つ追加コレクションの数」で行うので、
  既定（語彙索引だけを束ねる）では意味検索は**従来の単一コレクションの経路のまま**（生スコア・呼び出し回数とも同じ）。
- **ABAC**: 語彙索引の全文の系統へ、主と同じフィルタをそのまま渡す（ADR-0092 決定 3。問い合わせの省略を統制にしない）。
- 🔴 **キーワードのモードの `Score` は RRF の値になる**（従来は主の全文の並びの擬似スコア `1/順位`）。束ねるコレクションが 2 本以上になったための必然であり、並びの意味（順位）は変わらない。
- 二段検索（ADR-0035）の起点は主のベクトル側のままである（IADR-0467 決定 5）。語彙索引の文書は起点にならない。

### 決定 6: 段 S2（Claude の再順位付け）が使う接点

S1 は再順位付けを実装しない。S2 が要るものを次のとおり残す:

- **候補**: `HybridSearchService.SearchDetailedAsync` が返す `HybridSearchOutcome.Fused`（並べ替え・切り詰めの前・ABAC 後）。段は二段検索（`GraphExpandingSearchService`）と同じく
  `IHybridSearchService` のデコレータとして挟み、最後に `Finish`（露出の用途 `search` で落とし、並べて `topK` へ切る）を通す形が既存の作法である。
- **越境の判定**: 各候補は `Attributes`（`confidentiality`）を持つ。最も高い機密区分は `ConfidentialityLevels.FromAttributes` ＋ `Rank` で求める（`RagOrchestrator.HighestConfidentiality` と同じ）。
  語彙索引の文書は `restricted` / 未指定（→ `restricted`）を含むので、**ZDR 必須の判定は省けない**（ADR-0127 決定 3・4）。
- **個人資料**: 再順位付けへ送る候補にも `AiInputExposure.IsAllowed`（`ai_input`。既定「含めない」）を当てる（ADR-0127 決定 3）。
- **用途**: LLM ゲートウェイの用途（`CompletionApiRequest` の `Purpose`。回答生成は `rag-answer`）に `rerank` を足し、費用を回答生成と分けて計上する（S2）。

## 理由

- **置き場所を埋め込みのモデルから切り離す**のが (a) の本質である。高機密文書は埋め込みを持たないので、ベクトルのコレクションのどれかに置く理由が無い。
- **呼ぶ前に分ける**のは、ADR-0127 が「埋め込まない」を機密区分の性質として決めたからである。送信先の有無（ティア A の配備）で結果が変わる形にしない。
- 検索側は**既にある縮退の経路（IADR-0467）を、縮退ではなく設計として使う**。新しい合成の機構を作らない（ADR-0127 §理由「検索の側はほぼそのまま使える」）。

## 結果

- **良い影響**
  - 高機密文書が、キーワードとハイブリッドの検索で見つかる（ABAC の許す利用者に限る）。意味検索のモードには現れない（ADR-0127 決定 2）。
  - 高機密文書の本文は、どの埋め込みの送信先にも渡らない（Ruri を opt-in で有効にした配備でも）。
- **悪い影響 / トレードオフ**
  - 🔴 **RAG 回答の経路では、語彙索引の文書が文脈に入り、`restricted` と機密区分が未指定・未知の本文が Claude（ティア B・ZDR）へ送られ得る。** S1 の時点でこの経路が開く
    （計画 ADR-0127 実測 9。越境は最も高い機密区分で判定し、ZDR に対応しないモデルは除かれる。個人資料は `ai_input` に従う）。**ADR-0127 決定 4 が受け入れたリスク**であり、追加統制は無い。
  - 🔴 **個人資料（既定の機密区分 `restricted`）は、露出のトグルのどれかが ON なら語彙索引に載る。** 従来は既定構成で 1 点も索引されていなかった（埋め込みが拒まれていた）。
    横断検索に出るのは `search_exposure` が ON のときだけで（`Finish`）、見えるのは所有者・共有先の分岐だけである（ABAC。ADR-0061 決定 5・6）。
  - キーワードのモードの `Score` が RRF の値になる（決定 5）。
  - 削除の Qdrant 呼び出しが文書あたり 1 回増える。
- **残るもの**
  - 🔴 **展開の順序と依存**: 語彙索引のコレクションを作るのは取り込みサービスの起動時のブートストラップ（`QdrantBootstrapHostedService`）だけである。
    **取り込みサービスを先に（または同時に）上げ、起動ログで語彙索引の作成と全文索引が成功したことを確かめてから、検索サービスを上げる。**
    逆順・ブートストラップの失敗の間、検索側では (1) 文書削除は語彙索引の分だけ no-op（上記。削除の購読は失敗しない）、
    (2) **キーワードとハイブリッドの検索は語彙索引の全文の系統が `NotFound` で落ち、検索ごとに縮退の警告と計器
    `search.keyword_degraded.total`（理由 `backend_error`）が 1 件ずつ増える**（検索そのものは 200 で続く）。取り込みの再起動で作られれば収まる。
    手順は運用仕様書 §埋め込みプロバイダ。
  - **既に取り込まれた高機密文書は、語彙索引に自動では入らない**（従来どこにも書かれていない）。運用で `DocumentUpdated` を再発行する（運用仕様書 §埋め込みプロバイダ の「再索引手順」の手順 2）。
  - **巨大な高機密文書の打ち切りが無い**: 語彙索引への書き込み（`IndexLexicallyAsync`）は埋め込みを呼ばないので埋め込みの総枠（`EmbeddingBudget`）を通らず、
    チャンク数に比例する Qdrant の書き込みを受け口の実行期限（既定 420 秒）だけが上から抑える（1 回 10 秒の期限つき）。段 S4 で扱う。
  - 検索の readiness（`qdrant-fulltext-index` / `qdrant-cjk-ngram-index`）は主コレクションの索引しか見ない（Ruri のコレクションと同じ）。語彙索引の索引の欠落は取り込みの起動時ログ（Error）だけに出る。
  - 取り込みは Ruri のコレクション（`knowledge_chunks_ruri_v3`）を今も作る（空）。片付けは段 S4。
  - 権限内属性値（facet）は実 Qdrant ではキーワード索引が無く例外になる（既存の欠陥。§実測「参考」）。
  - 「横断検索に含める」OFF・「AI の入力に含める」ON の個人資料は RAG の文脈に入らない（`Finish` が横断検索の露出で先に落とす。既存の挙動で fail-closed の向き。
    語彙索引で到達するようになった）。#1752 で扱う。
  - **機械検査**: 語彙索引の名前の一致は `k8s-local-up.test.js` が見る。分け方・束ね方は単体試験と実 Qdrant の統合試験（`Category=Integration`。PR の CI では走らない）が見る。
- **フォローアップ**
  1. 段 S2: Claude による再順位付け（決定 6 の接点）。
  2. 段 S3: nDCG@10 の差（「全文のみ＋Claude」と「ハイブリッド（voyage）」）を IADR-0422 のハーネスで測る。
  3. 段 S4: Ruri の配備物と関連文書の後始末。ADR-0017 の引用の追随（本 PR で注記した範囲は作業仕様書 §ADR-0017 の引用）。
  4. 別 issue: 権限内属性値の facet のキーワード索引。

## 試験

受け入れ基準と試験の対応（T-ID）は作業仕様書 §受け入れ基準 → 試験 と `docs/tests/FR-02_ingestion.md`（T-23〜T-33）・`docs/tests/FR-03_hybrid-search.md`（T-92〜T-99）。

## 関連

- Supersedes: なし（IADR-0025 の取り込み側の高機密の扱いを改める。同 IADR に追記）
- Superseded by: なし
