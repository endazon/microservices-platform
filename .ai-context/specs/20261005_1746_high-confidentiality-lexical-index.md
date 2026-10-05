---
title: 高機密文書を埋め込まず、ベクトルを持たない専用のコレクション（語彙索引）に全文索引だけで載せ、検索は全文の系統だけで束ねる（#1746 段 S1）
type: spec
status: done
related_ids: [FR-02, FR-03, FR-05, FR-19, UC-01, UC-04, SC-02, ADR-0127, ADR-0092, ADR-0016, ADR-0017, ADR-0070, ADR-0057, ADR-0061, IADR-0497, IADR-0025, IADR-0085, IADR-0467, IADR-0422, IADR-0318, IADR-0339, IADR-0358]
author: claude
created: 2026-10-05
updated: 2026-10-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0127_high-confidentiality-lexical-only-and-claude-rerank.md（決定 1・2・4・7・フォローアップ 1・2）
  - planning:projects/microservices-platform/07_adr/ADR-0092_multi-collection-search-fusion-and-query-egress.md（決定 1・3。決定 2 の一部は ADR-0127 が改定）
  - planning:projects/microservices-platform/07_adr/ADR-0016_embedding-provider-voyage.md（§決定「高機密（ティア A）」の行は ADR-0127 が改定）
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md（越境マトリクス。値は不変）
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-02・FR-03 の 2026-10-05 例外）
issue: "#1746"
---

# 仕様書: 高機密文書の語彙索引（#1746 段 S1）

> 本仕様書は実装着手前に作成した（着手 2026-10-05）。判断の記録は **IADR-0497** に置く。計画は project-planning `c3ad458` を読んだ。

## 起点となる計画書（トレーサビリティ）

- 要求: **FR-02**（取り込み。2026-10-05 例外「高機密文書は埋め込みを作らず、全文索引（語彙索引）にだけ登録する」）・
  **FR-03**（横断検索。2026-10-05「高機密文書は意味検索の系統を持たない」）。ブランチ名は FR-03 を起点に取った（検索で見つかることが受け入れ基準の主語である）。
- 計画 ADR: **ADR-0127**（Accepted・2026-10-05）。決定 1（埋め込まず語彙索引だけ・置き場所は実測して IADR へ・意味の無いベクトルは採らない）、
  決定 2（キーワードとハイブリッドだけで RRF に入る・意味検索には現れない・ABAC は全系統）、決定 4（`restricted` と未指定・未知も同じ）。
  ADR-0092 決定 1・3（束ねて RRF・ABAC は束ね方と独立）は改まっていない。ADR-0017 は Superseded（ADR-0127）。
- 段割り（#1746）: **S1 = 本件**（語彙索引: 置き場所の実測と IADR・取り込み・検索で束ねる・IADR-0025 の追随）。S2 = Claude の再順位付け。S3 = nDCG@10 の差。S4 = 後始末。
- 前提: 計画 ADR のレンジを 0128 まで広げる PR #1750 は `origin/develop` に入った（`c4c1c460`。`5192a7a4` はマージの再試行で生じた空の重複で、本 PR の別コミットで CHANGELOG から除外する）。
- 採番: `origin/develop` `5192a7a4` の最大は IADR-0496。開いている PR は #1726（CHANGELOG の自動更新）だけで IADR を足さない。本件は **IADR-0497**。

## 実測: 語彙索引の置き場所（ADR-0127 決定 1。2026-10-05）

Qdrant の版: 配備は **v1.18.1**（`deploy/docker-compose.yml:136`・`deploy/local/infra/qdrant.yaml:18`）。Testcontainers.Qdrant 4.12.0 の既定は **v1.13.4**（`Testcontainers.Qdrant.dll` の文字列で確認）。
両方を `docker run` で起こし、REST（curl）と gRPC（`Qdrant.Client` 1.18.1。本番と同じクライアント）で当てた。

### REST（v1.18.1。スクリプトは下）

