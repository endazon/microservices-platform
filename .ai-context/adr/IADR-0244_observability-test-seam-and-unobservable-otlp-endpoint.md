---
title: IADR-0244 テレメトリ設定は「三信号のリソース一致」で守り、OTLP 送信先は internal のシームで導出だけ守る
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0006
  - IADR-0216
  - IADR-0233
  - IADR-0236
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0006_observability.md
---

# IADR-0244 テレメトリ設定の試験可能面と、残る観測不能領域

## 状況

`AddPlatformObservability` / `AddPlatformLogging`（`ObservabilityExtensions.cs`・各 **13 箇所**が使用）は
**壊れても何一つ落ちない**。OTLP 送信先が変わってもリソース属性が割れても、例外は出ず、ログは出続け、
HTTP は 200 を返し、ビルドも通る。**テレメトリが黙って別のリソースへ付くだけ**である。

`ADR-0006` は「相関 ID でログ・トレース・メトリクスを突合する」と定めており、
これは**三者のリソース属性が一致していて初めて成立する**。コード自身も
「導出を 2 箇所に書かず、この 2 つのヘルパへ集約する」と書いているが、
**その一致を守る機械は無かった**。

## 何が観測できるかを先に実測した

「テストを書けるか」ではなく「**何が観測可能か**」から決めた。実測（OpenTelemetry 1.16.0）:

| 面 | 観測可否 | 手段 |
| --- | --- | --- |
| トレースのリソース属性 | **可** | `TracerProvider.GetResource()` |
| メトリクスのリソース属性 | **可** | `MeterProvider.GetResource()` |
| **ログのリソース属性** | **可** | `LoggerProvider.GetResource()`（`ILoggerFactory` を触って構築させた後） |
| `IncludeScopes` / `IncludeFormattedMessage` / `ParseStateValues` | **可** | `IOptionsMonitor<OpenTelemetryLoggerOptions>` |
| **OTLP 送信先** | 🔴 **不可** | 下記 |

### 🔴 OTLP 送信先が観測不能であることは 3 経路で確かめた

`AddOtlpExporter(o => o.Endpoint = otlpEndpoint)` で設定した値は、外から読む手段が無い。
**1 経路で見つからないのは「その形では見つからない」でしかない**ため、3 経路で確かめた。

1. `IOptionsMonitor<OtlpExporterOptions>` を `""` / `otlp` / `traces` / `metrics` / `logs` ほかで総当り
   → **どの名前も既定値 `http://localhost:4317/`** を返し、設定した値は出てこない
2. `IConfigureOptions<OtlpExporterOptions>` の登録数 → **0 件**
3. `TracerProviderSdk` を深さ 6 までリフレクションで走査し、`Otlp` を名に含む型の
   `Uri` / URL 文字列フィールドを探索 → **見つからず**

## 決定

### 決定 1: 三信号のリソース一致を試験で固定する

`ADR-0006` の突合が成立する条件そのものを守る。**トレースを基準に、メトリクスとログの
リソース属性が完全一致すること**を assert する。片方だけを見ると、両方が同じようにずれた場合に
気付けない。

### 決定 2: `IncludeScopes` を名指しで守る

`false` へ退行すると `CorrelationIdMiddleware` の `BeginScope` が `LogRecord` から消え、
**`ADR-0006` が静かに成立しなくなる**（ログは出続けるので気付けない）。
`IncludeFormattedMessage` / `ParseStateValues` も同じ試験で見るが、**assert のメッセージを
フラグごとに分け**、どれが壊れたかが失敗本文で特定できるようにする。

### 決定 3: 🔴 OTLP 送信先の**導出だけ**を internal のシームで守る

`ObservabilityExtensions.OtlpEndpointOf` を `private static` → **`internal static`** にし、
`Platform.Shared.Infrastructure.csproj` へ `InternalsVisibleTo` を足す。

**なぜ製品コードを触ってまでシームを入れたか。**
このシームが無いと、OTLP 送信先まわりは**変異を当てられない対象として丸ごと残る** ——
設定キー名を変えても、既定値を書き換えても、**どのテストも落ちない**。
「テストが無い」より「**検出できないことが記録されていない**」ほうが危険である。
シームを入れれば、少なくとも次の 2 つの退行は捕まる。

- 設定キー `Otlp:Endpoint` の変更（設定が読まれなくなる）
- 既定値 `http://otel-collector:4317` の書き換え（設定を持たない環境が別の宛先へ行く）

`internal` の露出は試験アセンブリ 1 つに限る。公開面は変えない。

## 検出しないこと

🔴 **ここに挙げたものは「試験済み」ではない。** 次の人が「endpoint はテスト済み」と読まないよう明記する。

- 🔴 **`OtlpEndpointOf` の戻り値が実際に exporter へ届いているか。**
  決定 3 が守るのは**導出だけ**である。`AddOtlpExporter` への受け渡しを外して
  `o.Endpoint` を書かなくしても、**どのテストも落ちない**（上の 3 経路の実測がその理由）。
  この穴は残る。**位置が特定されている穴として記録する。**
- **テレメトリが実際に OTLP 先へ届くか。** 収集器との疎通は試験していない。
- **リソース属性が正しい値かどうか**（`service.name` が意味的に妥当か）。
  試験が見るのは「三信号で一致していること」と「サービス名・バージョンが載っていること」だけである。
- **`AddAspNetCoreInstrumentation` 等の計装が実際に収集するか。** 登録の有無すら見ていない。

## 影響

- `ObservabilityExtensions.cs` の `OtlpEndpointOf` が `internal` になる（公開面は不変）。
- `Platform.Shared.Infrastructure` に `InternalsVisibleTo` が 1 つ増える。
- 変異試験 8 種（`IncludeScopes` / `ParseStateValues` / `IncludeFormattedMessage` /
  メトリクスのリソース / ログのリソース / 既定値 / 設定キー名 / サービスバージョン）が
  **すべて当たることを実測した**。

## 代替案

- **シームを入れず、観測できる面だけ試験する** —— OTLP 送信先が丸ごと無防備になり、
  かつ**その事実がどこにも記録されない**。採らない。
- **`InternalsVisibleTo` ではなくリフレクションで private を読む** —— 名前が変われば静かに
  no-op 化する（`IADR-0233` が同型の失敗を記録している）。採らない。
- **実際に OTLP 収集器を起動して疎通を見る** —— 統合試験の射程であり、単体試験で持つと
  Docker 依存が入る。第 1 PR の方針（プロセス内で完結）を崩す。採らない。
- **`OtlpExporterOptions` を公開設定として作り直す** —— 製品コードの設計変更であり、
  被覆向上の PR の射程を超える。必要なら別 issue。
