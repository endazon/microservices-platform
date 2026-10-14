---
title: IADR-0536 埋め込みが使えない間も public / internal の文書を語彙索引へ全文だけの点（埋め込み待ち）として書き、埋め込みが使えるようになったら主へ移して消す。待ちの件数は計器と周期ごとの Warning で読む（IADR-0497 決定 2 を改める）
type: impl-adr
status: Accepted
related_ids: [FR-02, FR-03, FR-05, UC-04, NFR-06, ADR-0016, ADR-0027, ADR-0070, ADR-0127, IADR-0497, IADR-0503, IADR-0504, IADR-0534, IADR-0025, IADR-0358]
author: claude
created: 2026-10-14
updated: 2026-10-14
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0127_high-confidentiality-lexical-only-and-claude-rerank.md (決定 1・2・4)
  - planning:projects/microservices-platform/07_adr/ADR-0016_embedding-provider-voyage.md
related_specs:
  - ../specs/20261014_1908_lexical-only-chunks-while-embedding-unavailable.md
---

# IADR-0536: 埋め込みが使えない間も public / internal の文書を全文だけの点として書く（#1908）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-14
- 決定者: オーナー裁定（2026-10-14・#1908 のコメント）。実装の形は claude

## 起点・関連

- 起点 issue: #1908（問い 1〜3）。オーナー裁定（2026-10-14）: **問い 1 は「全文だけの断片として書く」**、**問い 3（観測）も実施**、問い 2（現行のまま）は採らない。
- 関連する計画書 ID: FR-02（取り込み）・FR-03（キーワード／ハイブリッド検索）・FR-05（ABAC）・UC-04・NFR-06（障害時の縮退運転）
- 関連する計画 ADR: ADR-0016（埋め込みの機密区分ルーティング）・ADR-0127（高機密は語彙索引だけ。決定 1・2・4）・ADR-0070 決定 4（本文なしはメタデータ点）・ADR-0027（Wolverine）
- 関連する実装 ADR: [IADR-0497](./IADR-0497_high-confidentiality-lexical-index-vectorless-collection.md)（**決定 2 の「public / internal の経路は 1 ビットも変えない」「恒久的な拒否は語彙索引へは回さない」を本 IADR が改める**。同 IADR に追記）・
  [IADR-0504](./IADR-0504_voyage-embedding-key-secret-wiring.md)（鍵なしは再試行の後 DLQ・鍵を入れて再投入。**取り込み側の帰結を本 IADR が改める**。同 IADR に追記）・
  [IADR-0503](./IADR-0503_republish-document-updated-admin-endpoint-and-driver.md)（再発行の口と駆動スクリプト。既存分の流し直しに使う）・
  [IADR-0534](./IADR-0534_search-response-degraded-signal.md)（`embed-failed` は「全文なら当たる」を意味しない。**本 IADR の後は新規文書にも当たる**。同 IADR に追記）・
  [IADR-0358](./IADR-0358_bodyless-document-metadata-point.md)（メタデータ点）
- 関連する実装仕様書: `.ai-context/specs/20261014_1908_lexical-only-chunks-while-embedding-unavailable.md`（母集合・試験の写像・変異試験はそちら）
- 採番: `origin/develop` `966c8d94` の最大は IADR-0535。開いている PR（#1726 CHANGELOG の自動更新・#1920 文書）は IADR を足さない。本件は **0536**。

## コンテキストと課題

- Voyage の鍵が無い間、ゲートウェイは `Embedded=false`・`Retryable=true` を返す（IADR-0504）。取り込みは例外で再試行の後 DLQ へ送り、恒久的な拒否（`Retryable=false`）はスキップする。
  **どちらも点を書かない**。全文索引（`text` / `text_ngram`）は同じ点のペイロードなので、**public / internal の文書は全文検索にも現れない**。
- IADR-0497 決定 2 はこれを「埋め込めるはずの文書の不調を語彙索引で覆い隠さない」として意図的に選んでいた。
- PoC（2026-10-11〜14）: AST の情報収集が集めたニュース・開示・確定報告書（`internal`）が検索で 0 件。2026-10-12 は取引判断 682 件すべてが `ragContext=empty`、
  10-14 の日報も「判断に影響したニュースや開示は今回の入力に含まれていない」。オーナーは「ニュースなしの判断は実弾の前提として危うい」として決定 2 を改めると裁定した。

## 検討した選択肢

