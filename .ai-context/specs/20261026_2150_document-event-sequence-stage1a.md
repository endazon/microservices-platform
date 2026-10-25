---
title: 作業仕様書 — 文書ごとの単調な発行の連番を契約に足し、並行トークンにする（#2150 / #2251 段 1a）
type: spec
status: done
related_ids: [FR-02, FR-06, FR-19, FR-20, ADR-0027, ADR-0057, ADR-0061, ADR-0142, IADR-0637, IADR-0122, IADR-0455, IADR-0595, IADR-0596, IADR-0597, IADR-0604, IADR-0608, IADR-0636]
author: claude
created: 2026-10-26
updated: 2026-10-26
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-02・FR-06・FR-19）
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md（少なくとも 1 回・再試行。順序は保証しない）
---

# 仕様書: 文書ごとの単調な発行の連番（#2150 / #2251 段 1a）

## 起点となる計画書（トレーサビリティ）

- 要求: FR-02（文書の取り込み・索引）、FR-06（文書の CRUD・版）、FR-19 / FR-20（個人資料の露出・論理削除・復元・Obsidian 同期）
- 計画 ADR: ADR-0027（Wolverine。少なくとも 1 回の配送・再試行）、ADR-0057（削除の伝播）、ADR-0061（露出）、ADR-0142（論理削除の撤収）
- 🔴 **順序の鍵を定める計画 ADR は無い。** 受け手の順序判定（`UpdatedAt`）は実装の決定（IADR-0595 / IADR-0596）であり、本件もその置き換えとして実装の範囲で決める（IADR-0637）。計画への環流は要らない（コーディネーターの判断 2026-10-26）。
- 実装 ADR: **IADR-0637**（本件）
- issue: #2150（UpdatedAt を進めない変更は順序を判定できない）、#2251（再試行で順序が入れ替わると、外した個人資料が載り直す）、#2258（段 1c の追跡）

## 段の分割（コーディネーターの判断 2026-10-26）

| 段 | 範囲 | PR |
| --- | --- | --- |
| **1a（本書）** | 契約 `Sequence`・列 `EventSequence`・migration・採番・並行トークン化。単一文書の経路（共有の付与・取り消し・SC-19 の論理削除・Obsidian 側の削除・復元・Obsidian の既存ノートの更新）と完全削除を 409 へ写す | 本 PR |
| 1b | 多数文書の 2 経路（Tags/Rename・Republish）。ページの確定で競合したら読み直して再試行（上限は 1b の仕様書）、上限を超えたら `nextCursor` つきの 409。部分の結果は返さない | 1a の直後 |
| 1c | もとから 409 へ写していない 8 経路（#2258）。単一文書は `DocumentVersionConflict`、複数文書は id なしの 409 | 1b と並べて |
| 2 | 受け手 3 つ（取り込み・グラフ・Wiki）。印に `Sequence` と `UpdatedAt` を両方持ち、両方に連番があれば連番で、無ければ `UpdatedAt` で比べる | 1a〜1c の後 |

**PoC への配備は 1a・1b・1c がそろってから行う。** develop に 1a だけが入っている間、PoC は古いままにする。

## 設計の前提の確認（実装前）

- `DocumentUpdated` の組み立ては発行アダプタ 1 か所である。ポートを呼ぶのは唯一の発行点 `DocumentEndpoints.PublishUpdatedAsync` だけである（IADR-0455。門の 4 入口を全経路が通る。`PublishGateCoverageTests` が走査で固定している）。
  - そこで +1 すれば、全経路の発行に連番が付く。
- `DocumentDeleted` の組み立ても 1 か所で、呼び出しは 3 経路である（文書の削除・個人資料の完全削除・定期処理 2 種）。
  - 3 経路とも、消す前に文書行を読んでいる。そのため「最後の値 + 1」を渡せる。
- 🔴 **崩れた前提（止めて報告した）**: 状態の変化は `Touch()` しないので、`Version` を進めない。
  - いまの並行トークンは `Version` だけなので、並行する 2 つの状態の変化が同じ連番を読み、どちらの UPDATE も成功する（同じ値が 2 つの違う状態に付く）。
  - そこで `EventSequence` 自体を並行トークンにした（案 (i)。比較は IADR-0637）。

## 母集合（規則 9）

### 最初の引き方とそのずれ（規則 9 の実例）

