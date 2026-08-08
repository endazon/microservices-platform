---
title: IADR-0073 AST 向け BFF pass-through（assumptions/risk-controls/monitor）を interim の platform 同居から例外3 の unit-owned Bff プロジェクト（AiStockTrading.Bff.Endpoints）へ挙動不変で移行する
type: impl-adr
status: Accepted
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
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
  - "../../planning/projects/microservices-platform/06_technical/10_composability-design.md"
---

# IADR-0073: AST 向け BFF pass-through の例外3（unit-owned Bff）移行

- 状態: Accepted
- 日付: 2026-07-19
- 決定者: claude（実装）／ endazon（マージ判断）

## 起点・関連

- 関連する計画書 ID: **FR-14**（構成変更で完結する疎結合ユニット・合成点 1 行組み込み）
- 関連 ADR: [[IADR-0063]]（BFF 合成点・例外3 の規範）／[[IADR-0056]]（ユニット構成）／
  [[IADR-0057]]（一方向依存）／[[IADR-0070]]（assumptions・決定4=interim）／
  [[IADR-0071]]（risk-controls・決定4）／[[IADR-0072]]（monitor・決定4）／
  AST/IADR-0091（AST 側 `AiStockTrading.Bff.Endpoints` 新設）
- Issue: MSP #286（本 issue）／依存する AST 側 PR: endazon/ai-stock-trading（`AiStockTrading.Bff.Endpoints` 新設）
- 先行実装: MSP #283/PR #285・#287/PR #289・#288/PR #294

## 背景・課題

例外3（IADR-0063）の規範は、可変ユニットのドメイン固有 BFF エンドポイントを **当該ユニットの
`<unit>/backend/Bff/` プロジェクト**へ置き、合成点から 1 行参照する（knowledge は `Knowledge.Bff.Endpoints`
で実施済み）。AST は submodule（別リポ・読み取り専用）のため、#285/#289/#294 では 例外3 プロジェクトを AST 側へ
追加できず、3 モジュール（`AssumptionsBffEndpoints`/`RiskControlsBffEndpoints`/`MonitorBffEndpoints`）を
**interim** で `Platform.Bff/Foundation/Endpoints/`（platform 同居）に置いた（IADR-0070/0071/0072 の各決定4）。

各決定4 は「ユニットの BFF エンドポイントが増える前に恒久像（AST 側 unit-owned Bff ＋合成点参照）へ移す」ことを
follow-up #286 として約束していた。AST の BFF は現状 3 モジュール（計 13 ルート）で、これ以上増やす前に移行する。

論点は 3 点。

1. **AST 側プロジェクトの依存**: `AiStockTrading.Bff.Endpoints` は MSP の Shared や Contracts を参照するか
   （knowledge の Bff.Endpoints は `Platform.Shared.Infrastructure` と `Knowledge.Contracts` を参照している）。
2. **submodule 越境 ProjectReference の成立性**: platform（本リポ）の `Platform.Bff` が submodule 内 csproj を
   参照して、MSP CI・AST 単独 CI の双方でビルドが成立するか。
3. **後方互換（挙動不変）の担保**: 13 ルートの振る舞いを 1 つも変えずに所在だけ移す方法。

## 決定

### 1. `AiStockTrading.Bff.Endpoints` は FrameworkReference のみの自己完結ライブラリとする（DTO 非結合を維持）

3 モジュールはいずれも **DTO 非結合の pass-through**（IADR-0070 決定3・0071 決定1・0072 決定1）で、
`Microsoft.AspNetCore.*`（Minimal API）と `System.Net.Http` のみを使い、MSP の Contracts / Shared を一切参照しない。
よって AST 側プロジェクトは `OutputType=Library` ＋ `FrameworkReference Include="Microsoft.AspNetCore.App"` のみで
成立する（knowledge の Bff.Endpoints が Contracts/Infrastructure を参照するのとは異なり、**より薄い**）。

- **利点**: AST 単独リポでも AST の `Directory.Build.props`（net10.0 継承）だけでビルドでき（IADR-0064 の
  standalone フォールバック）、MSP 側へ逆依存しない＝一方向依存（IADR-0057）を厳格に保つ。
- pass-through が将来 AST 契約へ型付けしたくなった場合は、AST 側で `AiStockTrading.Bff.Endpoints` が AST の
  Contracts を参照すればよく、MSP は無変更（合成点参照のまま）。境界が AST 側に閉じる。

### 2. 合成点 `Platform.Bff` から submodule 内 csproj を 例外3 で参照する（checker が許可）

`Platform.Bff.csproj` に `ProjectReference`
`..\..\..\..\ai-stock-trading\backend\Bff\AiStockTrading.Bff.Endpoints\AiStockTrading.Bff.Endpoints.csproj`
を 1 行追加する（knowledge と同じ相対深度＝src まで 4 つ上がる）。`check-unit-dependencies.js` の
`isUnitBffEndpoints`（`src/<unit>/backend/Bff/`・unit≠platform）× `isBffCompositionHost`（Platform.Bff）で
`bff-composition-exception` として許可される（AST も src 配下の 1 ユニットとして扱われる＝knowledge と等価）。

- MSP CI は submodule checkout（public unit・IADR-0065 でトークン不要）で csproj が実在するため成立する。
- AST 単独 CI は `AiStockTrading.Bff.Endpoints` を AST の `backend.slnx` でビルドする（Platform.Bff は AST リポに
  無いので参照は現れない＝一方向）。
