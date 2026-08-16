---
title: Serilog を OTel Logging SDK へ寄せて ratchet を消化する（#455 の断片）
type: spec
status: done
related_ids:
  - ADR-0006
  - ADR-0030
  - IADR-0216
  - NFR
author: implementation-agent
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md"
  - "../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md"
related_specs:
  - "../adr/IADR-0216_otel-logging-sdk-replaces-serilog.md"
  - "../tech/tech-requirements.md"
  - "../security/security.md"
---

# 仕様書: 不採用ライブラリ `Serilog` の撤去と OTel Logging SDK への一本化

> 本仕様書は実装着手前に作成した。計画書（`project-planning` の `projects/microservices-platform/`）を
> 一次情報とし、本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（機能追加ではない）
- 非機能要件（NFR）: **無採番**。理由は後述「起点 ID の選び方」を参照
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR:
  - 計画 `ADR-0006`（可観測性スタック。OTel 統一計装・相関 ID による三者突合）
  - 計画 `ADR-0030`（バックエンドアプリケーション層のライブラリ標準。**ロギング = 標準 `ILogger` + OpenTelemetry Logs、Serilog / Seq は不採用**）
- 実装 ADR: [`IADR-0216`](../adr/IADR-0216_otel-logging-sdk-replaces-serilog.md)
- 計画書リンク:
  - [`ADR-0006`](../../planning/projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md)
  - [`ADR-0030`](../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md)
- 関連する既存仕様書:
  - [`20260803_issue-455_backend-application-standard.md`](20260803_issue-455_backend-application-standard.md)（ratchet の導入）
  - [`20260803_issue-471_backend-libraries-detection-gaps.md`](20260803_issue-471_backend-libraries-detection-gaps.md)（検出の穴埋め）
  - [`../tech/tech-requirements.md`](../tech/tech-requirements.md)（ライブラリ標準の実装側要点）
  - [`../security/security.md`](../security/security.md)（監査ログの記録経路）

### 起点 ID の選び方

`.claude/rules/traceability.md`「起点 ID の種別」の `NFR` の項に照らし、本作業は **無採番 `NFR`** とする。

- 計画側は非機能要件表に ID 列を持つ（`NFR-01`〜`NFR-27`）ので、ケース 1（ID 列が無い）ではない。
- 本作業は**ライブラリ標準への準拠と検査器 ratchet の消化**であり、稼働する製品の品質要件
  （性能・可用性・セキュリティ等）のどの番号にも対応しない。ケース 2（ID 列はあるが当たる番号が無い）に当たる。
- したがって**無理に近い番号を付けず、計画へ環流もしない**（`traceability.repo.md`：メタ作業は代表例）。
- 一方、実装判断そのものは `IADR-0216` に残す。コミット / PR のスコープは `IADR-0216` を用いる。

## 目的・背景

計画 `ADR-0030` は**ロギングを「標準 `ILogger` + OpenTelemetry Logs」に決め、Serilog（および Seq）を不採用**とした。
しかし実装は Serilog（`Serilog.AspNetCore` + `Serilog.Sinks.OpenTelemetry`）でログを OTLP へ流しており、
`scripts/check-backend-libraries.js` の ratchet に **13 件**の既知残件として計上されている。

本作業はこの 13 件を消化する。すなわち **ログの出口を Serilog から OTel Logging SDK
（`builder.Logging.AddOpenTelemetry()`）へ移し**、`Serilog*` のパッケージ参照・CPM 版定義・baseline エントリを
すべて取り除く。

## 対象範囲

- 対象:
  - `src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Extensions/ObservabilityExtensions.cs`
    の `ConfigurePlatformSerilog` → OTel Logging の設定関数へ置換
  - 各サービスの `Program.cs`（12 本）の初期化 2 行
  - `Foundation/Middleware/CorrelationIdMiddleware.cs`（Serilog `LogContext` → `ILogger.BeginScope`）
  - `Foundation/Audit/AuditLogger.cs`（実体は既に `ILogger`。コメントの経路記述のみ）
  - `.csproj` 3 本の `PackageReference`、`src/Directory.Packages.props` の `PackageVersion` 2 件
  - `scripts/backend-library-baseline.json` の `Serilog` 13 エントリ
  - 変更で事実と食い違う live 文書（後述の母集合 表 A の「要修正」行）
