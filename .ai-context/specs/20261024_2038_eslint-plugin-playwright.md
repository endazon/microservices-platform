---
title: 作業仕様書 — [E-74] ESLint の eslint-plugin-playwright（no-wait-for-timeout・no-nth-methods・skip 理由必須）を e2e に適用する
type: spec
status: in-progress
related_ids: [NFR, IADR-0569, IADR-0622, IADR-0275]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/06_technical/13_frontend-stack.md（§採用技術一覧の Linter 欄）
issue: "#2038"
---

# 作業仕様書 — Playwright 用の ESLint 規則（E-74）

> 本仕様書は実装着手前に作成した（着手 2026-10-24）。親は #2013。基準書は `docs/tests/E2E_STANDARDS.md` の §3・§4・§10・§12。

## 受け入れ基準（issue #2038）

- [ ] 既存 spec が lint を通る（必要なら同 PR で直す）。`waitForTimeout` の新規混入が止まる

（CI が緑になってから印を付ける。マージをもって完了とする。）

## 判断

1. **依存**: `eslint-plugin-playwright@2.12.1`（2026-10-05 公開。19 日前）を workspace ルート `src/package.json` の devDependencies に足す。ESLint の他のプラグインと同じ置き場所で、版は lockfile が固定する。peer は `eslint >=8.40.0`（本リポは 9 系）。
2. **射程**: `**/e2e/**/*.ts`（L1 の `*.smoke.spec.ts`・Keycloak のテーマの `*.kc.ts`・`support/`）。`ai-stock-trading/**` は外す（別リポの submodule。Testing Library・TanStack と同じ線引き。IADR-0275）。
3. **規則**: recommended に重ねて、基準書が「機械で止める」と書いた 3 つを error にする。
   - `playwright/no-wait-for-timeout`（recommended では warn。warn では CI が止まらない）
   - `playwright/no-nth-methods`（recommended に無い）
   - `playwright/require-annotation-reason`（skip / fixme の理由。recommended に無い）
   - あわせて `playwright/no-skipped-test` を `allowConditional: true` で error にする（L1 の skip は 0。live は前提による条件つき skip だけを許す。基準書 §10）。
4. **既存の違反は spec を直して解消する。** 無効化・抑制ファイル（`eslint-suppressions.json`）への退避はしない。導入時点の違反は 25 件（error 24・warn 1）だった。

| 規則 | 件数 | 直し方 |
| --- | --- | --- |
| `prefer-web-first-assertions`（recommended の error） | 11 | `expect(await x.allTextContents()).toEqual([...])` → `await expect(x).toHaveText([...])`。配列の `toHaveText` は件数と順序ごと一致を見るので、同じ強さで待つ形になる |
| `no-nth-methods` | 13 | SC-11 の列は `row.getByRole('cell')` を `toHaveText([種別, 深刻度, 対象, 詳細])` で 1 回に見る（詳細の列も見るようになった）。SC-19 の候補は件数 1 を確かめた上で `toHaveText([...])` / `toContainText([...])` と `click()`。SC-06 の a11y は表と行で絞る（sc06 の spec の `rowOf` と同じ形）。i18n のナビは唯一の navigation をそのまま使い、リンクは `filter({ visible: true })` の件数で待つ。Keycloak のテーマは送信ボタンをフォームの中で役割と名前（`送信`）で引く。デバイスの活動は `filter({ visible: true })` の件数で待つ |
| `consistent-spacing-between-blocks`（warn） | 1 | 空行を足す |

## 母集合（規則 9・10）

- 誤りの側の文字列: 「**目標**: ESLint の」「現状は規約だけ」「ESLint（Playwright 用ルール） | なし」。`docs/`・`.ai-context/adr/`・`src/eslint*.js` を走査した。
  - 追随したもの: `docs/tests/E2E_STANDARDS.md` の §3・§4・§10・§12。
  - 除外したもの: IADR-0569（規則に触れていない）。
- 本変更で新たに誤りになる自分の記述: 基準書 §3 の「CSS セレクタ・XPath を使わない」は規約のまま（`no-raw-locators` は採らない。Keycloak のテーマの画面は Keycloak の markup を id で引くため）。§3 にその旨を書いた。

## 検証

```bash
cd src && pnpm install --frozen-lockfile && pnpm run build
cd src && CI=1 pnpm --filter @platform/frontend exec playwright test           # 185 passed (1.7m)
cd src && pnpm run lint && pnpm run lint:templates && pnpm run typecheck && pnpm run format:check && pnpm run format:templates
cd src && pnpm run test:coverage                                               # 161 files / 2039 tests passed
node scripts/check-knip.js --require                                           # 床どおり
```

- Keycloak のテーマの spec（`*.kc.ts`）は使い捨ての Keycloak が要り、手元では走らない（docker が無い）。`keycloak-theme-a11y.yml` は `src/platform/frontend/e2e/keycloak/**` と `src/pnpm-lock.yaml` の変更で起動するので、本 PR の CI で確かめる。
- 変異（一時ファイルを置いて lint し、消した）: `page.waitForTimeout(100)` → `no-wait-for-timeout` の error。`.nth(0)` → `no-nth-methods` の error。条件の無い `test.skip(...)` → `no-skipped-test` の error。理由の無い `test.fixme()` → `require-annotation-reason` の error。
