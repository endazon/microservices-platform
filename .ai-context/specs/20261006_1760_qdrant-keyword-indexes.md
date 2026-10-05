---
title: 作業仕様書 — 権限内属性値の facet と ABAC フィルタが引くキー（`tags`・`shared_with`・`attributes.<key>`）に Qdrant のキーワード索引を張る（#1760）
type: spec
status: done
related_ids: [FR-04, FR-05, FR-02, FR-03, SC-01, SC-08, NFR-06, ADR-0043, ADR-0009, ADR-0127, IADR-0502, IADR-0014, IADR-0151, IADR-0318, IADR-0339, IADR-0448, IADR-0467, IADR-0497]
author: claude
created: 2026-10-06
updated: 2026-10-06
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0043_scoped-attribute-value-lookup.md（実装方式は実装リポジトリの IADR で確定する）
  - planning:projects/microservices-platform/07_adr/ADR-0009_vector-db-qdrant.md
issue: "#1760"
---

# 作業仕様書 — Qdrant のキーワード索引（権限内属性値の facet・ABAC フィルタ）（#1760）

> 本仕様書は実装着手前に作成した（着手 2026-10-06）。判断の記録は **IADR-0502** に置く。
> 計画は project-planning `c3ad458` を読んだ。基点は MSP `origin/develop` `0172d9b7`。

## 起点となる計画書（トレーサビリティ）

- 要求: **FR-04**（対象範囲フィルタの候補）・**FR-05**（ABAC。アクセス可能な文書のみを検索・回答対象とする）。
  ブランチ名は FR-05 を起点に取った（候補の口も検索も ABAC フィルタの同じキーを引く）。画面は **SC-01**（検索・チャット）・**SC-08**（AI 分析）の対象範囲フィルタ。
- 計画 ADR: **ADR-0043**（スコープ付き属性値ルックアップ。「検索段の Qdrant ペイロードフィルタを用いた実装方式は実装リポジトリの IADR で確定する」）・**ADR-0009**（Qdrant）。
- 関連 IADR: IADR-0151（facet で数える。§フォローアップ「payload index の要否は未検証」）・IADR-0497（§実測「参考」で facet が索引なしでは例外になることを実測し「別 issue」とした）・
  IADR-0318（索引は起動のたびに無条件で張る）・IADR-0014（属性はネスト構造体 `attributes -> {k: v}`）・IADR-0448（集合値キーの語彙は `DocumentAttributeEncoding` が持つ）。
- 起点 issue: **#1760**。
- 採番: `origin/develop` `0172d9b7` の IADR の最大は 0501。本件は **IADR-0502**。

## 射程と、触らないもの

- **触る**: 取り込みの Qdrant アダプタ（`QdrantIngestionVectorStore`。キーワード索引の起動時の付与・書き込み時の付与・既存の点からのキーの発見）、
  起動時の器（`QdrantBootstrapHostedService` の 1 行・発見の `BackgroundService` を 1 本）、取り込みのポート（既定実装つきのメソッド 2 本）、
  契約（`AttributeValueKeys` に索引を張るキーの集合を 1 つの情報源として足す）、検索の facet（索引が無いキーは空集合へ倒す）、
  試験（単体・実 Qdrant の統合試験）、文書（機能仕様書 FR-05・テスト仕様書・運用仕様書）、IADR（新 IADR-0502・IADR-0151／IADR-0497 への日付つき追記・索引）。
- **触らない**: facet の呼び方（ABAC フィルタ・件数を捨てる）、フィルタの組み立て（`BuildAttributeConditions`）、全文索引（`text`・`text_ngram`）の宣言、
  検索の readiness（`qdrant-fulltext-index` / `qdrant-cjk-ngram-index`）、`check-stack-ready.js` の門 G13、稼働中のクラスタ（何も実行しない）。

## 実測（2026-10-06。手元の Docker で `qdrant/qdrant:v1.18.1`〔配備と同じ。`deploy/docker-compose.yml:136`〕と v1.13.4〔Testcontainers 4.12.0 の既定〕。REST）

