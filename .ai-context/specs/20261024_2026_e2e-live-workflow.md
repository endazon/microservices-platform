---
title: 作業仕様書 — [E-32] e2e-live.yml ワークフローと成果物・skip 検査
type: spec
status: in-progress
related_ids: [NFR, NFR-18, ADR-0090, IADR-0569, IADR-0570, IADR-0624, IADR-0625, IADR-0248]
author: claude
created: 2026-10-24
updated: 2026-10-25
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0090（門は依存ごと。全 skip を緑にしない）
issue: "#2026"
---

# 作業仕様書 — live 層のワークフロー（E-32）

> 本仕様書は実装着手前に作成した（着手 2026-10-24）。親は #2013（設計はコメント 3 §7「実行の運用」・基準書 §11）。先行の E-31（#2025・IADR-0624）が
> live の config・setup・readiness 門・teardown を置いた。🔴 **本 PR は稼働中の PoC に触れない・秘密の値を投入しない**（コードと設定だけ）。

## 受け入れ基準（issue #2026）

- [ ] nightly が `integration-stack.yml` のスタック上で緑（マージ前は本 PR の `pull_request` の実行で確かめる）
- [ ] report と trace が `!cancelled()` で 14 日保存
- [ ] 全 skip が赤

## 判断

1. **スタック**: `integration-stack.yml` と同じ起動器（k3d ＋ `scripts/k8s-local-up.sh --live`、同じ opt-in）で、別のワークフロー・別のジョブとして起こす
   （integration-stack の門の列に E2E を足すと、45 分の上限と失敗の切り分けが重なる）。`check-stack-ready.js --live` を門にする。
2. **試験アカウント**: **リポジトリの secret を使わない。** ジョブの中で `gen-e2e-accounts.js`（IADR-0570）が乱数で作った値を Secret
   `platform-infra/e2e-accounts` に入れ、`reconcile-realm.sh` で 6 名を足す。値は `::add-mask::` で伏せ、runner の一時領域のファイル（`umask 077`）だけに置き、
   ジョブの終わりに消す（スタックも消える）。したがって `pull_request`（fork を含む）で走っても渡る秘密が無い。
3. **起動**: 日次（`47 19 * * *`。integration-stack の 18:30 と重ねない）は層 ro。`workflow_dispatch` の `layer` を `E2E_LAYER` へ渡す。
   `pull_request` は live の土台（このワークフロー・live の config と spec・3 つの検査器）を変える PR だけに `paths:` で絞る（必須にしない）。
4. **TLS**: 検証を外さない。ルート CA（`cert-manager/local-edge-root-ca`）を Node（`NODE_EXTRA_CA_CERTS`）と Chromium（NSS の DB）に信頼させる。
5. **全 skip**: 新設 `scripts/check-e2e-skips.js` が JSON レポートを読み、test を持つ project の全部が skip、または skip でない test が 0 件なら赤。
   選んだ層に spec がまだ無い（E-40 以降）のは警告だけ —— setup の 6 回の実ログインと teardown の失効は実際に走るので「0 件の緑」ではない。
6. **成果物**: html レポートと test-results を `!cancelled()` で 14 日。**上げる前に** 新設 `scripts/check-e2e-artifact-secrets.js` が資格情報の値
   （パスワード・TOTP の秘密）を全ファイルと中の zip まで走査し、載っていれば赤にして上げない。値そのものに加えて、TOTP の秘密の base32・各値の base64・
   Basic 認証の `e2e-<role>:<パスワード>` の base64 も探す。zip の入れ子が 4 段を超えたら例外（［2026-10-24 / PR #2226 の AI レビュー 🟡・🟢］）。
   試験アカウントを作る前に落ちた実行では走査も upload もしない（本当の失敗を隠さない）。作った後で値のファイルが無ければ赤。
7. **セッション cookie と保存期間**: 層の trace は cookie を必ず記録する（IADR-0624）。teardown が実行の終わりにログアウトで失効させ、失効できなければ
   Playwright の実行が exit 1（`continue-on-error` を付けない）でジョブが赤。さらにスタックはジョブの終わりに消える。BFF のセッションの寿命は 30 日
   （`BffSessionOptions.SessionLifetimeSeconds` 2592000）で成果物の 14 日より長いが、使い捨てのスタックでは cookie の発行元がジョブの終わりで無くなるので
   保存期間は基準書 §11 の 14 日のままにする。**常設の PoC へ流す経路を足すときは、この前提が崩れるので見直す**（IADR-0625）。
8. **権限**: ワークフロー単位で `contents: read` だけ。起票は PR では走らない `report-failure`（`ci-failure-issue.yml`。`issues: write`・`actions: read`）だけ。
9. **必須チェックにしない**（`docs/ai-workflow.md` の表に取り消し線で載せる）。

10. **初回の実行の失敗と直し方**（PR #2226 の `e2e-live`。詳しくは IADR-0625）: setup の 6 役割が `/realms/platform/login-actions/required-action` に残った。
    realm の `CONFIGURE_TOTP` が `defaultAction` で、宣言に `requiredActions` の無い試験アカウントへ既定で付いたため。6 名に `"requiredActions": []` を明示し、
    回帰の試験を足し、setup は必須アクションの画面で即座に理由つきで落ちるようにした。
    ［2026-10-25 追記 / #2026］宣言の `[]` だけでは直らなかった（3 回目の実行も同じ画面）。Keycloak の POST /users は宣言に無い action を外さない
    （`isUpdateExistingUser=false`）。`reconcile-realm.js` が作成の後に PUT で `requiredActions` を当て直すようにした（詳しくは IADR-0625 の追記）。

## 母集合（規則 9・10）

- 誤りの側の文字列: 「`e2e-live.yml`（**目標**」「**目標**: `scripts/check-e2e-skips.js`」「live のブラウザ E2E | なし」。`docs/`・`.ai-context/adr/`・`scripts/` を走査した。
  - 追随したもの: `docs/tests/E2E_STANDARDS.md` §6・§11・§12、`docs/ai-workflow.md` の必須チェック表、`scripts/README.md`、`scripts/scripts.repo.test.js`（検査器の数 64 → 66・権限の表）。
  - 除外したもの: IADR-0569（凍結）。

## 検証

```bash
node scripts/check-e2e-skips.js --self-test           # 7 件
node scripts/check-e2e-artifact-secrets.js --self-test  # 7 件
REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js
actionlint .github/workflows/e2e-live.yml
```

［2026-10-25 追記 / #2026］`check-e2e-artifact-secrets.js --self-test` は符号化形（base32・base64・Basic）、入れ子の上限、オフセット付きの形の正例・負例を足して **14 件**になった（上の「7 件」は起草時の数。`check-e2e-skips.js` は 7 件のまま）。

- 実物の Playwright の成果物で: 一時の spec で `fill(パスワード)` → 走査器が html レポート（埋め込みの zip の中を含む）・trace・error-context の 7 箇所を名指しした。
- 偽の BFF と Keycloak（`e2e/support/fakeLiveStack.ts`）に対して、一時の層の spec（1 回目に落ちて trace を残す）を足して live の config を流した:
  setup 6 passed・層 1 flaky・teardown 6 回ログアウト・生きているセッション 0。成果物 26 ファイルに資格情報の値 0 件、`check-e2e-skips` 緑。
  セッション cookie は 6 個中 1 個（層の spec の admin）が trace に載っていた（想定どおり。teardown で失効済み）。
