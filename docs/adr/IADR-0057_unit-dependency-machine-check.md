---
title: IADR-0057 ユニット依存方向の機械検査は軽量スクリプト（csproj 走査）＋フロント ESLint で行い、NetArchTest は採らない
type: impl-adr
status: Accepted
related_ids:
  - FR-14
  - ADR-0018
  - IADR-0027
  - IADR-0056
author: claude
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-14: 構成変更で完結する疎結合)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018 (契約・イベントによる疎結合)"
---

# IADR-0057: ユニット依存方向の機械検査は軽量スクリプト＋フロント ESLint で行う

- 状態: Accepted
- 日付: 2026-07-11
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-14（構成変更のみで完結する疎結合ユニット）
- 関連 ADR: ADR-0018（契約・イベント疎結合）／[[IADR-0027]]（Foundation/Composable 依存方向）／[[IADR-0056]]（ユニット第一構成）
- 関連仕様書: `docs/specs/20260711_issue-231_unit-dependency-guard.md`、[`src/README.md`](../../src/README.md)（依存規則）
- Issue: #231

## コンテキストと課題

ユニット間の依存方向規則（[`src/README.md`](../../src/README.md) §依存規則）は現状レビュー頼みで機械検査がない。規約は次の 3 点：

1. **ユニット外参照**: 可変ユニット → platform は `platform/backend/Shared/` の 2 プロジェクト（`Platform.Shared.Contracts` / `Platform.Shared.Infrastructure`。#227/IADR-0062 で `KnowledgePlatform.*` から改名）のみ許可。**platform → 可変ユニットは禁止**（一方向依存）。例外は統合テスト（`Tests/`）が検証対象サービスを ProjectReference する場合。
2. **Foundation → Composable 禁止**（IADR-0027）: `Foundation/` 配下から `Composable/` の実装へ `using` してはならない。
3. **フロント**: 可変ユニット（`@knowledge`）は `@foundation` のみ参照可。platform/frontend 側から `@knowledge` を参照するのは合成点（`src/features/index.ts`）1 箇所のみ。

これらを CI で fail させる機械検査をどう実現するか。

## 検討した選択肢

1. **backend: NetArchTest.Rules を各 Tests プロジェクトへ追加**: アセンブリのメタデータ（型参照）を検査でき表現力は高い。反面、(a) 各ユニット・各 Tests プロジェクトへ依存パッケージとテストコードを分散追加する必要があり、(b) 検査はビルド済みアセンブリ単位のためユニット横断（platform↔可変ユニット）の参照方向を 1 箇所で俯瞰できず、(c) submodule として切り出す将来のユニットにも同じボイラープレートを強制する。ProjectReference レベルの一方向依存（規則 1）は本来アセンブリ参照ではなくプロジェクト参照の問題で、ソース参照の合成（frontend）は .NET の型検査対象外。
2. **backend: 軽量 Node スクリプトで csproj の ProjectReference と `Foundation/` 配下の `using` を静的走査（本決定）**: 既存の CI 検査（`check-doc-links.js` / `check-commit-messages.js` / `validate-pipeline-config.js`）と同じ「外部依存ゼロの Node スクリプト＋CI ジョブ＋自己テスト」方式に揃う。リポジトリ全体（全ユニット）を 1 プロセスで俯瞰でき、submodule ユニットにもコード追加不要（親リポの CI が `src/**/*.csproj` を走査するだけ）。
3. **frontend: ESLint `no-restricted-imports`（本決定）**: フロントの合成点制約は型ではなく import 経路の制約であり、既に必須化されている `npm run lint` に相乗りできる。

## 決定

**選択肢 2＋3 を採用する。**

- **backend**: `scripts/check-unit-dependencies.js`（外部依存ゼロ）が `src/**/*.csproj` の `ProjectReference` を解決してユニット間参照を判定し、`Foundation/` 配下 `.cs` の `using *.Composable.*` を検出する。CI は `ci.yml` に `unit-dependencies` ジョブを追加し、`--self-test`（検査器自体の単体試験）＋本走査を必須チェックにする。
- **frontend**: `src/eslint.config.js` に 2 つの境界オーバーライドを追加する。(a) `platform/frontend/src/**`（合成点 `features/index.ts` を除く）から `@knowledge` の import を `no-restricted-imports` で禁止、(b) `knowledge/frontend/src/**` から `@features`（platform 合成点）の import を禁止。`npm run lint` が既に必須のため CI 追加は不要。
- 採用方式と理由は本 IADR と `scripts/README.md` に記録する。

## 理由

- **既存様式との一貫性**: CI 検査は Node スクリプト＋自己テスト方式で定着しており（doc-links / commit-messages / pipeline-config）、学習コストと保守点を増やさない。
- **ユニット横断の俯瞰**: 規則 1（platform→可変ユニット禁止）は複数アセンブリにまたがる参照方向の問題で、リポジトリ全体を 1 プロセスで走査する方が NetArchTest（アセンブリ単位）より素直に表現できる。
- **submodule 親和性**: 追加可変ユニット（#230）は親リポの走査対象に入るだけで検査され、テストコードのボイラープレートを強制しない（IADR-0056 の submodule 切り出し可能性を損なわない）。
- **合成点制約の自然な写像**: フロントの「合成点以外は @knowledge 禁止」は import 経路の制約であり ESLint がそのまま表現できる。

## 結果

- `scripts/check-unit-dependencies.js`（規則 1・2 の静的検査、`--self-test` 内蔵）。
- `scripts/scripts.test.js` に検査ロジックの単体テストを追加。
- `.github/workflows/ci.yml` に `unit-dependencies` ジョブ（必須チェック）。
- `src/eslint.config.js` にフロント境界ルール（合成点以外の `@knowledge` / `@features` import を error）。
- `scripts/README.md` に方式・根拠を追記。

## フォローアップ

- 追加可変ユニット（#230）のサンプルで platform→可変ユニット参照が実際に fail することを通し検証する。
- 契約の階層化（#229）で `<unit>.Contracts` を分離した場合、許可参照先（現状 Shared 2 プロジェクト）の更新が必要になり得る。

## 関連

- Supersedes: なし
- Superseded by: なし