```bash
#!/bin/bash
# Qdrant measurement for #1746 S1. Usage: qd-measure-1746.sh <rest-port>
P=${1:-16333}; B=http://localhost:$P
r(){ echo "\$ $1 $2 ${3:+-d '$3'}"; curl -s -X $1 "$B$2" -H 'Content-Type: application/json' ${3:+-d "$3"}; echo; }
r GET /
echo "## (a) collection without vectors config"
r DELETE /collections/lex_a
r PUT /collections/lex_a '{}'
r GET /collections/lex_a
r PUT /collections/lex_a/index '{"field_name":"text","field_schema":{"type":"text","tokenizer":"multilingual","min_token_len":1,"max_token_len":40,"lowercase":true}}'
r PUT /collections/lex_a/index '{"field_name":"text_ngram","field_schema":{"type":"text","tokenizer":"prefix","min_token_len":1,"max_token_len":2,"lowercase":true}}'
r PUT '/collections/lex_a/points?wait=true' '{"points":[{"id":"00000000-0000-0000-0000-000000000001","vector":{},"payload":{"document_id":"d1","text":"ABAC policy for hr","text_ngram":"機密 密文 文書","attributes":{"confidentiality":"restricted"}}},{"id":"00000000-0000-0000-0000-000000000002","vector":{},"payload":{"document_id":"d2","text":"public note","text_ngram":"","attributes":{"confidentiality":"confidential"}}}]}'
r POST /collections/lex_a/points/scroll '{"filter":{"must":[{"key":"text","match":{"text":"abac"}},{"key":"attributes.confidentiality","match":{"any":["restricted"]}}]},"limit":10}'
r POST /collections/lex_a/points/scroll '{"filter":{"must":[{"key":"text_ngram","match":{"text":"密文"}}]},"limit":10}'
r POST /collections/lex_a/points/scroll '{"filter":{"must":[{"key":"text","match":{"text":"abac"}},{"key":"attributes.confidentiality","match":{"any":["public"]}}]},"limit":10}'
r POST /collections/lex_a/points/search '{"vector":[0.1,0.2],"limit":3}'
r POST /collections/lex_a/facet '{"key":"attributes.confidentiality","exact":true}'
r POST '/collections/lex_a/points/delete?wait=true' '{"filter":{"must":[{"key":"document_id","match":{"value":"d1"}}]}}'
r POST /collections/lex_a/points/count '{"exact":true}'
echo "## (a') point without the 'vector' field at all"
r PUT '/collections/lex_a/points?wait=true' '{"points":[{"id":"00000000-0000-0000-0000-000000000003","payload":{"document_id":"d3","text":"no vector key"}}]}'
echo "## (b) points without vectors in an existing vector collection"
r DELETE /collections/vec_b
r PUT /collections/vec_b '{"vectors":{"size":4,"distance":"Cosine"}}'
r PUT '/collections/vec_b/points?wait=true' '{"points":[{"id":"00000000-0000-0000-0000-000000000011","vector":{},"payload":{"text":"x"}}]}'
r PUT '/collections/vec_b/points?wait=true' '{"points":[{"id":"00000000-0000-0000-0000-000000000012","payload":{"text":"x"}}]}'
r PUT '/collections/vec_b/points?wait=true' '{"points":[{"id":"00000000-0000-0000-0000-000000000013","vector":[],"payload":{"text":"x"}}]}'
echo "## (b') named vector collection, point without the named vector (vectors are optional per point for named vectors)"
r DELETE /collections/vec_c
r PUT /collections/vec_c '{"vectors":{"dense":{"size":4,"distance":"Cosine"}}}'
r PUT '/collections/vec_c/points?wait=true' '{"points":[{"id":"00000000-0000-0000-0000-000000000021","vector":{},"payload":{"text":"x"}}]}'
r POST /collections/vec_c/points/count '{"exact":true}'
```

