---
title: 作業仕様書 — 変換ジョブの再変換の check-then-write を、状態の並行トークン（UPDATE … WHERE Status = 読んだ状態）で塞ぎ、負けた側は再発行せず 409 にする（#2159）
type: spec
status: done
related_ids: [FR-12, FR-06, UC-06, SC-07, ADR-0027, IADR-0597, IADR-0609, IADR-0043, IADR-0154]
author: claude
created: 2026-10-25
updated: 2026-10-25
plan_refs:
  - planning:projects/microservices-platform/03_usecases/01_usecases.md（UC-06 例外フロー: 失敗ジョブの再変換）
---

# 仕様書: 変換ジョブの再変換の並行を塞ぐ（#2159）

## 起点（トレーサビリティ）

- 要求: FR-12（変換）・UC-06（再変換は失敗ジョブに限る）。SC-07（変換ジョブ画面）。親: #2047
- 経緯: #2103（文書の楽観的同時実行。IADR-0597）の実装中に同型の窓として見つかった。
- 関連 IADR: IADR-0597（版の競合を 409 へ写す形）・IADR-0609（再変換は queued への遷移と発行を 1 つの確定に載せる）・IADR-0043（ジョブの read-modify-write の並行性は follow-up）

## 事象

`EfConversionJobStore.PrepareRetryAsync` はジョブを読み、`TryRequeue`（`failed` のときだけ `queued` へ）で判定し、保存する check-then-write である。
ジョブ行に並行トークンが無いので、同じジョブへの再変換が並行すると、両方が `failed` を読んで判定を通り、両方の `UPDATE` が成功し、
`RawDocumentFetched` を 2 回発行し得る（画面の二度押し・再送）。

## 選択肢と決定

| 案 | 中身 | 長所 | 短所 |
| --- | --- | --- | --- |
| A. 条件付き UPDATE（`ExecuteUpdateAsync` … `WHERE Status = 'failed'`） | 再変換の口だけで、遷移を 1 文の UPDATE にし、0 行なら発行しない | 射程が再変換だけ。マイグレーション不要 | 🔴 `TryRequeue` が決める項目（`Error`・`DeadLettered`・`HasBody`・`UpdatedAt`）をストアに書き下すことになり、ドメインの規則が 2 か所になる。🔴 器（InMemory）は `ExecuteUpdate` を実行できず、既存の端点・ストアの試験が動かなくなる |
| B. 版の列（`Version` を並行トークンに。#2103 と同じ） | 列を足し、保存のたびに進める | #2103 と同じ形 | マイグレーションが要る。版を進める点を全遷移に足す |
| C. `xmin`（PostgreSQL のシステム列） | 列を足さずに並行トークンにする | マイグレーション不要 | 器（InMemory）に無い。**行への全ての書き込みが互いに競合する**（取り込み側の状態更新どうしも） |
| **D. 状態（`Status`）を並行トークンにする（採用）** | EF が行の UPDATE に `WHERE "Status" = @読んだ状態` を付ける。0 行なら `DbUpdateConcurrencyException` | **A の意味（読んだ状態のままなら遷移する）をドメインの規則を書き下さずに得る**。マイグレーション不要（スナップショットの注記 1 行。#2158 と同じ）。器（InMemory）も並行トークンを照合する。負けの写し方は #2103（`DocumentVersionConflict`）と同じ「並行例外 → 409」 | 状態を読んでから書く**他の経路**（取り込みの開始・成功・失敗、人手補正）にも同じ照合が掛かる（下） |

**D を採る。** 理由: 窓の正体は「読んだ状態が書く時点で変わっている」ことであり、照合すべきは状態そのものである。
版（B）は状態以外の変化（人手補正の本文の差し替え等）でも負けにするので、再変換の判定より広い。

### 他の経路への影響（D の帰結）

`Status` の照合は、ジョブ行を読んでから書く全ての経路に掛かる。いずれも「読んだ状態のまま」なら従前どおり成功する。

