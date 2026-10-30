---
title: IADR-0661 視覚回帰は 3 面だけを公式 Docker イメージの中で手動で流し、project visual は PW_VISUAL=1 のときだけ持つ（E-80）
type: impl-adr
status: Accepted
related_ids: [NFR, SC-01, SC-19, IADR-0569, IADR-0621, IADR-0009, IADR-0442]
author: claude
created: 2026-10-30
updated: 2026-10-30
related_specs:
  - ../specs/20261030_2039_visual-regression-three-faces.md
---

# IADR-0661: 視覚回帰の 3 面と流し方（#2039 / E-80）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-30
- 決定者: claude（#2039）

## 起点・関連

- 起点 issue: #2039（#2013 の分割表 E-80。任意・P2）。
- 前提:
  - E2E 基準書 §8（`docs/tests/E2E_STANDARDS.md`）。視覚回帰は既定で採らず、採るなら公式 Docker イメージ・`@visual` の別 project `visual`・`maxDiffPixelRatio ≤ 0.01`・`animations: 'disabled'`・揮発要素の `mask` とする。
  - IADR-0569。層・タグ・project 名の正本で、`visual` は「採るときだけ」と定めている。
- 受け入れ基準: Docker 上で安定（連続 5 回同一）。PR の必須チェックには入れない。

## コンテキストと課題

- スクリーンショットはランナーの OS・フォント・ブラウザで変わる。
  - L1 の `e2e` ジョブ（ubuntu ランナー・`playwright install --with-deps chromium`）とローカルでは描画が揃わない。
  - L1 と同じ project に入れると、基準画像を撮った環境の外で比べて落ちる。
- 公式イメージ（`mcr.microsoft.com/playwright`）には pnpm が無い。
  - `corepack` で入れるには通信が要る。
  - L1 の `webServer` は `pnpm run preview` を起こす。
- 対象 3 面には揮発する値がある。
  - NotFound はシェルの中に描かれ、シェルは利用者名を出す。
  - SC-19 の一覧は日時・容量を出す。
  - SC-01 は回答本文と履歴を出す。

## 決定

1. **3 面だけを、各面の要素に絞って撮る**（`e2e/visual.spec.ts`）。
   - NotFound の region、SC-19 の削除確認の dialog、SC-01 の出典の一覧（`ol`）。
   - 揮発する値を範囲に入れないので、`mask` は要らない。
   - 撮る直前にポインタを `(0, 0)` へ外す。
   - 機能の正しさ（文言・通信）は各画面の `*.smoke.spec.ts` が持つ。visual は見え方だけを見る。
     ただし BFF の往復の完全性（`expectBffTrafficIsComplete`）は同じく見る。
2. **project `visual` は `PW_VISUAL=1` のときだけ設定に現れる**。
   - `smoke` は `visual.spec.ts` を `testIgnore` する。
   - 旗の無い実行（ローカルの素の `playwright test`・`frontend.yml` の `e2e`）には project が無い。
   - 許容差は project の `expect.toHaveScreenshot` に置く（`maxDiffPixelRatio: 0.01`・`animations: 'disabled'`）。
3. **入口は `pnpm --filter @platform/frontend run test:e2e:visual` の 1 つ**。
   - ホストでビルドする。次に、版を固定した公式イメージを `--network none`・`--ipc=host`・ホストの uid で起こす。
   - `src/` をマウントし、ホストの `node_modules` の Playwright と vite を使う。
   - `PW_VISUAL=1` のとき、`webServer` は `node node_modules/vite/bin/vite.js preview` を起こす（pnpm を経ない）。L1 の起動の形は変えない。
   - 基準画像の更新は同じ入口に `--update-snapshots` を足す。
4. **イメージの版はロックファイルの `@playwright/test` の版と同じにする**。`scripts/scripts.repo.test.js` が一致を見る（陰性対照つき）。
   - 依存の更新で Playwright だけが上がると、イメージの中では `node_modules` の Playwright が求めるブラウザが無く、実行が大きく落ちる。
   - ただし visual は CI で流れないので、その落ち方では気付けない。
5. **CI のワークフローには載せない**（手動）。
   - 受け入れ基準は「PR の必須チェックに入れない」であり、非必須の自動実行も求めていない。
   - 公式イメージは 1 回の取得で約 2 GB ある。フロントの PR ごとに取る費用に対し、守る面は 3 つしかない。