```text
$ GET / 
{"title":"qdrant - vector search engine","version":"1.18.1","commit":"e01c207f40a2fe01ed23a191957a76e224fe5726"}
## (a) collection without vectors config
$ DELETE /collections/lex_a 
{"result":false,"status":"ok","time":0.000115217}
$ PUT /collections/lex_a -d '{}'
{"result":true,"status":"ok","time":0.018086718}
$ GET /collections/lex_a 
{"result":{"status":"green","optimizer_status":"ok","indexed_vectors_count":0,"points_count":0,"segments_count":2,"confi …（省略。"vectors":{} を含む）
$ PUT /collections/lex_a/index -d '{"field_name":"text","field_schema":{"type":"text","tokenizer":"multilingual","min_token_len":1,"max_token_len":40,"lowercase":true}}'
{"result":{"operation_id":2,"status":"acknowledged"},"status":"ok","time":0.002137337}
$ PUT /collections/lex_a/index -d '{"field_name":"text_ngram","field_schema":{"type":"text","tokenizer":"prefix","min_token_len":1,"max_token_len":2,"lowercase":true}}'
{"result":{"operation_id":4,"status":"acknowledged"},"status":"ok","time":0.002129811}
$ PUT /collections/lex_a/points?wait=true -d '{"points":[{"id":"00000000-0000-0000-0000-000000000001","vector":{},"payload":{"document_id":"d1","text":"ABAC policy for hr","text_ngram":"機密 密文 文書","attributes":{"confidentiality":"restricted"}}},{"id":"00000000-0000-0000-0000-000000000002","vector":{},"payload":{"document_id":"d2","text":"public note","text_ngram":"","attributes":{"confidentiality":"confidential"}}}]}'
{"result":{"operation_id":5,"status":"completed"},"status":"ok","time":0.013209808}
$ POST /collections/lex_a/points/scroll -d '{"filter":{"must":[{"key":"text","match":{"text":"abac"}},{"key":"attributes.confidentiality","match":{"any":["restricted"]}}]},"limit":10}'
{"result":{"points":[{"id":"00000000-0000-0000-0000-000000000001","payload":{"document_id":"d1","text":"ABAC policy for hr","text_ngram":"機密 密文 文書","attributes":{"confidentiality":"restricted"}}}],"next_page_offset":null},"status":"ok","time":0.000980983}
$ POST /collections/lex_a/points/scroll -d '{"filter":{"must":[{"key":"text_ngram","match":{"text":"密文"}}]},"limit":10}'
{"result":{"points":[{"id":"00000000-0000-0000-0000-000000000001","payload":{"document_id":"d1","text":"ABAC policy for hr","text_ngram":"機密 密文 文書","attributes":{"confidentiality":"restricted"}}}],"next_page_offset":null},"status":"ok","time":0.000287756}
$ POST /collections/lex_a/points/scroll -d '{"filter":{"must":[{"key":"text","match":{"text":"abac"}},{"key":"attributes.confidentiality","match":{"any":["public"]}}]},"limit":10}'
{"result":{"points":[],"next_page_offset":null},"status":"ok","time":0.000260297}
$ POST /collections/lex_a/points/search -d '{"vector":[0.1,0.2],"limit":3}'
{"status":{"error":"Wrong input: Not existing vector name error: "},"time":0.000353314}
$ POST /collections/lex_a/facet -d '{"key":"attributes.confidentiality","exact":true}'
{"status":{"error":"Wrong input: No appropriate index for faceting: `attributes.confidentiality`. Please create one to facet on this field. Check https://qdrant.tech/documentation/concepts/indexing/#payload-index to see which payload schemas support Match conditions"},"time":0.00038154}
$ POST /collections/lex_a/points/delete?wait=true -d '{"filter":{"must":[{"key":"document_id","match":{"value":"d1"}}]}}'
{"result":{"operation_id":6,"status":"completed"},"status":"ok","time":0.00116262}
$ POST /collections/lex_a/points/count -d '{"exact":true}'
{"result":{"count":1},"status":"ok","time":0.000206123}
## (a') point without the 'vector' field at all
$ PUT /collections/lex_a/points?wait=true -d '{"points":[{"id":"00000000-0000-0000-0000-000000000003","payload":{"document_id":"d3","text":"no vector key"}}]}'
{"status":{"error":"Format error in JSON body: missing field `vector`"},"time":0.0}
## (b) points without vectors in an existing vector collection
$ DELETE /collections/vec_b 
{"result":false,"status":"ok","time":0.000091083}
$ PUT /collections/vec_b -d '{"vectors":{"size":4,"distance":"Cosine"}}'
{"result":true,"status":"ok","time":0.023494709}
$ PUT /collections/vec_b/points?wait=true -d '{"points":[{"id":"00000000-0000-0000-0000-000000000011","vector":{},"payload":{"text":"x"}}]}'
{"result":{"operation_id":1,"status":"completed"},"status":"ok","time":0.001042215}
$ PUT /collections/vec_b/points?wait=true -d '{"points":[{"id":"00000000-0000-0000-0000-000000000012","payload":{"text":"x"}}]}'
{"status":{"error":"Format error in JSON body: missing field `vector`"},"time":0.0}
$ PUT /collections/vec_b/points?wait=true -d '{"points":[{"id":"00000000-0000-0000-0000-000000000013","vector":[],"payload":{"text":"x"}}]}'
{"status":{"error":"Validation error in JSON body: [points[0].vector.vector: dense vector must not be empty]"},"time":0.0}
## (b') named vector collection, point without the named vector (vectors are optional per point for named vectors)
$ DELETE /collections/vec_c 
{"result":false,"status":"ok","time":0.000076009}
$ PUT /collections/vec_c -d '{"vectors":{"dense":{"size":4,"distance":"Cosine"}}}'
{"result":true,"status":"ok","time":0.023801747}
$ PUT /collections/vec_c/points?wait=true -d '{"points":[{"id":"00000000-0000-0000-0000-000000000021","vector":{},"payload":{"text":"x"}}]}'
{"result":{"operation_id":1,"status":"completed"},"status":"ok","time":0.001392755}
$ POST /collections/vec_c/points/count -d '{"exact":true}'
{"result":{"count":1},"status":"ok","time":0.000113883}
```

v1.13.4 の REST は同じ結果（違いは誤りの文言だけ: ベクトル検索は `Vectors are not configured in this collection`、`vector: []` は `Vector dimension error: expected dim: 4, got 0`）。

### gRPC（`Qdrant.Client` 1.18.1。scratch のコンソール 1 本）