使い捨てのコレクションを 2 つ作った —— ベクトルのコレクション `vec_f`（`size: 4`）と、ベクトルの設定が空のコレクション `lex_f`（語彙索引と同じ形）。
両方へ同じ 3 点（`tags: ["人事","Policy"]・shared_with: ["bob"]・attributes: {department: "HR", confidentiality: "internal"}` ／
`tags: ["policy"]・attributes: {department: "hr", confidentiality: "public"}` ／ `attributes: {department: "sales", confidentiality: "confidential"}`）を書き、
索引を張る前と後で同じ問い合わせを投げた（スクリプトは scratch `m1760.sh`）。

| # | 問い | 張る前 | 張った後（`keyword`） | v1.13.4 |
| --- | --- | --- | --- | --- |
| m-1 | facet `tags` / `shared_with` / `attributes.department` | **3 つとも `No appropriate index for faceting: <key>`**（両コレクション） | 値と件数が返る（`HR`・`hr`・`sales` を別の値として返す） | 同じ |
| m-2 | facet のフィルタのキー（`attributes.owner` 等）にも索引が要るか | — | **要らない**（索引の無いキーでフィルタしても facet は通る。要るのは facet するキーだけ） | 同じ |
| m-3 | `Match.any` の大小文字（`attributes.department` に `HR`／`hr`／`Hr`） | 1・1・0 件（完全一致・大小文字を区別） | **1・1・0 件（同じ）** | 同じ |
| m-4 | リスト項目の `Match.any`（`tags` に `policy`／`Policy`） | 1・1 件 | **1・1 件（同じ）** | 同じ |
| m-5 | 否定条件（`must_not attributes.doc_scope == private-note`。キーの欠落は残る） | 5 点中 4 点 | **4 点（同じ）**（`attributes.doc_scope` に索引を張った後） | — |
| m-6 | 同じ索引をもう一度張る | — | `completed`（冪等） | 同じ |
| m-7 | 1 点も持たないキー（`attributes.nonexistent`）に張れるか | — | **張れる**（`points: 0`）。その facet は空集合 | 同じ |
| m-8 | 既に点があるコレクションへ後から張る | — | 既存の点も索引に入る（`payload_schema` の `points: 3`） | 同じ |
| m-9 | JSON パスとして不正なキー（`attributes.my key`・`attributes.`） | — | **張れない**（`Invalid json path`。`wait=false` でも同期に返る）。その facet も同じ理由で失敗する | — |
| m-10 | `wait=false` で張る | — | `acknowledged` で即時に返り、直後の facet は通った | — |

スクリプト（m-1〜m-8。m-9・m-10 と m-5 の `doc_scope` は同じ `j` 関数で追加に投げた）:

