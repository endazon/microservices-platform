---
title: ユニット依存方向の機械検査（platform→可変ユニット禁止・Foundation→Composable 禁止・合成点以外の @knowledge 禁止）（Issue #231）
type: spec
status: done
related_ids:
  - FR-14
  - ADR-0018
  - IADR-0027
  - IADR-0056
  - IADR-0057
author: claude
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-14: 構成変更で完結する疎結合)
  - planning:projects/microservices-platform/07_adr/ADR-0018 (契約・イベント疎結合)
related_specs:
  - "../adr/IADR-0057_unit-dependency-machine-check.md"
  - "../../src/README.md"
---

# 仕様書: ユニット依存方向の機械検査（Issue #231）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-14（構成変更のみで完結する疎結合ユニット）
- 関連 ADR: ADR-0018（契約・イベント疎結合）／IADR-0027（Foundation/Composable 依存方向）／IADR-0056（ユニット第一構成）
- 実装判断: [IADR-0057](../adr/IADR-0057_unit-dependency-machine-check.md)（軽量スクリプト＋ESLint 方式の採択）
- Issue: #231

## 目的・背景

ユニット間の依存方向規則（[`src/README.md`](../../src/README.md) §依存規則）は現状レビュー頼みで機械検査がなく、規約違反の参照が混入しても CI で止められない。再編（#210）で確定した依存方向を CI で機械強制する。

## 対象範囲

- 対象（新規/変更）:
  - `scripts/check-unit-dependencies.js`（新規）: backend の規則 1・2 を静的検査。`--self-test` 内蔵。
  - `scripts/scripts.test.js`（変更）: 検査ロジックの単体テストを追加。
  - `.github/workflows/ci.yml`（変更）: `unit-dependencies` ジョブを追加（必須チェック）。
  - `src/eslint.config.js`（変更）: フロント境界ルール（合成点以外の `@knowledge` / `@features` import を禁止）。
  - `scripts/README.md`（変更）: 方式・根拠を追記。
- 対象外:
  - 名前空間・アセンブリ名の改名（#227）。本検査は現行命名（`KnowledgePlatform.Shared.*`）のまま参照**方向**のみを見る。
  - 契約の階層化・BFF 合成（#229）。分離後は許可参照先の更新が要る（#229 側で対応）。
  - **`src/README.md` §依存規則の「規則2」（`Composable/Steps/` の段どうしは直接参照せずイベント経由のみ）**。
    Issue #231 の受け入れ基準（platform→可変ユニット／Foundation→Composable／合成点以外の `@knowledge` import の 3 点）に
    規則2は含まれないため本検査の対象外とする。段（Step）は同一名前空間 `*.Composable.Steps` に属し、相互参照は
    `using` を伴わない同一名前空間の型利用になり得るため、`using` 走査ベースの本方式では機械検出が難しい（NetArchTest 等の
    型参照解析が必要）。必要になった時点で別途フォローアップ（[IADR-0057](../adr/IADR-0057_unit-dependency-machine-check.md) のトレードオフの範囲）とする。

## 検査ルール（src/README.md §依存規則の写像）

backend（`scripts/check-unit-dependencies.js`）:

1. **ユニット外参照（規則 3）**: 各 `.csproj` の `ProjectReference` を解決し、参照元ユニットと参照先ユニット（`src/<unit>/` の第 1 セグメント）が異なる場合、
   - `platform → 可変ユニット` は常に違反（一方向依存）。
   - `可変ユニット → platform` は、参照先が `platform/backend/Shared/`（Contracts / Infrastructure の 2 プロジェクト）なら許可。それ以外は違反。ただし参照元が **Tests プロジェクト**（`*.Tests.csproj` または `Tests/` 配下）の場合は統合テスト例外として許可（例: `KnowledgePlatform.IntegrationTests` → `AuthorizationService.Api`）。
2. **Foundation → Composable（規則 1 / IADR-0027）**: `Foundation/` ディレクトリ配下の `.cs` に `using <ns>.Composable(.|;)` が現れたら違反。

frontend（`src/eslint.config.js`）:

3. **合成点制約（規則 例外2）**: `platform/frontend/src/**`（合成点 `src/features/index.ts` を除く）から `@knowledge` / `@knowledge/*` の import を禁止。`knowledge/frontend/src/**` から `@features`（platform 合成点）の import を禁止。

## 実装方針

- Node 標準モジュールのみ（既存 `check-doc-links.js` 等と同様に外部依存ゼロ）。
- 検査器はロジック関数（`classifyProjectReference` / `scanFoundationComposable` 等）を `module.exports` し、`scripts.test.js` から単体テストする。`--self-test` 引数で合成データによる自己試験を CI 冒頭で実行する。
- 現行ツリーに違反が無い（＝グリーン）ことを確認し、意図的に違反を注入すると fail することを自己テストで固定する。

## 受け入れ基準（Issue #231）との対応

- [x] 規約違反の参照（platform→可変、Foundation→Composable、合成点以外の @knowledge import）が CI で fail する
  - backend 2 種は `unit-dependencies` ジョブ（`check-unit-dependencies.js`）で fail。
  - frontend の @knowledge import は `lint` ジョブ（ESLint `no-restricted-imports`）で fail。
- [x] 現行ツリーは検査を通過する（誤検知なし）。

## 検証

- `node scripts/check-unit-dependencies.js --self-test` → 自己試験 OK。
- `node scripts/check-unit-dependencies.js` → 現行ツリー 違反 0。
- `node scripts/scripts.test.js` → 追加テスト含め全 pass。
- `npm run lint`（src/）→ 合成点以外の `@knowledge` import が無く pass。合成点への違反注入で error。

## 実装判断・フォローアップ

- 方式選定（NetArchTest ではなく軽量スクリプト＋ESLint）は [IADR-0057](../adr/IADR-0057_unit-dependency-machine-check.md) に記録。
- 追加可変ユニット（#230）のサンプルで platform→可変ユニット参照が実際に fail することを通し検証（#230 側）。
- 契約の階層化（#229）後は許可参照先の更新が必要になり得る。
