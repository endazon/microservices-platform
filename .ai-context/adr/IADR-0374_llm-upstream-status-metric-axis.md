---
title: IADR-0374 上流 HTTP ステータスは llm.result と直交する 6 値の軸として持ち、生ステータスは載せない
type: impl-adr
status: Accepted
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
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0038_analysis-purpose-drop-fable-5.md (決定 4・決定 6)
  - planning:projects/microservices-platform/07_adr/ADR-0044_llm-usage-metrics-and-pricing-table.md (決定 1)
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR-21)
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md
---

# IADR-0374: LLM 失敗メトリクスの上流ステータス軸（#1091）

- 状態: Accepted
- 日付: 2026-09-05
- 決定者: claude（実装）

## 起点・関連

- 機能要求 **FR-11**（LLM 送信可否・呼び出し先切替）／非機能要求 **NFR-21**（障害検出 5 分以内）
- 計画 **ADR-0038** 決定 4（429 は再試行でありフォールバックではない）・決定 6（フォールバック発火の可観測化）、
  **ADR-0044** 決定 1（費用系と同じ軸で読めること）、**ADR-0006**（可観測性スタック）
- 実装 issue **#1091**（出所は #786 のコメントと #380 の実測）
- 先行: **IADR-0110**（終了理由カウンタと「値域を閉じる」規律）／**IADR-0212**（出力トークン Histogram）／
  **IADR-0225**（用途フォールバック鎖と 429 の境界）／**IADR-0306**（ログ衛生）／
  **IADR-0244**（メトリクスは外から観測可能という実測）
- 作業仕様書: `.ai-context/specs/20260905_issue-1091_llm-upstream-status-axis.md`

## コンテキストと課題

**分類はできていた。分類の結果を残していなかった。**

`LlmFallbackPolicy` は ADR-0038 決定 4 のとおり 429 を「フォールバックさせない失敗」として正しく
分類しており、`StatusCodeOf(ex)` という抽出器も既にある。にもかかわらず:

| 経路 | 上流ステータスの残り方 |
| --- | --- |
| フォールバックした側（400 系） | **ログにだけ** `{Status}` として出ていた |
| フォールバックしない側（**429 はここ**・5xx・通信断） | **どこにも残らない**。ログは `{Endpoint}` `{Model}` のみ |

メトリクスは `llm.result="upstream_error"` の一点で、属性は 6 本（`result` / `stop_reason` /
`purpose` / `model` / `provider` / `confidentiality`）。**HTTP ステータスの軸は無い。**
稼働 image のタグ一覧の実測（#1091 のコメント）も同じ 9 種（費用系を含む）で、ステータス軸は無かった。

🔴 **ADR-0038 決定 4 が分けた両側の観測可能性が非対称である。** 分けた意味は運用でしか回収できず、
その運用の目（`upstream_error` の内訳）が塞がっていた。

**効いた先**: #380 の受け入れ基準 ③（Opus 5 のレート制限枠の確認）が、**トラフィックが在っても
達成できない**。代替の Loki も使えない —— 稼働クラスタで `/loki/api/v1/labels` は空を返し、
仮に生きていても非フォールバック側のログにステータスが入っていないので同じ結論になる。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A（採用）** | **`llm.upstream_status` を 7 本目の属性として足す**。値域は `none` / `rate_limited` / `client_error` / `server_error` / `transport` / `other` の 6 値 | `llm.result` の意味を動かさずに 429 を切り出せる。増える系列は `upstream_error` の断面に限られる |
| B | `llm.result` に `rate_limited` を足す（429 を `upstream_error` から抜く） | 🔴 **既存のしきい値方針（`upstream_error` 率 > 10% が 10 分継続 → critical）の意味が黙って変わる。** 運用文書・ダッシュボード・過去データの解釈をすべて引き直すことになる |
| C | 生の `http.response.status_code`（数値）を載せる | 🔴 **値域が非有界。** 上流の仕様変更で系列が増え続ける。IADR-0110 が明示した設計原則（属性値の値域を閉じる）に正面から反する |
| D | メトリクスは触らず、ログにだけステータスを足して Loki で読む | 🔴 稼働クラスタの Loki にラベルが 1 つも無い。**「読める見込み」を根拠にした是正になる**。ログ側の是正（決定 7）は行うが、それだけでは基準 ③ を満たさない |
| E | 失敗専用の新しい計器（`llm.upstream_failures.total`）を立てる | 分母（総呼び出し数）が別計器になり、率を出すのに 2 計器を突き合わせることになる。ADR-0044 決定 1 の「同じ軸で読める」から遠ざかる |

## 決定

### 決定 1: `llm.result` を増やさず、**直交する軸**を足す（案 A）

`llm.result` は「**基盤側が何をしたか**」（送った／拒んだ／落とした／諦めた）、
`llm.upstream_status` は「**上流が何を返したか**」であり、独立した軸である。

