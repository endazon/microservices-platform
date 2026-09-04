---
title: 作業仕様書 — #1091 LLM 失敗メトリクスへ上流 HTTP ステータスの軸（値域を閉じた 6 値）を足す
type: spec
status: done
related_ids:
  - FR-11
  - NFR-21
  - ADR-0006
  - ADR-0025
  - ADR-0038
  - ADR-0044
  - IADR-0104
  - IADR-0110
  - IADR-0212
  - IADR-0225
  - IADR-0244
  - IADR-0306
  - IADR-0345
  - IADR-0374
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0038_analysis-purpose-drop-fable-5.md (決定 4・決定 6)
  - planning:projects/microservices-platform/07_adr/ADR-0044_llm-usage-metrics-and-pricing-table.md (決定 1)
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR-21)
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md
related_specs:
  - ./20260818_issue-863_adr-0038-fallback-order-and-429.md
  - ./20260830_issue-380_opus5-max-tokens-measurement.md
  - ../adr/IADR-0374_llm-upstream-status-metric-axis.md
issue: "1091"
---

# 作業仕様書 — #1091 上流 HTTP ステータスの軸

## 結論（先に書く）

`llm.completion.total` へ **7 本目の属性 `llm.upstream_status`** を足す。値域は
**`none` / `rate_limited` / `client_error` / `server_error` / `transport` / `other` の 6 値に閉じる**。
生の HTTP ステータス（`http.response.status_code` の数値）は載せない。
`llm.result` の値域は**変えない**（既存のしきい値方針・ダッシュボードの意味を動かさない）。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-11**（LLM 送信可否・呼び出し先切替）
- 非機能要求: **NFR-21**（障害検出 5 分以内 / MTTR 30 分以内）
- 関連計画 ADR: **ADR-0038**（決定 4 ＝ 429 は再試行でありフォールバックではない／決定 6 ＝ フォールバック
  発火の可観測化）、**ADR-0044**（決定 1 ＝ 費用系と終了理由系を**同じ軸**で読めること）、
  **ADR-0006**（可観測性スタック）、**ADR-0025**（既定モデル Opus 5）
- 実装 ADR: 本作業で **IADR-0374** を起草する。先行は IADR-0110（終了理由カウンタ）／
  IADR-0212（出力トークン Histogram）／IADR-0225（用途フォールバック鎖と 429 の境界）／
  IADR-0306（ログ衛生）／IADR-0244（可観測性の試験可能面）

## 対象範囲（母集合を自分で引いた結果）

### 引き方

**「失敗を計上している箇所」を記憶で挙げず、計上 API の名前と `catch` の両方で走査した**
（`.claude/rules/traceability.repo.md` 規則 9）。実行したコマンドと出力は下記のとおり。

```
$ grep -rn "RecordCompletion" --include=*.cs src | grep -v "/obj/\|/bin/"
src/platform/backend/Services/LlmGateway/Common/Observability/LlmCompletionMetrics.cs:91   （定義）
src/platform/backend/Services/LlmGateway/Features/Completions/Complete/Endpoint.cs:36,48,71,93,105
src/platform/backend/Services/LlmGateway/Features/Completions/CompleteStream/Endpoint.cs:57,69,122,133

$ grep -rn "catch (" --include=*.cs src/platform/backend/Services/LlmGateway | grep -v "/Tests/"
Features/Completions/Complete/Endpoint.cs:83
Features/Completions/CompleteStream/Endpoint.cs:117
Features/Embeddings/Embed/EmbeddingEndpoints.cs:78
Infrastructure/ExternalServices/AnthropicContentBlockSanitizer.cs:50
```

### 母集合 — 失敗を計上している箇所と、そこへ渡っている情報

