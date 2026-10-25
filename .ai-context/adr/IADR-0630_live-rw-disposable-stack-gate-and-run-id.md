---
title: IADR-0630 live-rw の層は使い捨てのスタックの中だけで流し（E2E_STACK と kubectl の context の門を sweep と同じ実装で持つ）、作るものの名前は実行ごとの runId で付け、e2e-revocable では作らない
type: impl-adr
status: Accepted
related_ids: [NFR, SC-19, SC-20, ADR-0032, IADR-0624, IADR-0625, IADR-0629]
author: claude
created: 2026-10-25
updated: 2026-10-25
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032（SPA は BFF セッション）
related_specs:
  - ../specs/20261025_2031_live-rw-private-notes.md
---

# IADR-0630: live-rw の門と runId（#2031）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-25
- 決定者: claude（#2031 / E-51。条件はオーケストレーターの指示 2026-10-25: 書き込みは使い捨てのスタックの中だけ・名前に `e2e-<runId>-`・
  sweep の dry-run で残骸 0 と見ていない視点 0）

## 起点・関連

- 起点 issue: #2031（親 #2013 の基準書 §7・live-poc.md §3）。関連: [IADR-0629](./IADR-0629_live-cleanup-and-disposable-only-sweep.md)（後始末と sweep）・
  [IADR-0624](./IADR-0624_live-playwright-config-setup-and-readiness.md)（config・setup・門）・[IADR-0625](./IADR-0625_e2e-live-workflow-disposable-stack.md)（e2e-live）。
- 作業仕様書: [20261025_2031_live-rw-private-notes](../specs/20261025_2031_live-rw-private-notes.md)。

## コンテキストと課題

波 4（live-rw）は稼働の環境へ書き込む。sweep（IADR-0629）は向け先を使い捨てのスタックに限ったが、層 `rw` の spec そのものには門が無く、
PoC へ向けた `E2E_LAYER=rw` は書き込みをそのまま流す。作るものの名前の runId を決める場所も無かった。
また e2e-revocable のセッションは失効の試験が実行の中で無効にするので、sweep の個人資料の視点で毎回「見ていない視点」が出ていた
（PR #2234 の e2e-live で実測: `private-notes@e2e-revocable` が 401）。

## 決定

1. **門の単一実装**: 使い捨てのスタックの判定（一覧 `['e2e-live']` と kubectl の current-context `k3d-<stack>` の照合）を
   `scripts/lib/disposable-stack.js` へ移し、sweep と live-rw の門の両方がそれを使う。live-rw の門は globalSetup が、到達の確かめより前に
   子プロセスで呼び、違えば**例外で止める**（前提未整備の exit 2 ではない。流してはならない実行である）。
2. **`E2E_SWEEP_STACK` を `E2E_STACK` へ改める**。層に `rw` を含むとき・`E2E_SWEEP` を与えたときに必須（既定値は持たない）。どちらも無いのに
   `E2E_STACK` だけを与えるのは読み込みで例外。
3. **runId**: `E2E_RUN_ID`（英小文字と数字 4〜16 字）。未設定なら config が実行ごとに 1 回だけ決めて環境変数へ置き、ワーカーが引き継ぐ。
   名前は `e2e-<runId>-<種類>-r<再試行>`（`support/cleanup.ts` の e2eName）。再試行が前の試行の名前と重ならない。
4. **rw は ro の後に流す**（同じ実行で ro を選んでいれば `live-rw` は `live-ro` に依存する）。e2e-live の既定の層を `ro,rw` にする。
5. **sweep の個人資料の視点から e2e-revocable を外し、e2e-revocable では作らない**。L1 が「`revocable` を参照する live の spec は
   `createCleanup` を呼ばない」と「teardown が要る state の役割（SWEEP_ROLES）＝ sweep の視点の和集合」を固定する。
6. **トークンを記録しない**（M-067）: その spec のファイルは trace・スクリーンショット・動画を切り（Playwright はファイルの先頭か config でしか
   切り替えられない）、発行の応答から deviceId だけを取り、応答の直後に画面を開き直してトークンの表示を消す。
7. **共有の動的束縛は SC-03（`/docs/<id>`）で判定し、SC-02（検索）では判定しない**。作った個人資料は本文を持たず、露出「横断検索に含める」は
   既定 OFF で索引に入らない。共有の付与・取消とは別の要因（露出・取り込みの待ち）が混ざる。

## 理由

- 決定 1: 一覧と照合を 2 か所に持つと、片方だけに PoC の context が足される。sweep の試験（IADR-0629）がすでに判定を固定しているので、
  同じ関数を使えば live-rw の門も同じ試験に乗る。
- 決定 5: 失効の試験は e2e-revocable の役割そのもの（IADR-0626・E-53）なので、そのセッションを teardown まで生かすことはできない。
  見ていない視点を毎回出すと、本物の欠け（別の役割の 401）が埋もれる。作らない規則を機械で固定すれば、外した視点に残骸は生まれない。

## 影響

- 波 4 の rw spec は `e2eName(cfg.runId, …)` で名前を付け、作った直後に `cleanup.track` する。e2e-revocable で作るものを足すときは本 IADR を改める。
- 手元で `E2E_LAYER=rw` を流すには、k3d の e2e-live を起こし、`E2E_STACK=e2e-live` を与える（PoC の context では globalSetup が止める）。
- 🔴 残余: IADR-0629 と同じく、kubectl の context とエッジの URL が同じクラスタを指すことは確かめていない。
