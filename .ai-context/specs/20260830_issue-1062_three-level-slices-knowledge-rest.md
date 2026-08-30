---
title: 作業仕様書 — knowledge ユニット 8 サービスのスライスを Features/<集約>/<操作>/ の 3 段へ移送する（#1062）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0065
  - IADR-0282
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md (Accepted 2026-08-30)
related_specs:
  - ./20260830_issue-1061_remove-worker-layer.md
issue: "#1062"
---

# 作業仕様書 — スライスの 3 段化（knowledge ユニット・残り 8 サービス）

## 目的と射程

計画 `ADR-0065` 決定 2 が「スライスは `Features/<集約>/<操作>/` の **3 段**」を規範とした。
本 PR は knowledge ユニットのうち **8 サービス・10 集約・27 ファイル**を担当する。

| サービス | 集約 | `Features/` の `.cs` |
| --- | --- | ---: |
| `AiAnalysisService` | Analysis | 1 |
| `ConversionService` | ConversionJobs | 4 |
| `DashboardService` | Dashboard / KnowledgeHealth | 3 |
| `DataSourceService` | DataSources | 5 |
| `FeedbackService` | Feedback | 1 |
| `IngestionService` | Ingestion | 1 |
| `RetrievalService` | McpTools / Search | 9 |
| `WikiService` | Wiki | 3 |

**純移送であり、挙動は 1 つも変えない。** 受け入れ基準は「テスト件数が移送前後で一致すること」で担保する。

### 射程外（本 PR で触らない）

- **`DocumentService` / `GraphService`**（同 issue の別 PR。並行作業のため触らない）。
- **`src/platform/backend/`**（同上）。`Platform.Shared.Infrastructure.Tests` と `Platform.Bff.Tests` に
  本 PR で改名する型の**完全名を検体文字列として持つ箇所が 4 件**あるが、いずれも当該サービスを
  `ProjectReference` しない**任意の見本文字列**であり、載っている検査の合否に関与しない。
  後述「追随しなかったもの」に記録し、platform 側の PR へ申し送る。
- `Tests/` の鏡写し化（`ADR-0065` 決定 3。issue #1063）。本 PR で触るのは
  **移送に伴う `using` と型完全名の文字列だけ**である。
- **`Features/` の外へファイルを出すこと**（別種の変更）。候補は「申し送り」に記す。
- `src/ai-stock-trading`（submodule。別リポジトリ）。

## 計画側の確認（planning は隣接クローンを直接走査）

```console
$ cd /c/10_SourceCode/project-planning
$ sed -n '109,118p' projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md
### 決定 2 — スライスは `Features/<集約>/<操作>/` の 3 段とする
**1 ユースケースのファイルを 1 フォルダへ束ねる。** `Endpoint` ／ `Command`（または `Query`）／
`Handler` ／ そのユースケースが発行するイベントを同居させる。
- **集約（2 段目）はビジネス能力の単位で切る。** エンドポイントごとにフォルダを作らない。
- **3 段目を規範とする**（利用者裁定 2026-08-30）。
```

## 境界の引き方（本 PR が採った規則）

移送の判断は 3 つの規則に閉じる。**規則を先に決め、10 集約すべてに同じ規則を当てた。**

### 規則 A — 3 段目へ降ろすのは「そのユースケース専用のファイル」だけ

集約の複数の操作から使われるファイル（DTO 束・ストア・ポート・共有ヘルパ・
複数エンドポイントを 1 グループに登録する `*Endpoints.cs`）は**集約（2 段目）に残す**。
`ADR-0065` 決定 2 が束ねよと言うのは「1 ユースケースのファイル」であり、共有物ではない。

### 規則 B — ファイルの**中身は割らない**（純移送の境界）

`*Endpoints.cs` の多くは 1 つの `MapGroup` に複数の操作を登録する 1 クラスである。
これを操作ごとのファイルへ割るには**クラスの分割と `Program.cs` の登録の書き換え**が要り、
`MapGroup` の再構成でルート登録の順序・タグ付けが動き得る。**それは移送ではなく実装変更である。**

本 PR は `git mv` で動かせる単位だけを動かした。**エンドポイントの分割は残作業として申し送る**
（issue #1062 の受け入れ基準「全ユースケースが操作フォルダを持つ」は、本 PR だけでは満たされない）。

