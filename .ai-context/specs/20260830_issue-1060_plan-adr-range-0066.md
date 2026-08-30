---
title: 作業仕様書 — 計画 ADR レンジを ADR-0001..0066 へ更新する（#1060・後続 6 件の前提）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0065
  - ADR-0066
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md (Accepted 2026-08-30)
  - planning:projects/microservices-platform/07_adr/ADR-0066_frontend-feature-isolation-and-import-direction.md (Accepted 2026-08-30)
related_specs: []
issue: "#1060"
---

# 作業仕様書 — 計画 ADR レンジを `ADR-0001..0066` へ

## 目的と射程

`.claude/rules/traceability.repo.md`「起点 ID の種別（固有）」節の計画 ADR レンジ宣言を
`ADR-0001..0064` → `ADR-0001..0066` へ更新する。**同節は `check-commit-messages.js` /
`check-trace-blocks.js` の一次情報**であり、更新しないまま `ADR-0065` / `ADR-0066` を参照すると
コミット件名・PR タイトル・trace ブロックの値域検査がすべて落ちる。

**射程はレンジ宣言 1 行と、その追随記録（別紙への 1 世代分の追記）に限る。**
`ADR-0065` / `ADR-0066` の中身を実装へ反映する作業は #1061〜#1066 が持つ。

## 計画側の実在確認（planning submodule は撤去済みのため隣接クローンを直接走査）

```console
$ cd /c/10_SourceCode/project-planning && git log --oneline -1
91fc07f adr(MSP): ADR-0065 で標準構成を単一プロジェクト＋ Vertical Slice へ改め、ADR-0066 で feature 間 import を禁じ、誤った適合判定を 5 箇所訂正する (#490) (#504)

$ ls projects/microservices-platform/07_adr/ | tail -3
ADR-0065_backend-service-single-project-vsa.md
ADR-0066_frontend-feature-isolation-and-import-direction.md
README.md
```

両 ADR とも frontmatter は `status: Accepted` / `created: 2026-08-30`。**番号は仮ではなく確定である**
（issue #1060 の「マージ前は番号が仮である可能性」という留保は解消している）。

## 母集合の引き方（`.claude/rules/traceability.repo.md` §是正・追随の母集合 規則 9）

**誤りの側の文字列（`0064`）で追跡下の全ファイルを走査した**（`src/ai-stock-trading` は submodule のため除外）。

```console
$ git grep -n "0064" -- . ':!src/ai-stock-trading' | wc -l
33
```

33 件の内訳と除外理由:

| 分類 | 件数 | 扱い |
| --- | ---: | --- |
| **`ADR-0001..0064` のレンジ宣言** | **1** | 🔴 **本作業の対象**（`.claude/rules/traceability.repo.md:7`） |
| `IADR-0064`（単独ビルド用フォールバック props）への参照 | 30 | **別物**。計画 ADR ではなく実装 ADR の 64 番。対象外 |
| `docs/how-to/plan-id-range-history-annex.md` の 3 回目の記録（`0001..0058` → `0001..0064`） | 2 | **過去の記録であり正しい**。書き換えない。**代わりに 4 回目を追記する**（規則 10） |

**`scripts/scripts.repo.test.js:1273` と `scripts/check-test-traceability.js:432` の
`ADR-0001..0039` は合成フィクスチャ**であり実ファイルを読んでいない。値域の検査対象ではないため対象外。

## 規則 10 —— この変更で新たに誤りになる自分の記述

- `docs/how-to/plan-id-range-history-annex.md` は**世代ごとの追随記録**を持つ。`0001..0064` で止まったままにすると、
  別紙が「最後の引き直しは 3 回目」と読める状態になる。**4 回目を追記する。**
- **世代数（「N 世代目」という総数）は書かない** —— `.claude/rules/traceability.repo.md` が
  「別紙が増えるたびに腐る導出値である」として禁じている。見出しの `［日付・N 回目］` は既存の書式であり維持する。

## 受け入れ基準（issue #1060）

- [ ] `.claude/rules/traceability.repo.md` §起点 ID の種別 のレンジ表記が `` `ADR-0001..0066` `` である（欠番なしの宣言も維持）
- [ ] trace ブロックに `ADR-0065` を書いた `docs/` 配下の文書が `check-trace-blocks.js` で値域違反にならない
- [ ] コミット件名 `docs(ADR-0066): …` が `check-commit-messages.js` の実在性検査を通る
- [ ] `node --test scripts/scripts.repo.test.js` が緑

## 検証（実測を貼ること）

```bash
node scripts/check-trace-blocks.js && node scripts/check-commit-messages.js && node --test scripts/scripts.repo.test.js
```

値域が実際に効いていることは**変異試験で確かめる**（宣言だけでは検査器が働いた証跡にならない）:
`ADR-0067`（実在しない）を trace ブロックへ一時的に入れて `check-trace-blocks.js` が落ちること、
`ADR-0065` では落ちないことを対で見る。

## 実測（2026-08-30）

```console
$ node scripts/check-trace-blocks.js
[check-trace-blocks] OK: 158 件の Markdown に trace ブロックの違反はありません。

$ REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js | tail -1
✓ 664 tests passed

$ node scripts/check-landed-subjects.js | tail -1
[check-landed-subjects] OK: 着地件名 573 件。baseline 外の規約違反はありません。
```

### 変異試験 —— 値域が実際に効いていることの証跡

trace ブロックの `adrs:` へ実在しない `ADR-0067` を入れると落ち、戻すと通る。

```console
$ # adrs: [..., ADR-0066, ADR-0067] にして実行
$ node scripts/check-trace-blocks.js
  docs/how-to/plan-id-range-history-annex.md
    - trace ブロック adrs: 計画 ADR レンジ（ADR-0001..0066）外です: ADR-0067
```

**エラー文の「ADR-0001..0066」が、更新後の宣言レンジを実際に読んでいることを示している。**

## 🔴 作業環境の罠 2 件（本作業の変更とは無関係。判定を誤らせるので記録する）

着手時、ローカルで検査が 2 件落ちた。**どちらも実装ではなく作業環境の問題**であり、CI では起きない。

### 罠 1 — `Services/*/Tests/` がディスク上は小文字 `tests/`

`core.ignorecase=true` のため、8 プロジェクト分割の撤去時に **git のインデックスだけ `Tests/` になり、
ディスクは `tests/` のまま**残っていた。**14 サービス中 12 件**（`McpServer` / `NotificationService` /
`GraphService` 以外）。検査器は `fs.readdirSync` の実際の大小文字を見るため
`FeedbackService/Tests/ の .cs が見つからない` で落ちる。ディスク側の大小文字を直した（`git status` は不変）。

**#1063（`Tests/` の鏡写し移送）の着手前に必ず確認すること。**

### 罠 2 — 追跡外の `planning/` が古いクローンとして残っていた

`scripts/check-commit-messages.js` の `DEFAULT_PLAN_PROJECTS_DIR` は
`<repo>/planning/projects` で、**無ければ宣言レンジへ fallback する**設計である
（planning submodule 撤去後の正しい経路）。ところが worktree に `planning/`（`aeb97c4`・ADR は
0032 までしか無い）が untracked で残っており、**検査器がそちらを読んでいた**。
結果 `check-landed-subjects.js` が `ADR-0033` 以降を「実在しない」と 19 件の偽陽性で報告した。
退避したところ 573 件すべて OK になった。
