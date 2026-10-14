---
title: 埋め込みが使えない間も public / internal の文書を全文だけの点として語彙索引へ書き、鍵の投入後に埋め直す
type: spec
status: done
related_ids: [FR-02, FR-03, FR-05, UC-04, NFR-06, ADR-0016, ADR-0027, ADR-0070, ADR-0127, IADR-0497, IADR-0503, IADR-0504, IADR-0534, IADR-0536]
author: claude
created: 2026-10-14
updated: 2026-10-14
plan_refs:
  - planning:projects/microservices-platform/02_requirements（FR-02 取り込み / FR-03 横断検索 / NFR-06 障害時の縮退運転）
  - planning:projects/microservices-platform/07_adr/ADR-0127_high-confidentiality-lexical-only-and-claude-rerank.md
issue: "#1908"
---

# 仕様書: 埋め込みが使えない間も public / internal の文書を全文だけの点として書く（#1908）

## 起点となる計画書（トレーサビリティ）

- FR-02（取り込み）・FR-03（横断検索。キーワード／ハイブリッド）・FR-05（ABAC）・UC-04・NFR-06（障害時の縮退運転）。
- ADR-0016（埋め込みの機密区分ルーティング）・ADR-0127（高機密は語彙索引だけ）・ADR-0070 決定 4（本文なしはメタデータ点）・ADR-0027（Wolverine）。
- 実装判断の記録: [IADR-0536](../adr/IADR-0536_embedding-pending-lexical-points.md)（[IADR-0497](../adr/IADR-0497_high-confidentiality-lexical-index-vectorless-collection.md) 決定 2 を改める）。
- 起点: #1908。**オーナー裁定（2026-10-14）**: 問い 1 は「全文だけの断片として書く」、問い 3（観測）も実施、問い 2（現行のまま）は採らない。優先度は高。

## 背景（PoC の事象）

- Voyage の鍵が無い間、ゲートウェイは埋め込みに `Embedded=false`・`Retryable=true` を返す。取り込みは例外を投げて再試行の後 DLQ へ送る。
  恒久的な拒否（`Retryable=false`）はスキップする。どちらも**点を書かない**ので、全文索引（同じ点のペイロード）にも載らない。
- その結果、AST の情報収集（ニュース・開示・確定報告書。いずれも `internal`）が検索で 0 件のまま取引判断と日報に届いていない
  （2026-10-12 は判断 682 件すべてが `ragContext=empty`）。検索側の語彙縮退（#995・#1871 の `embed-failed`）は、埋め込みが働いていた時期に
  索引された文書にしか効かない。

## 決めたこと（要約。詳細と比較は IADR-0536）

1. **取り込み（`public` / `internal`）**: 埋め込みが `Embedded=false` を返したら（`Retryable` の真偽を問わない）、**そのチャンクと残りのチャンクを
   埋め込みを呼ばずに語彙索引へ「全文だけの点」として書く。** 点には `embedding_pending = true` と理由 `embedding_pending_reason`（`transient` / `rejected`）を載せる。
   本文なしの文書のメタデータ点も同じ。例外は投げない（DLQ へ送らない）。完了イベントは書いた点の数（埋めた数＋待ちの数）で出す。
   - 輸送の例外（gRPC 不達）・呼び出しごとの時間切れ・埋め込みの総枠の使い切りは**従来どおり**（例外で再試行／総枠はデッドレター）。
   - 🔴 **高機密（confidential・restricted・未指定・未知）の扱いは変えない。** 従来どおり埋め込みを呼ばずに語彙索引へ書き、**待ちの印は付けない**
     （埋め直しの対象にしない）。埋め直しは点の属性の機密区分を `LexicalIndexPolicy` で判定し直し、高機密なら**埋め込みを呼ばない**（二重の守り）。
2. **埋め直し（鍵の投入後）**: 取り込みサービスの常駐の器（`EmbeddingPendingReembedHostedService`）が、起動直後と以後一定間隔（既定 300 秒）で
   語彙索引の待ちの点を数え、1 周期に最大 64 点を埋め直す。成功した点は**主（埋め込みのコレクション）へ同じ ID・同じペイロードで書いてから、語彙索引の点を消す**。
   最初の失敗で周期を打ち切る（鍵が無い間にゲートウェイを叩き続けない）。
3. **観測（問い 3）**: 待ちの点の件数をゲージ `ingestion.embedding_pending.points`（Meter `microservices-platform.ingestion-service`）で出し、
   書いた数・埋め直した数を計数 `ingestion.embedding_pending.written` / `ingestion.embedding_pending.reembedded` で出す。
   **件数が 0 でない周期ごとに Warning を 1 行**出す（覆い隠さない）。
