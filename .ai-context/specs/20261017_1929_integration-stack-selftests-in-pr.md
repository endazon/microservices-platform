---
title: 後段（integration-stack / integration）の門の自己試験を PR の static-checks でも走らせる（#1929 の再発防止）
type: spec
status: done
related_ids: [NFR, IADR-0232, IADR-0429, IADR-0546, SC-15]
author: claude
created: 2026-10-17
updated: 2026-10-17
plan_refs: []
issue: "#1929"
---

# 仕様書: 後段の門の自己試験を PR でも走らせる

## 起点

- #1929: develop の integration-stack が落ち続けている（自動起票。コメント 10 件）。
- 赤の run を `actions/runs/<id>/jobs` と job のログで分類した。赤は 2 回の連なりであり、どちらも**本スレッドの起票時点で既に直っている**。

| 連なり | 赤の run（develop） | 落ちたステップ | 原因の PR | 直した PR |
| --- | --- | --- | --- | --- |
| 1 | 38574914487（`186b57f7`）・38578640408（`3ebeaa8e`） | 17「Gate — ABAC の正常系と検索の命中」 | #1925（文書一覧をページで返す） | #1930（`8b06c8b7`。検証器がページを辿る） |
| — | 38729082264 attempt 1（`02b4d146`） | T-25 の偶然の赤 | — | 再実行で緑（ADR-0118 決定 2。issue に記録済み） |
| 2 | 38750063758（`d3addc82`）・38753189871・38755602741・38761582832・38761640946（`1721e667`） | 7「Self-test the password-reset mail gate」 | #1944（realm に組み込みの `account` / `account-console` を足した） | #1950（`ac9e795a`。組み込みクライアントを選びから外す） |

- `ac9e795a` 以降、develop の integration-stack は 10 本（push 8・schedule 2）すべて緑である（最新 38969742127・`7fa45d22`）。

## 根本原因（連なり 2）

- 連なり 2 は**門の自己試験**（`--self-test`）の赤である。クラスタは関係ない。ログは
  `[check-password-reset-mail] 実行時エラー: AssertionError: public client が復活している（IADR-0429 に反する）`。
- 自己試験は実データ（realm の宣言）をラチェットとして読む。だから門の器を触らない #1944 でも赤になった。
- **この自己試験は PR の CI では走らず、integration-stack（push: develop・日次・dispatch）でだけ走る。** #1944 の PR は緑のままマージされ、develop が赤になった。#1950 の仕様書も同じことを書いている。
- 手元で再現した: `d3addc82` で `node scripts/check-password-reset-mail.js --self-test` は exit 1（上と同じ文言）、親 `28342843` は exit 0。クラスタ・helm・kubectl・submodule の無い環境で再現する。
- 連なり 1 は稼働スタックへの本走査（seed の後の門）の赤であり、PR で前倒しできない（IADR-0232 決定 1 の回収先そのもの）。本件の対象外。

## 母集合（規則 9）

- 後段のワークフロー（`on:` に `pull_request` を持たない）で `node scripts/*.js --self-test` を呼ぶものを `grep -n "self-test" .github/workflows/*.yml` で走査した。
  - `integration-stack.yml`: `check-stack-ready.js` / `check-password-reset-mail.js` / `check-login-existence-disclosure.js` / `check-mcp-client-provisioning.js`。いずれも PR の CI に無い。
  - `integration.yml`: `check-coverage-floor.js`（`ci.yml` の集約 `build-and-test` が呼ぶ。対象外）／`check-integration-executed.js`（PR に無い）。
  - 除外: `integration-stack-rerun.yml`・`backlog-audit.yml`・`ci-latency-watch.yml`・`cutover-rehearsal.yml` の自己試験。前倒しの対象は「PR が壊し得る実データを読む門」で、本件の型（宣言の変更で後段だけ赤）に当たるのは上の 2 ワークフローである。あとで同型の事故が起きたら母集合へ足す（同型 2 回で検査器を足す運用）。
- 5 本の自己試験は、クラスタ・helm・kubectl・submodule の無い環境で全件緑・計約 1.2 秒だった（手元の実測）。

## 変更

1. `ci.yml` の `static-checks` に上の 5 本の自己試験を `if: ${{ !cancelled() }}` つきで足す。ジョブは増やさない。後段の自己試験は残す（高価な起動の前に門自身を確かめる役）。
2. `scripts.repo.test.js` に #1929 節を足す。
   - 後段の 2 ワークフローが `pull_request` で起動しないこと（前倒しが要る前提）。
   - 後段で `--self-test` を呼ぶ検査器は、すべて `static-checks` で `!cancelled()` つきで呼ばれること。列挙は持たず走査する。0 件走査は赤。
   - 陰性対照: 呼び出しを 1 本外す・`!cancelled()` を外すと検出する。
3. `scripts/README.md` の「CI が呼ぶ検査」の表に行を足す。
4. IADR-0232 に 2026-10-17 の追記を置く（決定 1 の表に載っていない「落としたもの」を PR へ戻す。#1551 の追記と同じ形）。

## 受け入れ基準

- AC1: PR の CI（`static-checks`）が 5 本の自己試験を走らせる。
- AC2: 後段に自己試験を足して PR 側への配線を忘れると、`scripts-tests` が赤になる。
- AC3: integration-stack が本ブランチで緑である（`workflow_dispatch`）。

## 検証

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`: 1100 件緑。
- 変異: `ci.yml` の `check-login-existence-disclosure.js --self-test` を `true` に替えると #1929 節が赤になり、戻すと緑。
- 再現: `d3addc82` の自己試験 exit 1／`28342843` exit 0（#1944 の PR に本変更が在れば PR の段階で赤になっていた）。
- integration-stack を本ブランチで `workflow_dispatch` し、結果を PR に記録する。