```text
==== gRPC Qdrant.Client 1.18.1 against port 16334 (v1.18.1)
delete: FAIL QdrantException: Collection 'lex_grpc' could not be deleted
(a) CreateCollectionAsync(name, vectorsConfig: (VectorParamsMap)empty): OK created
(a) GetCollectionInfo.vectors: OK { "paramsMap": { } }
(a) text index: OK Completed
(a) upsert point Vectors=empty NamedVectors: OK Completed
(a) upsert point with Vectors unset: FAIL RpcException: Status(StatusCode="InvalidArgument", Detail="Expected some vectors")
(a) scroll full-text: OK d1
(a) search with vector: FAIL RpcException: Status(StatusCode="InvalidArgument", Detail="Wrong input: Not existing vector name error: ")
(a) count: OK 1
(a) delete by document_id: OK Completed
(a) count after delete: OK 0
(a) CreateCollectionAsync(name) with no vectors arg (null VectorParams): OK { "paramsMap": { } }
(b) create vector coll: OK ok
(b) upsert Vectors=empty NamedVectors into unnamed-vector coll: OK Completed
(b) upsert Vectors unset into unnamed-vector coll: FAIL RpcException: Status(StatusCode="InvalidArgument", Detail="Expected some vectors")
(b) upsert Vectors=float[0]: FAIL RpcException: Status(StatusCode="InvalidArgument", Detail="Wrong input: points[0].vector.vector: dense vector must not be empty")
(b) count: OK 1
(b) search: OK 0
==== gRPC Qdrant.Client 1.18.1 against port 17334 (v1.13.4)
delete: FAIL QdrantException: Collection 'lex_grpc' could not be deleted
(a) CreateCollectionAsync(name, vectorsConfig: (VectorParamsMap)empty): OK created
(a) GetCollectionInfo.vectors: OK { "paramsMap": { } }
(a) text index: OK Completed
(a) upsert point Vectors=empty NamedVectors: OK Completed
(a) upsert point with Vectors unset: FAIL RpcException: Status(StatusCode="InvalidArgument", Detail="Expected some vectors")
(a) scroll full-text: OK d1
(a) search with vector: FAIL RpcException: Status(StatusCode="InvalidArgument", Detail="Wrong input: Vectors are not configured in this collection")
(a) count: OK 1
(a) delete by document_id: OK Completed
(a) count after delete: OK 0
(a) CreateCollectionAsync(name) with no vectors arg (null VectorParams): OK { "paramsMap": { } }
(b) create vector coll: OK ok
(b) upsert Vectors=empty NamedVectors into unnamed-vector coll: OK Completed
(b) upsert Vectors unset into unnamed-vector coll: FAIL RpcException: Status(StatusCode="InvalidArgument", Detail="Expected some vectors")
(b) upsert Vectors=float[0]: FAIL RpcException: Status(StatusCode="InvalidArgument", Detail="Wrong input: Vector dimension error: expected dim: 4, got 0")
(b) count: OK 1
(b) search: OK 0
```

（先頭の `delete: FAIL` は、存在しないコレクションの削除でクライアントが例外を投げるだけで、測定の対象ではない。）

### facet（権限内属性値）—— 本件と独立の既存の欠陥

```text
$ POST /collections/vec_f/facet (vector collection, no keyword index)
{"status":{"error":"Wrong input: No appropriate index for faceting: `attributes.department`. Please create one to facet on this field. ..."},"time":0.000483999}
```

ベクトルのコレクションでもキーワード索引が無いと facet は動かない。IADR-0151 §結果 が「facet の payload index の要否は未検証」と残したものの実測である。
語彙索引を束ねても悪化しない（主の照会が先に落ちる）。**別 issue に切り出す**（本 PR の射程外）。

### 判定

(a) ベクトルを持たない専用のコレクション・(b) 既存のベクトルのコレクションへベクトル無しの点、の**どちらも両版で成立する**。
採用は **(a)**。理由（置き場所が埋め込みのモデルに依らない・意味検索に出ないことが構造で決まる・既存の束ねる経路がそのまま効く）は IADR-0497 §検討した選択肢。

## 決定の要約（正は IADR-0497）

1. 語彙索引 = ベクトルの設定が空のコレクション `knowledge_chunks_lexical`（`Qdrant:LexicalCollection`。取り込みと検索で同じキー・同じ既定名。無効化の口なし。ベクトルのコレクションと同名なら起動失敗）。
2. 取り込みは**埋め込みを呼ぶ前に**分ける。`public` / `internal` 以外（`ConfidentialityLevels.FromAttributes` が未知・未指定を `restricted` へ倒す）は語彙索引だけ。ゲートウェイ（`EmbeddingEgress` / `EmbeddingRouter`）は変えない。
3. ペイロードは `BuildChunkPayload` のまま。本文なしはメタデータ点（`has_body = false`）。
4. 削除・全文索引・`text_ngram` の後付けは語彙索引を含む全コレクション。削除の時間の上限の本数 +1。
5. 検索は語彙索引を `LexicalOnly` の追加コレクションとして常に束ねる（`NoQueryEmbedding`）。keyword / hybrid は全文の系統で入り、semantic は問い合わせない。ABAC は同じフィルタ。
6. S2 の接点（候補・越境の判定・`ai_input`・用途 `rerank`）を IADR-0497 決定 6 に残す。**S1 は再順位付けを実装しない。**

