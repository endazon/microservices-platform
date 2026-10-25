---
title: 作業仕様書 — 取り込みが DocumentDeleted を受けて版の印を削除済みの墓石にし、削除の後に届いた古い更新で索引へ取り込み直さない（#2263）
type: spec
status: done
related_ids: [FR-02, FR-06, FR-19, ADR-0027, ADR-0057, IADR-0595, IADR-0637, IADR-0639, IADR-0640]
author: claude
created: 2026-10-26
updated: 2026-10-26
issue: "#2263"
---

# 作業仕様書 — 取り込みの削除の墓石（#2263）

> 本仕様書は実装着手前に作成した（着手 2026-10-26、MSP `origin/develop` `69635020`）。判断は [IADR-0640](../adr/IADR-0640_ingestion-delete-tombstone.md) に残す。
> #2150 段 2（#2264・IADR-0639）の残余として起票した窓を閉じる。

## 起点（トレーサビリティ）

- FR-02（取り込み）/ FR-06・FR-19（削除）/ ADR-0027（少なくとも 1 回の配送。到着順は保証しない）/ ADR-0057（削除の伝播）。
- 前提: IADR-0595（取り込みの版の印）・IADR-0637（発行の連番。削除は最後の値 + 1）・IADR-0639（受け手の順序の鍵は辞書順 (時刻, 連番)）。

## 窓（現状）

- 文書の削除で、検索の索引の点を消すのは Retrieval の `retrieval-delete` 段である。取り込み（IngestionService）は `DocumentDeleted` を受けない。
- 取り込みの版の印（Qdrant の `knowledge_document_index_versions`、1 文書 1 点）は、削除の事実を持たない。
  - Retrieval の削除は検索のコレクションから点を消すだけで、この印のコレクションには触れない。
- そのため、削除の後に、削除の前の最後の `DocumentUpdated` が再配送されると、取り込みは「印より古くない」と読んで取り込み直す。削除した文書の点が、索引に戻る。
- グラフ（`graph_document_sync_marks`）と Wiki（`DocumentSyncMark`）は削除の墓石を持つので、この窓は無い（IADR-0639）。取り込みだけに残っていた。

## 方針（グラフの規則に揃える）

1. 取り込みに `ingestion-delete` 段（`IngestionService.Features.Ingestion.RemoveDeleted.DocumentDeletedConsumer`）を足す。
   - `DocumentDeleted` を購読し、版の印を **削除済みの墓石** にする（`DocumentIndexVersion` に `Deleted` を足す。時刻は `DeletedAt`、連番は削除の `Sequence`、判定軸の指紋は空）。
   - 点は取り込みが書く全コレクションから消す（`DeleteByDocumentFromAllAsync`。撤収の門と同じ）。Retrieval の削除と重なるが、どちらも冪等である。
   - 消してから印を書く（撤収の門と同じ順。印を先に書いて削除が落ちると、再試行は「印と等しい削除」と読んでやり直す。どちらの順でも収束するが、既存に揃える）。
2. 順序の規則は `DocumentEventOrder` とグラフの `GraphSyncOrdering` と同じにする（時刻は保存の精度＝ミリ秒へ丸める）。
   - **古い削除**（印より厳密に古い。作り直した文書の版を反映した後に遅れて届いた削除）は適用しない。
   - **更新**: 印より厳密に古いものは捨てる。加えて、**印が削除済みなら、鍵が等しい更新も捨てる**（削除は寿命の最後の出来事である。グラフの `IsStaleUpdate` と同じ）。
   - 書き終えた後の読み直し（`FinishAsync`）も同じ述語で判定する。墓石と等しい鍵の版の点を残さない。
3. **取り込みの受け口と削除の受け口を、文書ごとに直列にする**（プロセス内の文書ごとの排他）。
   - 2 つの受け口は別のキューで、同じプロセスの中で並行に動き得る。
   - 排他が無いと、更新の受け口が門を通った後・自分の印を書く前に墓石が書かれた場合、更新の受け口が墓石を自分の印で上書きする。比較交換が無いからである。そのまま点を書き、読み直しも自分の印を読むので、削除した文書が戻る。
   - 既定の配備は 1 レプリカである（IADR-0595 の残余「複数レプリカ・並行受信の前に解消する」は変わらない）。
4. 削除の受け口の実行期限は、取り込みの受け口の実行期限（既定 435 秒）と同じにする。
   - 排他の待ちは、取り込みの受け口 1 回分を超えない。
   - 再試行の鎖も同じ数値なので、ブローカの未確認の上限（1800 秒）に収まる（`EnsureRetryChainFits` と同じ式）。
5. migration は要らない。
   - 版の印は Qdrant のペイロードで、`deleted` のキーを足すだけである。旧い印（キーが無い）は削除済みでないと読む。
   - 印のコレクションは、従前どおり無ければ作る（`SetIndexVersionAsync`）。起動時の準備（`QdrantBootstrapHostedService`）は変えない。
6. 宣言（pipeline.json の段）・Wolverine の購読・自己申告・イベントの配線の baseline（`event-topology-baseline.json`）を追随させる。

## 窓（規則 11）

窓は「削除の印（墓石）」と「その前後に届く更新・削除」の間にある。形を確定する前に、両側のプローブで 4 つの形を比べた。

プローブ（印・後着の順。T は時刻、括弧は連番）:

