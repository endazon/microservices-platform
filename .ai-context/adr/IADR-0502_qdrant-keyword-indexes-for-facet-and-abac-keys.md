---
title: IADR-0502 権限内属性値の facet と ABAC フィルタが引くキー（tags・shared_with・attributes.<key>）に Qdrant のキーワード索引を張る。キーの集合は照会の写像から導き、起動時・書き込み時・既存の点からの発見の 3 か所で冪等に張る
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-05, FR-02, FR-03, SC-01, SC-08, NFR-06, NFR-08, ADR-0043, ADR-0009, ADR-0127, IADR-0014, IADR-0151, IADR-0318, IADR-0339, IADR-0448, IADR-0467, IADR-0497]
author: claude
created: 2026-10-06
updated: 2026-10-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0043_scoped-attribute-value-lookup.md（実装方式は実装リポジトリの IADR で確定する）
  - planning:projects/microservices-platform/07_adr/ADR-0009_vector-db-qdrant.md
related_specs:
  - ../specs/20261006_1760_qdrant-keyword-indexes.md
---

# IADR-0502: facet と ABAC フィルタが引くキーに Qdrant のキーワード索引を張る（#1760）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-06
- 決定者: claude（#1760。実 Qdrant v1.18.1 / v1.13.4 の実測で確定）

## 起点・関連

- 起点 issue: **#1760**（権限内属性値の facet が実 Qdrant ではキーワード索引が無く例外になる）
- 出所: [IADR-0151](./IADR-0151_scoped-attribute-value-facets.md) §フォローアップ「facet の payload index の要否は未検証」・
  [IADR-0497](./IADR-0497_high-confidentiality-lexical-index-vectorless-collection.md) §実測「参考」（索引が無いと `No appropriate index for faceting`。フォローアップ 4「別 issue」）
- 作法の先例: [IADR-0318](./IADR-0318_qdrant-fulltext-payload-index.md)（索引は起動のたびに存在の有無によらず張る）・
  [IADR-0339](./IADR-0339_japanese-fulltext-app-side-bigram.md)（既存の点への後付けは起動後のバックグラウンド）
- 表現の前提: [IADR-0014](./IADR-0014_qdrant-attribute-payload-key.md)（属性はネスト構造体 `attributes -> {k: v}`）・
  [IADR-0448](./IADR-0448_set-valued-document-attribute-matching-in-one-predicate.md)（集合値キーの語彙は `DocumentAttributeEncoding.SetValuedKeys`）・
  [IADR-0467](./IADR-0467_multi-collection-rrf-fusion-and-per-collection-query-embedding.md)（照会はティア A の束ねるコレクションの和集合）
- 計画: ADR-0043（スコープ付き属性値ルックアップ。実装方式は実装リポジトリの IADR で確定する）・ADR-0009（Qdrant）
- 作業仕様書: [`20261006_1760_qdrant-keyword-indexes.md`](../specs/20261006_1760_qdrant-keyword-indexes.md)（§実測 m-1〜m-10）

## コンテキストと課題

権限内属性値の照会（`POST /attribute-values`。SC-01・SC-08 の対象範囲フィルタの候補）は、検索が読む全コレクション
（主・ティア A の束ねるコレクション・語彙索引）へ ABAC フィルタつきの facet を投げ、値の和集合を返す（IADR-0151・IADR-0467・IADR-0497）。
**Qdrant の facet は、対象キーにキーワード索引が無いと `InvalidArgument: No appropriate index for faceting: <key>` で失敗する**（§実測 m-1）。
取り込みが張る索引は全文（`text`・`text_ngram`）だけであり、実 Qdrant では照会が例外になっていた（InMemory の試験は緑のまま）。

決めること:

1. 張るキーの集合（`tags`・`shared_with` は固定。`attributes.<key>` は属性辞書に従う動的な集合）と、その情報源。
2. いつ・どのコレクションに張るか（新規・既存の配備の両方）。
3. 索引でフィルタの意味（大小文字・完全一致・欠落の扱い）が変わらないか。パラメータ（`is_tenant`・`on_disk`）。
4. 索引がまだ無いキーを照会されたときの扱い。
5. readiness を足すか。

