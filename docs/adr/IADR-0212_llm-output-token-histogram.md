---
title: IADR-0212 出力トークンを明示バケットの Histogram で計り、送信が成立した経路だけに記録する
type: impl-adr
status: Accepted
related_ids:
  - NFR-19
  - FR-11
  - ADR-0006
  - IADR-0101
  - IADR-0104
  - IADR-0110
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md"
---

# IADR-0212: LLM 出力トークンの Histogram（明示バケット・送信成立時のみ記録）

- 状態: Accepted
- 日付: 2026-08-16
- 決定者: claude（実装）

## 起点・関連

- Issue: **#786**。仕様書: `../specs/20260816_issue-786_llm-output-token-histogram.md`。
- 実装 ADR: [[IADR-0110]]（`llm.completion.total` を置いた決定・**本 ADR が同じ Meter へ計器を 1 本足す**）、
  [[IADR-0104]]（終了理由の語彙）、[[IADR-0101]]（既定 `max_tokens` = 4096）。
- **[[IADR-0110]] を Supersede しない。** 決定はすべて生きており、本 ADR は計器を足すだけである。

## コンテキストと課題

**#786 は「メトリクスが 1 系列も出ていない」として起票され、起票時は「`Meter` が `MeterProvider` に
登録されていない疑い」と書いた。これは誤りだった。** `Program.cs:29` に
`AddMeter(LlmCompletionMetrics.MeterName)` があり配線は健全で、**真因は `/complete` への無トラフィック**
（OTel の Counter は最初の `Add()` まで export されない）。

訂正の結果、残った問いが本 ADR の対象である —— **出力トークンの計器を足すか**。

[[IADR-0101]] は既定 `max_tokens` を 4096 と決め、**「実運用値で再調整する」を #380 へ預けている**。
#380 の受け入れ基準 ① は**出力トークンの実測**を求めるが、`llm.completion.total` は
`stop_reason=max_tokens` の**回数**しか持たない。「上限に張り付いているのか、余裕があるのか」は
**分布**でしか読めない。

## 決定

### 1. 出力トークンの Histogram を足す（#443 へ送らない）

`llm.completion.output_tokens`（`unit: {token}`）を `LlmCompletionMetrics` へ足す。

**送る理由が無い。** #443 は可観測性の再実装（XL）だが、本件は**取得側の実装が要らない**:

| プロバイダ | 出力トークンの取得元 |
| --- | --- |
| `ClaudeProvider` | SDK の `msg.Usage.OutputTokens`（ストリームは最終チャンク） |
| `CopilotProvider` | OpenAI 互換 `usage.completion_tokens` |
| `SelfHostedProvider` | 同上 |

**3 プロバイダすべてで既に値が取れており**、応答 DTO（`CompletionResult` / `CompletionApiResponse`）にも
載っている。実機でも `outputTokens: 16` が返ることを確認した。

### 2. 属性は Counter の 6 つから `llm.result` を落とした 5 つ

`llm.result` は Histogram では**常に `sent`** になり（決定 3）、系列を分けない。載せても情報が増えず、
バケット × 属性の直積を無駄に広げるだけである。

残す 5 つ（`llm.stop_reason` / `llm.purpose` / `llm.model` / `llm.provider` / `llm.confidentiality`）は
[[IADR-0110]] が**値域を閉じた**もの（未知値は `other`、null は `none` へ集約）をそのまま流用する。
とくに `llm.stop_reason` は `max_tokens` と `end_turn` で分布が明確に違うはずで、**本計器の主軸**である。

**組み立ては 1 箇所に集約する。** `RecordCompletion` の中で `TagList` を 1 度作り、
Histogram 側はそこから `llm.result` だけを外して使う。2 箇所で組み立てると片方が腐る。

### 3. ★ 記録点は Counter と非対称にする —— 送信が成立した経路だけ

**[[IADR-0110]] の Counter は全 8 経路で計上する**（未送信も計上しないと拒否率の分母が欠ける）。
**Histogram は `result=sent` の 2 経路だけで記録する。**

