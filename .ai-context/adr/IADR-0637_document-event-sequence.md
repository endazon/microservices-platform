---
title: IADR-0637 文書ごとの単調な発行の連番（Document.EventSequence）を唯一の発行点で進めて DocumentUpdated / DocumentDeleted の末尾に運び、連番そのものを並行トークンにする。新たに負け得る単一文書の経路と完全削除は 409 version_conflict へ写し、多数文書の 2 経路と既存の 8 経路は段 1b・1c、受け手の比較は段 2 に分ける
type: impl-adr
status: Accepted
related_ids: [FR-02, FR-06, FR-09, FR-18, FR-19, FR-20, SC-09, SC-19, SC-20, ADR-0027, ADR-0057, ADR-0061, ADR-0142, IADR-0122, IADR-0283, IADR-0296, IADR-0455, IADR-0595, IADR-0596, IADR-0597, IADR-0604, IADR-0608, IADR-0636]
author: claude
created: 2026-10-26
updated: 2026-10-26
related_specs:
  - ../specs/20261026_2150_document-event-sequence-stage1a.md
  - ../specs/20261026_2150_document-event-sequence-stage1b.md
  - ../specs/20261026_2150_document-event-sequence-stage1c.md
---

# IADR-0637: 文書ごとの単調な発行の連番を契約に足し、並行トークンにする（#2150 / #2251）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-26
- 決定者: claude（#2150 / #2251。案 A と案 (i)、範囲の判断 (a)、段 1a・1b・1c・2 の分割はコーディネーターの判断 2026-10-26）

## 起点・関連

- 計画:
  - FR-02 / FR-06 / FR-19 / FR-20。
  - ADR-0027（Wolverine。少なくとも 1 回の配送と再試行。到着順は保証しない）。
  - 🔴 **順序の鍵を定める計画 ADR は無い。** 受け手が何で順序を決めるかは、実装の決定である（取り込み IADR-0595・Wiki と目録 IADR-0596・グラフ `GraphDocument.TryApply`）。本 IADR はその鍵を置き換える土台であり、計画への環流は要らない。
- 前提とする実装 ADR:
  - [IADR-0455](./IADR-0455_publish-gate-all-paths-withdrawal-and-private-note-scope.md): `DocumentUpdated` の発行はすべて唯一の発行点を通る。
  - [IADR-0608](./IADR-0608_transactional-outbox-document-updated.md) / [IADR-0604](./IADR-0604_transactional-outbox-document-deleted.md): 発行は確定と同じトランザクションに積む。
  - [IADR-0597](./IADR-0597_document-version-optimistic-concurrency.md): `Version` の並行トークンと 409 `version_conflict` の唯一の写し点 `DocumentVersionConflict`。
  - [IADR-0122](./IADR-0122_contract-schema-source-and-compat-gate.md) 決定 2: 契約の追加は末尾・既定値つき。
- 本 IADR が置き換える土台になるもの:
  - [IADR-0595](./IADR-0595_ingestion-index-version-and-write-then-delete.md): 取り込みの版の印。`UpdatedAt` で比べ、等しい版は反映する。
  - [IADR-0596](./IADR-0596_wiki-catalog-receiver-ordering.md): Wiki・目録の受け手の順序。
  - [IADR-0636](./IADR-0636_private-note-soft-delete-withdrawal.md): 論理削除の撤収と復元の載せ直しは版を進めない。残余リスク「順序」が #2251 である。
- 作業仕様書: [20261026_2150_document-event-sequence-stage1a](../specs/20261026_2150_document-event-sequence-stage1a.md)。

## コンテキストと課題

- 受け手は `DocumentUpdated.UpdatedAt` で順序を決め、等しい版は反映する（再配送と再発行が同じ版を運ぶため）。
- ところが、次の変化は `Touch()` しないので、`UpdatedAt` を進めずに発行する。
  - 露出（[IADR-0283](./IADR-0283_rag-context-ai-input-exposure.md) 決定 4: 版履歴と Obsidian 同期の `baseVersion` を動かさない）
  - 共有の付与・取り消し
  - 論理削除・復元（IADR-0636）
  - タグの改名・再発行
