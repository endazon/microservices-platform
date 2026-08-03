---
title: 技術要件書
type: tech-requirements
status: in-progress
related_ids:
  - NFR
  - FR-14
  - ADR-0004
  - ADR-0005
  - ADR-0007
  - ADR-0008
  - ADR-0020
  - ADR-0027
  - ADR-0029
  - ADR-0030
  - IADR-0048
  - IADR-0117
author: claude
created: 2026-07-04
updated: 2026-08-03
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/03_tech-stack-selection.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0020_dotnet-10-upgrade.md"
  - "../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (NFR)"
---

# 技術要件書

> 必須ドキュメント（リポジトリ単位）。本リポジトリの技術要件を定める。雛形は `docs/templates/tech_requirements_template.md`。
> 確定判断は実装ADR（`docs/adr/`）に残す。単一情報源は各設定ファイル（`src/Directory.Build.props` 等）。
>
> **リポジトリの位置づけ**: 主たる成果物は**マイクロサービスプラットフォーム基盤（platform ユニット）**。
> ナレッジ活用機能（knowledge ユニット）は基盤に付随する必須の可変機能セットである
> （issue #209 / [IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md)。
> フォルダ構成・依存規則は [`src/README.md`](../../src/README.md)）。

## 起点となる計画書（トレーサビリティ）

- 技術検討（06_technical）: `03_tech-stack-selection.md`（実装フレームワーク・データストア・実行基盤）、
  `10_composability-design.md`（コンポーザビリティ）
- 関連 ADR / 非機能要件（NFR）: ADR-0004（Keycloak OIDC）／ADR-0005（Istio mTLS）／ADR-0007（ArgoCD+Helm）／
  ADR-0008（k3s）／NFR（性能・可用性・セキュリティ・運用・拡張性）
- 計画制約との差異: **なし（解消済み）**。実装は **.NET 10 / C# 13** で、計画側も
  [ADR-0020](../../planning/projects/microservices-platform/07_adr/ADR-0020_dotnet-10-upgrade.md)（Accepted・2026-07-23）で
  実装フレームワークを **.NET 10（LTS）** に確定した。実装が先行していた経緯は [[IADR-0048]] と
  `feedback/20260709_dotnet10-target-framework-deviation.md` に記録している（旧「計画 fixed は .NET 8」との乖離は ADR-0020 で解消）。

## 技術スタック

| 区分 | 採用 | バージョン | 備考 |
| --- | --- | --- | --- |
| 言語（バックエンド） | C# | 13（`LangVersion 13`） | 単一情報源 [`src/Directory.Build.props`](../../src/Directory.Build.props)。Nullable/ImplicitUsings 有効 |
| ランタイム（バックエンド） | .NET | 10（`net10.0`） | ADR-0020（計画側も .NET 10 で確定）／[[IADR-0048]]（実装の先行採用）。`global.json` は SDK 8.0.0 + `rollForward: latestMajor` |
| フレームワーク（バックエンド） | ASP.NET Core（Minimal API） | .NET 10 同梱 | アプリケーション層の標準は ADR-0030（後述「バックエンドアプリケーション層標準」）。ORM は EF Core |
| パッケージ管理 | Central Package Management | — | バージョンは [`src/Directory.Packages.props`](../../src/Directory.Packages.props) に集約。ソリューションは `.slnx` |
| 言語（フロントエンド） | TypeScript | 5.6 | `src/<unit>/frontend/`（npm workspaces ルート = `src/`）。Node は CI と揃え 22 |
| フレームワーク（フロントエンド） | React + Vite | React 18 / Vite 5（ESM） | SPA。基盤(`platform/frontend`)/画面(`knowledge/frontend` の features)分離（[[IADR-0033]]・[[IADR-0056]]）。BFF は `/bff/*` 経由 |
| 認証（利用者） | Keycloak（OIDC / Authorization Code + PKCE） | — | ADR-0004。SPA は public client `spa-web`（`oidc-client-ts`） |
| データストア（業務） | PostgreSQL | — | DB per Service（ADR-0002）。jsonb 属性は EF Core の ValueComparer で content 比較（#184） |
| データストア（ベクトル） | Qdrant | — | モデル別コレクション・決定的チャンク ID（[[IADR-0002]]） |
| オブジェクトストレージ | MinIO（S3 互換） | RELEASE.2025-04-08 | 正規化本文・資産。ClusterIP のみ（[[IADR-0024]]）。資格情報は k8s Secret |
| メッセージング | RabbitMQ / Kafka（**Wolverine**） | — | ADR-0027 / ADR-0028。イベント駆動パイプライン。契約は `Shared.Contracts`。**現行実装は MassTransit で、Wolverine への置き換えは各サービスの再実装 issue（#438〜#451）で行う**（#455 / #441） |
| 実行基盤 | k3s（Kubernetes） | — | ADR-0008。Helm `deploy/helm/microservices-platform`、Namespace `microservices-platform` |
| サービスメッシュ | Istio（Envoy mTLS） | — | ADR-0005 / [[IADR-0026]]。STRICT mTLS（`PeerAuthentication`/`DestinationRule`） |
| CI/CD・GitOps | ArgoCD + Helm | — | ADR-0007。Git を単一の真実源に宣言的同期（`deploy/argocd/`） |
| コンテナレジストリ | Harbor | — | ADR-0007。`global.image.registry: harbor.internal`、Pull は `imagePullSecrets` |
| 可観測性 | OpenTelemetry（OTLP） | — | `Otlp__Endpoint` で collector へ送出。トレース相関に利用 |

