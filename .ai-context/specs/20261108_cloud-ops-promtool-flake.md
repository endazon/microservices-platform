---
title: 作業仕様書 — cloud-ops.test.js の promtool の単体試験が、並べて流したときに置き場の空きの不足で落ち、理由の読めない失敗になる・陰性対照が黙って通る
type: spec
status: in-progress
related_ids: [NFR-21, IADR-0692, IADR-0698, IADR-0697, IADR-0709]
author: claude
created: 2026-11-08
updated: 2026-11-08
related_specs:
  - 20261108_egress-per-source-otlp-identity.md
---

# 作業仕様書 — cloud-ops.test.js の promtool の試験の不安定

> 着手 2026-11-08（develop `2a8f75148`）。試験の作りだけを直す（警報の式・宣言は変えない）。IADR は要らない（判断は試験の手段に閉じる）。

## 起点

- 並行の組の報告: 「IADR-0692 LLM の月次予算の進みの警報: promtool の単体試験」が、検査をまとめて流した回と 4 本を同時に流した回で落ち、
  promtool の出力が空に見えた。単独では 4 回とも通った。

## 再現（実測）

- `scripts/cloud-ops.test.js` を同時に 4 本 × 5 回流すと、20 回のうち 9 回で落ちた（手元の作業環境。根の置き場の空きは 21 MB）。
- 落ちた回の promtool の標準エラーは、空行のあとに `write to WAL: log samples: write /tmp/test_storage…/wal/00000000: no space left on device`・
  `opening test storage: mkdir /tmp/test_storage…/chunks_head: no space left on device`・`panic: preallocate: no space left on device` が並ぶ。
  試験の失敗の文言は先頭の空行で切れて見え、「出力が空」と読めた。
- 単独で流すと promtool は 0.8 秒・最大 RSS 80 MB で通る（時間切れ・メモリではない）。試験は `spawnSync` に時間切れを渡していない。

## 原因

1. promtool の単体試験は、試験の群ごとに一時の TSDB（WAL と head の chunk。WAL の segment は事前に確保される）を **TMPDIR（既定 /tmp）** に作る。
   置き場の空きを、同時に流れるほかの処理と取り合い、尽きると評価の途中で書き込みに失敗して 0 でない終了になる（期待の照合の結果ではない）。
2. 月次予算の試験は 5 分ごとに 8 日分（2305 点 × 9 系列、2305 回の評価）を流し、promtool の中で最も長く（0.8 秒）置き場を持つ。
3. 失敗の文言は stdout と stderr をそのまま連ね、終了のコード・シグナルを出さない（空行で始まり、理由が読めない）。
4. 🔴 陰性対照は「0 でない終了」だけを見るので、置き場の失敗でも「落ちた」と数え、**陰性対照が黙って通る**（実測: 置き場の失敗の回に、
   印の無い系列の陰性対照が「落ちた」と数えられていた）。
5. 砂場（`/tmp/cloud-ops-*`）を消さないので、流すたびに /tmp に残る（手元に 133 個）。強制終了の後は promtool の一時の TSDB も /tmp に残る。

## 方式

- promtool の呼び出しを 1 つの関数（`promtool(args)`）にまとめる。
  - TMPDIR を呼び出しごとの砂場の下の一時のディレクトリにし、終われば消す。
  - 戻り値に `why`（終了のコード・シグナル・空でない出力の行）と `infra`（シグナル、または置き場・一時の TSDB の失敗の文言）を持たせる。
  - 陽性の試験の失敗の文言は `why`。陰性対照は `failedOnExpectation`（終了 1・`infra` でない・`FAILED:` を含む）だけを「落ちた」と数える。
- 月次予算の試験の点を 1 時間ごと（193 点）にする。費用は単調増加の counter なので、7 日の increase と按分の値は 5 分ごとと同じ（期待は変えない）。
  所要は 0.8 秒 → 0.1 秒（実測）。評価の間隔も 1 時間（for: 5m は次の評価で firing になる）。
- 終わりに砂場を消す。
- 時間切れの値は渡さない（時間切れが原因ではない）。試験の skip・期待の緩めはしない。

## 母集合（規則 9・10）

- `git grep -n "run('promtool'" scripts/cloud-ops.test.js` → 11 か所（check 1・test 10）。すべて `promtool()` へ。
- 陰性対照の `assert.notStrictEqual(<x>.status, 0, …)` → 7 か所。すべて `failedOnExpectation` へ。
- 同じ形の呼び出しは `scripts/deploy-cloud-egress-audit.test.js`（`spawnSync('promtool', …)` 3 か所）にもある。本件の範囲の外として残し、報告する。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| AC1 | promtool は砂場の下の一時の置き場を使い、終われば消える。理由の説明に終了のコードが載る | 新しい試験「promtool の呼び出し」 |
| AC2 | 置き場の空きの不足・シグナルでの終了を、陰性対照の「落ちた」に数えない。期待との食い違いは数える | 同（合成の結果 3 種） |
| AC3 | 月次予算の試験は 1 時間ごとの点で、期待（鳴る 2 つ・鳴らない 2 つ）と陰性対照 3 つがそのまま通る | 既存の試験 |
| AC4 | 置き場の失敗の回の失敗の文言は、終了のコード・「期待の照合の外の理由」・置き場の失敗の行を出す（空にならない） | 手で再現（同時 4 本 × 5 回） |
| AC5 | 既存の 60 件が緑 | `node scripts/cloud-ops.test.js` |