| # | 位置 | `llm.result` | 例外が手元にあるか | 上流ステータスを取れるか |
| --- | --- | --- | --- | --- |
| 1 | `Complete/Endpoint.cs:93`（`catch` 内・フォールバックする側） | `fallback` | **あり** | **取れる**（`LlmFallbackPolicy.StatusCodeOf(ex)`。ログには既に `{Status}` として出ている） |
| 2 | `Complete/Endpoint.cs:105`（`catch` 内・フォールバックしない側。**429 はここ**） | `upstream_error` | **あり** | **取れるのに取っていない** —— ログは `{Endpoint}` `{Model}` だけ、メトリクスは軸なし |
| 3 | `CompleteStream/Endpoint.cs:122`（`catch` 内。ストリームは鎖を持たない） | `upstream_error` | **あり** | **取れるのに取っていない**（#2 と同型） |
| 4 | `Complete/Endpoint.cs:48` ／ `CompleteStream/Endpoint.cs:69` | `provider_missing` | 例外なし | 上流を叩いていない（該当なし） |
| 5 | `Complete/Endpoint.cs:36` ／ `CompleteStream/Endpoint.cs:57` | `egress_denied` | 例外なし | 上流を叩いていない（該当なし） |

**#2 と #3 が本 issue の対象**である。#1 は既にログに出ているが**メトリクスには無い**ため、
軸を足すなら同時に載せる（下記「決定」）。

### 除外したものと理由

- `Features/Embeddings/Embed/EmbeddingEndpoints.cs:78`（埋め込み経路の `catch`）——
  **そもそも `LlmCompletionMetrics` を一切呼んでいない**（上の `RecordCompletion` 走査に現れない）。
  埋め込み経路の同種メトリクスは `docs/observability/llm-completion-metrics.md` §未決事項に
  既出の別件であり、本 issue の宣言ファイル領域にも入らない。**本作業では触らない。**
- `Infrastructure/ExternalServices/AnthropicContentBlockSanitizer.cs:50`（`catch (JsonException)`）——
  応答本文のサニタイズ内部の握り潰しであり、呼び出しの成否とは別レイヤ。計上箇所ではない。
- `LlmUsageMetrics`（`llm.tokens.total` / `llm.cost.total`）—— **成功した呼び出ししか計上しない**
  （失敗にトークンも金額も無い）。失敗の軸を持つ計器ではない。
- `src/ai-stock-trading`（submodule。本リポジトリでは修正できない）。

### 「上流ステータスの軸が無い」ことの確認（陰性結論に陽性対照を対で置く）

```
$ grep -rniE "status_code|StatusTag|http\.response" --include=*.cs \
        src/platform/backend/Services/LlmGateway | grep -v "/Tests/"
Common/Observability/LlmUsageMetrics.cs:39    public const string PricingStatusTag = "llm.pricing_status";
Common/Observability/LlmUsageMetrics.cs:114   { PricingStatusTag, ... }
Features/Completions/CompleteStream/Endpoint.cs:36,37,40,44,45   （SSE の応答ヘッダ。計器ではない）
```

**陽性対照**: 同じ走査条件で既存タグは見つかる —— `grep -rn "StopReasonTag" …` は **11 件**、
`llm.pricing_status`（費用系の軸）は上のとおり **1 件**ヒットする。
**走査は「タグ定数を見つけられる」ことが示されており、そのうえで補完カウンタ側に
HTTP ステータスの軸は 1 件も無い。** issue 本文の実測（稼働 image のタグ一覧 9 種）とも一致する。

## 決定（実装方針）

### 決定 1: `llm.result` を増やさず、**直交する 7 本目の軸**を足す

- `llm.result` に `rate_limited` を足す案は採らない。足すと 429 が `upstream_error` から**抜け**、
  **既存のしきい値方針（`upstream_error` 率 > 10% が 10 分継続 → critical）の意味が黙って変わる**。
  運用文書とダッシュボードの解釈をすべて引き直す必要が出る。
