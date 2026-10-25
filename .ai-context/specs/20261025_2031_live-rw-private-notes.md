---
title: 作業仕様書 — live-rw の層の門と、SC-19/20 個人資料の一生・共有の動的束縛・端末の失効（E-51）
type: spec
status: in-progress
related_ids: [SC-19, SC-20, FR-05, FR-19, FR-20, UC-11, NFR, ADR-0032, IADR-0624, IADR-0629, IADR-0630]
author: claude
created: 2026-10-25
updated: 2026-10-25
plan_refs:
  - planning:projects/microservices-platform/05_screens（SC-19・SC-20）
  - planning:projects/microservices-platform/07_adr/ADR-0032（SPA は BFF セッション）
issue: "#2031"
---

# 作業仕様書 — live-rw の門と個人資料の書き込み（#2031 / E-51）

> 本仕様書は実装着手前に作成した（着手 2026-10-25）。親は #2013（網羅表 M-062・M-063・M-067、基準書 §7、live-poc.md §3）。
> 🔴 書き込みは使い捨てのスタック（e2e-live.yml）の中だけで行う。稼働中の PoC・本番には向けない。トークン・cookie の値を書かない・出さない。

## 受け入れ基準（#2031 ＋ オーケストレーターの条件 2026-10-25）

- [x] **live-rw の門**: `E2E_LAYER` に `rw` を含むときは `E2E_STACK`（使い捨てのスタックの名前）を要り、globalSetup が kubectl の
      current-context = `k3d-<stack>`・stack が使い捨てのスタックの一覧にあることを確かめ、違えば例外で止まる（sweep と同じ門・同じ実装）—— L1 ＋ 単体
- [ ] M-062: e2e-user が `e2e-<runId>-note-…` を作成 → e2e-operator へ個人指定で共有 → 取消 → 論理削除 → 完全削除が画面で通る —— live-rw
- [ ] M-063: 共有の前と取消の後は e2e-operator の SC-03（`/docs/<id>`）が「文書が見つかりませんでした」（BFF 404）、共有中だけ見える（動的束縛） —— live-rw
- [ ] M-067: SC-20 で端末 `e2e-<runId>-device-…` を登録 → トークン発行（**表示を記録しない**）→ 個別失効で「失効済み」 —— live-rw
- [ ] 作ったものは afterEach の `cleanup.ts` が消し、e2e-live の sweep の dry-run が **残骸 0・見ていない視点 0** —— live の実走

## 判断（IADR-0630）

1. **門の単一実装**: 使い捨てのスタックの判定（一覧 `['e2e-live']` と kubectl の context の照合）を `scripts/lib/disposable-stack.js` へ移し、
   sweep と live-rw の門の両方がそれを使う（2 か所に一覧を持たない）。live-rw の門は globalSetup が子プロセスで呼ぶ。
2. **`E2E_SWEEP_STACK` を `E2E_STACK` へ改める**（sweep と rw の門が同じ向け先を読む。同じ値に 2 つの名前を付けない）。
3. **runId**: `E2E_RUN_ID`（英小文字と数字 4〜16 字）。未設定なら config が 1 回だけ決めて環境変数へ置き、ワーカーが引き継ぐ。
   名前は `e2e-<runId>-<種類>-r<再試行>`（再試行が前の試行の名前と重ならない）。
4. **rw は ro の後に流す**（同じ実行で ro を選んでいれば `live-rw` は `live-ro` に依存する）。ro の到達の試験と書き込みを並べない。
5. **sweep の個人資料の視点から e2e-revocable を外す**。e2e-revocable のセッションは失効の試験（auth-errors.ro・E-53）が実行の中で
   無効にするので、teardown の時点で一覧を読めない（PR #2234 の e2e-live で実測: 401）。代わりに **e2e-revocable で作らない**ことを規則にし、
   L1 が「`revocable` を参照する live の spec は後始末を積まない（`createCleanup` を呼ばない）」を固定する。