4. **既存分の流し直し**: 新しい管理の口は作らない。既存の `DocumentUpdated` の再発行（`POST /documents/republish-updated`・`scripts/republish-document-updated.js`）で
   `public` / `internal` を流し直す（本 PR の配備後は鍵が無くても DLQ へ行かず、待ちの点として入る）。DLQ の `DocumentUpdated` は従来どおり再投入しない（古い状態の写し）。
   手順は運用仕様書 §埋め込みプロバイダ と Voyage の鍵の Runbook に書く。

## 母集合（規則 9・10。誤りの側の文字列で全文書を走査した結果）

### 書き込みの経路（取り込みが Qdrant へ書く口）

`git grep -n "client.UpsertAsync\|client.DeleteAsync\|UpdateBatchAsync\|CreatePayloadIndexAsync" src/knowledge/backend/Services/IngestionService`（PR 前の develop `966c8d94`）:

| 口 | 本件での扱い |
| --- | --- |
| `UpsertChunkAsync`（主へチャンク） | 変えない。埋め直しの書き先でもある |
| `UpsertMetadataPointAsync`（主へメタデータ点） | 変えない。埋め直しの書き先でもある |
| `UpsertLexicalChunkAsync` / `UpsertLexicalMetadataPointAsync`（高機密） | 変えない（待ちの印を付けない） |
| **新** `UpsertEmbeddingPendingChunkAsync` / `UpsertEmbeddingPendingMetadataPointAsync` | 語彙索引へ、同じペイロード ＋ `embedding_pending=true`・理由 |
| `DeleteByDocumentFromAllAsync` | 変えない。語彙索引も消すので、再取り込みで待ちの点も消える（二重に残らない） |
| **新** `DeleteEmbeddingPendingAsync`（点 ID 指定） | 埋め直しの後に待ちの点だけを消す |
| `BackfillCjkNgramAsync`（`text_ngram` の後付け） | 変えない（語彙索引も対象。待ちの点は最初から `text_ngram` を持つ） |
| `EnsureCollectionsAsync`（全文索引） | 語彙索引の `embedding_pending` に真偽の索引を足す（数える・拾う問い合わせが全走査にならないように） |
| キーワード索引（起動時・書き込み時・発見） | 変えない（待ちの点の書き込みも属性キーへ張る） |

検索サービスの `QdrantVectorStore.UpsertAsync` は試験と InMemory だけが使う（本番の書き込みは取り込みだけ）。対象外。

### 検索の経路（全文だけの点がどう読まれるか）

- `HybridSearchService`: 語彙索引は**常に**束ねられ（IADR-0497 決定 5）、キーワードとハイブリッドで全文の系統として RRF に入る。意味検索のモードには入らない。
  → 待ちの点は**キーワードとハイブリッドで当たり、意味検索では当たらない**。AST の `HttpKnowledgeBaseSearch` は Mode を送らない（既定 hybrid）ので当たる。
- RRF は `ChunkId` で束ねる（`scores[hit.ChunkId]`）。埋め直しの途中（主へ書いた後・語彙索引から消す前）に両方へ同じ ID の点があっても、結果は 1 件にまとまる。
- ABAC: 語彙索引の全文の系統へ主と同じフィルタを渡す。待ちの点は高機密の点と同じペイロード表現なので、判定軸は変わらない。
- 露出（`search_exposure` / `ai_input`）: `Finish` が点の属性で落とす。全文だけの点も同じ。
- 文書削除（`DocumentDeletedConsumer`）: 束ねる追加コレクション（語彙索引を含む）からも消す。待ちの点も消える。

### DLQ（`wolverine-dead-letter-queue`）

- 取り込みの受け口から DLQ へ行く経路: ① 埋め込みの一時障害の例外（`EmbeddingTransientException`。**本件で撤去**）、② 本文の取得・Qdrant・埋め込みの時間切れ・輸送の例外（再試行の枯渇後。変えない）、
  ③ 埋め込みの総枠の使い切り（`EmbeddingBudgetDeadLetterPolicy`。変えない）。
- DLQ のメッセージは古い状態の写しなので再投入しない（運用仕様書の既定）。流し直しは `DocumentUpdated` の再発行で行う。

### 記述の母集合（この変更で誤りになる記述）

