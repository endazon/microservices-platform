---
title: 作業仕様書 — CodeQL の PR 解析をコード変更のある PR に限定する（#719）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0182
  - IADR-0186
author: claude
created: 2026-08-14
updated: 2026-08-14
plan_refs: []
---

# 作業仕様書: CodeQL の PR 解析をコード変更のある PR に限定する

- 起点 issue: #719（起点 ID: NFR。kit 改定 planning#327 への追随）

## 目的

docs のみの PR でも `Analyze (csharp)` が毎回 5〜10 分回っている（利用者指摘 2026-08-14）。C# コードに効かない PR で CodeQL 解析をスキップし、PR の CI 時間と Actions 費用を削る。

## 変更

- `.github/workflows/codeql.yml` の `pull_request` トリガーへ paths を追加（cs / csproj / slnx / sln / props / targets / packages.lock.json / ワークフロー自身）
- `push`（develop/main）と週次 `schedule` は paths なしの全量解析のまま（セキュリティ網羅の担保はそちらが持つ）

## 判断の記録

- 決定はキット側（planning#327 の `codeql.example.yml`）にあり、本リポは追随である（運用ガイド §11「配布点は kit に一本化」）。リポ独自の判断が無いため新規 IADR は起こさない
- 【落とし穴】paths 付きチェックを required status check に指定すると、paths に合致しない PR でチェックが作られず恒久 pending になる（IADR-0182 が記録した「実在しないチェック名の指定で恒久 pending」と同型）。必須チェックを設定する際は本仕様書と `docs/ai-workflow.md` の必須チェック節を併読すること

## 受け入れ基準の充足

| # | 基準 | 確認 |
| --- | --- | --- |
| 1 | pull_request へ paths 追加 | 本 PR の diff |
| 2 | push / schedule は不変 | 本 PR の diff（トリガー節のみの変更） |
| 3 | required check 非指定の注意を記録 | 本仕様書「判断の記録」と codeql.yml 内コメント |

## 補記（2026-08-14・スコープ追加）: submodule `src/ai-stock-trading` の前進

利用者指示（2026-08-14「Security / Vulnerable transitive dependencies が解消されていないが、720 のプルリクで対応してください」）により、本 PR のスコープへ Security 赤の解消を追加した。

- **事象**: develop の SSH.NET ピン（`7a9e5e9`・IADR-0186）取り込み後も `Vulnerable transitive dependencies` が失敗（run 31797511121 で実測）。残る NU1903 の発生源は本リポではなく **submodule `src/ai-stock-trading` の `AiStockTrading.IntegrationTests`** だった
- **対処**: submodule pin を `91d52c2` → `e4df308`（AST develop 先端）へ前進（コミット `84508a8`・独立コミット。AST pin bump の既存慣行と同型）
- **根拠（実測）**: AST 側の対のピンは AST コミット `07bb9da`（AST#476 の一部）で導入済み — 同コミットの `Directory.Packages.props` diff が GHSA-q939-rpr3-3284 を名指しして `SSH.NET 2026.0.0` を追加している。`07bb9da` は前進範囲 `91d52c2..e4df308` に含まれる（`git merge-base --is-ancestor` で確認）。**コミット件名の grep ではヒットしない**（件名は借株料の機能実装であり、ピンは同 PR の diff に同乗している）
- **効果（実測）**: 前進前 `b97ccbf` の Security run 31797511121 = failure、前進後 `84508a8` の run = success。緑化は本前進に帰属する
- **IADR-0186 決定 3 との関係**: 「submodule 側は直さない・環流する」は **IADR-0186 起票時点で AST 側が未修正だった状況**での決定である。環流先の AST 側修正（#476）が着地したため、本 PR の pin 前進はその決定が予定していた「環流の回収」に当たり、矛盾しない
