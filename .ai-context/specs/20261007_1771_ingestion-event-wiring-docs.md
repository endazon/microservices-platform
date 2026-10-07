---
title: 作業仕様書 — IngestionCompleted の MT 発行を IADR-0234 の単位表へ載せ、死んだ契約型 IngestionRequested を消し、腐った文書 2 件を現物へ合わせる（#1771・第 4 回全体監査 B-13 / C-4）
type: spec
status: done
related_ids:
  - FR-01
  - FR-02
  - FR-03
  - FR-14
  - ADR-0027
  - ADR-0035
  - IADR-0234
  - IADR-0314
  - IADR-0122
  - IADR-0430
author: claude
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md (§決定・再試行は Wolverine の耐久メッセージ機能で賄う)
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-01 / FR-02)
issue: "#1771"
---

# 作業仕様書 — IngestionCompleted の所有者・IngestionRequested の撤去・文書の腐り（#1771）

## 目的と射程

第 4 回全体監査（2026-10-07）の結線監査が MSP で見つけた指摘 B-13 / C-4 のうち、**実装側だけで閉じる受け入れ基準 2〜5** を処理する。

- **受け入れ基準 1（`IngestionCompleted` の購読者を結線するか、FR-01 / FR-02 の文書から結線の約束を外すか）は本 PR で決めない。**
  計画側の裁定依頼 planning#741 の項目 6 が既に起票済みである（2026-10-07 に `repos/endazon/project-planning/issues` を
  `IngestionCompleted|B-13` で走査して確認）。FR-01 の図 `ING -->|IngestionCompleted| DOC` と FR-02 の「後続の検索反映へ連鎖する」は
  **そのまま残し**、裁定と矛盾する記述を足さない。よって本 PR は #1771 を閉じない（`Refs #1771`）。

## 受け入れ基準と処置

| # | 基準 | 処置 |
| --- | --- | --- |
| 1 | 結線か文書修正か | **対象外**（planning#741 項目 6 の裁定待ち） |
| 2 | IADR-0234 の単位表に `IngestionCompleted` の MT 発行の所有単位と、E2 完了後の baseline 期待値（実物 5 行） | IADR-0234 へ日付つき追記 `［2026-10-07 追記 / #1771］`。単位 **E4（辺 `IngestionCompleted`）** を新設し、E2 完了後 5 行・E4 完了後 3 行と書く。本文の表は凍結記録のため書き換えない |
| 3 | `IngestionRequested` を契約から削除するか、追随先 issue を明記 | **削除する**（下記の母集合で依存 0 を確認）。契約の後方互換検査（IADR-0122）では型の削除は破壊的なので、承認エントリを置いて `--update` で消費する |
| 4 | 文書の腐り 2 件 | `docs/tech/composability-classification.md` の購読者列へ Graph / Retrieval を足す。`docs/functional/FR-02_ingestion.md` の E2 を Wolverine の再試行・デッドレターへ直す |
| 5 | 任意: `InMemoryVectorStore` の移動 / 既定無効の明記 | 移動は**見送る**（下記）。`GraphExpansion` は FR-03 の機能仕様書へ「既定無効」を明記。`ClusterSummary` は既に明記済み（`docs/observability/knowledge-health-indicators.md`「既定 オフ」） |

IADR を新たに起こす判断は無い（基準 2 は既存 IADR-0234 への追記、基準 3 の承認理由は baseline の `$acceptedBreakingChanges` に残る）。

## 実測（着手時・develop abe9a251）

### baseline（`scripts/backend-library-baseline.json`）の 9 行の内訳

| 行 | 所属単位 |
| --- | --- |
| `ConversionService.csproj` / `ConversionService.Tests.csproj` / `DocumentService.csproj` / `DocumentService.Tests.csproj` | E2（辺 `DocumentNormalized`。ConversionService 発行・DocumentService 購読が MT のまま） |
| `IngestionService.csproj` / `IngestionService.Tests.csproj` | **どの単位にも属さない**（`IngestionCompleted` の MT 発行。`Program.cs` の `AddMassTransit` と `MassTransitIngestionCompletedPublisher`） |
| `Knowledge.Contracts.Tests.csproj` / `Knowledge.IntegrationTests.csproj` / `Platform.Shared.Infrastructure.csproj` | C1 / C2 / C3（どの辺からも到達しない固定 3 行） |

9 = 固定 3 ＋ E2 の 4 ＋ IngestionService の 2。E2 完了後は **5 行**で、IADR-0234 の表が言う「E3b 後 3 行」には到達しない（表は IngestionService の 2 行を E3b で落ちると数えていたが、E3b が動かしたのは `DocumentUpdated` の購読だけで、`IngestionCompleted` の発行は MT のまま残った）。

### `node scripts/check-event-topology.js`

```text
notice: 購読が 0 件のイベントが 2 件ある（違反ではない）: IngestionCompleted（発行元: knowledge/IngestionService） / IngestionRequested（発行元: なし）
[check-event-topology] OK: イベント 6 件 / 購読 8 件が baseline と一致。
  DocumentDeleted: 発行 [knowledge/DocumentService(wolverine)] → 購読 [knowledge/GraphService(wolverine), knowledge/RetrievalService(wolverine), knowledge/WikiService(wolverine)]
  DocumentUpdated: 発行 [knowledge/DocumentService(wolverine)] → 購読 [knowledge/GraphService(wolverine), knowledge/IngestionService(wolverine), knowledge/WikiService(wolverine)]
  IngestionCompleted: 発行 [knowledge/IngestionService(masstransit)] → 購読 [-]
```

