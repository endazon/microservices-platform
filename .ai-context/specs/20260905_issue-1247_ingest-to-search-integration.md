---
title: 作業仕様書 — 取り込み → 索引 → 検索ヒットの段間結合テストを、Docker の有無で分けた 2 層で置く
type: spec
status: done
related_ids:
  - FR-02
  - FR-03
  - FR-21
  - NFR-21
  - ADR-0009
  - ADR-0016
  - ADR-0035
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - "02_requirements/01_requirements.md FR-02（取り込み: parse → chunk → embed → index）"
  - "02_requirements/01_requirements.md FR-03（検索: ベクトル＋全文のハイブリッド）"
  - "07_adr/ADR-0009（ベクトル DB はポートで抽象化し、製品を差し替え可能にする）"
  - "07_adr/ADR-0016（機密区分でモデル別コレクションへルーティングする）"
related_adrs:
  - IADR-0390
  - IADR-0368
  - IADR-0232
  - IADR-0358
  - IADR-0014
issue: "#1247"
---

# 作業仕様書: 取り込み → 検索の段間結合テスト（#1247）

## 起点

#447 の「退行防止（テスト必須）」2 番目が「取り込み → 検索ヒットまでの結合テスト」を求めている。
**in-repo の自動テストとして 1 本も無い。** 稼働 dev クラスタで検索が全件 0 件になった事故（#1215）は、
段のどこで切れているかを in-repo で測る手段が無いために発見が遅れた。

## 自分で引いた母集合（実測 2026-09-05・基点 `3d5f8c99`）

`git rev-parse --is-shallow-repository` → `false`（履歴は打ち切られていない）。

### 陰性: 段間を測るテストは無い

```console
$ grep -rnE '"/search|/search/hybrid|IHybridSearchService|SearchAsync' \
    src/knowledge/backend/Tests/Knowledge.IntegrationTests
（0 件）
```

### 陽性対照（同じ走査が生きていること）

```console
$ grep -rnE '"/search|/search/hybrid|IHybridSearchService|SearchAsync' \
    src/knowledge/backend/Services/RetrievalService/Tests | wc -l
98
```

**98 件は「検索の中」だけを測っている。** 索引へ入った点が検索に当たるかは、
どのテストも取り込み側の書き込みを経由していない。

### 取り込み → 検索の全段（母集合）

| 段 | 実体 | 既存テストの担当 |
| --- | --- | --- |
| 1. 事象の配送 | `DocumentUpdated`（Wolverine / RabbitMQ） | `DocumentUpdatedFanOutTests`（実ブローカ・Docker 必須） |
| 2. 本文取得 | `IDocumentContentReader` | IngestionService の単体 |
| 3. 分割 | `MarkdownChunkingService` | IngestionService の単体 |
| 4. 埋め込み | `IEmbeddingService`（機密区分ルーティング） | IngestionService の単体 |
| 5. **索引への書き込み** | `IIngestionVectorStore` → `QdrantIngestionVectorStore` | 単体のみ（実 Qdrant 無し） |
| 6. **索引からの読み出し** | `IVectorStore` → `QdrantVectorStore` | 単体のみ（実 Qdrant 無し） |
| 7. 検索の合成 | `IHybridSearchService` / `POST /search` | RetrievalService の 98 件 |

🔴 **5 と 6 の間に「同じ索引を指しているか」を測るテストが無い。**
書き込み側と読み出し側は**別サービス・別クラス**であり、ペイロード鍵（`text` / `document_id` /
`attributes.*`）は型ではなく文字列で一致させている（`QdrantIngestionVectorStore.FullTextKey` の
コメントが「サービスを跨ぐため型では束ねられない」と自白している）。**これは [[IADR-0014]] が
実際に踏んだ「テストは緑・本番は空」の型そのものである。**

## 決めたこと（issue が「先に決めよ」と指定した 2 択）

**(A) を採る。ただし (A) の実体は 2 層である。** 論拠は [[IADR-0390]]。

- **層 1（Docker 不要・PR で必ず走る）**: 段 2〜7 を 1 本のプロセス内で通す。
  索引は `InMemoryVectorStore`（RetrievalService の**本番コードに実在する**ポート実装）を
  **書き手と読み手で 1 インスタンス共有**する。
- **層 2（Docker 必須・`Category=Integration`）**: 段 5・6 を**実 Qdrant** で通す。
  層 1 が原理的に測れない「2 つの Qdrant アダプタのペイロード表現が一致しているか」を測る。

