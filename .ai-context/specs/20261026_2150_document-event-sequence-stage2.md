---
title: 作業仕様書 — 受け手 3 つ（取り込み・グラフ・Wiki）の順序の判定に発行の連番を入れる（#2150 / #2251 段 2）
type: spec
status: done
related_ids: [FR-02, FR-13, FR-17, FR-19, ADR-0027, ADR-0061, ADR-0142, IADR-0637, IADR-0595, IADR-0596, IADR-0636]
author: claude
created: 2026-10-26
updated: 2026-10-26
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-02・FR-13・FR-17・FR-19）
---

# 仕様書: 受け手の順序の判定に発行の連番を入れる（#2150 / #2251 段 2）

## 起点となる計画書（トレーサビリティ）

- 要求: FR-02（取り込み・索引）、FR-13（Wiki 同期）、FR-17（グラフ）、FR-19（個人資料の露出・論理削除）
- 計画 ADR: ADR-0027（少なくとも 1 回の配送。到着順は保証しない）、ADR-0061（露出）、ADR-0142（論理削除の撤収）。順序の鍵を定める計画 ADR は無い（段 1a と同じ）
- 実装 ADR: IADR-0637（段 1。発行の連番）、IADR-0595（取り込みの版の印）、IADR-0596（Wiki の版の印）、IADR-0636（論理削除の撤収）
- issue: #2150・#2251

## 判定規則（コーディネーターの判断 2026-10-26: 案 (a)）

- **比較の鍵は辞書順 (時刻, 連番)** とする。時刻は `DocumentUpdated.UpdatedAt` で、削除なら `DocumentDeleted.DeletedAt` を使う。連番は `Sequence` である。
  1. まず時刻で比べる。比べる前に、受け手の保存の精度へ丸める（取り込み: ミリ秒・Wiki とグラフ: マイクロ秒）。
  2. 時刻が等しく、**双方に連番があれば**連番で比べる。
  3. どちらかの連番が null（旧形式の電文・移行前の印）なら、時刻だけで比べる（等しければ反映する。現行と同じ）。
- 到着したイベントは、印より**厳密に古い**ときだけ捨てる。等しい（再配送・再発行）なら反映する（冪等）。
- 比較は `Knowledge.Contracts/Ordering/DocumentEventOrder.cs`（名前空間 `Knowledge.Contracts.Ordering`）の 1 か所に置く。
  - `Events/` に置かない。`check-event-topology.js` が `*.Contracts/Events/*.cs` をイベント型として数え、baseline に無いイベントとして落ちる（#2264 の CI で実測）。
- **削除の鍵は (DeletedAt, Sequence)** とする。
  - `DeletedAt` は削除の時刻で、最後の `UpdatedAt` 以上である（更新の時刻は削除より前にしか付かない）。`Sequence` は最後の連番 + 1 である。
  - そのため、削除はそれまでのどの更新よりも後に並ぶ。
  - 削除の後に同じ ID で作り直された文書は、削除より新しい `UpdatedAt` を持つので、削除より後に並ぶ（作り直しが勝つ）。
  - 削除の印を持つとき、削除と**同じ鍵**の更新は古い扱いにする（削除は寿命の最後の出来事である。IADR-0596 決定 2 の現行と同じ）。
- 改訂の記録: IADR-0637 決定 3 の「双方に Sequence があれば Sequence」は、**IADR-0638** で改める。0637 には日付つきの追記で改訂先を書き、旧の文は残す。
  - 根拠は、文書 ID の再利用（IADR-0596 決定 2。変換が ID を決定的に採番する）である。
  - 作り直した文書の連番は 0 から始まる。連番を第 1 の鍵にすると、作り直した文書の更新が削除の印より永久に古く読まれる。
- 退けた案 (b): (寿命〔CreatedAt〕, 連番)。時計の後退に強いが、契約の追加と段 1 の追補が要る。現行も同じ弱点を持つので採らない。
  - **残余**: Touch した側のレプリカの時計が戻ると、その更新は古いと読まれる。時計の後退が実測されたら (b) へ移す（IADR-0638 に条件つきで記録する）。

