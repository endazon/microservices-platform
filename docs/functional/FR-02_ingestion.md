---
title: 機能仕様書 — FR-02 取り込み（パース・チャンク化・埋め込み・索引登録）
type: functional-spec
status: in-progress
created: 2026-06-27
updated: 2026-10-09
author: claude
---
<!-- trace:
ids: [FR-02, FR-03, FR-05, UC-04]
adrs: [ADR-0003, ADR-0009, ADR-0013, ADR-0027, ADR-0070, ADR-0127, ADR-0016, ADR-0092]
iadrs: [IADR-0002, IADR-0149, IADR-0358, IADR-0388, IADR-0497, IADR-0025]
specs: [20260627_FR-02_ingestion-pipeline, 20260809_issue-536_search-result-updated-at, 20260903_issue-1193_bodyless-document-metadata-index, 20260905_issue-1253-1254_bodyless-index-and-hasbody-vocabulary, 20261005_1746_high-confidentiality-lexical-index, 20261007_1771_ingestion-event-wiring-docs, 20261009_1771_ingestion-completed-no-wiring]
issues: [#532, #536, #580, #1193, #1253, #1254, #1746, #1771, planning#741]
-->

# 機能仕様書: 取り込み

## 起点

- 機能要求: 文書のパース・チャンク化・埋め込み生成と検索インデックスへの登録 ／ ユースケース: データソースを登録・同期する
- 関連 ADR: メッセージング基盤（MassTransit + RabbitMQ。後継の Wolverine 採用により Superseded・注記は #580）、ベクトルストア Qdrant への直接書き込み、LLM Gateway 経由の埋め込み生成

## 機能概要

`IngestionService` が `DocumentUpdated` イベントを購読し、文書本文を検索可能なチャンクへ変換して Qdrant に登録する。これにより横断検索と AI 回答が文書を参照できるようになる。

## 入力 / 出力

### 入力イベント: `DocumentUpdated`

| フィールド | 型 | 用途 |
| --- | --- | --- |
| `DocumentId` | Guid | 文書識別子。チャンク削除・冪等 ID の基。 |
| `Title` | string | ペイロード `document_title`。 |
| `Status` | string | 文書状態。 |
| `MarkdownUri` | string? | 本文の所在。null の場合は取り込みをスキップ。 |
| `Attributes` | Dictionary<string,string> | ABAC 属性。ペイロード `attributes.<key>`。 |
| `Tags` | List<string> | タグ。ペイロード `tags`。 |
| `UpdatedAt` | DateTimeOffset | 更新時刻。**ペイロード `updated_at`（Unix epoch ミリ秒の整数）としてそのまま索引へ載せる**（#536。更新日時は索引ペイロードへ epoch ミリ秒で持つという実装判断）。**取り込み時刻を書かない** —— 書くと再索引のたびに全文書の「更新日時」が今になる。 |

### 出力イベント: `IngestionCompleted`

`DocumentId` / `ChunkCount` / `CompletedAt` を発行する。**この事象に購読者は無く、結線もしない**（2026-10-09 の裁定。計画は発行だけを定める）。
検索への反映は手順 7 の Qdrant への登録の時点で成立しており、完了通知を受けて動く後続の段は無い。

## 処理フロー（データソース登録・同期の基本フロー）

1. `DocumentUpdated` を受信する。
2. `MarkdownUri` が null なら警告ログを残しスキップする（例外フロー E1）。
3. 既存チャンクを `DeleteByDocumentAsync(DocumentId)` で削除する（再取り込みの冪等性）。
4. **parse**: `IDocumentContentReader.ReadAsync(MarkdownUri)` で本文 Markdown を取得する。
5. **chunk**: `IChunkingService.Chunk(text, maxTokens, overlap)` で見出し単位 + オーバーラップで分割する。
6. **チャンクが 0 件なら**（本文が空＝テキスト層の無い原本など）、本文由来のチャンク・埋め込みは作らず、
   **メタデータ点を 1 つだけ**登録して 8 へ進む（§本文なしの文書）。高機密文書のメタデータ点は語彙索引へ書く（§高機密文書）。
   **［2026-10-05］文書の機密区分が `public` / `internal` でなければ**（`confidential`・`restricted`・未指定・未知）、
   **埋め込みを呼ばずに**各チャンクを語彙索引へ登録して 8 へ進む（§高機密文書）。
7. 各チャンクについて:
   1. `chunkIndex`（0始まり）を採番する。
   2. `chunkId` を `DocumentId` + `chunkIndex` から決定的に生成する。
   3. **embed**: `IEmbeddingService.EmbedAsync(text)` で埋め込みベクトルを得る。
   4. **index**: `IIngestionVectorStore.UpsertChunkAsync(...)` で Qdrant に登録する（`chunk_index`/`tags`/`attributes` を含む）。
8. `IngestionCompleted(DocumentId, chunkCount, now)` を発行する。

### 例外フロー

- **E1（本文所在なし）**: `MarkdownUri` が null。警告ログを残し、何も登録せず正常終了する（メッセージは ack）。
- **E2（本文取得失敗）**: HTTP 取得が失敗した場合、`IDocumentContentReader` は例外を送出し、**Wolverine の再試行（2 秒・10 秒・30 秒の 3 回。全サービス共通の既定）を使い切ったらデッドレターへ送る**。`DocumentUpdated` の購読は Wolverine のハンドラであり、MassTransit に残っているのは `IngestionCompleted` の発行だけである（この例外の扱いには関わらない）。

## チャンク化規則（`MarkdownChunkingService`）

- Markdown 見出し（`#`〜`######`）でセクション分割する。
- セクションが `maxTokens`（既定 512、4文字≒1トークン推定）以下ならそのまま 1 チャンク。
- 超える場合は文（`。` `.` 改行）単位で詰め、`maxTokens` 到達で切り出す。
- **overlap**（既定 50 トークン ≒ 200 文字）: 長いセクションを分割する際、直前チャンク末尾の文字を次チャンク先頭へ引き継ぎ、文脈の断絶を防ぐ。

## 本文なしの文書（メタデータだけで索引する）

本文が取り出せない原本（テキスト層を持たない PDF など）は、**本文由来のチャンク・埋め込みを作らない**
（作れない）。それでも**題名・タグ・取り込み元のパス・データソース名から作った索引テキストを持つ点を
1 つだけ**登録し、横断検索に載せる。載せなければ、利用者はその文書の存在を知る手段を持たない。

- **判定は本文（の分割結果）そのもので行う** —— チャンクが 0 件になったときが「本文なし」である。
  変換側の状態名には依存しない（状態名の改名や別経路で静かに漏れるため）。
  **［2026-09-05］ただし、契約が運ぶ本文の有無（`hasBody`）と食い違ったら警告を残す。**
  判定そのものは変えない —— 足すのは観測だけである。二重化した情報のどちらかだけが変わったとき、
  従来は誰も気づかないまま索引の中身が割れていた。
- 点の ID は文書 ID から決定的に導き、**本文チャンクとは決して衝突しない**位置（`chunk_index` = `-1`）を使う。
  取り込みは冒頭で当該文書の点を全消しするので、**本文チャンクとメタデータ点が同時に存在することはない。**
- **索引テキストに入るのは題名・タグ・取り込み元のパス・データソース名**である。更新日時は既に
  ペイロードが持ち、ABAC 属性は入れない（絞り込みとは別経路の当て方を作らないため）。
  **［2026-09-05］パスとデータソース名を足した。** 従前この 2 つは取り込みの口へ届いておらず、
  利用者は題名を正確に覚えている場合しかその文書へ辿り着けなかった。**パスは区切り文字を空白へ
  開いてから入れる**（開かないとフォルダ名が語にならず当たらない）。拡張子は語にしない。
  **題名と重なる語は 2 度並べない。**
- 🔴 **本文ありのチャンクにはパスもデータソース名も載せない。** 載せると全文側にパスの断片が当たり、
  「本文に書いてある語で当たった」と「置き場所の名前で当たった」が抜粋から区別できなくなる。
  **本文なしの点は抜粋が空なので、その混同が起きない。** この非対称は意図したものである。
- 🔴 **既に索引済みの文書は、次の同期で再取得されるまで題名・タグだけのままである**
  （台帳がパスを知らないので再索引しても足す材料が無い）。**再同期は冪等である** ——
  文書 ID もメタデータ点の ID も決定的に導かれ、取り込みは全消ししてから登録する。
- ベクトルは**索引テキスト**から作る（本文由来ではない）。埋め込みの機密区分ルーティングは
  本文チャンクと同一に扱う —— 本文が無いことを理由に送信制御を緩めない。
- 点は `has_body = false` を持つ。検索側は復元時にこれを見て**本文抜粋を空にする**ので、
  索引テキスト（メタデータ）が本文の抜粋として外へ出ることはない。
- 完了イベントは**チャンク数 0 で発行する**（本文なしは失敗ではない。溜めない）。

## 高機密文書（埋め込まず語彙索引だけに載せる）

**［2026-10-05］** `confidential`・`restricted`・機密区分が未指定・未知の文書は、**埋め込みを作らない。**
本文はどの埋め込みの送信先（クラスタ内のセルフホストを含む）へも送らない。かわりに、**ベクトルを持たない専用の
Qdrant コレクション（語彙索引。既定 `knowledge_chunks_lexical`）**へ全文索引だけで載せる。

- **埋め込みを呼ぶ前に分ける。** 埋め込みへ進むのは機密区分が `public` / `internal`（大小文字は問わない）の文書だけで、
  それ以外は空・未知の値・属性なしを含めてすべて語彙索引だけ（安全側）。ゲートウェイの拒否を見てから分けるのではない ——
  セルフホストの埋め込みを有効にした配備でも、高機密文書は埋め込まれない。
- **点の表現はベクトルのコレクションの点と同じ**（`document_id` / `text` / `text_ngram` / `attributes` / `tags` / `shared_with` /
  `updated_at` / `markdown_uri` / `chunk_index`。本文なしは `has_body = false`）。違うのは**ベクトルを持たない**ことだけで、
  意味の無いベクトル（零ベクトル・ハッシュ）は入れない。チャンク ID は本文チャンクと同じ規則で決める。
- 本文の無い高機密文書は、語彙索引へ**メタデータ点を 1 つ**載せる（§本文なしの文書と同じ索引テキスト）。
- 埋め込みを呼ばないので、埋め込みの総枠・一時障害の再試行は関係しない。Qdrant への書き込みの失敗は従来どおり再試行される。
- `public` / `internal` の文書の埋め込みが恒久的に拒否されたとき（外部経路の無効化・次元不整合など）は、**従来どおりスキップする**
  （語彙索引へは回さない —— 埋め込めるはずの文書の不調を覆い隠さない）。
- 検索側は語彙索引を**全文の系統だけ**で束ねる（キーワードとハイブリッドのモードで現れ、意味検索のモードには現れない。
  [ハイブリッド検索 機能仕様書](./FR-03_hybrid-search.md)）。
- 🔴 **既に取り込まれた高機密文書は、語彙索引に自動では入らない**（従来はどこにも書かれていなかった）。
  `DocumentUpdated` の再発行（運用仕様書の再索引手順）で載る。

## 索引（Qdrant コレクション）

- コレクション名: `Qdrant:CollectionName`（既定 `knowledge_chunks`）。後方互換で `Qdrant:Collection` もフォールバックで解決する。
- ベクトル: 次元 = `Qdrant:VectorSize`（既定 1536）、距離 = Cosine。
- 起動時に `QdrantBootstrapHostedService` が存在保証（無ければ作成）する。
  **［2026-10-05］語彙索引（`Qdrant:LexicalCollection`。既定 `knowledge_chunks_lexical`）も、ベクトルの設定が空のコレクションとして作る。**
  全文索引（`text` / `text_ngram`）・`text_ngram` の後付け・文書単位の削除は、語彙索引を含む全コレクションに効く。
- ペイロード: `document_id` / `document_title` / `text` / `markdown_uri` / `chunk_index` / `tags` / `attributes.<key>` / **`updated_at`** / **`has_body`**。
- **`updated_at` は Unix epoch ミリ秒の整数**である（同実装判断の決定 1）。ISO-8601 文字列にすると同じ時刻を `+09:00` とも `Z` とも書けるため、辞書順が実時刻順と一致しない（並び順は #532 が使う）。
  **本項目より前に索引されたチャンクはキーを持たない** —— 検索側は `null` で返す（縮退。再索引で解消する）。
- **`has_body` は本文なしの点だけが持つ**（真偽）。**キーの欠落は「本文あり」を表す** ——
  既存の点はすべて本文チャンクなので、後付け（backfill）は要らない。

## トレーサビリティ

- コード: `IngestionService`（`DocumentUpdatedConsumer`, `MarkdownChunkingService`, `MetadataIndexText`, `QdrantIngestionVectorStore`, `IDocumentContentReader`, `QdrantBootstrapHostedService`）。各所に `// FR-02, UC-04` を付す。
- テスト: `IngestionService.Tests`。
