---
title: 作業仕様書 — 取り込み（Ingestion）の DocumentUpdated の順序と原子性（版の印・書いてから消す）
type: spec
status: done
related_ids: [FR-02, FR-05, FR-06, FR-19, UC-04, ADR-0027, ADR-0057, ADR-0016, ADR-0127, IADR-0595, IADR-0497, IADR-0536, IADR-0540, IADR-0551, IADR-0396, IADR-0149]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-02・FR-05・FR-06・FR-19）
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md（Wolverine・再試行／デッドレター・Outbox を標準とする）
  - planning:projects/microservices-platform/07_adr/ADR-0057_deletion-propagation-scope.md（決定 1・2: 索引まで消す・復元に使える残余を置かない）
issue: "#2101"
---

# 作業仕様書 — 取り込み（Ingestion）の DocumentUpdated の順序と原子性

> 本仕様書は実装着手前に作成した（着手 2026-10-23）。判断の記録は **IADR-0595**（新規）に置く。
> 親 issue #2047 の指摘 M-4（Ingestion 分）・M-5。Wiki・Catalog の順序防御は #2102（別 PR）。Transactional Outbox の欠如は #2042（別件）。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-02**（取り込み・索引登録）、**FR-05**（ABAC。索引の判定軸）、FR-06 / FR-19（削除・撤収の伝播）。ユースケース: UC-04。
- 計画 ADR: **ADR-0027**（Accepted。メッセージは少なくとも 1 回・失敗は再試行の後デッドレター）、**ADR-0057**（Accepted。決定 1: 削除は索引まで及ぶ／決定 2: 復元に使える残余を置かない。削除の事実の記録は許す）、ADR-0016・ADR-0127（コレクションの分離・語彙索引）。
  **計画に順序（到着順の逆転）を扱う明文は無い。** 本変更は FR-02 / FR-05 の受け入れ基準（索引に載る・権限外に出ない）を、少なくとも 1 回・順序非保証の配送の下でも保つための実装判断であり、計画との矛盾は無い。
  版の印は ADR-0057 決定 2 の「残余」に当たらない（文書 ID・版・一方向の指紋だけで、本文・題名・属性の値を持たず復元に使えない。IADR-0595 決定 3）。
- 実装 ADR: **IADR-0595**（新設）。引き継ぐ判断: IADR-0497 決定 4（全コレクションからの削除）・IADR-0536（埋め込み待ちの点）・IADR-0540 / IADR-0551（本文の欠落・アーカイブの門）・IADR-0396 決定 4・5（露出の門）・IADR-0149 決定 1・5（`updated_at` は epoch ミリ秒・イベントの値）。
- 対照: GraphService の `GraphDocument.Apply`（`if (updatedAt < UpdatedAt) return false;`）と試験「保持中より古いイベントは適用されない_厳格化後に緩和が復活しない」。

## 範囲

- 本 PR: IngestionService の `DocumentUpdated` の受け口（`DocumentUpdatedConsumer`）と、その書き込みポート（`IIngestionVectorStore` ／ `QdrantIngestionVectorStore`）。
- 範囲外（境界）:
  - **#2042（Transactional Outbox が無い）**: 発行側（DocumentService）の「DB の確定」と「イベントの発行」の原子性。発行が落ちたイベントは本 PR では回復しない。本 PR は**届いたイベントの順序と、取り込みの途中失敗**だけを扱う（受け手側）。Outbox が入っても到着順の逆転・再配送は残るので、本 PR の防御は Outbox と独立に要る。
  - **#2102**: WikiService・DocumentService（Catalog）の順序防御。
  - **全順序の版番号を契約に足すこと**（`DocumentUpdated` に単調増加の版を運ぶ）。`UpdatedAt` を進めない変更の順序（下の §限界）はこれが無いと判定できない。発行側・4 つの受け手（Ingestion / Wiki / Catalog / Graph）を揃えて変える契約変更であり、本 PR では広げない（未決事項として報告する）。
  - #2096（デッドレターの計器）。

## 母集合（規則 9・10。着手前に引いた）

`git grep` で誤りの側の字面（`DeleteByDocumentFromAllAsync`・`IIngestionVectorStore`・`DocumentDeleted`・`ev.UpdatedAt`）を引いた結果:

