---
title: DocumentService の DocumentNormalizedConsumer（MassTransit の受け口）の本文の取得に呼び出しごとの期限を持たせる（#1657・#1640 の射程の外に残った 1 か所）
type: spec
status: done
related_ids: [FR-12, FR-06, FR-01, UC-04, ADR-0027, ADR-0003, ADR-0050, ADR-0006, IADR-0478]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md（再試行・デッドレター）
  - planning:projects/microservices-platform/07_adr/ADR-0050_document-body-fingerprint.md 決定 1（本文指紋。受け口は正規化本文を読んで指紋を計算する）
related_specs:
  - 20260927_issue-1640_consumer-outbound-call-timeouts.md
issue: "#1657"
---

# 仕様書: 正規化文書のカタログ登録の受け口の本文の取得の期限（#1657）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-12**（正規化。`DocumentNormalized` の発行と、その購読によるカタログ登録）、**FR-06**（文書の台帳。カタログ登録は FR-06 のテスト仕様の射程）、FR-01 / UC-04（取り込みの連鎖）
- 関連 ADR: ADR-0027（Wolverine。移行期間中は本受け口だけ MassTransit に残る）、ADR-0003（Superseded by ADR-0027。MassTransit の再試行・デッドレター＝`UsePlatformRetry`）、ADR-0050 決定 1（本文指紋）、ADR-0006（計器）
- 関連 IADR: **IADR-0478**（受け口の外への呼び出しの期限。本 PR は日付つきで追記する。新しい IADR は起こさない）
- 起点 issue: #1657（#1640＝PR #1650・#1652 の再監査で見つかった残り）

## 事実（着手前の実測。origin/develop `478bba79`）

- `src/knowledge/backend/Services/DocumentService/Features/Documents/Catalog/DocumentNormalizedConsumer.cs` は MassTransit の `IConsumer<DocumentNormalized>` である。
  45 行目 `storage.GetTextAsync(ev.MarkdownUri, ct)`（本文指紋のための本文の取得）に呼び出しごとの期限が無い。`IObjectStorageClient` は `Platform.Shared.Infrastructure` の S3 共通クライアント（他の経路と共有）である。
- MassTransit 8.4.1 の受け口の実行期限:
  - 本リポジトリは `UseTimeout`（`TimeoutFilter`。opt-in のミドルウェア）も RabbitMQ の `ConsumerTimeout`（`x-consumer-timeout` のキュー引数）も設定していない（`git grep -n "UseTimeout\|x-consumer-timeout" -- src/platform src/knowledge deploy` は 0 行）。
    したがって受け口の ct（`ConsumeContext.CancellationToken`）はバスの停止だけで立ち、**1 通ごとの実行期限は無い**。
  - 再試行は `cfg.UsePlatformRetry()`（`UseMessageRetry`・間隔 2 / 10 / 30 秒・試行上限 4）。メモリ内の再試行で、同じ配信の中で回り、ack は最後の試行の後である。使い切ると `<queue>_error`（デッドレター）。
- 帰結: 止まったストレージは S3 クライアントの既定の期限まで受け口を占有する。時間切れとしても取り消しとしても記録されない（Wolverine の受け口のように「受け口の ct に負ける」形ではなく、「長い待ち」として現れる）。
- 本受け口の他の外への呼び出し: DB（EF Core。`KnownTagsAsync`・`FindAsync`・`SaveChangesAsync`・`TagResolver.NamesAsync`・`PublishUpdatedIfIndexableOrWithdrawingAsync` の読み取り）と `DocumentUpdated` の発行（Wolverine。`IDocumentUpdatedPublisher`）。
- `ConsumerCallTimeouts.RunAsync` は輸送を問わず ct の連結で期限を与える形で、MassTransit の受け口にもそのまま当てられる（Wolverine の型に依存しない）。
- ブローカの `consumer_timeout` は配備で上書きしていない＝既定 1800 秒（IADR-0478 決定 7 と同じ前提。`Messaging:BrokerConsumerTimeoutSeconds`）。

## 受け入れ基準

