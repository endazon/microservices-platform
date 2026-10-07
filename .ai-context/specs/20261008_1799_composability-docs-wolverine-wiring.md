---
title: 作業仕様書 — 構成の差し替え（コンポーザビリティ）の文書の MassTransit 前提の記述を Wolverine の実物へ合わせる（#1799）
type: spec
status: done
related_ids: [FR-14, FR-02, FR-15, NFR, ADR-0018, ADR-0027, IADR-0234, IADR-0239]
author: claude
created: 2026-10-08
updated: 2026-10-08
issue: "#1799"
---

# 作業仕様書 — コンポーザビリティの文書を Wolverine の実物へ合わせる（#1799）

> 本仕様書は編集着手前に作成した（着手 2026-10-08）。基点は MSP `origin/develop` `62b059be`
> （#1782 / PR #1798 のマージ後）。計画は project-planning `aa068ac`（隣接クローン・読み取り専用）。
> 文書の是正とコードのコメント 2 箇所の是正であり、挙動（コード・テスト・ワークフロー）は変えない。

## 起点（トレーサビリティ）

- FR-14（コンポーザビリティ。宣言的パイプライン構成）・FR-15（自己申告）。issue の件名は FR-14,FR-02,NFR。
- 計画 ADR-0018（コンポーザブルアーキテクチャ）・ADR-0027（Wolverine 採用）。
- IADR-0234（Wolverine 移行の単位。辺 E2 `DocumentNormalized` は未着手、E4 `IngestionCompleted` は 2026-10-07 追記で新設）・
  IADR-0239（`AddPlatformWolverineStep` の戻り値で queue 宣言を受ける）。いずれも本文は凍結記録として書き換えない。
- #1782 / PR #1798 の作業仕様書 `20261008_1782_sc07-test-spec-and-readme-rot.md` §母集合 4 の最終行で
  「射程外・別 issue へ切り出す」とした 9 箇所が本 issue の起点である。

## 受け入れ基準

- AC1: issue の表の各行（区分表 :115・ガイド :54 / :76・FR-14 機能 :42 / :52 / :77・FR-14 試験 :30・構成図 :75・技術要件 :58）を
  Wolverine のステップ登録・購読の実物に合わせる。MassTransit が残る箇所はそのとおり書く。
- AC2: ガイドの「新しい段を足す手順」を実在する API・設定点で書く（各手順の根拠 path:line は下表）。
- AC3: `RawDocumentFetchedConsumer.cs:91` のコメントを実物に合わせる。
- AC4: 規則 9・10 の母集合を下に引き、除外は理由つきで残す。凍結記録（`.ai-context/specs/`・superpowers・IADR 本文）は書き換えない。
- AC5: `docs/` の規約（trace ブロック・`updated:`）を守り、文書系の検査をすべて緑にする。

## 🔴 issue 本文の「現行の実物」の訂正（規則 10: 他人の記述を検証せず転記しない）

issue は「MassTransit は Conversion・Document・Ingestion の**発行**（`AddMassTransit`。移行単位 E4）にだけ残る」と書くが、実物は違う。

| サービス | MassTransit に残るもの | 出典 | 移行単位 |
| --- | --- | --- | --- |
| ConversionService | `DocumentNormalized` の**発行**（`AddMassTransit` に consumer なし・`UsePlatformRetry`） | `ConversionService/Program.cs:154-161` / `:130`（`MassTransitDocumentNormalizedPublisher`） | E2 |
| DocumentService | `DocumentNormalized` の**購読**（`x.AddPlatformPipelineStep<DocumentNormalizedConsumer>(pipeline)`・`IConsumer<DocumentNormalized>`） | `DocumentService/Program.cs:299-313`、`DocumentNormalizedConsumer.cs:30` | E2 |
| IngestionService | `IngestionCompleted` の**発行**（購読者は 0） | `IngestionService/Program.cs:128-137`・`:23` | E4 |

`scripts/event-topology-baseline.json:22-30`（`DocumentNormalized` の発行・購読とも `masstransit`）と `:52-55`（`IngestionCompleted` の発行が `masstransit`）が同じ事実を持つ。
したがって文書には「`DocumentNormalized` の発行・購読と `IngestionCompleted` の発行」と書く（Document の購読を落とさない）。

## 実物の確認（出典は path:line。基点 `62b059be`）