## 母集合（規則 9・10。2026-10-05 `5192a7a4` 時点）

### コードの母集合（誤りの側の語で引いた）

| 引いた語 | 走査 | 結果と扱い |
| --- | --- | --- |
| `: IIngestionVectorStore`（実装の全数） | `git grep -n "IIngestionVectorStore" -- src` | 7 実装（本番 1・試験 6: `DocumentUpdatedConsumerTests`・`PrivateNoteIndexProductionTests`・`IngestionTimeoutTests`・`EmbeddingBudgetDeadLetterPipelineTests`・`SharedIndexIngestionVectorStore`・`DocumentUpdatedFanOutTests`）。**ポートに既定実装を置かず**、全 7 つへ 2 つの口を足した（コンパイルが母集合を強制する） |
| `RemoveAll<QdrantClient>`（検索のホストを起こす器） | `git grep -n "RemoveAll<QdrantClient>"` | 3 つ（`TestWebApplicationFactory`・`GrpcKestrelFactory`・`IngestToSearchInProcessTests.RetrievalHost`）。合成点が Qdrant のクライアントを要するようになったので、3 つとも `FusedCollections` を差し替えた（前 2 つは `None`、後 1 つは別の索引を語彙索引として束ねる）。`FusedQueryEmbeddingTests` の器は本番の合成点を残す（`KeepProductionFusedCollections`） |
| `confidential` を渡して埋め込みの拒否を期待する試験 | `git grep -n "confidential" -- src/knowledge/backend/Services/IngestionService/Tests` | `DocumentUpdatedConsumerTests` の T-09・T-14 の 2 本。**`internal` の恒久的な拒否を測る形に改めた**（高機密文書はもう埋め込みを呼ばない） |
| `restricted` を既定に持つ文書の試験 | `git grep -n "restricted" -- src/knowledge/backend/Services/IngestionService/Tests src/knowledge/backend/Tests` | `PrivateNoteIndexProductionTests`（個人資料の既定が `restricted`）。語彙索引へ書かれても露出の門の主張は変わらないので、記録の器に語彙索引の口を足し、陽性の 1 本に「語彙索引へ書かれた」を足した |
| 削除の本数を前提にした値 | `git grep -n "DefaultCollectionCount\|CollectionCount: 1\|コレクション 2 本"` | 取り込み `IngestionTimeouts.DefaultCollectionCount`（2 → 3）と T-21、検索 `DocumentDeletedTimeouts.Default`（1 → 2）と配線の試験。導出値（削除の最悪 20 → 30 秒 / 10 → 20 秒）は計算し直し、受け口の実行期限（420 秒・60 秒）に収まる |

### 文書の母集合（「高機密は索引されない／セルフホスト固定」の誤りの側で引いた）

`git grep -n -E "索引されない|索引しない|索引スキップ|fail-closed.*(高機密|confidential)|(高機密|confidential).*(fail-closed|スキップ|索引されない)|ruri_v3" -- docs deploy scripts perf README.md src ':!src/ai-stock-trading' ':!*.cs'` → 30 行。

