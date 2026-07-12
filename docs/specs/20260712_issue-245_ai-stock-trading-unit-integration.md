---
title: サンプルユニット（ai-stock-trading）の submodule 結合・通し検証（Issue #245）
type: spec
status: in-progress
related_ids:
  - FR-14
  - IADR-0056
  - IADR-0058
  - IADR-0060
  - IADR-0064
author: claude
created: 2026-07-12
updated: 2026-07-12
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-14: 構成変更で完結する疎結合ユニット)"
related_specs:
  - "../adr/IADR-0060_submodule-unit-operations.md"
  - "../adr/IADR-0064_standalone-build-props-fallback.md"
  - "../adr/IADR-0065_public-unit-submodule-ci-fetch-no-token.md"
  - "../how-to/adding-a-unit-submodule.md"
  - "../../src/README.md"
---

# 仕様書: サンプルユニット（ai-stock-trading）の submodule 結合・通し検証（Issue #245）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-14（構成変更のみで完結する疎結合ユニット）
- 関連 ADR: [[IADR-0056]]（ユニット第一構成）／[[IADR-0058]]（private submodule の CI 取得）／
  [[IADR-0060]]（submodule 運用・CI 自動発見・単独ビルド規約）／[[IADR-0064]]（単独ビルド props の MSB4092 回避）
- 実装判断: [[IADR-0065]]（public ユニットの CI submodule 取得はトークン不要）
- Issue: #245（#230 残作業: サンプルユニットでの submodule 通し検証とトークン/Renovate 運用）

## 目的・背景

[[IADR-0060]] は追加可変機能ユニットの submodule 運用（テンプレート・CI 自動発見・単独ビルド規約・
バージョン固定）を整備したが、**サンプルユニット（別リポジトリ）での end-to-end 通し検証**は本リポジトリ
内で完結できないため #230 → #245 に繰延されていた。本作業は、実ユニット `endazon/ai-stock-trading`
（既にユニットレイアウト済み・AST PR #103）をサンプルユニットとして `src/ai-stock-trading` に submodule
追加し、**ビルド／単体テスト／フォーマット**が submodule 配置状態で成立することを検証・記録する。

実行時（`docker compose` 起動・実 RabbitMQ/PostgreSQL/Keycloak 疎通・実 API）はスコープ外とし、
ai-stock-trading 側の後続 issue（compose 整備 = ai-stock-trading#107、実 E2E = #82）へ分離する。

## スコープ

**含む（本 PR）**
- `src/ai-stock-trading` への git submodule 追加（gitlink 固定・`.gitmodules` 追記）。
- `ci.yml` の `lint` / `build-and-test` に「`src/*` のユニット submodule のみ非再帰 init」ステップを追加し、
  CI が自動発見 glob `src/*/backend/backend.slnx` で AST を実際にビルド・テスト・整形検査できるようにする。
  checkout の `submodules: recursive`/`true` は private な `planning`（IADR-0058）を巻き込み失敗するため使わない
  （[[IADR-0065]]）。
- 作業仕様書（本書）と [[IADR-0065]] の記録。

**含まない（後続へ分離）**
- ai-stock-trading の実行環境（docker-compose / appsettings / .env.example）整備 → ai-stock-trading#107。
- 実コンテナ/実 API による統合 E2E → ai-stock-trading#82。
- Renovate/Dependabot の `git-submodules` 自動更新の有効化（メンテナ判断・IADR-0060 記載どおり任意）。

## 実施内容と検証結果（ローカル: Windows / .NET SDK 10.0.301）

`git submodule add -b develop https://github.com/endazon/ai-stock-trading.git src/ai-stock-trading`
（AST 内 `planning` submodule も `--recursive` で取得）後、submodule 配置状態で以下を実測。

| 検証 | コマンド | 結果 |
| --- | --- | --- |
| ビルド | `dotnet build src/ai-stock-trading/backend/backend.slnx` | 成功（0 警告 / 0 エラー） |
| 単体テスト | `dotnet test src/ai-stock-trading/backend/backend.slnx` | 32 プロジェクト / 675 合格 / 0 失敗 / 0 スキップ |
| フォーマット | `dotnet format src/ai-stock-trading/backend/backend.slnx --verify-no-changes` | クリーン（差分なし） |

### 確認できた設計上の要点

- **CI 自動発見の妥当性**: `src/*/backend/backend.slnx` に `src/ai-stock-trading/backend/backend.slnx` が合致（[[IADR-0060]]）。
- **単一情報源の保全（[[IADR-0064]]）**: AST 直下 `Directory.Build.props` は「パスを `ParentDirectoryBuildProps`
  へ束ね、親 `src/Directory.Build.props` を import-chain、既定は単独時のみ」の修正パターン。submodule 配置時に
  上位の単一情報源を継承し**上書きしない**ことを 0 警告ビルドで実測。CPM（`Directory.Packages.props`）も
  MSBuild の最近接解決により二重適用は発生しない。
- **ユニット間コンパイル依存なし**: AST は platform Shared を直接参照せず、リポ内
  `TestSupport/AiStockTrading.TestSupport.PlatformShim`（[[IADR-0013]]・本番非使用の足場）で自己完結。
  したがって単独リポと submodule 配置で同一にビルド可能で、`src/README.md` の依存規則（ユニット外参照は
  platform Shared のみ許可）にも抵触しない（そもそもユニット外参照ゼロ）。

## fail-safe（安全既定）

- 本作業はビルド/テスト/整形のみで**外部送信・実発注・実接続を伴わない**。AST の実接続（発注 = #13 /
  市場データ = #81 / 実 LLM 費用 = #79）は既定 no-op で、compose 整備（#107）以降に明示設定時のみ有効化する。
- CI の submodule 取得は public リポの read のみ（トークン不要。[[IADR-0065]]）。secret は追加しない。

## 受け入れ条件

- [x] `src/ai-stock-trading` を submodule として追加（gitlink 固定・`.gitmodules` 追記）。
- [x] submodule 配置状態でユニットのビルド・単体テスト・`dotnet format --verify-no-changes` がローカルで成立。
- [ ] `ci.yml` の `lint` / `build-and-test` が submodule を取得し、CI 上で AST を含めて緑になる（PR の CI で確認）。
- [x] 実行環境（compose）整備を ai-stock-trading#107 に前提 issue として分離、#82 と相互参照。
- [ ] claude-review の指摘（🔴🟡🟢）をすべて解消。

## リスク・未確定

- CI 上でのみ顕在化する差異（Linux ランナーのパス大文字小文字・改行）: PR の CI で確認する。
- submodule pin の更新運用（Renovate `git-submodules`）は本 PR では有効化しない（メンテナ判断）。