- これらの間では、到着順の逆転を受け手が見分けられず、後着が勝つ（#2150）。
- 再試行の待ち（2・10・30 秒）で逆転すると、外したはずの個人資料が索引・グラフへ載り直す。露出する向き（fail-open）に倒れる（#2251）。

## 検討した選択肢

### 順序の鍵

- A. **文書ごとの単調な発行の連番を契約に足す（採用）。**
  - 発行のたびに +1 する。版の意味（版履歴・`baseVersion`・If-Match）を変えない。
- B. 状態の変化でも `UpdatedAt` を進める。採らない。
  - `UpdatedAt` は画面の「最終更新」と Obsidian 同期の判定材料でもある。
  - 版を進めない理由（IADR-0283 決定 4）と衝突する。

### 連番の並行（A の前提を確かめて崩れた点）

- いまの並行トークンは `Version` だけである。状態の変化は `Version` を進めない。
- そのため、並行する 2 つの状態の変化が同じ連番 S を読んで S+1 を書くと、どちらの `UPDATE … WHERE Version = V` も成功する。同じ連番が 2 つの違う状態に付く。

| 案 | 中身 | 正しさ | 範囲 | 判断 |
| --- | --- | --- | --- | --- |
| **(i)** | `EventSequence` 自体を EF の並行トークンにする。後から確定する側は確定ごと負ける（発行も出ない） | 正しい | 新たに負け得る経路を 409 へ写す改修（下の表） | **採用** |
| (ii) | DB 側の原子的な +1（`UPDATE … SET seq = seq + 1 RETURNING`） | **正しくない**。後から確定した側が、確定の前に読んだ古い状態に大きい連番を付けて出し得る（例: 露出 ON の確定の後に、ON を読む前の共有の変更が、後の連番で OFF を運ぶ） | 小さい | 採らない |
| (iii) | 各確定の冒頭で行をロックして読み直す（`SELECT … FOR UPDATE`） | 正しい | 経路ごとに「読む位置」を確定の中へ移す改修。発行点を呼ぶ 19 経路すべてに及ぶ | 採らない |

## 決定

### 決定 1: 連番は唯一の発行点で進め、確定の中で保存する

- `Document.EventSequence`（`bigint NOT NULL DEFAULT 0`）を足す。
- `DocumentEndpoints.PublishUpdatedAsync`（唯一の発行点。IADR-0455）が `AdvanceEventSequence()` で +1 し、同じ確定（Outbox のトランザクション）で保存する。その値を `DocumentUpdated.Sequence` に運ぶ。
- 追跡されていない実体で呼ばれたら、例外で止める。保存されないまま進めると、次の発行が同じ値を使う。
- 既存の行は 0 から始める（遡及付与しない）。旧い発行は連番を持たない。

### 決定 2: 削除は最後の値 + 1 を運ぶ

- `DocumentDeleted.Sequence` は、消す前に読んだ `EventSequence + 1` である。
  - 3 経路とも、消す前に行を読んでいる。
  - Wiki は削除と更新を同じ印で比べる（段 2）。
- 完全削除で行が見つからない id は null を運ぶ（旧形式と同じ扱い）。

### 決定 3: 契約は末尾・既定値つきの追加とする

- `DocumentUpdated` と `DocumentDeleted` の末尾に `long? Sequence = null` を足す（IADR-0122 決定 2。`check-contract-schema` は非破壊 2 件）。
- 旧い電文（outbox に残った発行・旧い送り手）は null として読める。
- 段 2 の受け手は、両方に連番があれば連番で比べ、どちらかに無ければ `UpdatedAt` で比べる。
- ［2026-10-26 追記 / #2150 段 2］**この項は [IADR-0639](./IADR-0639_receiver-ordering-by-time-then-sequence.md) で改訂した。** 受け手は辞書順 (時刻, 連番) で比べ、連番を第 1 の鍵にしない。文書 ID の再利用（IADR-0596 決定 2）で、作り直した文書の連番が 0 へ戻るためである。上の文は記録として残す。

### 決定 4: 連番は並行トークンである（案 (i)）

- 読んだ後に同じ文書へ別の発行（または版を進める更新）が確定していれば、後から確定する側は確定ごと巻き戻る。発行も出ない。
- 新たに負け得る経路のうち、次を **#2103 と同じ形の 409 `version_conflict`** へ写す（`DocumentVersionConflict.AfterLostSaveAsync`）。