| 案 | 評価 |
| --- | --- |
| **(A) 埋め込めなかったチャンクを語彙索引へ「全文だけの点」として書き、印（`embedding_pending`）を付ける。取り込みの器とは別の常駐の器が、埋め込みが使えるようになったら主へ移して語彙索引の点を消す** | **採用**。①置き場所は既存の語彙索引（ベクトルを持たないコレクション。IADR-0497 決定 1）をそのまま使える —— 主は次元つきでベクトル無しの点を置けない。②検索側は無改修（語彙索引は常に束ねられ、キーワードとハイブリッドで全文の系統に入る）。③埋め直しは点のペイロードだけで完結し、台帳（文書サービス）への呼び戻しが要らない。④鍵を入れれば運用の操作なしで収束する |
| (B) 現行のまま（DLQ に溜め、鍵の投入後に再発行）とし、手順書に明記する | **採らない**（オーナー裁定・問い 2） |
| (C) 全文だけで書くが、埋め直しは運用者が `DocumentUpdated` を再発行して行う（自動の器を持たない） | 採らない（主たる手段としては）。どの文書が待ちなのかを文書サービスは知らないので、再発行は public / internal の全件になり、埋め込めていた文書の費用も重なる。**補助の手段としては残す**（再発行の取り込みは冒頭で全コレクションから消すので、待ちの点も消えて埋め直される） |
| (D) 一時障害（`Retryable=true`）だけ全文で書き、恒久的な拒否は従来どおりスキップ | 採らない。恒久的な拒否も「埋め込みが使えない」（経路の無効化・次元不整合）であり、裁定の趣旨（使えない間も語彙検索で当てる）から外れる。理由は点の属性（`embedding_pending_reason`）と計器のタグで分けて観測する |
| (E) 一時障害はまずブローカの再試行（4 試行）に回し、枯渇してから全文で書く | 採らない。ゲートウェイの応答は鍵の欠落と一時的な不調を区別しない（どちらも `Retryable=true`）。鍵が無い間は 4 試行 × 文書の数だけ無駄に待ち、DLQ 行きの判定も Wolverine の失敗処理の中へ持ち込むことになる。一時的な不調は埋め直しの器が次の周期で拾う |

## 決定

### 決定 1: public / internal の埋め込みが `Embedded=false` なら、そのチャンクから後ろを全文だけの点として語彙索引へ書く

- 対象は `LexicalIndexPolicy.IsLexicalOnly` が偽の文書（`public` / `internal`）だけ。ゲートウェイの応答が `Embedded=false` のとき、`Retryable` の真偽を問わない。
- **そのチャンクと残りのチャンクを、埋め込みを呼ばずに**語彙索引へ書く（使えない送信先をチャンクの数だけ叩かない）。それより前の埋め込めたチャンクは従来どおり主へ書く。
  同じチャンク ID が主と語彙索引の両方へ書かれることはない。
- 点のペイロードは語彙索引の点と同じ表現（`BuildChunkPayload`）に、**`embedding_pending = true`（真偽）** と **`embedding_pending_reason`（`transient` / `rejected`）** を足したもの。
  本文なしのメタデータ点も同じ（`has_body = false`・`chunk_index = -1`）。ポートの口は `UpsertEmbeddingPendingChunkAsync` / `UpsertEmbeddingPendingMetadataPointAsync` として高機密の口と分ける。
- **例外を投げない**（DLQ へ送らない）。完了イベントのチャンク数は書いた点の数（埋めた数 ＋ 待ちの数）。文書ごとに Warning を 1 行残す（理由・機密区分・件数）。
- 🔴 **変えないもの**: 高機密（`confidential`・`restricted`・未指定・未知）の経路（埋め込みを呼ばずに語彙索引。**印は付けない**）・ゲートウェイの越境判定・
  輸送の例外（gRPC 不達）と呼び出しごとの時間切れの再試行・埋め込みの総枠の使い切りのデッドレター（`EmbeddingBudgetDeadLetterPolicy`）。
- 一時障害の例外型 `EmbeddingTransientException` は送出元が無くなったので撤去する。

### 決定 2: 埋め直しは取り込みサービスの常駐の器が行い、「主へ書く → 語彙索引の点を消す」の順に移す

- `EmbeddingPendingReembedHostedService` が起動直後と以後 `Ingestion:EmbeddingPending:IntervalSeconds`（既定 300 秒）ごとに 1 周期を走らせる。
  1 周期で拾うのは `Ingestion:EmbeddingPending:BatchSize`（既定 64）点まで。正の整数でない値は既定へ倒す。
- 各点について: 属性を `LexicalIndexPolicy` で判定し直し、**高機密なら埋め込みを呼ばずに残す**（二重の守り。件数に残り続けるので決定 3 の Warning で見える）。
  埋め込み → **書く直前に点を読み直す**（無い・更新日時か本文が変わっていたら捨てる。取り込みが同じ文書を書き直した場合に古い本文を主へ書かない）→
  主へ同じ ID・同じペイロード（チャンクは `UpsertChunkAsync`、メタデータ点は `UpsertMetadataPointAsync`）→ 語彙索引から**その ID で印を持つ点だけ**を消す。
- **最初の埋め込みの失敗でその周期を打ち切る**（鍵が無い間、1 周期の埋め込みの呼び出しは 1 回）。輸送の例外・時間切れは周期の失敗として Warning を残し、次の周期へ進む（ホストを止めない）。
- 順序の理由: 途中で落ちても点が両方に残るだけで（検索の RRF はチャンク ID で束ねるので 1 件に見える）、次の周期で書き直して消す —— 収束する。逆順は書く前に落ちた点を失う。
- 語彙索引の `embedding_pending` に真偽のペイロード索引を張る（最初の周期。冪等。失敗しても周期は止めない）。数える・拾う問い合わせを全走査にしないための最適化である。

