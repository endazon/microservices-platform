---
title: 作業仕様書 — claude-code-action の pin の行末コメントの版を SHA（v1.0.267）に合わせる（#2476）
type: spec
status: done
related_ids: [NFR]
author: claude
created: 2026-11-07
updated: 2026-11-07
issue: "#2476"
plan_refs: []
---

# 作業仕様書 — claude-code-action の版コメントの是正（#2476）

> 本仕様書は実装着手前に作成した（着手 2026-11-07）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（メタ作業。NFR）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: なし
- 計画書リンク: なし

## 目的・背景

- #2450（Dependabot）が `anthropics/claude-code-action` の pin を `4845bacbb38b3c4b15d948f035349786f678e068` へ上げた（2026-11-06 マージ）。この SHA は公式タグ v1.0.267 の commit と一致する。
- Dependabot は SHA だけを書き換えたため、`uses:` 行の行末コメントは `# v1.0.264（2026-10-31 実測で緑。この版へ上げた PR の claude-review）` のまま残った。コメントの版が SHA と食い違う。

## 対象範囲

- 対象: `.github/workflows/claude-code-review.yml`・`.github/workflows/claude-coding.yml` の `uses: anthropics/claude-code-action@…` 行の行末コメント
- 対象外: SHA そのもの・`.github/dependabot.yml`・ワークフローの起動条件・必須チェック

## 設計

- コメントを `# v1.0.267（2026-11-06 に #2450 で上げた。develop へ入った後の #2474 の claude-review が、この SHA で緑）` にする。
- 根拠として書くのは確かめた事実だけである。
  - #2450 自身の claude-review は `skipped`（Dependabot の PR）であり、緑の実測にならない。
  - #2474（#2450 マージ後の develop を base とする PR）の claude-review（run 42239369554、job 127965286639）は `success` で、ジョブログに `Download action repository 'anthropics/claude-code-action@4845bacbb38b3c4b15d948f035349786f678e068'` がある。

## 母集合（規則 9: 誤りの側の文字列で全文を走査した）

`git grep -n "1\.0\.264"`（`v` 無しも含めて引いた）と、関連語 `git grep -n "claude-code-action"` で引いた。

| 当たり | 扱い | 理由 |
| --- | --- | --- |
| `.github/workflows/claude-code-review.yml:141` の行末コメント | **直す** | 本件 |
| `.github/workflows/claude-coding.yml:128` の行末コメント | **直す** | 本件 |
| `1.0.264` の他の当たり | — | 着手時点（本仕様書を書く前）は上の 2 件だけ |
| 本仕様書自身 | 外す（規則 8） | 是正前の字面を引用しているため、本仕様書のコミット後は走査に当たる。是正後の走査は「本仕様書だけが当たる」が期待値 |
| `scripts/check-ai-workflow-config.js` の試験データ（`# v1`・`# v1（実測で緑）`） | 外す | 架空の SHA を使う自己試験で、実リポの版に依存しない |
| `CHANGELOG.md` の Dependabot の版上げの行 | 外す | 生成物。#2450 の行は次の生成で入る |
| `.github/workflows/claude-code-review.yml:91` の `claude-code-action（cfc3eb22…）` | 外す（記録に留める） | `v1.0.264` の字面ではない。旧い pin の SHA を引いた挙動の説明で、現 SHA で挙動が同じかは確かめていない。確かめずに書き換えない |
| `docs/`・`AI_SETUP.md`・`scripts/README.md`・`scripts/action-versions.json` の claude-code-action の記述 | 外す | 版の数字を持たない |
| `.ai-context/` の過去の記録 | 外す | 凍結記録 |

規則 10（この変更で新たに誤りになる自分の記述）: 新しいコメントは #2450・#2474 の番号と日付だけを持ち、次の版上げで再び古くなる。これは版を上げる PR が直す前提であり、本件の範囲外である。

## 受け入れ基準

- [x] 2 ファイルの `uses:` 行のコメントが v1.0.267 を示し、根拠の括弧書きが確かめた事実だけを書く
- [x] SHA は変えない（2 ファイルで同じ SHA のまま）
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` と文書の検査器が通る

## テスト方針

- `node scripts/check-ai-workflow-config.js`（2 か所の SHA の一致）と `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` を通す。
- 検査器・試験は足さない。コメントの版と SHA の食い違いはこれが 1 回目の記録である（「検査器・規約の追加は同型の事故 2 回から」）。

## 計画書との差異

- 差異: なし

## 未決事項

- なし