| 経路 | 1a の前に負けた相手 | 1a の後に負けた側の応答 |
| --- | --- | --- |
| 共有の付与・取り消し | 負けなかった | 409 |
| SC-19 の論理削除・Obsidian 側の削除・復元（`SaveDeletionStateAsync`） | 負けなかった | 409 |
| Obsidian の既存ノートの更新（Push。`expectedVersion` = `baseVersion`） | Touch する更新（500） | 409 |
| 個人資料の完全削除（Purge。複数件） | Touch する更新（500） | **id なしの 409**（`DocumentVersionConflict.ProblemForBatch`。全件の削除と発行が巻き戻る） |

- 定期処理の完全削除（退職者・90 日）は、**所有者ごとに確定を隔離する**。
  - 並行トークンに負けた所有者の分は、行と発行ごと巻き戻して次周期へ回す。他の所有者と周期の後段は進める。
  - 従前は周期全体の catch しか無く、90 日の削除は全所有者を 1 回で確定していた。1a の後は 1 件の負けで周期の残りが止まるので、1a の中で直した（#2259 のレビュー）。
- Push と Purge は、最初に引いた母集合（発行の門を呼ぶ経路）から落ちていた。根の語（Document 行を確定する経路）で引き直して見つけた。範囲は判断 (a) で 1a に含めた。

### 決定 5: 段を分ける

- **1a（本 IADR の PR）**: 決定 1〜4。
- **1b**: 多数文書の 2 経路（Tags/Rename・Republish）。1 件の競合でページの確定ごと巻き戻る。
  - ページの確定で負けたら、読み直してやり直す（上限は 1b の作業仕様書で決める）。
  - 上限を超えたら、`nextCursor`（または未処理の範囲）つきの 409 を返す。部分の結果は返さない。
  - 再開が冪等であることを試験で示す。
- **1c（#2258）**: もとから 409 へ写していない 8 経路。単一文書は `DocumentVersionConflict`、複数文書は id なしの 409 にする。
  - Archive・Publish・PutBody・AddTag・SyncConflicts/Resolve・StripUntrustedBoundary・ArchiveAstQuotes・ArchiveBodyless。
  - 1a で、これらも Touch しない状態の変化に負けるようになる（窓が広がる）。そのため、別件ではなく段として続ける。
- **2**: 受け手 3 つ（取り込み・グラフ・Wiki）の印に `Sequence` と `UpdatedAt` を両方持ち、決定 3 の規則で比べる。
- **PoC への配備は 1a・1b・1c がそろってから行う。** 1a だけを配備すると、1b・1c の経路の 500 の窓が広がった状態で動く。

### 決定 6（［2026-10-26 追記 / #2150 段 1b］）: 多数の文書を 1 回で確定する口は、読み直してやり直し、上限を超えたら何も確定せずに 409 を返す

- 対象はタグの改名（`PUT /tags/{id}`）と再発行（`POST /documents/republish-updated`）である。
- 発行の連番に負けたら、追跡を捨てて読み直し、やり直す。
  - 改名は全体（タグ・重複の検査・対象の文書）、再発行はページを読み直す。
  - 上限は `DocumentVersionConflict.MaxBatchCommitAttempts = 3`（初回を含む）。
- 上限を超えたときの応答:
  - 改名: 409 `{ error: "version_conflict", message }`。SC-09 は `message` をそのまま出す。未処理の範囲は要求の全体で、同じ要求の再送で再開できる。
  - 再発行: 409 `{ error: "version_conflict", resumeCursor }`。`resumeCursor` は負けたページの先頭である。駆動スクリプトは 200 以外を失敗として数え、同じカーソルで再試行するので、変更は要らない。
  - どちらも部分の確定は無い。