### 決定 3: 待ちの件数を計器と周期ごとの Warning で読めるようにする（問い 3）

- Meter `microservices-platform.ingestion-service`（サービス名。`Program.cs` で収集対象へ足す）に次を置く:
  - ゲージ `ingestion.embedding_pending.points`（Prometheus では `ingestion_embedding_pending_points`）: 周期ごとに数えた語彙索引の待ちの点の数。**まだ数えていない間は系列を出さない**（0 を「待ちなし」と読ませない）。
  - 計数 `ingestion.embedding_pending.written`（タグ `ingestion.embedding_pending_reason` = `transient` / `rejected`）と `ingestion.embedding_pending.reembedded`。
- **待ちが 0 でない周期は必ず Warning を 1 行出す**（残りの数・この周期に埋め直した数・打ち切った理由・高機密として残した数・Runbook の所在）。0 になった周期は Information。
- **0 が正常である。** 0 でない値が続くことが「埋め込みが使えていない」の検出になる。

### 決定 4: 既存分の流し直しは、既存の `DocumentUpdated` の再発行で行う（新しい口は作らない）

- 本 PR の配備後、`public` / `internal` の文書を再発行（`POST /documents/republish-updated`・`scripts/republish-document-updated.js --attr confidentiality=internal` と `public`）すると、
  鍵が無くても DLQ へ行かず、待ちの点として入る（その時点で語彙検索に当たる）。
- DLQ に溜まった `DocumentUpdated` は古い状態の写しなので**再投入しない**（運用仕様書の既定どおり）。再発行で今の台帳から作り直した後、運用者が中身を確かめて purge する。
- 鍵の投入後は、埋め直しの器が待ちの点を主へ移す（再発行は要らない）。急ぐときは取り込みの Pod を作り直す（起動直後に 1 周期が走る）。

## 理由

- 裁定は「使えない間も語彙検索で当てる」と「覆い隠さない」の両立を求めた。前者は語彙索引の再利用（検索は無改修）で、後者は待ちの印・計器・周期ごとの Warning で満たす。
- 埋め直しを点のペイロードだけで完結させるのは、待ちの文書を知っているのが取り込み（語彙索引）だけだからである。台帳側の再発行に寄せると対象を絞れない（案 C）。

## 結果

- **良い影響**
  - 鍵の無い間も、public / internal の文書がキーワードとハイブリッドの検索で当たる（AST の `ragContext` / `ragReferences`・日報の材料が空でなくなる）。
  - 鍵の投入後、運用の操作なしで主へ移る（意味検索にも当たるようになる）。
  - 鍵の無い間の取り込みの再試行・DLQ の滞留が無くなる（再試行のたびのゲートウェイの呼び出しが減る）。
- **悪い影響 / トレードオフ**
  - 🔴 **埋め込みの不調が検索の結果からは見えにくくなる**（IADR-0497 決定 2 が避けた形）。決定 3 の計器と Warning が代わりの検出であり、**ゲージが 0 でない間は埋め込みが使えていない**と読む。
  - 待ちの点は**意味検索のモードでは当たらない**（語彙索引はベクトルを持たない。IADR-0497 決定 5）。
  - 恒久的な拒否（経路の無効化・次元不整合）でも点が入るので、取り込みの「索引できた件数」だけでは拒否に気づけない。理由のタグ（`rejected`）で分けて読む。
- **残るもの**
  - 埋め直しは取り込みの Pod の数だけ同時に走り得る（PoC は 1 Pod）。同じ点を 2 回埋める費用が出るだけで、書き込みと削除は冪等である。
  - 読み直しから主への書き込みまでの間に、同じ文書の新しい `DocumentUpdated` が削除・埋め込み・書き込みを**すべて**終えると、古い本文が主へ残り得る（次の更新で直る）。窓は Qdrant 1 往復である。
  - 途中で待ちへ切り替わった巨大な文書は、残りのチャンクの書き込みが埋め込みの総枠を通らない（高機密の経路と同じ。IADR-0497 §残るもの）。受け口の実行期限が上から抑える。
  - **機械検査**: 取り込みと埋め直しの振る舞いは単体試験（`DocumentUpdatedConsumerTests`・`EmbeddingPendingReembedderTests`・`LexicalIndexStoreTests`）が固定する。実 Qdrant での往復（印の索引・条件つき削除）は統合試験を足していない（純関数と gRPC の記録で形を固定）。

## 試験

受け入れ基準と試験の対応は作業仕様書 §受け入れ基準と試験の写像 と `docs/tests/FR-02_ingestion.md`（T-09・T-11・T-13〜T-15 の改定、T-40〜T-48）。

## 関連

- Supersedes: なし（IADR-0497 決定 2 の public / internal の部分を改める。同 IADR に追記）
- Superseded by: なし