### 規則 C — 集約が持つユースケースが 1 つなら 3 段目を置かない

3 段目の効用は「同じ集約の中で**どのファイルがどのユースケースのものか**を見分けられること」である。
集約の全ファイルが 1 ユースケースのものなら、操作フォルダは**集約名の言い換え**にしかならず、
束ねる対象も無い。**枠だけを作って「形は揃った」と見せる**のは、`ADR-0065` 決定 4 が
`.gitkeep` 規範を撤回した理由そのものである（枠の存在を適合と読み替える経路を作らない）。

## 集約ごとの判断（10 集約すべて）

### 3 段目を置いた集約（4 サービス・4 集約）

| 移送先 | 動かしたファイル | 理由 |
| --- | --- | --- |
| `ConversionService/Features/ConversionJobs/Normalize/` | `RawDocumentFetchedConsumer.cs`, `NormalizationService.cs` | `RawDocumentFetched` を起点とする正規化変換の 1 ユースケース。`NormalizationService` の呼び出し元は本コンシューマだけ |
| `ConversionService/Features/ConversionJobs/CorrectFigure/` | `FigureCorrectionService.cs` | 人手補正（`IADR-0154`）の 1 ユースケース。`IFigureCorrectionService` の呼び出し元は `/jobs/{id}/figures/...` だけ |
| `DataSourceService/Features/DataSources/Sync/` | `DataSourceSyncService.cs`, `DataSourceSyncHostedService.cs`, `DataSourceSyncOptions.cs` | 原本同期（`UC-04`）の 1 ユースケース。ハンドラ（`DataSourceSyncService`）・定期起動（`HostedService`）・その構成が揃う |
| `RetrievalService/Features/Search/Hybrid/` | `IHybridSearchService.cs`, `HybridSearchService.cs`, `GraphExpandingSearchService.cs`, `GraphExpansionOptions.cs`, `GraphRerank.cs` | ハイブリッド検索（`FR-03`/`UC-01`）の 1 ユースケース。二段検索（`ADR-0035`）は**同じ `IHybridSearchService` の着脱可能な段**であって別ユースケースではない（`GraphRerank` は `HybridSearchService.RrfK` を、`GraphExpandingSearchService` は `HybridSearchService` 実体を参照する） |
| `RetrievalService/Features/Search/RemoveDeleted/` | `DocumentDeletedConsumer.cs` | 削除伝播（`FR-06`/`FR-19`/`ADR-0057`）の 1 ユースケース |
| `WikiService/Features/Wiki/SyncDocument/` | `DocumentSyncConsumer.cs` | `DocumentUpdated` → Wiki.js 同期の 1 ユースケース |
| `WikiService/Features/Wiki/RemoveDeleted/` | `DocumentDeletedConsumer.cs` | `DocumentDeleted` → Wiki.js 撤去の 1 ユースケース |

### 2 段目に残したファイル（規則 A・B）

| ファイル | 残した理由 |
| --- | --- |
| `ConversionService/.../ConversionJobs/ConversionJobEndpoints.cs` | `/jobs` グループに一覧・個別・再変換・図補正の **4 操作**を登録する 1 クラス（規則 B） |
| `DataSourceService/.../DataSources/DataSourceEndpoints.cs` | `/datasources` の CRUD ＋手動同期を登録する 1 クラス（規則 B） |
| `DataSourceService/.../DataSources/SyncSchedule.cs` | 🔴 **同期専用ではない。** 一覧・個別・作成・部分更新の **4 エンドポイントが `NextSyncAt` の算出に読む**（`DataSourceEndpoints.cs:27,33,43,97,127`）。定期同期ワーカーが書き、読み手は CRUD 側にいる**集約横断の singleton** であり、規則 A により 2 段目に残す |
| `RetrievalService/.../Search/SearchEndpoints.cs` | `/search` と `/search/attribute-values` の **2 操作**を登録する 1 クラス（規則 B） |
| `WikiService/.../Wiki/WikiEndpoints.cs` | 一覧・slug 個別・documentId 個別の **3 操作**＋共有の `ProxyOrNotFoundAsync`・応答 DTO を持つ 1 クラス（規則 B） |

### 3 段目を置かなかった集約（規則 B・C。6 集約）