## アーキテクチャ概要

マイクロサービス（DB per Service）＋ BFF 集約＋イベント駆動パイプライン。フロント（SPA）は BFF のみを叩き、
BFF が ABAC スコープ解決（AuthorizationService）と各サービス呼び出しを集約する。取り込みは
DataSource→Conversion→Ingestion→（Document/Wiki）のイベントパイプラインで、段の有効/無効・購読は宣言的
構成（`pipeline.json`・[[IADR-0028]]）で組み替える（FR-14）。

```mermaid
flowchart TB
  SPA[React SPA] -->|/bff/*| BFF
  BFF --> AuthZ[AuthorizationService（ABAC）]
  BFF --> Doc[DocumentService]
  BFF --> Retr[RetrievalService]
  BFF --> AI[AiAnalysisService]
  subgraph Pipeline[イベント駆動パイプライン]
    DS[DataSourceService] --> Conv[ConversionService] --> Ing[IngestionService]
    Conv --> Doc
    Doc --> Wiki[WikiService] --> WikiJs[(Wiki.js)]
    Ing --> Qdrant[(Qdrant)]
  end
  Doc --> PG[(PostgreSQL / DB per Service)]
  Conv --> MinIO[(MinIO)]
  AI --> LLM[LlmGateway] -->|egress matrix| External[(外部/自ホスト LLM)]
```

## バックエンドアプリケーション層標準（ADR-0030）

計画側が [ADR-0030](../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md)（Accepted）と
[12_backend-application-stack](../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md)（`fixed`）で
アプリケーション層のライブラリ標準と設計様式を確定した。棚卸し表の**全量は計画書が正**であり、本節は
実装リポジトリで守る要点と本リポ固有の具体化のみを記す（作業仕様書:
[20260803_issue-455_backend-application-standard.md](../specs/20260803_issue-455_backend-application-standard.md)）。

### 設計様式

- Pragmatic Clean Architecture ＋ **Vertical Slice**（Feature 単位。不要な Repository / Service 抽象を作らない）
- API は **ASP.NET Core Minimal API**（.NET 10）
- CQRS のローカルディスパッチも **Wolverine ハンドラに統一**する。独自 Dispatcher・**MediatR は使わない**
- **Domain 層は外部ライブラリへ依存しない**（.NET 標準のみ）。Result 型は共有カーネルに自前実装する

### プロジェクト構成（サービス単位）

```text
src/<unit>/backend/Services/<Name>Service/
 ├── src/{<Name>.Api, <Name>.Application, <Name>.Domain, <Name>.Infrastructure, <Name>.Contracts}
 └── tests/{<Name>.UnitTests, <Name>.IntegrationTests}
```

**共有カーネルはサービス単位に置かない**（[IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md)）。
本リポジトリはユニット第一構成（[[IADR-0056]]・ADR-0019）を採り、ユニット外から参照できるのは
`src/platform/backend/Shared/` のプロジェクトのみである（[`src/README.md`](../../src/README.md) の依存規則）。
Result / Error はサービスをまたいで同一の型である必要があるため、**`Platform.Shared.Kernel` として 1 つに集約**する。
計画書の構成図はサービス内の論理レイヤを示したものであり、物理配置の具体化として扱う。