| 経路 | 読む → 書く | 照合で負けるとき | 帰結 |
| --- | --- | --- | --- |
| 再変換（`PrepareRetryAsync`） | failed → queued | 並行する再変換が先に queued にした | **null を返し、端点は 409 `not_retryable`（現在の状態を読み直して返す）**。発行しない |
| 取り込み開始（`StartAsync`） | 任意 → processing | 同じジョブの別の遷移が先に入った（同じ原本の再配送が並行した等） | 例外が受け口へ出て、Wolverine の再試行（2・10・30 秒）で読み直してやり直す。従前は後勝ちで黙って上書きしていた |
| 成功・失敗（`SucceedAsync` / `FailAsync`） | processing → succeeded / failed | 同上 | 同上 |
| 人手補正（`ApplyCorrectionAsync`） | succeeded のまま本文の参照を差し替え | 補正の最中に同じジョブが再処理へ入った | 例外（端点は 500 になり得る）。従前は再処理の結果を補正の本文で上書きし得た。**頻度は低く、本 issue では写し方を変えない**（残余 → #2255） |

## 負けた側の応答

- 端点（`Retry/Endpoint.cs`）は従前から「ストアが null を返したら 409 `not_retryable`」である。**応答の形は変えない**（画面は `error` で分岐する）。
  ただし `status` は要求の最初に読んだ値（`failed`）ではなく、**読み直した現在の状態**を返す（#2103 の `AfterLostSaveAsync` が現在版を読み直すのと同じ。負けた側に「失敗のまま」と見せない）。
- 並行の負けは確定（`CommitAsync`）の中で起きる。ストアは並行例外を捕まえて、**負けたジョブと図の追跡だけを外し**（`EntityState.Detached`）、null を返す。確定は何も積まずにコミットする（発行 0）。
  - DbContext 全体（`ChangeTracker.Clear()`）にしない理由: 同じ確定（スコープ）に他の書き込みが載ったとき、それまで黙って捨ててしまうため（AI レビュー 🟡 1。範囲を負けた実体に限れば、後から確定に書き込みを足しても壊れない）。

## 母集合（規則 9）

`ConversionJob` の行を書く全経路を `git grep -n "SaveChangesAsync" -- 'src/knowledge/backend/Services/ConversionService/**' ':!**/Tests/**'` で引いた: `StartAsync`・`SucceedAsync`・`FailAsync`・`PrepareRetryAsync`・`ApplyCorrectionAsync` の 5 か所（上の表）。
`ExecuteUpdate` / `ExecuteDelete` / 生 SQL でジョブ行を書く経路は 0 件（同じ grep で確認）。

## 受け入れ基準

- [x] AC1（並行・決定的）: 同じジョブへの再変換が並行し、片方の保存の直前にもう片方が確定しても、`RawDocumentFetched` は 1 回だけ出る。負けた側の `PrepareRetryAsync` は null を返し、ジョブは queued のまま（器: 保存の割り込みで窓を開ける）。
- [x] AC2（実 PostgreSQL。`Category=Integration`。**`integration.yml` run 40200968420 / job 121191874583（head 2797f1f9）で緑**: knowledge の統合試験は宣言 114・実走 129・合格 129・失敗 0）: 同じ窓を実 PostgreSQL ＋ 実ブローカで開け（先の確定がジョブ行の行ロックを持つ間に後の再変換を流し、ロック待ちになったことを `pg_locks` で確かめてから先を確定する）、受け手に届くのは 1 通だけ（`Category=Integration`）。
- [x] AC3: 負けた側の端点は 409 `not_retryable` で、`status` は読み直した現在の状態（queued）。500 にならない。
- [x] AC4（陽性対照）: 並行しない再変換は従前どおり 202 で 1 通出る。既存の再変換・取り込み・補正の試験が緑。
- [x] AC5: スナップショットとモデルに差が無い（`HasPendingModelChanges` が偽）。
- [x] 変異: 状態の並行トークンを外すと AC1 が赤（2 回出る）。

## PoC の事前手順

**不要。** スキーマは変わらない（並行トークンは EF の照合であり列を足さない）。
