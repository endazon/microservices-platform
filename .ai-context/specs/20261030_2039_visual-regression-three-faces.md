---
title: 作業仕様書 — 視覚回帰を 3 面だけ（NotFound・SC-19 確認ダイアログ・SC-01 出典ラベル）公式 Docker で（#2039 / E-80）
type: spec
status: done
related_ids: [NFR, SC-01, SC-19, IADR-0569, IADR-0661]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2039"
---

# 作業仕様書 — 視覚回帰 3 面（#2039）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `7bef62b6`）。#2013 の分割表 E-80（波 7・任意・P2）。

## 範囲

- 正本: E2E 基準書 §8（`docs/tests/E2E_STANDARDS.md`。#2013 の基準書 §9 を写したもの）。
- 触る領域（issue）: `playwright.config.ts`（`@visual` project）、`e2e/visual.spec.ts`、`-snapshots/`。
- 入口のために足す: `src/platform/frontend/package.json` の `test:e2e:visual`。
- 文書の追随（下の母集合）: `docs/tests/E2E_STANDARDS.md`、`src/platform/frontend/README.md`。
- 版の一致の試験: `scripts/scripts.repo.test.js`。

## 受け入れ基準

- [x] Docker 上で安定（連続 5 回同一）。
  - 許容差 0 の一時設定で 5 回とも pass した。基準画像の sha256 は不変。
  - 陰性対照（機密区分を 1 つ変える）は 2% の差で落ちた。
- [x] PR の必須チェックには入れない。
  - project `visual` は `PW_VISUAL=1` のときだけ存在し、`smoke` は本 spec を外す。
  - 素の `--list` は 217 件で、visual を含まない。

## 設計

決定と却下案は IADR-0661。

## 母集合（規則 9・10）

- **規則 9**: 「視覚回帰」「@visual」「`visual`（project 名）」「test:e2e」で `*.md` を全文走査した。
  - `docs/tests/E2E_STANDARDS.md`: 追随する。
    - §2.1 の project 表に `visual` 行を足す。
    - §8 に採用の追記を足す。
    - §11 の CI 表に手動の行を足す。
    - §12 の「視覚回帰: なし」を改める。
    - trace ブロックに IADR・仕様書・issue を足す。
  - `.ai-context/adr/IADR-0569_e2e-layers-and-live-opt-in.md`（表の `@visual`・`visual` 行）: 「採るときだけ `visual`」であり、本変更と整合する。凍結記録なので書き換えない。
  - `src/platform/frontend/README.md`: 開発コマンドの一覧に 1 行足す。
  - `docs/how-to/local-development.md`・`docs/how-to/session-handoff.md`・`docs/tests/SC-0x`（`test:e2e` の記述）: L1 の入口の記述で、変わらない。追随しない。
- **規則 10**（この変更で新たに誤りになる自分の記述）:
  - `playwright.config.ts` の既存コメント「L1 の project 名は `smoke`」は正しいまま。
  - 「既定の testMatch は … 拾うため」は、`visual.spec.ts` を外す理由にもなる。コメントを足した。
  - §12 の「本書の目標: 既定で採らない（候補 1 件）」は、3 面だけ採った今も目標として正しい。
  - 導出値（217 件・27 ファイル）は、本変更後に `--list` で計算し直した。

- **［2026-10-30 追記 / #2039］CI の scripts-tests が赤になったので、母集合を足した**（`BINARY_EXTENSIONS`・「空である」・「既定空」で走査した）。
  - 原因: 基準画像 3 枚が `check-nul-bytes` に「生の NUL を含む」と検出された（リポ初の追跡下のバイナリ）。
  - ローカルで捕まらなかった理由: `scripts.test.js` を `git add` の前に流した。`check-nul-bytes` は `git ls-files`（追跡下だけ）を走査するので、未追跡の画像は母集合に入っていなかった。
  - 是正:
    - `scripts/check-nul-bytes.js` の `BINARY_EXTENSIONS` に `.png` を足した。自己試験に、`.png` の陽性の例と、同じバイト列を別の名で置く陰性対照を足した。集合を `['.png']` ちょうどに固定した。
    - `scripts/scripts.repo.test.js` の「空である」を「`.png` だけ」に改めた。
    - `scripts/README.md` の「既定空」を改めた。
    - IADR-0661 の残余リスクに 1 項を足した。
    - `.ai-context/adr/IADR-0247_nul-byte-check.md`（「空で始める」）と仕様書 20260822 は凍結記録で、当時の記述として正しい。書き換えない。
  - 検証は、画像をコミットした状態で `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` を流して行う。

## 検証

- `pnpm run test:e2e:visual --update-snapshots`（初回）。続けて許容差 0 で 5 回。
- 通常の許容差での陰性対照。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（画像をコミットした状態で。上の追記のとおり、初回は `git add` 前に流して捕まえ損ねた）。
- prettier・eslint・typecheck（`platform/frontend`）。
- 文書系の検査: trace-blocks・knowledge-graph・adr-numbering・cross-repo・plan-id・doc-updated。
