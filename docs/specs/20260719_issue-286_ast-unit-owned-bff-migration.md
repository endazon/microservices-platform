---
title: AST 向け BFF pass-through（assumptions/risk-controls/monitor）を例外3 の unit-owned Bff プロジェクトへ移行（Issue #286）
type: spec
status: draft
related_ids:
  - FR-14
  - IADR-0056
  - IADR-0057
  - IADR-0063
  - IADR-0070
  - IADR-0071
  - IADR-0072
author: claude
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md (コンポーザブル)"
  - "../../planning/projects/microservices-platform/06_technical/10_composability-design.md (合成点)"
related_specs:
  - "../adr/IADR-0073_ast-unit-owned-bff-migration.md"
  - "../adr/IADR-0070_ast-frontend-integration.md"
  - "../adr/IADR-0071_ast-risk-controls-bff-integration.md"
  - "../adr/IADR-0072_ast-monitor-bff-integration.md"
  - "20260718_issue-288_ast-monitor-bff.md"
---

# 仕様書: AST 向け BFF pass-through の例外3（unit-owned Bff）移行（Issue #286）

> 本仕様書は実装着手前に作成する。#286 は priority:could の tech-debt（リファクタ）。
> **後方互換（挙動不変）を最優先**とし、#285/#289/#294 で確立した 3 モジュール・計 13 ルートの挙動を
> 完全保持したまま、interim の platform 同居配置を 例外3 の恒久像（AST unit-owned Bff プロジェクト＋
> 合成点 1 行参照）へ移す。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-14**（構成変更で完結する疎結合ユニット。合成点 1 行での組み込み）
- 実装判断: [[IADR-0063]]（BFF 合成点・例外3 の規範）／[[IADR-0056]]／[[IADR-0057]]（一方向依存）／
  [[IADR-0070]]（assumptions・決定4=interim）／[[IADR-0071]]（risk-controls・決定4）／
  [[IADR-0072]]（monitor・決定4）／**[[IADR-0073]]（本移行の設計判断）**
- Issue: MSP #286（本 issue）／依存する AST 側 PR: endazon/ai-stock-trading（`AiStockTrading.Bff.Endpoints` 新設）
- 先行実装: MSP #283/PR #285（assumptions）・#287/PR #289（risk-controls）・#288/PR #294（monitor）

## 目的・背景

`src/README.md`「依存規則 例外3」（IADR-0063）の規範は、可変ユニットのドメイン固有 BFF エンドポイントを
**当該ユニットの `<unit>/backend/Bff/` プロジェクト**へ置き、合成点（`Platform.Bff/Composition/BffEndpointComposition`）
から 1 行参照する形である（knowledge は `Knowledge.Bff.Endpoints` として実施済み）。

AST は submodule（読み取り専用・別リポ）のため、#285/#289/#294 では 例外3 のプロジェクトを AST 側へ追加できず、
**interim** として 3 モジュールを `Platform.Bff/Foundation/Endpoints/`（platform 同居）へ置いた（IADR-0070/0071/0072
の各決定4）。pass-through で DTO 非結合・依存も生じないが、恒久像は 例外3。ユニットの BFF エンドポイントが
増える前に恒久像へ移す（IADR-0070 決定4 の約束）。本 issue はその移行である。

## 現状（origin/develop = 10d79e0 時点）

interim の 3 モジュール（すべて `namespace Platform.Bff.Foundation.Endpoints`・`Microsoft.AspNetCore.App` の
FrameworkReference のみに依存＝MSP 内部プロジェクト参照ゼロ・DTO 非結合）:

| モジュール | ルート数 | 後段クライアント | 特記 |
| --- | --- | --- | --- |
| `AssumptionsBffEndpoints` (`/bff/assumptions`) | 3 | `ConfigurationService` | GET・GET /history・PUT |
| `RiskControlsBffEndpoints` (`/bff/risk-controls`) | 6 | `RiskManagementService` | settings/status/stage-gate |
| `MonitorBffEndpoints` (`/bff/monitor`) | 4 | `MarketMonitorService` | **DELETE も本文転送** |

合成点 `BffEndpointComposition.Modules` は 12 モジュール（knowledge 7 + platform 2 + AST 3）。
`Program.cs` は 3 サービスの名前付き HttpClient を登録済み（本 PR で変更なし）。

## スコープ（2 リポ・リポ内検証完結を優先／live 依存は分離）

### 先行: AST 側 PR（endazon/ai-stock-trading）

1. `backend/Bff/AiStockTrading.Bff.Endpoints/` を新設（`Knowledge.Bff.Endpoints` と同型の薄い pass-through
   ライブラリ。`OutputType=Library` ＋ `FrameworkReference Microsoft.AspNetCore.App` のみ）。
2. 3 モジュールを `namespace AiStockTrading.Bff.Endpoints` へ移設（**中身・ルート・pass-through 挙動は完全同一**。
   拡張メソッド名 `MapAssumptionsBffEndpoints`/`MapRiskControlsBffEndpoints`/`MapMonitorBffEndpoints` を保持）。
3. `backend/backend.slnx` の `/Bff/` フォルダへ登録（AST 単独 CI でビルド対象）。
4. AST 側 作業仕様書＋IADR-0091 を追加。

