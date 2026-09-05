---
title: 検索側が読むコレクションに点と全文索引が在ることを門にする（読み書き先の乖離を静かに通さない）
type: spec
status: draft
related_ids: [FR-02, FR-03, NFR-09, ADR-0009, ADR-0016, IADR-0025, IADR-0284, IADR-0313, IADR-0315, IADR-0318, IADR-0339, IADR-0369, IADR-0377, IADR-0382]
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0009_vector-db-qdrant.md
  - planning:projects/microservices-platform/07_adr/ADR-0016_embedding-model-routing.md
---

# 仕様書: issue #1215 — 検索が読むコレクションの門

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-02（取り込み・索引）, FR-03（検索）／非機能: NFR-09
- ユースケース（UC）: UC-01（検索）／画面（SC）: SC-01
- 関連 ADR: ADR-0009（ベクトル DB = Qdrant）, ADR-0016（埋め込みのルーティング）
- 実装 ADR: IADR-0025（モデル別コレクション）, IADR-0313（決定的ローカル埋め込み）,
  IADR-0315（Qdrant サーバ版をクライアントへ揃える）, IADR-0318 / IADR-0339（全文索引 `text` / `text_ngram`）,
  IADR-0369（門 G9〜G11）, IADR-0377（門 G12）, IADR-0382（本作業の門 G13）

## 目的・背景

issue #1215 は稼働 dev クラスタで「検索が全件 0 件」になる 3 つの原因（埋め込み GW 不調・**読み書き先
コレクションの不一致**・全文ペイロード索引の欠落）を記録している。**issue の記述は 2026-09-03 時点であり、
その後 #1088（PR #1227）でクラスタが PERSIST 既定・develop 最新イメージで立て直されている。**
本作業は issue の記述を前提に置かず、**まず現状を陽性・陰性の対で測り直した**（結果は次節）。

本作業の本体は受け入れ基準 2、すなわち **同型の乖離を静かに通さない門**である。症状は配備状態に依存して
再発しうるのに対し、門はリポジトリに残る。

## 現状の実測（2026-09-05・稼働 k3s。issue の記述の転記ではない）

### 測り 1: 検索側が読むコレクション名と、点が実在するコレクション名

```console
$ kubectl -n microservices-platform get deploy retrieval-service ingestion-service llmgateway-service -o json | (env 抽出)
### retrieval-service   | k3d-local/microservices-platform/retrieval-service:latest
    Qdrant__CollectionName = knowledge_chunks_deterministic_v1
### ingestion-service   | k3d-local/microservices-platform/ingestion-service:latest
    Embedding__Collections__2__Name = knowledge_chunks_deterministic_v1
    Embedding__Collections__2__VectorSize = 1024
### llmgateway-service  | k3d-local/microservices-platform/llm-gateway:latest
    Embedding__Routing__Endpoints__2__Enabled = true
```

→ **読み書き先は一致している**（`knowledge_chunks_deterministic_v1`）。埋め込みは
`LOCALEMBED=1` 相当（`embedding.deterministicLocal.enabled=true`）の決定的ローカルで、
3 サービスが同時に配線されている（IADR-0313 が要求する形）。

### 測り 2: そのコレクションの点数と `payload_schema`

```console
$ (使い捨て pod 経由) GET http://qdrant.platform-infra:6333/collections/knowledge_chunks_deterministic_v1
"points_count":3,
"payload_schema":{
  "text":      {"data_type":"text","params":{"tokenizer":"multilingual","min_token_len":1,"max_token_len":40,"lowercase":true},"points":3},
  "text_ngram":{"data_type":"text","params":{"tokenizer":"prefix",      "min_token_len":1,"max_token_len":2, "lowercase":true},"points":3}}

$ 同 /collections/knowledge_chunks_voyage_3_5   → "points_count":0（索引は在る）   ← 陰性側の対照
$ 同 /collections/knowledge_chunks_ruri_v3      → "points_count":0（索引は在る）   ← 陰性側の対照
```

→ **原因 2・3 は解消している。** 点は検索側が読むコレクションに在り、`text` / `text_ngram` の
両系統の全文ペイロード索引が張られている（パラメータもアプリの宣言と一致する）。

### 測り 3: 検索が実際に当たるか（陽性・陰性の対）

`SEARCH_HITS=1 SEARCH_SEEDED=1 bash scripts/verify-oidc-edge-flow.sh` → **PASS 26 / FAIL 0（段 19/19）**。

- 陽性: seed 文書がヒット（3 件・合言葉 `msp-searchseed-tanpopo`）／全文だけで 1 件（語順を替えたクエリ）／
  日本語の語だけで 1 件（`text_ngram` の系統）