この配置は [[IADR-0056]] §決定 3 の**部分改定**にあたる。同決定はユニット外参照を
`src/platform/backend/Shared/` の **2 プロジェクト**（`Platform.Shared.Contracts` / `Platform.Shared.Infrastructure`）
のみに限っていたが、[IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md) が
`Platform.Shared.Kernel` を加えて **3 プロジェクトへ改定**した（2026-08-03 / #455）。改定はこの 1 点に限り、
「platform → 可変ユニットは禁止」「統合テストの例外」は引き続き有効である。
`Platform.Shared.Kernel` は **.NET 標準以外の `PackageReference` を持たない**（ADR-0030 選定基準 3 を成立させる
ための置き場であり、`scripts/check-backend-libraries.js` が `*.Domain.csproj` の許容 `ProjectReference` を
同プロジェクトのみとして機械強制する）。**実体プロジェクトは未作成**で、最初にそれを必要とする
サービス再実装 issue（#438〜#451）が作成する。

### ライブラリ標準（要点）

| 用途 | 採用 | 使わない |
| --- | --- | --- |
| ローカル/リモートハンドラ・Outbox | Wolverine | MediatR・独自 Dispatcher・MassTransit |
| マッピング | Riok.Mapperly（ソースジェネレータ） | AutoMapper・Mapster |
| 検証 | FluentValidation | — |
| Result 表現 | `Platform.Shared.Kernel` の自前 Result / Error | OneOf・CSharpFunctionalExtensions |
| エラー応答 | 標準 `AddProblemDetails()` + `IExceptionHandler` | Hellang.Middleware.ProblemDetails |
| ロギング | 標準 `ILogger` + OpenTelemetry Logs | Serilog 系（Seq 含む） |
| キャッシュ | HybridCache（L1）+ Redis（L2） | — |
| レジリエンス | Polly（`Microsoft.Extensions.Http.Resilience` 経由） | — |
| テスト | xUnit **v3**（※現行は v2。後述）・**AwesomeAssertions**・NSubstitute・Testcontainers・Respawn | FluentAssertions（v8 商用化） |
| API ドキュメント / バージョニング | Microsoft.AspNetCore.OpenApi + Scalar / Asp.Versioning.Http | Kiota・NSwag |

バージョンの単一情報源は [`src/Directory.Packages.props`](../../src/Directory.Packages.props)（CPM）である。

### 機械的強制と移行の進め方