- `llm.result` は「**基盤側が何をしたか**」（送った／拒んだ／落とした／諦めた）、
  `llm.upstream_status` は「**上流が何を返したか**」であり、軸として独立している。
  除外したいときは `{llm_result="upstream_error", llm_upstream_status!="rate_limited"}` と書ける。

### 決定 2: 値域は 6 値に閉じる（生ステータスを載せない）

| 値 | 条件 |
| --- | --- |
| `none` | 上流の失敗ではない計上（`sent` / `egress_denied` / `provider_missing`） |
| `rate_limited` | 上流ステータス **429** |
| `client_error` | 400–499（**429 を除く**） |
| `server_error` | 500–599 |
| `transport` | ステータスが取れず、**ネットワーク層の失敗の形をしている**（`HttpRequestException`
  でステータス無し・`SocketException`・`TimeoutException`・`IOException`）。`TaskCanceledException` は
  **入れない** —— 両エンドポイントの `catch` が `ex is not OperationCanceledException` で弾いており、
  ここへは届かない（届かない分岐を書くと「見ているつもり」の死んだコードになる） |
| `other` | ステータスが取れず、ネットワーク層でもない（例: `Llm:SelfHosted:BaseUrl` 未設定の
  `InvalidOperationException`、応答の逆直列化失敗） |

**`transport` と `other` を分ける**のは、設定ミス（`BaseUrl` 未設定）を「呼び出し先の通信障害」として
数えると、**直せる対象を取り違える**ためである。`other` は既存の未知値集約先（`ValueOther`）と同じ語である。

### 決定 3: 分類は**メトリクス側でだけ**行い、判定器は `LlmFallbackPolicy` を使い回す

`LlmFallbackPolicy.StatusCodeOf(ex)`（例外連鎖から HTTP ステータスを取り出す唯一の実装）を呼ぶ。
**ステータス抽出を二重に書かない**（IADR-0225 が定めたフォールバック判定と同じ一次情報から導く）。
フォールバックするか否かの**判定そのものは複製しない** —— `ShouldFallBack` は呼ばない。

### 決定 4: `RecordCompletion` は**文字列ではなく例外**を受け取る

`RecordCompletion(..., Exception? failure = null)` とし、タグ値は**分類器の戻り値以外にはなり得ない**
ようにする。文字列を受ける口にすると、呼び出し側が `ex.Message`（プロンプト断片・利用者識別子を
含み得る）を渡せてしまう。**プロンプト本文・利用者識別子・エンドポイント URL は属性にしない**
（IADR-0110 の設計原則、IADR-0306 のログ衛生と同じ向き）。

### 決定 5: Histogram（`llm.completion.output_tokens`）へは載せない

Histogram は**送信が成立した経路だけ**に記録するため `llm.upstream_status` は常に `none` になり、
系列を分けない。既に `llm.result` を同じ理由で落としている（IADR-0212 決定 2）。同じ扱いにする。

### 決定 6: `fallback` 行にも軸を載せる（常に `client_error`）

フォールバックは 400 系でのみ発火し 429 を除く（ADR-0038 決定 4）ため、`fallback` 行の
`llm.upstream_status` は構成上 `client_error` に限られる。**系列は増えない。**
「この試行に対して上流が何を返したか」を全行で真にするほうが、軸の意味が濁らない。

### 決定 7: 非フォールバック側のエラーログにも `{Status}` を構造化フィールドで残す

`Complete/Endpoint.cs` の `LogError` と `CompleteStream/Endpoint.cs` の `LogError` に
`LlmFallbackPolicy.StatusCodeOf(ex)` を足す（受け入れ基準 3）。値は整数（または未取得時 null）で、
利用者由来の文字列は載せない。

## カーディナリティの見積もり

**属性値の直積は増えるが、実現する組み合わせは直積ではない。**