- 陰性: 索引に無い語 `msp-absent-zzzznotexistword` は 0 件／在らない日本語の語は 0 件／
  属性を持たない `poc-operator` の検索は 0 件（deny-by-default）

→ **受け入れ基準 1 は現状で満たしている。** よって本作業は**索引の張り直し・backfill を行わない**
（冪等な後追いは `QdrantBootstrapHostedService` / `QdrantCjkNgramBackfillHostedService` が起動時に既に持つ）。

## 対象範囲

- 対象: `scripts/check-stack-ready.js` へ門 **G13** を足す（判定は純関数として切り出す）。
  `scripts/scripts.repo.test.js` に陽性・陰性の対と**変異試験**を置く。実装 ADR（IADR-0382）と索引、
  `docs/operations/operations.md` の追随。
- 対象外: **索引テキストの内容**（`bodyAbsent` ↔ `hasBody` の語彙統一は #1253 / #1254 の射程。交差させない）。
  埋め込みプロバイダの選択そのもの（ADR-0016 / IADR-0313 が確定済み）。
  `verify-qdrant-fulltext-index.sh`（使い捨てコレクションで**索引の挙動**を測る器であり、
  稼働コレクションの状態は測らない。役割が違うので統合しない）。

## 母集合（規則 9・10 —— 誤りの側の語で走査した結果と除外理由）

走査は追跡下のファイルに対し `git grep` で行った（`src/ai-stock-trading` は submodule ＝別リポジトリなので
除外。`docs/` `.ai-context/` `CHANGELOG.md` は記録であり配線ではないので除外。テストは配線ではないが、
固定している不変条件を壊さないため確認だけ行った）。

**(1) 読み書き先コレクションを決めている全箇所**

| 箇所 | 役割 |
| --- | --- |
| `src/knowledge/backend/Services/RetrievalService/appsettings.json` | 検索側の既定 `Qdrant:CollectionName`（`knowledge_chunks_voyage_3_5`） |
| `src/knowledge/backend/Services/RetrievalService/appsettings.Development.json` | ローカル実行時の上書き（`knowledge_chunks`） |
| `src/knowledge/backend/Services/RetrievalService/Infrastructure/ExternalServices/QdrantVectorStore.cs` | 解決順（`CollectionName` → `Collection` → 既定 `knowledge_chunks`） |
| `src/knowledge/backend/Services/IngestionService/appsettings.json` | 取り込み側の既定コレクション 2 件（voyage / ruri） |
| `src/knowledge/backend/Services/IngestionService/Domain/Ports/EmbeddingCollectionsOptions.cs` | `Embedding:Collections` の束縛 |
| `src/platform/backend/Services/LlmGateway/appsettings.json` | ルーティング表（モデル → コレクション。deterministic-local は index 2） |
| `deploy/helm/microservices-platform/templates/deployment.yaml` | `Qdrant__CollectionName` / `Embedding__Collections__2__*` / `Embedding__Routing__Endpoints__2__Enabled` の注入 |
| `deploy/helm/microservices-platform/values.yaml` | `embedding.deterministicLocal.collection` / `.dimensions` |
| `scripts/k8s-local-up.sh` | `LOCALEMBED=1` → `--set embedding.deterministicLocal.enabled=true` |

**(2) 埋め込みプロバイダの分岐**: `scripts/k8s-local-up.sh`（`LOCALEMBED`）、
`deploy/helm/.../deployment.yaml`（`$det` ブロック。3 サービス同時配線）、
`values.yaml`（`embedding.enabled` / `embedding.deterministicLocal.enabled`）、
LlmGateway の `appsettings.json`（`Embedding:Routing:Endpoints` の 3 件と Priority）。
`.github/workflows/integration-stack.yml` は呼び出し側（値を決めていない）。

**(3) 全文ペイロード索引を決めている箇所**:
`QdrantIngestionVectorStore.BuildFullTextIndexParams()`（`text` / multilingual / 1..40 / lowercase）と
`BuildCjkNgramIndexParams()`（`text_ngram` / prefix / 1..2 / lowercase）が**単一情報源**。
キーは `QdrantIngestionVectorStore.FullTextKey`（`text`）と
`Knowledge.Contracts.Indexing.CjkBigramPayload.PayloadKey`（`text_ngram`）。
→ **門はこの 2 関数と 2 定数を走査して期待値を作る。ここへ書き写さない**（G7 の locale と同じ姿勢）。

