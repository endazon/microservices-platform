---
title: IADR-0284 検索の観測は既存の本文直接受け入れ経路を seed に使い、判定を「入口」と「命中」の 2 段に分ける
type: impl-adr
status: Proposed
related_ids: [FR-02, FR-03, FR-05, FR-21, UC-01, UC-03, SC-01, SC-02, ADR-0014, ADR-0015, ADR-0016, IADR-0133, IADR-0252, IADR-0255, IADR-0256, IADR-0264]
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0016_embedding-model-routing.md
  - planning:projects/microservices-platform/07_adr/ADR-0015_object-storage.md
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
---

# IADR-0284: 検索の観測は既存の本文経路を seed に使い、判定を 2 段に分ける

- 状態: Proposed
- 日付: 2026-08-28
- 決定者: claude（#992 案 1 は利用者計画で選定済み。本 IADR はその具体形を決める）

## 起点・関連

- #992「統合スタックで『検索が実際に効くこと』を観測可能にする」の**案 1**（選定済み）
- 前提となる穴: [[IADR-0255]]（`POST /bff/search` は「200 ＋ 空」を 3 つの失敗と区別できない）
- 先例: [[IADR-0133]]（ABAC の dev 初期投入。宣言的 JSON ＋ 管理 API ＋ 冪等）/
  [[IADR-0252]]（「200 ＋ 空リスト」を PASS にしない・正と負の対照を対で置く）
- 本文の置き場: [[IADR-0264]]（FR-21。本文はオブジェクトストレージ、DB は `MarkdownUri` だけ）
- 埋め込みの縮退: [[IADR-0256]]（空ベクトルを後段へ渡さない）

## コンテキスト

#992 は「検索が壊れている」と「該当が無い」が CI で区別できないことを問題にしている。
その決定的な理由として本文は**索引に何も入らない**ことを挙げ、
「BFF 経由で作った文書は `MarkdownUri` を持たない（`CreateDocumentRequest` に項目が無い）」と書いていた。

### 実測すると、前提は半分だけ正しかった

| 主張 | 実測 |
| --- | --- |
| `CreateDocumentRequest` が本文を受けない | **DocumentService については誤り。** `DocumentEndpoints.cs` の `CreateDocumentRequest` は末尾に `string? Body = null` を持ち、本文を格納して `MarkdownUri` を立てる（FR-21・[[IADR-0264]]） |
| BFF 経由の文書は本文を持たない | **正しい。** `Knowledge.Bff.Endpoints` の `DocumentCreateRequest` は 5 項目で `Body` を持たず、転送時に落ちる |
| 文書の初期投入経路が無い | **正しい。** `k8s-local-up.sh` が投入するのは ABAC ポリシーだけ |

さらに**issue が触れていない 2 つの事実**を見つけた。

1. 🔴 **DocumentService にオブジェクトストレージが配線されていない。**
   `values.yaml` の `services.document` に `objectStorage: true` が無く、`NullObjectStorageClient` へ縮退する。
   縮退実装は**決定的な URI を返すだけで本文を永続化しない**。
   `MarkdownUri` は立つが、取り込み側が本文を読めない。**FR-21 は配備のどの環境でも本文を落としている。**
2. 🔴 **埋め込みが無いと索引に 1 チャンクも入らない。**
   問い側（`HybridSearchService`）は空ベクトルなら全文側だけで続ける（[[IADR-0256]]）が、
   索引側（`DocumentUpdatedConsumer`）は `Embedded=false` で `continue` するか例外を投げ、
   **`UpsertChunkAsync` に到達しない**。統合スタックには `Embedding__Voyage__ApiKey` が無い。

## 検討した選択肢

1. **既存 FR-21 経路（`POST /documents` の `Body`）を seed に使う（採用）**
2. `CreateDocumentRequest` へ `MarkdownUri` を足し、seed が URI を直接渡す
3. BFF の `DocumentCreateRequest` へ `Body` を足し、seed も検証も BFF 経由で行う
4. 判定を「全文側でヒットする」ことへ倒し、埋め込みを前提にしない

## 決定

**決定 1: seed は既存の FR-21 本文直接受け入れ経路を使う。新しい欄を作らない。**

選択肢 2 は [[IADR-0264]] が既に退けている ——「新しい欄を作ると取り込み
（`MarkdownUri` の有無で起動する）の分岐が 2 本になる」。**同じ理由がそのまま生きている。**
選択肢 3（BFF に `Body`）は `docs/api/openapi.yaml` と orval 生成物を巻き込み、
**seed のためだけに公開契約を広げる**ことになる。seed は使い捨てスタックの初期投入であり、
[[IADR-0133]] の ABAC 投入が管理 API を直接叩くのと同じ層で済む。

**決定 2: 投入器は [[IADR-0133]] と同型にする** —— 宣言的 JSON（`deploy/local/search-seed/`）を
単一情報源とし、**API 経由**（直 DB 書き込みをしない）・**冪等**・**既定オフの opt-in**（`SEARCHSEED=1`）。
資格情報の解決（realm ファイルからパスワードと client_secret を引く）は
`seed-abac-policies.js` の関数を再利用する —— **値を 2 か所に写すと #933 / #984 の drift がまた起きる。**

**決定 3: 判定を「入口」と「命中」の 2 つの opt-in に分ける。**