```promql
# 429 だけを読む
sum by (llm_provider) (rate(llm_completion_total{llm_upstream_status="rate_limited"}[30m]))
# 429 を除いた呼び出し先障害（既存のしきい値の意図に近い側）
sum(rate(llm_completion_total{llm_result="upstream_error", llm_upstream_status!="rate_limited"}[30m]))
```

**既存のしきい値方針の数値は本 IADR では動かさない**（実測前の出発点であることは変わらない）。
変えるべきかどうかは、この軸で実際の内訳が見えてから判断する。

### 決定 2: 値域は 6 値に閉じる

| 値 | 条件 |
| --- | --- |
| `none` | 上流の失敗ではない計上（`sent` / `egress_denied` / `provider_missing`） |
| `rate_limited` | 上流ステータス **429** |
| `client_error` | 400–499（**429 を除く**） |
| `server_error` | 500–599 |
| `transport` | ステータスが取れず、ネットワーク層の失敗の形（`HttpRequestException` のステータス無し・`SocketException`・`TimeoutException`・`IOException`） |
| `other` | 上記のいずれでもない（`InvalidOperationException` 等。想定外のステータスもここへ集約する） |

🔴 **`transport` と `other` を分ける。** `Llm:SelfHosted:BaseUrl` 未設定は `InvalidOperationException`
であり、これを「呼び出し先の通信障害」として数えると**直す対象を取り違える**（設定を直すべき局面で
上流の障害対応を始める）。`other` は既存の未知値集約先（`ValueOther`）と同じ語を使う。

### 決定 3: 抽出は `LlmFallbackPolicy.StatusCodeOf` を使い回す。判定は複製しない

ステータス抽出を 2 箇所に書かない —— 片方だけが上流 SDK の変化に取り残される。
**`ShouldFallBack` は呼ばない**（IADR-0225 が定めたフォールバックの判定そのものは 1 箇所のままにする）。
本軸が答えるのは「上流が何を返したか」だけで、「それに対して何をするか」は引き続き
`LlmFallbackPolicy` が単独で決める。

### 決定 4: `RecordCompletion` は**文字列ではなく例外**を受ける

`RecordCompletion(..., Exception? failure = null)` とし、タグ値は分類器の戻り値以外にはなり得ない
ようにする。文字列を受ける口にすると、呼び出し側が `ex.Message`（プロンプト断片・利用者識別子を
含み得る）をそのまま渡せてしまう。**プロンプト本文・利用者識別子・エンドポイント URL は属性にしない**
（IADR-0110 の設計原則、IADR-0306 のログ衛生と同じ向き）。**API の形で禁止を強制する。**

### 決定 5: Histogram（`llm.completion.output_tokens`）には載せない

Histogram は送信が成立した経路にしか記録せず、その経路の値は常に `none` で系列を分けない。
`llm.result` を同じ理由で落としている（IADR-0212 決定 2）。同じ扱いにする。

### 決定 6: `fallback` 行にも軸を載せる（構成上つねに `client_error`）

フォールバックは 400 系でのみ発火し 429 を除くため、値は 1 つに定まる（**系列は増えない**）。
「この試行に上流が何を返したか」を全行で真にしておくほうが、軸の意味が濁らない。
`llm.result="fallback"` の情報と重複するが、**重複しているのは事実の写しであって判定ではない。**

### 決定 7: 非フォールバック側のエラーログにも `{Status}` を残す

`/complete` と `/complete/stream` の `LogError` に `LlmFallbackPolicy.StatusCodeOf(ex)` を足す。
**メトリクスは傾向、ログは個別**という役割分担（IADR-0110）を、失敗側でも成立させる。

## カーディナリティ

**属性値の直積は増えるが、実現する組み合わせは直積ではない。**

| `llm.result` | 取り得る `llm.upstream_status` | 系列の増分 |
| --- | --- | --- |
| `sent` / `egress_denied` / `provider_missing` | `none` のみ | **0**（1 値が付くだけ） |
| `fallback` | `client_error` のみ | **0** |
| `upstream_error` | 5 値 | 最大 **×5**（この断面だけ） |

`upstream_error` 行は `stop_reason` が常に `none` である。増分は障害時の断面に限られ、
現構成での実系列数は引き続き数十のオーダーに収まる。

## 実測（2026-09-05・稼働中の Rancher Desktop k3s）

**実 LLM は 1 度も呼んでいない。** `Llm__ApiKey` は空のまま、ティア B（`claude-managed`）を
`Enabled=false` にし、ティア A（`selfhosted-oss`）をクラスタ内の使い捨てスタブへ向けた。
外部への実トラフィックは 0 である（#380 の申し送り）。

