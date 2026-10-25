---
title: 作業仕様書 — 計画 ADR レンジを ADR-0001..0144 へ引き直し、アクセシビリティの意味で引いた NFR-12 を NFR-30 へ直す（#2186）
type: spec
status: done
related_ids:
  - NFR-30
  - NFR-12
  - ADR-0142
  - ADR-0143
  - ADR-0144
  - ADR-0031
author: claude
created: 2026-10-24
updated: 2026-10-24
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0142_private-note-soft-delete-withdraws-exposure.md (Accepted)
  - planning:projects/microservices-platform/07_adr/ADR-0143_ai-input-user-default-is-initial-value.md (Accepted)
  - planning:projects/microservices-platform/07_adr/ADR-0144_break-glass-consented-admin-view.md (Accepted)
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR-12 パスワードポリシー / NFR-30 WCAG 2.2 AA)
related_specs:
  - 20261016_1961_archive-effect-exclusion
  - 20261024_2015_axe-all-screens
issue: "#2186"
---

# 作業仕様書 — 計画 ADR レンジ `ADR-0001..0144` と、アクセシビリティの `NFR-12` → `NFR-30`

## 目的と射程

1. `.claude/rules/traceability.repo.md`「起点 ID の種別（固有）」節の計画 ADR レンジ宣言を `ADR-0001..0141` から引き直す。
2. 計画の `NFR-12` はパスワードポリシーである。アクセシビリティの起点 ID は `NFR-30`（WCAG 2.2 レベル AA）である（planning#814）。
   live な文書とコードで、アクセシビリティの意味で `NFR-12` を引く記述を `NFR-30` へ直す。

**射程外**: ADR-0142〜0144 が求める実装。判断を伴う決定は無いため IADR は起こさない（凍結記録の除外は既存の [[IADR-0331]]・[[IADR-0391]] の方針「凍結記録を残し、それを引く live 側を直す」に従う）。

## 🔴 issue の数えとの差（規則 10: 他人の数えを転記しない）

issue は「`0001..0143`（計画 `a47c050` 時点）」と書く。**着手時に引き直すと、計画 `origin/main` は `aa27f28`（2026-10-24）で `0001..0144` である。**
`a47c050` の後に ADR-0144（緊急アクセス・planning#841）がマージされた。宣言は実測の `0001..0144` とする。

## 計画側の実在確認（読み取り専用）

計画リポジトリの作業ツリーは触らず、`git archive origin/main tools projects docs` を scratchpad へ展開して導出器を走らせた。

```console
$ git -C /home/user/project-planning fetch origin main
$ git -C /home/user/project-planning log -1 --format='%h %cs' origin/main
aa27f28 2026-10-24
$ node tools/doc-checks/gen-plan-ranges.js --check   # 展開先で実行
## microservices-platform
| FR | [1, 22] | **[1, 22]** | 22 | なし | ✅ |
| UC | [1, 11] | **[1, 11]** | 11 | なし | ✅ |
| SC | [1, 22] | **[1, 22]** | 22 | なし | ✅ |
| ADR | [1, 144] | **[1, 144]** | 144 | なし | ✅ |
**参考**: NFR は実物 [1, 30]・30 件。
✅ 宣言と実物は一致している
$ git diff fc61ca3 origin/main -- projects/microservices-platform/07_adr | grep -E '^[-+]status:'
+status: Accepted      # 0142〜0144 の 3 件（追加ファイル）。既存 ADR の状態は不動
```

- 前回の出典は `fc61ca3`（2026-10-16 の回）。0142・0143 は `8fda703`（planning#834〜planning#837）、0144 は `f16cb1d`（planning#841）で追加された。
- `NFR` は前回（2026-10-24 の `NFR-01..30` の回）から不動。FR / UC / SC も不動。

## 母集合の引き方（規則 9・10）

### A. レンジ

**軸 1（旧上限 `0141` のレンジ表記）**: `git grep -n -o -E "\.\.0?141\b|\[1, ?141\]|141 ?件" -- . ':!src/ai-stock-trading'`

| ヒット | 扱い |
| --- | --- |
| `.claude/rules/traceability.repo.md:7`（宣言） | **対象** |
| `docs/how-to/plan-id-range-history-annex.md:32・41・52` | 除外。過去の回の記録（日付つき） |
| `.ai-context/specs/20261016_1960…`・`20261016_1961…`・`20261024_2015…`・`20261024_2184…` | 除外。確定済みの作業仕様書（凍結） |
| `scripts/fixtures/review-floor/pr2183-review3.txt` | 除外。過去の AI レビューの写し（試験の入力） |

**軸 2（`ADR-0001..` 形）**: 宣言 1 件のほかは `scripts/backlog-audit.js`・`scripts/check-planning-adr-range.js` の単体試験の固定入力と `IADR-0432` の時点つき記録だけである。書き換えない。