| 対象 | 扱い |
| --- | --- |
| `docs/operations/operations.md`（表の 2 行目・セルフホスト有効化の箇条・fail-closed の箇条・再索引手順・障害対応の表） | **直した**（語彙索引の行を足し、Ruri の行を opt-in に改め、既存の高機密文書は `DocumentUpdated` の再発行が要ることを再索引手順へ足した） |
| `docs/security/security.md`（高機密文書本文の外部埋め込みの行） | **直した**（埋め込みを呼ばないことと、RAG の文脈で `restricted` 等の本文が Claude へ送られ得る受け入れたリスクを足した） |
| `docs/functional/FR-02_ingestion.md`・`FR-03_hybrid-search.md` | **直した**（§高機密文書・§高機密文書は語彙索引から全文の系統だけで現れる。FR-03 の「既定は空」の記述に改まった旨の注記） |
| `deploy/docker-compose.yml`（llm-gateway の SelfHosted の注記・embedding プロファイルの注記）・`helm/.../templates/embedding.yaml`・`values.yaml` の `embedding:` 見出し | **注記を足した**（有効にしても高機密文書は埋め込まれない）。Ruri の配備物そのものの後始末は S4 |
| `deploy/helm/.../templates/deployment.yaml:276`（「既定では非描画＝高機密は fail-closed」） | **除外**（llmgateway の SelfHosted 配線の描画条件の注記で、ゲートウェイ側の fail-closed は事実として残る。S4 で見直す） |
| `scripts/k8s-local-up.sh:534`・`scripts/verify-oidc-edge-flow.sh:68,599,882` | **除外**（「埋め込みが得られなければ取り込みはそのチャンクを索引しない」は `public` / `internal` について今も真。seed 文書は `public` であり、検証の判定は変わらない） |
| `deploy/helm/.../values.yaml:1210`（決定的ローカル埋め込みの注記） | **除外**（同上。`public` の文書の話） |
| `docs/tests/UC-04_datasource-registration-sync.md` T-31（埋め込みが恒久失敗なら索引しない） | **除外**（`public` / `internal` の恒久失敗について今も真） |
| `docs/api/openapi.yaml:47`（`/embed` の説明「高機密は fail-closed」） | **除外**（ゲートウェイの `/embed` の振る舞いは変えていない。生成物でもある） |
| `perf/ndcg/README.md`・`operations.md` の Ruri の A/B の表・`ruri_v3` の各行 | **除外**（Ruri の opt-in 配備と測定の手順は残る。S3・S4 で見直す） |
| `.ai-context/specs/` の該当行 | **除外**（凍結記録） |

### 自分の変更で新たに誤りになる記述（規則 10）

| 記述 | 扱い |
| --- | --- |
| IADR-0467「束ねる追加コレクションの既定は空・そのとき従来と同一」 | **IADR-0467 に日付つき追記**（本文は残置） |
| `docs/functional/FR-03_hybrid-search.md` §束ねる「既定は空」・受け入れ基準「追加が空の既定では同一」 | **注記を足した**（次節で語彙索引を常に束ねると書いた） |
| `docs/tests/FR-03_hybrid-search.md` T-75「追加コレクションが空（既定）」 | 「（既定）」を外した（試験そのものは空の構成を測っており、正しい） |
| IADR-0025 決定 3「取り込み側は索引をスキップする」・フォローアップ (a)(c) | **IADR-0025 に日付つき追記** |
| IADR-0085「有効化まで高機密は索引されない」 | **IADR-0085 に日付つき追記**（配備物は opt-in のまま残る） |
| `IngestionTimeouts.DefaultCollectionCount` のコメント「モデル別コレクションの数」 | 直した（＋語彙索引 1 本） |
| `DocumentDeletedTimeouts` の「最悪は既定で 10 秒」 | 直した（20 秒） |

### ADR-0017 の引用（#580 の規則。Superseded by ADR-0127）

`git grep -n -P "(?<!I)ADR-0017(?!\d)" -- . ':!src/ai-stock-trading'` → 131 行（`.ai-context/specs/` を含む）。除外: **`.ai-context/specs/`**（凍結）、
**`AST/ADR-0017`・`planning:projects/ai-stock-trading/...ADR-0017`**（AST の計画 ADR。別名前空間）、**IADR-0093**（`ADR-0017 サービス間認証・エッジ` と書いており、MSP の ADR-0017 の題とも合わない。誤引用の疑いで、本件の追随とは別）。
残りは**本 PR の別コミット**で、コード・設定のコメントと `docs/` の散文へ `ADR-0017（Superseded by ADR-0127・注記は #1746）` を、frontmatter と trace ブロックの ID リストへ後継 `ADR-0127` を項目として併記する（数と除外の内訳はそのコミットの節に書く）。
**IADR の本文は書き換えない**（凍結記録。IADR-0025・IADR-0085 は日付つき追記で扱った）。

## 受け入れ基準 → 試験

| # | 受け入れ基準（#1746 段 S1・ADR-0127） | 試験 |
| --- | --- | --- |
| 1 | 高機密（`confidential`・`restricted`・未指定・未知）のチャンクは**埋め込みを呼ばず**語彙索引へ書かれる | FR-02 T-23（7 通り）・T-32（9 通り）・I-08 |
| 2 | 語彙索引の点に全文・2-gram・ABAC の全ペイロード・`shared_with` が載る | FR-02 T-24・T-30・I-09 |
| 3 | 本文の無い高機密文書はメタデータ点（`has_body = false`）で語彙索引へ | FR-02 T-26・T-30・I-09（削除の試験がメタデータ点で書く） |
| 4 | `public` / `internal` は従来どおり埋め込まれる。一時障害は従来どおり再試行、恒久的な拒否は従来どおりスキップ | FR-02 T-25・T-09・T-11・T-13・T-14（既存） |
| 5 | 高機密文書にベクトルを作らない | FR-02 T-28・T-29・I-09（点のベクトルが空・ベクトル検索は拒まれる） |
| 6 | 文書単位の削除（取り込みの全コレクション・検索の削除の購読）が語彙索引を含む | FR-02 T-27・T-31・FR-03 T-97・I-09 |
| 7 | 全文索引・`text_ngram` の後付けが語彙索引を含む | FR-02 T-28（`QdrantCjkNgramIndexTests` の 2 本を含む） |
| 8 | 意味検索のモードに語彙索引は現れない。keyword / hybrid は RRF の全文の系統で現れる | FR-03 T-92・T-93・T-94・I-08・I-10 |
| 9 | 語彙索引のクエリは埋めない（ゲートウェイを呼ばない） | FR-03 T-95・T-98 |
| 10 | ABAC は語彙索引の系統にも掛かり、権限外は現れない | FR-03 T-96・I-09 |
| 11 | 配備の配線（helm / compose）が安全な既定で両サービスに同じ値を渡す。`helm template` が描画できる | FR-03 T-99（`k8s-local-up.test.js` の 2 本）・下の検証 |
| 12 | 本番の合成点が語彙索引を束ねる | FR-03 T-99（T-Q-06） |