- **BFF イメージビルド（Dockerfile）**: `Platform.Bff/Dockerfile` は build context（リポ root）から
  `platform/` + `knowledge/` のみを COPY していたため、submodule の AST Bff を COPY する 1 行を追加する
  （knowledge と同型）。`images.yml` は既に src/* submodule を fetch 済み（IADR-0070）なので runner 上に実体があり、
  AST Bff は上位 `Directory.Build.props`（net10.0）を継承してビルドできる。
- **backend をビルド/リストアする全ワークフロー**: `Platform.Bff` を含む platform slnx を扱う各ワークフローは
  submodule fetch が要る。`ci.yml`/`images.yml` は既に fetch 済みだが、**`codeql.yml`（`dotnet build src/*/backend/backend.slnx` のトレースビルド）と `security.yml`（`dotnet restore` の脆弱性スキャン）は fetch 未実施**だったため、
  `ci.yml` と同型の `Fetch unit submodules` ステップを両者へ追加する。特に `security.yml` は `dotnet restore` が
  不在 ProjectReference を**エラーにせず黙ってスキップ**する（CI は green のまま AST Bff が脆弱性スキャンから漏れる）
  ため、fetch を追加して AST unit を確実に走査対象へ含める（現状は FrameworkReference のみで実害ゼロだが、将来 AST Bff が
  依存を持つと検出漏れになる）。
- **一般化**: **将来 別 submodule ユニットの BFF を例外3 参照する際も、当該ユニットの Bff を Dockerfile へ同梱し、
  backend をビルド/リストアする全ワークフロー（ci/images/codeql/security）で submodule を fetch する必要がある**
  （in-tree の knowledge と異なり submodule 越境ゆえの追加手順）。

### 3. 挙動不変: モジュール本体は 1 文字も変えず、名前空間とプロジェクト所在のみ移す

3 モジュールの中身（`MapGroup`・各ルート・`ProxyAsync`・DELETE 本文転送・502/4xx/409 透過・匿名 401・
`Authorization` 伝播）は**バイト等価**で AST 側へ移し、`namespace` のみ `Platform.Bff.Foundation.Endpoints` →
`AiStockTrading.Bff.Endpoints` へ変更する。拡張メソッド名（`MapAssumptionsBffEndpoints` 等）は保持するため、
合成点は `using` の切替えのみで 3 行の呼び出しは不変。`Program.cs` の 3 サービス HttpClient 登録も不変。

- 既存の振る舞いテスト（`BffAssumptionsEndpointTests`/`BffRiskControlsEndpointTests`/`BffMonitorEndpointTests`）は
  文字列クライアント名（`ConfigurationService` 等）と実 HTTP ルートで動くため無改変で緑。
- `BffEndpointCompositionTests`（12 モジュール／12 ルートグループの過不足検出）で合成点の等価性を固定。
  `using` に `AiStockTrading.Bff.Endpoints` を追加（Config/Authz 用の `Platform.Bff.Foundation.Endpoints` は残す）。
- 移行を固定する契約テスト（AST 3 モジュールの型が AST assembly 由来であること＝所在移行の回帰防止）を追加する。

### 4. 順序依存: AST PR を先行し、MSP は AST コミットへ pin する

MSP PR は `src/ai-stock-trading` を、`AiStockTrading.Bff.Endpoints`（＋その単体テスト）を含む AST コミット
（PR #202 の先端 `9c8a56b`）へ再pinする。AST PR が develop へマージされた後、develop 追従の再pin（dependabot もしくは手動）で最終化する。
squash merge の場合は先端コミットが dangling になり得るため、**この最終化を追従する follow-up issue #296
（priority:could）をマージと同時に起票済み**（放置防止）。マージ判断はユーザー。本 PR はリポ内検証
（`dotnet build/test`・`check-unit-dependencies.js`・helm template・#275 ドリフト・image ビルド成立）まで（live #284 分離）。

## 影響・トレードオフ

- **利点**: 例外3 の恒久像に統一（knowledge/AST が同形）。ユニット追加のたびに Platform.Bff を肥大させない。
  AST の BFF が AST 側へ閉じ、将来の型付け・拡張が MSP 無変更で可能。依存方向は checker で機械強制のまま。
- **代償**: MSP バックエンドビルドが submodule checkout に依存する（既に IADR-0065 で public unit 前提。CI は
  submodule fetch 済み）。移行に伴い submodule 再pinが 1 回必要（順序依存）。
- **却下案**: (a) interim 同居のまま放置 → 規範逸脱の恒久化・IADR-0070 決定4 の約束不履行で却下。
  (b) AST モジュールを MSP 内の新規 platform プロジェクトへ移す → AST ユニットの所有物を platform が持つのは
  例外3 の趣旨（ユニット所有）に反し却下。(c) `AiStockTrading.Bff.Endpoints` に MSP Contracts を参照させる →
  不要（DTO 非結合）かつ AST 単独ビルド不能・逆依存を招くため却下。

## 計画環流

なし（本 IADR は IADR-0063 の例外3 規範と IADR-0070/0071/0072 決定4 の約束を実装で履行するもの）。

## 検証

- 依存方向: `node scripts/check-unit-dependencies.js`（例外3=`bff-composition-exception` で AST Bff 参照を許可）と
  `--self-test` が緑。
- BFF: `dotnet build`／`dotnet test Platform.Bff.Tests`（既存 139+ ＋ composition 回帰 ＋ NetworkIsolation ＋
  移行契約テスト）が緑。振る舞い（13 ルート・匿名 401・OwnerOnly 後段委譲・4xx/409 透過・502・DELETE 本文転送）不変。
- フォーマット: `dotnet format Platform.Bff/... --verify-no-changes` 緑。
- deploy: `helm template`／`docker compose config`／`node scripts/check-image-mapping.js --self-test`（#275）緑
  （本 PR は deploy 面を変更しないため既存緑を維持）。
- live（実イメージビルド・OIDC・Istio 疎通・E2E）は #284 へ分離。