6. **トークンを記録しない**（M-067）: その test は trace・スクリーンショット・動画を切り、発行の応答から `deviceId` だけを取り、
   応答を待った直後に画面を開き直してトークンの表示を消す（失敗時の error-context にも載らない）。トークンの値に触れる assertion を置かない。
7. **SC-02（検索）での見え方は判定しない**（M-063 は SC-03 で見る）。作った個人資料は本文を持たず、露出の「横断検索に含める」は既定 OFF なので
   索引に入らない。検索で見るには露出を ON にして取り込みを待つ必要があり、動的束縛（共有の付与と取消）の判定とは別の要因が混ざる。

## 試験の流れ（`e2e/live/private-notes.rw.spec.ts`）

- M-062・M-063（1 test・段ごとに `test.step`）: SC-19 で作成（POST の応答から ID を取り、直後に `track`）→ 未共有で operator の
  `/docs/<id>` が 404 → 「共有先を変更する」→「E2E Operator」を個人で追加 → 一覧のバッジが「個人指定（1 人）」→ operator に見える（見出しが
  タイトル）→ 取り消す → operator が 404 → 削除する（論理削除）→ 削除済みタブ → 完全に削除する → 行が消える。
- M-067（`e2e/live/devices.rw.spec.ts`。trace 等を切るのはファイルの先頭でしかできないので分けた）: SC-20 で端末名を入れてトークンを発行 → 応答の `deviceId` を `track` → 画面を開き直す → 行の「この端末を失効する」→ 確認 →
  「失効済み」。端末の行は履歴として残る（網羅表のとおり。消す口は失効だけ）。
- 後始末: 個人資料は `DELETE /bff/private-notes/{id}` → `POST /bff/private-notes/purge`、端末は `DELETE /bff/private-notes/devices/{id}`
  （本文が済ませていれば 404＝消えた扱い）。

## 母集合（規則 9・10）

- `E2E_SWEEP_STACK` を書いている箇所（`git grep`）: env.ts・teardown.ts・e2e-live.yml・live-opt-in.smoke.spec.ts・scripts.repo.test.js・
  E2E_STANDARDS.md・IADR-0629・作業仕様書 20261025_2030。前の 6 つを改め、IADR-0629 には日付つき追記、作業仕様書（凍結）は直さない。
- sweep の視点の役割を書いている箇所: e2e-live-sweep.js・scripts.repo.test.js（#2030 節）・IADR-0629 決定 5・E2E_STANDARDS.md。

## 変更するファイル

- 新規 `e2e/live/private-notes.rw.spec.ts`・`e2e/live/devices.rw.spec.ts`・`scripts/lib/disposable-stack.js`・IADR-0630
- `e2e/live/support/{env,readiness,teardown,cleanup}.ts`・`playwright.live.config.ts`・`e2e/live-opt-in.smoke.spec.ts`
- `scripts/e2e-live-sweep.js`・`scripts/scripts.repo.test.js`・`scripts/live-scripts.json`・`scripts/README.md`
- `.github/workflows/e2e-live.yml`（既定の層を `ro,rw`・`E2E_STACK`）・`docs/tests/E2E_STANDARDS.md`・IADR-0629（追記）・`.ai-context/adr/README.md`

## 検証

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`: 1199 passed（#2031 の判定器の 1 件を足し、#2030 節の視点の期待を 5 役割へ改めた）。
- L1 `e2e/live-opt-in.smoke.spec.ts`: 25 passed（新規 3: rw の門・runId と名前・視点の和集合と revocable の規則。既存の sweep の 2 件を `E2E_STACK` へ改めた）。
- `E2E_LIVE=1 E2E_LAYER=ro,rw E2E_STACK=e2e-live … --list`: 48 件（live-rw に M-062/M-063 と M-067）。
- `tsc -p tsconfig.node.json` 0 件・eslint 0 件・prettier 緑。文書の検査は緑。
- live: e2e-live（PR）で ro,rw を流し、rw の 2 件が通ること・sweep の dry-run が残骸 0・見ていない視点 0 であること（結果は PR に書く）。