| 区分 | 対象 | 扱い |
| --- | --- | --- |
| 受け口 | `DocumentUpdatedConsumer.Handle`（Ingestion の受け口は**これ 1 つ**。購読は `pipeline.json` の `ingest` 段だけ） | 改める |
| `DocumentDeleted` の受け口 | **Ingestion には無い**（`retrieval-delete`・`wiki-delete`・`graph-delete` の 3 段）。索引の点は RetrievalService の `DocumentDeletedConsumer` が消す | 改めない。削除の後に遅れて届く古い `DocumentUpdated` は、版の印（点を消しても残る）で弾く。物理削除は本文の実体を先に消してから発行するため（`Delete/Endpoint.cs`: purge → publish）、削除前の版と等しい版の再配送は本文の欠落（NoSuchKey）で点を書かない |
| 書き込みの口 | `UpsertChunkAsync`・`UpsertMetadataPointAsync`・`UpsertLexicalChunkAsync`・`UpsertLexicalMetadataPointAsync`・`UpsertEmbeddingPendingChunkAsync`・`UpsertEmbeddingPendingMetadataPointAsync`（6 口） | 書いた点を記録し、後始末で残す |
| 削除の口 | `DeleteByDocumentFromAllAsync`（撤収の門 2 つ・判定軸が変わる更新の先消し） | 残す（fail-closed の側） |
| 順序の判定キー | `DocumentUpdated.UpdatedAt`（`Document.Touch()` が進める: 更新・属性の更新・正規化の適用・本文の差し替え・公開・アーカイブ・タグの追加） | 判定キーに使う。**進めない変更**: 露出トグル（`SetExposureAttributes`）・共有の付与と取り消し・タグの改名・再発行 → §限界 |
| 再配送 | Wolverine の再試行（同じ配信の中で回る）・デッドレターからの再投入・ブローカの再配送・レプリカ／並列 | 等しい版は反映（冪等）。古い版は弾く。並行は書き終えた後の読み直しで塞ぐ |
| 偽物（試験） | `IIngestionVectorStore` の実装: Ingestion の試験 9 つ・結合試験 2 つ（`SharedIndexIngestionVectorStore`・fan-out の `RecordingVectorStore`） | 受け口を通る 8 つに新しい口を足す（印は常に無い ＝ 従前の挙動）。受け口を通らない 3 つ（埋め直し・索引の状態・キーワード索引の器）は既定（投げる）のまま |
| 時間の上限 | `IngestionTimeouts.From` の `EnsureFits`（最悪の所要時間）・`EnsureRetryChainFits` | 版の印 3 回・旧版の削除を足し、受け口の既定を 420 → 435 秒（IADR-0595 決定 5） |

除外: `EmbeddingPendingReembedder`（埋め直しは取り込みと別の器で、取り込みとの競合は既存の再確認〔#1908〕が扱う）。`DocumentIndexStatusGrpcService`（読むだけ）。

## 要件

1. 索引へ反映済みの版より古い `DocumentUpdated` は反映しない（skip して ack）。等しい版は反映する。
2. 撤収（露出 OFF・アーカイブ）・削除で点が無くなった後も、それより古い版で点が書き戻されない。
3. 判定軸（属性辞書 ＋ 共有先）が変わらない更新は、取り込みの途中で失敗しても旧版の点が残る（文書が消えない）。
4. 判定軸が変わる・不明な更新は、旧版の点を先に消す（失敗しても旧い判定軸の点を残さない。fail-closed）。
5. 新版を書き終えたら、この取り込みで書いていない旧版の点（チャンク数の減少の余り・別のコレクション）を消す。
6. 取り込みの最中により新しい版が反映されたら、追い越された版の点を残さない。
7. 版の印は検索のコレクションに混ぜない。題名・本文・タグ・属性の値を持たない。

## 設計（決定は IADR-0595）

- 版の印: ベクトルを持たない専用コレクション `knowledge_document_index_versions`（1 文書 1 点・点 ID ＝ 文書 ID）。ペイロードは `document_id`・`updated_at`（epoch ミリ秒）・`access_fingerprint`（判定軸の SHA-256）。最初の書き込みで作る（起動時のブートストラップは変えない）。
- 受け口の順: 印を読む → 古ければ終える → 撤収の門（全消し → 印）→ 本文所在なし（印だけ進める・指紋は引き継ぐ）→ 判定軸が変わる／不明なら全消し → 印を書く → 本文・チャンク・書き込み（書いた点を記録）→ 印を読み直す → 追い越されていれば自分の版以下を消して終える／そうでなければ書いていない旧版の点を消す → 完了イベント。
- 旧版の削除のフィルタ: `document_id` ∧ ¬(`updated_at` > 版) ∧ ¬(`has_id` 書いた点)。コレクションごとに書いた点を分ける。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| 1 | 新しい版（`confidential`）の後に古い版（`internal`）が届いても、索引の内容・機密区分・置き場所が新しい版のまま。古い版は本文を読まない | `IngestionOrderingAndAtomicityTests.保持中より古い更新は反映されない_厳格化後に緩和が復活しない` |
| 2 | 共有先を外した版の後に古い版が届いても共有先が復活しない | `…共有先を外した版の後に古い版が届いても共有先は復活しない` |
| 3 | 同じ版の再配送で点が増えず同じ内容に収束する | `…同じ版の再配送は点を増やさず同じ内容に収束する` |
| 4 | 撤収（アーカイブ）・削除で点が消えた後の古い版が点を書き戻さない（陽性対照つき） | `…撤収した後に古い版が届いても点は書き戻されない`・`…削除で点が消えた後に古い版が届いても点は書き戻されない` |
| 5 | 判定軸が同じ更新で本文の取得・書き込みが失敗しても旧版の点が残る | `…本文の取得が失敗しても旧版の点は残る`・`…書き込みが途中で失敗しても文書は索引から消えない` |
| 6 | 判定軸が変わる更新・印の無い文書で失敗したら旧版の点は残らない（再試行の成功で新しい判定軸で載る） | `…判定軸が変わる更新で失敗したら旧版の緩い点は残らない`・`…版の印が無い文書は旧版を先に消す` |
| 7 | チャンク数の減少・置き場所の移動で旧版の点が残らない | `…チャンク数が減った版を書き終えると旧版の余りの点は消える`・`…置き場所が移った同じ版の再発行でも元の置き場所の点は残らない` |
| 8 | 取り込みの最中に新しい版が反映されたら古い版の点は残らない | `…取り込みの最中に新しい版が反映されたら古い版の点は残らない` |
| 9 | Qdrant の写像（フィルタ・巡り方・印の点の形・コレクションの遅延作成）と、実 Qdrant v1.18.1 でのフィルタの解釈 | `QdrantIndexVersionStoreTests`（6 件）・`IngestionIndexVersionQdrantTests`（2 件・Docker） |
| 10 | 上記は防御を外すと赤になる | 下の §変異 |
| 11 | 受け口の最悪の所要時間が実行期限に収まり、再試行の連鎖がブローカの consumer_timeout に収まる | `IngestionTimeoutTests`（既定 435 秒・境界 1782 / 1783 秒） |

