---
title: 作業仕様書 — 必読規約（Claude 集合）を 90% 未満へ戻す：CLAUDE.md の TypeScript / React 節の詳細を別紙へ出す（#1770・第 4 回全体監査 B-4）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0172
  - IADR-0178
  - IADR-0190
author: claude
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:docs/ai-implementation-workflow-guide.md §8（必読規約は総量 50KB 予算）
related_specs: []
issue: "#1770"
---

# 作業仕様書 — 必読規約（Claude 集合）を 90% 未満へ戻す

## 目的と射程

第 4 回全体監査（2026-10-07）の指摘 B-4。`node scripts/check-reading-budget.js`（develop `c272d570`）の実測:

```
warn  Claude Code: 46,334 バイト（予算 51,200 の 90.5%）
        CLAUDE.md  23,775
        .claude/rules/traceability.md  15,955
        .claude/rules/traceability.repo.md  6,604
```

**90% 未満（46,080 B 未満）へ戻し、次の規約追加に使える余白を作る**（目安 85% 前後）。
減らし方は従来どおり「塊を別紙へ出し、入口の 1 行だけ残す」（IADR-0172 決定 3）。

**出す塊**: `CLAUDE.md`「技術スタック別ルール / TypeScript / React」節（約 6.1 KB。必読集合で最大の塊）の
**理由・設定の置き場所・手順**。IADR-0172 決定 3 の順序基準「機械が代替している度合いが高い順」に照らし、
この節の規範の大半は ESLint・`check-static-egress.js`・`check-i18n-catalogs.js`・CI の再生成差分検査・
`format:check`・カバレッジしきい値が**既に機械で止めている**。説明を出しても統制は残る。

**残すもの（判断時に要る規範）**: 採るスタック（React 19 / Vite 6 / Vitest 3 / Node 22 / TanStack Router /
TanStack Query / Tailwind v4 / `@platform/ui` / Lingui / Storybook / Vitest + Playwright）、禁止事項
（Redux・手書き HTTP クライアント・各サービス直叩き・外部 CDN 等・SPA のトークン保持・`oidc-client-ts` 再導入・
色だけの状態表示・除外グロブの複写）、`/verify` と test-author が「技術スタック別ルール」から引くコマンド
（`pnpm run lint` / `typecheck` / `format:check` / `test:coverage`）、進捗の正本（IADR-0121）への導線。

**別紙へ出すもの**: 各規範の理由（「片方だけだと静かに割れる」等）、設定ファイルの所在（`src/testing/setup.ts`・
`playwright.config.ts` の `use.locale`・`public/config.js`・`docs/authz/bff-session-design.md` 等）、
生成・検査の手順（`pnpm run codegen` / `pnpm run i18n` / Storybook のビルド）、ESLint が止める import の具体列挙。

**別紙の置き場所**: `docs/how-to/frontend-conventions-annex.md`（新規）。`docs/README.md` の how-to 行の
「必読規約から出した別紙」の列挙へ加える。`docs/` 規約に従い、計画 ADR・IADR・修飾付き issue 参照は
trace ブロックへ置き、表示テキストには書かない。

**射程外**:
- `.claude/rules/traceability.md`（キット配布物）は編集しない（受け入れ基準 3）。
- `.claude/rules/traceability.repo.md` は触らない（回帰テストが固定する文字列が多く、今回の目標は CLAUDE.md だけで達する）。
- C# / .NET 節・CI 節は触らない（`check-cpm-versions.js` が「パッケージ」項を引用し、テストが IADR-0169 を固定する）。
- 判断を伴う新しい決定は無い（既存の減量方式 IADR-0172 決定 3 / IADR-0190 の適用）ため IADR は起こさない。

## 母集合の引き方（規則 9・10）

**軸 1（CLAUDE.md の当該節を引用・参照している箇所）**:
`git grep -n "CLAUDE\.md" -- . ':!src/ai-stock-trading' ':!.ai-context/specs' ':!.ai-context/superpowers' ':!CHANGELOG.md' | grep -iE "front|フロント|React|Tailwind|Lingui|i18n|BFF|orval|prettier|format|Storybook|coverage|カバレッジ|TanStack|Redux|egress|認証|oidc|技術スタック別"`

| ヒット | 扱い |
| --- | --- |
| `.claude/commands/verify.md:13`・`.claude/agents/test-author.md:16`・`AGENTS.md:71`・`.github/copilot-instructions.md:36`・`README.md:177`（「技術スタック別ルール」からコマンド・配置を引く） | 影響なし。節見出しとコマンド（lint / typecheck / format:check / test:coverage・テストの配置）は残す |
| `scripts/check-cpm-versions.js:8,452`（「C# / .NET」の「パッケージ」項） | 影響なし。C# 節は触らない |
| `.ai-context/adr/IADR-0131:31`（「CLAUDE.md と IADR-0121 決定 3 は SPA から BFF への到達経路を 2 つに限る」） | 影響なし。2 経路（orval 生成フック・`apiFetch` / `apiStream`）は残す。凍結記録でもある |
| `.ai-context/adr/IADR-0214:99`（「両ユニットの CI 独立（CLAUDE.md §CI）」） | 影響なし。CI 節は触らない |
| その他の `.ai-context/adr/` の「CLAUDE.md 禁止事項」「同型の事故が 2 回」等 | 影響なし。当該節の外 |

**軸 2（当該節に固有の言い回しを他所が引いていないか）**: `静かに割れる` / `切替 UI は持たない` /
`最も速く腐る` / `手書き HTTP クライアントは禁止` / `回帰防止のラチェット` / `両スタックの CI を独立` を
`git grep -l` で走査。CLAUDE.md 以外のヒット（`src/platform/frontend/playwright.config.ts`・
`src/platform/frontend/src/utils/formatDateTime.ts`・`useNotifications.ts`・`src/vitest.config.ts`・
`docs/tech/tech-requirements.md`・`.ai-context/adr/*`）は**いずれも IADR を典拠に引いており CLAUDE.md を
引いていない**。追随不要。

**軸 3（回帰テストが CLAUDE.md の文字列を固定していないか）**: `grep -n "CLAUDE" scripts/scripts.repo.test.js`。
当該節に関わる固定は `#697: SPA 移行の進捗が CLAUDE.md から消え、規範が残っている`（`React 19` / `Vite 6` /
`TanStack Router` / `IADR-0121` の存在、`第 2 段の項目まで消化済み` の不在）のみ。**すべて残す**。
`#695 段 5`（切り出しの残骸検査）の対象一覧へ新しい別紙を加える（同じ方式の別紙は同じ扱いにする）。

**規則 10（この変更で新たに誤りになる自分の記述）**: 当該節の「フォーマット」項は「除外グロブを
`package.json`・ワークフロー・**本ファイル**へ複写しない」と書く。別紙へ移すと「本ファイル」が別紙を指すことになるため、
別紙側では「必読規約・本別紙」と書き直す。また `docs/README.md` の how-to 行に別紙の列挙があり、追加しないと
別紙が辿れない（受け入れ基準 2）。

## 受け入れ基準

1. `node scripts/check-reading-budget.js` の Claude 集合が 90% 未満（46,080 B 未満）。目安 85% 前後。
2. 別紙は `docs/README.md` と CLAUDE.md の入口 1 行から辿れ、`node scripts/gen-knowledge-graph.js --check` が通る。
3. `.claude/rules/traceability.md` を変更しない（`git diff --stat origin/develop` に現れない）。
4. `check-trace-blocks` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-doc-updated --base origin/develop` /
   `check-commit-messages` / `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が通る。

## 結果

（実測は PR 本文に記載する）