- 対象外:
  - `MassTransit` / `FluentAssertions` の残件（別 issue。本作業では 1 件も動かさない）
  - トレース・メトリクスの計装（`AddPlatformObservability`）。**ログの出口だけを差し替える**
  - `src/ai-stock-trading`（別プロジェクトの submodule）・`planning/`
  - ログの**内容**（何を出すか）の変更。出口の実装だけを替える

## 母集合（自分で引いた結果）

`.claude/rules/traceability.repo.md`「是正・追随の母集合の取り方」に従い、**着手前に自分で引いた**。
issue 本文や親エージェントの提示した「反映先」は母集合として採らず、下記の走査で引き直した。
**走査基準コミット: `c01bc093`**（本仕様書を書く前の作業ツリー。規則 8 に従い、本仕様書自身は母集合に含まない）。

引き方は規則 1〜5 に従った —— **誤りの側（`Serilog`）の語で引き**、大小文字を無視し、
**拡張子で絞らず**（`--include` を使わない）、**行フィルタを継がず**（`git grep -l` でパスから引く）、
**除外はパスだけ**（`:!src/ai-stock-trading` / `:!planning`）とし、**軸を 6 本引いた**。

| 軸 | 検索語 | 件数 |
| --- | --- | --- |
| 1 | `serilog`（大小無視・全追跡ファイル） | **33 ファイル** |
| 2 | `MinimumLevel\|WriteTo\|Enrich\|UseSerilog\|Log\.Logger\|ForContext\|LoggerConfiguration\|Sinks\|SelfLog\|Serilog__\|SERILOG_` | 30 行（軸 1 の外に **0 ファイル**。`AnthropicContentBlockSanitizer.cs` の `WriteTo` は `Utf8JsonWriter` で無関係） |
| 3 | `ConfigurePlatformSerilog` | 13 行（軸 1 の内側） |
| 4 | `using Serilog` | 16 ファイル（うち `.cs` は **13**） |
| 5 | 変更後に誤りになる導出値（`42 件` / `29 プロジェクト` / `15 / 14 / 3` / `59 / 129 / 15` / `baseline が空`） | 20 行 |
| 6 | 未追跡ファイル・submodule（`src/ai-stock-trading` / `planning`） | **0 件**（`git status --untracked-files=all` も空） |

軸 4 は**親エージェントの前提（`.cs` 15 ファイル）と食い違う**。実測すると `using Serilog` を持つ `.cs` は
**13** であり、残り 2 ファイルは別の形で Serilog に触れている（後述の差異 1）。**数を転記せず引き直したことで見つかった**。

### 表 A: 全 33 ファイルの処置（除外したものは理由を書く）