- 冪等: 改名済みの名前への再送は、辞書を変えずに再発行だけを行う。再発行の重複は、受け手の版の印が吸収する。
- **改名は R1（1 トランザクションのまま）とする**（コーディネーターの判断）。
  - 前提: 改名は管理者のまれな操作である。文書行のロックは利用者の編集を失敗させず、待たせるだけである。
  - 見積もり（1 件あたり約 1〜2 ms）:
    - N（使用件数）= 1,000 → 約 1〜2 秒
    - N = 22,564 → 約 25〜45 秒
  - 件数と所要時間を構造化ログ（Information。表示名は出さない）に残し、PoC で実測する。
  - R2（バッチ化）は採らない。契約・BFF・画面の変更が要り、「辞書は改名済みなのに射影が旧名のまま」の中間状態を生むためである。
  - 発行点の保存を 1 回にまとめる案（往復を N 回からバッチ回へ減らす）も、唯一の発行点の形を変えるので、1b では採らない。
  - 使用件数が大きいと分かれば、どちらかへ移す（#2260）。
- 前提の変化: 再発行は「台帳を書き換えない」口だった（[IADR-0503](./IADR-0503_republish-document-updated-admin-endpoint-and-driver.md)）。1a の後は、発行の連番だけを書き換える。版と更新時刻は従前どおり動かない。
  - IADR-0503 は凍結の記録なので書き換えない。運用仕様書と OpenAPI の記述は直した。

### 決定 7（［2026-10-26 追記 / #2150 段 1c / #2258］）: もとから 409 へ写していなかった 8 経路を写す

- 単一文書の 5 経路は、`DocumentVersionConflict.AfterLostSaveAsync`（#2103 と同じ形）へ写す。
  - Archive・Publish・PutBody・SyncConflicts/Resolve（負け得るのは元の資料を書く `local` だけ）・AddTag。
  - AddTag は gRPC だけの口である。利用の場が `AddDocumentTagStatus.VersionConflict` を返し、応答を作る唯一の点 `Respond` が写像の前で `ABORTED` を投げる（`ToProto` は投げない）。
    - 既定の枝（`NOT_WRITABLE`）へ化けさせない。
    - proto の結果の値は足さない。
    - 呼び出し元（graph）はどの `RpcException` も 502 として承認を確定しないので、挙動は従前と同じである。
- 複数文書の 3 経路は、id なしの 409 `{ error: "version_conflict", message }`（`ProblemForBatch(BatchPageConflictMessage)`）へ写す。
  - StripUntrustedBoundary・ArchiveAstQuotes・ArchiveBodyless。
  - 読み直してやり直す処理は入れない（決定 6 と違う）。一回限りの管理操作で、同じ要求（同じカーソル）の再送で再開できるからである。
- 個人資料の完全削除（決定 4）の 409 にも `message` を載せる。本文の実体は消えている場合があるが、資料は削除済みのまま残り、再送で片付くことを伝える（#2259 のレビュー）。
- 衝突の解決は、退避したローカル本文を**確定の後に**消す。従前は確定の前に消しており、負けると未解決の衝突から本文だけが消えた（選び直すと空の本文が書かれた）。
- gRPC の `ABORTED` は、応答を作る唯一の点（`Respond`）が写像の前で投げる。写像（`ToProto`）は例外を投げない。
- 既存の性質（変えない）: PutBody と Resolve の `local` は、本文のオブジェクトを確定の前に同じキーへ上書きする。負けると台帳の指紋と食い違い得るが、本文を書き直せば揃う。
- これで、Touch しない状態の変化に新たに負ける経路は、すべて 409（または周期の次回）へ写った。**PoC への配備の条件（1a・1b・1c がそろう）を満たす。**

## 結果

- 発行は文書ごとに全順序を持つ。同じ連番が 2 つの状態に付くことは無い（並行トークンが止める）。
- 受け手はまだ連番を読まないので、挙動は変わらない。
  - Contracts の互換の試験で確かめた: 旧い電文は null、往復は保たれる、連番の有無は他の内容を変えない。
  - 受け手の既存の試験も、変更なしで緑だった。
- 残余リスク:
  - 1b・1c がマージされるまで、その 10 経路は Touch しない状態の変化にも負けて 500 になり得る。負けた側は確定ごと巻き戻るので、状態は壊れない。
  - 完全削除が負けたとき、先に消したオブジェクトは戻らない（[IADR-0296](./IADR-0296_deletion-propagation-to-object-storage.md) 決定 3 の順のまま）。
    - 行は論理削除のまま残り、完全削除のやり直しで片付く。
    - IADR-0597 の時点からある性質である。
  - #2150 / #2251 の順序の逆転そのものは、段 2 まで残る。
