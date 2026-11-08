---
title: 作業仕様書 — deploy-cloud-egress-audit.test.js の promtool の呼び出しにも、一時の置き場の分離・失敗理由の表示・陰性対照の厳密化を当てる（#2527 の残り）
type: spec
status: in-progress
related_ids: [NFR-21, NFR-18, IADR-0705, IADR-0709, IADR-0710]
author: claude
created: 2026-11-08
updated: 2026-11-08
related_specs:
  - 20261108_cloud-ops-promtool-flake.md
---

# 作業仕様書 — deploy-cloud-egress-audit.test.js の promtool の手当て

> 着手 2026-11-08（develop `3086b127f`。#2527 = `1e2370462` を含む）。試験の作りだけを直す（警報の式・宣言・期待は変えない）。
> IADR は要らない（判断は試験の手段に閉じ、#2527 の方式をそのまま当てる。新しい決定は無い）。

## 起点

- #2527（作業仕様書 [20261108_cloud-ops-promtool-flake](20261108_cloud-ops-promtool-flake.md)）は `scripts/cloud-ops.test.js` の promtool の
  呼び出しを 1 つの関数へまとめ、一時の置き場（TMPDIR）を呼び出しごとの砂場にし、失敗の理由を出し、陰性対照を「終了 1・置き場の失敗でない・
  `FAILED:` を出した」に絞った。同仕様書の §母集合は、同じ形の呼び出しが `scripts/deploy-cloud-egress-audit.test.js` に残ることを範囲の外として報告した。
- 本件はその残りを直す。

## 母集合（規則 9・10。依頼の「3 か所」は検証せず転記しない）

走査（develop `3086b127f`）:

- `git grep -lI promtool origin/develop -- scripts/` → 5 ファイル。うち promtool を**起動する**のは `cloud-ops.test.js` と
  `deploy-cloud-egress-audit.test.js` の 2 つ。`README.md`・`fixtures/review-floor/pr2496-review1.txt`（レビューの記録の写し）・
  `scripts.repo.test.js`（`ci.yml` の文字列を読むだけ）は起動しない（除外）。
- `git grep -nIE "(spawnSync|execFileSync|execSync|spawn|execFile|exec|run)\(\s*['\"\`]promtool" origin/develop -- scripts/` → 5 か所。
  - `cloud-ops.test.js:51`（#2527 の関数の中の 1 か所。直済み）
  - `deploy-cloud-egress-audit.test.js:618`（`promtoolReportModeRun`）・`:659`（`promtoolCertAlertTest` の陽性）・`:664`（同 陰性対照）・
    `:709`（`credentialAlertProblems`）→ **直接の呼び出しは 4 か所**（依頼の 3 か所ではない）。
- シェル（`scripts/**/*.sh`）で promtool を起動する行 → 0 件。
- scripts/ の外: `.github/workflows/ci.yml:779-780`（CI の段がリポジトリの規則ファイルに直に当てる。単体の試験の砂場の外で、1 回きりの
  ジョブの中。本件の範囲の外）。
- 陰性対照（`deploy-cloud-egress-audit.test.js`）→ **5 か所**: `:665`（証明書の期限 rabbitmq）・`:871`・`:874`・`:878`（report の段の 3 つ）・
  `:997`（オーナー用の secret_id の「記録なし」。問題の一覧に `/promtool/` を含むかで数えていた）。

## 方式

- #2527 の関数を `scripts/lib/promtool.js` へ移す（`promtool(args, { tmpRoot, cwd })`・`failedOnExpectation`・`INFRA_PATTERN`）。
  - 中身は #2527 と同じ（`spawnSync` に `cwd`＝リポジトリの根・`encoding`・`maxBuffer` 64 MiB・`TMPDIR`、`r.error` の文言、`infra` の正規表現、
    `why` の形、`failedOnExpectation`）。一時の置き場は `tmpRoot` の下の `promtool-tmp-*`。
  - `cloud-ops.test.js` は `const promtool = (args) => promtoolLib.promtool(args, { tmpRoot: sandbox })` と `failedOnExpectation` を受けるだけにする
    （呼び出し側・試験の数・振る舞いは変えない。#2527 の試験「promtool の呼び出し」は共通器をそのまま試す）。
- `deploy-cloud-egress-audit.test.js` の 4 か所を共通器へ。`tmpRoot` は各関数がすでに作って finally で消す一時のディレクトリ
  （`egress-report-alerts-*`・`cert-alerts-*`・`credential-alerts-*`）。
  - 陽性の失敗の文言は `why`。
  - 陰性対照 4 か所（`:665`・`:871`・`:874`・`:878`）は `assert.ok(failedOnExpectation(r), …\n${r.why})`。
  - `credentialAlertProblems` は、期待との食い違いだけを `promtool の単体試験が落ちた:` と言い、それ以外の失敗（置き場・シグナル・`FAILED:` の無い
    終了）は `promtool が期待の照合の外の理由で終わった（陰性対照に数えない）:` と言う（どちらも問題として返すので陽性の検査は赤になる）。
    陰性対照（`:997`）は前者の文言（`/^promtool の単体試験が落ちた:/`）だけを数える。
- `scripts/README.md` の lib の表に `lib/promtool.js` を足す。
- 時間切れの値は渡さない。試験の skip・期待の緩めはしない。

## 受け入れ基準

| # | 基準 | 確かめ方 |
| --- | --- | --- |
| AC1 | scripts/ の中で promtool を直に起動するのは `scripts/lib/promtool.js` だけ | `git grep -nIE "(spawnSync\|execFileSync\|execSync\|spawn\|execFile\|exec\|run)\(\s*['\"\`]promtool" -- scripts/` が lib の 1 か所 |
| AC2 | egress-audit の promtool は呼び出し側の一時のディレクトリの下の `promtool-tmp-*` を TMPDIR に使い、/tmp に一時の TSDB を残さない | TMPDIR を記録する代役で実測・流す前後の `/tmp/test_storage*` の数 |
| AC3 | 置き場の空きの不足（ENOSPC）で落ちた回を、5 か所の陰性対照のどれも「落ちた」と数えない（赤になり、理由を出す） | 陽性は実物・陰性は ENOSPC を返す代役で流す。develop の版は同じ代役で 3 件とも緑（黙って通る）だったことを並べる |
| AC4 | 期待との食い違いは今までどおり数える（陰性対照の変異を外すと赤・陽性の期待を壊すと赤で `FAILED:` を出す） | 一時の変異で実測して戻す |
| AC5 | 既存の試験が緑（egress-audit 18 件・cloud-ops 61 件）。cloud-ops の振る舞いは変えない | `node scripts/deploy-cloud-egress-audit.test.js` / `node scripts/cloud-ops.test.js` |