- LlmGateway の image のみ `kubectl set image` で差し替えた（**他の Pod は再起動していない**）。
- 収集経路は本作業の前から転送構成だった（稼働 collector の metrics パイプラインは
  `[prometheusremotewrite, debug]`）。**自分で有効化していないので fail-safe へ戻す操作もしていない**
  （IADR-0345 の作法は「一時的に有効化したら戻す」であり、既に有効な構成を勝手に落とさない）。

### 陽性 —— 429 が系列として実在する

```
$ sum by (llm_result, llm_upstream_status, llm_purpose) (llm_completion_total)
{llm_purpose="trade-decision", llm_result="upstream_error", llm_upstream_status="rate_limited"} 6
{llm_purpose="rag-answer",     llm_result="upstream_error", llm_upstream_status="rate_limited"} 3
```

（`/complete` を 6 回、`/complete/stream` を 3 回。**ストリーム経路でも同じ軸で計上される。**）

ログ側（決定 7）も同時に確認した:

```
LLM call failed at endpoint selfhosted-oss (oss-llm) (upstream status 429)
```

### 陰性 —— 生ステータスは系列に現れない

```
$ llm_completion_total{llm_upstream_status="429"}
[]
```

**この空ベクタは「陽性が同時に出ていること」とセットでのみ意味を持つ。**
上の陽性が 9 件出ている同じ時点で `"429"` が空である、という対で読む。

### 陰性 —— 500 は 429 と別の値になる

スタブの応答を 500 へ切り替えて 4 回叩き、`llm_upstream_status="server_error"` が別系列として
現れることを確認した（`rate_limited` の 9 件は変化しない）。

### 変異試験（軸を落とすと陰性が落ちることの確認）

| # | 変異 | 結果 |
| --- | --- | --- |
| 1 | 分類器から `rate_limited` の枝を削除 | **429 の 2 テストが落ちた**（`/complete` と `/complete/stream`）。500 / transport / other の各テストは通ったままで、**落ちたのは 429 を見ているものだけ**である |
| 2 | 分類を止めて生ステータス（`status.ToString()`）を返す | **値域テスト 5 ケースが全部落ちた**（`{"none","rate_limited",…}` に `"429"` `"500"` `"400"` `"404"` `"418"` は含まれない） |

いずれも変異を戻して 244 件緑を再確認した。

## 結果

### 良い影響

- **429 を他の失敗とメトリクスだけで区別できる。** #380 の受け入れ基準 ③ は、**計器の側では満たした**。
- **ADR-0038 決定 4 が分けた両側の観測可能性が対称になった。** フォールバックした側だけが
  観測できるという非対称が解けた。
- 設定ミス（`other`）と呼び出し先の通信障害（`transport`）が分かれ、**直す対象を取り違えない。**
- 既存のダッシュボード・アラート方針は**式を変えずにそのまま動く**（`llm.result` を触っていない）。

### 悪い影響 / トレードオフ

- 障害時の系列数が最大 5 倍になる断面がある（`upstream_error` の断面のみ）。
- **生のステータスはメトリクスからは分からない。** 404 と 418 はどちらも `client_error` である。
  原文が要る調査はログ（決定 7 で足した `{Status}`）で行う。
- `fallback` 行の `client_error` は `llm.result` と情報が重複する。

### 🔴 「429 が無い」と読むときの作法

**空ベクタは「無い」の証拠にならない。** Prometheus は起きていないラベル値を 0 として持たない。

```promql
# 分子（429 が起きたか）— 空ベクタは「起きていない」とも「計器が死んでいる」とも読める
sum(increase(llm_completion_total{llm_upstream_status="rate_limited"}[7d]))
# 陽性対照（同じ期間に計器が動いていたか）— これが非空でなければ上の空は何も意味しない
sum(increase(llm_completion_total{llm_upstream_status="none"}[7d]))
```

**この 2 本を対で出して初めて「429 は 0 件だった」と読める。** #380 の実測でこの作法を守る。

### フォローアップ

1. **#380 の受け入れ基準 ③ の実測そのもの**は #380 に残る（実 API キーと、費用が出る負荷投入の
   人の許可が要る。本 IADR はその手段を用意しただけである）。
2. `upstream_error` 率のしきい値（> 10% / 10 分 / critical）を **429 を除いた式へ改めるか**は、
   内訳の実測を得てから判断する。**実測前に数値も式も動かさない。**
3. 埋め込み経路（`/embeddings`）は本軸を持たない。そもそも補完カウンタを一切呼んでいないためであり、
   同種メトリクスの新設は可観測性仕様書の未決事項として既出である。
4. Grafana ダッシュボードへの内訳パネル追加（`sum by (llm_upstream_status)`）。既存パネルは
   式を変えずに動くため、本作業では触っていない。

## 関連

- 可観測性仕様書: `docs/observability/llm-completion-metrics.md`
- 運用仕様書: `docs/operations/operations.md`（監視・アラート）
- テスト仕様書: `docs/tests/FR-11_llm-egress-routing.md`（T-21 / T-1091）