## 変異試験の結果（2026-10-05。スクリプト scratch `mut-1746.py`。1 変異ずつ当て、取り込み／検索の単体試験の全件を走らせ、戻す）

| # | 枝 | 変異 | 結果 | 落とした試験 |
| --- | --- | --- | --- | --- |
| M1 | 分け方 | `confidential` も埋め込み可にする | **killed**（5 件赤） | T-23（`confidential`・`CONFIDENTIAL`）・T-32・T-26・T-27 |
| M2 | 分け方 | 未知・未指定を埋め込み可にする（deny-list へ反転） | **killed**（9 件赤） | T-23（空・未知・前後空白・属性なし）・T-32 |
| M3 | 取り込み | 語彙索引への分岐を外す（埋め込みへ進む） | **killed**（10 件赤） | T-23・T-24・T-27・個人資料の陽性 |
| M4 | 語彙索引への書き込み | 書き先を最初のベクトルのコレクションにする | **killed** | T-30 |
| M5 | 語彙索引の点 | 零ベクトルを入れる | **killed**（3 件赤） | T-29・T-30 |
| M6 | 全コレクションからの削除 | 語彙索引を外す | **killed** | T-31 |
| M7 | ブートストラップ | 語彙索引を次元つきで作る | **killed** | T-28 |
| M8 | 本文なしの高機密 | メタデータ点を埋め込みへ回す | **killed** | T-26 |
| M9 | クエリを埋めない | 語彙索引のクエリも客体で埋める | **killed** | T-95 |
| M10 | ベクトル検索を引かない（hybrid） | `!collection.LexicalOnly` を外す | **survived**（想定どおり） | — 二重の守りの片方。M9 の守りが空ベクトルを渡すので、引く条件 `vector.Length > 0` が偽のまま。**M9+M10 は killed**（T-95） |
| M11 | ABAC | 語彙索引の全文の系統にフィルタを渡さない | **killed**（2 件赤） | T-96（実際に絞る索引で、許さないスコープで現れる。2 件） |
| M12 | 意味検索から外す | 語彙索引を意味検索の経路選択に数える | **killed** | T-94（生スコアの経路でなくなる） |
| M13 | 意味検索から外す | 束ねる経路で語彙索引も引く（`Where` を外す） | **survived**（想定どおり） | — 同上（M9 の守りが空ベクトルを渡す）。**M9+M13 は killed**（T-95 の意味検索で束ねる経路の 1 本。本変異を見て足した） |
| M14 | 警告 | 語彙索引でも縮退の警告を出す | **killed** | T-93 |
| M15 | 合成点 | `Program.cs` が語彙索引を束ねない | **killed** | T-99（T-Q-06） |
| M16 | 削除の購読 | 追加コレクション（語彙索引）を回らない | **killed**（3 件赤） | T-97・T-F-17 ほか（削除の購読の試験） |

16 本中 14 本 killed。生き残った 2 本はいずれも**二重の守りの片方**で、もう片方（クエリを埋めない）を同時に外した組（M9+M10・M9+M13）は killed。
実 Qdrant の統合試験（I-09・I-10）は変異の対象にしていない（手元の Docker で緑を確かめた）。

## 残るもの（受け入れたもの・後段へ回すもの）