### 本 PR（MSP・リポ内で緑）

1. **submodule 再pin**: `src/ai-stock-trading` を AST 側 PR のコミットへ再pin（project を含む commit）。
2. **例外3 参照**: `Platform.Bff.csproj` に `ProjectReference` を 1 行追加
   （`..\..\..\..\ai-stock-trading\backend\Bff\AiStockTrading.Bff.Endpoints\AiStockTrading.Bff.Endpoints.csproj`。
   knowledge と同じ相対深度）。`check-unit-dependencies.js` は 例外3（`bff-composition-exception`）で許可。
3. **合成点移行**: `BffEndpointComposition.cs` の `using` を `AiStockTrading.Bff.Endpoints` へ切替え、3 行の
   `Map*BffEndpoints()` 呼び出しはそのまま（拡張メソッド名不変）。コメントを恒久像（例外3 完了）へ更新。
4. **interim 撤去**: `Platform.Bff/Foundation/Endpoints/{Assumptions,RiskControls,Monitor}BffEndpoints.cs` を削除。
   `Config`/`Authz` は platform 固有のため据え置き（`Platform.Bff.Foundation.Endpoints` 名前空間は残る）。
5. **テスト整合**: `BffEndpointCompositionTests.cs` の `using` を `AiStockTrading.Bff.Endpoints` へ追加
   （Config/Authz 用の `Platform.Bff.Foundation.Endpoints` は残す）。件数（12 モジュール／12 ルートグループ）は不変。
   移行を固定する契約テスト（AST 3 モジュールが AST assembly 由来であること）を追加。
6. **IADR 更新**: IADR-0070/0071/0072 の各決定4 に「#286 で 例外3 へ移行済み」を追記。IADR-0073 を新設。
7. **ビルド配線（submodule 越境の追従）**: `Platform.Bff` が例外3 で submodule の AST Bff を参照するため、
   backend をビルド/リストアする経路すべてに submodule 実体を行き渡らせる（IADR-0073 決定2）。
   - `Platform.Bff/Dockerfile` に AST Bff の `COPY` を追加（image ビルド成立。**COPY 漏れの是正**であり
     live 疎通確認ではない）。
   - `codeql.yml`（トレースビルド）と `security.yml`（脆弱性リストア）に `ci.yml` 同型の submodule fetch を追加
     （`security.yml` は `dotnet restore` が不在参照を黙ってスキップし AST が脆弱性スキャンから漏れる gap の是正）。

### 本 PR に含めない（分離）

- 実疎通（Istio/OIDC/E2E・AST デプロイ・実イメージの稼働）は #284(live)。本 PR はリポ内検証（`dotnet build/test`・
  `check-unit-dependencies.js`・helm template・#275 ドリフト・**image ビルドの成立**）まで（実行時疎通は含まない）。
- AST 契約（DTO）の BFF 側型付けは行わない（pass-through のまま。契約検証は後段と AST 側テスト）。

## 受け入れ基準（DoD 写像）

- [ ] AST リポに `AiStockTrading.Bff.Endpoints`（薄い pass-through）を新設し 3 モジュールを移設（AST PR）。
- [ ] MSP submodule ピン更新（project を含む AST commit）。
- [ ] `BffEndpointComposition.cs` を 例外3 参照へ移行し、interim 3 ファイルを撤去。挙動（13 ルート・匿名 401・
      OwnerOnly 後段委譲・4xx/409 pass-through・502・DELETE 本文転送）を完全保持。
- [ ] `check-unit-dependencies.js`（例外3=`bff-composition-exception`）で緑。自己試験（`--self-test`）緑。
- [ ] `Platform.Bff.Tests`（既存 139+ ＋ composition 回帰 ＋ NetworkIsolationTests）緑。移行契約テスト追加。
- [ ] `dotnet format --verify-no-changes` 緑。helm template 破綻なし。#275 ドリフト検査緑。
- [ ] IADR-0070/0071/0072 決定4 更新・IADR-0073 新設。

## リスク・後方互換の担保方法

- **挙動不変の担保**: モジュールの中身（ルート定義・ProxyAsync）は 1 文字も変えず、名前空間とプロジェクト所在
  のみ移す。既存の振る舞いテスト（`BffAssumptionsEndpointTests`/`BffRiskControlsEndpointTests`/
  `BffMonitorEndpointTests` = 匿名 401・pass-through・502・DELETE 本文）は文字列クライアント名で動くため無改変で緑。
- **合成点の等価性**: `BffEndpointCompositionTests`（12 モジュール／12 ルートグループの過不足検出）で回帰を固定。
- **依存方向**: 例外3（合成点→ AST Bff）は checker が許可。AST Bff は FrameworkReference のみで MSP へ逆依存
  しない（一方向維持）。
- **ビルド越境**: MSP の Platform.Bff は submodule 内 csproj を参照するため、CI は submodule checkout 必須
  （public unit・IADR-0065 でトークン不要）。AST 単独 CI では AST の `Directory.Build.props` を継承してビルド。
- **順序依存**: MSP PR は AST PR のコミットへ pin する。AST PR マージ後、develop 追従の再pin（dependabot もしくは
  手動）で最終化する。マージ判断はユーザー。