### 変異（防御を外すと赤になること。2026-10-23 実測）

| 変異 | 赤になった試験 |
| --- | --- |
| 順序の門を外す（`IsStale` → false） | 1・2・4（4 件） |
| 常に先に全消しする（従前の形） | 5（2 件） |
| 先の全消しをしない | 6（2 件） |
| 書き終えた後の読み直しを外す | 8（1 件） |
| 書き終えた後の旧版の削除を外す | 7（2 件） |
| 撤収の門で印を書かない | 4 の撤収（1 件） |
| 旧版のフィルタを `Gt` → `Gte`（実 Qdrant） | `IngestionIndexVersionQdrantTests` の旧版の削除（1 件） |

## 限界（残余）

- **`UpdatedAt` を進めない変更の順序は判定できない**（露出トグル・共有の付与と取り消し・タグの改名・再発行）。等しい版として届くので、その間の到着順の逆転は従前どおり後着が勝つ。全順序の版番号を契約に足す変更が要る（範囲外。未決事項）。
- **発行側の時計**: 版は DocumentService の `UtcNow`。レプリカ間の時計の差が更新の間隔より大きいと、新しい更新が古いと判定され得る（GraphService と同じ前提）。
- **並行の窓**: 印の読み書きは比較交換ではない。同じ文書の 2 版を並行に取り込むとき、印の記録の後先が逆転する窓は残る。書き終えた後の読み直しで大半を塞ぐが、同じチャンク ID を古い版が上書きした後に消すと、新しい版の該当チャンクが次の更新まで欠ける（緩む側ではなく欠ける側へ倒れる）。既定の配備（1 レプリカ・受信は Inline）では並行は起きない。
- **総枠の使い切り**（デッドレターへ直行）で途中まで書いた版は、旧版の残りと混ざったまま次の更新まで残る（判定軸は同じ。従前は旧版が消えて途中までの新版だけが残った）。
- **移行**: 配備時点で印を持つ文書は無い。最初の更新は「判定軸が不明」として従前どおり先に消す。印を一斉に付けるには再発行（`scripts/republish-document-updated.js` ＝ `POST /documents/republish-updated`）を流す（任意。public / internal は埋め込みの費用がかかる。PR 本文の配備手順）。

## 影響範囲

- コード: `IngestionService`（`Domain/DocumentIndexVersion.cs` 新設・`Domain/Ports/IIngestionVectorStore.cs`・`Features/Ingestion/Ingest/DocumentUpdatedConsumer.cs`・`IngestionTimeouts.cs`・`Infrastructure/ExternalServices/QdrantIngestionVectorStore.cs`・`Program.cs` の注記）。
- 試験: `IngestionService.Tests`（新設 2・偽物 7 つに口を足す・時間の上限の値）、`Knowledge.IntegrationTests`（新設 1・偽物 2 つ）。
- 文書: `docs/functional/FR-02_ingestion.md`（処理フロー・§順序と原子性）・`docs/tests/FR-02_ingestion.md`（T-21 の値・T-55〜T-63・I-14）。
- 配備: Qdrant にコレクションが 1 本増える（最初の取り込みで作られる）。受け口の実行期限の既定 420 → 435 秒（構成で上書きしていなければ自動）。helm・pipeline.json の変更なし。

## 検証

- `dotnet build` / `dotnet test`（IngestionService.Tests 全件・Knowledge.IntegrationTests の関係分〔実 Qdrant・実ブローカを含む〕）・`dotnet format --verify-no-changes`・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・文書系の検査器。結果は PR 本文に記録する。
