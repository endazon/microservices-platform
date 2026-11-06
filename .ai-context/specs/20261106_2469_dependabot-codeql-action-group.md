---
title: 作業仕様書 — dependabot で codeql-action の init と analyze を同じ PR で上げる（#2469）
type: spec
status: done
related_ids: [NFR]
author: claude
created: 2026-11-06
updated: 2026-11-06
issue: "#2469"
---

# 作業仕様書 — dependabot の codeql-action group（#2469）

> 本仕様書は実装着手前に作成した（着手 2026-11-06）。dependabot の PR を監査した結果を受けた、コーディネーターの決定（案 A）による。

## 事象と原因（ログで確認した）

- Dependabot（github-actions）は、`github/codeql-action/analyze` と `github/codeql-action/init` を別の依存として扱い、別々の PR を作った（#2446 は analyze だけ、#2448 は init だけを v4.39.1 → v4.39.2 へ上げる）。
- CodeQL は、同じ workflow 内の codeql-action が同じ版であることを要求する。両 PR とも Analyze (csharp) と Analyze (javascript-typescript) が `Loaded a configuration file for version '4.39.1', but running version '4.39.2'. All steps in a workflow that use github/codeql-action must use the same version` で落ちる。
- どちらの PR も単独では緑にならない。`.github/dependabot.yml` の github-actions の項目に `groups` が無いため、次の版上げでも同じことが起きる。

## 対応

- `.github/dependabot.yml` の github-actions の項目に `groups.codeql-action.patterns: ["github/codeql-action*"]` を足す。
- 依存名は PR の件名とブランチ名から確かめた（`github/codeql-action/analyze`・`github/codeql-action/init`）。パターンは両方に一致する。
- 他の action はまとめない（範囲は本件の事象に限る）。

## 受け入れ基準

- [x] github-actions の項目に、`github/codeql-action*` に一致する group がある
- [x] dependabot.yml が YAML として正しく、Dependabot の設定の形（`version: 2`・各 `updates` 項目）を保つ
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が通る
- [x] `check-action-versions`（`--dir .github/workflows --compare-with-ref origin/develop`）が通る

## 母集合（規則 9: 誤りの側の文字列で全文を走査した）

`git grep -n -i -E "codeql-action|dependabot"`（submodule `src/ai-stock-trading`・`CHANGELOG.md` を除く）で引いた。

| 当たり | 扱い | 理由 |
| --- | --- | --- |
| `.github/dependabot.yml` github-actions の項目 | **直す** | 本件の原因 |
| `.github/workflows/codeql.yml` の init / analyze の pin（v4.39.1） | 外す | 版はこの PR では動かさない。group の PR が両方を上げる |
| `scripts/action-versions.json`・`scripts/check-action-versions.js`・`scripts/README.md` の codeql-action / Dependabot の記述 | 外す | メジャー版の下限と走査範囲の記述で、group の有無に依存しない |
| `docs/how-to/adding-a-unit-submodule.md` の Dependabot の記述 | 外す | gitsubmodule エコシステムの記述で、本件と無関係 |
| `docs/ai-workflow.md`・`docs/traceability-appendix.md`・`docs/how-to/commit-message-rules-annex.md` | 外す | Dependabot の存在・bot 著者の除外の記述で、group の有無に依存しない |
| `.ai-context/` の過去の記録 | 外す | 凍結記録 |

## 検査器・試験を足さない理由

- 同型の事故はこれが 1 回目である（「検査器・規約の追加は同型の事故 2 回から」）。記録はこの仕様書と #2469 に残す。

## マージ後の扱い

- #2446 / #2448 はコーディネーターが閉じる。Dependabot が作り直す group の PR は改めて監査する。