### 規則 11 の表（行＝形、列＝プローブ。〇＝期待どおり、×＝期待と違う）

プローブの定義（どれも、印が先に反映した状態を持ち、後から別のイベントが届く）:
- **P1 同時刻の逆転**: 印 (T, 6)。後着 (T, 5)。古いので捨てるのが期待（#2150 の根）。
- **P2 作り直し**: 印は削除 (Td, 9)。後着は作り直した文書の更新 (T2 > Td, 1)。反映するのが期待。
- **P3 遅延した古い ON・OFF**: 印は OFF (T, 6)（撤収）。後着 ON (T, 5) は捨てるのが期待（#2251 の根）。逆に、印 ON (T, 6)・後着 OFF (T, 5) も捨てる（新しい版を撤収しない）。
- **P4 null の混在**: 印 (T, null)・後着 (T, 3) は反映する。印 (T, 3)・後着 (T, null) も反映する。後着 (T−1, 9) は捨てる。どれも現行と同じ。
- **P5 削除と更新の交差**: 印は削除 (Td, 9)。後着の古い更新 (T ≤ Td, ≤ 8) は捨てる。印は作り直しの更新 (T2, 1)。後着の古い削除 (Td < T2, 9) は捨てる。印は更新 (T, 8)。後着の削除 (Td ≥ T, 9) は適用する。

| 形 | P1 | P2 | P3 | P4 | P5 |
| --- | --- | --- | --- | --- | --- |
| 時刻だけ（現行） | ×（等しいので反映） | 〇 | ×（等しいので反映。グラフは印が無く、どの順でも × ） | 〇 | 〇（同時刻だけ ×） |
| 連番が先（指示の原案） | 〇 | **×（作り直しが永久に古い）** | 〇 | 〇 | **×（作り直しの後の削除の判定が逆になる）** |
| **辞書順 (時刻, 連番)（採用）** | 〇 | 〇（削除と同じミリ秒の作り直しだけ ×。現行の Wiki と同じ） | 〇（グラフは墓石の表が要る） | 〇 | 〇 |

## 母集合（規則 9。根の語で全文を走査した）

- 引いた語: `UpdatedAt` の大小比較（`<` `>` `<=` `>=`）・`IsStale`・`TryApply`・`SyncMark`・`IndexVersion`・`last-write`・`DeletedAt` の比較。
- 対象: `src/knowledge/backend/Services/*/{Domain,Features,Infrastructure,Common}`（試験と migration を除く）。DocumentService（発行側）は除く。

| 受け手 | 箇所 | 今の判定 | 扱い |
| --- | --- | --- | --- |
| 取り込み（Ingestion） | `Domain/DocumentIndexVersion.cs` `DocumentIndexOrdering.IsStale` | 版の印の UpdatedAt（ms）より厳密に古ければ捨てる | 直す（印に連番を持つ） |
| 取り込み | `DocumentUpdatedConsumer.FinishAsync`（書き終えた後の読み直し） | 印の UpdatedAt が自分より新しければ追い越された | 直す（同じ規則） |
| 取り込み | `QdrantIngestionVectorStore.BuildSupersededFilter`（旧版の点の削除） | 点の `updated_at` が自分以下の点を消す | 直す（点に `sequence` を載せる。同じ UpdatedAt の新しい版の点を消さない） |
| 取り込み | 版の印の保存（`BuildIndexVersionPoint` / `ReadIndexVersion`） | `updated_at` と判定軸の指紋 | 直す（`sequence` を足す。読めなければ null） |
| グラフ（Graph） | `Domain/GraphDocument.TryApply` | ノードの UpdatedAt より古ければ適用しない | 直す |
| グラフ | `GraphDocumentSyncConsumer.WithdrawAsync`（露出 OFF・アーカイブ） | **判定なし**。ノードを消し、印が残らない | 直す（撤収・削除の後も残る印の表を新設する。migration 1 本） |
| グラフ | `Delete/DocumentDeletedConsumer` | **判定なし** | 直す（同じ印で古い削除を捨てる） |
| Wiki | `Domain/DocumentSyncMark.cs` `IsStaleUpdate` / `IsStaleDelete` | 印の AppliedAt（µs）と削除済みの旗 | 直す（`AppliedSequence` を足す。migration 1 本） |
| Wiki | `DocumentSyncConsumer.RecordApplied`・`DocumentDeletedConsumer`・`WikiSyncOrdering.ReconcileIfOvertakenAsync` の述語 | 上の 2 つの述語を使う | 述語を直せば追随する（呼び出しの引数に連番を足す） |

