---
title: LlmGateway の終了理由（拒否率）をメトリクス化して可観測にする（issue #395）
type: spec
status: done
related_ids:
  - FR-11
  - NFR
  - UC-01
  - UC-02
  - ADR-0006
  - ADR-0010
  - ADR-0025
  - IADR-0022
  - IADR-0101
  - IADR-0104
  - IADR-0110
author: claude
created: 2026-07-28
updated: 2026-08-01
related_specs:
  - "../adr/IADR-0110_llm-completion-stop-reason-metrics.md"
  - "../adr/IADR-0104_llm-stop-reason-refusal.md"
  - "./20260725_issue-379_llm-stop-reason-refusal.md"
  - "../../docs/functional/FR-11_llm-egress-routing.md"
  - "../../docs/tests/FR-11_llm-egress-routing.md"
  - "../../docs/operations/operations.md"
---

# 仕様書: 拒否率（`stopReason`）の可観測化（issue #395）

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [#395](https://github.com/endazon/microservices-platform/issues/395)（`enhancement`）。
  [IADR-0104](../adr/IADR-0104_llm-stop-reason-refusal.md) §フォローアップ 3「拒否率の可観測性」の消化（起点は #379 / PR #391）。
- 要求: **FR-11**（LLM 送信可否の統制）、**NFR**（可観測性）。UC-01 / UC-02。
- 設計: ADR-0006（計画リポ）
  （OTel/Prometheus/Loki/Tempo への統一計装）、
  ADR-0010（計画リポ）（LLM ゲートウェイ）、
  ADR-0025（計画リポ）（既定 Opus 5・安全性分類器）、
  [IADR-0104](../adr/IADR-0104_llm-stop-reason-refusal.md)（`stopReason` の契約化・`Sent` と `StopReason` は独立した軸）。
- 本作業の実装判断は [IADR-0110](../adr/IADR-0110_llm-completion-stop-reason-metrics.md)。

## 背景と問題

[IADR-0104](../adr/IADR-0104_llm-stop-reason-refusal.md) により終了理由は**ログには**残るようになったが、**残っているのはログだけ**で、
拒否がどの頻度で起きているかを継続的に把握する手段が無い。拒否率は次の判断に直結する運用指標である。

- 既定モデルを Opus 5 にした（ADR-0025 / [IADR-0101](../adr/IADR-0101_default-model-opus-5.md)）ことで、安全性分類器による拒否が実運用にどれだけ出ているか
- 特定の用途（`purpose=trade-decision` 等）が恒常的に拒否されていないか（AST では拒否＝全判断 Hold へ縮退する）
- 障害（呼び出し先不達）と拒否の切り分け

現状の可観測性スタックは `AddPlatformObservability`（`Platform.Shared.Infrastructure`）で
OpenTelemetry の metrics パイプライン（ASP.NET Core / HttpClient / Runtime instrumentation ＋ OTLP exporter）
まで配備済みだが、**LlmGateway には独自の `Meter` が一つも無い**（`AddMeter` 登録も無い）。

## 対象範囲

### 変更する

| 対象 | 変更内容 |
| --- | --- |
| `LlmCompletionMetrics.cs`（新規） | `IMeterFactory` で `Meter` を作り、`llm.completion.total` カウンタを公開。属性を有限集合へ丸める |
| `CompletionEndpoints.cs` | `/complete`・`/complete/stream` の**全終了経路**（送信成立・越境拒否・プロバイダ未登録・呼び出し失敗）で計上 |
| `Program.cs` | `AddMetrics()` と `WithMetrics(m => m.AddMeter(...))` で OTLP へ流す |
| `CompletionMetricsTests.cs`（新規） | `MeterListener` で発行を固定（T-21） |
| `CompletionEndpointCollection.cs`（新規）＋既存 4 テストクラス | `MeterListener` は Meter 名でプロセス全体を購読するため、`/complete` を叩くテストクラスを 1 コレクションへ入れて直列化（他クラスの測定混入を防ぐ） |
| `docs/adr/IADR-0110_*`（新規）・`docs/adr/README.md` | 決定の記録と索引 |
| `docs/observability/llm-completion-metrics.md`（新規） | メトリクス定義・属性・カーディナリティ・PromQL 例 |
| `docs/operations/operations.md` | 拒否率の監視観点・しきい値方針・クエリ例へのリンク |
| `docs/functional/FR-11` / `docs/tests/FR-11` | 可観測性の節・T-21 |

### 変更しない（意図的に対象外）

- **`AddPlatformObservability`（共有インフラ）**。全サービス共通の計装であり、LlmGateway 固有の
  `Meter` 登録のためにシグネチャを変えない。OpenTelemetry の builder は加算的なので、
  LlmGateway の `Program.cs` から `WithMetrics(...)` を追加すれば足りる。
- **応答契約・ログ出力**（`CompletionApiResponse` / `LogStopReason`）。メトリクスは追加の観測面であり、
  既存の契約・ログは無変更（ログは未知語彙を**原文のまま**残す役割を引き続き担う）。
- **埋め込み経路（`/embeddings`）**。本 issue は補完（completion）の終了理由が対象。
- **アラートの実配線**（Grafana / Alertmanager のルール投入）。しきい値の**方針**は運用仕様書に記載するが、
  ルール定義そのものは経路B/本番像の運用作業であり、稼働中環境へ触れない本 PR の範囲外。
- **ダッシュボード JSON の投入**。PromQL 例をドキュメントに残すに留める（同上）。
- #394（`finish_reason` 写像）・#403（縮退応答のモデル名）。別 issue・別 PR。

## 決定（要約。詳細は [IADR-0110](../adr/IADR-0110_llm-completion-stop-reason-metrics.md)）

**カウンタ 1 本（`llm.completion.total`）に、送信可否（`llm.result`）と終了理由（`llm.stop_reason`）を
別々の属性として載せる。属性値はすべて有限集合へ丸め、未知値は `other` バケットへ集約する
（未知値の原文はログ側が保持する）。**

| 属性 | 値域（有限） |
| --- | --- |
| `llm.result` | `sent` / `egress_denied` / `provider_missing` / `upstream_error` |
| `llm.stop_reason` | `end_turn` / `max_tokens` / `refusal` / `stop_sequence` / `tool_use` / `other` / `none` |
| `llm.purpose` | `Llm:Routing:PurposeModels` に定義済みの用途 ＋ `default` ＋ `other` |
| `llm.model` | route 結果（設定 `Models` 由来） / `none` |
| `llm.provider` | `claude` / `selfhosted` / `copilot`（設定由来） / `none` |
| `llm.confidentiality` | `public` / `internal` / `confidential` / `restricted` |

拒否率 = `llm.stop_reason="refusal"` の合計 ÷ `llm.result="sent"` の合計。
`Sent=false`（越境拒否）とモデルの `refusal` は**別軸**で表現される（[IADR-0104](../adr/IADR-0104_llm-stop-reason-refusal.md) §決定に従う）。

## 実装方針（TDD）

1. **Red**: `CompletionMetricsTests` を追加し、`MeterListener` で `llm.completion.total` を購読して
   拒否・上限到達・正常終了・越境拒否・呼び出し失敗の 5 経路を検証する。現状はカウンタが存在せず失敗する。
2. **Green**: `LlmCompletionMetrics` を追加し、`CompletionEndpoints` の全終了経路で計上する。
   `Program.cs` で `AddMeter` する。
3. **追随**: 可観測性仕様書（新規）・運用仕様書・機能仕様書・テスト仕様書・IADR・索引。
4. **検証**: `dotnet test` / `dotnet format --verify-no-changes`（platform・knowledge 両ユニット）。

## テスト観点

| ID | 観点 | 期待 |
| --- | --- | --- |
| T-21a | 拒否（`refusal`） | `llm.result=sent` かつ `llm.stop_reason=refusal` が 1 計上される |
| T-21b | 上限到達（`max_tokens`） | `llm.stop_reason=max_tokens`（拒否と混ざらない） |
| T-21c | 正常終了（`end_turn`） | `llm.stop_reason=end_turn` |
| T-21d | 越境拒否（`Sent=false`） | `llm.result=egress_denied` かつ `llm.stop_reason=none`（拒否と別軸） |
| T-21e | 呼び出し失敗（例外） | `llm.result=upstream_error` |
| T-21f | 未知の終了理由 | `llm.stop_reason=other`（カーディナリティを閉じる。原文はログ側） |
| T-21g | 未定義の purpose | `llm.purpose=other`（呼び出し側の自由文字列でカーディナリティが増えない） |
| T-21h | ストリーミング（`/complete/stream`） | 非ストリーミングと同じ属性で計上される |

## 受け入れ基準（issue #395 §受け入れ基準に対応）

- [x] `stopReason` 別のカウンタが LlmGateway から公開され、OTLP 経由で収集できる
- [x] `refusal` / `max_tokens` / 正常終了 / 送信拒否（`Sent=false`）が、メトリクス上で相互に区別できる
- [x] 属性のカーディナリティが有限であることを確認・明記した（プロンプト等の高カーディナリティ値を属性にしていない）
- [x] 拒否率を見るためのダッシュボード or クエリ例が運用ドキュメントに記載されている
- [x] テスト（メトリクス発行の単体テスト）を追加した

## 完了条件（DoD）

- `dotnet build` / `dotnet test` が platform・knowledge 両ユニットで通る
- `dotnet format --verify-no-changes` が両ユニットで通る
- 上表の受け入れ基準がすべてチェック済み
- `docs/DEFINITION_OF_DONE.md` を満たす