不採用ライブラリの混入は [`scripts/check-backend-libraries.js`](../../scripts/check-backend-libraries.js) が
`.csproj` と MSBuild の `.props` / `.targets`（`Directory.Build.props` は配下の全プロジェクトへ
`PackageReference` を一括注入できるため。[#471](https://github.com/endazon/microservices-platform/issues/471)）の
`PackageReference`・`GlobalPackageReference` と `.cs` の `using` を走査して検出し、CI で止める。
**CPM の `PackageVersion`（版の中央定義）は違反にしない** — 下記 ratchet の消化が終わるまで、
[`src/Directory.Packages.props`](../../src/Directory.Packages.props) は不採用パッケージの版定義を正当に持つ。ただし**現行実装は MassTransit / FluentAssertions / Serilog を
広範に使用中**（実測: `.csproj` 15 / 14 / 3、`.cs` 59 / 129 / 15）であるため、即時禁止では
「成果物は正しいのに赤」が常態化する（同じ判断の先例は [`scripts/README.md`](../../scripts/README.md) の
`check-permission-denials.js` の**段階ポリシー**——赤の常態化は「赤を無視する学習」を生み検査の目的そのものを
壊すため、許容値までは警告に留める。planning#146 / #149 / #160）。よって **ratchet 方式**を採る。

- 既知の違反は `scripts/backend-library-baseline.json` にプロジェクト単位で記録する
- baseline に無いプロジェクトでの違反は **fail**（新規混入を止める）
- baseline 内の違反は warn（残件として実行サマリに出す）
- baseline にあるのに違反が消えた場合も **fail**（baseline の減らし忘れを検出する）

各サービスの再実装 issue（#438〜#451）は、移行と同時に baseline から自プロジェクトを削除する。baseline が
空になった時点で不採用パッケージを `Directory.Packages.props` から削除する。

**xUnit は標準が v3 だが現行は v2 である。** `xunit.runner.visualstudio` は v2 用（2.x）と v3 用（3.x）で
別系列であり、**CPM は 1 パッケージ 1 バージョンしか持てない**ため、v3 へ移ると既存 30 のテスト
プロジェクトが同時に移らざるを得ない。この切替は独立した issue で行う。それまで **`xunit.v3` を参照する
プロジェクトを作ってはならない**（非互換の runner と組み合わさる）。`check-backend-libraries.js` が
`templates/` を含めて検査し、混入を止める。

**年 1 回、AwesomeAssertions・Wolverine のライセンス / 保守状況を点検する**（ADR-0030 フォローアップ）。
手順は[運用仕様書](../operations/)に記載する。

## 非機能要件の実現方針

| 区分 | 目標 | 実現方針 |
| --- | --- | --- |
| 性能 | 検索 p95 1.5s / RAG 初回 5s / 取り込み 1万件・時 / 更新 15 分以内反映 | ハイブリッド検索＋ベクトル索引（Qdrant）、SSE ストリーミング（[[IADR-0037]]）。**負荷試験は未実施（#196）** で目標達成の実測が未追跡 |
| 可用性 | 99.9%（月間ダウンタイム約 43 分以内） | HPA + PodDisruptionBudget（#197・`scaling`）、readiness/liveness プローブ、RollingUpdate、GitOps ロールバック（Git revert） |
| セキュリティ | 認証・認可・データ越境統制・監査ログ | Keycloak OIDC（ADR-0004）＋ ABAC fail-closed（[[IADR-0012]]）、Istio STRICT mTLS（[[IADR-0026]]）＋ NetworkPolicy、deny-by-default／存在秘匿（[[IADR-0009]]）、LLM egress マトリクス（[[IADR-0025]]）。詳細は `docs/security/security.md` |
| 運用・保守 | 検出 5 分以内 / MTTR 30 分以内 | OTel 可観測性、ArgoCD GitOps、構成ドリフト検出（[[IADR-0029]]）、起動時 fail-fast（[[IADR-0028]]）。**監視アラート・バックアップ・Runbook は整備中（#198）** |
| 拡張性 | 段の挿抜・購入部品の差し替え（FR-14） | 宣言的パイプライン構成（`pipeline.json`・[[IADR-0028]]）＋ Foundation/Composable 構造（[[IADR-0027]]）。契約は `Shared.Contracts`。共通エンベロープ・契約テストは条件付き繰延（[[IADR-0049]]） |

## 開発・ビルド・テスト・デプロイ

- **バックエンド**: `dotnet build` / `dotnet test`（xUnit。標準は v3・現行は v2 で各サービス再実装時に切替）/
  `dotnet format --verify-no-changes`（CI lint ゲート）を
  ユニット別ソリューション（[`src/platform/backend/backend.slnx`](../../src/platform/backend/backend.slnx) /
  [`src/knowledge/backend/backend.slnx`](../../src/knowledge/backend/backend.slnx)）毎に実行する。
- **フロントエンド**: `npm run lint`（ESLint flat config）/ `npm run typecheck` / `npm run test`（Vitest）/
  `npm run test:coverage`（v8・しきい値ラチェット）/ E2E は Playwright。
- **CI**: バックエンド [`ci.yml`](../../.github/workflows/ci.yml)、フロント [`frontend.yml`](../../.github/workflows/frontend.yml) /
  [`frontend-tests.yml`](../../.github/workflows/frontend-tests.yml)。セキュリティ（gitleaks/dependency-review）・CodeQL。
  コミット/PR 件名はトレーサビリティ規約を機械検査（`check-commit-messages.js` / `pr-title.yml`）。
- **デプロイ**: ArgoCD が `deploy/helm/microservices-platform` を宣言的同期（ADR-0007）。構成変更のみで段の組み替え・
  スケール調整が完結する（GitOps）。

## 未決事項

- 性能目標の負荷試験・実測（#196）。達成状況に応じ HPA しきい値（#197 `scaling.hpa`）を調整する。
- 監視アラート閾値・バックアップ/リストア・Runbook の整備（#198）。
- ~~計画制約「.NET 8」の更新 or 是正の計画側判断（[[IADR-0048]] / plan-feedback）。~~
  **決着済み（2026-07-23）**: 計画側が [ADR-0020](../../planning/projects/microservices-platform/07_adr/ADR-0020_dotnet-10-upgrade.md)（Accepted）で
  .NET 10（LTS）に確定し、乖離は解消した。残る作業は同 ADR のフォローアップ
  （個別プロセス文書に残る「.NET 8」表記の順次追随）で、計画側の担当である。
- ADR-0030 標準への移行残件（#455）: 不採用ライブラリの baseline を各サービス再実装 issue で解消し、
  空になった時点で `Directory.Packages.props` から不採用パッケージを削除する。xUnit v2 → v3 の切替時期と
  `Xunit.SkippableFact` の v3 代替（`Assert.Skip`）は各サービス側で確定する。
- サービス間 HTTP の `Refit` は棚卸し表に記載が無い。ADR-0029（内部同期は gRPC）との関係は #441 で決着する。
