---
title: 作業仕様書 — 宣言的パイプライン構成（構成定義スキーマ・CI スキーマ検証・MassTransit トポロジ生成）
type: spec
status: in-progress
related_ids:
  - FR-14
  - ADR-0018
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md
  - planning:projects/microservices-platform/06_technical/10_composability-design.md
related_specs:
  - ./20260708_issue-102_composability-fixed-variable-separation.md
  - ../../docs/tech/composability-classification.md
  - ../adr/IADR-0027_composability-folder-structure.md
  - ../adr/IADR-0028_declarative-pipeline-config.md
---

# 作業仕様書: 宣言的パイプライン構成

Issue: #111（親: #102）。先行 PR #110（Foundation/Composable 分離）にスタックする。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-14（構成の組み替え容易性）
- 関連 ADR: ADR-0018（宣言的構成＋プラグイン規約、Accepted）・ADR-0003（MassTransit+RabbitMQ）・ADR-0007（GitOps）
- 計画書リンク: `06_technical/10_composability-design.md` §設計要素 1（パイプライン定義）・2（プラグイン規約）・5（安全弁）

## 目的・背景

パイプラインの段構成・イベント購読を Git 管理の構成定義で宣言し、段の有効/無効・購読の組み替えを
**コード改修なし（構成変更＋GitOps 適用のみ）**で行えるようにする。#110 で分離した `Composable/Steps/`
を構成から束ねる仕組みを作る。

## 対象範囲

- 対象:
  1. **構成定義**: `deploy/helm/knowledge-platform/files/pipeline.json`（正、既定＝現行配線と等価）と
     JSON Schema（`pipeline.schema.json`）
  2. **CI スキーマ検証**: `scripts/validate-pipeline-config.js`（依存パッケージなしの Node スクリプト。
     必須項目・イベント型整合・接続性・循環検出・重複検出）を `ci.yml` の必須ジョブに追加
  3. **共通ステップインタフェース**: `IPipelineStep`（`static abstract string StepName`）を
     Shared.Infrastructure に新設し、全 5 段（convert / catalog / ingest / wiki-sync / wiki-delete）が実装
  4. **トポロジ生成**: `AddKnowledgePlatformPipelineStep<TConsumer>()` が構成を読み、段の登録
     （有効/無効・キュー名）を生成。誤構成は起動時 fail-fast
  5. **GitOps 配送**: Helm ConfigMap で pipeline.json を段ホスティングサービスへマウントし、
     `Pipeline:ConfigPath` から読み込む（構成変更＝ConfigMap 更新→ロールアウト）
  6. **記録**: 設計判断を IADR-0028 に起票
- 対象外:
  - イベント共通エンベロープ（#102 残項目のうち契約変更を伴うもの。別途）
  - 段の入力イベント型の実行時再バインド（IConsumer<T> の型引数変更はプラグイン改版として扱う）
  - 条件分岐・並列段（計画の未決事項どおり初期は直列＋購読の有効/無効に限定）
  - 構成情報 API・ドリフト検出（#112）・構成ビューア（#113）

## 設計

### 1. 構成定義（正: Helm チャート内 `files/pipeline.json`）

```json
{
  "version": 1,
  "events": ["RawDocumentFetched", "DocumentNormalized", "DocumentUpdated",
             "DocumentDeleted", "IngestionRequested", "IngestionCompleted"],
  "sources": [
    { "event": "RawDocumentFetched", "service": "data-source-service" },
    { "event": "DocumentUpdated", "service": "document-service" },
    { "event": "DocumentDeleted", "service": "document-service" }
  ],
  "steps": [
    { "name": "convert", "service": "conversion-service",
      "consumer": "ConversionService.Worker.Composable.Steps.RawDocumentFetchedConsumer",
      "input": "RawDocumentFetched", "outputs": ["DocumentNormalized"], "enabled": true },
    { "name": "catalog", "service": "document-service",
      "consumer": "DocumentService.Api.Composable.Steps.DocumentNormalizedConsumer",
      "input": "DocumentNormalized", "outputs": ["DocumentUpdated"], "enabled": true },
    { "name": "ingest", "service": "ingestion-service",
      "consumer": "IngestionService.Worker.Composable.Steps.DocumentUpdatedConsumer",
      "input": "DocumentUpdated", "outputs": ["IngestionCompleted"], "enabled": true },
    { "name": "wiki-sync", "service": "wiki-service",
      "consumer": "WikiService.Api.Composable.Steps.DocumentSyncConsumer",
      "input": "DocumentUpdated", "outputs": [], "enabled": true },
    { "name": "wiki-delete", "service": "wiki-service",
      "consumer": "WikiService.Api.Composable.Steps.DocumentDeletedConsumer",
      "input": "DocumentDeleted", "outputs": [], "enabled": true }
  ]
}
```

- `steps` = イベント購読段（MassTransit コンシューマ）。`sources` = 同期 API 起点のイベント発行
  （購読を持たないため段ではない）。`queue`（任意）でキュー名を上書き可能。
- イベント名は `Shared.Contracts/Events/` の型名と一致させる。

### 2. 検証規則（CI・`validate-pipeline-config.js`）

| # | 規則 | 誤構成の例 |
| --- | --- | --- |
| V1 | JSON として妥当・`version: 1`・必須項目（name/service/consumer/input/outputs） | 欠落 |
| V2 | 段名の一意性・(service, queue) の一意性 | 重複キュー |
| V3 | `input`/`outputs`/`sources[].event` が `events` に列挙済み | 未知イベント型 |
| V4 | 各段の `input` が「他段の outputs ∪ sources」に含まれる（接続性） | 誰も発行しないイベントの購読 |
| V5 | イベントグラフ（event→step→outputs）に循環がない | A→B→A |
| V6 | `consumer` が .NET 型完全名の形式 | 型名でない文字列 |