段（パイプラインステップ）の登録経路と、メッセージ基盤の配線の実体。`src/` 前置を省く箇所は
`src/knowledge/backend/Services/` 配下、`Foundation/` は `src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/`。

| 事実 | 出典 |
| --- | --- |
| `AddPlatformMassTransit` はどの `.cs` にも無い | `git grep -n AddPlatformMassTransit -- '*.cs'` が空（ヒットは `docs/` 2 件と #1782 の作業仕様書 1 件だけ） |
| Wolverine の段の型: `IPipelineStep<TIn>`（`IPipelineStep` を継承。入力型の申告） | `Foundation/Pipeline/IPipelineStep.cs:22-23` |
| 段の実例: `RawDocumentFetchedConsumer : IPipelineStep<RawDocumentFetched>`・`StepName => "convert"`・`Handle(RawDocumentFetched, Envelope, CancellationToken)` | `ConversionService/Features/ConversionJobs/Normalize/RawDocumentFetchedConsumer.cs:21` / `:24` / `:30` |
| ハンドラとして認められるメソッド名（Handle / Consume ほか 11 個） | `Foundation/Pipeline/WolverinePipelineExtensions.cs:31-36` |
| 手順 1: 宣言の読み込み `builder.AddPlatformPipelineConfig()` ＋ `builder.Configuration.GetPlatformPipeline()` | `ConversionService/Program.cs:134-135`（定義 `Foundation/Pipeline/PipelineExtensions.cs:22` / `:61`） |
| 手順 2: 段の登録 `opts.AddPlatformWolverineStep<T>(pipeline)`（`UseWolverine` 内。戻り値は段宣言） | `ConversionService/Program.cs:164` / `:170`（定義 `WolverinePipelineExtensions.cs:47-49`） |
| 手順 2 の規則: 宣言なし→既定登録 / 未宣言・consumer 不一致・input 空→起動失敗 / `IPipelineStep<TIn>` 不在→起動失敗 / 入力型を受けるハンドラメソッド不在→起動失敗 / input 不一致→起動失敗 / `enabled:false`→規約探索からも除外して登録しない / 登録 | `WolverinePipelineExtensions.cs:82-90`（規則 1）/ `:92-115`（規則 2〜4）/ `:117-126`（規則 5）/ `:128-136`（規則 6）/ `:138-143`（規則 7）/ `:145-166`（規則 8）/ `:169-174`（規則 9） |
| 手順 3: queue 宣言の受け取り `step?.Queue ?? nameof(TIn)` | `ConversionService/Program.cs:172` |
| 手順 3: fan-out exchange への束ね `opts.UseRabbitMq(...).AutoProvision().BindPlatformQueue<TIn>(service, queue)` | `ConversionService/Program.cs:176-177`（定義 `Foundation/Extensions/WolverineExtensions.cs:122-129`） |
| 手順 3: 受信 `opts.ListenToPlatformQueue(service, queue)`（キュー名は `<service>.<queue>`） | `ConversionService/Program.cs:186`（定義 `WolverineExtensions.cs:68-86`） |
| 手順 3（発行側）: Wolverine で発行する出力イベントの経路 `opts.RoutePlatformEvent<TOut>()` | `ConversionService/Program.cs:183`（定義 `WolverineExtensions.cs:107-112`）、`DocumentService/Program.cs:336-337` |
| 手順 4・5 ＋ 再試行 2s/10s/30s → `MoveToErrorQueue`: `opts.UsePlatformMessagingDefaults()` | `ConversionService/Program.cs:189`（定義 `WolverineExtensions.cs:39-44` / `:136-165`） |
| readiness: `AddPlatformHealthChecks().AddPlatformWolverineBroker()` | `ConversionService/Program.cs:62-63`（定義 `WolverineExtensions.cs:189-197`） |
| 自己申告: `AddPlatformIntrospection(service, pipeline, i => i.AddWolverineStep<T>())` | `ConversionService/Program.cs:139-140`（定義 `Foundation/Introspection/IntrospectionExtensions.cs:21` / `:102`） |
| 宣言: `pipeline.json` の `steps[]`（name / service / consumer / input / outputs / enabled） | `deploy/helm/microservices-platform/files/pipeline.json:17-23` |
| Helm の `pipelineSteps: true` | `deploy/helm/microservices-platform/values.yaml:234` ほか |
| 他の Wolverine 段の登録（同じ形） | `IngestionService/Program.cs:145`・`GraphService/Program.cs:353-354`・`WikiService/Program.cs:124-125`・`RetrievalService/Program.cs:252` |
| 残る MassTransit の段登録経路は `AddPlatformPipelineStep<TConsumer>`（`IBusRegistrationConfigurator` の拡張。`IConsumer` ＋ `IPipelineStep` 制約）で、呼び出しは 1 箇所 | 定義 `Foundation/Pipeline/PipelineExtensions.cs:75-77`、呼び出し `DocumentService/Program.cs:302` のみ |
| MassTransit の再試行は各サービスの `AddMassTransit` 内で `cfg.UsePlatformRetry()`（`Foundation/Extensions/MassTransitExtensions.cs:28`） | `ConversionService/Program.cs:159`・`DocumentService/Program.cs:309`・`IngestionService/Program.cs:134` |
| 新規の MassTransit 参照は CI が止める（ratchet） | `scripts/check-backend-libraries.js`・`scripts/backend-library-baseline.json` |

