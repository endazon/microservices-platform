---
title: 作業仕様書 — live の後始末の共通部品（cleanup.ts）と残骸掃き（e2e-live-sweep.js）
type: spec
status: in-progress
related_ids: [NFR, ADR-0032, IADR-0624, IADR-0625, IADR-0629]
author: claude
created: 2026-10-25
updated: 2026-10-25
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032（SPA は BFF セッション）
issue: "#2030"
---

# 作業仕様書 — live の後始末の共通部品と sweep（#2030 / E-50）

> 本仕様書は実装着手前に作成した（着手 2026-10-25）。親は #2013（基準書 §7・live-poc.md §3・§7）。
> 🔴 稼働中の PoC・本番には向けない。live の実行は CI の e2e-live（使い捨てスタック）だけで確かめる。値（cookie・資格情報）を書かない・出さない。
> 起点 ID: 計画に該当する FR は無い（試験の基盤＝メタ作業。無採番 NFR の場合 2。IADR-0179 決定 1・IADR-0188）。

## 受け入れ基準（#2030 ＋ オーケストレーターの条件 2026-10-25）

- [x] `--dry-run` で接頭辞 `e2e-` に一致するものだけを列挙する（読み取りの要求だけを送る）—— 単体テスト
- [x] `e2e-` 以外を消さないことを単体テストで固定する（接頭辞の真理値表と、混ぜた一覧に対して消す要求が `e2e-` の ID にしか飛ばないこと）
- [x] 既定は dry-run。消すには `--delete` の明示が要る（`--dry-run` と `--delete` の同時指定は例外）—— 単体テスト
- [x] 使い捨てのスタックの外へ向けない: 向ける先（`--edge-url`・`--auth-dir`・`--stack`）を明示しなければ例外で止まり、`--stack` は使い捨てのスタックの名前（`e2e-live`）だけを受け、kubectl の current-context が `k3d-<stack>` でなければ止まる。止まる経路は要求を 1 つも送らない —— 単体テスト
- [x] `--live`（#1550）が無ければ exit 3 で何もしない（`live-scripts.json` へ分類。#1550 節の既存の試験が拒否の経路を試す）
- [x] seed（ABAC ポリシー・検索用文書・タグ辞書）の名前は `e2e-` で始まらない（sweep が seed を消さない根拠）—— 単体テスト
- [x] `cleanup.ts`: 積んだ後始末を逆順に `/bff/*` へ直接送り（`X-MSP-CSRF` つき）、404 は消えたとみなし、失敗しても残りを続けて「未削除一覧」を test に attach する。`e2e-` で始まらない名前・`/bff/` 以外の経路は積む時点で例外 —— L1
- [ ] e2e-live（使い捨てスタック）の teardown が、失効の前に sweep の dry-run を流し、全種類の一覧を読めて 0 件であること（live の実走で確かめる）

## 判断（IADR-0629）

1. **sweep は Node の単体スクリプト（標準モジュールだけ）で、セッションは live-setup が書いた storageState から読む。** セッションは
   teardown が失効させるので、sweep は**live の実行の中**（globalTeardown の失効の前）で流す。`E2E_SWEEP`（`dry-run` / `delete`。
   未設定は流さない）と `E2E_SWEEP_STACK` で選ぶ。`E2E_SWEEP` を与えて `E2E_SWEEP_STACK` を与えないのは読み込みで例外。
2. **既定は dry-run。** `--delete` が無ければ GET だけを送る。
3. **向け先の門**: `--edge-url`・`--auth-dir`・`--stack` は既定値を持たない（無ければ例外）。`--stack` は `DISPOSABLE_STACKS = ['e2e-live']`
   だけを受け、kubectl の current-context が `k3d-<stack>` と一致しなければ止まる（PoC の context は `rancher-desktop`。エッジの URL は
   どちらも `https://localhost` なので URL では区別できない）。門は要求の前に置く。