- 最初は「発行の門を呼ぶ経路」で引いた（`PublishUpdatedIfIndexable` / `SaveDeletionStateAsync` の呼び出し元 19 ファイル）。その結果を次のように報告した。
  - 「新たに負け得る経路は 6 つ」
  - 「既存の 8 経路は 500 の窓が前からあり、Sequence では悪化しない」
- **根の語「Document 行を確定する経路」で引き直すと、ずれていた。**
  - 並行トークンが照合されるのは、発行するかどうかに関係なく、Document 行の UPDATE / DELETE である。
  - 引き直しでは、`Document` の mutator 10 個（`Update`・`UpdateMetadata`・`AddTag`・`ApplyNormalized`・`SetMarkdownUri`・`RecordContentFingerprint`・`SetExposureAttributes`・`Publish`・`Archive`・`ReplaceUntrustedBoundaryWrapped`）の呼び出し元、`Documents.Remove(` / `RemoveRange(`、発行点の呼び出し元を全件 grep した。
  - そのうえで、`DocumentVersionConflict` への写しの有無で分けた。
  1. **8 経路も悪化する。** いままでは Touch する相手にだけ負けた。1a の後は、Touch しない状態の変化にも負ける。→ 段 1c に格上げした（#2258）。
  2. **どの一覧にも無かった書き手が 2 つあった。** ObsidianSync/Push（既存ノートの更新）と PrivateNotes/Purge（複数件の行の削除）である。→ 1a に含めた（判断 (a)）。
- 教訓: **「窓を開けた変更が照合する対象」（ここでは行の確定）で引く。** 「変更が触ったコード」（ここでは発行の門）で引くと、触っていないのに照合される書き手が落ちる。

### 引き直した母集合（非試験の DocumentService）

| 書き手 | 写し（1a 前） | 1a の扱い |
| --- | --- | --- |
| Update・UpdateMetadata・SetExposure・Delete | 済み（IADR-0597） | 変更なし（連番で負けても同じ 409） |
| GrantShare・RevokeShare | なし | **409 に写す** |
| PrivateNotes/SoftDelete・ObsidianSync/Delete・PrivateNotes/Restore（`SaveDeletionStateAsync`） | なし | **409 に写す** |
| ObsidianSync/Push（既存ノートの更新） | なし（アプリ側の `baseVersion` の 409 だけ） | **409 に写す**（`expectedVersion` = `baseVersion`） |
| PrivateNotes/Purge | なし | **id なしの 409 に写す**（`DocumentVersionConflict.ProblemForBatch`） |
| Tags/Rename・Republish | なし | 段 1b（残余） |
| Archive・Publish・PutBody・AddTag・StripUntrustedBoundary・ArchiveAstQuotes・ArchiveBodyless・SyncConflicts/Resolve | なし | 段 1c（#2258。残余） |
| `DocumentNormalizedConsumer` | なし | 写さない（例外 → Wolverine の再試行が読み直す） |
| Create（REST・Push の新規） | なし | 写さない（新しい行。競合しない） |
| 個人資料の定期処理（`PrivateNoteMaintenanceService`。退職者の削除・90 日の削除） | なし（周期全体の catch だけ） | **所有者ごとに確定を隔離する**（下の「レビューで直したこと」） |

契約の受け手（`DocumentUpdated` / `DocumentDeleted` を読むサービス）: IngestionService・GraphService・WikiService・RetrievalService（削除のみ）。1a では受け手を変えない。

### レビューで直したこと（#2259 の AI レビュー 🟡 3）

- 最初の表では、定期処理を「例外を捕まえ、次の周期でやり直すので写さない」とした。
- **catch の範囲を確かめていなかった。**
  - 確定の失敗を捕まえるのは `RunCycleAsync`（周期全体の catch）だけである。
  - `PrivateNoteMaintenanceService.cs:194` の catch は名簿の読み直し（`StillPurgeableAsync`）だけを囲み、確定は囲まない。
  - 90 日の削除は全所有者を 1 回で確定していた。
- 1a の後は、Touch しない状態の変化（復元・共有の変更）とタグの改名・再発行にも負ける。1 件の負けで次が止まる。
  - 同じ周期の他の所有者の削除
  - 後段（版の整理・通知 3 種・同期履歴と緊急アクセスの記録の保持期限）