| # | パス | 処置 | 理由 |
| --- | --- | --- | --- |
| 1 | `src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Extensions/ObservabilityExtensions.cs` | **修正** | `ConfigurePlatformSerilog` を `AddPlatformLogging` へ置換 |
| 2 | `src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Middleware/CorrelationIdMiddleware.cs` | **修正** | `Serilog.Context.LogContext.PushProperty` → `ILogger.BeginScope` |
| 3 | `src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Audit/AuditLogger.cs` | **修正（コメントのみ）** | 実体は既に `ILogger`。コメントの「Serilog→OTLP」が事実でなくなる |
| 4 | `src/platform/backend/Shared/Platform.Shared.Infrastructure/Platform.Shared.Infrastructure.csproj` | **修正** | `Serilog.AspNetCore` / `Serilog.Sinks.OpenTelemetry` の `PackageReference` を削除 |
| 5 | `src/knowledge/backend/Services/ConversionService/src/ConversionService.Worker/ConversionService.Worker.csproj` | **修正** | `Serilog.AspNetCore` の `PackageReference` を削除 |
| 6 | `src/knowledge/backend/Services/IngestionService/src/IngestionService.Worker/IngestionService.Worker.csproj` | **修正** | 同上 |
| 7 | `src/Directory.Packages.props` | **修正** | `PackageVersion` 2 件の削除 ＋ L64-66 の「移行完了時に削除する」対象リストから `Serilog` を外す |
| 8-19 | `Program.cs` × 12（platform 3 / knowledge 9。内訳は下表 B） | **修正** | 初期化 2 行の差し替えと `using Serilog;` の削除 |
| 20 | `scripts/backend-library-baseline.json` | **修正** | `Serilog` 13 エントリの減算 |
| 21 | `scripts/check-backend-libraries.js` | **修正（docstring のみ）** | 「現行実装は … Serilog を広範に使用中」が事実でなくなる。残件数 42 → 29 |
| 22 | `docs/tech/tech-requirements.md` | **修正** | 実測値 `.csproj` 3 / `.cs` 15 と「Serilog を広範に使用中」が事実でなくなる（規則 10 の導出値） |
| 23 | `docs/security/security.md` | **修正** | 監査ログの経路「（Serilog→OTLP）」が事実でなくなる |
| 24 | `scripts/scripts.repo.test.js` | **除外** | `Serilog` は**合成フィクスチャ**（`matchesBanned('Serilog.AspNetCore','Serilog')` 等）であり、リポの実状態を指していない。`Serilog` は `BANNED` に残り続けるので**依然として正しい試験**である。`42 件` の言及も `#471` 時点の測定として明示的に帰属されている |
| 25 | `scripts/README.md` | **除外** | 不採用ライブラリの**例示列挙**。`Serilog` は撤去後も不採用のままなので記述は正しい |
| 26 | `.github/workflows/ci.yml` | **除外** | 同上（不採用ライブラリの例示コメント。行 269） |
| 27 | `templates/unit-template/README.md` | **除外** | 同上（不採用ライブラリの例示） |
| 28 | `templates/unit-template/backend/Services/SampleService/src/SampleService.Api/Program.cs` | **除外** | 記述は「ロギングは標準 `ILogger` + OpenTelemetry Logs（Serilog は使わない）」であり、**本作業で初めて事実になる**。修正不要 |
| 29 | `docs/specs/20260719_issue-284-live-integration-wiring.md` | **除外** | 確定済みの作業仕様書。`traceability.repo.md`「確定済みの `docs/specs/` は書き換えない」 |
| 30 | `docs/specs/20260803_issue-455_backend-application-standard.md` | **除外** | 同上 |
| 31 | `docs/specs/20260803_issue-471_backend-libraries-detection-gaps.md` | **除外** | 同上（`42 件` の記載も当時の実測として凍結） |
| 32 | `docs/superpowers/plans/2026-06-26-P0-foundation.md` | **除外** | `docs/superpowers/` は書き換えない（同規約）。P0 当時の設計記録 |
| 33 | `docs/superpowers/specs/2026-06-26-P0-foundation-design.md` | **除外** | 同上 |

軸 5 が出した `docs/specs/2026*` の `42 件` 記載（5 ファイル）も **29 に書き換えない** —— いずれも
**その issue の完了時点の実測値**であり、凍結された作業仕様書だからである。**本作業の実測値は本書に書く。**

### 表 B: 差し替える `Program.cs` 12 本と現行の呼び出し形

**2 通りある**（親の前提「すべて同じ 2 行」は誤り。実測で判明）。

| 形 | 呼び出し | 対象 |
| --- | --- | --- |
| A: ホスト差し替え | `builder.Host.UseSerilog((ctx, logConfig) => logConfig.ConfigurePlatformSerilog(ctx.Configuration, ServiceName));` | Platform.Bff / AuthorizationService.Api / LlmGateway.Api / AiAnalysisService.Api / DashboardService.Api / DataSourceService.Api / DocumentService.Api / FeedbackService.Api / RetrievalService.Api / WikiService.Api（**10 本**） |
| B: DI 登録 | `builder.Services.AddSerilog((sp, logConfig) => logConfig.ConfigurePlatformSerilog(builder.Configuration, ServiceName));` | ConversionService.Worker / IngestionService.Worker（**2 本**） |

**どちらも `builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);` の 1 行へ収束させる**
（形の違いは Serilog の API に由来しており、OTel Logging では区別が消える）。

## 設計