| フラグ | 測るもの |
| --- | --- |
| `SEARCH_SEEDED=1` | seed 文書が一覧に見え **`markdownUri` を持つ**（取り込みの早期 return を通過する形）／属性を持たない利用者には見えない（負の対照） |
| `SEARCH_HITS=1` | seed 文書の語で検索して**実際にヒットする**（`SEARCH_SEEDED` を含意する） |

**分ける理由は達成条件が違うことである。** 前者は今日の統合スタックで緑にできるが、
後者は埋め込みの供給（#992 案 2）が要る。1 つのフラグに混ぜると、
**後者が原理的に落ちるせいで前者まで CI から降ろすことになる。**

**決定 4: 越境判定（`EmbeddingEgress` / `EmbeddingRouter`）を 1 バイトも触らない。
判定を「全文側」へ倒さない。**

「埋め込みが無くても全文側で当たるのではないか」は**索引に点が在るときだけ**成り立つ。
索引側が fail-closed で止まっているので、全文側にも点が無い（コンテキスト 2）。
**倒す先が無い。** 機密区分 × ティアの既定値は「CI だから」で開けるものではない。

**決定 5: `services.document` に `objectStorage: true` を足す。**
本番でも正しい（ADR-0015・FR-21）。seed の前提であると同時に、既存欠陥の是正である。

**決定 6: CI には `SEARCH_SEEDED=1` だけを載せる。`SEARCH_HITS=1` は案 2 の裁定まで載せない。**

**「走っていない検査は正しさを保証しない」ことは承知のうえでの保留である。**
今日載せれば毎晩落ち、`report-failure` が issue を起こし続け、**他の退行がその中に埋もれる。**
保留していることは本 IADR と作業仕様書に明記し、**沈黙で先送りしない。**

## 理由

- **決定 1・2 は「既に在るものを使う」側に倒している。** #992 が求めるのは索引可能な文書であって、
  新しい投入 API ではない。実測しないまま issue 本文の「項目が無い」を信じていれば、
  **既に在る経路の隣にもう 1 本作るところだった。**
- **決定 3 の分割は [[IADR-0252]] の系である。** あちらは「正の対照と負の対照を対で置く」ことで
  「直っていても壊れていても同じ緑」を潰した。ここでは**測れるものと測れないものを分ける**ことで、
  「測れないものに引きずられて何も測らない」を潰す。
- **決定 4 を破る誘惑は具体的だった** —— ティアA（セルフホスト）の stub を有効化すれば
  `confidential` 文書は索引できる。**しかし検索は `knowledge_chunks_voyage_3_5` しか読まない**
  （`RetrievalService/appsettings.json`）ので、ティアA のコレクション（`..._ruri_v3`）へ入れても見えない。
  **「安全側に見える回避策」が目的も達しない**ことを実測で確かめてから捨てた。

## 結果

- **良い影響**:
  - 「索引可能な文書を投入する経路」が存在するようになり、案 2 が決まればその日に `SEARCH_HITS=1` を
    CI へ載せられる（seed も判定も既に在る）。
  - `SEARCH_SEEDED=1` により、**今日から**「取り込みの入口条件が満たされているか」が CI で分かる。
    [[IADR-0255]] 時点では応答の形までしか見られなかった。
  - FR-21 の本文が実際に永続化されるようになった（決定 5）。
- **悪い影響 / トレードオフ**:
  - **opt-in フラグが 3 つ（`ABAC_POSITIVE` / `SEARCH_SEEDED` / `SEARCH_HITS`）になった。**
    `verify-oidc-edge-flow.sh` の段数は組み合わせで変わるため、`TOTAL` を加算式にした。
    固定値のままなら**組み合わせのたびに門が誤発火する**。
  - **seed は使い捨てスタック専用である。** `SEARCHSEED=1` は文書を作る（副作用）。
    残しておきたいクラスタに対して立ててはならない。
  - **`confidentiality=public` の seed は、鍵の無いスタックで DLQ を生む** ——
    ティアB が選ばれ、`Retryable=true` の縮退が取り込みの例外になりリトライされるためである。
    使い捨てスタックでは無害だが、**「取り込みが静かに成功した」わけではない**ことを記録しておく。
- **フォローアップ**:
  1. **案 2（埋め込みの供給）の裁定**。候補は「ティアB の宛先だけをクラスタ内 stub へ向ける」
     （越境マトリクスを触らずに済む唯一の形）。裁定が出たら `SEARCH_HITS=1` を CI へ載せる。
  2. **案 3（`POST /bff/search` の縮退の区別）**は別 issue（#992 のコメントが推す分割）。
  3. **BFF の `DocumentCreateRequest` に `Body` を通すか**は SC-05 の画面要件が出たときに決める。

## 検証

- 変異試験 7 件（seed 不在 / `markdownUri` null / 検索 0 件 / 別文書のみ / 全開放 / 段の削除 /
  `TOTAL` の加算落ち）をスタブ HTTP サーバで実測。**すべて EXIT=1 で検出。**
- 基準（変異なし）5 モードで門が誤発火しないことを確認。
- 単体: `scripts/scripts.repo.test.js`（投入器の純粋関数・判定の結線）/
  `scripts/k8s-local-up.test.js`（`SEARCHSEED` の opt-in トークン。既定オフで不在・単独検出力あり）。
- **実クラスタでの実走は CI に委ねる**（この作業環境に docker / k3s が無い）。