**除外して確かめたもの**: `RetrievalService` の readiness（`QdrantFullTextIndexHealthCheck` /
`QdrantCjkNgramIndexHealthCheck`）は**索引の有無しか見ず、点の在り処を見ない**。
かつ readiness 本文は外から読めないことがある（`verify-oidc-edge-flow.sh` 段 19 が実測で
「読めなかった」に落ちている）。**門を app の readiness に委譲できない理由がここに在る。**

## 設計（門 G13）

### 判定（すべて fail-closed。純関数 `evaluateSearchCollection` に切り出す）

1. **(a) 検索側が読むコレクション名を決められること。** 稼働 Deployment の env `Qdrant__CollectionName`
   を走査（サービス名は書かない）し、無ければ `RetrievalService/appsettings.json` の
   `Qdrant:CollectionName` を読む。**どちらからも決められなければ失敗**（既定値へ落とさない）。
   複数の Deployment が別々の値を持っていれば失敗（どれが検索側か決まらない）。
2. **(b) Qdrant のコレクション一覧が 0 件なら失敗。** 走査が壊れている（0 件を緑にしない。G2 と同じ作法）。
3. **(c) (a) のコレクションが一覧に無ければ失敗。** 「索引はされているが検索は在らない先を見る」形。
4. **(d) そのコレクションに `text` と `text_ngram` の全文ペイロード索引が在り、パラメータが
   アプリの宣言（走査して得た値）と一致すること。** 索引が無いとき Qdrant v1.18.1 は例外を投げず
   部分文字列の全走査へ落ちる（IADR-0318）ので、**件数では検出できない**。
5. **(e) 🔴 本体 —— 読み書き先の乖離。** 走査したコレクションのうち**どれかに点が在るのに、
   検索側が読むコレクションの点が 0 なら失敗**（＝#1215 原因 2 そのもの）。
   どこにも点が無ければ notice（まだ何も取り込んでいないだけであり、それを赤にすると
   `SEARCHSEED` 無しで立てた素のスタックが必ず赤になる ＝ 意味の無い赤を量産する）。
   ただし **`SEARCHSEED=1` を明示した実行では、検索側の点 0 を失敗にする**（G10 の `PERSIST` と同じ
   「宣言された期待に対して測る」作法）。

### 収集（外部依存・門の外側）

Qdrant は `platform-infra` に居り、**pod に curl も wget も無い**（実測）。エッジにも出ていない。
そこで G6 と同じく**使い捨ての busybox pod** を `platform-infra` に立て、
`http://qdrant.platform-infra:6333/collections` と `/collections/<name>` を GET して JSON を読む。

- 🔴 `--rm --attach` は**完了が速いと出力を取り落とす**（実測。空文字が返り「コレクションが無い」と
  読み違える）。**pod を立て、終端まで待ち、`kubectl logs` で読み、`delete` する**形にする。
- 🔴 **稼働 Pod は 1 つも再起動しない。** 読み取りは GET だけで、Qdrant へ書き込みを一切発行しない。

## 受け入れ基準（issue #1215）

- [x] 1. Given 稼働クラスタ / When seed 文書を 1 件取り込む / Then 検索で当たる（陽性）・存在しない語は 0 件（陰性）
      → 上の「測り 3」。**現状で充足**（PASS 26 / FAIL 0）。
- [ ] 2. Given `check-stack-ready.js` / When 読み書き先が食い違う / Then 赤になる（変異試験）
      → 本作業。G13 (e) の陰性対照 ＋ 判定を外すと陰性が落ちる変異試験。

## テスト方針

- `node scripts/check-stack-ready.js --self-test`: G13 の (a)〜(e) を陽性・陰性の対で固定する。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（実体は `scripts.repo.test.js`）:
  同じ対に加えて**変異試験**を 1 本置く —— (e) の判定を「点の在り処を見ない」形へ差し替えた
  `check-stack-ready.js` をコンパイルして走らせ、**陰性対照が落ちなくなる**ことを見る。
- 走査（アプリ源泉からの期待値の取り出し）が壊れたら**期待値が空になって静かに緑**にならないよう、
  「読めなかった」を失敗として扱うことも対で固定する。

## 未決事項・申し送り

- 稼働クラスタの `LOCALEMBED` は**使い捨てスタック向け**である。Voyage を使う運用へ戻すときは
  鍵の供給と `embedding.*` の一致を改めて確かめること（門 G13 は「読み書き先が一致していること」しか
  言わず、**どちらの向きが正しいか**は言わない）。
- G13 は Qdrant の REST（6333）を使う。アプリは gRPC（6334）を使う（IADR-0315）。
  **版が食い違えば REST と gRPC で見えるものが変わりうる**が、本門はサーバ側の 1 つの状態を読むだけで、
  版の突合は行わない（G11 がイメージ参照の一致を見ている）。