| `llm.result` | 取り得る `llm.upstream_status` | 実系列の増分 |
| --- | --- | --- |
| `sent` / `egress_denied` / `provider_missing` | `none` のみ | **0**（`none` 1 値が付くだけ） |
| `fallback` | `client_error` のみ | **0** |
| `upstream_error` | `rate_limited` / `client_error` / `server_error` / `transport` / `other` | 最大 **×5** |

`upstream_error` 行は `stop_reason` が常に `none` であり、`purpose` × `model` × `provider` ×
`confidentiality` の実現組み合わせも障害時に限られる。**増分は `upstream_error` の断面だけに掛かり、
上限は現状の 5 倍**である。生ステータス（未知の値域・上流の仕様変更で増える）を載せる案は採らない。

## 変更するファイル（宣言ファイル領域）

| ファイル | 変更 |
| --- | --- |
| `src/platform/backend/Services/LlmGateway/Common/Observability/LlmCompletionMetrics.cs` | タグ定数 6 値 ＋ 分類器 ＋ `RecordCompletion` の引数追加 |
| `src/platform/backend/Services/LlmGateway/Features/Completions/Complete/Endpoint.cs` | `fallback` / `upstream_error` の計上へ例外を渡す。`LogError` に `{Status}` |
| `src/platform/backend/Services/LlmGateway/Features/Completions/CompleteStream/Endpoint.cs` | 同上（鎖なし経路） |
| `src/platform/backend/Services/LlmGateway/Tests/Common/Observability/CompletionMetricsTests.cs` | 陽性 / 陰性 / 変異試験 |
| `.ai-context/adr/IADR-0374_*.md` ＋ `.ai-context/adr/README.md` | 実装 ADR と索引 |
| `docs/observability/llm-completion-metrics.md` | 属性表・PromQL・#380 との関係 |
| `docs/operations/operations.md` | 監視観点への追記（1 段落） |
| `docs/tests/FR-11_llm-egress-routing.md` | T-21 の追補（新テスト） |

## テスト計画

| ID | 種別 | 内容 | 期待 |
| --- | --- | --- | --- |
| T-1091a | 陽性 | `/complete` で上流が **429** を返す | `llm.result=upstream_error` かつ **`llm.upstream_status=rate_limited`** |
| T-1091b | 陰性 | 上流が **500** | `llm.upstream_status=server_error`（`rate_limited` ではない） |
| T-1091c | 陰性 | ステータスの取れない**通信断**（`HttpRequestException` ステータス無し） | `llm.upstream_status=transport` |
| T-1091d | 陰性 | ステータスも通信でもない例外（`InvalidOperationException`） | `llm.upstream_status=other` |
| T-1091e | 値域 | 400（フォールバックする側） | `fallback` 行が `client_error`、`sent` 行が `none` |
| T-1091f | 値域 | 送信成立・越境拒否 | `llm.upstream_status=none` |
| T-1091g | 値域 | **生ステータスを載せない** | 全測定のタグ値が上の 6 値の集合に含まれる（`"429"` という値が出ない） |
| T-1091h | ストリーム | `/complete/stream` で 429 | 非ストリームと同じ属性 |
| T-1091i | **変異** | 分類器から `rate_limited` の枝を落とす（軸が `other` へ潰れる） | T-1091a が**落ちること**を手で確認し、結果を記録する |

計器の観測手段は既存の `MeterListener` プローブ（`CompletionMetricsTests.MetricsProbe`）を使う ——
**IADR-0244 が「メトリクスは外から観測可能」と実測で確定させた面**であり、新しいシームは要らない。

## 受け入れ基準（issue の 5 項目に対応）

1. 429 が他の失敗と**メトリクスだけで**区別できる → `sum by (llm_upstream_status) (llm_completion_total{llm_result="upstream_error"})`
2. 値域が閉じていることをテストで固定する → T-1091g
3. 非フォールバック側のエラーログに上流ステータスを構造化フィールドで残す → 決定 7
4. 選んだ形と理由を実装 ADR に残す → IADR-0374
5. #380 の受け入れ基準 ③ を「計器の側では満たした」と明示する → PR 本文と IADR §結果