## 実測（要約。正は作業仕様書 §実測）

手元の Docker で `qdrant/qdrant:v1.18.1`（配備の版）と v1.13.4（Testcontainers の既定）。ベクトルのコレクションとベクトルの設定が空のコレクションの両方。

| # | 結果 |
| --- | --- |
| m-1 | 索引が無いと facet は `tags`・`shared_with`・`attributes.department` のいずれも失敗する（両コレクション・両版）。`keyword` を張ると通る |
| m-2 | 索引が要るのは facet するキーだけ。フィルタのキーは索引が無くても facet は通る |
| m-3〜m-5 | **フィルタの意味は索引で変わらない**: 完全一致・大小文字の区別（`HR`／`hr`／`Hr` は張る前も後も 1・1・0 件）、リストの「いずれか一致」、否定条件で欠落を残す扱い |
| m-6〜m-8 | 張り直しは冪等。点の無いキーにも張れる（facet は空）。後から張っても既存の点が索引に入る |
| m-9 | JSON パスとして不正なキー（`attributes.my key`）は張れない。そのキーは facet もできない |
| m-10 | `wait=false` は受け付けで即時に返る。不正なキー・無いコレクションの失敗は同期に返る |

## 検討した選択肢

### (1) 張るキーの集合の情報源

| 案 | 採否 | 理由 |
| --- | --- | --- |
| **照会の写像 `AttributeValueKeys.ToPayloadKey` を通して導く**（`KeywordIndexKeys`。採用） | ○ | 照会（facet）とフィルタ（`BuildAttributeConditions`）が引くキーと、索引のキーが**同じ関数の出力**になる。集合値キーの語彙は `DocumentAttributeEncoding.SetValuedKeys`（IADR-0448）から取るので、集合値キーを足せば索引も増える |
| 取り込み側に張るキーを列挙する（`["tags", "shared_with", "attributes.department", …]`） | × | 照会側だけ増えたキーに索引が無い形が作れる（#539 が `tags` で踏んだ「片方だけ直る」型） |

### (2) いつ張るか（`attributes.<key>` は動的）

| 案 | 採否 | 理由 |
| --- | --- | --- |
| (a) **書き込み時に、書く点の属性キーへ張る**（プロセス内で覚え、同じ〔コレクション, キー〕へは 2 度呼ばない。採用） | ○ | 点を持つキーには索引の作成が必ず出る（構築の完了は待たない。索引が無いキーの読み方は決定 5）。属性辞書を知らなくてよい |
| (b) 起動時に属性辞書（認可サービス）のキーへ張る | × | 取り込み（knowledge）から認可サービス（platform）への起動時の依存とサービス間の認証が要る。辞書に無いキーを持つ文書（辞書の改廃・手で投入された点）を取りこぼし、辞書にあって点の無いキーへは張っても facet は空である |
| (c) 検索が facet の失敗を見て張る | × | 検索は読み手であり、索引の持ち主は取り込みである（IADR-0318 と同じ分担）。2 つのサービスが同じ索引の宣言を持つと、パラメータが割れて張り替えが往復する。最初の照会は失敗するか遅れる |
| (d) 起動時だけ、既存の点を標本で読んで張る | × | 少数の文書にだけ付いたキーを取りこぼす（「その値を持つ文書は在るのに候補に出ない」） |

**既存の配備**: (a) は「このプロセスが書いたキー」にしか効かない。再起動後に一度も書かれていないキーを持つ点のために、
**起動後のバックグラウンドで全コレクションの点を `attributes` だけ読んで走査し、現れたキーへ (a) と同じ経路で張る**（発見。IADR-0339 の後付けと同じ作法）。
`payload_schema`（コレクション情報）は張り済みの索引しか返さないので、張っていないキーの発見には使えない。

### (3) 書き込み時の失敗

