---
title: IADR-0203 依存更新の担当分割（Renovate / Dependabot）と手元 git フックの射程
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0031
  - IADR-0060
  - IADR-0115
  - IADR-0121
  - IADR-0141
author: claude
created: 2026-08-15
updated: 2026-08-21
plan_refs: []
---

# IADR-0203: 依存更新の担当分割（Renovate / Dependabot）と手元 git フックの射程

- 状態: Accepted
- 日付: 2026-08-15
- 決定者: 実装エージェント（issue #768）

## 起点・関連

- 関連する計画書 ID: 計画 `ADR-0031`（SPA スタック）/ NFR（運用保守）。関連 IADR: [IADR-0121](./IADR-0121_spa-stack-migration-staging.md) 決定 1
  （第 5 段 = 運用系ツーリング）/ [IADR-0115](./IADR-0115_impl-handoff-kit-as-single-source.md)（キット同期の分類）
- 関連する実装仕様書: [`docs/specs/20260815_issue-768_renovate-husky.md`](../specs/20260815_issue-768_renovate-husky.md)
- 関連 issue: #768（本決定）/ #493（親）/ #562（フロントの format ゲート）/ #260（Dependabot gitsubmodule）

## コンテキストと課題

第 5 段の Renovate と Husky を先行導入するにあたり 3 点を決める。**依存更新ツールが 2 つになる**（同じ
エコシステムを両方が見れば PR が二重に出る）。**手元フックをどこまで強制するか**（強制しすぎると非対話で
作業する AI の手が止まる）。**Husky をどこへ入れるか**（git ルートに `package.json` が無く、pnpm workspace
ルートは `src/` である）。

## 検討した選択肢

| 論点 | 案 A | 案 B（採用） |
| --- | --- | --- |
| 担当分割 | すべて Renovate へ寄せ Dependabot を廃止 | **npm = Renovate / `github-actions`・`gitsubmodule` = Dependabot** |
| フックの射程 | 計画の一覧どおり Commitlint と lint-staged も入れる | **CI にあるゲートのうち速い 2 つを、CI と同じ実体で前倒しする** |
| 設置場所 | git ルートに最小の `package.json` を新設する | **`src/package.json` の `prepare` から `cd .. && husky`** |

## 決定

1. **npm（`src/` の pnpm workspace）は Renovate が担当する**（直下 `renovate.json` / `enabledManagers: ["npm"]`）。
   **`github-actions` と `gitsubmodule` は Dependabot に残す** —— `gitsubmodule` は #260 の 3 リポ横断オーナー
   承認済み決定（「Dependabot、Renovate ではない」）で、private な `planning` を `registries` ＋
   `PLANNING_REPO_TOKEN` で引く構成が動いている。**これを覆さない。**
2. **手元の git フックは CI ゲートの厳密な部分集合に留める。** `pre-commit` = ステージ済み `src/**` の
   `prettier --check`（CI: `frontend.yml` / Format check）、`commit-msg` = `scripts/check-commit-messages.js`
   （CI: `ci.yml` / commit-messages）の 2 つだけ。**CI に無いゲートを手元に作らない**（#562）。`typecheck` /
   `lint` / `test:coverage` は入れない（横断で遅く CI が全量を持つ）。依存が未インストール・`pnpm` 不在なら
   **素通りさせる**（fail-open。最後の砦は CI である）。
3. **Commitlint と lint-staged は入れない。** Commitlint は規約の第 2 の情報源になり（正本は
   `.claude/rules/traceability.md` と `check-commit-messages.js`）、lint-staged はグロブで対象を決めるため
   **整形範囲の単一情報源（`src/.prettierignore`。#562）を 2 本に割る**。フックは同じ実体を呼ぶ形にする。
   **例外（`src/` の外に置く専用入口）を許す 3 条件は、下の 2026-08-16 追記が定める。**
4. **husky は `src/package.json` の `prepare` から git ルートで実行する**（`cd .. && husky || true`。husky v9 は
   cwd に `.git` を要求するため）。`core.hooksPath` は相対 `.husky/_` となり、git が作業ツリーのトップ基準で
   解決するので worktree ごとに自然に切り替わる。`|| true` は CI の `--frozen-lockfile` install を
   husky の失敗で落とさない安全弁である。
5. **`.github/dependabot.yml` は編集しない。** キット配布物の**分類 A（バイト一致）**であり、棲み分けの
   注記を入れると `check-kit-sync.js` が落ちることを実測した。注記は `renovate.json` の `description` と
   本 ADR が持つ。キット側へ同じ注記を入れるかは**環流で判断する**。

