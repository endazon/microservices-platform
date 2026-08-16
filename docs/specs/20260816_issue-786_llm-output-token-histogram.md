---
title: 作業仕様書 — LLM 出力トークンの Histogram を足し、#786 の残る受け入れ基準を閉じる
type: spec
status: done
related_ids:
  - NFR-19
  - FR-11
  - ADR-0006
  - ADR-0044
  - IADR-0101
  - IADR-0104
  - IADR-0110
  - IADR-0212
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md"
related_specs:
  - "../adr/IADR-0212_llm-output-token-histogram.md"
  - "../adr/IADR-0110_llm-completion-stop-reason-metrics.md"
---

# 作業仕様書: LLM 出力トークンの Histogram（#786）

## 1. 起点

**#786 は「メトリクスが 1 系列も出ていない」として起票されたが、起票時の推定は誤りだった。**

起票時は「`Meter` が `MeterProvider` に登録されていない疑い」と書いた。実装を読むと
`Program.cs:29` に `AddMeter(LlmCompletionMetrics.MeterName)` があり、**配線は健全**だった。
**真因は `/complete` への無トラフィック** —— OpenTelemetry の Counter は最初の `Add()` まで
export されない。issue のタイトルと本文は訂正済み（コメントに実測を残した）。

その訂正の結果、残ったのは受け入れ基準の後半 2 つである。

| 基準 | 状態 |
| --- | --- |
| ① `llm_completion_total` が系列として現れ、ラベルが値を持つ | **済**（実測。§2） |
| ② `docs/observability/llm-completion-metrics.md` のクエリ例が値を返す | **本 PR**（§2 の留保つき） |
| ③ 出力トークンの Histogram を足すか判断する | **本 PR ＝ 足す**（[[IADR-0212]]） |
| ④ 同型の配線漏れが他サービスに無いかを走査 | **本 PR ＝ 0 件**（§3） |

## 2. 実測（2026-08-16・稼働中の k3s）

### ① 系列は出る

`/complete` を呼ぶと即座に系列が現れた。**6 属性すべてが値を持つ。**

```
llm_completion_total{llm_confidentiality="internal", llm_model="claude-opus-5",
  llm_provider="claude", llm_purpose="default", llm_result="sent",
  llm_stop_reason="max_tokens"} = 1
```

### ② ★ 系列は出るのに `rate()` は 0 を返す

**これが #380 にとって最も重要な実測である。**

| クエリ | 結果 |
| --- | --- |
| `count(llm_completion_total)` | 2 → **6**（6 回の呼び出し後） |
| `sum(rate(llm_completion_total{llm_stop_reason="max_tokens"}[30m]))` | **0** |
| `sum by (llm_purpose) (rate(...{llm_stop_reason="refusal"}[30m]))` | **空ベクタ** |
| 上限到達率（`increase` の比） | **NaN → 0** |

**理由**: 6 回の呼び出しが**すべて別のラベル組み合わせ**になり、各系列がサンプル 1 点しか持たない。
`rate()` は同一系列に 2 点以上を要求する。**refusal は一度も起きていない**ため系列自体が存在しない
（Prometheus は「起きていないラベル値」を 0 として持たない）。

> **#380 のゲートは「計器がある」では開かない。**「**同一ラベル組み合わせで反復するトラフィック**が
> 保持期間のあいだ蓄積している」が要る。#787（永続化）が片付いても、**トラフィックが無ければ
> 上限到達率は読めない。** これは #380 へ書き戻す。

### ③ 出力トークンは既に手元まで来ている

```
POST /complete {"prompt":"Say OK","purpose":"default","maxTokens":16}
→ {"text":"OK","model":"claude-opus-5","inputTokens":10,"outputTokens":16,
   "sent":true,"stopReason":"max_tokens"}
```

## 3. 母集合 —— 同型の配線漏れ（受け入れ基準 ④）

**「Meter や計器を作ったが `AddMeter` していない」を、誤りの側（計器の生成 API）から引いた。**

| 軸 | 引き方 | 結果 |
| --- | --- | --- |
| 1 | `new Meter(` / `CreateCounter` / `CreateHistogram` / `CreateUpDownCounter` / `CreateObservable` | 本番 2・テスト 1 |
| 2 | `AddMeter(` を **repo 全体**（`.md` も含む） | コード 2・散文 4 |
| 3 | ファイル名に `metric` | 新規なし |
| 4 | `WithMetrics` / `AddOpenTelemetry(` | 3 箇所（共通拡張 ＋ 2 サービス） |
| 5 | `Counter<` / `Histogram<` / `UpDownCounter<` / `Gauge<` | **`Histogram<` は 0 件** |
| 6 | `.csproj` / `Directory.Packages.props` の計測 SDK | OTel のみ（prometheus-net 等は不採用） |
| 7 | `meterFactory.Create` | 軸 1 と一致 |
| 8 | フロントの `@opentelemetry` | 0 件 |

| 自前 Meter | 定義元 | 登録元 | 判定 |
| --- | --- | --- | --- |
| `microservices-platform.llm-gateway` | `LlmCompletionMetrics.cs:20` | `LlmGateway.Api/Program.cs:29` | **健全** |
| `microservices-platform.document-service` | `IngestTagMetrics.cs:22` | `DocumentService.Api/Program.cs:24` | **健全** |