| 経路 | Counter | Histogram | 理由 |
| --- | :---: | :---: | --- |
| `/complete` 送信成立 | ○ | **○** | `result.OutputTokens` がスコープに在る |
| `/complete/stream` 送信成立 | ○ | **○** | 最終チャンクから確定した `outputTokens` |
| egress 拒否（2 経路） | ○ | × | プロバイダを呼んでいない |
| プロバイダ未登録（2 経路） | ○ | × | 同上 |
| upstream 例外（2 経路） | ○ | × | 応答が返っていない |

**0 を記録して埋めない。** 埋めると分布の最下段が「**短い応答**」と「**応答が無かった**」の混合になり、
上限到達の判断が濁る。呼び出し回数は Counter が持っているので、分布側で欠ける情報は無い。

実装上は `RecordCompletion(..., int? outputTokens = null)` とし、**既定 `null` = 記録しない**。
6 経路は呼び出しを 1 文字も変えていない。

### 4. バケット境界は明示する（`InstrumentAdvice`）

```
0, 16, 64, 128, 256, 512, 1024, 2048, 3072, 4096, 8192
```

**上限付近を細かく刻む**（2048 / 3072 / 4096）—— [[IADR-0101]] の 4096 が妥当かは
「**上限のすぐ下に山があるか**」で読むためである。4096 超は既定を上げた場合の観測用に 1 段だけ置く。

OTel の既定バケット（`0, 5, 10, 25, 50, 75, 100, 250, 500, 750, 1000, 2500, 5000, 7500, 10000`）は
**1000〜2500 が空きすぎ**で、4096 の判断に使えない。

**これはリポジトリ初の Histogram である**（`Histogram<` は走査して 0 件だった）。
先例が無いので、境界の意図をここに残す。

### 5. 入力トークンは足さない

#380 の基準 ① が求めるのは出力側である。入力側はプロンプト設計という別の問いに属し、
同じ計器へ混ぜると「どちらの分布を見ているか」が読みにくくなる。要るなら別 issue で足す。

## 影響・トレードオフ

- **系列が増える。** Histogram は Prometheus 上でバケットごとに系列になるため、
  属性の組み合わせ 1 つにつき 11 + `_sum` + `_count` の系列を持つ。値域は閉じているが、
  Counter より重い。dev 規模では問題にならず、重くなったら属性を削れる（増やすより削る方が安全）。
- **`?? 0` の合流は解消しない。** `CopilotProvider` / `SelfHostedProvider` は
  「upstream が `usage` を返さなかった」と「本当に 0」を区別できない（`payload?.Usage?.CompletionTokens ?? 0`）。
  **現状のまま記録する** —— 0 が積み上がれば異常として読める。区別が要るなら別 issue。

## 検出しないこと（明示）

- **系列が Prometheus に現れるかは CI では検査しない**（稼働クラスタと実トラフィックが要る）。
  単体テストが固定するのは「記録される／されない」と属性の形までである。
- **#380 は本 ADR では閉じない。** 計器が在っても、**同一ラベル組み合わせで反復するトラフィック**が
  保持期間のあいだ蓄積していなければ `rate()` は 0 を返す（実測。仕様書 §2 ②）。
  #380 のゲートには**トラフィックの条件**も要る。

## 代替案

| 案 | 採否 |
| --- | --- |
| **#443 へ送る** | 却下（決定 1）。取得側の実装が要らず、#380 が待っている |
| **Counter と同じく全 8 経路で 0 を記録** | 却下（決定 3）。分布の最下段が混合になる |
| **OTel の既定バケット** | 却下（決定 4）。1000〜2500 が空きすぎて 4096 の判断に使えない |
| **入力トークンも同時に足す** | 却下（決定 5）。別の問い |
| **`llm.result` も属性に載せる** | 却下（決定 2）。常に `sent` で系列を分けない |
