---
title: "T-25 の偶然の赤の記録が p と W を載せない（2 回とも「—」）のを、ジョブのログではなく注釈から読んで塞ぎ、読めなかった理由を記録へ出す（#1998）"
type: spec
status: done
related_ids: [SC-15, NFR-13, ADR-0118, ADR-0113, IADR-0232]
author: claude
created: 2026-10-20
updated: 2026-10-20
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0118_timing-chance-red-rerun-and-monthly-record.md 決定 2・4
  - planning:projects/microservices-platform/07_adr/ADR-0113_timing-judgement-rank-sum-test.md フォローアップ 2（p 値を必ず出す）
---

# 仕様書: T-25 の偶然の赤の記録が p と W を載せない欠陥を塞ぐ（#1998）

## 起点

- issue: #1998（`[CI] integration-stack が失敗している`。自動起票）
- 画面 SC-15（パスワードリセット）のテスト T-25 ／ NFR-13 ／ 計画 ADR-0118 決定 2・4 ／ ADR-0113 フォローアップ 2

## 調べたこと（根本原因）

### 1. #1998 の赤そのもの: T-25 の偶然の赤（設計どおりの約 1%）

- 赤の run: 39068251547 attempt 1（`schedule` / `fe06bdee`）。失敗した手順は `🔴 Gate — パスワードリセットの送出とメール本文（#1144）` だけで、
  その中の失敗も T-25 の順位和検定だけ（注釈 `T-25 だけが赤（p=0.0100 / W=712）`。`p=0.0100` は 4 桁の丸めで、判定は `p < 0.01`）。
- 自動の再実行（attempt 2）は合格（p=0.3842 / W=545）。その後の develop の run（10/18 `39165637555`・10/19 `39335684002`、同じ `fe06bdee`）も緑。
- 10 月の分布（`node scripts/t25-monthly-summary.js --month 2026-10`、2026-10-20 12:15 UTC に実行）:
  判定まで届いた n=185・不合格 2（いずれも同じコミットの次の観測が合格）＝ 1.08%（有意水準 1% と同じ大きさ）・p の中央値 0.4932・
  一様分布からの KS 距離 D=0.043（目安 1.36/√185＝0.100 を超えない）。**系統差の兆候は無い。ADR-0118 決定 2・3 の「偶然の赤」に当たる。**

### 2. 記録の欠陥: 自動の記録が p と W を 2 回とも「—」で書いた

- #1998 の 2 件のコメント（再実行の依頼・結果）も、#1929 の 2 件（run 38729082264）も、T-25 の p / W / 判定がすべて「—」。
  **自動の記録が働いた 2 回とも、値を 1 つも載せていない**（同型 2 回）。
- 同じ run を手元で `t25-rerun-on-chance-red.js --run-id 39068251547 --attempt 2`（読むだけ）で走らせると `p=0.0100 / p=0.3842` を読む。
  読み取り器（`parseT25FromJobLog`）は実ログで正しく動く。**落ちているのはワークフローの中でのログの取得**であり、
  `t25Of()` は例外を握りつぶして `null` を返すので、**理由がどこにも残らない**。
- ログの取得（`GET /actions/jobs/{id}/logs`）だけが、ほかの読み取り（JSON）と違い、別ホストへの 302 を辿る。理由を名指す証拠はログに残っていない
  （握りつぶしのため）。**原因の仮説を当てに行くより、302 を辿らない読み取り口へ移し、それでも読めなければ理由を記録へ出す。**

## 方式

1. **検査器が T-25 の結果を注釈として出す。** `check-password-reset-mail.js --live` は、順位和検定まで届いたら（合否によらず）
   `::notice title=t25-rank-sum::verdict=… p=… W=… expectedW=… slower=… istio=…` を 1 行出す（Actions の中だけ）。
2. **記録はまず注釈から読む。** `GET /check-runs/{job id}/annotations`（JSON。302 を辿らない）。無ければ従来どおりログから読む。
   両方とも読めなければ、**読めなかった理由**（各経路の失敗の 1 行目）を返す。
3. **理由を記録へ出す。** 再実行の依頼・結果のコメントで p が読めなかったときは、その理由を併記する。ワークフローのログにも出す。
4. 再実行のジョブへ `checks: read` を足す（注釈の読み取りに要る）。権限表（`scripts.repo.test.js` の `JOB_SCOPES`）も同じ値へ更新する。
5. 月次の要約（`t25-monthly-summary.js`）も同じ読み取り器を使う（注釈 → ログ）。人が手元で走らせる経路で、ログで動いていたが、読み口を 1 つにする。

### 採らなかった案

- **ログの取得を再試行で待つ**: 「ログがまだ用意されていない」が原因なら効くが、attempt 1 のログを 13 分後に読んだ結果も「—」だったので、待ちでは説明できない。
- **手順の出力（`t25_p` / `t25_w`）を読む**: 手順の出力は API から読めない（同じ run の後続の手順からだけ読める）。

## 受け入れ基準

- AC1: 検査器は、順位和検定まで届いた `--live` の実行で、合否によらず `t25-rank-sum` の注釈を 1 行出す。届かなければ出さない。
- AC2: 注釈の読み取り器は、`title` が `t25-rank-sum` の注釈から verdict / p / W / 期待値 / 遅い側 / istio を返す。無ければ null。
- AC3: 読み取りは注釈を先に見て、無ければログを読む。両方とも失敗すれば値は null で、理由（経路ごとの 1 行目）を返す。
- AC4: 再実行の依頼・結果のコメントは、p を読めなかったとき理由を載せる。読めたときは載せない。
- AC5: 再実行のジョブの権限は `actions: write` / `checks: read` / `issues: write`（＋ `contents: read`）で、権限表の試験と一致する。
- AC6: 既存の自己試験（ログから読む経路）がすべて通る。

## 検証

- `node scripts/check-password-reset-mail.js --self-test` / `node scripts/t25-monthly-summary.js --self-test` /
  `node scripts/t25-rerun-on-chance-red.js --self-test` / `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- 変異: 注釈の読み取りを外す・理由の併記を外す・権限を外す で、それぞれの試験が赤になることを確かめる。