🔴 **層 1 だけでは足りず、層 2 だけでも足りない。** 層 1 は自分で書いたフェイクが自分と一致するので
アダプタ間の乖離を検知できない。層 2 は本環境（containerd / nerdctl）では**走らない**ので、
それだけだと「書いたが skip で緑」に戻る。**両方置くことが決定の内容である。**

## 設計

### 層 1: `IngestToSearchInProcessTests`（`Category` を付けない ＝ PR で走る）

```
DocumentUpdated
  → DocumentUpdatedConsumer.Handle（本番の実装そのまま）
      ├ IDocumentContentReader  : スタブ（storage:// を取れないため）
      ├ IChunkingService        : 本番の MarkdownChunkingService
      ├ IEmbeddingService       : スタブ（LLM ゲートウェイを立てないため。8 次元）
      └ IIngestionVectorStore   : SharedIndexIngestionVectorStore ─┐
                                                                   │ 同一インスタンス
  → POST /search（RetrievalService の本番ホスト）                    │
      └ IVectorStore            : InMemoryVectorStore ─────────────┘
```

`SharedIndexIngestionVectorStore` は**書き込み側ポートを読み出し側ポートの語彙へ写す唯一の場所**で、
`UpsertChunkAsync` / `UpsertMetadataPointAsync` / `DeleteByDocumentFromAllAsync` を
`InMemoryVectorStore.UpsertAsync` / `DeleteByDocumentAsync` へ落とす。
**`HasBody` の写像を持つ**（メタデータ点は `HasBody: false`）—— ここを取り違えると
[[IADR-0358]] 決定 3 が閉じた「メタデータが本文として返る」が再発する。

トレイトは `TestKind=Integration` のみ（[[IADR-0368]] 決定 1）。
🔴 **`Category=Integration` を付けない** —— 付けると `ci.yml` の `--filter "Category!=Integration"` で
**PR から消える**（[[IADR-0368]] 決定 3 が名指しした事故）。

### 層 2: `IngestToSearchQdrantTests`（`Category=Integration`）

`Testcontainers.Qdrant`（版は `Directory.Packages.props` に既出）で実 Qdrant を起こし、
**本番の `QdrantIngestionVectorStore` で書き、本番の `QdrantVectorStore` で読む。**
埋め込みだけスタブ（決定的な 8 次元）にする。

🔴 **本環境では実行できない**（Docker デーモンが無い）。**PR 時点では未実行である**と PR 本文へ書く。
初回の実走は develop マージ後の `integration.yml` である。

### NFR「登録から 15 分以内に検索へ反映」をどこで測るか

**本テストでは測らない。** 層 1 は同期呼び出しであり、層 2 はコンテナ 1 台の待ち時間である。
どちらも**配送遅延・再試行・バックログを含まない**ので、ここで緑になっても NFR の担保にならない。
測る場所は稼働スタック（`integration-stack` / #1215 の門）であり、**現時点では測っていない。**
[[IADR-0390]] 決定 4 に「測っていない」として明記する（満たしていると読ませない）。

## 受け入れ基準

1. (A)/(B) の選択と理由が [[IADR-0390]] にある。
2. `DocumentUpdated` → 取り込み → 索引 → 検索ヒットを 1 本のテストが通す（層 1・層 2 の両方）。
3. NFR の計測箇所を明記する（上節）。
4. 🔴 **0 件で緑にならない**: ヒット件数 `>= 1` を主張し、**陰性対照**（当たらない語では 0 件）と
   対で置く。**変異試験**（索引への書き込みを外すと落ちる）の出力を PR に載せる。
5. `dotnet test src/knowledge/backend/backend.slnx` が緑。
6. **層 1 は skip されずに実走する**（実行件数で示す。skip を「在る」と数えない）。

## 交差の確認

- **#1215**（稼働 dev クラスタの検索 0 件）: 触るのは `scripts/check-stack-ready.js` と配備側であり、
  本作業は `Tests/` と `RetrievalService/TestMarker.cs` のみ。**重ならない。**
- **#1219**（`integration-stack` が赤い）: workflow を見るが本作業はファイルを触らない。
- **#1252-1254**（索引テキストの内容・`bodyAbsent` ↔ `hasBody`）: 本作業は表現を**固定しない**——
  層 1 は「本文の語で当たること」だけを主張し、索引テキストの中身は主張しない。