## 是正（旧 → 新）

| 箇所 | 旧 | 新 |
| --- | --- | --- |
| `docs/tech/composability-classification.md:115` | MassTransit + RabbitMQ 配線（`AddPlatformMassTransit`） | RabbitMQ 配線。現行は Wolverine の共通ヘルパ群（`UsePlatformMessagingDefaults` ほか）。2 辺だけ `AddMassTransit` ＋ `UsePlatformRetry` が残る |
| `docs/tech/composable-component-guide.md:54-55` | MassTransit 配線（`AddPlatformMassTransit`）／`AddPlatformPipelineStep<T>` | Wolverine の共通ヘルパ群／`AddPlatformWolverineStep<T>`（残る MassTransit 段の経路は `AddPlatformPipelineStep<T>` と併記） |
| 同 §2.1 :76-84・:92-93 | `IConsumer<TIn>` 実装 → `AddPlatformPipelineStep<T>` 1 行 | `IPipelineStep<TIn>` ＋ `Handle(TIn)` → 宣言読み込み → `AddPlatformWolverineStep` → `BindPlatformQueue` / `ListenToPlatformQueue` / `RoutePlatformEvent` / `UsePlatformMessagingDefaults` → 自己申告 → `pipeline.json` → Helm |
| `docs/functional/FR-14_composability.md:42` / `:51` / `:52` / `:60` / `:77` / `:86` | MassTransit コンシューマ・`AddPlatformPipelineStep`・MassTransit トポロジ・`IConsumer<TIn>`・`PipelineExtensions` | Wolverine ハンドラ（`IPipelineStep<TIn>`）・`AddPlatformWolverineStep`・RabbitMQ の購読（キュー・exchange 束ね）・入力型の導出元・`WolverinePipelineExtensions`（残る MassTransit 段は併記） |
| `docs/tests/FR-14_composability.md:28` / `:30` | 対象 `PipelineExtensions`／対象外 MassTransit 本体 | 対象に `WolverinePipelineExtensions` を加える／対象外 Wolverine・MassTransit 本体 |
| `docs/tech/system-architecture.md:75` | `RabbitMQ<br/>MassTransit` | `RabbitMQ<br/>Wolverine（一部の辺は MassTransit）` |
| `docs/tech/tech-requirements.md:58` | 現行実装は MassTransit で、置き換えは再実装 issue で行う | 現行は Wolverine。MassTransit は移行の済んでいない 2 辺だけに残る |
| `deploy/helm/microservices-platform/files/README.md:37-42`（母集合で追加） | `IConsumer<TIn>` ＋ `AddPlatformPipelineStep<T>` | ガイド §2.1 と同じ Wolverine の手順 |
| `RawDocumentFetchedConsumer.cs:91-92`（コメント） | MassTransit の再試行→デッドレター | Wolverine の再試行（`UsePlatformMessagingDefaults`。2/10/30 秒）→ `MoveToErrorQueue` |
| `Foundation/Pipeline/IPipelineStep.cs:4-6`（コメント。母集合で追加） | 段（MassTransit コンシューマ）。購読は `IConsumer<TIn>`、発行は `IPublishEndpoint` | 段は Wolverine ハンドラ（`IPipelineStep<TIn>`）。移行中の MassTransit 段は `IConsumer<TIn>` |

## 母集合（規則 9 / 10。誤りの側の文字列で全走査）

