---
title: 可変部品（Composable コンポーネント）共通実装ガイド — 基盤への接続仕様と実装指示
type: tech
status: fixed
related_ids:
  - FR-14
  - FR-15
  - ADR-0018
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
  - "../../planning/projects/microservices-platform/06_technical/10_composability-design.md"
  - "../../planning/projects/microservices-platform/06_technical/09_datasource-connectors.md"
related_specs:
  - ../specs/20260709_composable-component-implementation-guide.md
  - ../adr/IADR-0027_composability-folder-structure.md
  - ../adr/IADR-0028_declarative-pipeline-config.md
  - ./composability-classification.md
---

# 可変部品（Composable コンポーネント）共通実装ガイド

基盤（Foundation＝固定部）に接続する**可変部品**（パイプライン段・ポートアダプタ・プロバイダ・
コネクタ・新サービス・フロントエンド feature）を実装するための**単一の入口**となる共通仕様・
実装指示書である。個別の規約はそれぞれの原典（IADR 等）が正であり、本書は入口と手順を提供する。
矛盾がある場合は原典を優先し、本書を修正すること。

## 0. 前提 — 固定と可変の境界（ADR-0018）

| 区分 | 対象 | 変更手段 |
| --- | --- | --- |
| **固定（Foundation）** | 同期 API 経路・ABAC・メッセージ基盤・イベント契約・正規化形式・認証・可観測性 | 変更には新 ADR が必要 |
| **可変（Composable）** | パイプライン段・イベントバインディング・ポート実装（アダプタ）・プロバイダ・コネクタ・フロント feature | 構成変更＋プラグイン追加（本書の手順） |

現状の棚卸しは[固定/可変区分表](./composability-classification.md)を参照。
コード配置規約は [IADR-0027](../adr/IADR-0027_composability-folder-structure.md)、
宣言的構成は [IADR-0028](../adr/IADR-0028_declarative-pipeline-config.md) が原典である。

## 1. 基盤が可変部品に提供するもの（接続仕様）

可変部品が依存してよい基盤側の資産は以下に限る。

### 1.1 契約（`src/Shared/KnowledgePlatform.Shared.Contracts`）

- **イベント契約型**（`Events/`）: 段の入出力。**後方互換の追加のみ許可**（破壊的変更は禁止。
  変更が必要なら新 ADR）。新イベント型の追加は契約追加として PR レビュー＋`pipeline.json` の
  `events` への列挙が必要。
- **同期 DTO**: サービス間同期 API の契約。経路自体は固定（[区分表 §1](./composability-classification.md)）
  であり、契約は [docs/api/openapi.yaml](../api/openapi.yaml) でバージョン管理される。

### 1.2 横断基盤（`src/Shared/KnowledgePlatform.Shared.Infrastructure`）

| 提供物 | 内容 | 可変部品からの使い方 |
| --- | --- | --- |
| メッセージ基盤 | MassTransit + RabbitMQ 配線（`AddPlatformMassTransit`） | 直接触らない。段の登録は §2.1 の拡張メソッド経由 |
| 宣言的パイプライン構成 | `Foundation/Pipeline/`（`IPipelineStep`・`AddKnowledgePlatformPipelineConfig`・`AddKnowledgePlatformPipelineStep<T>`） | 段が実装・利用する（§2.1） |
| 認証 | JWT/Keycloak・ロール変換 | 自動適用（サービス側 Program.cs の基盤登録で有効） |
| 可観測性 | OTel・相関 ID・ヘルスチェック | 自動適用。独自計装を追加する場合も OTel API に統一 |
| ストレージポート | `IObjectStorageClient`（S3/Null 実装, IADR-0024） | アダプタから委譲先として利用可 |

### 1.3 実行時構成

- パイプライン宣言: `deploy/helm/knowledge-platform/files/pipeline.json`（**構成の正**。
  運用手順は [同ディレクトリの README](../../deploy/helm/knowledge-platform/files/README.md)）。
- 実装差し替えの選択は**構成**（接続文字列・エンドポイントの有無、ルーティング表）または
  **DI 登録**（合成ルート）で行う。ビルド分岐（`#if` 等）は用いない。

## 2. 部品種別ごとの実装手順

### 2.1 パイプライン段（Step）— イベント購読→処理→発行

配置: `<Service>/Composable/Steps/`。段はコア改修なしで追加できる（FR-14）。

