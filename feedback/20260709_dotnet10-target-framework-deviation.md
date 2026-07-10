---
title: 実装は .NET 10 / C# 13 へ統一済み — 計画制約「ASP.NET Core + .NET 8」との乖離の解消判断を依頼
type: plan-feedback
status: open
category: 要求の誤り
related_ids: [NFR, ADR-0001, IADR-0048]
source_repo: microservices-platform
source_ref: "src/Directory.Build.props（net10.0 / LangVersion 13）/ 実装リポ issue #202 / docs/adr/IADR-0048_dotnet10-target-framework.md / 2026-07-09 全体レビュー"
author: claude
created: 2026-07-09
updated: 2026-07-10
---

> **実装側の意思決定記録**: 2026-07-10 に **IADR-0048**（.NET 10 / C# 13 採用・単一情報源
> `src/Directory.Build.props`・実態を正とし計画追随を推奨）を起票し、issue #202 の item 1 を完了。
> 本 feedback（計画側の制約更新 or 是正の判断依頼）は引き続き open。

# フィードバック: 実装は .NET 10 / C# 13 — 計画制約「ASP.NET Core + .NET 8」との乖離

## 種別

要求の誤り（確定済み制約条件が実装実態と不一致。制約の更新 or 実装の是正の判断が必要）

## 起点となる計画書

- 機能要求（FR）: —（制約条件・技術スタック）
- ユースケース（UC）: —
- 画面（SC）: —
- 関連 ADR: —（技術スタック選定は 06_technical/03）
- 計画書リンク: `projects/microservices-platform/02_requirements/01_requirements.md`（制約条件）、`projects/microservices-platform/06_technical/03_tech-stack-selection.md`、`projects/microservices-platform/INDEX.md`（概要）

## 現状（計画書の記述 / As-Is）

- `02_requirements`（fixed）制約条件: 「実装は ASP.NET Core + **.NET 8** に統一する」
- `03_tech-stack-selection.md`（fixed）: 「実装フレームワーク: ASP.NET Core + .NET 8」（比較評価も .NET 8 前提）
- `INDEX.md` 概要: 「実装: ASP.NET Core + .NET 8（AI/変換含む）」

## 問題点 / あるべき姿（To-Be）

実装リポジトリは全バックエンドを **`net10.0` / C# 13** に統一している（`src/Directory.Build.props` を単一情報源とし、`global.json` は SDK 8.0.0 + `rollForward: latestMajor`）。CI も .NET 10 SDK でビルド・テストしており、確定制約「.NET 8」と恒常的に乖離した状態である。

計画（fixed）と実装の乖離は「新 ADR または計画変更で根拠を残す」運用だが、本件はどちらの記録も無いまま推移していた（実装リポ側は issue #202 で IADR 起票を追跡開始）。

## 実装で判明した経緯

2026-07-09 の実装リポジトリ全体レビューで、`Directory.Build.props`（net10.0）と計画制約（.NET 8）の不一致、および当該乖離の IADR・フィードバック未記録を確認した。

## 提案（計画への反映案）

- 反映先候補: 要求更新（制約条件）＋ 技術検討（03_tech-stack-selection）＋ INDEX 更新
- 提案内容（いずれかを計画側で判断）:
  1. **制約を実態へ更新**する: 「ASP.NET Core + .NET 10（または『サポート中の最新 LTS/STS』のような版数非固定表現）」へ改訂し、変更履歴に根拠を残す。実装リポの IADR（#202 で起票予定）を相互参照する。
  2. **.NET 8 へ是正**を指示する: 互換性・サポート期限の評価の上でダウングレードを実装リポへ依頼する（現実的には非推奨。全サービス・CI・コンテナイメージへ波及）。
- 付随して、版数を固定で書く箇所（INDEX 概要等）を「単一情報源（実装リポ `Directory.Build.props`）参照」とする表現も検討に値する。

## 影響範囲

- 制約条件・技術スタック選定・INDEX の記述のみ（アーキテクチャ・サービス分割への影響なし）。
- 実装リポジトリ: issue #202（IADR 起票）、`docs/tech/tech-requirements.md` 整備（issue #200）。
