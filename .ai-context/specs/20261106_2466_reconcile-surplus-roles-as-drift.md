---
title: 作業仕様書 — realm の調停の差分に、サービスアカウントの権限の余剰を数える（段 1 は check で数え apply は報告だけ）
type: spec
status: done
related_ids: [NFR-18, IADR-0369, IADR-0688]
author: claude
created: 2026-11-06
updated: 2026-11-06
issue: "#2466"
---

# 作業仕様書 — 調停の差分にサービスアカウントの権限の余剰を数える

> 着手 2026-11-06（develop `00fa8b26`）。判断の記録は **IADR-0688**（新規）。issue は #2466。稼働中のクラスタには何もしない。

## 起点

- 要求 **NFR-18**。前提: **IADR-0369**（宣言と実行時の境界。サービスアカウント利用者のロールは宣言が正）。

## 現状（develop で読み直した）

- `deploy/local/keycloak-setup/reconcile-realm.js` は、サービスアカウント利用者のロールの**欠落**だけを計画する。稼働で足されたロール（余剰）は比べないので、`--check` が `drift=0` を返す。
- 実体（client・role・group・mapper・user）は加算的で、余剰を消さない（設計どおり。本件はサービスアカウントのロールに限る）。

## 母集合（規則 9）

- 「宣言が全集合」と境界表が定めるもののうち、余剰を見ていないもの: サービスアカウント利用者の realm ロール・client ロール。scope の割当は既に全集合で比べている。人の seed 利用者のロールは実行時が所有する（SC-17）ので対象外。
- Keycloak が自分で付けるもの（数えない）: 既定ロール `default-roles-<realm>`（すべての利用者に直接付く）、認可サービスを有効にした client のサービスアカウントの `uma_protection`（自分の client のロール）。
- 稼働でロールを足す口の走査（`role-mappings` への書き込み）: `scripts/check-mcp-client-provisioning.js`（使い捨ての登録者。宣言のサービスアカウントではない）、AuthorizationService の SC-17（人の利用者）。宣言のサービスアカウントへロールを足す口は無い。

## 方針

1. `collectLive` はサービスアカウント利用者の全部の割当（`GET users/{id}/role-mappings`）を読む。読めない形なら余剰を見ない（推測で外さない）。
2. `plan` は余剰を出力先 `surplus` に積む（`ops` に入れない）。本文は外す DELETE。
3. `--check`: 余剰を drift に数える（G9 が赤）。
4. apply: 段 1（既定）は外さずに `surplus` の行で報告する（収束の判定に入れない＝起動器を止めない）。`RECONCILE_REMOVE_SURPLUS=1` で外す（Job の env。起動器の口が 0 / 1 だけを受ける）。

## 受け入れ基準

- AC-1: 宣言より多い realm ロール・client ロールが余剰になり、`ops` には入らない（試験）。
- AC-2: 既定ロール・自分の client の `uma_protection`・宣言どおりの割当は余剰にならない。全割当を読めないときは余剰を見ない（試験）。
- AC-3: check は余剰を返し、apply は既定で外さず収束し、切り替えでだけ DELETE を打つ（試験）。
- AC-4: Job は切り替えを `"0"` の既定で持ち、起動器の口は 0 / 1 以外で止まる（試験）。
- AC-4b: 起動器の Job の差し替えは、余剰の切り替えと作り直しの名前（IADR-0685）を互いの env へ当てない（名前が `0` でも。試験・順を逆にすると落ちることを確かめた）。
- AC-4c: 全割当を読めない（role-mappings が想定外の形）サービスアカウントは、check でも apply でも 1 行で報告し、余剰 0 として黙って流さない。drift には数えない（試験）。
- AC-4d: 大文字を含む realm 名でも、既定ロールを余剰に数えない。`removeSurplus` のときに DELETE が失敗すると、`applyErrors` に数える（試験）。
- AC-5: 既存の調停の試験が通る。

## 残余

1. 段 2（apply で既定で外す）は、PoC で `--check` の余剰が 0 になってから別の PR で入れる。
2. 人の seed 利用者の余剰は見ない（実行時が所有）。