`git grep -n -E "語彙索引へは回さない|覆い隠さない|覆い隠す|DLQ へ行くだけ|再試行の後 DLQ|再試行のあと DLQ|EmbeddingTransientException|transient embedding failure|索引に入らない|DLQ を再投入|DLQ 再投入で回復" -- . ':!.ai-context/specs'`
と `git grep -n -E "embed-failed|鍵待ち|鍵が無い|鍵なし" -- docs deploy scripts src/knowledge` の生の出力から、本件に関わる行:

| 箇所 | 扱い |
| --- | --- |
| `IngestionService/Features/Ingestion/Ingest/DocumentUpdatedConsumer.cs`（一時障害の例外・恒久拒否のスキップ） | 実装を改める |
| `IngestionService/Tests/.../DocumentUpdatedConsumerTests.cs`（T-09・T-11・T-14・T-15 相当） | 期待を改める |
| `docs/functional/FR-02_ingestion.md`（語彙索引へは回さない） | 改める・節を足す |
| `docs/functional/FR-03_hybrid-search.md`（`embed-failed` は全文なら当たるを意味しない） | 改める |
| `docs/tests/FR-02_ingestion.md`（T-13・T-14） | 改める・T-40〜を足す |
| `docs/operations/operations.md`（再索引の前に鍵／一時障害は DLQ／全件が DLQ へ行くだけ） | 改める・手順を足す |
| `docs/operations/voyage-embedding-key-runbook.md`（鍵の有無の表・再試行の後 DLQ） | 改める・埋め直しの確認を足す |
| `deploy/helm/microservices-platform/values.yaml`（コメント）・`deploy/local/README.md`・`deploy/local/vault/eso/bootstrap.sh`（コメント）・`deploy/bootstrap/secret-templates.example.yaml`（コメント） | 鍵なしの帰結の記述を改める |
| `scripts/republish-document-updated.js`（冒頭コメント） | 鍵なしの帰結の記述を改める |
| `platform/.../LlmGateway/Domain/Ports/EmbeddingProviderNotConfiguredException.cs`・`Features/Embeddings/Embed/EmbedUseCase.cs`・`Tests/.../VoyageEmbeddingKeyTests.cs`（取り込みは再試行の後 DLQ） | ゲートウェイの挙動は変えない。取り込み側の帰結のコメント・`because` を改める |
| `.ai-context/adr/IADR-0497`（決定 2）・`IADR-0504`・`IADR-0534`・`IADR-0503` | 凍結記録。本文は書き換えず、日付つきの追記で IADR-0536 を指す |

除外とその理由:

- `.ai-context/specs/` の確定済みの作業仕様書（#1746・#1762・#1764・#1819・#1871 ほか）: point-in-time の記録であり書き換えない。
- `IADR-0255:77`・`CjkBigramPayloadTests.cs:42`・`check-backend-libraries.js:334`・試験の器の「覆い隠さない」（`BrokerTcpGate.cs` ほか）: 語が一致しただけで別の事柄。
- `deploy/helm/.../templates/deployment.yaml:335`・`operations.md:1062`: 決定的ローカル埋め込みの説明で、本件の帰結に触れない。
- `SearchDegradationMetrics.cs:8`・`SearchDto.cs:111`・`document_search.proto`: 検索クエリの縮退の説明で、取り込みの帰結に触れない。
- `docs/api/openapi.yaml` の `embed-failed`: 「hybrid は語彙検索のみ」は本件後も正しい。

## 受け入れ基準と試験の写像