| 集約 | ファイル | 置かなかった理由 |
| --- | --- | --- |
| `AiAnalysisService/Features/Analysis` | `AnalysisEndpoints.cs` 1 件 | **1 ファイルの中に 3 操作**（`/ask`・`/ask/stream`・`/analyze`）と共有ヘルパ（`SseJson`・`ExtractUserAttributes`）・共有リクエスト型（`AskRequest`）がある。動かせる単位が無い（規則 B） |
| `FeedbackService/Features/Feedback` | `FeedbackEndpoints.cs` 1 件 | 同上。**3 操作**（投稿・一覧・統計）と共有ヘルパ（`ToDto`・`SinceUtc`・上限定数）が 1 クラス（規則 B） |
| `DashboardService/Features/Dashboard` | `DashboardEndpoints.cs` 1 件 | 同上。**4 操作**と共有集計ヘルパ（`AggregateUsageAsync` 他）が 1 クラス（規則 B） |
| `DashboardService/Features/KnowledgeHealth` | `KnowledgeHealthDtos.cs`, `KnowledgeHealthEndpoints.cs` | エンドポイントは **2 操作**（閲覧・観測値報告）で 1 クラス、DTO は両方が使う共有物（規則 A・B） |
| `IngestionService/Features/Ingestion` | `DocumentUpdatedConsumer.cs` 1 件 | 集約のユースケースが **1 つだけ**。`Ingestion/Ingest/` は集約名の言い換えであり、束ねる相手もいない（規則 C） |
| `RetrievalService/Features/McpTools` | `McpToolContracts.cs`, `McpToolEndpoints.cs` | 集約のユースケースが **1 つだけ**（ツール定義の自己申告）。2 ファイルは既に同じ 1 ユースケースに閉じており、フォルダを 1 段足しても分ける相手がいない（規則 C） |

## 母集合（自分で引いた。規則 9・10）

追跡下の全ファイルを **2 つの検索語**で走査した（`src/ai-stock-trading` は submodule のため除外）。

```console
$ git grep -l -I -E "Features\.(ConversionJobs|DataSources|Search|Wiki)" -- . ':(exclude)src/ai-stock-trading'
$ git grep -l -I -E "Features/(ConversionJobs|DataSources|Search|Wiki)/" -- . ':(exclude)src/ai-stock-trading'
```

名前空間側で 32 件、パス側で 8 件が当たり、`DocumentService` / `GraphService` 配下と
移送しないファイルを落として次の内訳になった。

| 区分 | 件数 | 扱い |
| --- | --- | ---: |
| 移送するファイルそのもの | 11 | `git mv` ＋ `namespace` 行 |
| 追随して直すもの | 17 | 本 PR で更新（下表） |
| 追随しなかったもの | 6 | 触らない（下表） |

### 追随して直した 17 件

| ファイル | 直す理由 |
| --- | --- |
| `deploy/helm/microservices-platform/files/pipeline.json` | 🔴 **`consumer` は型の完全名で起動時 fail-fast の照合対象**。`convert` / `wiki-sync` / `wiki-delete` / `retrieval-delete` の 4 段が本 PR で改名される |
| `ConversionService/Program.cs` / `DataSourceService/Program.cs` / `RetrievalService/Program.cs` / `WikiService/Program.cs` | 新しい名前空間の `using` |
| `ConversionService/Tests/{NormalizationServiceTests,RawDocumentFetchedConsumerTests,RawDocumentFetchedConsumerJobTests,PipelineStepRegistrationTests,PipelineConfigLoaderTests}.cs`, `Tests/Golden/NormalizationGolden.cs` | `using` と、`consumer` 完全名の検体（`PipelineStepRegistrationTests` は `typeof(...).FullName` と突き合わせる） |
| `DataSourceService/Tests/{DataSourceCredentialExposureTests,DataSourceSyncHostedServiceTests,DataSourceSyncOptionsBindingTests,DataSourceSyncServiceTests,SyncScheduleTests}.cs` | `using`（`SyncSchedule` は残るが同ファイルが `Sync/` 側の型も使う） |
| `RetrievalService/Tests/{DocumentDeletedConsumerTests,GraphExpansionTwoStageSearchTests,HybridSearchServiceTests,PrivateNoteSearchExposureTests,SearchModeNormalizationTaintTests}.cs` | `using` |
| `WikiService/Tests/{DocumentDeleteArchiveSyncTests,DocumentSyncConsumerTests,PipelineRecomposeTests}.cs` | `using` と `Consumer` 完全名の検体 |
| `Knowledge.IntegrationTests/Fixtures/RawDocumentFetchedEdge.cs` | `using`（`typeof(RawDocumentFetchedConsumer).FullName` を宣言へ流す器） |
| `Knowledge.IntegrationTests/Messaging/PipelineDeclarationLoadedTests.cs` | 🔴 **宣言の `consumer` が実装と一致することを直接主張する**テスト |
| `docs/tests/TEST_STRATEGY.md` | `RawDocumentFetchedConsumer.cs:81` のパス |
| `docs/tests/FR-19_private-note-wikijs-exclusion.md` | `DocumentSyncConsumer.cs` のパス |
| `docs/tests/SC-06_datasource-management.md` | `DataSourceSyncHostedService.cs` へのリンク先 |