| # | 基準 | 写像先 |
| --- | --- | --- |
| AC-1 | 縮めた受け口の ct（30 秒）の下で本文の取得が止まると（呼び出しごとの期限 1 秒）、受け口の ct より前に**時間切れ**（`ConsumerTimeoutException`、段 `catalog`・呼び出し先 `content`）として投げ、カタログへ保存せず発行もしない。受け口の ct は立っていない | `CatalogTimeoutTests.止まった本文の取得は時間切れとして投げ保存も発行もしない`（FR-06 テスト仕様 T-71。develop の最大 T-70 の次） |
| AC-2 | 時間切れは計器 `messaging.consumer.timeout`（`messaging.step=catalog`・`messaging.timeout.target=content`）に 1 回数えられる | 同上（計器を `MeterListener` で聴く） |
| AC-3 | 対照: 呼び出し元の取り消しは時間切れに化けず、取り消し（`OperationCanceledException`・`TimeoutException` ではない）のまま外へ出て、計器に数えない | `CatalogTimeoutTests.呼び出し元の取り消しは取り消しのまま外へ出る`（T-71） |
| AC-4 | 構成 `DocumentCatalog:ContentReadTimeoutSeconds`（既定 20 秒）を読む。1 回の配信の再試行の連鎖（試行上限 4 × 本文の期限 ＋ 試行間の待ち 42 秒）がブローカの `consumer_timeout` 以上なら起動を止める（等号を含む） | `CatalogTimeoutTests.構成が無ければ既定の期限になる`・`再試行の連鎖が…起動を止める`（T-71） |
| AC-5 | 本番の Program.cs が期限を DI に置き、時間切れの部品を登録する | `CatalogTimeoutWiringTests`（T-71） |
| AC-6 | 既存のカタログ登録の試験（MassTransit のハーネス・直接組み立て）が緑のまま | `NormalizedAssetLedgerTests`・`NormalizedExposureWithdrawalTests`・`NormalizedBodyPresenceTests`・`IngestTagFilterTests` |

## 設計

- 本文の取得を `ConsumerCallTimeouts.RunAsync(StepName, CatalogTimeouts.ContentTarget, timeouts.ContentRead, t => storage.GetTextAsync(uri, t), ct)` で包む（IADR-0478 決定 1〜3 をそのまま）。
  時間切れは例外のまま投げ、指紋を null（不明）へ畳まない —— 畳むと指紋の無いまま台帳が更新され、再試行されずに成功で終わる（`GraphDocumentSyncConsumer` と同じ判断）。
- **構成キーは `DocumentCatalog:ContentReadTimeoutSeconds`**。理由:
  - 他サービスの同じ期限は「サービスの節＋`ContentReadTimeoutSeconds`」（`Ingestion:` / `Graph:` / `Wiki:`）である。
  - DocumentService の既存の構成節は機能ごとに `Document` を前置した名前（`DocumentRead:TrustedUserContextClients`・`DocumentTagWrite:TrustedUserContextClients`）であり、`Document:` 単独の節は無い。
    DocumentService は多くの機能を持つので、サービス全体の `Document:` にするとどの受け口の値か読めない。機能（`Features/Documents/Catalog`）の名で `DocumentCatalog` とし、項目名は他サービスと同じ `ContentReadTimeoutSeconds` に揃える。
  - 既定は IngestionService の `Ingestion:ContentReadTimeoutSeconds` と同じ 20 秒（同じストレージから同じ正規化本文を読む。値の理由は IADR-0478「値の理由（取り込み）」の本文の項）。
- **起動時の検査**（`CatalogTimeouts.From`）:
  - MassTransit の受け口には 1 通ごとの実行期限が無いので、Wolverine の受け口の `EnsureFits`（受け口の ct に負けないこと）に当たる制約は無い。
  - 残る制約は「1 回の配信の再試行の連鎖がブローカの `consumer_timeout` に収まること」（IADR-0478 決定 7。MassTransit の `UseMessageRetry` も同じ配信の中で回る）。
    `ConsumerHandlerTimeouts.EnsureRetryChainFits` を、1 試行の上限として**本受け口が期限で抑えている部分**（本文の取得）で呼ぶ。試行上限と待ちの合計は MassTransit 側の単一情報源（`MassTransitExtensions.MaxAttempts`・新設の `TotalRetryCooldown`）から取る。
  - 既定: 4 × 20 ＋ 42 ＝ 122 秒 ＜ 1800 秒。本文の期限を 440 秒以上にすると 4 × 440 ＋ 42 ＝ 1802 秒で止まる（境界の試験に使う）。
  - **DB と発行は式に入れない**（IADR-0478「残るもの」と同じ。DB は Npgsql のコマンド期限、発行は射程外）。既定の構成では 1 試行あたり 419 秒（(1800 − 42) ÷ 4 − 20）が DB と発行の余白として残る。
