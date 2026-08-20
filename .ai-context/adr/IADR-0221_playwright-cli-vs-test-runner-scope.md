---
title: IADR-0221 Playwright は「AI のブラウザ操作＝CLI」「CI の E2E＝テストランナー」で棲み分け、CLI はリポジトリ管理下に置かない
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0033
  - IADR-0121
  - IADR-0179
plan_refs:
  - planning:draft/cross-project/20260817_skill-mcp-adoption-decision.md
  - planning:tools/impl-handoff-kit/repo-template/AI_SETUP.md (§4-3)
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)
author: claude
created: 2026-08-17
updated: 2026-08-17
---

# IADR-0221: Playwright CLI とテストランナーの棲み分け

- 状態: Accepted
- 日付: 2026-08-17
- 決定者: 実装担当（AI）／計画側の採否確定（planning#399・利用者裁定 2026-08-17）を受けた棲み分けの決定

## 起点・関連

- 関連する計画書 ID: **`NFR`（無採番）** —— 開発ツールの選定という**工程の統制**であり、計画側の
  非機能要件表（`NFR-01`〜`NFR-27`）に当たる番号が無い（[IADR-0179](./IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 1）。
- 計画側の判断記録: `planning/draft/cross-project/20260817_skill-mcp-adoption-decision.md`
  （Playwright MCP は**見送り**、Playwright CLI + Skills を**採用**）
- 関連 IADR: [IADR-0033](./IADR-0033_frontend-spa-foundation.md)（フロント SPA 基盤。**Playwright を
  スクリーンレベル e2e として採る**と定めた ADR）・
  [IADR-0121](./IADR-0121_spa-stack-migration-staging.md) 決定 2（パッケージマネージャは pnpm workspace）

## コンテキストと課題

キットが配布する `AI_SETUP.md` §4-3 は次を定める。

> E2E・UI 確認のブラウザ操作は **Playwright CLI**（`playwright-cli`）を使う。**Playwright MCP は導入しない**

**この文言をそのまま本リポへ適用すると、既存の E2E 資産と衝突する。** 本リポは
`src/platform/frontend` で `@playwright/test`（テストランナー）による E2E を**既に CI で運用している**
（`playwright.config.ts` / `e2e/` / `frontend.yml` の `E2E smoke` ジョブ。[IADR-0033](./IADR-0033_frontend-spa-foundation.md)）。

キットの文言は**コーディングエージェントのブラウザ操作手段**を CLI へ寄せる趣旨であり、
**CI の検証資産をテストランナーから CLI へ移せ**という趣旨ではない。しかし文面だけでは読み分けられない。
「統一する」と書いてある以上、次に読む者が既存 `e2e/` を移設対象と解する余地が残る。

さらに、キットの導入手順 `npm i -D @playwright/cli@latest` は **npm を前提としており、本リポの
pnpm workspace では素直に成立しない**。`frontend.yml` には同型の罠が実測つきで記録されている。

> `@playwright/test` は workspace ルート（`src/`）ではなく `platform/frontend` の devDependency であり、
> pnpm は npm と違って各パッケージの `.bin` しか見せないため、`src/` での素の `pnpm exec playwright` は
> バイナリを解決できず `ERR_PNPM_RECURSIVE_EXEC_FIRST_FAIL` で落ちる（PR #489 の CI で実測）。

## 検討した選択肢

| # | 案 | 内容 | 評価 |
| --- | --- | --- | --- |
| 1 | **棲み分けを明文化し、CLI はリポジトリ管理下に置かない** | 役割で分ける。CLI は各自導入 | **採用** |
| 2 | キットの文言どおり CLI へ全面統一 | 既存 `e2e/` を CLI へ移設 | 不採用。CI の検証資産を失う期間が生じ、[IADR-0033](./IADR-0033_frontend-spa-foundation.md) を覆す根拠が無い |
| 3 | `@playwright/cli` を `src/platform/frontend` の devDependency に加える | リポジトリ管理下に置く | 不採用（下記） |
| 4 | CLI を導入せず MCP も入れない | ブラウザ操作の手段を持たない | 不採用。AI の UI 確認手段が無くなる |

## 決定

### 決定 1: 役割で棲み分ける

| 用途 | 手段 |
| --- | --- |
| **CI の E2E テスト**（退行検知の検証資産） | **`@playwright/test`** を継続する。[IADR-0033](./IADR-0033_frontend-spa-foundation.md) は覆さない |
| **AI エージェントのブラウザ操作**（UI 確認・探索・スクリーンショット） | **`playwright-cli` + Skills** |
| **Playwright MCP** | **導入しない**（計画側の判断をそのまま引く。両方入れるとツール選択が不定になる） |

**`e2e/` 配下のテストを CLI へ移設することは求めない。**

### 決定 2: `@playwright/cli` は `package.json` に加えない

理由は 3 つある。

1. **CI のどのジョブも起動しない。** ビルドにもテストにも要らず、**エージェントの作業道具**である。
   プラグイン（superpowers 等）を「ユーザー単位設定のためリポジトリでは配布できない」として
   各自導入にしたのと同じ性質である（`AI_SETUP.md` §4-2）。
2. **workspace に 2 つ目の Playwright が入る。** `pnpm-lock.yaml` に別系統のバージョンが載り、
   CI のキャッシュとブラウザダウンロードが二重化する。**CI が使わないものを CI の依存へ足すことになる。**
3. **キットの `npm i -D` は本リポでは成立しない。** 上記 `ERR_PNPM_RECURSIVE_EXEC_FIRST_FAIL` の罠がある。

**将来リポジトリ管理下へ移す場合は、`src/platform/frontend` へ入れ**（`@playwright/test` と同じ場所）、
**`pnpm --filter @platform/frontend exec` で起動する**こと。`src/` 直下から素で呼ばない。

### 決定 3: 読み分けは `AI_SETUP.md` §4-3 に置く。**`CLAUDE.md` には置かない**

**必読規約の予算に余地が無い。** 当初は `CLAUDE.md` §生成 AI の活用 へ 1 行（261 B）足したが、
**`scripts.repo.test.js` の余白下限ラチェット（1000 B。#730 / [IADR-0190](./IADR-0190_permanent-headroom-by-annexing-examples.md) 決定 2）に掛かった** ——
余白は 1,068 B → **807 B** になった。

**縮めても入らない。** 実測した候補は次のとおりで、**最短の 100 B 版でも 968 B にしかならない**。

| 案 | 行のサイズ | 余白 | 判定 |
| --- | ---: | ---: | --- |
| 当初 | 261 B | 807 B | NG |
| 短縮 A | 155 B | 913 B | NG |
| 短縮 B | 139 B | 929 B | NG |
| 最短 C | 100 B | 968 B | NG |
| **削除** | **0 B** | **1,068 B** | **OK** |

**したがって置かない。** ラチェットのメッセージが指示するとおり
「規範でない部分を別紙へ出してから加筆する」しかないが、**そのために無関係な必読条文を
削るのは本 PR の射程ではない**。

**規範は失われていない。** `CLAUDE.md` は冒頭で「**最初に `AI_SETUP.md` を読む**（利用可能な AI の
宣言・有効化・シークレットの正本）」、§生成 AI の活用 で「**AI の有効化・認証は `AI_SETUP.md` が
正本である**」と既に述べており、**ブラウザ操作ツールの選定はその射程に入る**。

- `AI_SETUP.md` §4-3 … 固有デルタ第 2 種の注記として（導入手順を読む面）
- 本 IADR … 判断の記録

**キット本文（§4-3 の地の文）は書き換えない。** 土台はキット側が正であり、
本リポの事情は**注記の追加**として分ける（`scripts/kit-sync-classification.json` の分類 B の考え方）。

## 影響

- 既存の `src/platform/frontend/{playwright.config.ts,e2e/}` と `frontend.yml` の `E2E smoke` は**無変更**
- `pnpm-lock.yaml` は**無変更**（決定 2）
- `AI_SETUP.md` の固有デルタが 1 件増える（分類表の該当行を更新した）

## 未解決

- **`playwright-cli install --skills` が配置するスキルの実挙動は未検証である。** 各自導入のため
  CI で固定できない。実運用で問題が出た場合は本 IADR へ追記する（本文は書き換えない）。