| # | 基準 | 試験（陽性／陰性） |
| --- | --- | --- |
| A1 | `public` / `internal` の埋め込みが一時障害（鍵なし）のとき、チャンクを語彙索引へ待ちの点として書き、例外を投げない | `DocumentUpdatedConsumerTests`: 一時障害 → 待ちの点が全チャンク分・理由 `transient`・例外なし・完了イベント（T-14 改） |
| A2 | 恒久的な拒否も同じく待ちの点として書く（理由 `rejected`） | 同: 恒久拒否 → 待ちの点（T-13 改・T-09 改） |
| A3 | 途中のチャンクで失敗したら、それより前は主へ、そこから後は埋め込みを呼ばずに待ちの点へ | 同: 2 チャンク目で失敗 → 主 1・待ち 2、埋め込みの呼び出しは 2 回（T-40） |
| A4 | 本文なしのメタデータ点も同じ | 同: 本文なし＋一時障害 → 待ちのメタデータ点 1（T-15 改） |
| A5 | 高機密は待ちの印を付けない（従来どおりの語彙索引） | 同: T-23 の既存（待ちの袋は空）＋ 埋め直しの陰性（高機密の点は埋め込みを呼ばない）（T-43） |
| A6 | 埋め込めたときは待ちの点を書かない（陽性対照） | 同: T-25 に「待ちの袋は空」を足す |
| A7 | 埋め直しは成功した点を主へ書いてから語彙索引の点を消す（二重に出ない・消し忘れない） | `EmbeddingPendingReembedderTests`: 操作の順が「主へ書く → 待ちの点を消す」、消すのは書いた点だけ（T-41） |
| A8 | 埋め直しの失敗は点を残し、周期を打ち切る | 同: 1 点目が失敗 → 主へ書かない・消さない・2 点目を呼ばない（T-42） |
| A9 | 再確認で点が消えていた／中身が変わっていたら書かない（取り込みとの競合） | 同: 再確認が null ／ 更新日時が違う → 主へ書かない（T-44） |
| A10 | 件数をゲージで読め、0 でなければ Warning を出す。0 なら出さない | 同: ゲージの観測値・Warning の有無（T-45）・`EmbeddingPendingMetricsTests` |
| A11 | 待ちの点のペイロードは主の点と同じ表現 ＋ 印。語彙索引へ書く。数える・拾うフィルタは印だけを引く | `QdrantIngestionVectorStoreTests` / `LexicalIndexStoreTests`: 純関数（ペイロード・フィルタ）と gRPC の記録（T-46） |
| A12 | 読み戻し（payload → 待ちの点）が書いた値を復元する | 同: 往復（T-47） |
| A13 | 本番の Program.cs が器・計器を登録する | `IntrospectionEndpointTests` と同じ器で、ホストのサービスに器があることを見る（T-48） |
| 変異 | 「待ちの点を書かない」に戻すと A1・A2・A4 が赤になる | 作業中に実施し、結果を下の §変異試験 に記す |

## 変異試験

`dotnet test Services/IngestionService/Tests/IngestionService.Tests.csproj`（変異は作業後に元へ戻した）:

| 変異 | 結果 |
| --- | --- |
| 取り込みで待ちの点を書かない（チャンクの書き込みの分岐を通さない・メタデータ点の書き込みを外す） | **5 件が赤**: T-09 改（恒久拒否）・T-11 改（一時障害）・T-40（途中で失敗）・T-14 改・T-15 改。他 166 件は緑 |
| 埋め直しで語彙索引の点を消さない ＋ 高機密の判定を外す | **5 件が赤**: T-41（主へ書いて消す）・上限の試験・T-43（3 通り）。他 166 件は緑 |
| 変異なし | 171 件すべて緑 |

## 既存の再発行の手順との整合（2026-10-14・コーディネーターの連絡を受けて）

- 同日、PoC に Voyage の鍵が投入され、埋め込みは通るようになった（`knowledge_chunks_voyage_3_5` が 0 → 234 件）。優先度は中へ下がったが、裁定（鍵が無い・壊れたときは全文だけの点）は有効なので範囲は変えない。
- PoC は既存の約 3.9 万件の `DocumentUpdated` を、運用仕様書の「`DocumentUpdated` の再発行」の手順（dry-run → カナリア → 全件）で流す予定である。
  **本 PR は既存分の流し直しに新しい手段を足さない。** 同じ手順をそのまま使い、運用仕様書・Voyage の鍵の Runbook には「別の手順を持たない」と書いた。
  変わるのは、鍵が届いていない・壊れている間に流した文書が DLQ ではなく語彙索引の埋め込み待ちへ入ることだけであり、そのため Runbook の 5-c に
  「DLQ が増えないことだけでは鍵の確認にならない（`knowledge_chunks_voyage_3_5` が増えることで確かめる）」を足した。

## 残るもの

- 待ちの点は**意味検索のモードでは当たらない**（語彙索引はベクトルを持たない）。鍵の投入後に埋め直されるまでの縮退である。
- 埋め直しは複数の取り込みの Pod で同時に走り得る（PoC は 1 Pod）。同じ点を 2 回埋める費用が出るだけで、書き込みと削除は冪等である。
- 再確認から主への書き込みまでの間に、同じ文書の新しい `DocumentUpdated` が削除・埋め込み・書き込みを**すべて**終えると、古い本文が主へ残り得る（次の更新で直る）。窓は Qdrant 1 往復である。
- 巨大な文書で途中から待ちへ切り替わると、残りのチャンクの書き込みは埋め込みの総枠を通らない（高機密の経路と同じ。IADR-0497 §残るもの）。