```bash
Q=localhost:16333
j(){ curl -s -X "$1" "$Q$2" -H 'content-type: application/json' ${3:+-d "$3"}; echo; }
for c in vec_f lex_f; do
  if [ $c = vec_f ]; then j PUT /collections/$c '{"vectors":{"size":4,"distance":"Cosine"}}' >/dev/null; V='"vector":[0.1,0.2,0.3,0.4],'; else j PUT /collections/$c '{"vectors":{}}' >/dev/null; V='"vector":{},'; fi
  j PUT "/collections/$c/points?wait=true" '{"points":[
   {"id":1,'"$V"'"payload":{"tags":["人事","Policy"],"shared_with":["bob"],"attributes":{"department":"HR","confidentiality":"internal"}}},
   {"id":2,'"$V"'"payload":{"tags":["policy"],"attributes":{"department":"hr","confidentiality":"public"}}},
   {"id":3,'"$V"'"payload":{"attributes":{"department":"sales","confidentiality":"confidential"}}}]}' >/dev/null
  echo "== $c: facet BEFORE index"
  for k in tags shared_with attributes.department; do echo -n "$k: "; j POST /collections/$c/facet '{"key":"'$k'"}'; done
  echo "== $c: filter BEFORE index (count)"
  for v in '["HR"]' '["hr"]' '["Hr"]'; do echo -n "dept $v: "; j POST /collections/$c/points/count '{"exact":true,"filter":{"must":[{"key":"attributes.department","match":{"any":'"$v"'}}]}}'; done
  for v in '["policy"]' '["Policy"]'; do echo -n "tags $v: "; j POST /collections/$c/points/count '{"exact":true,"filter":{"must":[{"key":"tags","match":{"any":'"$v"'}}]}}'; done
  echo -n "facet tags with unindexed filter: "; j POST /collections/$c/facet '{"key":"tags","filter":{"must":[{"key":"attributes.confidentiality","match":{"any":["internal"]}}]}}'
  for k in tags shared_with attributes.department attributes.confidentiality; do j PUT "/collections/$c/index?wait=true" '{"field_name":"'$k'","field_schema":"keyword"}' >/dev/null; done
  echo "== $c: facet AFTER index"
  for k in tags shared_with attributes.department; do echo -n "$k: "; j POST /collections/$c/facet '{"key":"'$k'"}'; done
  echo -n "facet tags with filter: "; j POST /collections/$c/facet '{"key":"tags","filter":{"must":[{"key":"attributes.confidentiality","match":{"any":["internal"]}}]}}'
  echo -n "facet dept with shared_with filter: "; j POST /collections/$c/facet '{"key":"attributes.department","filter":{"must":[{"key":"shared_with","match":{"any":["bob"]}}]}}'
  echo "== $c: filter AFTER index (count)"
  for v in '["HR"]' '["hr"]' '["Hr"]'; do echo -n "dept $v: "; j POST /collections/$c/points/count '{"exact":true,"filter":{"must":[{"key":"attributes.department","match":{"any":'"$v"'}}]}}'; done
  for v in '["policy"]' '["Policy"]'; do echo -n "tags $v: "; j POST /collections/$c/points/count '{"exact":true,"filter":{"must":[{"key":"tags","match":{"any":'"$v"'}}]}}'; done
  echo -n "must_not private-note (missing key) count: "; j POST /collections/$c/points/count '{"exact":true,"filter":{"must_not":[{"key":"attributes.doc_scope","match":{"value":"private-note"}}]}}'
  echo -n "idempotent re-create: "; j PUT "/collections/$c/index?wait=true" '{"field_name":"tags","field_schema":"keyword"}'
  echo -n "index on never-written key: "; j PUT "/collections/$c/index?wait=true" '{"field_name":"attributes.nonexistent","field_schema":"keyword"}'
  echo -n "facet on indexed-but-absent key: "; j POST /collections/$c/facet '{"key":"attributes.nonexistent"}'
  echo -n "payload_schema: "; curl -s $Q/collections/$c | python3 -c 'import sys,json;print(json.dumps(json.load(sys.stdin)["result"]["payload_schema"],ensure_ascii=False))'
done
```

判定:

- **facet が引くキーにキーワード索引が要る**（IADR-0497 §実測「参考」の再確認。語彙索引〔ベクトルの設定が空〕でも同じ）。
- **フィルタの意味は索引で変わらない**（m-3〜m-5。完全一致・大小文字の区別・リストの「いずれか一致」・否定条件の欠落の扱い）。
  したがって索引を張っても ABAC は過剰にも過少にも許可しない。索引はフィルタの経路の性能（全点走査の回避）にだけ効く。
- 不正なキーは索引にできない（m-9）。そのキーは facet もできないので、張れないことで失うものは無い。**書き込みを止める理由にはしない。**

## 設計（正は IADR-0502）

1. **張るキーの集合は契約の 1 か所から導く**: `AttributeValueKeys.KeywordIndexKeys(attributeKeys)` ＝
   集合値キー（`DocumentAttributeEncoding.SetValuedKeys` を `ToPayloadKey` に通したもの ＝ `shared_with`・`tags`）∪ 属性キーを `ToPayloadKey` に通したもの。
   **照会（facet）とフィルタが通る写像と同じ関数を通す**ので、片方だけ増える形にならない。
