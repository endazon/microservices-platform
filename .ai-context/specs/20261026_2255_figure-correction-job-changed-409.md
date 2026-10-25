---
title: 作業仕様書 — 人手補正の保存が同じジョブの状態の変化と交差しても 500 にせず 409 job_changed を返し、補正を保存せず再発行もしない（#2255）
type: spec
status: done
related_ids: [FR-12, UC-06, SC-07, IADR-0154, IADR-0043, IADR-0597]
author: claude
created: 2026-10-26
updated: 2026-10-26
issue: "#2255"
---

# 作業仕様書 — 人手補正の 409 job_changed（#2255）

> 本仕様書は実装着手前に作成した（着手 2026-10-26）。発端は PR #2254（#2159）の AI レビュー 🟡 2。#2159 の作業仕様書 `20261025_2159_conversion-retry-status-concurrency` §他の経路への影響が、本件を残余として送った。

## 起点となる計画書（トレーサビリティ）

- FR-12 / UC-06（人手補正）/ SC-07（変換ジョブの画面）。
- 関連 IADR: IADR-0154（人手補正 Phase 1）・IADR-0043（ジョブの read-modify-write）・IADR-0597（版の競合を 409 へ写す形）。
- 新しい IADR は起こさない。#2159 の再変換の口（409 `not_retryable`・現在の状態を読み直す）と同じ形を、人手補正の口へ当てるだけだからである（#2159 も作業仕様書だけで行った）。

## 根の原因（コードを読んで確かめた）

- #2254 で `ConversionJob.Status` を並行トークンにした（`ConversionJobDbContext`）。ジョブ行の UPDATE は `WHERE "Status" = @読んだ状態` を伴う。
- 人手補正の流れ（`FigureCorrectionService.CorrectAsync`）は次の順である。
  1. ジョブを読み、`queued` / `processing` なら 409 `job_busy`。
  2. 図と本文を読み、置換する。
  3. 本文をオブジェクトストアへ保存する。
  4. `IConversionJobStore.ApplyCorrectionAsync` で、図の補正とジョブの本文の参照（`MarkCorrected`）を保存する。
  5. `DocumentNormalized` を再発行する。
- 窓は 2 つある。どちらも「1 で確かめた状態が、4 の保存の時点で変わっている」形である（同じ原本の再配送で再処理が始まった等）。
  - **窓 A（4 の中: 読んでから保存まで）**: `ApplyCorrectionAsync` が読んだ後、保存の前に状態が変わる。並行トークンの照合で `DbUpdateConcurrencyException` になる。誰も捕まえないので、**未処理例外で 500** になる。issue の本題。
  - **窓 B（1 と 4 の間）**: 1 の後、4 の読みより前に状態が変わる。`ApplyCorrectionAsync` は**状態を確かめ直さない**。読んだ `processing` のまま補正を書き、照合も通る（読んだ状態のまま）。そのため、**処理中のジョブへ補正を黙って書き、`DocumentNormalized` まで再発行する**（200）。
  - issue は窓 A だけを挙げていたが、同じ根（確かめた状態と書く時点の状態の食い違い）なので、両方を塞ぐ。

## 方針

- `ApplyCorrectionAsync` は結果を返す（`CorrectionWrite`: `Applied` / `Missing` / `JobChanged`）。
  - 書く直前に読んだ行で状態を確かめ直す（`ConversionJob.IsBusy`。1 と同じ述語を共有する）。`queued` / `processing` なら書かずに `JobChanged` を返す（窓 B）。
  - 保存の並行例外を捕まえ、負けたジョブと図の追跡だけを外して（`EntityState.Detached`）、`JobChanged` を返す（窓 A）。外し方は #2159 の `PrepareRetryAsync` と同じで、DbContext 全体は捨てない。
  - ジョブか図が消えていたら `Missing` を返す。従前は黙って戻り、呼び出し側が再発行まで進んでいた。
- `FigureCorrectionService` は、`JobChanged` なら現在の状態を読み直す。新しい状態 `FigureCorrectionStatus.JobChanged`（`CurrentStatus` つき）を返し、**再発行しない**。`Missing` は `FigureNotFound`（404）へ写す。
- 端点は `JobChanged` を **409 `{ error: "job_changed", status: <読み直した現在の状態> }`** へ写す。#2159 の再変換の負けた側（`not_retryable`・`status` は読み直した値）と同じ形である。
- BFF は 409 の本文を透過する（`ConversionBffEndpoints` の `RelayAsync`）。変更は要らない。
- OpenAPI（手書きの `docs/api/openapi.yaml`）に、`job_changed` と `status` を書き足す。生成物（orval の `bff.schemas.ts`）を再生成する。`docs/api/BFF_bff-surface.md` の表も追随する。

## 窓（規則 11）

窓は「確かめた状態（1）」と「書く時点の状態（4 の保存）」の間にある。形を確定する前に、両側のプローブで 3 つの形を比べた。

プローブ:

