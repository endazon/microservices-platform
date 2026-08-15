---
title: IADR-0203 依存更新の担当分割（Renovate / Dependabot）と手元 git フックの射程
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0031
  - IADR-0115
  - IADR-0121
author: claude
created: 2026-08-15
updated: 2026-08-15
plan_refs: []
---

# IADR-0203: 依存更新の担当分割（Renovate / Dependabot）と手元 git フックの射程

- 状態: Accepted
- 日付: 2026-08-15
- 決定者: 実装エージェント（issue #768）

## 起点・関連

- 関連する計画書 ID: 計画 `ADR-0031`（SPA スタック）/ NFR（運用保守）。関連 IADR: [[IADR-0121]] 決定 1
  （第 5 段 = 運用系ツーリング）/ [[IADR-0115]]（キット同期の分類）
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
4. **husky は `src/package.json` の `prepare` から git ルートで実行する**（`cd .. && husky || true`。husky v9 は
   cwd に `.git` を要求するため）。`core.hooksPath` は相対 `.husky/_` となり、git が作業ツリーのトップ基準で
   解決するので worktree ごとに自然に切り替わる。`|| true` は CI の `--frozen-lockfile` install を
   husky の失敗で落とさない安全弁である。
5. **`.github/dependabot.yml` は編集しない。** キット配布物の**分類 A（バイト一致）**であり、棲み分けの
   注記を入れると `check-kit-sync.js` が落ちることを実測した。注記は `renovate.json` の `description` と
   本 ADR が持つ。キット側へ同じ注記を入れるかは**環流で判断する**。

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