4. **一致は `name.startsWith('e2e-')` だけ**（大文字小文字を区別。文字列でないものは一致しない）。サーバ側の絞り込み（文書の `q`）は
   候補を減らすだけで、判定は必ず手元の接頭辞で行う。
5. **種類**（live-poc.md §3）: 文書（admin）・ABAC ポリシー（admin）・データソース（admin）・個人資料（e2e-user。論理削除 → 完全削除）・
   タグ（admin。使用中は 409 になるので最後）・MCP クライアント（admin。**BFF に削除の口が無い**ので列挙だけ）。AST の監視銘柄は
   AST の live の持ち物なので MSP の sweep に入れない。
6. **`--older-than` は持たない。** 設計（基準書 §7）は常設 PoC で並行する実行の作りかけを消さないための窓だったが、向け先を使い捨ての
   スタックに限ったので並行する実行が無い。タグには作成日時も無い。共有の環境へ向ける日が来たら、窓と一緒に門を改める。
7. **出力**は種類・ID・名前（いずれも試験の識別子。値ではない）。dry-run で残骸があれば警告（exit 0）。delete の失敗・一覧の失敗は exit 1、
   門で止まったら exit 2、`--live` が無ければ exit 3。teardown は sweep の失敗でも失効を必ず済ませ、最後に exit 1。

## 母集合（規則 9・10）

- `e2e-` で始まる名前を作る・持つものを走査した: `deploy/local/*-seed/*.json` に無い（`e2e-user` 等の利用者名は sweep の種類に無い）。
- `e2e-live-sweep` / `--older-than` / sweep を書いている文書（`git grep`）: `docs/tests/E2E_STANDARDS.md` §7 の 1 箇所（「目標」として `--older-than 24h` を書いていた）→ 実装に合わせて書き換えた。
  ほかは #2013 の基準書 §7・live-poc.md §3（issue 本文）だけ。`--older-than` を持たない理由は IADR-0629 決定 6。
- 検査器の母集合（scripts.repo.test.js #683 節の 66 本）: sweep は駆動器なので `NOT_CHECKERS` へ入れた（数を上げない）。

## 変更するファイル

- 新規 `scripts/e2e-live-sweep.js`、`src/platform/frontend/e2e/live/support/cleanup.ts`、IADR-0629
- `src/platform/frontend/e2e/live/support/{env,teardown}.ts`（`E2E_SWEEP`・`E2E_SWEEP_STACK`・失効の前の sweep）
- `src/platform/frontend/e2e/live-opt-in.smoke.spec.ts`（L1）、`scripts/scripts.repo.test.js`（単体）、`scripts/live-scripts.json`、
  `scripts/README.md`、`.github/workflows/e2e-live.yml`（dry-run の sweep を流す・paths）、`.ai-context/adr/README.md`

## 検証

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`: 1197 passed（#2030 節 8 件を含む）。
  - 変異: `isSweepable` を `toLowerCase().includes('e2e-')` にすると真理値表が赤（`"E2E-r1"`）、context の判定を外すと「止まらない」で赤。戻して緑。
- L1 `e2e/live-opt-in.smoke.spec.ts`: 21 passed（新規 2: `E2E_SWEEP` の値域と sweep の引数、cleanup の逆順・CSRF・404・未削除一覧）。
  手元は preview の `dist` が無いので webServer を外した一時の config で流した（この 2 件はブラウザも preview も使わない）。
- `tsc -p tsconfig.node.json`（e2e）0 件。`pnpm run lint` 0、`format:check` 緑。`tsc -b` の 2 件と knip の unlisted +1 は手元に AST の submodule が無いため（既知）。
- 文書: check-trace-blocks / check-doc-links / check-doc-updated / check-cross-repo-refs / check-plan-id-qualification / check-reading-budget /
  gen-knowledge-graph --check / check-adr-numbering 緑。
- live: e2e-live（PR で起動する。paths に `scripts/e2e-live-sweep.js` を足した）の teardown が dry-run の sweep を流し、全種類の一覧を読めて残骸 0 件であること（結果は PR に書く）。