| 案 | 採否 | 理由 |
| --- | --- | --- |
| **書き込みを止めない**（Warning。不正なキーは覚えて再試行しない・他の失敗は次の書き込みで張り直す。採用） | ○ | 候補のための索引で本文の索引（FR-02）を止めない。不正なキーは facet もできないので失うものが無い（m-9） |
| 失敗を上げて取り込みを再試行へ落とす | × | 不正なキーを持つ文書は永久に索引されない（再試行しても直らない） |

## 決定

### 決定 1: 張るキーの集合は `AttributeValueKeys.KeywordIndexKeys(attributeKeys)` の 1 か所から導く

集合値キー（`DocumentAttributeEncoding.SetValuedKeys` ＝ `shared_with`・`tags`）∪ 属性キー、を **`ToPayloadKey` に通した**もの（序数順・重複なし・空と空白は捨てる）。
属性キー `department` は `attributes.department`、`Tags`（大小文字違い）は照会と同じく最上位の `tags` へ寄る。

### 決定 2: 張る時点は 3 つ（どれも冪等な `CreatePayloadIndex`）

- (i) **起動時**（`QdrantBootstrapHostedService` → `EnsureKeywordIndexesAsync`）: 集合値キーを全コレクションへ、存在の有無によらず張る（IADR-0318 決定 2 と同じ作法）。失敗はブートストラップの Error。
- (ii) **書き込み時**（`UpsertChunkAsync`・`UpsertMetadataPointAsync`・`UpsertLexicalChunkAsync`・`UpsertLexicalMetadataPointAsync`）: 書く点の属性キーを決定 1 へ通し、
  まだ張っていない（コレクション, キー）だけを `wait=false` で張る（索引の構築を取り込みの期限 `IngestionTimeouts` に入れない）。
  記憶はプロセス内だけで、(i) と共有する。失敗の扱いは §検討 (3)。呼び出し元の取り消しは上げる。
- (iii) 下の決定 3。

### 決定 3: 既存の点からの発見（`QdrantKeywordIndexDiscoveryHostedService` → `EnsureKeywordIndexesForExistingPointsAsync`）

起動後のバックグラウンドで、全コレクションを 1,024 点ずつ `attributes` だけ読んで（ベクトルは読まない）最後のページまで scroll し、現れた属性キーへ決定 2 (ii) の経路で張る。
**標本にしない**（§検討 (2) (d)）。例外は器が捕まえて Error で残す（ホストを止めない。IADR-0339 の後付けと同じ）。
**1 つのコレクションの走査の失敗（`NotFound`・`Unavailable` 等の `RpcException`）は、そのコレクションだけを諦めて
Warning（コレクション名と状態コードだけ。ペイロードの値は出さない）を残し、次のコレクションへ進む**（独立監査 🟡3）。
呼び出し元の取り消しは上げる。次のページは前の応答の `NextPageOffset` から読む（単体試験が要求の位置を突き合わせて固定する。独立監査 🟡1）。

**コレクション**: 取り込みが持つ全コレクション ＝ `Embedding:Collections` の全モデル ＋ 語彙索引（IADR-0497）。
ティア A の束ねるコレクション（`knowledge_chunks_ruri_v3`）と検索の主（`knowledge_chunks_voyage_3_5`）は `Embedding:Collections` に含まれる（`appsettings.json`・helm）。

### 決定 4: パラメータは `keyword`。`is_tenant`・`on_disk` は既定（偽）のまま

`BuildKeywordIndexParams()`（純関数）が空の `KeywordIndexParams` を明示する。属性は全問い合わせに付くテナントの区切りではなく（`is_tenant` は 1 つのキーで点を分割する最適化）、
規模（NFR-08 の数十万点）はメモリ上の索引で足りる。**索引でフィルタの意味は変わらない**（m-3〜m-5）ので、ABAC が過剰にも過少にも許可しない。

### 決定 5: facet するキーに索引が無ければ、そのコレクションの値は空集合にする（検索側）

