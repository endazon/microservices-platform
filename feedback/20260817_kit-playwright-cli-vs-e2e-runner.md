---
title: キットの「ブラウザ操作は playwright-cli に統一」が、CI で @playwright/test を運用する配布先と衝突する
type: plan-feedback
status: open
category: 新たな制約(ADR要)
related_ids:
  - NFR
  - ADR-0030
  - IADR-0033
  - IADR-0221
source_repo: endazon/microservices-platform
source_ref: "chore/NFR-mcp-skill-deployment-pin-2c78212 / #847 / docs/adr/IADR-0221_playwright-cli-vs-test-runner-scope.md"
author: claude
created: 2026-08-17
dispatched: true
planning_issue: planning#409
---

# フィードバック: キットの Playwright 統一指示が確定済み ADR と衝突する

## 種別

新たな制約（ADR が必要）

## 起点となる計画書

- 機能要求（FR）: なし
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: `ADR-0030`（バックエンド標準構成・ブランチ起点）/ `IADR-0033`（CI の E2E に `@playwright/test`）/ `IADR-0221`（本件の決着）
- 計画書リンク: planning#399（スキル・MCP の採否確定・2026-08-17）/ キット HOWTO §B-3.5

## 現状（計画書の記述 / As-Is）

planning#399 の追随として配布された HOWTO §B-3.5 は、**ブラウザ操作を `playwright-cli` に統一する**と述べている。

本リポは `src/platform/frontend` で **`@playwright/test` による E2E を CI で運用中**である（`IADR-0033`。`.github/workflows/frontend.yml` の e2e ジョブ）。

## 問題点 / あるべき姿（To-Be）

**「AI がブラウザを操作する手段」と「CI が回す E2E テストランナー」は別の関心事**であり、統一すべき対象が違う。キットの文面は両者を区別していないため、配布先は次のいずれかを選ばざるを得ない。

- キットに従って `@playwright/test` を捨てる → **確定済み `IADR-0033` の無断逸脱**
- キットに従わない → **追随漏れとして分類表に記録が要る**

**あるべき姿は役割の棲み分けである。**

| 用途 | 手段 |
| --- | --- |
| CI の E2E テスト | **`@playwright/test` を継続**（`IADR-0033` を覆さない） |
| AI のブラウザ操作 | **`playwright-cli` + Skills** |
| Playwright MCP | **導入しない** |

## 実装で判明した経緯

#847（計画 pin の追随・スキル / MCP の配備）でキット HOWTO §B-3.5 を取り込む際に判明した。本リポ側は役割で棲み分ける実装 ADR `IADR-0221` を起こして決着させた。

あわせて **`@playwright/cli` を `package.json` に加えない**と決めた —— CI のどのジョブも起動せず、pnpm workspace に 2 つ目の Playwright が入るためである（`frontend.yml` に記録済みの `ERR_PNPM_RECURSIVE_EXEC_FIRST_FAIL` の罠もある）。将来入れる場合は `src/platform/frontend` へ入れ `pnpm --filter @platform/frontend exec` で起動する。

## 提案（計画への反映案）

- 反映先候補: **キット HOWTO 更新** / （任意）運用ガイド更新
- 提案内容:
  1. HOWTO §B-3.5 を**「役割の棲み分け」**へ改める。「ブラウザ操作は `playwright-cli` に統一」ではなく、**「AI の対話的なブラウザ操作は `playwright-cli`。CI の E2E テストランナーはリポジトリの既存選択を覆さない」**とする。
  2. 既に E2E ランナーを CI で運用している配布先向けの注記（併存してよい / `@playwright/cli` を workspace ルートへ入れると pnpm の再帰実行で事故る）を加える。
  3. （任意）**キット側で「統一」と書くときは、それが確定済み ADR を覆し得る指示かどうかを確認する規律**を運用ガイドへ足すことを検討する。

## 影響範囲

- **`@playwright/test` を CI で運用している他の配布先すべて**に同じ衝突が起こる（運用ガイド §11 のパリティ対象）。
- 本リポでは `IADR-0221` として決着済みだが、**キット文面が直らない限り、次の配布先が同じ判断をやり直す**。