> **［2026-08-16 追記 / #802］却下されたのは「グロブを書くこと」ではなく「`src/` 内の対象選択を
> 2 本目のグロブで行うこと」である。** #777 が `src/package.json` へ `format:templates` /
> `lint:templates`（雛形 `templates/*/frontend` 専用の入口）を入れた。**これは本決定 3 の例外として
> 許す。** 却下された lint-staged との線引きを、**この 1 箇所**に置く（却下理由と例外が離れると、
> 片方だけを読んで「lint-staged 相当を足してよい」と読める。[IADR-0141](./IADR-0141_audit-rounds-and-population-drawing.md)）。
>
> **2 本目の入口を許すのは、次の 3 条件をすべて満たすときだけである。**
>
> 1. **射程外であること。** 対象が `src/` の外にあり、`src/.prettierignore` /
>    `src/.prettierrc.json` / `src/eslint.config.js` の**探索射程に構造的に入らない**こと。
>    Prettier は各ファイルの位置から上へ設定を探し、ESLint flat config は設定ファイルのある
>    ディレクトリの外を検査しない。**設定を移せば済む問題ではない**ことが条件である。
> 2. **規則の情報源を増やさないこと。** 設定は同じ `src/.prettierrc.json` を `--config` で明示し、
>    ESLint の禁止リストは `eslint.config.js` から import する（`src/eslint.templates.config.js`）。
>    **入口は増えるが規則は 1 つ**であることが条件である。
> 3. **入口ごとに対象が重ならないこと。** 同じファイルを 2 つの入口が別ルールで整形しない。
>
> **lint-staged が却下されたのは条件 1 を満たさないためである** —— 対象が `src/` 内であり、そこは
> `.prettierignore` が既に単一情報源として働いている領域だからである。したがって
> **`src/` 内の対象をグロブで選ぶ入口は、今後も入れない。**
>
> 例外を置かないと何が起きるかは実測されている: 設定が見つからない雛形は **Prettier の既定
> （ダブルクォート）で整形され、`--check` は自己矛盾しないので通ってしまう** ——
> **「検査しているのに何も守っていない」最悪の形**である。同じ理由で雛形は pnpm workspace の
> メンバでもある（[IADR-0121](./IADR-0121_spa-stack-migration-staging.md) 決定 2 の 2026-08-16 追記 / [IADR-0060](./IADR-0060_submodule-unit-operations.md) / #784）。
>
> **手元フック（決定 2）の射程は `src/` 配下のままとする。** 雛形は CI の
> `Format check (unit template)` / `Lint (unit template)` が見る。決定 2 の「CI ゲートの厳密な
> 部分集合」は保たれており、本追記は決定 2・3 の他の内容を変えない（状態は `Accepted` のまま）。

## 理由

- 決定 1: 無主地（npm）だけを新ツールに任せ、**既に動いていて横断決定のあるものは動かさない**のが最小の変更。
  重複は設定 1 行で機械的に排除でき、運用の申し合わせに頼らない。
- 決定 2・3: 手元フックは**速く・CI と食い違わない**ことに価値がある。CI に無い規則を足すと、手元では通るのに
  CI で落ちる（逆も）が生まれフックが外される。規約の情報源を増やすのも同じ理由で避ける。
- 決定 4: git ルートへ `package.json` を新設すると workspace ルートが 2 つに見え、`packageManager` の単一
  情報源（CI が `package_json_file: src/package.json` で参照）を揺らす。

## 結果

- 良い影響: npm 依存の更新が自動化され、整形とコミット規約の違反がコミット時点で分かる。
- トレードオフ: フックは `src/node_modules` が無ければ黙って素通りする。Renovate は **App が有効化されるまで
  何も起こさない**（本セッションでは PR 生成を確認できていない）。
- フォローアップ: Renovate App の有効化（利用者作業）。lint-staged / Commitlint / Knip / Plop は親 #493。
  Supersedes / Superseded by いずれも なし。

> **［2026-08-21 追記 / ADR-0048］決定 1 の `gitsubmodule` 部分と決定 5 は、計画 ADR-0048（決定 2:
> 本リポジトリは planning に依存しない）により上書きされた。** `planning` submodule を撤去し
> （`git rm --cached planning` ＋ `.gitmodules` の該当節を削除）、`.github/dependabot.yml` から
> `registries: planning-git`（`PLANNING_REPO_TOKEN` 使用）と `gitsubmodule` 更新エントリの
> `planning-git` 参照を削除した。**「これを覆さない」（決定 1）は覆った**——計画側の決定が実装側の
> 決定に優先するためである（`src/ai-stock-trading` の `gitsubmodule` エントリは維持し、対象はこの 1 件のみ）。
> 決定 5 の「編集しない」根拠（`check-kit-sync.js` の分類A バイト一致検査）も、同じ ADR-0048 決定 6
> （kit-sync 検査の廃止）により前提が失われている。決定 2〜4（手元フックの射程・Husky 配置）は不変。