1. `IConsumer<TIn>`（MassTransit）と `IPipelineStep`（`Shared.Infrastructure.Foundation.Pipeline`）を
   実装する。`static abstract string StepName` は `pipeline.json` の `steps[].name` と一致させる。
2. 対象サービスの `Program.cs`（合成ルート）に `AddKnowledgePlatformPipelineStep<T>(pipeline)` を
   1 行追加する。
3. `pipeline.json` に段を宣言する（`name` / `service` / `consumer`＝型完全名 / `input` / `outputs` /
   `enabled`）。入力イベント型は `events` に列挙済みであること。
   ローカル検証: `node scripts/validate-pipeline-config.js deploy/helm/knowledge-platform/files/pipeline.json`
4. 段をホストするサービスが新規なら Helm `values.yaml` で `pipelineSteps: true` を設定する
   （ConfigMap checksum によるロールアウト対象になる）。

**制約**（違反は起動時 fail-fast または規約違反）:

- 段が依存してよいのは `Shared.Contracts` のイベント型・自プロジェクトの `Foundation/Ports/`・
  `Foundation/Domain/` のみ。**段どうしの直接参照は禁止**（連携はイベント経由のみ）。
- 宣言と実装の不整合（段の宣言漏れ・`consumer` 型名不一致・`input` と `IConsumer<TIn>` の不一致）は
  **起動失敗**する。`enabled: false` は購読・キューを生成しない。
- **入力イベント型の変更は構成のみでは行えない**。プラグイン改版（コード変更＋宣言更新）として扱う
  （IADR-0028）。

### 2.2 ポートアダプタ（Adapter）— 外部コンポーネント接続

配置: `<Service>/Composable/Adapters/`。

1. 接続点となる抽象が `<Service>/Foundation/Ports/`（または `Shared.Infrastructure` のポート）に
   あることを確認する。**無い場合はポート新設から**（ポートの新設は固定部への追加＝設計判断であり、
   実装 ADR（IADR）を起こす）。
2. ポートを実装するアダプタを `Composable/Adapters/` に置く。外部 SDK（Qdrant・S3・GraphQL 等）への
   依存は**アダプタ内に閉じる**（ポート迂回の直接依存は禁止。[区分表 §3](./composability-classification.md)）。
3. 合成ルート（`Program.cs`）で DI 登録する。構成による実装選択（例: 接続文字列の有無で
   Qdrant/InMemory を切替）を行う場合、その選択ヘルパは `Composable/` 側に置く
   （`Foundation/Extensions/` に置くと依存方向規則違反。IADR-0027）。

既存アダプタ（`QdrantVectorStore`・`S3ObjectStorageClient`・`WikiJsGraphQlClient`・
`PandocConversionService` 等）を実装例として参照すること（一覧は[区分表 §3](./composability-classification.md)）。

### 2.3 LLM・埋め込みプロバイダ — LlmGateway への追加

配置: `LlmGateway` の `Composable/` 配下。

1. `ILlmProvider` / `IEmbeddingProvider`（LlmGateway のポート）を実装する。
2. ルーティング表（構成駆動。IADR-0007/0022/0025）にプロバイダとモデル経路を追加する。
   エグレス統制（どの外部先へ出てよいか）は**固定**であり、統制の変更は新 ADR が必要（FR-11）。
3. 呼び出し側サービスは変更しない（各サービスは `IEmbeddingService` / `IDiagramCoder` 等の
   ポート経由で LlmGateway を利用しており、プロバイダ追加の影響は LlmGateway 内に閉じる）。

### 2.4 データソースコネクタ（Connector）— 予約・未実装

配置予約: `DataSourceService` の `Composable/Connectors/`。計画は
`06_technical/09_datasource-connectors.md`。現状 DataSourceService は登録メタのみで、コネクタ実行
基盤は未実装。**着手時は作業仕様書＋（共通コネクタ抽象を新設するなら）IADR を先に作成する**こと。

### 2.5 新サービスユニットの追加

原典は [src/Services/README.md](../../src/Services/README.md)（レイアウト・依存規則・サブモジュール手順）。要点:

1. `src/Services/<Name>/`（`src/` + `tests/`）を規約レイアウトで作成し、各プロジェクト内を
   `Foundation/` / `Composable/` に二分する（IADR-0027。空フォルダは作らない）。
2. `src/KnowledgePlatform.slnx` に csproj を登録する。ビルド設定・パッケージ版は
   `Directory.Build.props` / `Directory.Packages.props` の中央管理に従う（csproj に `Version=` を書かない）。
