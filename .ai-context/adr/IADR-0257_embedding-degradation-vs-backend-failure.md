---
title: IADR-0257 検索は「後段の設計上の縮退」だけ続行し、「後段の故障」は 500 のまま上げる
type: impl-adr
status: Accepted
related_ids:
  - FR-02
  - FR-03
  - FR-05
  - SC-01
  - UC-01
  - ADR-0016
  - ADR-0023
  - IADR-0014
  - IADR-0089
  - IADR-0249
  - IADR-0252
  - IADR-0255
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0016_embedding-model-and-collections.md
  - planning:projects/microservices-platform/07_adr/ADR-0023_mesh-and-ports.md
---

# IADR-0257 検索の縮退と故障の切り分け（#995）

- 状態: Accepted
- 日付: 2026-08-23
- 決定者: claude（実装担当）

## 起点・関連

- 関連する計画書 ID: FR-02 / FR-03 / FR-05 / UC-01 / SC-01、ADR-0016（埋め込みの越境判定・
  モデル別コレクション）、ADR-0023（メッシュとポート規約）
- 関連する実装仕様書: `.ai-context/specs/20260823_issue-995_bff-search-500.md`
- 起票: `#995`（integration-stack が develop で赤）。観測: run 32584777623 / job 97059924871

## コンテキストと課題

`integration-stack` の `verify-oidc-edge-flow.sh` 段 **[11/17]「認証ありで検索を叩く」**だけが
2 回連続で落ちた。`POST /bff/search`（認証あり）が **200 ではなく本文なしの 500** を返す。
他 16 段（認証・ABAC 正常系・文書 CRUD）はすべて PASS で、**壊れているのは検索の後段だけ**である。

BFF は後段の非 2xx を透過する（`SearchBffEndpoints.cs`）ので、500 の出所は RetrievalService である。
そして RetrievalService の `/search` には例外ハンドラが無い（`SearchEndpoints.cs`）ため、
**未処理例外がそのまま本文なしの 500 になる**。

原因は **2 つの欠陥が直列に並んでいた**ことである。

### 欠陥 A: 宛先ホストが k8s に存在しない

`RetrievalService.Api/appsettings.json` の `Services:LlmGateway` は `http://llm-gateway:8080`。
これは **compose のサービス名**であり、**k8s の Service 名は `llmgateway-service`** である
（chart の `templates/service.yaml` が `{{ $name }}-service` を作る × values キー `llmgateway`）。
同名の ExternalName エイリアスも無い。→ 名前解決失敗 → `HttpRequestException` → 500。

同じ宛先を持つ 4 呼び出し元のうち、**values.yaml で上書きしていたのは aianalysis の 1 件だけ**で、
そこには「llm-gateway の Service 名は llmgateway-service である点に注意」というコメントまであった。
**注意書きは書かれたが、Retrieval / Ingestion / Conversion へは追随していなかった。**

`check-bff-downstreams.js` は原理的にこれを見ない。**IADR-0249「検出しないこと」が
「ポート以外のドリフト（ホスト名の誤り・スキーム）。見るのはポートだけである」と明記している。**
ここはポートが 8080 で正しく、**ホスト名だけが誤っている**。

### 欠陥 B: ゲートウェイの fail-safe（空ベクトル）を、そのまま Qdrant へ渡していた

欠陥 A を直しても段 11 は 500 のままである。

`/embed` は**必ず 200 を返す**設計である（`EmbeddingEndpoints.cs`）。送信拒否（fail-closed）・
次元不整合・呼び出し失敗のいずれも `Vector: [], Embedded: false` の 200 で返し、
**呼び出し側が `Embedded` を見て降りる**のが ADR-0016 の契約である。
IngestionService の `DocumentUpdatedConsumer` は実際にそうしている。

RetrievalService の `LlmGatewayEmbeddingService` だけが **`Embedded` を読まず** `result?.Vector ?? []`
を返し、`HybridSearchService` がその **0 次元ベクトルを `IVectorStore.SearchAsync` へ渡していた**。
Qdrant は次元 1024 のコレクションへ 0 次元のクエリを受けて `RpcException` を返す。
`QdrantVectorStore.SearchAsync` は `KeywordSearchAsync` と違い `RpcException` を捕まえていない。

統合スタックでは**この経路が必ず通る**。`Embedding__Voyage__ApiKey` を配線しているのは
`docker-compose.yml` だけで、**helm 側（`values.yaml` / `values-local.yaml`）はどこにも配線していない**。
キー未設定の `VoyageEmbeddingProvider` は必ず例外を投げ、ゲートウェイは必ず空ベクトルを返す。

🔴 **`InMemoryVectorStore.SearchAsync` は `queryVector` をまったく参照しない**ため、
単体・結合テストは 0 次元でも 1536 次元でも同じ結果を返して緑のままだった。
[[IADR-0014]] が記録した「テストは緑・本番は壊れている」と同型である。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A** | `SearchEndpoints` に catch-all を置き、例外を **200 ＋ 空**へ潰す | ❌ 後段が死んでいても検索が緑になる。段 11 が警戒している「直っていても壊れていても同じ緑」そのもの |
| **B** | `QdrantVectorStore.SearchAsync` でも `RpcException` を捕まえ、`KeywordSearchAsync` と対称にする | △ 500 は消えるが、**壊れた原因（0 次元ベクトルを渡していること）を残したまま症状を隠す** |
| **C（採用）** | **空ベクトルを後段へ渡さない**（縮退として扱い、系統を落として続行）。**到達不能・非 2xx・`RpcException` は 500 のまま**上げる | ✅ 設計上の縮退と本当の故障を分けられる。故障は CI が赤で教える |