- 試験の器: `Consume(ConsumeContext)` の本体を `internal Task ConsumeAsync(DocumentNormalized ev, CancellationToken ct)` へ切り出し、`ConsumeContext` を組み立てずに縮めた受け口の ct で呼ぶ（既存の `KnownTagsAsync` と同じ理由）。
  `Consume` は `ConsumeAsync(context.Message, context.CancellationToken)` を呼ぶだけにする。
- 🔴 時間依存の試験は、縮めた受け口の ct（30 秒）と呼び出しごとの期限（1 秒）の比を 30 倍にする（#1650 の監査 B2 と同じ）。

## 母集合（同じ欠陥の走査。規則 9。origin/develop `478bba79`）

- 軸 1（MassTransit の受け口）: `git grep -n "IConsumer<" -- src` をコメント行を除いて読む → 製品の受け口は `DocumentNormalizedConsumer` の 1 件。
  残りは共通部品の型の照合（`PipelineExtensions.cs:109`・`IntrospectionExtensions.cs:80`）。submodule `src/ai-stock-trading` はコード上に `IConsumer<` を持たない（Wolverine へ移行済み。ヒットは `.ai-context/` と `docs/` の記録だけ）。
- 軸 2（MassTransit の購読の登録）: `git grep -n "AddMassTransit(\|AddConsumer<\|AddPlatformPipelineStep<" -- src/platform src/knowledge ':!*Tests*'` →
  `AddMassTransit` は ConversionService・DocumentService・IngestionService の 3 か所。購読（`AddPlatformPipelineStep` / `AddConsumer`）を持つのは DocumentService だけ。ConversionService・IngestionService は発行だけで受け口を持たない。
- 軸 3（本受け口の中の外への呼び出し）: 本文の取得（**本 PR の射程**）・DB・`DocumentUpdated` の発行。
- 除外と理由:
  - DB（EF Core）: IADR-0478「残るもの」と同じく Npgsql のコマンド期限に任せる。
  - `DocumentUpdated` の発行（Wolverine）: IADR-0478「残るもの」の完了イベントの発行と同じ扱い（射程外）。
  - 受け口全体の実行期限（MassTransit の `UseTimeout`）を新たに入れること: issue の射程外。入れると受け口の ct が立つようになり、`EnsureFits` の検査が要る形に変わる。現状は再試行の連鎖の検査で足りる。

## 変異（1 か所ずつ書き換え、`git show HEAD:<path> > <path>` で戻す）

試験は `dotnet test …/DocumentService.Tests.csproj --filter "FullyQualifiedName~CatalogTimeout"`（8 件）で回した。

| # | 変異 | 赤になった試験 |
| --- | --- | --- |
| M1 | **本文の取得の期限を外す**（`calls.RunAsync(…)` を `storage.GetTextAsync(markdownUri, ct)` に） | `止まった本文の取得は時間切れとして投げ保存も発行もしない`（30 秒の受け口の ct で取り消しとして落ちる） |
| M2 | **取り消しを時間切れに化けさせる**（`OperationCanceledException` を捕まえて `ConsumerTimeoutException` を投げる） | `呼び出し元の取り消しは取り消しのまま外へ出る` |
| M3 | 時間切れを本文指紋の不明（null）へ畳む | `止まった本文の取得は時間切れとして投げ保存も発行もしない` |
| M4 | 起動時の再試行の連鎖の検査を外す | `再試行の連鎖がブローカの_consumer_timeout_に収まらなければ起動を止める` の 2 件（440 秒・consumer_timeout 122 秒） |
| M5 | Program.cs から期限の登録を外す | `CatalogTimeoutWiringTests.本番の配線は本文の期限を既定値で張り受け口を組み立てられる` |

いずれも 1 か所ずつ入れ、`git show HEAD:<path> > <path>` で戻した（戻した後の `git status` は差分なし）。

## 検証

PR 本文に記録する（前景実行・コマンドと出力）。