- 直したこと:
  - 退職者の削除（もとから所有者ごとの確定）と 90 日の削除（所有者ごとに分けた）を、所有者 1 人分ずつ `CommitIsolatedAsync` で囲む。
  - 並行トークンの負けだけを捕まえ、追跡を捨てて次の所有者へ進む。
  - 負けた分は行が残り、次周期の再入で消える。
- 教訓（規則 9 の続き）: 「例外を捕まえてやり直す」と書く前に、**その catch が囲む範囲（何を巻き添えにして止めるか）**を行番号で確かめる。

## 実装

- 契約: `DocumentUpdated` と `DocumentDeleted` の末尾に `long? Sequence = null` を足す（IADR-0122 決定 2 の非破壊の追加）。`scripts/contract-schema-baseline.json` を更新する（非破壊 2 件）。
- 台帳: `Document.EventSequence`（`long`。既定 0）を足し、`AdvanceEventSequence()` で +1 する。`DocumentDbContext` で並行トークンにする。
- migration: `20261025145956_AddDocumentEventSequence` で `bigint NOT NULL DEFAULT 0` の列を追加する。既存行は 0 になる。
- 採番:
  - 唯一の発行点が、確定の中で +1 して保存し、その値を運ぶ。追跡されていない実体で呼ばれたら、例外で止める（黙って連番を落とさない）。
  - 削除の 3 経路は「最後の値 + 1」を運ぶ。完全削除で行が見つからない id は null を運ぶ。
- 409 の写しは上の表のとおりである。
- 定期処理の完全削除（退職者・90 日）は所有者ごとに確定し、並行トークンに負けた所有者の分だけを次周期へ回す。

## 受け入れ基準

- [x] 発行のたびに連番が 1 つ進む。`UpdatedAt` を進めない変化（共有の付与・取り消し・論理削除・復元）を含む。台帳の値は最後に発行した値である。
- [x] `DocumentDeleted` は最後の連番 + 1 を運ぶ。
- [x] 並行する 2 つの確定では、負けた側が 409 `version_conflict` になり、勝った側の連番だけが進む。負けた側の発行（更新・削除）は出ない。
  - 7 経路の theory: grant・revoke・SC-19 の削除・Obsidian の削除・restore・push・purge。
- [x] 連番を持たない旧い電文は `Sequence = null` として読める。連番は往復で保たれる。連番の有無は他の内容を変えない（Contracts の互換の試験）。
- [x] 受け手の既存の試験が変更なしで緑である（Ingestion 256・Graph 857・Wiki 230・Retrieval 575）。
- [x] 変異で赤になる。
  - 並行トークンを外す → 並行の theory が赤。
  - 採番を止める → 連番の試験と並行の theory が赤。
  - 各経路の写しを外す → その経路の theory の 1 件が赤。
- [x] 再発行の等価の試験は、連番を除いて等しく、連番だけが +1 になる。
- [x] 定期処理の完全削除（90 日・退職者）は、1 人分が並行する確定に負けても周期を失敗にしない。
  - 他の所有者の資料は消え、負けた分は行ごと残って発行も出ず、次周期で消える。
  - 所有者ごとの catch を外す変異で 2 件とも赤になる。
- [ ] 実 PostgreSQL（Integration）で緑になる。コーディネーターが dispatch する。

## 残余（PR 本文にも書く）

- 1a の後、段 1b の 2 経路（Tags/Rename・Republish）と段 1c の 8 経路（#2258）は、**Touch しない状態の変化にも負けて 500 になり得る**（1a の前は Touch する相手にだけ負けた）。
  - 負けた側は確定ごと巻き戻るので、状態は壊れない。
  - 追跡先は、1b が本件の続き、1c が #2258 である。
- 完全削除が負けたとき、先に消したオブジェクトは戻らない（オブジェクトが先・行が後の順を変えない）。
  - 行は論理削除のまま残り、完全削除のやり直しで片付く。
  - 版の並行トークン（IADR-0597）の時点からある性質である。
- 受け手は連番をまだ読まない。#2150 / #2251 の順序の逆転は段 2 まで残る。

## PoC の事前手順

- migration 1 本（`Documents.EventSequence` の追加）。他の migration との適用順に依存しない。
- 🔴 **単独で配備しない。** 1b・1c と一緒に配備する（1a だけでは、1b・1c の経路の 500 の窓が広がった状態になる）。