`QdrantVectorStore.ListAttributeValuesAsync` は、`InvalidArgument` かつ本文 `No appropriate index for faceting` の失敗**だけ**を空集合へ倒す（**Warning** を残す。ペイロードキーは `LogSanitizer` で無害化する）。
索引が無いのは、**そのコレクションのどの点もそのキーを持たないか、その索引がまだ構築されていないか**である（独立監査 🟡2）。後者の原因は次の 3 つ:
  - 書き込み時の作成は `wait=false` なので、構築が終わる前に facet が来た（決定 2 (ii)）。
  - 作成が一時的に失敗した（取り込みは Warning だけ残して書き込みを続け、次の書き込みで張り直す。決定 2 (ii)）。
  - 再起動後、発見の走査（決定 3）がまだ終わっていない、または失敗した（そのコレクションのキーは次に書かれるか次の起動まで索引が無い）。

前者では空集合が正しい答えであり（例: SC-01 の軸 `project` を 1 文書も持たない配備で、その軸の照会全体が例外になるのを防ぐ）、
後者では候補を一時的に取りこぼす（減らす向き）。後者に運用者が気付けるよう、Information ではなく Warning で残す。
不正なキー（同じ `InvalidArgument`・別の文言）と Qdrant の不調は従来どおり上げる。空集合は候補を減らす向きで、ABAC を緩めない。

### 決定 6: readiness は足さない

`qdrant-fulltext-index` が readiness に在るのは、全文索引の欠落が**例外を伴わず**部分文字列の全走査へ静かに落ちるからである（IADR-0318 決定 3）。
キーワード索引の欠落は (1) 決定 2 で書き込みのたびに自己修復し、(2) 集合値キーの欠落はブートストラップの Error・書き込み時の Warning に出る。
属性キーは動的なので、検索側からは「どのキーに索引が在るべきか」を知り得ず、見られるのは固定の 2 キーだけである。費用に見合わないので足さない。

## 結果

- 良い影響:
  - 実 Qdrant で権限内属性値の照会が例外にならない（主・ティア A・語彙索引の全部。統合試験 I-11）。検索段の ABAC フィルタも同じ索引を使う（意味は不変）。
  - 張るキーと照会するキーが同じ関数から出る（片方だけ増えない）。
- 悪い影響・制約（受け入れたもの）:
  - **発見の走査は起動のたびに全点の `attributes` を読む**（NFR-08 の数十万点で 1,024 点 × 数百ページ。バックグラウンドで、取り込みと検索は止めない）。
    回数を減らす記録（どのキーまで張ったか）は持たない —— 記録が実態とずれる経路を増やさない。
  - 発見の走査が終わるまで（または失敗した間）、再起動後にまだ書かれていない既存のキーには索引が無く、その軸の候補は空集合になる（決定 5）。
  - 書き込み時の索引は `wait=false` なので、張った直後の数百ミリ秒は facet が空集合を返し得る。
  - JSON パスにならない属性キーには索引が付かない（facet もできない。従来どおり例外）。
  - `check-stack-ready.js` の門 G13・検索の readiness はキーワード索引を見ない（決定 6）。
  - 検索の `QdrantVectorStore.UpsertAsync`（本番の呼び出し元は無い）は索引を張らない。
- **機械検査**: キーの集合・パラメータ・各経路の呼び出しは単体試験（`QdrantKeywordIndexTests`・`QdrantKeywordIndexHostedServiceTests`・`AttributeValuesMissingIndexTests`）、
  実 Qdrant での成立は統合試験 `KeywordIndexQdrantTests`（`Category=Integration`。PR の CI では走らない。`integration.yml` が回収する）。

## 試験

受け入れ基準と試験の対応は作業仕様書 §受け入れ基準 → 試験 と `docs/tests/FR-02_ingestion.md`（T-34〜T-39・I-11〜I-13）・`docs/tests/FR-05_abac-access-control.md`（T-73）。

## 関連

- Supersedes: なし（IADR-0151 §フォローアップ・IADR-0497 フォローアップ 4 の未決事項を決める。両 IADR に日付つきで追記）
- Superseded by: なし