- 🔴 RAG 回答の文脈に語彙索引の文書が入り、`restricted` と未指定・未知の本文が Claude（ZDR）へ送られ得る。ADR-0127 決定 4 の受け入れたリスク（追加統制は無い）。
- 個人資料（既定 `restricted`）は露出のトグルのどれかが ON なら語彙索引へ載る（従来は既定構成で 1 点も索引されていなかった）。横断検索に出るのは `search_exposure` が ON のときだけ。
- 既に取り込まれた高機密文書は `DocumentUpdated` の再発行まで語彙索引に入らない（運用仕様書の再索引手順）。
- 検索の readiness は語彙索引の全文索引を見ない（Ruri のコレクションと同じ）。
- 権限内属性値の facet は実 Qdrant でキーワード索引が無く例外になる（既存の欠陥。§実測）。
- 統合試験（`LexicalIndexQdrantTests`）は `Category=Integration` で PR の CI では走らない（`integration.yml` が回収する）。本 PR では手元の Docker で実走した（§検証）。

## ADR-0017 の引用の追随（本 PR の 3 つ目のコミット）

走査: `git grep -n -P "(?<![I/\w-])ADR-0017(?!\d|（Superseded)" -- . ':!src/ai-stock-trading' ':!.ai-context/specs'`（AST の行は除く）。

- **注記した（28 ファイル・コメント／散文 47 行）**: `src/platform/backend/Services/LlmGateway/`（本体 10・試験 5）・`src/platform/backend/Shared/Platform.Shared.Contracts/`（`EmbedDto.cs`・`embedding.proto`。コメントだけ）・
  `deploy/`（compose・helm の 4 ファイル）・`scripts/measure-search-ndcg.js`・`scripts/README.md`・`perf/ndcg/README.md`。書式は `ADR-0017（Superseded by ADR-0127・注記は #1746）`（1 行に 1 か所）。
- **ID リストへ後継を併記した**: IADR-0313・IADR-0397・IADR-0422 の `related_ids`、`docs/api/east-west-grpc.md` の trace ブロック（いずれも `updated:` を前進）。
  IADR-0025・IADR-0085・IADR-0467・`docs/functional/FR-03_hybrid-search.md`・`docs/operations/operations.md` は 2 つ目のコミットで併記済み。`docs/how-to/plan-id-range-history-annex.md` は既に `ADR-0127` を持つ。
- **残した（除外）**:
  - IADR の本文・`plan_refs`（IADR-0025・0085・0313・0397・0422・0467 の散文）—— 凍結記録。0025・0085・0467 は日付つき追記で扱った。
  - IADR-0093 —— `ADR-0017 サービス間認証・エッジ` と書いており、MSP の ADR-0017（Ruri）の題と合わない。誤引用の疑いで本件の追随とは別（S4 で扱う）。
  - `.ai-context/adr/README.md` の IADR-0422 の行 —— 索引のタイトルセルは本体 `title:` と字を共有する規則（12 字以上）があり、注記を足すと本体と離れる。本体の `related_ids` に併記した。
  - `docs/api/openapi.yaml:47` —— `/embed` の説明文。`docs/` の表示テキストへ計画 ID を足さない規則に当たる（既存の 1 行。trace ブロックを持たない生成物の形であり、S4 で見直す）。
  - `.ai-context/specs/` —— 凍結。

## 検証（2026-10-05。`origin/develop` `5192a7a4` 基点）

| 検査 | 結果 |
| --- | --- |
| `dotnet build src/knowledge/backend/backend.slnx` | 0 エラー・警告 1 件（`IngestToSearchQdrantTests.cs` の `QdrantBuilder()` の CS0618。develop に既存。新しい統合試験は版を引数で渡して出さない） |
| `dotnet build src/platform/backend/backend.slnx` | 0 エラー・0 警告 |
| `dotnet test src/knowledge/backend/backend.slnx --filter "Category!=Integration"` | 全件緑（Ingestion 136・Retrieval 472・他ユニットも緑） |
| `dotnet test LlmGateway.Tests`（コメントだけの変更の確認） | 333 件緑 |
| 統合試験（手元の Docker・`Knowledge.IntegrationTests.Search` と `DocumentUpdatedFanOutTests`） | 16 件緑（実 Qdrant v1.18.1 の `LexicalIndexQdrantTests` 5 件を含む。skip ではなく実走） |
| `dotnet format --verify-no-changes`（knowledge・platform） | 差分なし |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 918 件緑（初回は `check-test-spec-coverage` の床の上げ忘れと、注記の試験名の誤りで赤 → 直した） |
| `k8s-local-up.test.js`・helm の 2 本 | 緑（語彙索引の 2 本を含む） |
| `helm template`（既定・`embedding.enabled=true`）・`helm lint` | 描画できる。既定で ingestion・retrieval の 2 つにだけ `Qdrant__LexicalCollection` が増える（他の差分なし）。`lexicalIndex.collection` を空にすると描画が失敗する |
| check-trace-blocks / check-doc-updated / check-test-traceability / check-commit-messages / gen-knowledge-graph --check / check-cross-repo-refs / check-plan-id-qualification / check-unit-dependencies / check-doc-links / check-adr-numbering | すべて OK |
| gitleaks（`origin/develop..HEAD`） | no leaks found |
