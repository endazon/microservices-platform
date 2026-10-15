---
title: 作業仕様書 — 再発行の駆動スクリプトに --created-after を足し、SeaweedFS 切り替え後の文書だけを流せるようにする
type: spec
status: done
related_ids: [FR-02, FR-06, IADR-0503, IADR-0509]
author: claude
created: 2026-10-15
updated: 2026-10-15
plan_refs: []
---

# 作業仕様書: 再発行の駆動スクリプトに `--created-after` を足す（#1931 項目 1）

> 本仕様書は実装着手前に作成した（着手 2026-10-15）。基点は MSP `origin/develop` `8b06c8b7`。

## 起点となる計画書（トレーサビリティ）

- FR-02（取り込み）・FR-06（索引の作り直し）。関連 IADR: IADR-0503（再発行の口と駆動スクリプト）、IADR-0509（キーセットの SQL）。
- 起点の事象: #1931。PoC の再索引（45,571 件）がカナリアで止まった。2026-09-26 の SeaweedFS への切り替えより前に作られた文書（5,803 件）は
  本文が SeaweedFS に無く、取り込みが `NoSuchKeyException` で失敗する。駆動スクリプトは古い順に走査するので最初からこの分に当たる。
- 射程は #1931 の**項目 1 だけ**。項目 2（取り込み側の NoSuchKey の扱い）は別 PR。`src/` には触らない。

## 方式

- 口（`POST /documents/republish-updated`）は `createdAfter` を持たない。`src/` に触らずに下限を付けるため、
  **駆動スクリプトが開始カーソルを合成する**。カーソルは `v1:<UtcTicks>:<Id の N 形式>` の base64url（`DocumentPageCursor`）で、
  口は「`(CreatedAt, Id)` が厳密に後ろ」を選ぶ。`(createdAfter, 00000000-…-0000)` を開始位置にすると、
  `CreatedAt > after`、または `CreatedAt = after` かつ `Id > 全零`（＝実在の Id すべて）が選ばれる。**つまり `after ≤ createdAt`**。
  上限は既存の `createdBefore`（`createdAt < before`）のまま。区間は `after ≤ createdAt < before`。
- 走査は古い順のまま（カーソルの開始位置を動かすだけ）。
- dry-run も開始カーソルを付けて呼ぶ。口の `remaining` が区間の件数、内訳は区間の残り全件について数えられる。
  `matched` は `createdBefore` 側の全件なので、`matched - remaining` を「createdAfter より前（対象外）」として出す。
- 状態ファイル: `filters.createdAfter`（無指定は null）を記録する。新規の状態は `cursor` と `confirmedCursor` を開始カーソルにする
  （止まって確かめた位置へ戻すときに先頭へ戻らないため）。進捗の分母は区間の件数にする（`excluded` を最初のページで記録）。
- 条件の不一致: `--resume` は絞り込みを受け付けない（既存の `--created-before` と同じ）。既存の状態ファイルがあるまま新規に流すと拒否され、
  `sameFilters` は `createdAfter` が完全一致のときだけ同じ走査とみなす（`createdBefore` は既定が開始時刻なので無指定を同じとみなすが、
  `createdAfter` には既定が無いので、無指定と指定は違う走査である）。版は上げない（旧い状態は `createdAfter` 無し＝ null と読む）。
- 不正な日時は止める。`after ≥ before`（空の区間を含む）は止める。`before` 無指定のときは新規の状態で開始時刻と比べる。

## 走査した母集合（規則 9）

`git grep -n -i 'created-before\|createdBefore\|created_before'`（`.ai-context/specs` を除く）の結果:

| 箇所 | 扱い |
| --- | --- |
| `scripts/republish-document-updated.js`（使い方・解析・`filtersOf`・`newState`・`stateProblem`・`sameFilters`・`buildRequestBody`・再開／新規のログ） | 対称に `createdAfter` を足す |
| `scripts/scripts.repo.test.js`（#1762 の純関数の試験 5 箇所） | 同じ形で `createdAfter` の試験を足す |
| `docs/operations/operations.md` の再索引の手順（手順 2 の絞り込み・手順 3 の状態） | 追記する。切り替え後だけを流す手順を足す |
| `scripts/README.md` の一覧 | 絞り込みの列挙に `--created-after` を足す |
| `docs/api/openapi.yaml`・`src/…/Republish/*`・`DocumentService.Tests` | 口の仕様。口は変えないので対象外 |
| `.ai-context/adr/IADR-0503` | 凍結記録。対象外（本仕様書が記録） |
| `docs/operations/voyage-embedding-key-runbook.md` | 再索引の手順を持たない（運用仕様書へ委ねている）。対象外 |

## 受け入れ基準

- [x] `--created-after` だけ: 解析で UTC へ寄せ、新規の状態の `cursor` / `confirmedCursor` が開始カーソルになる。
- [x] 区間: `--created-after` と `--created-before` を両方渡せ、要求の本文に `createdBefore` と開始カーソルが載る。
- [x] `after ≥ before` と不正な日時は止める。`--resume` と併用は止める。
- [x] 開始カーソルが口の `DocumentPageCursor` の形（`v1:<ticks>:<32 桁の 0>`）である。
- [x] dry-run の要約が区間の件数と対象外の件数を出す。
- [x] 状態ファイルの `createdAfter` が違えば同じ走査とみなさない。
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` と文書系の検査器が通る。

## 検証の記録

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → `✓ 1076 tests passed`（#1931 の 5 件を含む）。
- 変異 9 種（開始カーソルの Id・ticks の起点・確かめた位置・`sameFilters`・空区間・`--resume` 併用・`excluded`・UTC 寄せ・dry-run 要約）は
  いずれも #1931 の試験で赤になった（scratchpad の変異器で `#1931` の試験ブロックだけを変異した写しへ当てた）。
- 文書系: `check-trace-blocks` / `check-doc-links` / `gen-knowledge-graph --check` / `check-cross-repo-refs` / `check-plan-id-qualification` /
  `check-doc-type-vocabulary` / `check-nul-bytes` は緑。
