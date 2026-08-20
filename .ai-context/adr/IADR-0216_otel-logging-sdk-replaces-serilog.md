---
title: IADR-0216 ログの出口を Serilog から OTel Logging SDK へ移す
type: impl-adr
status: Accepted
related_ids:
  - ADR-0006
  - ADR-0030
author: implementation-agent
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md (可観測性スタック)
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (ロギング = ILogger + OTel Logs、Serilog 不採用)
---

# IADR-0216: ログの出口を Serilog から OTel Logging SDK へ移す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: Accepted
- 日付: 2026-08-16
- 決定者: implementation-agent（計画 `ADR-0030` の下位決定として）

## 起点・関連

- 関連する計画書 ID: 計画 `ADR-0006`（可観測性スタック）/ 計画 `ADR-0030`（バックエンドアプリケーション層のライブラリ標準）
- 関連する実装仕様書: [`docs/specs/20260816_issue-455_serilog-to-otel-logging.md`](../specs/20260816_issue-455_serilog-to-otel-logging.md)
- 起票: #455（不採用ライブラリの ratchet 消化）の断片

## コンテキストと課題

計画 `ADR-0030` は選定基準 2（標準機能優先）に基づき、**ロギングを「標準 `ILogger` + OpenTelemetry Logs」に
確定し、Serilog（および Seq）を不採用**とした。しかし実装は P0 基盤構築時のまま
`Serilog.AspNetCore` + `Serilog.Sinks.OpenTelemetry` でログを OTLP へ流しており、
`scripts/check-backend-libraries.js` の ratchet に **13 件**の既知残件として計上され続けている。

問題は「どう置換するか」ではなく **「置換して何が変わるのか」を明示できるか**である。
ログは障害対応の一次資料であり、`ADR-0006` は相関 ID による三者（メトリクス・ログ・トレース）突合を
決定している。**出口を替えて相関 ID や監査ログの抽出条件が静かに壊れると、検知はできない。**
よって本 ADR は、置換の方針とあわせて**変わらないこと・変わることを分けて記録する**。

## 検討した選択肢

| # | 選択肢 | 評価 |
| --- | --- | --- |
| 1 | **`builder.Logging.AddOpenTelemetry()`（OTel Logging SDK）へ移す** | `ADR-0030` の決定そのもの。標準 `ILogger` のプロバイダとして OTLP へ流す。トレース・メトリクスと同じ `ResourceBuilder`・同じエンドポイントを共有でき、可観測性の設定が 1 箇所に集まる |
| 2 | Serilog を残し `BANNED` から外す | 計画 `ADR-0030` の決定に反する。ライセンス持続性の基準を通した決定を実装都合で覆すことになり、覆すなら計画側の改定 ADR が要る |
| 3 | Serilog を残したまま baseline だけ削る | ratchet の意味を壊す（「消えたのに残っていれば fail」の逆を人手でやる行為）。検査を無効化するだけで、標準への準拠は進まない |

選択肢 1 を採る。2 は計画への逸脱、3 は検査器の空洞化であり、いずれも採らない。

## 決定

**1. ログの出口を `builder.Logging.AddOpenTelemetry()`（OTel Logging SDK）に一本化する。**

- `Platform.Shared.Infrastructure` の `ConfigurePlatformSerilog`（`LoggerConfiguration` 拡張）を廃し、
  **`AddPlatformLogging`（`ILoggingBuilder` 拡張）**を置く。各サービスの `Program.cs` は
  `builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);` の 1 行で初期化する。
- `Serilog.AspNetCore` / `Serilog.Sinks.OpenTelemetry` の `PackageReference`（3 プロジェクト）と
  `PackageVersion`（`src/Directory.Packages.props`）を削除する。
- **`Serilog` は `check-backend-libraries.js` の `BANNED` に残す。** 不採用の決定は変わらず、
  再混入は fail させ続ける。撤去したのは実装であって決定ではない。

**2. 変わらないこと（実測で確認した）。**

| 面 | 変わらない理由・確認方法 |
| --- | --- |
| **OTLP エンドポイント** | `config["Otlp:Endpoint"] ?? "http://otel-collector:4317"` の導出を据え置き。プロトコルも Serilog 側が `OtlpProtocol.Grpc` を明示していたため OTLP/gRPC のまま |
| **リソース属性** | `ResourceBuilder.CreateDefault().AddService(serviceName, serviceVersion: "0.1.0")` を `AddPlatformObservability`（トレース・メトリクス）と**共有**する。Serilog 側は `ResourceAttributes` に `service.name` だけを手で置いていたので、むしろ属性はトレース・メトリクスと厳密に一致するようになる |
| **`CorrelationId` の伝播** | `Serilog.Context.LogContext.PushProperty` → `ILogger.BeginScope` へ替え、`IncludeScopes = true` で `LogRecord` の属性へ載せる。レスポンスヘッダ `X-Correlation-ID` の扱いは 1 行も変えない。**テストで `LogRecord` の属性として実測する** |
| **監査ログ（`AuditLogger`）の出口** | **元から `ILogger<AuditLogger>` である**（Serilog の API を直接呼んでいない）。`Audit=true` を含む構造化プロパティの付与も変えない。`docs/security/security.md` が約束する「`Audit=true` で抽出可能」を**テストで実測する** |
| **構造化ログの項目** | `ILogger` のメッセージテンプレートとプロパティ名（`{AuditAction}` 等）は不変。`ParseStateValues = true` により `LogRecord` の属性へ展開される |
| **OTLP 先が不在でも起動が落ちないこと** | Serilog sink・OTel エクスポータのいずれも接続失敗を例外に昇格させない。**テストで実測する** |