2. **いつ張るか**（3 つの時点。どれも冪等な `CreatePayloadIndex`）:
   - (i) **起動時**（`QdrantBootstrapHostedService`）: 集合値キー（`tags`・`shared_with`）を全コレクションへ無条件で張る（#1116 と同じ作法）。
   - (ii) **書き込み時**（チャンク・メタデータ点・語彙索引の 4 つの口）: 書く点の属性キーを集合へ通し、まだ張っていない（コレクション, キー）だけを張る。
     張ったものはプロセス内で覚える（2 回目以降は呼ばない）。`wait=false`（取り込みの期限に索引の構築を入れない）。
     **失敗は書き込みを止めない**（Warning を残す）。不正なキー（`InvalidArgument`）は覚えて再試行しない。それ以外は次の書き込みで再試行する。
   - (iii) **既存の点からのキーの発見**（起動後のバックグラウンド。`QdrantKeywordIndexDiscoveryHostedService`）: 全コレクションの点を `attributes` だけ読んで scroll し、
     現れた属性キーへ (ii) と同じ経路で張る。**既存の配備で、再起動後に一度も書かれていないキーにも索引が付く**。
3. **コレクション**: 取り込みが持つ全コレクション（`Embedding:Collections` の全モデル ＋ 語彙索引）。ティア A の束ねるコレクション（`knowledge_chunks_ruri_v3`）は
   `Embedding:Collections` に含まれる（`appsettings.json`・helm）。検索の主（`Qdrant:CollectionName`）も同じ集合に含まれる。
4. **パラメータ**: `keyword`。`is_tenant`・`on_disk` は既定（偽）のまま（属性は全問い合わせに付くテナントの区切りではない。規模は NFR-08 の数十万点で、メモリ上の索引で足りる）。
5. **検索側**: facet が「索引が無い」（`InvalidArgument` ＋ `No appropriate index for faceting`）で失敗したら、そのコレクションの値は空集合にする。
   **書き込み時に必ず張るので、索引が無いキー ＝ そのコレクションのどの点も持っていないキー**であり、空集合が正しい答えである
   （SC-01 の軸 `project` を 1 文書も持たない配備で、候補の照会全体が例外になるのを防ぐ）。他の失敗は従来どおり例外。
   検索は索引を張らない（索引の持ち主は取り込み。IADR-0318 と同じ分担）。
6. **readiness は足さない**（IADR-0502 決定 6）。

## 母集合（規則 9・10。2026-10-06 `0172d9b7` 時点）

### 規則 9（誤りの側の文字列で走査してから追随先を挙げる）

走査: `git grep -nE "No appropriate index|キーワード索引が無|キーワード索引|payload index の要否|facet の payload|索引の要否|keyword 索引|Keyword index|張るのは全文索引"`
（`src/ai-stock-trading`・`.ai-context/specs` を除く）。

| 当たり | 追随 |
| --- | --- |
| `.ai-context/adr/IADR-0151_scoped-attribute-value-facets.md:121`（§フォローアップ「payload index の要否は未検証」） | 日付つき追記（#1760 / IADR-0502 で実測・決定した） |
| `.ai-context/adr/IADR-0497_high-confidentiality-lexical-index-vectorless-collection.md:68・71〜72・185・193`（「参考」の行・「別 issue に切り出す」・残るもの・フォローアップ 4） | 193 行（フォローアップ 4）の直後へ日付つき追記 1 か所（本文は凍結記録なので書き換えない） |
| `QdrantFullTextIndexBootstrapTests.cs:81`（「キーワード型で張ると full-text Match が成立しない」） | 追随不要（`text` の索引の型の話。本件は別のキー） |

2 回目の走査（取り込みが張る索引を列挙している記述）: `git grep -nE "text, text_ngram|全文ペイロード索引（|起動時に.{0,20}索引"`。