走査範囲は追跡下の生きた文書（`docs/`・各 `README.md`・`deploy/` の注記・`templates/`・`scripts/README.md`）と `src/` の注記。
`.ai-context/`（凍結記録）と `CHANGELOG.md`（生成物）は対象外として**ヒットだけ記録**する。#1782 の作業仕様書 §母集合 4 の判断を基点に、
本 issue の射程（段の登録・メッセージ基盤の配線）で引き直した。

### 1. `AddPlatformMassTransit`（`git grep -n`）

| ヒット | 判断 |
| --- | --- |
| `docs/tech/composability-classification.md:115`・`docs/tech/composable-component-guide.md:54` | **是正** |
| `.ai-context/specs/20261008_1782_sc07-test-spec-and-readme-rot.md:99` | 除外（他 issue の凍結記録。本 issue への切り出しを記録した行） |

### 2. `AddPlatformPipelineStep`（生きた文書）

| ヒット | 判断 |
| --- | --- |
| `docs/tech/composable-component-guide.md:55` / `:78`・`docs/functional/FR-14_composability.md:51`・`deploy/helm/microservices-platform/files/README.md:39` | **是正**（段の登録経路として現行扱いしている） |
| `docs/tech/tech-requirements.md:458` / `:523` / `:533` | 除外（移行の安全弁・Phase 0 の日付つき経過記述。経路自体は `PipelineExtensions.cs:75` に実在し、`DocumentService/Program.cs:302` が使う） |
| `src/` の `PipelineExtensions.cs`・`DocumentService/Program.cs:302` ほか試験 | 除外（実在する API とその唯一の呼び出し） |

### 3. `IConsumer<`（生きた文書）

| ヒット | 判断 |
| --- | --- |
| `docs/tech/composable-component-guide.md:76` / `:92`・`docs/functional/FR-14_composability.md:60`・`deploy/helm/microservices-platform/files/README.md:37` / `:42` | **是正**（新しい段の実装形として書いている） |
| `docs/tech/tech-requirements.md:460` / `:482-499` | 除外（安全弁の検証記録。MassTransit 経路の型制約の説明として真） |

### 4. `MassTransit`（生きた文書。#1782 §母集合 4 の表を再確認）

| ヒット | 判断 |
| --- | --- |
| `docs/tech/composability-classification.md:115`・`docs/tech/composable-component-guide.md:54` / `:76`・`docs/functional/FR-14_composability.md:42` / `:52` / `:77`・`docs/tests/FR-14_composability.md:30`・`docs/tech/system-architecture.md:75`・`docs/tech/tech-requirements.md:58` | **是正**（本 issue の表） |
| `README.md:22` | 除外（#1782 で是正済み。「一部の辺〔発行・購読〕はまだ MassTransit」は上の訂正表と一致する） |
| `CLAUDE.md`・`docs/operations/operations.md:1533`・`docs/tech/tech-requirements.md:240`・`scripts/README.md:32`・`templates/unit-template/README.md:110` | 除外（不採用ライブラリとしての言及。真） |
| `docs/data/data-source.md:25`・`docs/functional/FR-01_data-source-catalog.md:25`・`docs/functional/FR-02_ingestion.md:22`・`docs/tests/FR-01_data-source-catalog.md:23`・`docs/tech/system-architecture.md:28`・`deploy/local/infra/rabbitmq.yaml:1` | 除外（Superseded な計画 ADR の引用。後継併記の書式どおり） |
| `docs/functional/FR-02_ingestion.md:67` | 除外（「MassTransit に残っているのは `IngestionCompleted` の発行だけ」は IngestionService について真） |
| `docs/tests/FR-12_document-normalization.md:65` | 除外（`MassTransitDocumentNormalizedPublisherTests` は実在する） |
| `docs/operations/operations.md:815` / `:819` | 除外（直下に 2026-10-06 の日付つき訂正があり、Wolverine の DLQ を書いている。運用手順の話題で本 issue の射程外） |
| `docs/operations/operations.md:1386` | 除外（MassTransit の辺が残るため「MassTransit は再接続」は偽ではない） |
| `docs/tech/tech-requirements.md:290` / `:314-343` / `:437` / `:475` / `:484` | 除外（移行残件の計数・経過記述。日付つきの実測記録） |
| `docs/tests/TEST_STRATEGY.md:93-100`・`scripts/README.md:32` の `RawDocumentFetchedConsumer.cs:81` の実例 | 除外（コミット `bc7bc8e` 時点の実例として書かれた経過記述） |
| `docs/how-to/session-handoff.md:431`・`docs/how-to/run-integration-tests-without-docker.md:93`・`docs/tech/20260707_wikijs-poc-record.md:31` | 除外（当時の事象・PoC の記録・実測した例外メッセージの引用） |
| `docs/screens/SC-11_configuration-viewer.md:115` | 除外（モックの表示ラベルの引用） |
| `deploy/docker-compose.yml:899` / `:923` | 除外（ai-stock-trading ユニットのサービス。本リポの knowledge / platform の配線ではない） |