購読者の実在（本番 .cs）: `GraphService/Features/GraphDocuments/Sync/GraphDocumentSyncConsumer.cs`（DocumentUpdated）、
`GraphService/Features/GraphDocuments/Delete/DocumentDeletedConsumer.cs`、`RetrievalService/Features/Search/RemoveDeleted/DocumentDeletedConsumer.cs`（DocumentDeleted）。
`deploy/helm/microservices-platform/files/pipeline.json` の steps（`graph-sync` / `graph-delete` / `retrieval-delete`）とも一致。

### FR-02 の E2（本文取得失敗）の経路

`DocumentUpdatedConsumer` は Wolverine のハンドラ（E3b）。`IngestionService/Program.cs` の `UseWolverine` 内で
`UsePlatformMessagingDefaults()`（`WolverineExtensions`: `OnAnyException().RetryWithCooldown(2s, 10s, 30s).Then.MoveToErrorQueue()`）が掛かる。
`IDocumentContentReader` の例外はハンドラから外へ出て、**Wolverine の再試行 3 回 → デッドレター**になる。MassTransit に残るのは
`IngestionCompleted` の発行だけで、この例外の扱いには関与しない。埋め込みの総枠切れ（`EmbeddingBudgetDeadLetterPolicy`）だけは再試行せずデッドレターへ行くが、E2 とは別の例外である。

## 母集合（規則 9・10）

### `IngestionRequested`（誤りの側の文字列で全走査。`src/ai-stock-trading` 除外）

`grep -rn IngestionRequested --exclude-dir=.git --exclude-dir=node_modules .`

| 箇所 | 処置 |
| --- | --- |
| `src/knowledge/backend/Shared/Knowledge.Contracts/Events/IngestionRequested.cs` | 削除 |
| `src/knowledge/backend/Shared/Knowledge.Contracts.Tests/EventMessageUrnTests.cs:20` | `InlineData` 1 行を削除 |
| `scripts/contract-schema-baseline.json` | 承認エントリ（`typeRemoved:...IngestionRequested`）→ `--update` で再生成 |
| `scripts/event-topology-baseline.json` | `--update` で再生成（行が消える） |
| `deploy/helm/microservices-platform/files/pipeline.json` の `events` | 削除（`validate-pipeline-config.js` の V3 は steps/sources が使うイベントだけを要求し、本型は誰も使っていない。ドリフト検出の `BuildEventBindings` は発行 0・購読 0 の行を 1 つ出さなくなるだけ） |
| `docs/tech/composability-classification.md:55` / `:125` | 行を消し、§6 の行を現状へ合わせる |
| 除外: `.ai-context/adr/IADR-0059`、`.ai-context/specs/*`、`.ai-context/superpowers/*` | 凍結記録。当時の記述として残す |

本番の発行 0・購読 0・本番参照 0（契約ファイル自身を除く）。フロントエンド・OpenAPI・orval 生成物への出現 0。

### 「MassTransit のリトライ」（誤りの側の文字列）

| 箇所 | 処置 |
| --- | --- |
| `docs/functional/FR-02_ingestion.md:67` | 直す（基準 4） |
| `IngestionService/Domain/Ports/IEmbeddingService.cs:15`・`IngestionService/Infrastructure/ExternalServices/StorageDocumentContentReader.cs:31` | 同じ経路（DocumentUpdated の Wolverine ハンドラ）のコメント。ファイル領域内なので直す |
| `DocumentService/.../DocumentNormalizedConsumer.cs:20` | **正しい**（DocumentNormalized は E2 未完で MT のまま） |
| `docs/tests/SC-07_conversion-jobs.md:219`・`docs/tests/TEST_STRATEGY.md:94` | 射程外（変換の経路。前者は報告に回す。後者は過去の実例の記述） |
| `.ai-context/adr/*`（IADR-0008 / 0021 / 0023 / 0025） | 凍結記録 |

### 規則 10（この変更で新たに誤りになる自分の記述）

- 契約型の数「6 件」: `docs/tech/composability-classification.md` 以外に現行文書で「イベント契約 6 件」を言う箇所は無い（凍結の作業仕様書のみ）。
- `check-event-topology.js` の notice の「2 件」: 出力は計算値であり固定文字列は無い。

## 見送り: `InMemoryVectorStore` の移動（基準 5 の前半）

使っているのは **2 つのテストアセンブリ**（`RetrievalService.Tests` の 17 ファイルと `Knowledge.IntegrationTests` の 3 ファイル。`McpToolDeclarationHosts.cs` は型引数として名指しする）。
`RetrievalService.Tests` へ移すと `Knowledge.IntegrationTests` がテストプロジェクト（xUnit v3 では実行可能アセンブリ）を参照することになり、
共有用のテスト支援プロジェクトを新設するならソリューション構成・配置規約（IADR-0259 系）の判断を伴う。さらに `QdrantVectorStore.cs` と
`HybridSearchService.cs` のコメントが本型を名指しで等価性の対照に使っている。**「容易なら」の条件を満たさないため本 PR では行わない。**

## 検証

- `dotnet build` / `dotnet test` — `src/knowledge/backend/backend.slnx`（契約の変更は knowledge ユニット内。platform は `Knowledge.Contracts` を参照しないことを確認）
- `dotnet format --verify-no-changes`
- `node scripts/check-event-topology.js` / `node scripts/check-contract-schema.js` / `node scripts/validate-pipeline-config.js`
- ci.yml `static-checks` の node 検査一式、`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- `scripts/backend-library-baseline.json` は増やさない（触らない）
