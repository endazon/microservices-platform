---
title: 作業仕様書 — dependabot の 3 本（@storybook/react-vite・sonner・tailwind-merge）を 1 本にまとめて通す
type: spec
status: done
related_ids: [NFR, ADR-0031, IADR-0134]
author: claude
created: 2026-11-07
updated: 2026-11-07
---

# 作業仕様書 — フロントの依存 3 本をまとめて上げる

> 本仕様書は実装着手前に作成した（着手 2026-11-07）。利用者の裁定「Dependabot は監査してそのままマージ」のうち、そのままでは通らない 3 本を通す形にする。

## 置き換える Dependabot PR

| PR | 更新 | そのままで通らない理由 |
| --- | --- | --- |
| #2483 | `@storybook/react-vite` 10.5.6 → 10.6.5（`src/packages/ui`） | lockfile の部分更新で `@babel/types` が 7.29.8 と 7.29.11 に割れ、`@lingui/core` / `@lingui/react` が peer の組で 2 実体になる |
| #2484 | `sonner` 2.0.7 → 2.0.8（`src/platform/frontend`） | 単独なら develop の床に収まるが、#2486 と合わせると `check-chunk-budget` の床を超える |
| #2486 | `tailwind-merge` 3.6.0 → 3.7.0（`src/packages/ui`） | 同上 |

## 事象と原因（実測）

### #2483: RiskSettingsPage の単体試験が落ちる（古いベースの問題ではない）

- Dependabot の rebase 後（head 0f0990a、base 44facea09）でも再現した。`vitest run ai-stock-trading/frontend/src/features/sc02-risk-settings` が 171 件中 157 件失敗する（`Lingui: Attempted to call a translation function without setting a locale.`）。
- 原因: 更新で `@babel/types@7.29.11` が新たに入り、platform・knowledge・packages の importer は `@lingui/core@6.6.0(@babel/core@7.29.7)(@babel/types@7.29.11)(…)` へ移った。submodule の `ai-stock-trading/frontend` の importer は `…(@babel/types@7.29.8)(…)` の組に残った。`node_modules/.pnpm` に `@lingui/core` が 2 実体でき、試験の準備が `activate` した実体と画面が読む実体が別になる。
- `storybook build` の `ERR_PACKAGE_PATH_NOT_EXPORTED` は本体との版ずれが原因で、develop の storybook 10.6.5（#2485）の上では起きない。

### #2484・#2486: 初期ロード合計の床を超える

同じ環境で develop 44facea09 から 1 本ずつ当てて、`dist/index.html` が読む 4 本の JS を測った（バイト）。

| 構成 | index | vendor-react | ui | vendor-query | 合計 | 床 660,917 との差 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| develop | 337,518 | 197,982 | 78,539 | 45,634 | 659,673 | −1,244 |
| ＋sonner | 338,504 | 197,982 | 78,539 | 45,634 | 660,659 | −258 |
| ＋tailwind-merge | 337,518 | 197,982 | 78,907 | 45,634 | 660,041 | −876 |
| ＋両方 | 338,504 | 197,982 | 78,907 | 45,634 | 661,027 | **+110** |
| ＋3 本（本 PR） | 338,504 | 197,982 | 78,907 | 45,634 | 661,027 | +110（ハッシュまで「両方」と同一） |

- 古いベース（8cca73e）の CI では sonner +0.99 kB・tailwind-merge +0.37 kB で床を超えていた。develop は床より 1,244 B 下にあるため、どちらか 1 本なら収まる。2 本目で超える。
- 分割では減らない。sonner の `Toaster` は `Layout.tsx`（初期ロード）が `components/ui/notifications.tsx` 経由で静的に読む。tailwind-merge は ui チャンクの `cn()` が読む。別チャンクへ切り出しても `modulepreload` に載り、初期ロード合計に数えられる。

## 対応

1. `@storybook/react-vite` を `^10.6.5` へ上げる（#2483 の package.json と lockfile）。
2. `pnpm update -r --depth Infinity @babel/types` で `@babel/types` を 7.29.11 の 1 版にそろえる。lockfile の差分は `@babel/types@7.29.8`・`@babel/helper-string-parser` 2 版の 1 版化と、`@lingui/*` の 7.29.8 側の組 3 件の削除だけになる（`pnpm dedupe` は `@testing-library/*` ほかも動かすので採らない）。
3. `sonner` `^2.0.8`・`tailwind-merge` `^3.7.0` を `pnpm add` で上げる。lockfile の差分は Dependabot の #2484・#2486 と同一。
4. `node scripts/check-chunk-budget.js --update` で床を 660,917 → 661,027 B へ上げ、`chunk-budget-baseline.json` に帰属の記録を足す（IADR-0134 のラチェット手順どおり。IADR は作らない）。

## 受け入れ基準

- [x] `@lingui/core@6.6.0(` の組が lockfile に 1 つだけである
- [x] sc02-risk-settings の試験が 171 件すべて通る
- [x] `check-chunk-budget --require` が通り、床の更新に帰属の記録がある
- [x] `pnpm install --frozen-lockfile`・lint・typecheck・format:check・test:coverage・build・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が通る

## 範囲外

- knowledge の `QdrantGrpcReadyTests`（#2486 の CI で 1 件落ちた）は依存の差分と無関係である。再実行（run 42284103334 attempt 2）で通った。試験は接続を受けてすぐ切る相手に `Unavailable` を期待するが、`Internal` が返る競合がある。本 PR では扱わない。