スクリプトは `--self-test` で不正構成フィクスチャ（V1〜V6 の各違反）を検査し、CI で本体検証と併走させる。

### 3. 実行時（Shared.Infrastructure `Foundation/Pipeline/`）

- `IPipelineStep`: `static abstract string StepName { get; }`。段（コンシューマ）が実装し、
  コードと構成の対応をコンパイル時に固定する（共通ステップインタフェース。購読=IConsumer<TIn>、
  発行=IPublishEndpoint と合わせ、計画の Subscribe/Process/Publish 概念に対応）。
- `PipelineOptions` / `PipelineStepOptions`: 構成セクション `Pipeline` のバインドモデル。
- `AddKnowledgePlatformPipelineConfig(builder)`: `Pipeline:ConfigPath` が指す JSON
  （`{"Pipeline": {…}}` 形の appsettings オーバレイ。Helm ConfigMap が正から生成）を構成へ追加。
- `AddKnowledgePlatformPipelineStep<TConsumer>(bus, pipeline)`: 登録規則は以下（誤構成対策＝fail-fast）。
  1. `Pipeline.Steps` が空（構成なし）→ 既定で登録（現行等価。ローカル・テスト互換）
  2. 構成があり `StepName` の段が未宣言 → **起動失敗**（InvalidOperationException）
  3. `consumer` と実型の完全名が不一致 → **起動失敗**
  4. 実型の `IConsumer<TIn>` の TIn 型名と `input` が不一致 → **起動失敗**
  5. `enabled: false` → 登録せずログ（購読・キューが生成されない）
  6. `enabled: true` → `AddConsumer<TConsumer>()`（`queue` 指定時はエンドポイント名を上書き）
- 起動時に実効段（登録/スキップ）を構造化ログへ出力する（#112 イントロスペクションの布石）。

### 4. GitOps 配送（Helm）

- `templates/pipeline-config.yaml`: ConfigMap（`files/pipeline.json` を `{"Pipeline": …}` に包んで格納）
- `deployment.yaml`: `services.<name>.pipelineSteps: true` のサービスに ConfigMap をマウントし
  `Pipeline__ConfigPath` を設定。ConfigMap の checksum アノテーションで構成変更時にロールアウト。
- 対象サービス: conversion / document / ingestion / wiki（段をホストする 4 サービス）

### 5. 変更対象ファイル（主要）

| 区分 | ファイル |
| --- | --- |
| 構成定義 | `deploy/helm/knowledge-platform/files/pipeline.{json,schema.json}`・`deploy/helm/knowledge-platform/files/README.md` |
| CI | `scripts/validate-pipeline-config.js`・`.github/workflows/ci.yml`（ジョブ追加） |
| 基盤 | `src/Shared/KnowledgePlatform.Shared.Infrastructure/Foundation/Pipeline/*`（新設） |
| 段 | 5 コンシューマに `IPipelineStep` 実装を追加 |
| 合成 | Conversion / Document / Ingestion / Wiki の `Program.cs`（構成読込＋段登録 API へ置換） |
| Helm | `templates/pipeline-config.yaml`（新設）・`templates/deployment.yaml`・`values.yaml` |

## 受け入れ基準

- [x] 構成定義（pipeline.json）と JSON Schema が Git 管理されている（既定＝現行配線と等価）
- [x] CI で構成のスキーマ検証（V1〜V6）が実行され、不正な構成が fail する（self-test 8 ケースで実証）
- [x] 段の有効/無効・キュー名が構成から MassTransit トポロジへ反映される
      （PipelineStepRegistrationTests: 既定登録・有効・無効・宣言漏れ/型不一致/入力不一致の fail-fast）
- [x] コード改修なしの組み替えをテストで実証（PipelineRecomposeTests: wiki-sync 無効・wiki-delete 有効の
      構成で DocumentUpdated が購読されず DocumentDeleted のみ処理される）
- [x] 既存回帰テストが全て成功する（13 プロジェクト・325 件、失敗 0。2026-07-08 実施。
      helm lint / dotnet format / doc-links も合格）
- [x] 設計判断が IADR-0028 に記録されている

## テスト方針

- 単体（各サービスのテストプロジェクト）: 上記 fail-fast 規則 2〜4 と有効/無効 5〜6 を
  MassTransit TestHarness で検証。既存テストは「構成なし→既定登録」経路の回帰として利用。
- CI 検証スクリプト: `--self-test` に V1〜V6 の違反フィクスチャを内蔵し、CI ジョブで常時実行。
- Helm: `helm template` がエラーなく ConfigMap・マウントを生成することを目視確認（実クラスタ適用は運用手順）。

## 計画書との差異

- 差異: なし。計画の未決事項（スキーマ表現力）は「直列＋購読の有効/無効に限定」で初期化（計画の想定どおり）。
  段の**入力イベント型の実行時再バインドは行わず**、入力変更はプラグイン改版（コード変更）として扱う
  — IConsumer<T> の型安全性を優先（IADR-0028 に理由を記録）。

## 未決事項

- キュー命名規約の恒久化（既定は MassTransit の kebab-case。`queue` での上書きは可能）
- 共通エンベロープ導入時の `events` 宣言の拡張（バージョン付きイベント名）
