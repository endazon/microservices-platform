---
title: 作業仕様書 — [E-71] frontend.yml の e2e を fullyParallel・2 シャード・blob→merge・成果物常時・timeout-minutes にする
type: spec
status: in-progress
related_ids: [NFR, IADR-0569, IADR-0621, IADR-0232]
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0090（門は依存ごと。全 skip を緑にしない）
issue: "#2036"
---

# 作業仕様書 — L1 E2E のシャードと成果物（E-71）

> 本仕様書は実装着手前に作成した（着手 2026-10-24）。親は #2013。基準書は `docs/tests/E2E_STANDARDS.md` の §9〜§12。

## 受け入れ基準（issue #2036）

- [x] PR の e2e が 2 シャードで 10 分以内。report と trace が常時 14 日。必須チェック名（ジョブ名）が `docs/ai-workflow.md` の表と一致
  - 実測（PR #2220・head `e9e9a1b3`）: `e2e-shard (1/2)` 1 分 38 秒、`e2e-shard (2/2)` 1 分 40 秒、集約 `e2e` 31 秒（シャードの開始から集約の終了まで 2 分 23 秒）。
  - 「trace が常時」の意味: 成果物（blob・html）のアップロードが成否に関わらず常時である。trace 自体は `on-first-retry`（基準書 §9 の L1 の値）なので、再試行した test にだけ残る。

（CI が緑になってから印を付ける。マージをもって完了とする。）

## 判断

1. **check 名 `e2e` は、シャードをまとめる集約ジョブに残す。** シャードの脚はジョブ ID `e2e-shard`（表示名 `e2e-shard (N/2)`）にする。
   - `docs/ai-workflow.md` の必須チェック表に `e2e` は**無い**（`frontend.yml` は `paths:` を持ち、必須にできない）。したがって必須チェック名は本 PR で変わらない。
   - それでも `e2e` を残すのは、基準書 §11 などの文書が `e2e` ジョブを名指ししており、`check-workflow-job-refs.js` の面 C がその実在を突合するためである。
   - 集約ジョブは `if: !cancelled()` で走るため、最後のステップで `needs.e2e-shard.result` が `success` であることを確かめる。これが無いとシャードが赤でも `e2e` が緑になる。
2. **reporter**: CI のシャードは `blob` ＋ `list`、集約ジョブの `merge-reports` が `html` ＋ `github`。シャードでも `github` を出すと、注釈が二重になる。
3. **`fullyParallel: true`**。ファイル単位の配分では、本数の偏りがシャードの所要の偏りになる。spec の中にファイル内で共有する状態（モジュール直下の `let`・`describe.serial`・`beforeAll`）は 0 件だった（`grep -rn "describe.serial\|describe.configure\|beforeAll\|^let " e2e`）。
4. **時間の上限**: ジョブの `timeout-minutes: 20`（基準書 §12）。`globalTimeout` を CI で 15 分（基準書 §4）。後者が先に切れるので、どの test で止まったかがレポートに残る。
5. **シャードの起動はパッケージの文脈で `playwright test --shard=N/2` を直接呼ぶ。** ルートの `pnpm run test:e2e` へ引数を渡すと `--` の扱いが pnpm の版で揺れる。
6. **集約ジョブも `pnpm install --frozen-lockfile` を行う**（`merge-reports` は `@playwright/test` を要する）。workspace の全メンバが要るので submodule も取る。ブラウザは要らない。
7. **基準書 §2.1・§11 の「L1 は必須チェック」を訂正する。** 実際は必須チェックではない（`docs/ai-workflow.md` の表が正本）。受け入れ基準の「表と一致」を確かめる途中で見つけた食い違いである。IADR-0569 決定 1 の表の同じ記述は、IADR-0621 で訂正を記録する（IADR-0569 の本文は凍結記録なので書き換えない）。

## 母集合（規則 9・10）

- 誤りの側の文字列: 「`e2e` ジョブ」「必須チェック | **はい**」「アップロードしていない」「`fullyParallel` | 未指定」「`timeout-minutes` | なし」。`docs/`・`.github/`・`scripts/`・`.ai-context/adr/` を走査した。
  - 追随したもの: `docs/tests/E2E_STANDARDS.md` の §2.1（必須チェックの記述）・§9（reporter・成果物）・§11（L1 の行）・§12（3 行と、シャードの行の追加）。
  - 除外したもの: IADR-0569 決定 1 の表（凍結記録。IADR-0621 で訂正を記録）。`docs/ai-workflow.md`（`e2e` を載せておらず、正しい）。
- 本変更で新たに誤りになる自分の記述: 基準書 §10 の「1 シャードあたり 10 分以内」は目標のまま正しい。§9 の「シャード時に」は、シャードが常時になったので「シャードごとに」へ書き換えた。

## 検証

```bash
cd src && pnpm install --frozen-lockfile && pnpm run build
cd src && CI=1 pnpm --filter @platform/frontend exec playwright test --shard=1/2   # 93 passed (1.0m)
cd src && CI=1 pnpm --filter @platform/frontend exec playwright test --shard=2/2   # 92 passed (40s)
cd src && GITHUB_ACTIONS=true pnpm --filter @platform/frontend exec playwright merge-reports --config playwright.config.ts --reporter html,github ./all-blob-reports
#   → ::notice title=🎭 Playwright Run Summary::  185 passed (1.8m)、playwright-report/index.html
actionlint .github/workflows/frontend.yml
REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js
```

- CI の所要（2 シャードそれぞれ 10 分以内）は PR の実行で確かめ、PR 本文に書いた（上の受け入れ基準の実測）。