## #380 の基準 ③ に対する効き方（先に限界を書く）

計器は満たすが、**「429 が発生していない」と結論するにはこれだけでは足りない。**
Prometheus は起きていないラベル値を 0 として持たないため、
`llm_completion_total{llm_upstream_status="rate_limited"}` の**空ベクタは「無い」の証拠にならない**。
`llm_upstream_status="none"` の系列が同期間に**実在すること**（＝計器が動いていること）を
**陽性対照**として同時に示して初めて「429 は 0 件だった」と読める。この作法を IADR と
可観測性仕様書へ書く。**実測そのものは #380 に残る。**

## 実測（稼働 k3s）の計画

- LlmGateway の image だけ差し替える（`kubectl set image`）。**他の Pod は再起動しない。**
- **実 LLM は呼ばない。** `Llm__ApiKey` は空のまま。ティア A（`selfhosted`）エンドポイントを
  有効化し、`Llm__SelfHosted__BaseUrl` を**クラスタ内の使い捨てスタブ**へ向け、
  スタブが 429 / 500 / 接続不能を返す。外部への実トラフィックは 0。
- 収集経路は現状で既に転送構成である（稼働 `otel-collector-config` の metrics パイプラインは
  `[prometheusremotewrite, debug]`）。**本作業で転送を有効化していないので、fail-safe へ戻す操作もしない**
  （IADR-0345 の作法は「自分で一時的に有効化したら戻す」）。
- `deploy/local/observability/prometheus.yaml` を単体 apply しない（PVC が外れて TSDB を失う。#1202 の実測）。
- 事後にスタブと環境変数を撤去し、image を元へ戻す。

## 実測の結果（2026-09-05・稼働中の Rancher Desktop k3s）

### 構成（実 LLM を呼んでいないことの根拠）

- `Llm__ApiKey` は**空のまま**。ティア B（`claude-managed`）を `Enabled=false` にし、
  ティア A（`selfhosted-oss`）を `Priority=1` でクラスタ内スタブ（Caddy・`respond <code>`）へ向けた。
  → **候補集合に外部プロバイダが 1 つも無い状態**で叩いた。外部への実トラフィックは 0。
- LlmGateway の image のみ差し替え（`kubectl set image` → `…/llm-gateway:issue1091`）。
  **他の Pod は再起動していない。**
- 収集経路は**本作業の前から**転送構成（稼働 `otel-collector-config` の metrics パイプラインが
  `[prometheusremotewrite, debug]`）。自分で有効化していないので fail-safe へ戻す操作もしていない。
- `deploy/local/observability/prometheus.yaml` は**単体 apply していない**（PVC が外れて TSDB を失うため）。

### (a) 陽性 —— 429 が系列として実在する

```
$ sum by (llm_result, llm_upstream_status, llm_purpose) (llm_completion_total)
{llm_purpose="trade-decision", llm_result="upstream_error", llm_upstream_status="rate_limited"} 6
{llm_purpose="rag-answer",     llm_result="upstream_error", llm_upstream_status="rate_limited"} 3
{llm_purpose="trade-decision", llm_result="upstream_error", llm_upstream_status="server_error"} 4
```

`/complete` を 6 回・`/complete/stream` を 3 回（いずれも 429）→ `rate_limited` 9 件。
**ストリーム経路でも同じ軸で計上される。** スタブを 500 へ切り替えた 4 回は `server_error` で、
**429 とは別系列**になった（陰性対照 1）。

### (b) 陰性 —— 生ステータスは系列に現れない

```
$ llm_completion_total{llm_upstream_status="429"}   → []
$ llm_completion_total{llm_upstream_status="500"}   → []
```

