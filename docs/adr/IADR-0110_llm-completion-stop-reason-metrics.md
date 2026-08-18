---
title: IADR-0110 補完の終了理由をカウンタ 1 本で計上し、属性は有限集合へ丸める
type: impl-adr
status: Accepted
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
  - IADR-0225
  - ADR-0038
author: claude
created: 2026-07-28
updated: 2026-08-18
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md (OTel/Prometheus/Loki/Tempo への統一計装・Accepted)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md (LLM ゲートウェイ設計・Accepted・本文凍結)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md (既定 Opus 5・安全性分類器による refusal)"
---

# IADR-0110: 補完の終了理由メトリクス

- 状態: Accepted
- 日付: 2026-07-28
- 決定者: claude（実装）

## 起点・関連

- 起点 issue: [#395](https://github.com/endazon/microservices-platform/issues/395)（`enhancement`）。
  [[IADR-0104]] §フォローアップ 3「拒否率の可観測性」の消化（起点は #379 / PR #391）。
- 仕様書: `docs/specs/20260728_issue-395_refusal-metrics.md`。
- **本 IADR は [[IADR-0109]]（#394 / PR #415）を参照する**が、両者に実装上の依存は無い（正規化は
  プロバイダ境界、計上はエンドポイント境界で独立している）。develop へのマージ順序は問わない。
  本 PR が先に入る場合、`IADR-0109` のリンクは当該 PR がマージされるまで一時的に未解決になる。
- 本 IADR は**メトリクスの定義**（名前・属性・計上点）のみを扱う。応答契約・ログ・ルーティングは変更しない。

## コンテキストと課題

[[IADR-0104]] で終了理由（`refusal` / `max_tokens` / 正常終了）は区別できるようになったが、
**残っているのはログだけ**である。拒否率は運用判断（既定モデルの妥当性・特定用途の恒常的拒否・
障害と拒否の切り分け）に直結するのに、継続的な集計手段が無い。

可観測性の土台（`AddPlatformObservability` の OTel metrics ＋ OTLP exporter。ADR-0006）は配備済みだが、
**LlmGateway に独自の `Meter` が一つも無い**（`AddMeter` 登録も無い）ため、ゲートウェイ固有の指標が
一切出ていない。

### 難所はメトリクス設計ではなくカーディナリティ

計上すること自体は難しくない。難しいのは**属性値の値域を閉じること**である。時系列 DB の系列数は
属性値の直積で増え、非有界な属性を 1 つ混ぜるとカーディナリティ爆発を起こす。本経路には
非有界になり得る値が 2 つある。

1. **`purpose`** — `CompletionApiRequest.Purpose` は**呼び出し側が自由に指定できる文字列**である
   （エンドポイントは空なら `"default"` に倒すだけで、値の検証はしない）。誤った・実験的な purpose を
   送る呼び出し側が 1 つあるだけで系列が無限に増える。
2. **`stop_reason`** — [[IADR-0104]]（および #394 / [[IADR-0109]]）の決定により、**未知の終了理由は
   原文のまま透過する**。これは契約・ログでは正しいが、メトリクス属性にそのまま載せると
   プロバイダ側の語彙追加がそのまま系列増加になる。

プロンプト・本文・利用者識別子は当然ながら属性にしない。

## 検討した選択肢

1. **カウンタ 1 本＋属性で軸を分ける／属性値は有限集合へ丸める（採用）** — `llm.completion.total` に
   `llm.result`（送信可否）と `llm.stop_reason`（終了理由）を別属性で載せる。両者は
   [[IADR-0104]] が定めたとおり**独立した軸**であり、属性を分けるとその意味構造がそのまま集計に出る。
   拒否率は `stop_reason="refusal"` ÷ `result="sent"` で得られる。未知値は `other` バケットへ集約し、
   原文はログ側（[[IADR-0104]] / [[IADR-0109]]）が保持する。
2. 指標名を分ける（`llm.completion.refusal.total` / `llm.completion.denied.total` …） — 属性の直積は
   減るが、比率を取るのに複数指標を突き合わせる必要があり、後から軸（例: エンドポイント）を足すと
   指標が増殖する。集計の柔軟性を失うわりに得るものが少ない。
3. `stop_reason` を原文のまま属性に載せる — 監査の情報量は最大だが、プロバイダ側の語彙追加が
   そのまま系列増加になる（とくにセルフホスト系は独自値を返し得る）。**未知値こそ稀**であり、
   稀な値のために全系列の安定性を賭けるのは割に合わない。ログに原文が残るため情報も失われない。
4. `purpose` を素通しする — 呼び出し側の自由文字列であり、非有界。誤設定 1 つで系列が壊れる。
5. ヒストグラム（レイテンシ）も同時に導入する — 有用だが #395 の要求（拒否率の可観測化）を超える。
   終了理由の計上とは独立に決められるため、本 IADR では扱わない。

## 決定

> **［2026-08-18 追記 / #863］`llm.result` の値域に `fallback` が加わり 5 値になった。**
> 計画 `ADR-0038`（`Accepted`）決定 6「フォールバックの発火を可観測にする」の実装（[[IADR-0225]]）による。
> **本 ADR の決定 1〜7 はいずれも覆っていない** —— 計器は `llm.completion.total` の 1 本のままであり
> （新しい計器を足さない選択を [[IADR-0225]] が明示的に採った）、属性を有限集合へ丸める原則も、
> `llm.result` と `llm.stop_reason` を独立した軸として扱う決定 4 も維持されている。
> **下の決定 2 の表の `llm.result` 行は、当時の値域である**（原文は書き換えない）。現行値は次のとおり:
>
> | 属性 | 値域（現行） |
> | --- | --- |
> | `llm.result` | `sent` / `egress_denied` / `provider_missing` / `upstream_error` / **`fallback`** |
>
> `fallback` は「上流が HTTP 400 系を返し、次の候補モデルへ切り替えた呼び出し」を表す。
> **フォールバックが起きた 1 リクエストは 2 件計上される**（見送った候補が `fallback`、
> 成功した候補が `sent`）。**決定 3 の「分母が欠けて拒否率が歪む」は影響を受けない** ——
> 拒否率の分母 `llm.result="sent"` は従来どおりリクエストあたり最大 1 件だからである。
> **`upstream_error` へ混ぜなかった理由**は、回復した呼び出しを呼び出し先障害の率へ入れると
> `upstream_error` 率 > 10%（critical）のしきい値方針が誤発火するためである（[[IADR-0225]] §検討した選択肢 C4）。

1. **カウンタ `llm.completion.total`**（単位 `{completion}`）を LlmGateway に定義する。
   `Meter` 名は `microservices-platform.llm-gateway`（サービス名と一致）。`IMeterFactory` 経由で生成し、
   `Program.cs` の `WithMetrics(m => m.AddMeter(...))` で既存の OTLP パイプラインへ流す。
   **共有インフラ（`AddPlatformObservability`）は変更しない**（OTel builder は加算的である）。
2. 属性は次の 6 つとし、**すべて有限集合**に丸める。

   | 属性 | 値域 | 有限である根拠 |
   | --- | --- | --- |
   | `llm.result` | `sent` / `egress_denied` / `provider_missing` / `upstream_error` | 実装が持つ終了経路の列挙 |
   | `llm.stop_reason` | `end_turn` / `max_tokens` / `refusal` / `stop_sequence` / `tool_use` / `other` / `none` | 正準語彙（`CompletionStopReasons`）＋未知集約＋未報告 |
   | `llm.purpose` | `PurposeModels` のキー ＋ `default` ＋ `other` | **設定で閉じる**（未定義値は `other`） |
   | `llm.model` | route 結果 / `none` | ルータは設定の `Models` からしか選ばない |
   | `llm.provider` | `claude` / `selfhosted` / `copilot` / `none` | エンドポイント設定由来 |
   | `llm.confidentiality` | `public` / `internal` / `confidential` / `restricted` | `SensitivityClass`（未知は `restricted` へ倒す既存仕様） |

3. **計上点は `/complete`・`/complete/stream` の全終了経路**（送信成立・越境拒否・プロバイダ未登録・
   呼び出し失敗）とする。「送信していない」も計上しないと**分母が欠けて拒否率が歪む**。
4. **`llm.result` と `llm.stop_reason` は独立した軸として扱う**（[[IADR-0104]] §決定の踏襲）。
   `Sent=false`（越境拒否）は `stop_reason=none` であり、モデルの `refusal` とは混ざらない。
5. **未知の終了理由・未定義の purpose は `other` へ集約**する。原文が必要な調査はログ（warn / 監査）で行う。
   メトリクスは「傾向を見る面」、ログは「個別を追う面」と役割を分ける。
6. プロンプト・本文・利用者識別子・エンドポイント URL は**属性にしない**。
7. 回帰は T-21（`CompletionMetricsTests`。`MeterListener` で発行を購読）で固定する。

## 理由

- **`result` と `stop_reason` を別属性にする**ことで、[[IADR-0104]] が守った「越境が成立したか」と
  「モデルがどう終えたか」の分離が、そのまま集計軸として現れる。1 本のカウンタから
  拒否率・拒否件数・越境拒否件数・障害件数のすべてを導ける。
- **`other` バケット**は、未知値を捨てずに（件数としては残す）カーディナリティを閉じる最小の手段である。
  「未知が増えている」こと自体は `other` の増加として観測でき、原文はログで追える。
- **`purpose` を設定で閉じる**のは、非有界な入力を有界化する唯一の確実な方法である。加えて
  「定義していない purpose が来ている」＝ルーティングが `default` へ落ちている状態を `other` の増加として
  検知できる（[[IADR-0102]] / [[IADR-0106]] が繰り返し踏んだ「割当の無音の失効」に対する遅い警報になる）。
- **未送信も計上する**のは、分母（試行総数）が欠けると拒否率が過大に見えるからである。

## 結果

- 良い影響: 拒否率・上限到達率・越境拒否率・呼び出し失敗率が同一カウンタから継続的に取得でき、
  Grafana でのダッシュボード化・アラート化が可能になる。ADR-0025 の安全性分類器による劣化と、
  呼び出し先障害を数値で切り分けられる。AST（拒否＝全判断 Hold）の運用指標にもなる。
- 悪い影響 / トレードオフ:
  - **`other` に丸めた値は、メトリクスだけでは内訳が分からない**（ログ併用が前提）。
  - 属性 6 つの直積は理論上は大きい（実際の系列数は「実在するルーティング組合せ × 実際に起きた終了理由」に
    限られ、現構成では数十のオーダー）。将来エンドポイントやモデルを大量に増やす場合は
    `llm.model` の除外を再検討する。
  - ストリーミング／非ストリーミングの区別は**属性にしない**（さらに ×2 になるため）。必要なら
    ASP.NET Core の HTTP メトリクス（ルート別）で切り分ける。
  - メトリクスは**アラート配線までは含まない**。しきい値の方針は運用仕様書に記載するが、
    ルール投入は稼働中環境への操作であり本作業の範囲外。
- フォローアップ:
  1. **拒否率アラートの実配線**（Grafana / Alertmanager）。方針は `docs/operations/operations.md` に記載。
  2. **レイテンシ・トークン消費のヒストグラム**（選択肢 5）。コスト最適化（05_observability-ops）と
     [#380](https://github.com/endazon/microservices-platform/issues/380)（`max_tokens` 実測）に接続すると効果が高い。
  3. 埋め込み経路（`/embeddings`）の同種メトリクス。本 IADR の型をそのまま流用できる。

## 関連

- Supersedes: なし（[[IADR-0104]] §フォローアップ 3 を消化する）
- Superseded by: なし
- 関連要求 / UC: FR-11（LLM 送信可否の統制）、NFR（可観測性）、UC-01 / UC-02
- 関連 IADR: [[IADR-0104]]（`Sent` と `StopReason` の軸の分離）、[[IADR-0022]]（ルーティング）、
  [[IADR-0101]]（既定 Opus 5・`max_tokens`）