3. ユニット外参照は `src/Shared/` のみ。サービス間連携は同期 API（openapi.yaml 管理）または
   イベント（Shared.Contracts）に限る。
4. デプロイ: Helm チャートへ Deployment を追加し、段をホストするなら `pipelineSteps: true` を設定する。
5. 同期 API の**新経路**（どのサービスがどの API に依存するか）は固定部の変更であり、
   [区分表 §1](./composability-classification.md) の更新＋設計判断の IADR 起票を伴う。

### 2.6 フロントエンド feature（画面）

原典は [IADR-0033](../adr/IADR-0033_frontend-spa-foundation.md)。要点:

1. `frontend/src/features/<scXX-name>/` に画面 feature を追加する。基盤は `src/foundation/`
   （config/auth/api/routing/ui）であり、feature から基盤へは import エイリアス
   `@foundation` を用いる。feature どうしの直接依存は避け、共有は foundation へ昇格させる。
2. バックエンド呼び出しは必ず `@foundation/api` の `apiFetch`（`/bff/*` 経由）を使う。
   各サービスの直接呼び出し・接続先のビルド焼き込みは禁止（実行時 config `public/config.js`）。
3. 認証・ロールは foundation の OIDC（Keycloak `spa-web`）とロールベースナビゲーション
   （IADR-0035）に従う。トークン・シークレットをコードに置かない。
4. テスト（Vitest + Testing Library）を実装と同居させ、カバレッジのラチェット
   （`vite.config.ts` の thresholds）を割らないこと（IADR-0034）。

## 3. 全部品共通のルール

1. **依存方向**: `Foundation/` → `Composable/` の参照は禁止。可変実装へのアクセスは必ずポート
   （抽象）経由。束ねるのは `Program.cs`（合成ルート）のみ。
2. **契約の不変**: イベント契約・同期 API 契約は後方互換の追加のみ。破壊的変更は新 ADR。
3. **仕様書**: 着手前に作業仕様書（`docs/specs/`）を作成する。対象があれば機能/通信/データ/テスト
   仕様書を更新し、重要な設計判断（ポート新設・共通抽象の導入等）は IADR に残す。
4. **トレーサビリティ**: 起点 ID（FR/UC/SC/ADR）をブランチ・コミット・コードコメント・PR に残す
   （`.claude/rules/traceability.md`）。
5. **テストと検証**: 受け入れ基準をテスト（xUnit / Vitest）へ写像し、PR 前に `/verify`
   （ビルド・テスト・lint）と `docs/DEFINITION_OF_DONE.md` を満たす。
6. **構成変更の運用**: pipeline.json の変更は CI `pipeline-config` 検証＋GitOps（ArgoCD）適用。
   ロールバックは Git revert（[Helm files README](../../deploy/helm/knowledge-platform/files/README.md)）。

## 4. 受け入れチェックリスト（PR 前の自己点検）

- [ ] 作業仕様書（`docs/specs/`）があり、起点 ID・計画書リンクを備えている
- [ ] 新規コードは `Composable/`（または `features/`）配下にあり、名前空間がフォルダと一致している
- [ ] `Foundation/` 内に `using *.Composable.*` が現れていない
- [ ] 外部 SDK への依存がアダプタ（ポート実装）内に閉じている
- [ ] 段の場合: `IPipelineStep.StepName`・`pipeline.json` 宣言・合成ルート登録の三点が揃い、
      `validate-pipeline-config.js` が通る
- [ ] 契約（イベント型・openapi.yaml）に破壊的変更がない
- [ ] 受け入れ基準がテストに写像され、`/verify` が通る
- [ ] ポート新設・共通抽象の導入など設計判断があれば IADR を起票した
- [ ] 計画書の誤り・不足を見つけた場合は `/plan-feedback` で環流した

## 5. 本書の位置づけと保守

- 本書は**リポジトリ横断の技術文書**（`docs/tech/`）であり、原典（IADR・各 README）の変更時に
  追随して更新する。原典と本書が矛盾したら**原典が正**。
- 計画側（`project-planning`）のプラグイン提供者向け共通仕様の有無・整合は
  [feedback/20260709_composable-implementation-guide-upstream.md](../../feedback/20260709_composable-implementation-guide-upstream.md)
  で環流中。計画側で上流仕様が確定したら本書の §1（接続仕様）を照合すること。