### 追随しなかった 8 件と理由

移送後に同じ 2 つの検索語で再走査し、残存が下表だけであることを確認した。

| ファイル | 除外理由 |
| --- | --- |
| `.ai-context/adr/IADR-0306_log-sanitization-placement.md` | 凍結記録（実装 ADR）。本文プロズを後から書き換えない |
| `.ai-context/specs/20260829_issue-447_fr12-golden-files.md`, `20260830_issue-1019_codeql-open-alerts.md` | 凍結記録（確定済み作業仕様書）。**CodeQL の指摘位置という「その時点の事実」の記録**であり、書き換えると記録が偽になる |
| `src/platform/backend/Shared/Platform.Shared.Infrastructure.Tests/Foundation/Introspection/ConfigInspectionServiceTests.cs`, `DriftServiceCoverageTests.cs`（2 箇所） | **platform ユニット（並行 PR の領域）**。`ConversionService` を `ProjectReference` しない見本文字列であり、合否に関与しない |
| `src/platform/backend/Bff/Platform.Bff.Tests/ConfigBffEndpointTests.cs` | 同上（`WikiService.Features.Wiki.DocumentSyncConsumer` の見本文字列） |
| `scripts/check-unit-dependencies.js` / `scripts/scripts.repo.test.js` の `Features/Feedback/...` 検体 | **`FeedbackService/Features/Feedback` は本 PR で動かさない**。検体は現行パスのまま正しい |
| `docs/` の型名だけの言及（`HybridSearchService` 等・パスを含まない行） | パスが変わらないので誤りにならない |

## 実装 ADR は作らない

**境界の引き方（規則 A〜C）は本仕様書と PR で記録し、`IADR` は起こさない。**
理由は 2 つある。

1. 規則 A・B は `ADR-0065` 決定 2 の**適用**であって改定ではない。決定 2 自身が
   「集約はビジネス能力の単位で切る／エンドポイントごとにフォルダを作らない」と
   境界の裁量を実装側へ残している。同型の先行 PR（#1061）も `IADR` を起こしていない。
2. 規則 C（3 段目を置かない集約がある）は決定 2 の字面（「実装 38 集約はすべて移送対象」）から
   離れる**唯一の点**であり、記録は要る。ただし **#1062 は 4 本以上の並行 PR に分かれており**、
   各 PR が同じ趣旨の `IADR` を別番号で起こすと採番が衝突する
   （`check-adr-numbering.js` は欠番・重複を許さないが、**並行 PR の先着調停はできない**と
   自ら明記している）。**#1062 全体を 1 本の `IADR` で締めるべき**であり、
   本 PR はその素材（規則 A〜C と 10 集約の適用結果）を提供する側に回る。**申し送りに記す。**

## 受け入れ基準

