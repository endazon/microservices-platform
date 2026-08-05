---
title: IADR-0048 バックエンドは .NET 10 / C# 13 を採用する（計画制約「.NET 8」からの乖離）
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0001
author: claude
created: 2026-07-10
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (制約条件: .NET 8)"
  - "../../planning/projects/microservices-platform/06_technical/03_tech-stack-selection.md (実装フレームワーク)"
  - "../../planning/projects/microservices-platform/INDEX.md (概要)"
---

# IADR-0048: バックエンドは .NET 10 / C# 13 を採用する（計画制約「.NET 8」からの乖離）

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: NFR（制約条件・技術スタック）／ADR-0001（技術選定の系譜）
- 関連仕様書: `docs/tech/tech-requirements.md`（#200）、`feedback/20260709_dotnet10-target-framework-deviation.md`
- Issue: #202（本 IADR 起票）／#200（技術要件書への反映）

## コンテキストと課題

計画リポの確定（fixed）文書は実装フレームワークを **ASP.NET Core + .NET 8** と定める。

- `02_requirements/01_requirements.md`（fixed）制約条件: 「実装は ASP.NET Core + .NET 8 に統一する」
- `06_technical/03_tech-stack-selection.md`（fixed）: 「実装フレームワーク: ASP.NET Core + .NET 8」
- `INDEX.md` 概要: 「実装: ASP.NET Core + .NET 8」

一方、実装は全バックエンドを **`net10.0` / `LangVersion 13`** に統一している（[`src/Directory.Build.props`](../../src/Directory.Build.props) を
単一情報源とし、`global.json` は SDK `8.0.0` + `rollForward: latestMajor`）。CI も .NET 10 SDK でビルド・テスト
している。CLAUDE.md・`docs/DEFINITION_OF_DONE.md` は「計画（fixed/Accepted）に反する実装は新 ADR または
`/plan-feedback` で根拠を残す」ことを要求するが、本乖離を記録した IADR が存在しなかった（`docs/adr/` に
.NET 10 への言及なし）。plan-feedback（`feedback/20260709_dotnet10-target-framework-deviation.md`）は
2026-07-09 の全体レビューで起票済みで、本 IADR はその実装側の意思決定記録を補完する。

## 決定

1. **バックエンドの実装ターゲットは `.NET 10` / `C# 13`（`LangVersion 13`）とする。**
   単一情報源は [`src/Directory.Build.props`](../../src/Directory.Build.props)。個別 `.csproj` で `TargetFramework` を
   上書きしない（CLAUDE.md 技術スタック別ルールと一致）。`global.json` は SDK `8.0.0` + `rollForward: latestMajor`
   とし、より新しい SDK でもビルド可能とする。

2. **計画制約「.NET 8」との乖離は本 IADR ＋ plan-feedback で根拠を残し、最終的な制約更新は計画側の判断に委ねる。**
   plan-feedback は「制約を .NET 10（または『サポート中の最新 LTS/STS』のような版数非固定表現）へ更新する」か
   「.NET 8 へ是正する」かを計画側に依頼している。本 IADR は前者（実態＝.NET 10 を正とし計画を追随更新する）を
   推奨する立場で記録する。是正（ダウングレード）が計画側で決定された場合は本 IADR を `Superseded` とし、
   実装を .NET 8 へ戻す作業 issue を起票する。

## 根拠 / 代替案

- **実態を正とする（.NET 10 維持）を推奨**: 全サービス・CI・コンテナイメージが既に .NET 10 で恒常運用されており、
  ダウングレードは全面波及（各 `Directory.Build.props`/`global.json`/CI/イメージ/依存パッケージのターゲット）で
  コスト・回帰リスクが高い。計画制約の版数固定は実装選択の自由度を過度に縛る性質のもので、アーキテクチャ
  （サービス分割・境界・契約）には影響しない。
- **版数固定 vs 版数非固定表現**: 計画側の制約を「サポート中の最新 LTS/STS」のような非固定表現へ改めれば、
  今後の版数更新のたびに計画・実装が乖離する事象を構造的に防げる。併せて版数を明記する箇所（INDEX 概要等）は
  「単一情報源（実装リポ `Directory.Build.props`）参照」とする表現が望ましい（plan-feedback で提案済み）。
- **C# 13 依存**: `LangVersion 13` を有効化しているが、言語機能への強い依存で .NET 8 に戻せない、という制約は
  設けない。決定の主因はダウングレードの波及コストとサポート方針であり、言語機能は副次的理由に留める。

## 影響

- ドキュメント: 本 IADR の追加。`docs/tech/tech-requirements.md`（#200）に確定結果（.NET 10 / C# 13・単一情報源）
  を記載する（#200 で対応）。
- コード: 変更なし（実態を追認する記録）。`src/Directory.Build.props` / `global.json` は現状維持。
- 計画: `feedback/20260709_dotnet10-target-framework-deviation.md` の判断依頼が未クローズ。計画側の制約更新 or
  是正指示を待つ。

  > **［2026-08-05 追記 / #497］この「未クローズ・待ち」は解消済みである（乖離そのものが無くなった）。**
  > 計画側は本 IADR が推奨した前者（実態＝.NET 10 を正とし計画を追随更新する）を採った。planning submodule
  > pin `d980a01` で実測した現在の記述は次のとおりで、上の「コンテキストと課題」が引く 3 箇所はいずれも
  > **`.NET 8` ではなく `.NET 10`** である。
  >
  > - [02_requirements/01_requirements.md](../../planning/projects/microservices-platform/02_requirements/01_requirements.md) 制約条件: 「実装は ASP.NET Core + **.NET 10** に統一する」（変更履歴 2026-07-24 で `.NET 8` から更新）
  > - [06_technical/03_tech-stack-selection.md](../../planning/projects/microservices-platform/06_technical/03_tech-stack-selection.md) 確定スタック一覧: 「ASP.NET Core + **.NET 10**（C#、LTS）」（変更履歴 2026-07-23）
  > - [INDEX.md](../../planning/projects/microservices-platform/INDEX.md) 概要: 「実装: ASP.NET Core + **.NET 10**」
  > - 根拠 ADR: [ADR-0020](../../planning/projects/microservices-platform/07_adr/ADR-0020_dotnet-10-upgrade.md) = `status: Accepted`（2026-07-23）
  >
  > 計画側 draft の環流記録も 2026-08-04 に `accepted` でトリアージ済みで、本リポジトリの控え
  > [feedback/20260709_dotnet10-target-framework-deviation.md](../../feedback/20260709_dotnet10-target-framework-deviation.md) も #497 で `accepted` へ揃えた。
  > よって本 IADR は `Accepted` を維持する（「.NET 8 へ是正」の分岐は発生しない）。**行番号は pin が動くとずれるため内容で特定する。**

## フォローアップ

- ~~計画側の判断（制約更新 or 是正）の反映と、本 IADR の状態同期（`Accepted` 維持 or `Superseded`）。~~
  → **完了（2026-08-05 / #497）**。計画側は制約更新（.NET 10）を採り、本 IADR は `Accepted` を維持する（上記［追記］）。
- `docs/tech/tech-requirements.md`（#200）への確定結果の記載。
- INDEX 概要等の版数固定表現を単一情報源参照へ改める提案の追跡（plan-feedback）。

## 関連

- Supersedes: なし
- Superseded by: なし（計画側が「.NET 8 へ是正」を決定した場合、実装を .NET 8 へ戻す新 IADR を起票し本 IADR を
  `Superseded` とする。計画側が「制約を .NET 10／版数非固定表現へ更新」を決定した場合は `Accepted` を維持する。）