除外した箇所と理由:

| 箇所 | 理由 |
| --- | --- |
| Retrieval `RemoveDeleted/DocumentDeletedConsumer` | 削除の受け口だけで、順序の判定を持たない（点を消すだけで、再作成は取り込みの印が決める） |
| DataSource の `SaaSConnector` / `WikiConnector` の `UpdatedAt <= watermark` | 外部の取り込み元の差分取得の水位で、`DocumentUpdated` の受け手ではない |
| Conversion・Feedback の `OrderByDescending(UpdatedAt)` | 一覧の並びで、順序の判定ではない |
| Retrieval `HybridSearchService` の `UpdatedAt` の並べ替え | 検索結果の並び（表示）で、反映の判定ではない |
| Graph `KnowledgeHealthCollector` の `UpdatedAt` / `BodyUpdatedAt` | 鮮度の指標の集計で、反映の判定ではない |

## 受け手ごとの設計

### グラフ（Graph）

- **墓石の表 `graph_document_sync_marks` を新設する**（migration 1 本）。
  - 列は 4 つ: `document_id`（主キー）・`applied_at`（timestamptz）・`applied_sequence`（bigint, null 可）・`deleted`（bool）。
  - 適用・撤収（露出 OFF・アーカイブ）・削除のたびに、同じトランザクションで書く。ノードの行を消しても残る。
- **判定の位置**: 受け口の冒頭で、文書の排他（`ITagEdgeLocks.LockDocumentAsync`。トランザクションの寿命の advisory lock）を取る。
  - そのうえで印を読み、古ければ ack して何もしない。
  - 印の読み・判定・書きを同じ文書の他の通と直列化する（レプリカ・並列度に依らない）。
  - 同じトランザクションで同じ鍵を取り直しても待ち合わない（PostgreSQL の advisory lock は同じセッションで再入できる）。
- `GraphDocument.TryApply` の比較は、印の判定へ移す（ノードの `UpdatedAt` は表示・鮮度の指標に使い続ける）。
- **保持期間: 無期限とし、掃除しない。**
  - 行は文書 1 件につき 1 行で、文書 ID・時刻・連番・旗だけを持つ（題名・属性を持たない。ADR-0057 決定 2 が許す「削除の事実の記録」と同じ性質）。
  - Wiki の印（IADR-0596）・取り込みの版の印（IADR-0595）も無期限である。
  - 消すと、遅れて届いた古い更新でノードが作り直される（墓石の意味が無くなる）。
  - **行数の見積もり**: これまでに受けた文書の数で上限が決まる。PoC は 22,564 件（#1762 の実測）で、1 行は約 60 バイトなので、索引を含めて 3 MB 未満である。
- 印が無い（移行前の文書）ときは反映する（現行と同じ）。初回の受信で印ができる。
- **排他の鍵の衝突（コーディネーターの条件 2）**:
  - 文書の排他は既存の `PostgresTagEdgeLocks.LockDocumentAsync` を使う。鍵は `graph-doc:<文書 ID>` を `hashtextextended(…, 0)` で 64 ビットへ畳んだ値である。
  - 新しい鍵の空間は作らない（同じ排他を、受け口の冒頭で先に取るだけである）。
  - 64 ビットの空間で別の文書と偶然一致しても、その 2 通が直列化されるだけで、正しさは崩れない（誤って通すことはない）。
  - 既存の鍵の空間との重なり:
    - タグ（`graph-tag:` の接頭辞）とは、接頭辞が違う文字列を畳むので、偶然の一致しか無い。
    - リース（クラスタ検出・要約・健全性）は固定の鍵で、セッション寿命の `pg_try_advisory_lock` である。関数も寿命も別で、一致しても余分に待つだけである（`TagEdgeLocks.cs` の注記と同じ）。