**差集合 = 0 件。** ワイルドカード登録（`AddMeter("*")`・接頭辞一致）は 1 件も無く、2 件とも明示登録である。

**除外**: `src/ai-stock-trading`（submodule 未取得＝走査できない。`git submodule status` が先頭 `-`）。
`planning/`（計画リポ）。

### ★ 検査器は足さない（1 回目は記録に留める）

**構造的には漏れうる。** 共通拡張 `AddPlatformObservability`
（`Platform.Shared.Infrastructure/Foundation/Extensions/ObservabilityExtensions.cs:14-37`）は
`AddAspNetCoreInstrumentation` / `AddHttpClientInstrumentation` / `AddRuntimeInstrumentation` の
3 本しか持たず、**`AddMeter` を一度も呼ばない**。12 サービス全部がこの拡張を使っているが、
自前 Meter は各サービスが自分で登録する必要がある。

それでも**検査器は足さない** —— `CLAUDE.md` の運用ガイドが「**同型の事故が 2 回起きたら**」を
条件としており、**今回は 0 回目（漏れは実在しなかった）**である。本節が 1 回目の記録に当たる。

## 4. 決定（詳細は [[IADR-0212]]）

**出力トークンの Histogram を足す。** #443 へ送らない。理由:

- **3 プロバイダすべてで値が取れている**（`ClaudeProvider` は SDK の `Usage`、`CopilotProvider` /
  `SelfHostedProvider` は OpenAI 互換の `usage.completion_tokens`）。取得側の実装は要らない
- **`result=sent` の 2 箇所からそのまま渡せる**（応答組み立てが既に同じ変数を使っている）
- #380 の受け入れ基準 ① は**出力トークンの実測**を求めており、Counter だけでは満たせない

**計画 ADR-0044 との対応**: 同 ADR は「未実装: トークン消費量・金額換算・フォールバック発火回数・単価表」と
明記している。本 PR が埋めるのは**トークン消費量まで**で、**単価表と金額換算（決定 2・決定 3）は未着手**である。
対応表は [[IADR-0212]] の起点・関連に置いた。

## 5. 実装

| ファイル | 変更 |
| --- | --- |
| `Foundation/Observability/LlmCompletionMetrics.cs` | `Histogram<int>` を追加し、`RecordCompletion` へ `int? outputTokens = null` を足す |
| `Foundation/Endpoints/CompletionEndpoints.cs` | `result=sent` の 2 箇所だけ値を渡す（残り 6 箇所は既定 null） |
| `tests/.../CompletionMetricsTests.cs` | Histogram の測定を拾うプローブと検査を追加 |
| `docs/observability/llm-completion-metrics.md` | Histogram のクエリ例と、§2 ② の「反復トラフィックが要る」注記 |

### 記録点の非対称（重要）

**Counter は全 8 経路で計上する**（[[IADR-0110]]・分母を欠けさせない）。
**Histogram は `result=sent` の 2 経路だけ**である。残り 6 経路にはトークン数が概念的に存在しない
（送信していない／応答が返っていない）。**この非対称は意図であり、[[IADR-0212]] 決定 3 に書く。**

## 6. 検証

| 検証 | 結果 |
| --- | --- |
| `dotnet build LlmGateway.Api.csproj` | 0 警告 / 0 エラー |
| `dotnet test LlmGateway.Api.Tests` | **156 件すべて合格**（うち本 PR で 5 件追加） |
| `dotnet format --verify-no-changes`（本体・テスト） | 両方 exit 0 |
| `check-adr-numbering` / `check-doc-links` ほか | exit 0 |

### 足したテスト（5 件）

| # | 何を固定するか |
| --- | --- |
| T-786a | 送信成立で出力トークン数が分布へ記録される（値と属性） |
| T-786b | `llm.result` を Histogram の属性に載せない（決定 2） |
| T-786c | **越境拒否では Histogram を記録しないが Counter は計上する**（決定 3 の非対称そのもの） |
| T-786d | upstream 例外でも Histogram を記録しない |
| T-786e | ストリーミング経路も同じ値を記録する |

> **★ T-786c は 1 度目に落ちた。** `confidentiality: "secret"` を渡せば越境拒否になると
> **推測**したが、実際は `sent` へ抜けた（既定ルーティングが受けてしまう）。
> 既存の T-21d が使っている構成（未承認のティア C だけを置く）を読んで写したら通った。
> **経路を推測で作らない** —— 同じファイルに実在する先例を読む。

### 実機での確認（未了・意図的）

`llm_completion_output_tokens_bucket` が Prometheus に現れることは**まだ測っていない**。
稼働クラスタのイメージは develop のビルドであり、本 PR の変更を含まないためである。
**マージ後にイメージを焼き直してから測る。** §2 ② のとおり、意味のある分布を読むには
**反復トラフィック**も要る。

## 7. 未決事項

- **入力トークンの Histogram は足さない。** #380 の基準 ① が求めるのは出力側であり、
  入力側は別の問い（プロンプト設計）に属する。要るなら別 issue。
- `CopilotProvider` / `SelfHostedProvider` の `?? 0` は「upstream が `usage` を返さなかった」と
  「本当に 0」を区別できない。**現状のまま記録する**（0 が積み上がれば異常として読める）。
  区別が要るなら別 issue。
