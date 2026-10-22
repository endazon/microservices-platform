---
title: radix 一式の更新（#2077）で ui チャンクが増え、初期ロードの床を超えた develop を床の更新で緑へ戻す
type: spec
status: done
related_ids: [NFR, ADR-0031, IADR-0134, IADR-0147]
author: claude
created: 2026-10-22
updated: 2026-10-22
---

# 仕様書: 初期ロードの床を radix 一式の更新に追随させる

> 本仕様書は作業着手前に作成した（着手 2026-10-22）。基点は MSP `origin/develop` `2767c148`。
> 床の更新は `check-chunk-budget.js` が定める手順（`--update` で更新し差分を PR に載せる）の範囲内であり、
> 前例（#1993・#1923 ほか）と同じく新しい IADR は起こさない。経緯は床ファイルの日付つき注記に残す。

## 起点

- develop（`2767c148`）で `frontend.yml` の build-test の `node scripts/check-chunk-budget.js --require` が
  「初期ロード合計が床を超えた: 635.03 kB > 633.16 kB（+1.88 kB）」で落ちる。
- 原因は #2077（dependabot `@radix-ui/react-tabs` 1.1.21 → 1.2.1、`60c9717d`）。依存の radix 一式も上がり、
  初期ロードに載る `ui` チャンクが 76,575 → 78,450 B（+1,875 B）になった。
- #2077 は自身の CI で build-test が赤のままマージされ、develop の push の Frontend CI は同時実行の打ち切り
  （cancelled）で赤が表に出なかった。

## 方針

- 依存の更新は受け入れる（巻き戻さない）。増分は依存ライブラリ自身のコード量であり、分割規則の退行ではない。
- `node scripts/check-chunk-budget.js --update` で床を 633,157 → 635,032 へ更新し、
  `scripts/chunk-budget-baseline.json` に日付つき注記（`$comment_initialTotalBytes_20261022_2077_radix-tabs-bump`）を足す。

## 受け入れ基準

1. develop HEAD のビルドで `node scripts/check-chunk-budget.js --require` が緑になる。
2. 増分が `ui` チャンクの radix 更新だけであることを A/B で示す: `package.json` 2 本と `pnpm-lock.yaml` だけを
   `a29bf9da`（#2077 の直前）へ戻したビルドの初期ロード合計が旧床 633,157 と一致し、JS チャンクのサイズ差が `ui` の 1 本だけである。
3. `smallLazyChunks` は 10 のまま変わらない。

## 結果

| 基準 | 結果 |
| --- | --- |
| 1 | `--require` OK（635.03 kB / 床 635.03 kB）・`--self-test` 13 件通過 |
| 2 | A: 633,157 B（旧床と一致）/ B: 635,032 B。JS 55 本中サイズ差は `ui` 76,575 → 78,450 の 1 本だけ |
| 3 | A/B とも 10 本 |
