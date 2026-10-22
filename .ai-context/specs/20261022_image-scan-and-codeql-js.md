---
title: 作業仕様書 — 建てたサービスイメージを Trivy で走査し（当面は報告だけ）、CodeQL に JavaScript / TypeScript を足す
type: spec
status: done
related_ids: [NFR, IADR-0584, IADR-0581, IADR-0067, IADR-0232]
author: claude
created: 2026-10-22
updated: 2026-10-22
issue: "GHSA-552h-47r7-6v6p"
---

# 作業仕様書 — イメージの走査と CodeQL の JS/TS（GHSA-552h-47r7-6v6p の 3 本目）

> 本仕様書は実装着手前に作成した（2026-10-22）。1 本目は IADR-0578、2 本目は IADR-0581。

## 起点

- GHSA-552h-47r7-6v6p（多層防御の強化。成果物と JS/TS のコードの静的な走査を足す）。
- 計画: NFR（統制・セキュリティ）。

## 方式

1. `images.yml` の `build (<service>)` で、建てたイメージを Trivy（`aquasecurity/trivy-action`、SHA 固定）で走査する。対象は CRITICAL / HIGH・修正版あり・脆弱性のみ。**当面は報告だけ**（`exit-code: "0"` ＋ `continue-on-error: true`）で、結果は実行サマリに表で載せる。イメージ名は compose の解釈から引き、見つからなければ warning を出して走査しない。
   固定する SHA は、公開の事案（GHSA-69fq-xp46-6x23）の後の正規のリリースであることを、リリース（Immutable）・タグ・コミットの署名で確かめ、根拠を IADR-0584 に書く。
2. `codeql.yml` の行列に `javascript-typescript` を足す（`build-mode: none`）。dotnet を使う step は csharp の脚に限る。PR の起動条件（`paths`）に JS/TS の拡張子と `src/pnpm-lock.yaml` を足す。
3. `scripts.repo.test.js` で 1 と 2 の形を固定する（変異試験つき）。`action-versions.repo.json` に trivy-action の下限を足す。

## 起動条件・job 名・必須チェックへの影響

- `images.yml`: 起動条件・job 名は変えない。step を足すだけ。走査は落とさないので、必須の集約 `image-build` の結果は変わらない。
- `codeql.yml`: **PR の `paths` を広げる（起動条件の変更）**。JS/TS の変更で解析が走らないと足した言語が PR で働かないため。`codeql.yml` は必須チェックではない（`paths` 付きは必須にしない運用。docs/ai-workflow.md）。job 名 `Analyze (csharp)` は変わらず、`Analyze (javascript-typescript)` が増える。

## 受け入れ基準

| # | 基準 | 確かめ方 |
| --- | --- | --- |
| AC1 | build ジョブが建てた後に Trivy で走査し、落とさない | `scripts.repo.test.js`「images.yml は…」と変異試験（exit-code を 1・continue-on-error を外す → 検出） |
| AC2 | CodeQL が csharp と javascript-typescript を解析し、JS/TS はビルドしない | 同「codeql.yml は…」 |
| AC3 | 必須チェックの名前と結果の扱いが変わらない | `git diff`・この PR の CI（`image-build` が従来どおり） |
| AC4 | 走査の結果が実行サマリに出る | この PR の `build (<service>)` の実行サマリ |