| 当たり | 追随 |
| --- | --- |
| `QdrantBootstrapHostedService.cs` のログ文言「full-text payload indexes (text, text_ngram)」 | 追随（キーワード索引も張ると書く） |
| `docs/operations/operations.md` §検索が全件 0 件になる（全文ペイロード索引の再起動手順） | 追随（キーワード索引の欠落の行を足す） |
| `docs/functional/FR-05_abac-access-control.md` §権限内属性値の照会 | 追随（キーワード索引の節を足す） |
| `scripts/check-stack-ready.js` の門 G13（`text` / `text_ngram` の宣言を走査） | 追随不要（純関数 2 本・定数 2 つを変えない。キーワード索引は G13 の射程外。§残るもの） |

### 規則 10（この変更で新たに誤りになる自分の記述）

- 取り込みのポートの注記「索引を持たない試験用の実装に維持する索引が無い」→ 新しい 2 本も同じ既定実装（何もしない）にするので崩れない。
- `LexicalIndexStoreTests` の記録器は `Upsert` / `Delete` 以外の RPC を `NotSupportedException` で落とす → **書き込み時に索引を張るので落ちる**。器に `CreateFieldIndex` を足す（本変更で新たに誤りになる）。
- `QdrantFullTextIndexBootstrapTests` は `EnsureCollectionsAsync` の索引が `text` だけであることを固定する → `EnsureCollectionsAsync` を変えず、別メソッドにするので崩れない（IADR-0339 決定 2 と同じ理由）。
- `IngestionTimeouts` の最悪の所要時間（コレクション数 × 期限）→ 書き込み時の索引は `wait=false` で、同じキーには 2 度呼ばないので見積もりを変えない。

## 受け入れ基準 → 試験

| # | 受け入れ基準（#1760） | 試験 |
| --- | --- | --- |
| 1 | 新規・既存のどちらの配備でも、facet が引くキーにキーワード索引が付く | 単体: 起動時（集合値キー × 全コレクション）・書き込み時（4 つの口）・既存の点からの発見。統合: 実 Qdrant で 3 種のコレクションの facet が通る |
| 2 | 実 Qdrant で権限内属性値の照会が例外にならない | 統合（I-11）。陰性対照: 索引を張らないコレクションで facet が `No appropriate index for faceting` で失敗する（I-12）。単体: 索引が無いキーの facet は空集合 |
| 3 | 検索段の ABAC フィルタ（同じキー）も同じ索引を使える・意味が変わらない | §実測 m-3〜m-5。統合: 索引を張った後も大小文字の区別と許可・不許可が変わらない |
| 4 | 張るキーの集合が照会側の写像（`ToPayloadKey`）と 1 つの情報源から導かれる | 単体: `KeywordIndexKeys` が `ToPayloadKey` を通す・集合値キーが全部入る（変異試験） |

## 残るもの（受け入れたもの）

- 発見の走査（iii）は起動のたびに全点の `attributes` を読む（NFR-08 の数十万点で数分になり得る。バックグラウンドで、取り込みと検索は止めない）。
- 発見の走査が終わるまで（または失敗した間）、再起動後にまだ書かれていない既存のキーには索引が無い。その間の facet は空集合（設計 5）。
- 不正なキー（JSON パスにならない属性キー）には索引が付かない。facet もできない（従来どおり例外）。
- `check-stack-ready.js` の門 G13・検索の readiness はキーワード索引を見ない。

## 試験の対応（実装後）

| 試験 | クラス | 測るもの |
| --- | --- | --- |
| FR-02 T-34 | `QdrantKeywordIndexTests` | キーの集合（`KeywordIndexKeys`）が `ToPayloadKey` と集合値キーの語彙から導かれる |
| FR-02 T-35 | 同上 | 起動時に集合値キーを全コレクション（語彙索引を含む）へ keyword で張る。パラメータは既定 |
| FR-02 T-36 | 同上 | 4 つの書き込みの口が書く点の属性キーへ `wait=false` で張る。同じ（コレクション, キー）へは 1 回 |
| FR-02 T-37 | 同上 | 失敗で書き込みを止めない。不正なキーは再試行しない・一時的な失敗は再試行する・取り消しは上げる |
| FR-02 T-38 | 同上 | 既存の点からの発見（ページを辿る・`attributes` だけ読む） |
| FR-02 T-39 | `QdrantKeywordIndexHostedServiceTests` | ブートストラップがキーワード索引を張る。発見の器が呼び、失敗を捕まえる |
| FR-05 T-73 | `AttributeValuesMissingIndexTests` | 索引が無いときだけ空集合。不正なキー・不調は上げる |
| FR-02 I-11〜I-13 | `KeywordIndexQdrantTests`（実 Qdrant v1.18.1） | 3 種のコレクションで facet が通る・陰性対照・既存の配備・不正なキー・フィルタの意味 |