### 1. `AddPlatformLogging`（`ObservabilityExtensions.cs`）

`LoggerConfiguration`（Serilog）を受ける `ConfigurePlatformSerilog` を廃し、`ILoggingBuilder` を受ける
`AddPlatformLogging` を置く。名前空間・ファイル位置は据え置き（`AddPlatformObservability` の隣）。

```
ILoggingBuilder.AddOpenTelemetry(options =>
    options.SetResourceBuilder(<AddPlatformObservability と同一の ResourceBuilder>)
           .IncludeScopes             = true   // CorrelationId 等のスコープを属性へ載せる
           .IncludeFormattedMessage   = true   // 整形済み本文を Body に載せる
           .ParseStateValues          = true   // 構造化プロパティを属性へ展開する
           .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)))
```

- **リソース属性・OTLP エンドポイントは `AddPlatformObservability` と同じ導出**
  （`config["Otlp:Endpoint"] ?? "http://otel-collector:4317"`、`AddService(serviceName, serviceVersion: "0.1.0")`）。
  トレース・メトリクスと同じ生成規則にそろえるため、`ResourceBuilder` の生成を private ヘルパに切り出す。
- 既定のプロトコルは OTLP/gRPC。Serilog 側も `OtlpProtocol.Grpc` を明示していたため**変わらない**。
- **コンソール出力は既定のまま残る。** `UseSerilog` は全ログプロバイダを置換していたが、
  `builder.Logging.AddOpenTelemetry()` は**追加**であり、`WebApplication.CreateBuilder` が既定で入れる
  Console プロバイダが残る。Serilog の `.WriteTo.Console()` に対応する。
- **`ReadFrom.Configuration(config)` の代替は不要。** 標準ホストは `appsettings.json` の
  `"Logging"` セクション（`LogLevel`）を既定で読む。Serilog 側が読んでいた `"Serilog"` セクションは
  **どの `appsettings*.json` にも存在しない**（軸 1 の走査で 0 件）ため、実効の設定は失われない。

### 2. `CorrelationIdMiddleware`

`Serilog.Context.LogContext.PushProperty("CorrelationId", id)` を
`ILogger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id })` に置換する。
`AddPlatformLogging` の `IncludeScopes = true` により、スコープ中に出たログの `LogRecord` へ
`CorrelationId` 属性が載る。ミドルウェアは規約ベースのため、コンストラクタに
`ILogger<CorrelationIdMiddleware>` を追加注入する。レスポンスヘッダ `X-Correlation-ID` の扱いは変えない。

### 3. `AuditLogger`

**実体は変えない**（既に `ILogger<AuditLogger>` を使っており、`Audit=true` を構造化プロパティとして
付与する形も変えない）。コメント中の経路記述「可観測性基盤（Serilog→OTLP）」だけを実態に合わせる。

### 4. ratchet の消化

`scripts/backend-library-baseline.json` から `"Serilog"` の 13 エントリを引く。
`Serilog` だけを持つプロジェクトはキーごと削除し、`MassTransit` 等と併記のものは配列から `"Serilog"` を抜く。
`Serilog` は `BANNED` に**残す**（不採用の決定は変わらないため、再混入は fail させ続ける）。

## 受け入れ基準

- [x] `Serilog` の `PackageReference` が `src/` から 0 件になる（実測 3 → **0**）
- [x] `src/Directory.Packages.props` の `Serilog.AspNetCore` / `Serilog.Sinks.OpenTelemetry` の `PackageVersion` が消える
- [x] `.cs` の `using Serilog` / `Serilog.` 修飾参照が `src/` から 0 件になる（残る 2 ファイルの `Serilog` は「なぜ移したか」を書いた説明コメントのみ）
- [x] `dotnet build` / `dotnet test` が platform / knowledge 両ユニットで通る（**Failed=0**。下表参照）
- [x] `dotnet format --verify-no-changes` が両ユニットで通る
- [x] `node scripts/check-backend-libraries.js` の残件が **42 → 29** になり exit 0
- [x] **変異試験**: baseline から Serilog を引かないまま実装だけ入れると **fail** する
- [x] **ログが実際に出る**: OTLP 先が無くても起動が落ちず、OTLP へ実際に LogRecord が飛ぶ