**この 2 つの空ベクタは (a) と同じ時点のものである。** 陽性が 9 件・4 件出ている状態で
`"429"` / `"500"` が空 ——「走査が空振りしている」のではなく「その値が存在しない」と読める。

### (c) 受け入れ基準 3（ログ）

```
$ kubectl -n microservices-platform logs deploy/llmgateway-service -c llmgateway-service | grep "LLM call failed"
LLM call failed at endpoint selfhosted-oss (oss-llm) (upstream status 429)
LLM call failed at endpoint selfhosted-oss (oss-llm) (upstream status 500)
```

**非フォールバック側**のエラーログに上流ステータスが構造化フィールドで載った。

### (d) 原状復帰

スタブ（ConfigMap / Deployment / Service）を削除し、`kubectl set env … -` で追加した 4 つの環境変数を
外し、image を `…/llm-gateway:latest` へ戻した。復帰後の env は変更前と同じ 6 件、image も同一である
（`kubectl get deploy -o jsonpath` で確認）。

### (e) 変異試験

| # | 変異 | 結果 |
| --- | --- | --- |
| 1 | 分類器から `rate_limited` の枝を削除 | **429 の 2 テストだけが落ちた**（`/complete`・`/complete/stream`）。500 / transport / other / 値域の各テストは通ったまま |
| 2 | 分類を止めて生ステータス（`status.ToString()`）を返す | **値域テスト 5 ケースが全部落ちた**（`{"none","rate_limited",…}` に `"429"` 等は含まれない） |

いずれも変異を戻し、`LlmGateway.Tests` 244 件緑を再確認した。

## 受け入れ基準の判定

| # | 基準 | 判定 | 根拠 |
| --- | --- | --- | --- |
| 1 | 429 をメトリクスだけで区別できる | ✅ | 実測 (a) |
| 2 | 値域が閉じていることをテストで固定 | ✅ | T-1091g（5 ケース）＋ 変異 2 |
| 3 | 非フォールバック側のログに上流ステータス | ✅ | 実測 (c) |
| 4 | 選んだ形と理由を実装 ADR に残す | ✅ | `.ai-context/adr/IADR-0374_llm-upstream-status-metric-axis.md` |
| 5 | #380 基準 ③ を「計器の側では満たした」と明示 | ✅ | 本書 §#380 との関係・IADR §結果・PR 本文 |

## 変更したもの

| ファイル | 変更 |
| --- | --- |
| `src/platform/backend/Services/LlmGateway/Common/Observability/LlmCompletionMetrics.cs` | `llm.upstream_status` の定数 6 値・値域集合・分類器・`RecordCompletion` の `failure` 引数・Histogram からの除外 |
| `src/platform/backend/Services/LlmGateway/Features/Completions/Complete/Endpoint.cs` | `fallback` / `upstream_error` の計上へ例外を渡す。`LogError` に `{Status}` |
| `src/platform/backend/Services/LlmGateway/Features/Completions/CompleteStream/Endpoint.cs` | 同上（鎖なし経路） |
| `src/platform/backend/Services/LlmGateway/Tests/Common/Observability/CompletionMetricsTests.cs` | T-1091a〜i（新規 9 本＋Theory 5 ケース）とスタブ 2 種 |
| `.ai-context/adr/IADR-0374_llm-upstream-status-metric-axis.md` ＋ `.ai-context/adr/README.md` | 実装 ADR と索引 |
| `docs/observability/llm-completion-metrics.md` | 属性表・PromQL・上流ステータスの節・陽性対照の作法・しきい値表・未決事項 |
| `docs/operations/operations.md` | 監視観点への追記 |
| `docs/functional/FR-11_llm-egress-routing.md` / `docs/tests/FR-11_llm-egress-routing.md` | 可観測性の記述と T-26 |

**`deploy/` は 1 行も変更していない**（#1159 と衝突しない。実測用のスタブは使い捨てでリポジトリに入れていない）。