- [x] 上表の 7 つの操作ディレクトリが存在し、中身が移送前と同一（`git mv` の rename 検出が効く）
- [x] 2 段目に残したファイル・3 段目を置かなかった 6 集約が、上表の理由どおりであること
- [x] `dotnet build src/knowledge/backend/backend.slnx` が成功
- [x] `dotnet test src/knowledge/backend/backend.slnx` が成功し、**件数が移送前と一致**
- [x] `dotnet format src/knowledge/backend/backend.slnx --verify-no-changes` が差分なし
- [x] `node scripts/check-unit-dependencies.js` 違反 0 件
- [x] `check-commit-messages.js` / `check-trace-blocks.js` / `check-doc-links.js` /
      `gen-knowledge-graph.js --check` / `check-adr-numbering.js` 緑
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` 緑
- [x] `pipeline.json` の `consumer` 4 件が新しい完全名になっている

## 検証記録（2026-08-30 実走）

### テスト件数の突合（純移送の担保）

移送**前**（`origin/develop` = `7b57319a`）と**後**で `dotnet test src/knowledge/backend/backend.slnx`
の per-project 件数が**完全に一致**した（12 テストプロジェクト・合計 **1253**／合格 **1210**／スキップ **43**）。

| テストプロジェクト | 前 | 後 |
| --- | --- | --- |
| `AiAnalysisService.Tests` | 95 | 95 |
| `FeedbackService.Tests` | 21 | 21 |
| `IngestionService.Tests` | 28 | 28 |
| `Knowledge.Contracts.Tests` | 27 | 27 |
| `DataSourceService.Tests` | 166 | 166 |
| `WikiService.Tests` | 64 | 64 |
| `ConversionService.Tests` | 81（合格 79 / スキップ 2） | 81（同） |
| `DocumentService.Tests` | 233 | 233 |
| `DashboardService.Tests` | 30 | 30 |
| `GraphService.Tests` | 275 | 275 |
| `RetrievalService.Tests` | 156 | 156 |
| `Knowledge.IntegrationTests` | 77（合格 36 / スキップ 41） | 77（同） |
| **合計** | **1253** | **1253** |

> スキップ 43 件は Testcontainers（Docker API 不在）由来 41 ＋ 変換の統合系 2 で、移送前から同数。

### 実行した検査

| コマンド | 結果 |
| --- | --- |
| `dotnet build src/knowledge/backend/backend.slnx` | 0 エラー（警告 3 は移送前と同一。`MinioBuilder` の CS0618） |
| `dotnet test src/knowledge/backend/backend.slnx` | 緑・件数一致（上表） |
| `dotnet format src/knowledge/backend/backend.slnx --verify-no-changes` | 差分なし |
| `node scripts/check-unit-dependencies.js` | 違反 0 |
| `node scripts/check-adr-numbering.js` | OK |
| `node scripts/check-trace-blocks.js` | OK |
| `node scripts/check-doc-links.js` | OK |
| `node scripts/gen-knowledge-graph.js --check` | OK |
| `node scripts/check-event-topology.js` | OK |
| `node scripts/validate-pipeline-config.js deploy/helm/.../pipeline.json` | OK |
| `node scripts/check-commit-messages.js` | OK |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | OK |

### 検証できなかったこと

- `git rev-parse --is-shallow-repository` = `false`（`git log` を出典に引ける状態であることを確認済み）。
- **`dotnet test src/platform/backend/backend.slnx`**: `Platform.Bff.csproj` が未 populate の
  submodule（`src/ai-stock-trading`）を `ProjectReference` するため本 worktree ではビルド不可。
  **本 PR は platform ユニットのファイルを 1 件も変更していない**ので、影響は無い。
- **`check-deploy-manifests.js`**: `kubeconform` が PATH に無く実行不可（`#1061` と同じ）。
  代替として `validate-pipeline-config.js` で `pipeline.json` を検証した。

## 申し送り

1. **エンドポイントの分割**（規則 B の残り）。`AnalysisEndpoints` / `FeedbackEndpoints` /
   `DashboardEndpoints` / `KnowledgeHealthEndpoints` / `ConversionJobEndpoints` /
   `DataSourceEndpoints` / `SearchEndpoints` / `WikiEndpoints` は、1 クラスに複数操作を持つため
   移送だけでは 3 段目へ降ろせない。**クラス分割は挙動を動かし得る実装変更**であり、
   別 issue が要る。issue #1062 の受け入れ基準はこれを終えるまで満たされない。
2. **`#1062` 全体を締める `IADR` 1 本**（規則 A〜C を実装側の規範として固定するなら）。
3. **platform ユニットの検体文字列 4 件**の追随（上表「追随しなかった 6 件」）。
4. **`Features/` の外へ出す候補**（本 PR の射程外）。`RetrievalService/Features/McpTools/`
   `McpToolContracts.cs` は自己申告の宣言であり `Infrastructure/` 寄り、
   `DataSourceService/.../SyncSchedule.cs` は集約横断の singleton で `Common/` 寄りである。
   いずれも移送とは別種の変更であり、判断ごと別 issue へ送る。