## 決定 1: 宛先ホストを是正する（欠陥 A）

`values.yaml` の `services.retrieval` / `services.ingestion` / `services.conversion` に
`Services__LlmGateway: http://llmgateway-service:8080` を追加する。

**本件に必要なのは retrieval だけだが、同じ軸で引いた同じ欠陥を 3 件とも直す。**
Ingestion / Conversion の壊れ方は**静か**である —— 埋め込みに失敗しても消費側は再試行/DLQ に回すだけで、
利用者からは「索引されない」としか見えない。**compose は正しいので触らない。**

## 決定 2: ゲートウェイの「埋め込めなかった」を、意味検索の系統の不在として読む（欠陥 B）

1. `LlmGatewayEmbeddingService`（Retrieval）は **`Embedded` を明示的に読む**。
   `false` なら空ベクトルを返す。`result?.Vector ?? []` という**契約への暗黙依存をやめる**。
2. `HybridSearchService` は**空ベクトルを後段へ渡さない**。
   - hybrid: ベクトル系統を**呼ばず**、全文検索だけで続行する。
     `QdrantVectorStore.KeywordSearchAsync` が「全文が使えなければベクトルのみ」へ降りるのと**対称**である。
   - semantic: 使える系統が無いので 0 件（HTTP 200）。**全文へ振り替えない**
     —— 利用者が選んだモードを実装が勝手に変えない。
   - いずれも `LogWarning` を出す。**`SearchResponse` は縮退の有無を持たない**ので、
     「200 なのに意味検索が効いていない」を後から知る手掛かりはログしかない。

## 決定 3: 🔴 「設計上の縮退」は続行、「本当の故障」は 500 のまま上げる

**200 ＋ 空へ一律に潰さない。**

- **続行するのは、後段が「使えない」と 200 で明示的に答えたときだけ**である
  （`/embed` の `Embedded=false`）。これは **ADR-0016 の fail-closed が正常に働いた状態**であり、
  故障ではない。機密区分による送信拒否も同じ経路を通るので、ここで 500 にすると
  **越境ポリシーが働くたびに検索が落ちる**ことになる。
- **後段へ到達できない・後段が非 2xx やエラーを返す場合は 500 のまま**にする
  （ゲートウェイ不達の `HttpRequestException`、Qdrant の `RpcException`）。
  **CI が赤くなるのが正しい状態**である。決定 1 の是正が将来ドリフトで戻れば、また赤くなる。

したがって `QdrantVectorStore.SearchAsync` と `ListAttributeValuesAsync` の `RpcException` は
**捕まえない**（選択肢 B を採らない）。`KeywordSearchAsync` の既存の捕捉は
「全文インデックス未作成」という**別の既知の縮退**に対するものであり、そのまま残す。

## 決定 4: ホスト名軸の機械検査は作らない（1 回目として記録に留める）

CLAUDE.md「検査器・規約の追加は**同型の事故が 2 回起きたら**を条件とする（1 回目は記録に留める）」。
ポート軸は `#342` → `#958` の 2 回で `check-bff-downstreams.js` を得たが、
**ホスト名軸は本件が 1 回目**である。

**次に同型（宛先ホストが実在しない）が起きたら、`check-bff-downstreams.js` を次の 2 点で拡張する。**

1. **typed client を読めるようにする。** Retrieval / Ingestion / Conversion は
   `AddHttpClient<TInterface, TImpl>(c => ...)` で登録しており、現行パーサ
   （`AddHttpClient("Name", c => ...)` しか読まない）の母集合に**最初から入らない**。
2. **設定層として `appsettings.json` を見る。** Retrieval / Ingestion のコード既定は `:5007` だが、
   `appsettings.json` が `:8080` へ上書きしている。**コード既定だけを見ると実効値を誤る。**
3. 判定は「ホストが chart の Service 名（`values.yaml` の `services:` キー ＋ `-service`）または
   実在する ExternalName エイリアス・非 chart Service（`wiki-js` / `minio` / infra）に一致すること」。

**この 3 点を書き残す理由は、次に起きたときに同じ調査をやり直さないためである。**

## 結果

- 良い影響
  - 段 11 が緑になる。`POST /bff/search` は埋め込みが無い環境でも 200 ＋ 契約どおりの形を返す。
  - ADR-0016 の fail-closed（越境拒否）が、検索経路でも**設計どおり縮退**として働くようになる。
    従前は「機密区分で送信を拒否したら検索が 500 になる」状態だった。
  - Ingestion / Conversion の埋め込み経路が k8s で初めて到達可能になる。
- 悪い影響・トレードオフ
  - 🔴 **段 11 が緑になっても「検索が効いている」ことにはならない。** 索引は空のままであり
    （埋め込みが無いので索引もされない）、**件数は 0 である**。
    観測できるのは「認証が要ること」「応答が契約どおりの形であること」までである（IADR-0255 が明記した範囲）。
    正の側の観測は `#992` の射程であり、**本 IADR はそれを解決しない。**
  - 意味検索が黙って落ちている状態が、応答からは区別できないまま残る。手掛かりはログだけである。
    区別を応答へ載せるなら `SearchResponse` の契約変更が要り、それは `#992` の裁定に属する。
- フォローアップ
  - `#992`（統合スタックで検索が実際に効くことを観測可能にする。方針裁定待ち）
  - ホスト名軸の機械検査（決定 4。**同型 2 回目が起きたときに着手する**）

## 関連

- Supersedes: なし
- Superseded by: なし