## 変異試験の結果（2026-10-06。スクリプト scratch `msp1760-mut/mut.py`。1 変異ずつ当て、該当プロジェクトの試験の全件を走らせ、戻す）

| # | 変異 | 結果 | 落とした試験 |
| --- | --- | --- | --- |
| M1 | キーの集合から集合値キーを外す | killed（4 件） | T-34・T-35・T-36 |
| M2 | 集合値キーから `shared_with` を落とす（`tags` だけ） | killed（3 件） | 同上 |
| M3 | キーを `ToPayloadKey` に通さない | killed（4 件） | T-34・T-36・T-38 |
| M4 | チャンクの口で張らない | killed（3 件） | T-36・T-37 |
| M5 | 語彙索引のチャンクの口で張らない | **初回 survived** → 試験を直して killed | 初回は同じ試験の語彙索引のメタデータ点が同じキーを持ち、記憶で覆い隠していた。口ごとに別のキーにした（T-36） |
| M20 | メタデータ点の口で張らない | killed | T-36 |
| M21 | 語彙索引のメタデータ点の口で張らない | killed | T-36 |
| M6 | `wait=true` で張る | killed | T-36 |
| M7 | 記憶を見ない（毎回張る） | killed（3 件） | T-36・T-37 |
| M8 | 不正なキーを覚えない | killed | T-37 |
| M9 | 一時的な失敗も覚える | killed | T-37 |
| M10 | 取り消しも握りつぶす | killed | T-37 |
| M11 | 索引の失敗で書き込みを止める | killed | T-37 |
| M12 | 発見で次のページを辿らない | killed | T-38 |
| M13 | 起動時に語彙索引へ張らない | killed | T-35 |
| M14 | `is_tenant` を立てる | killed | T-35 |
| M15 | ブートストラップがキーワード索引を張らない | killed | T-39 |
| M16 | 発見の器が発見を呼ばない | killed（2 件） | T-39 |
| M17 | 検索: 索引が無くても例外を上げる | killed | T-73 |
| M18 | 検索: `InvalidArgument` を全部空集合へ | killed（2 件） | T-73 |
| M19 | 検索: 文言だけで判定する | killed（2 件） | T-73 |
| I1 | チャンクの口で張らない（統合試験に対して） | killed | I-11 |
| I2 | 検索: 索引が無くても例外を上げる（統合） | killed | I-12 |
| I3 | 発見で張らない（統合） | killed | I-13 |

## 検証（2026-10-06。`origin/develop` `0172d9b7` 基点）

| 検査 | 結果 |
| --- | --- |
| `dotnet build src/knowledge/backend/backend.slnx` / `src/platform/backend/backend.slnx` | 0 エラー・0 警告 |
| `dotnet test src/knowledge/backend/backend.slnx --filter "Category!=Integration"` | 全件緑（Ingestion 150・Retrieval 548・他ユニットも緑） |
| 統合試験（手元の Docker・`Knowledge.IntegrationTests.Search`） | 21 件緑（新しい `KeywordIndexQdrantTests` 5 件を含む。skip ではなく実走。実 Qdrant v1.18.1） |
| `dotnet format --verify-no-changes`（knowledge・platform） | 差分なし |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 920 件緑 |
| `check-test-spec-coverage` | 初回は床の上げ忘れ（新しい 4 クラス）で赤 → `--update`（追加 4 対だけ） |
| check-trace-blocks / check-adr-numbering / gen-knowledge-graph --check / check-cross-repo-refs / check-plan-id-qualification / check-test-traceability / check-doc-updated / check-unit-dependencies / check-doc-links / check-commit-messages | すべて OK |
