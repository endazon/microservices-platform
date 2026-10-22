---
title: IADR-0581 GitHub Actions は commit SHA で固定して同じ行に版の注記を置き、Dependabot が SHA と注記を対で更新する。Dependabot の対象に nuget と npm を足す
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0578]
author: claude
created: 2026-10-22
updated: 2026-10-22
related_specs:
  - ../specs/20261022_actions-sha-pin-and-dependabot.md
---

# IADR-0581: Actions の SHA 固定と Dependabot（GHSA-552h-47r7-6v6p）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-22
- 決定者: オーナー指示（2026-10-22）・claude（注記の形と検査器の読み方）

## 起点・関連

- 起点: GHSA-552h-47r7-6v6p の 2 本目（多層防御の強化。サプライチェーンの固定）。
- 前提: [IADR-0578](./IADR-0578_ci-workflow-trust-and-injection-hardening.md)（1 本目）。`anthropics/claude-code-action` の SHA 固定は #1352 で先行していた。

## コンテキストと課題

ワークフローの `uses:` はメジャーのタグで参照していた。タグは指す先が動き得るため、同じ定義でも実行する中身が変わり得る。依存の自動更新は github-actions と submodule だけで、nuget と npm は対象外だった。

## 検討した選択肢

1. **タグのまま、許可するアクションを組織の設定で絞る** — 個人リポジトリでは設定の粒度が足りず、中身の固定にならない。
2. **SHA で固定し、同じ行に版の注記を置く（採用）** — Dependabot が SHA と注記を対で更新でき、`check-action-versions.js` は注記から版を読める。
3. **SHA だけで固定する（注記なし）** — 版が読めず、退行の検査が効かなくなる。

## 決定

1. **外部の `uses:` は 40 桁の commit SHA ＋同じ行の `# vX.Y.Z`。** 新しく足すときも同じ形にする。`scripts.repo.test.js` が全ワークフローで固定する。
2. **`check-action-versions.js` は SHA 固定の行の注記を版として読む。** 注記が無い SHA は従来どおり比較対象外。
3. **Dependabot は github-actions / gitsubmodule / nuget（`/src`）/ npm（`/src`）を週次で更新する。** 自動マージはしない。

4. **`global.json` の `sdk.version` を完全な版 `8.0.100` にする（`rollForward: latestMajor` は維持）。** nuget を Dependabot の対象に足した push で、GitHub の自動の依存の提出（NuGet）が初めて起動し、`setup-dotnet` が「rollForward を指定するなら完全な版が要る」として `8.0.0` を拒んだため。`8.0.100` は 8.0 系の最初の正規の SDK で、`latestMajor` により選ばれる SDK（CI の setup-dotnet 10.0.x・手元の 10.x）は変わらない。

## 結果

- 良い影響: ワークフローの実行する中身が定義で一意に決まる。依存の更新とセキュリティ修正の取り込みが PR として届く。
- 悪い影響: 版を上げる PR が増える（各エコシステム 5 本まで）。手で版を上げるときは SHA と注記の両方を書き換える必要がある。

## 残余

1. 注記と SHA の食い違い（注記だけ書き換える等）は検査していない。Dependabot は対で更新する。
2. nuget の更新が `Directory.Packages.props` の版を基盤の単一情報源として動かすため、AST（submodule）側との版の整合は従来どおり人が見る。