## 検証結果（実測。2026-08-16）

| コマンド | EXIT | 判定行 |
| --- | --- | --- |
| `dotnet build platform/backend/backend.slnx` | 0 | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| `dotnet test platform/backend/backend.slnx` | 0 | AuthZ **68** / LlmGateway **157** / Bff **231**（Skipped 1）= **Passed 456・Failed 0** |
| `dotnet build knowledge/backend/backend.slnx` | 0 | `Build succeeded. 2 Warning(s) 0 Error(s)`（警告 2 は `MinioBuilder` の obsolete。本作業と無関係の既存分） |
| `dotnet test knowledge/backend/backend.slnx` | 0 | 11 アセンブリ合計 **Passed 596・Failed 0**（`Knowledge.IntegrationTests` 43 を含む） |
| `dotnet format platform/backend/backend.slnx --verify-no-changes` | 0 | 差分なし |
| `dotnet format knowledge/backend/backend.slnx --verify-no-changes` | 0 | 差分なし |
| `node scripts/check-backend-libraries.js` | 0 | `OK: 新規混入 0 件 / Domain 依存規律 OK（既知残件 **29 件** は baseline 済み）` |
| `node scripts/check-backend-libraries.js --self-test` | 0 | `自己試験 68 件 OK` |

### テスト件数の基準値について（重要）

**親から渡された基準「platform Passed=446」は本ブランチの基点の値ではない。** 実測で確かめた。

- `446` は `/home/user/microservices-platform`（**`f423ca4e`**）で再現した（AuthZ 68 / LlmGateway **151** / Bff **227**）。
- 本ブランチの基点は **`c01bc093`** で、`f423ca4e` より 6 コミット新しい。うち
  `6c542d07`（LLM 出力トークン Histogram）が LlmGateway のテストを **151 → 157**（+6）にしている。
- したがって基点の platform は **452**、本作業の +4（`PlatformLoggingTests`）で **456** になる。**回帰ではない。**
- knowledge は **596** で基準どおり（本作業でテストを増やしていない）。

### 変異試験の結果

`scripts/backend-library-baseline.json` だけを変更前（`Serilog` 13 件を引き忘れた状態）へ戻し、
実装は本作業のままで検査器を走らせた。

```
EXIT=1
[check-backend-libraries] 違反 13 件を検出しました:

  [baseline 減らし忘れ] src/knowledge/.../AiAnalysisService.Api.csproj
    「Serilog」の参照は既に解消済みです。baseline の該当行を削除してください。
  （以下、13 件すべて同型）
```

**検査器は減算漏れを捕まえる。** ratchet の 3 番目の規則（「baseline にあるのに違反が消えた → fail」）が
実際に機能していることを実測で確認した。試験後に baseline は減算済みの状態へ戻した。

### ログが実際に出たことの証跡

**(a) 単体テスト**（`Platform.Bff.Tests/PlatformLoggingTests.cs`。4 件すべて Passed）
本番と同じ `AddPlatformLogging` を通し、OTLP エクスポータと同じパイプラインへ捕捉用
`BaseProcessor<LogRecord>` を挿して `LogRecord` を実測した。`CorrelationId` がスコープとして載ること、
監査プロパティ（`AuditAction` / `AuditSubject` / `AuditOutcome` / `AuditDetail` / `Audit=true`）が
属性として保たれることを確認。

**(b) テストが空振りしていないことの変異試験**
`IncludeScopes = true` を `false` にすると
`CorrelationIdMiddleware_相関IDをスコープとして_LogRecord_へ載せる` が **Failed** になった
（`Failed: 1, Passed: 3`）。テストは実際に当該設定を測っている。
なお `ParseStateValues` を `false` にしても監査ログのテストは通った —— メッセージテンプレート由来の
状態は `IReadOnlyList<KeyValuePair>` として既に読めるためで、**`ParseStateValues` は保険である**。

**(c) 実サービスの起動**（`Platform.Bff`。OTLP 先 = `http://127.0.0.1:1`＝誰も listen していない）
起動は落ちず、`/health/live` が **HTTP 200 `Healthy`** を返した。コンソールへ 150 行のログが流れた
（標準の `Microsoft.Extensions.Logging.Console` 書式。`info:` / `warn:` 接頭辞）。