- **P1（窓 A・保存の直前に状態が変わる）**: 期待は、500 にせず 409 `job_changed`。補正は保存されず、再発行もしない。
- **P2（窓 B・ストアが読む前に状態が変わる）**: 期待は、同じく 409 `job_changed`（黙って書かない）。
- **P3（陽性対照・並行しない）**: 期待は、従前どおり 200 で補正を保存し、再発行する。
- **P4（最初から処理中）**: 期待は、従前どおり 409 `job_busy`（1 の判定）。

| 形 | P1 | P2 | P3 | P4 |
| --- | --- | --- | --- | --- |
| 後の端だけ（並行例外を捕まえて 409 にする） | 〇 | ×（照合が通るので黙って書く） | 〇 | 〇 |
| 前の端だけ（書く直前に状態を確かめ直す） | ×（読んだ後・保存の前の変化は確かめ直しで見えず、500） | 〇 | 〇 | 〇 |
| **両端（確かめ直し ＋ 並行例外の捕捉）** | 〇 | 〇 | 〇 | 〇 |

→ 両端の形を採る。試験は P1〜P4 を写し、試験名に P 番号を入れる。

## 母集合（規則 9）

- ジョブ行を書く経路は、#2159 の作業仕様書の表と同じ 5 か所である（`git grep -n "SaveChangesAsync" -- 'src/knowledge/backend/Services/ConversionService/**' ':!**/Tests/**'` で引き直した）。
  - `StartAsync`・`SucceedAsync`・`FailAsync` は受け口（Wolverine の再試行で読み直す）。HTTP の端点ではない。
  - `PrepareRetryAsync` は #2159 で 409 へ写した。
  - **HTTP の端点で並行例外が素通りするのは `ApplyCorrectionAsync` だけ**である（本件）。
- `IConversionJobStore` の実装は、`EfConversionJobStore` と試験の `RacingStore`（`ConversionRetryRaceEndpointTests`）の 2 つである（`git grep -n ": IConversionJobStore"`）。後者は戻り値の型の変更に追随させる。
- 409 の理由の語の母集合: `git grep -n "job_busy\|figure_not_correctable\|body_unavailable"`。
  - 端点・試験・`docs/api/openapi.yaml`（2 か所）・`docs/api/BFF_bff-surface.md`・生成物 `bff.schemas.ts` に当たった。
  - 画面（`knowledge/frontend`）は理由の語で分岐しない。409 を一律に「注意」で出す。

## 試験

- ストアの単体（`ConversionCorrectionConcurrencyTests`）:
  - P1: 保存の直前に割り込み、別のスコープで再処理を始める（`StartAsync` で processing）。`ApplyCorrectionAsync` は投げずに `JobChanged` を返す。図は補正されず、状態は processing のまま。追跡は外れている。
  - P2: 先に processing にしてから `ApplyCorrectionAsync` を呼ぶと、`JobChanged` を返して書かない。
  - P3: 並行しなければ `Applied`。
- 端点（`ConversionFigureCorrectionTests`）:
  - P2: 補正の書き込みの手前で再処理を割り込ませるストアを使う。409 `job_changed` で、`status` は processing。`DocumentNormalized` は再発行されない。
  - P1 の端点版: 保存の直前の割り込み（`SaveChangesInterceptor`）を器の DbContext に入れる。409 `job_changed`（500 ではない）。
  - P3・P4 は既存の試験（`Correction_ReplacesImageEmbedWithCodeBlock_AndRepublishes`・`job_busy` の試験）が写す。
- 修正前に、新しい試験が赤になることを確かめる（P1 は 500 / 例外、P2 は 200）。

### 結果（実測）

- 修正前（端点の試験）:
  - P1 は `DbUpdateConcurrencyException` が端点まで出た（500 の形）。
  - P2 は 200（処理中のジョブへ黙って補正を書いた）。
  - P4 は緑（既存の `job_busy`）。
- 修正後、ConversionService.Tests は 250 件 緑（新規はストアの 4 件と端点の 3 件）。

| 変異 | 赤になった試験 |
| --- | --- |
| 書く直前の確かめ直しを外す（前の端を外す） | P2（ストア）・P2（端点） |
| 並行例外の捕捉を外す（後の端を外す） | P1（ストア）・P1（端点） |
| 戻した後 | 7 件すべて緑 |

## 残余

- 本文（オブジェクトストアの `document.md`）は、ジョブ行の保存より前に書く（従前どおり）。そのため、`job_changed` で終わった補正でも、本文のファイルは補正版で上書き済みになり得る。
  - 再処理が成功すれば、再処理がこのファイルを書き直す。
  - `DocumentNormalized` を再発行しないので、取り込みへは届かない。
  - 補正版を書き戻して消す形は採らない。並行する再処理の本文を上書きし得るからである。
- 画面（SC-07）は、補正の 409 を理由で出し分けず、「このジョブは再変換できません（実行中、または失敗以外の状態です）」と出す（従前から `job_busy` も同じ）。
  - 補正の文脈では語が合わない。画面の文言を分けるかは別に判断する（コーディネーターへ報告する）。