## 実測（2026-10-30。イメージ `v1.62.1-noble`）

- 許容差を 0（`maxDiffPixels: 0`・`maxDiffPixelRatio: 0`・`threshold: 0`）にした一時の設定で、連続 5 回流した。
  - 3 面とも 5 回とも pass した。
  - 基準画像の sha256 は 5 回の前後で変わらなかった。
- 陰性対照: 出典の 4 行目の機密区分を `restricted` から `confidential` へ変えた（ラベル「取扱制限」→「秘」）。
  差は 953 画素（画素の 2%）で、`maxDiffPixelRatio: 0.01` で落ちた。
- `PW_VISUAL` の無い `playwright test --list` は 217 件 27 ファイルで、`visual` を含まない。
- `PW_VISUAL=1 … --list --project visual` は 3 件 1 ファイル。

## 却下した案

- **L1 の `e2e` ジョブで流す**: ランナーの描画が基準画像の環境と違い、flaky の典型になる（基準書 §8）。
- **visual を PR の paths で起動する別ワークフロー（非必須）**: 決定 5 の費用。必要になったら workflow_dispatch か週次で足す（残余リスク参照）。
- **イメージの中で pnpm を入れる**: 通信が要り、`--network none` で起こせない。ローカルではプロキシの CA を渡す手間も増える。
- **全ページを撮って揮発要素を `mask`**: 撮る範囲が広いほどシェル・ナビの変更で 3 面すべての基準画像が割れる。守りたいのは面の見え方だけである。

## 残余リスク

- **CI で流れないので、3 面の見え方を変える PR が基準画像を更新せずにマージされ得る。** 次に手で流した人が赤を見て、原因の PR を辿ることになる。
  - 常時の検知が要るなら、`frontend.yml` とは別のワークフローを `workflow_dispatch` ＋週次で足す。足すときは本 IADR に追記する。
- **NUL の検査（`scripts/check-nul-bytes.js`）から基準画像をパスの規則で外した**（［2026-10-30 追記 / #2039］）。
  - 基準画像はリポ初の追跡下のバイナリで、PNG は形式上 NUL を含む。CI の scripts-tests が 3 枚を「生の NUL を含む」と検出した。
  - 外すのは `<spec>-snapshots/` 直下の `.png` だけである（`BINARY_PATH_RULES`）。拡張子の一括除外（`BINARY_EXTENSIONS`）は空のまま。
  - `-snapshots/` の外の `.png`、`-snapshots/` の中の `.png` 以外、入れ子の下の `.png` は従来どおり検査する。自己試験が陰性対照で固定する。
  - repo 試験は、規則が 1 つであることと、追跡下で外れるのが基準画像だけであることを固定する。
  - `.gitattributes` に同じ範囲（`**/*-snapshots/*.png binary`）を明示した。
  - 代償: 基準画像の名を持つファイルに混入した NUL は捕まらない。PNG なので、もともと NUL を含む。
- **AI レビューの下限の検査（`scripts/check-review-verdict.js`）が、画像を読めないと書いた文を数えないようにした**（［2026-10-30 追記 / #2039］）。
  - #2370 の 2 回目のレビューは、🔍 に「PNG 3 枚の画像の中身は行単位で読めないため未検証」と書いた。検査器はこれを「読んだと申告したのに精読していない自認」と読み、下限違反でジョブを落とした。
  - バイナリには行単位の差分が無い。この文は読めないものを読めないと書いただけで、精読の放棄ではない。追跡下にバイナリがある PR では、毎回同じ形で落ちる。
  - 外す文は 3 条件をすべて満たすものに限る（IADR-0643 と同じ狭さ）。
    1. 画像・PNG・バイナリ・`.png` の語を含む。
    2. 残りに読む対象の語（テスト・文書・`.ts` 等）が無い。
    3. 行がテキストの変更ファイルを名指さない。
  - 自己試験と、実本文の fixture（`scripts/fixtures/review-floor/pr2370-review2.txt`）の陽性対照が赤の側を固定する。
  - プロンプトの【検証の下限】に、バイナリは生成物と同じく K に数えてよいと 1 行足した。対は repo 試験が見る。
- 基準画像は `linux` の接尾辞を持つ。イメージの OS（noble）を変えると、描画が変わり得る。版を上げる PR では `--update-snapshots` で撮り直して差分を見る。
