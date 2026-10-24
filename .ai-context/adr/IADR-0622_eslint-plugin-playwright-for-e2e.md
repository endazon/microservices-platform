---
title: IADR-0622 E2E に eslint-plugin-playwright を適用し、待機・nth・skip 理由の規約を error で機械強制する
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0569, IADR-0275, IADR-0621]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md（§採用技術一覧の Linter 欄）
related_specs:
  - ../specs/20261024_2038_eslint-plugin-playwright.md
---

# IADR-0622: E2E の ESLint 規則（#2038）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-24
- 決定者: claude（#2038。親 #2013 の E-74）

## 起点・関連

- 起点 issue: #2038（親 #2013）。関連する計画書 ID: NFR（無採番）。
- 関連する実装判断: [IADR-0569](./IADR-0569_e2e-layers-and-live-opt-in.md)（E2E の実行層）・
  [IADR-0275](./IADR-0275_plop-feature-generator-and-lint-plugin-scope.md)（ESLint のプラグインの射程。submodule を外す線引き）。
- 作業仕様書: [20261024_2038_eslint-plugin-playwright](../specs/20261024_2038_eslint-plugin-playwright.md)。

## コンテキストと課題

基準書（`docs/tests/E2E_STANDARDS.md`）は `waitForTimeout`・`nth()` を使わないこと、`test.skip` に理由を書くことを定めるが、
機械の検査が無かった。導入前の実測で `nth()` / `first()` が 13 件、web-first でない比較が 11 件あった。

## 検討した選択肢

| 論点 | 案 | 採否 |
| --- | --- | --- |
| 既存の違反 | A. 抑制ファイル（`eslint-suppressions.json`）へ退避する | 採らない。直せる違反であり、退避する理由が書けない |
| | **B. spec を直す** | **採る**（25 件） |
| `no-wait-for-timeout` | A. recommended の warn のまま | 採らない。warn は CI を止めない（新規混入が止まらない） |
| | **B. error** | **採る** |
| skip | A. recommended の warn | 採らない。L1 の skip は 0 件が基準 |
| | B. `no-skipped-test` を全 e2e で `allowConditional: true` の error | 採らない。L1 でも `test.skip(cond, '理由')` が通り、「L1 の skip は 0」を機械で守れない（PR #2221 の AI レビュー 🟡） |
| | **C. 既定は `allowConditional` なしの error。門が要る層（`*.kc.ts`・`e2e/live/`）だけ `allowConditional: true`** | **採る** |
| CSS セレクタ | `no-raw-locators` を足す | 採らない。Keycloak のテーマの画面は Keycloak の markup を id で引くしかない |

## 決定

1. `eslint-plugin-playwright`（2.12.1）を `src/package.json` の devDependencies に足し、`src/eslint.config.js` で `**/e2e/**/*.ts` に recommended を適用する。`ai-stock-trading/**` は外す（IADR-0275 と同じ線引き）。
2. recommended に重ねる規則は `PLAYWRIGHT_RULE_OVERRIDES` に置く: `no-wait-for-timeout`・`no-nth-methods`・`require-annotation-reason`・`no-skipped-test` を error。
   `no-skipped-test` は**条件つきも含めて**止める（L1 の skip は 0 件）。
3. 前提（門）による条件つき skip を許すのは `PLAYWRIGHT_GATED_SKIP_FILES`（`**/e2e/keycloak/**/*.kc.ts` と `**/e2e/live/**/*.ts`）だけで、
   そこでは `no-skipped-test` を `allowConditional: true` にする。理由は `require-annotation-reason` が引き続き必須にする。
4. 既存の違反は spec を直して 0 件にする。抑制ファイルへは載せない。

## 理由

- 基準書の「目標」を、規約を緩めずに配備できる（直せない違反が無かった）。
- 層で分けることで、L1 の skip 0 件を機械で守りつつ、live 層と Keycloak の門（前提が満たされない時の条件つき skip）を書く余地を残す。

## 影響

- 新しい spec で `waitForTimeout`・`nth()`・理由の無い skip を書くと `pnpm run lint`（CI の `build-test`）が赤くなる。
- recommended の warn 規則（`expect-expect` など）は warn のまま残る（導入時点で e2e の warn は 0 件）。

## 残余リスク

- AST（submodule）の e2e には効かない。AST 側で採るかは AST の判断である。