**(d) OTLP へ実際にバイトが飛ぶこと**
`127.0.0.1:14317` で待ち受けて `Platform.Bff` を起動したところ **27,869 バイト**を受信した。
ペイロード中に次を確認した。

- gRPC メソッド `/opentelemetry.proto.collector.logs.v1.LogsService/Export`（**ログ**の Export である）
- リソース属性 `service.name` = `microservices-platform.bff`、`service.version` = `0.1.0`
  （IADR-0216 §決定 2「リソース属性は変わらない」の裏取り）
- 構造化ログの**テンプレートと整形済み本文の両方**
  （`Drift detection started; interval={Interval}` と `Drift detection started; interval=00:05:00`。
  `IncludeFormattedMessage = true` の裏取り）
- ログカテゴリ名（`Platform.Shared.Infrastructure.Foundation.Introspection.DriftDetectionHostedService`）

**OTel SDK のエクスポート失敗は `ILogger` へは出ない**（内部 EventSource へ出る）。(c) のコンソールに
OTLP のエラーが見えないのはこのためであり、ログが出ていないわけではない —— (d) がそれを示す。

## テスト方針

`Platform.Bff.Tests` に `PlatformLoggingTests.cs` を追加する（`Platform.Shared.Infrastructure` 専用の
テストプロジェクトは無く、Bff が同アセンブリを推移参照しているため）。**「変わらないはず」で済ませず実測する**。

1. `AddPlatformLogging` を通したロガーが `LogRecord` を 1 件以上出すこと（`InMemory` ではなく
   `AddInMemoryExporter` 相当の収集で確認する）
2. `CorrelationIdMiddleware` のスコープ内で出したログの `LogRecord` に `CorrelationId` 属性が載ること
3. `AuditLogger.Record` の構造化プロパティ（`AuditAction` / `AuditSubject` / `AuditOutcome` / `Audit`）が
   `LogRecord` の属性として保たれること（`security.md` が約束している「`Audit=true` で抽出可能」の裏取り）
4. **OTLP 先が居ない状態でホストが起動し、落ちないこと**（エクスポータの接続失敗はログに出るが例外にしない）

## 計画書との差異

- 差異: **なし**。本作業は計画 `ADR-0030` の決定（ロギング = 標準 `ILogger` + OTel Logs、Serilog 不採用）へ
  実装を追いつかせるものであり、計画に対する逸脱を含まない。`ADR-0006` の統一計装・相関 ID による
  三者突合も維持する（相関 ID はスコープ経由で `LogRecord` に載り続ける）。

### 親エージェントの前提との差異（実測で判明した分）

1. **「`using Serilog` を持つ `.cs` は 15 ファイル」は誤り。実測 13。** 残り 2 は
   `CorrelationIdMiddleware.cs`（`using` を持たず完全修飾 `Serilog.Context.LogContext` で使う）と
   `AuditLogger.cs`（コメントで言及するだけで、実体は既に `ILogger`）である。
   **`check-backend-libraries.js` は `using` 宣言しか見ないため、完全修飾参照の
   `CorrelationIdMiddleware.cs` を検出していなかった** —— 検査器の既知の限界の実例。
2. **「12 個の `Program.cs` はすべて同じ 2 行」は誤り。** 表 B のとおり 2 通りある。
3. **`AuditLogger.cs` を `ILogger` へ「する」必要は無い。** 既に `ILogger<AuditLogger>` である。
4. **`check-backend-libraries.js` docstring の「42 件の偽陽性」は、文が掛かる `PackageVersion` の
   実数と一致しない**（実測: `BANNED` 該当の `PackageVersion` は 5 件。42 は baseline 残件数）。
   これは本作業以前からの不正確であり、**本作業では直さない**（残件数 42 → 29 の更新のみ行う）。

## 未決事項

- なし。`Serilog` を `BANNED` から外すかは論点になり得るが、**外さない**（計画 `ADR-0030` が
  不採用と決めており、再混入を止め続ける必要がある）。