**軸 3（`ADR-0142`〜`ADR-0144` の既存引用）**: `IADR-0610`・仕様書 `20261023_2046…`・`BreakGlassViewGate.cs`・`BreakGlassViewAuthorizationTests.cs` が既に `ADR-0142` を引く（宣言の遅れの窓で書かれた）。本変更で値域内に入る。書き換えない。

### B. `NFR-12`（誤りの側の文字列で走査）

`grep -rnE "NFR[ ‐－-]?12|NFR12" --exclude-dir=node_modules .`（`.ai-context/specs/`・`.ai-context/superpowers/` を除く）。表記の揺れ（全角ハイフン・空白・ハイフン無し）は 0 件。
パスワードポリシーの意味で `NFR-12` を引く箇所は 0 件だった（全ヒットがアクセシビリティの意味）。

| ヒット | 意味 | 扱い |
| --- | --- | --- |
| `src/eslint.config.js:650` | jsx-a11y | **対象**。`NFR-30` へ |
| `src/packages/ui/src/styles.css:176` | 文字のコントラスト（WCAG AA） | **対象**。`NFR-30` へ |
| `src/platform/frontend/src/app/Layout.test.tsx:345` | `main` ランドマーク | **対象**。`NFR-30` へ |
| `.ai-context/adr/README.md:520・522`（IADR-0440・0442 の索引の行） | a11y | **対象（注記）**。索引は live だが、凍結本文の要約である。番号を差し替えると本文と食い違うため、`NFR-12` を残して「誤参照。正は `NFR-30`」を併記する（Superseded の引用と同じ「旧を残し正を併記」の形） |
| `CHANGELOG.md:507`（`6fc45859` の件名のスコープ） | a11y | **対象（生成物の側で是正）**。恒久履歴は書き換えず、`scripts/changelog-overrides.json` の `remap` で scope の `NFR-12` を `NFR-30` へ差し替える。`CHANGELOG.md` は手で直さない。次の自動再生成（`changelog.yml`）で反映される。手元で `node scripts/gen-changelog.js` を走らせ、当該行のスコープが `NFR,NFR-30,…` になることを確かめた（全体の再生成は本件と無関係な差分が 244 行出るため PR に含めない） |
| `.ai-context/adr/IADR-0440`・`0442`（本文と frontmatter）、`IADR-0443:82`（本文）、`IADR-0435`・`0436`・`0532`（frontmatter の `related_ids`）、`IADR-0614:69`（残余の記録） | a11y | **除外**。確定済み IADR は凍結記録である。frontmatter の `related_ids` は本文の引用と対であり、片方だけ直すと同じ記録の中で食い違う。IADR-0614 自身が「凍結記録は直さない」と書いている |
| `src/platform/frontend/e2e/keyboard-navigation.smoke.spec.ts:10` | 「誤った NFR-12 から直した」の経緯 | 除外。既に `NFR-30` で、`NFR-12` は経緯の引用 |
| `.ai-context/specs/`・`.ai-context/superpowers/` | — | 除外。point-in-time の凍結記録 |

**規則 10（本変更で新たに誤りになる自分の記述）**: 別紙の本回の項は可視テキストへ計画 ID を書かない（ADR は番号だけ）。trace ブロックの `adrs` へ ADR-0142〜ADR-0144、`specs` へ本仕様書、`issues` へ #2186・planning#834〜planning#837・planning#841 を足す。宣言は数字の置換だけで、必読規約の総量は増えない。
直前の回（`NFR-01..30`）の項の「`ADR` は本回では引き直していない…2 件遅れている」は当時の記録として残し、本回の項で解消を書く。

## 受け入れ基準

- [x] 宣言が `` `ADR-0001..0144` `` で、欠番なしの書式を保つ
- [x] `PR_TITLE="chore(NFR-30,ADR-0144): 試験" node scripts/check-commit-messages.js` が適合し、陰性対照 `ADR-0145` は違反
- [x] `check-trace-blocks.js` が新しいレンジで通る（別紙の trace ブロックが ADR-0142〜0144 を引く）
- [x] `check-planning-adr-range.js --with-nfr` が宣言の上限を 144 と読み、計画側の `kg-ranges.json` と 5 種（FR / UC / SC / ADR / NFR）で突合して `ok` になる（手元はセッションの GitHub プロキシ経由で取得。トークンなしの素の実行は `unverified`）
- [x] アクセシビリティの文脈で `NFR-12` を引く記述が live な文書とコードに残らない（上表の除外を除く）
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`check-reading-budget.js`・frontend の lint / format:check が緑

## 必須チェックへの影響

レンジを広げる変更であり、値域検査が新たに赤くなる入力は無い。ワークフロー・ジョブ名は触らない。