### 取り込み（Ingestion）

- 版の印（Qdrant のコレクション `knowledge_document_index_versions`）のペイロードに `sequence` を足す。読めなければ null とする。migration は無い。
- 点のペイロードにも `sequence` を足す（連番がある電文のときだけ）。
- **旧版の点の削除（`DeleteSupersededAsync`）を同じ規則に揃える**: 消すのは、この版より厳密に新しくない点である。
  - 点の時刻 < この版の時刻、または
  - 点の時刻 = この版の時刻で、点の連番 ≤ この版の連番、または
  - 点の時刻 = この版の時刻で、点かこの版のどちらかに連番が無い（旧点。現行と同じに消す）。
  - Qdrant のフィルタ: `updated_at > v` を残す。加えて、両方に連番があるときは `updated_at = v かつ sequence > s` を残す。
- **旧点（`sequence` の無い点）の扱い**: 時刻だけで比べる（現行と同じ）。試験で表明する。
- 書き終えた後の読み直し（`FinishAsync`）も同じ規則にする（追い越されたら、自分の版以下の点を消す）。

### Wiki

- 印 `DocumentSyncMark` に `AppliedSequence`（bigint, null 可）を足す（migration 1 本）。
- 更新と削除の判定（`IsStaleUpdate` / `IsStaleDelete`）を、上の規則（削除は (DeletedAt, Sequence)）にする。
- 突き合わせの再確認（`ReconcileIfOvertakenAsync` の述語）も同じ関数を通る。

## PR の分け方（判断）

- **1 本の PR、受け手ごとの commit** とする。commit は 4 つ: IADR-0638 と作業仕様書・グラフ・取り込み・Wiki。
- 理由:
  - 3 つの受け手は同じ規則（IADR-0638）と同じ作業仕様書を引く。分けると、後の 2 本が先の 1 本に積む形になる（文書の参照の実在の検査が in-repo を見るため）。
  - Integration の実行が 1 回で済む。
  - 見積もりは、試験を含めて約 1,500 行である。レビューで大きすぎると言われたら、受け手ごとに分ける（グラフ → 取り込み → Wiki の順。グラフがいちばん大きく、いまも fail-open の窓を持つため先にする）。

## 規則 11（窓の両側のプローブ）の試験

- 上の表の P1〜P5 を、受け手ごとに試験で表明する。
- 変異（辞書順を「時刻だけ」へ戻す・「連番が先」へ替える）で、P1・P3 と P2・P5 がそれぞれ赤になることを確かめる。

## 配備

- 段 2 は、1a〜1c を配備した上に載る。段 2 のマージの後に、段 2 だけを別に配備する（コーディネーターの判断 2026-10-26）。
- migration は 2 本ある（グラフの印の表の新設・Wiki の印の列の追加）。どちらも他の migration との適用順に依存しない。

## 変異（コーディネーターの条件 1）

| 変異 | 期待して赤になる升目 |
| --- | --- |
| 辞書順を「連番が先」へ戻す（`DocumentEventOrder.Compare`） | 3 つの受け手の P2・P5（作り直し） |
| 辞書順を「時刻だけ」へ戻す（`DocumentEventOrder.Compare`） | 3 つの受け手の P1・P3（同時刻の逆転） |
| 保存の精度への丸めを外す（受け手ごと） | その受け手の P1・P3（同時刻を、ティックの端数で新しいと読む） |

## 残余

- 削除と同じミリ秒（Wiki・グラフはマイクロ秒）に作り直された文書の更新は、削除と同じ鍵になり古い扱いになる（現行の Wiki と同じ）。
- 取り込みは `DocumentDeleted` を受けない（点の削除は Retrieval が行う）。削除の後に、最後の更新の再配送が届くと、取り込み直す。
  - この窓は #2150 / #2251 とは別で、現行からある。本段の範囲外とし、#2263 に起票した（同件を検索し、#2101・#2137 は別件と確かめた）。
- Touch した側の時計の後退（上の (b) の条件）。