**3. 変わること（正直に記録する）。**

| # | 変わる点 | 内容と判断 |
| --- | --- | --- |
| 1 | **コンソール出力の書式** | `UseSerilog` は**全ログプロバイダを置換**していたため、コンソールは Serilog の書式で出ていた。`builder.Logging.AddOpenTelemetry()` は**追加**であり、既定の `Console` プロバイダ（`Microsoft.Extensions.Logging.Console`）の書式に戻る。**ログ本文・プロパティは失われない**が、`docker logs` の見た目は変わる。機械が読む経路は OTLP 側であり、書式の互換は要件になっていないため受け入れる |
| 2 | **イベント単位の `ServiceName` プロパティが消える** | Serilog は `Enrich.WithProperty("ServiceName", serviceName)` で**各イベントに**サービス名を載せていた。OTel Logging では**リソース属性 `service.name`** が同じ役割を担う（Serilog 側も `ResourceAttributes` で二重に置いていた）。よって**サービス名で絞る検索は `service.name` で成立し続ける**が、`ServiceName` という名のイベント属性を条件にしたクエリがもし存在すれば壊れる。**リポジトリ内に `ServiceName` を条件とするダッシュボード・アラートは無い**ことを走査で確認した |
| 3 | **重大度（severity）の表現** | Serilog は `LogEventLevel`（`Verbose`/`Debug`/`Information`/`Warning`/`Error`/`Fatal`）を sink が OTel の `SeverityNumber` へ写していた。OTel Logging SDK は `Microsoft.Extensions.Logging.LogLevel` から直接写す。**`Trace`↔`Verbose`・`Critical`↔`Fatal` の名前が変わるだけで、`SeverityNumber` の値は同じ段に落ちる**（`Trace`=1, `Debug`=5, `Information`=9, `Warning`=13, `Error`=17, `Critical`=21）。**アプリのコードは元から `ILogger` の `LogLevel` を使っており、写像は 1 段挟まらなくなる分むしろ素直になる** |
| 4 | **`ReadFrom.Configuration(config)` の消滅** | Serilog は `appsettings` の `"Serilog"` セクションを読んでいた。**どの `appsettings*.json` にも `"Serilog"` セクションは存在しない**（走査で 0 件）ため、実効設定の喪失は無い。今後の水準設定は標準の `"Logging:LogLevel"` セクションで行う（既に全サービスが持っている） |
| 5 | **`LogContext` のグローバルな伝播** | Serilog の `LogContext` は `AsyncLocal` でどこからでも push できた。`ILogger.BeginScope` はロガー経由になる。**現行の唯一の利用箇所は `CorrelationIdMiddleware` の 1 箇所**であり、影響は無い |

**4. `Program.cs` の初期化は 1 行に収束させる。**
現行は `builder.Host.UseSerilog(...)`（10 本）と `builder.Services.AddSerilog(...)`（2 本）の 2 通りに
分かれていた。これは Serilog の API 差に由来する形であり、OTel Logging では区別が消える。
**12 本すべてを `builder.Logging.AddPlatformLogging(builder.Configuration, ServiceName);` に統一する。**

## 理由

- 計画 `ADR-0030` の決定（選定基準 2「標準機能優先」）を実装へ反映するのが本作業の目的であり、
  選択肢 1 以外は決定に反するか、検査を空洞化させる。
- ログ・トレース・メトリクスが**同一の `ResourceBuilder` とエンドポイント**を共有するため、
  `ADR-0006` の「相関 ID で三者を突合する」が構成上より強く保証される（従来はログ側だけが
  別系統でリソース属性を組み立てており、ずれ得る形だった）。
- 依存が 2 パッケージ減り、CPM の版定義も 2 件減る。Serilog 系の商用化・破壊的変更に追随する
  保守費用が消える（`ADR-0030` 選定基準 1）。

## 結果

- 良い影響:
  - 不採用ライブラリの ratchet 残件が **42 件 → 29 件**（13 件消化。`Serilog` の全消化）へ下がる。
  - ログのリソース属性がトレース・メトリクスと厳密に一致する。
  - `Program.cs` の初期化が 2 通り → 1 通りに収束する。
- 悪い影響・トレードオフ:
  - コンソールログの書式が変わる（上表 3-1）。運用手順書がログの見た目に依存していないことは確認済み。
  - イベント単位の `ServiceName` プロパティが消える（上表 3-2）。`service.name` リソース属性で代替する。
- フォローアップ:
  - 残る ratchet 残件は `MassTransit` / `FluentAssertions` の 29 件。別 issue で消化する。
  - `src/Directory.Packages.props` の「移行完了時に削除する」対象は `MassTransit` / `FluentAssertions` の 2 群になる。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連する計画 ADR: `ADR-0006`（可観測性スタック）・`ADR-0030`（アプリケーション層ライブラリ標準）
