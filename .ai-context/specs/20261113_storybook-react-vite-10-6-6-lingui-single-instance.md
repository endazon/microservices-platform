---
title: 作業仕様書 — dependabot #2538（@storybook/react-vite 10.6.6）の lockfile を、@lingui/* が 1 実体のまま通る形に解き直す
type: spec
status: done
related_ids: [NFR, ADR-0031]
author: claude
created: 2026-11-13
updated: 2026-11-13
---

# 作業仕様書 — @storybook/react-vite 10.6.6 の lockfile を解き直す

> 本仕様書は実装着手前に作成した（着手 2026-11-13）。#2511（2026-11-07）と同型の事象である。

## 事象と原因（実測）

- #2538 の CI の `test` が、`Lingui: Attempted to call a translation function without setting a locale` で落ちる。
  Dependabot の head（478b613b5）で `vitest run ai-stock-trading/frontend/src/features/sc02-risk-settings` が 171 件中 157 件失敗する。
- 原因: Dependabot の lockfile 更新が、版の変わっていない `react-docgen@8.0.4` の依存まで解き直し、
  `@babel/core@7.29.13`（＋ `@babel/{generator,helpers,parser,traverse,helper-module-transforms}@7.29.13`）を新たに入れた。
  `@lingui/core` / `@lingui/react` は `@babel/core` を peer に持つため、platform・knowledge・sample-unit の importer は
  `@lingui/core@6.6.0(@babel/core@7.29.13)(…)` の組へ移り、submodule の `ai-stock-trading/frontend`（`@babel/core` を直接宣言し 7.29.7 に固定）は
  `…(@babel/core@7.29.7)(…)` の組に残った。`node_modules/.pnpm` に `@lingui/core@6.6.0` が 2 実体でき、試験の準備が `activate` した実体と画面が読む実体が別になる。

## 対応

1. develop の `pnpm-lock.yaml` から、`@storybook/react-vite` だけを 10.6.6 に解き直す（`pnpm install --lockfile-only`。10.6.7 へ進まないよう一時的に完全一致で解いてから `^10.6.6` に戻す）。
   lockfile の差分は `@storybook/{react-vite,react,react-dom-shim,builder-vite}` の 10.6.5 → 10.6.6 だけ（+21/−21）になり、`@babel/core` は 7.29.7 の 1 版のまま残る。
2. `pnpm dedupe` は採らない（`@typescript-eslint/*`・`@testing-library/*`・`@lingui/cli` ほかも動かす）。`pnpm update -r --depth Infinity @babel/core` も採らない
   （submodule の `package.json` の specifier を書き換え、`@babel/types` を 7.29.14 へ進めて別の組の割れを作る。実測）。

## 受け入れ基準

- [x] `@lingui/core@6.6.0(` の組が lockfile に 1 つだけである
- [x] sc02-risk-settings の試験が 171 件すべて通る
- [x] `pnpm install --frozen-lockfile`・lint・typecheck・format:check・test・test:coverage・build・build-storybook・knip ラチェット・chunk 予算・i18n カタログ検査が通る

## 範囲外

- `@storybook/react-vite` 10.6.6 は peer に `storybook ^10.6.6` を求めるが、`storybook` 本体は 10.6.5 のままである（Dependabot の lockfile と同じ）。`build-storybook` は通る。本体の更新は別の Dependabot PR に任せる。
- `Integration` の `TraceContinuityTests` の失敗はバックエンドの試験であり、本変更と無関係である。触らない。