### 5. `src/` の注記（MassTransit を現行の段・再試行として書くもの）

| ヒット | 判断 |
| --- | --- |
| `ConversionService/Features/ConversionJobs/Normalize/RawDocumentFetchedConsumer.cs:91-92` | **是正**（issue の AC。ハンドラは Wolverine で、再試行は `UsePlatformMessagingDefaults`） |
| `Foundation/Pipeline/IPipelineStep.cs:4-6` | **是正**（「段（MassTransit コンシューマ）」「購読は `IConsumer<TIn>`」を段一般として書く。同ファイル :21-22 は新規段が `IPipelineStep<TIn>` を実装すると書いており、冒頭が食い違う） |
| `RawDocumentFetchedConsumer.cs:15-16` / `:26-29` | 除外（発行が MassTransit のまま・`ConsumeContext.GetRetryAttempt()` 相当の説明。真） |
| `Foundation/Pipeline/PipelineOptions.cs:51`（「省略時は MassTransit 既定の命名」） | 除外（MassTransit 段〔`DocumentNormalizedConsumer`〕には真。Wolverine 段の既定はイベント型名で、呼び出し側 `step?.Queue ?? nameof(TIn)` が示す。挙動の記述を広げるのは本 issue の射程外） |
| `Foundation/Pipeline/PipelineExtensions.cs:10`・`WolverinePipelineExtensions.cs`・`WolverineExtensions.cs`・`IntrospectionExtensions.cs` の MassTransit 言及 | 除外（併存する 2 経路の説明として真） |
| 各サービス `Program.cs`・`*Publisher.cs`・`DocumentNormalizedConsumer.cs`・`DocumentUpdatedConsumer.cs` の MassTransit 言及 | 除外（残る辺の実配線・その説明。真） |
| 試験コードの MassTransit 言及（`AddMassTransitTestHarness` ほか） | 除外（残る辺の試験。真） |

### 6. 規則 10（この変更で新たに誤りになる自分の記述）

- 新記述の「MassTransit が残るのは `DocumentNormalized` の発行・購読と `IngestionCompleted` の発行」は E2・E4 の移行で古くなる。
  **移行完了の PR で消す対象**である（`README.md:22` と同じ扱い）。書いた箇所: 区分表 §5・ガイド §1.2・FR-14 機能・構成図・技術要件・`IPipelineStep.cs` のコメント。
- ガイド §2.1 の手順に書いた API 名はすべて上表で実在を確かめた（`git grep -n` で定義と呼び出しの両方）。
- `enabled: false` の記述: Wolverine 経路は**ハンドラを登録しない**（規約探索からも除外）。一方、各サービスの `Program.cs` は
  `ListenToPlatformQueue` / `BindPlatformQueue` を `enabled` に関係なく呼ぶ（例 `ConversionService/Program.cs:172-186`）。
  したがって「`enabled: false` はキューを生成しない」を Wolverine 段について新たに書くと誤りになり得る。**本 PR は「購読（ハンドラ）を登録しない」と書き、
  キューの宣言の有無は主張しない**。旧記述（「購読・キューを生成しない」）が要求として正しいか・実装が要求を満たすかは本 issue の射程外であり、
  所見として報告する（コードは変えない）。
- 各 `docs/` 文書の `updated:` を 2026-10-08 へ進め、trace ブロックの `issues` に `#1799`、`specs` に本仕様書を足した（表示テキストに ID を増やしていない）。

## 検証

`check-trace-blocks` / `gen-knowledge-graph --check` / `check-doc-links` / `check-cross-repo-refs` / `check-plan-id-qualification` /
`check-reading-budget` / `check-doc-type-vocabulary` / `check-doc-status-vocabulary` / `check-doc-updated` / `check-commit-messages`。結果は PR 本文に記す。