- **Q1（削除の後の古い更新）**: 更新 (T, 8) → 削除 (Td ≥ T, 9) → 更新 (T, 8) の再配送。期待は、取り込み直さない。
- **Q2（等しい鍵・連番なし）**: 削除 (T, null) → 更新 (T, null)（旧形式の電文・同じミリ秒）。期待は、取り込み直さない（削除済みの印と等しい更新は古い）。
- **Q3（作り直し）**: 削除 (Td, 9) → 同じ ID の新しい更新 (T2 > Td, 1)。期待は、取り込む。
- **Q4（作り直しの後の古い削除）**: 更新 (T2, 1) → 削除 (Td < T2, 9) の遅延。期待は、適用しない（作り直した文書を消さない）。
- **Q5（削除の再配送）**: 削除 (Td, 9) → 削除 (Td, 9)。期待は、適用する（冪等。墓石のまま）。
- **Q6（交差）**: 更新が門を通った後、点を書く前に削除が届く。期待は、両方が終わった後に点が残らない。
- **Q7（陽性対照）**: 印が無い（初回）更新。期待は、取り込む。

| 形 | Q1 | Q2 | Q3 | Q4 | Q5 | Q6 | Q7 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 墓石なし（現状） | × | × | 〇 | 〇（削除を受けない） | 〇 | × | 〇 |
| 墓石あり・削除済みの旗なし（等しい鍵は反映する） | 〇 | × | 〇 | 〇 | 〇 | × | 〇 |
| 墓石あり・旗あり・削除に順序の門なし | 〇 | 〇 | 〇 | ×（古い削除が作り直しを消す） | 〇 | × | 〇 |
| **墓石・旗・削除の門・文書ごとの排他（採用）** | 〇 | 〇 | 〇 | 〇 | 〇 | 〇 | 〇 |

→ 最後の形を採る。試験は Q1〜Q7 を写し、試験名に Q 番号を入れる。変異（旗を外す・削除の門を外す・排他を外す）で、それぞれ Q2・Q4・Q6 が赤になることを確かめる。

## 母集合（規則 9）

- 削除の段の宣言の置き場所は、既存の `graph-delete` / `retrieval-delete` を `git grep -n -E "graph-delete|GraphDocuments\.Delete\.DocumentDeletedConsumer"` で引いて決めた。
  - `deploy/helm/microservices-platform/files/pipeline.json`（段の宣言の正本）
  - 受け口の `StepName`
  - `Program.cs`（`AddPlatformWolverineStep` / `BindPlatformQueue` / `ListenToPlatformQueue` / 自己申告）
  - 試験の器（Wolverine の外部トランスポートを落とす）
  - `scripts/event-topology-baseline.json`（`DocumentDeleted` の購読者）
  - helm の values と compose は、取り込みが既に RabbitMQ と pipeline-config を持つので、変更は要らない（確かめた）。
- 版の印を読む・書く箇所は `git grep -n "IndexVersion" -- 'src/knowledge/backend/Services/IngestionService/**'` で引いた。
  - `DocumentIndexVersion`・`DocumentIndexOrdering`・`QdrantIngestionVectorStore`（`BuildIndexVersionPoint` / `ReadIndexVersion`）・`DocumentUpdatedConsumer`（門・各撤収・`FinishAsync`）。
  - 試験の偽物（`StatefulIndex`）と統合試験の偽物も、その対象に入る。
- 順序の規則の共有元は `Knowledge.Contracts.Ordering.DocumentEventOrder`（IADR-0639）と、グラフの `GraphSyncOrdering`（同じ形を写す）である。

## 試験

- 純関数（`DocumentIndexOrdering`）: 削除済みの印と等しい鍵の更新は古い・古い削除・等しい削除・連番なし。
- 受け口（偽物の索引 `StatefulIndex`）: Q1〜Q7。
  - Q6 は、更新の受け口の点の書き込みの直前に削除の受け口を割り込ませる。排他があれば、削除は更新の終わりを待つ。両方の後に点が無く、印が墓石であることを見る。
- Qdrant の印の点: `deleted` の往復と、旧い点（キーなし）を削除済みでないと読むこと。
- 宣言: pipeline.json に段があり、取り込みのホストが起動時の突合を通る（既存の宣言の試験の延長）。

## 結果（実測）

- IngestionService.Tests は 279 件 緑（新規 11 件: Q1〜Q7・墓石の判定の純関数・印の往復・排他 2 件）。
- Knowledge.IntegrationTests はビルドが通る（警告 0）。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`: 1202 件 緑。
- pipeline.json の検証・event-topology（baseline を更新）・contract-schema・文書の検査が緑。

| 変異 | 赤になった試験 |
| --- | --- |
| 墓石の旗を見ない（等しい鍵の更新を反映する） | Q2・墓石の判定の純関数 |
| 削除の順序の門を外す | Q4 |
| 削除の受け口の排他を外す | Q6 |
| 戻した後 | 11 件すべて緑 |

- 修正前の形は、取り込みが `DocumentDeleted` を受けない（新しい受け口が無い）ので、新しい試験はコンパイルできない。
  - 既存の `削除で点が消えた後に古い版が届いても点は書き戻されない` は、削除の前の版より**古い**版だけを見ていた。
  - 削除の前の**最後の**版の再配送（Q1）は、修正前は印と等しいので取り込み直していた。

## 残余

- 墓石を持つ前（本 PR の配備より前）に削除された文書は、墓石が無い。
  - その文書の古い更新が後から届くと、従前どおり取り込み直す（印が無い・削除済みでない）。
  - 頻度は低い（削除の後の再配送）。直すには、過去の削除を DocumentService から引き直して墓石を書く一回限りの操作が要る。本件の範囲外とする。
- 複数レプリカ・並行受信にすると、プロセス内の排他は効かない（IADR-0595 の残余と同じ。その前に比較交換か分散の排他が要る）。
- 墓石は掃除しない（無期限）。1 文書 1 点で、版の印と同じ件数（IADR-0639 決定 2 の容量の見積もりと同じ桁）。
