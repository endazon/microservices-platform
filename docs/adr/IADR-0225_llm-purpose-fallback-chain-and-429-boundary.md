---
title: IADR-0225 用途別フォールバック順序は設定駆動の鎖として持ち、発火は 400 系に限り 429 を除外する
type: impl-adr
status: Accepted
related_ids:
  - ADR-0038
  - ADR-0010
  - FR-11
  - UC-02
  - IADR-0007
  - IADR-0022
  - IADR-0102
  - IADR-0104
  - IADR-0110
  - IADR-0111
  - IADR-0164
author: claude
created: 2026-08-18
updated: 2026-08-18
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0038_analysis-purpose-drop-fable-5.md (Accepted・決定 3・4・6 の実装)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md (Accepted・ルーティング機構)"
  - "../../planning/projects/microservices-platform/06_technical/05_observability-ops.md (用途別・モデル別の利用実績)"
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0017_llm-fallback-policy.md (ADR-0038 決定 3 が「同一の考え方」として引く AST 側の方針)"
---

# IADR-0225: 用途別フォールバック順序は設定駆動の鎖として持ち、発火は 400 系に限り 429 を除外する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-08-18
- 決定者: claude（#863 / 計画 `ADR-0038` の決定 3・4・6 の実装）

## 起点・関連

- 関連する計画書 ID: `ADR-0038`（`Accepted`）決定 3・4・6 ／ `ADR-0010` ／ `FR-11` ／ `UC-02`
- 関連する実装仕様書: [作業仕様書 #863](../specs/20260818_issue-863_adr-0038-fallback-order-and-429.md)、
  [機能仕様書 FR-11](../functional/FR-11_llm-egress-routing.md)、
  [テスト仕様書 FR-11](../tests/FR-11_llm-egress-routing.md)、
  [可観測性仕様書](../observability/llm-completion-metrics.md)、
  [ピン Runbook](../operations/llm-model-pin-runbook.md)

## コンテキストと課題

計画 `ADR-0038`（`Accepted`）は 6 つの決定を持つ。#850 / PR #859 が反映したのは**決定 1・2 だけ**である。

| 決定 | 内容 | 本 ADR 起案時点 |
| --- | --- | --- |
| 1 | `analysis` = `claude-opus-5` | 反映済み |
| 2 | `claude-fable-5` を利用許可集合から外す | 反映済み |
| **3** | **`analysis` のフォールバック順序を `claude-opus-5` → `claude-sonnet-5` と定める** | **未反映** |
| **4** | **429 は再試行でありフォールバックではない。フォールバックは HTTP 400 系で発火する** | **未反映** |
| 5 | フォールバック先は利用許可集合に残す | 値としては満たされていた（`claude-sonnet-5` は `Models` に在る） |
| **6** | **フォールバックの発火を可観測にする** | **未反映** |

**着手前の実測（2026-08-18）**: 現行 `LlmRouter` は「明示要求モデル → 用途別モデル → `DefaultModel` →
適格モデルの先頭」という**解決順序**を持つだけで、**HTTP 400 系での実行時フォールバック機構を持たない**。
`CompletionEndpoints` の `catch` は例外の種類も HTTP ステータスも見ずに `upstream_error` へ落とす。
`docs/operations/llm-model-pin-runbook.md`（2026-08-11）も「フォールバック機構は実装されていない」と
実測で記録していた。**したがって決定 3・4・6 は設定の追加では満たせず、機構の新設を伴う。**

決めるべきことは 4 つある。

1. **順序をどこに持つか**（設定か、コードか）。
2. **どの失敗で発火させるか**（決定 4 の 429／400 系の区別を、実装がそもそも識別できるか）。
3. **どこで発火させるか**（ルーターか、エンドポイントか。ストリーム経路を含めるか）。
4. **発火をどう可観測にするか**（新しい計器か、既存計器の属性か）。

### プロバイダ例外の形は測れる（決定 4 の前提条件）

`ClaudeProvider` にスタブ `HttpMessageHandler` を噛ませ、各ステータスに対する例外を観測した。

| 上流の応答 | 例外 | `StatusCode` |
| --- | --- | --- |
| 400 / 404 / 429 / 500 | `System.Net.Http.HttpRequestException` | `BadRequest` / `NotFound` / `TooManyRequests` / `InternalServerError` |

**`Anthropic.SDK` 4.0.0 は `HttpRequestException.StatusCode` を設定して投げる。**
OpenAI 互換プロバイダ（`SelfHostedProvider` / `CopilotProvider`）は `EnsureSuccessStatusCode()` を使い、
同メソッドが同じプロパティを設定する。**よって全プロバイダを 1 つの判定器で扱える** ——
決定 4 の「429 と 400 系を区別する」は、プロバイダごとの特別扱いなしに実装できる。

## 検討した選択肢

### A. 順序の持ち方

| # | 案 | 評価 |
| --- | --- | --- |
| **A1** | **`Llm:Routing:PurposeFallbackModels`（用途 → 順序つきモデル配列）を新設する（採用）** | [[IADR-0007]] の「呼び出し先は設定駆動」と同じ形。`PurposeModels` の兄弟キーとして読める。用途ごとに鎖の有無を分けられる（`trade-decision` に鎖を持たせない、が設定で表現できる） |
| A2 | `PurposeModels` の値を配列にする（第 1 候補も含めて 1 本の鎖にする） | 既存キーの型が変わる**破壊的変更**。`PurposeModels` を読む先（`LlmCompletionMetrics.NormalizePurpose` ・ピン Runbook の列挙コマンド・T-19 ガード）がすべて追随を要する。得るものは「鎖が 1 箇所に集まる」だけで、割に合わない |
| A3 | エンドポイントの `Models` の並び順をフォールバック順序とみなす | `Models` は「割当」ではなく**利用を許可する集合**である（[[IADR-0106]]）。順序に意味を持たせると、明示要求モデルのための登録が順序を壊す |

### B. 発火させる場所

| # | 案 | 評価 |
| --- | --- | --- |
| **B1** | **順序はルーターが解決し、発火はエンドポイントの再試行ループで行う（採用）** | 適格性（`Models` 登録・ZDR 除外）の判定は既に `LlmRouter` に在り、再利用できる。実際に投げるのはエンドポイントなので、失敗を見て次を投げるのもそこが自然 |
| B2 | ルーターが例外まで受け取り、内部で再試行する | ルーターが `ILlmProvider` を知ることになり、`ILlmRouter`（判定だけを返す純関数的な契約）が崩れる |
| B3 | プロバイダ（`ClaudeProvider`）の中で落とす | プロバイダはモデルを跨がない。エンドポイント間フォールバックへ広げるときに作り直しになる |

### C. 可観測化（決定 6）

| # | 案 | 評価 |
| --- | --- | --- |
| **C1** | **既存カウンタ `llm.completion.total` の `llm.result` に `fallback` を 1 値足す（採用）** | 計器が増えない。**既存の Grafana パネル `sum by (llm_result) (…)` に新系列が自動で現れる**ため、決定 6 の「用途別・モデル別の利用実績へ出す」がダッシュボードの変更なしに成立する。[[IADR-0164]] の「ゲートウェイのメトリクスは `llm.completion.total` の 1 本だけ」も偽にならない |
| C2 | 新カウンタ `llm.completion.fallback.total` を足す | 計器が 2 本になり、`scripts/scripts.repo.test.js` の「ダッシュボードの式のメトリクス名は `llm_completion_total` ただ 1 つ」という突合、[[IADR-0164]] の記述、Runbook の説明が同時に追随を要する。得るものは C1 と同じである |
| C3 | ログだけに出す | 「継続的に把握する手段が無い」という [[IADR-0110]] が解いた問題へ戻る。決定 6 は**利用実績へ出す**ことを求めている |
| C4 | 見送った呼び出しを従来どおり `upstream_error` として数える | **`upstream_error` 率 > 10%（critical）のアラート方針が誤発火する**（回復した呼び出しを障害として数えるため） |

## 決定

1. **フォールバック順序は設定 `Llm:Routing:PurposeFallbackModels`（用途 → 順序つきモデル配列）で持つ**（A1）。
   ここに書くのは**第 2 候補以降**であり、第 1 候補は従来どおり `PurposeModels`（無ければ `DefaultModel`）である。
   本 ADR 時点の値は **`analysis: ["claude-sonnet-5"]` のみ**とする（`ADR-0038` 決定 3）。
2. **`LlmRouter` が鎖を解決し、`RoutingDecision.FallbackModels` に載せる。**
   解決規則は ①適格モデル（`EligibleModels`＝ZDR 除外を通した集合）に含まれるものだけ残す
   ②第 1 候補と同一のモデルは落とす ③重複を落とし設定の順序を保つ。
   **①で落とした要素は warn ログに出す** —— `ADR-0038` 決定 5 は「登録されていなければフォールバックは
   その場で失敗する」と述べており、黙って落とすと [[IADR-0102]] / [[IADR-0106]] が実際に踏んだ
   「無音失効」と同型の罠になる。
3. **発火の判定は `LlmFallbackPolicy` ただ 1 箇所で行う**（`ADR-0038` 決定 4）。

   | 上流ステータス | 判定 |
   | --- | --- |
   | **400〜499（429 を除く）** | **フォールバックする** |
   | **429** | **フォールバックしない** |
   | 5xx・ステータスの取れない失敗 | フォールバックしない（従来どおり `upstream_error` へ縮退） |

4. **再試行ループは非ストリーミング `/complete` にのみ置く**（B1）。
   **`/complete/stream` はフォールバックを実装しない** —— 鎖を持つ `analysis` は `/complete` を使い
   （`RagOrchestrator.AnalyzeAsync`）、ストリーム経路の用途 `rag-answer` の第 2 候補は計画
   `ADR-0038` §未決事項で**未確定**だからである。**ただし設定でストリーム用途に鎖が置かれた場合は
   warn を出す**（無音の穴にしない）。
5. **可観測化は `llm.result` に `fallback` を足すことで行う**（C1）。フォールバックした 1 リクエストは
   **2 回計上される** —— 見送った第 1 候補が `fallback`（`llm.model` は見送った候補）、成功した第 2 候補が
   `sent`（`llm.model` は実際に使った候補）。発火の遷移と上流ステータスは warn ログに残す。
6. **429 の「再試行」そのものは実装しない。** 本 ADR が実装するのは
   「**429 でフォールバックしない**」までである（理由は下記）。
7. **`trade-decision` に鎖を持たせない。** `docs/operations/llm-model-pin-runbook.md` が
   「別のモデルへ切り替えて取引判断を続けてはならない」と定めている（`AST/ADR-0011` の再現性・監査可能性）。
   **設定に鎖が足されたら落ちるテストを置く**（禁止の記述だけでは破られる、という MSP #382 の懸念への手当て）。

### `ADR-0038` 決定 5 のガードは新設せず、既存の 1 本を広げた

決定 5（フォールバック先が `Models` に登録済みであること）の自動テストは、既存の T-19
`PurposeModels_AreAllRegisteredInClaudeEndpointModels` の**射程を `PurposeFallbackModels` へ広げる**形で
満たす（名前は `PurposeModelsAndFallbacks_AreAllRegisteredInClaudeEndpointModels` へ改めた）。
**並行するガードを新設しない** —— 同じ不変条件を 2 本で守ると片方が古くなる。
#850 が `PurposeModels_AreNotListedAsNonZdr` で採ったのと同じ形である。

## 理由

- **順序を設定に置くのは、値が計画の裁定で動くからである。** `analysis` の割当は 2026-08-02 の裁定で
  1 度動いており（`ADR-0038` 決定 1）、第 2 候補も同じ経路で動きうる。コードに埋めると裁定のたびに再ビルドになる。
- **429 を除外するのは条文だからであり、同時に運用の禁止を守るためでもある。** ピン Runbook は
  「429 を『利用不能』と読んで別モデルへ逃がすと、上の禁止を実質的に破る」と明示している。
  **除外を機構の中心に置き、テストで固定した**（変異試験 §結果）。
- **429 の再試行を実装しないのは、方針が決まっていないからである。** 回数・バックオフ・`Retry-After` の
  扱いを計画側のどの文書も定めていない。**決めていない方針を実装が発明すると、後から計画が別の値を
  定めたときに「実装が先に決めた」ことになる**（`ADR-0031` が起こした事故と同じ向きの失敗である）。
  現行の 429 の挙動（`upstream_error` の縮退）は本 ADR で変えない。
- **`ADR-0038` §未決事項が未確定と明記している `default` / `rag-answer` の第 2 候補を補わない。**
  「決まっていることと決まっていないこと」の区別を実装側で失わせない。
- **計器を増やさないのは、増やすと追随点が 4 つ増えるからである**（ダッシュボード 2 種・突合テスト・
  [[IADR-0164]] の記述）。属性値 1 つの追加なら、**既存の `llm.result` 別パネルに新系列が出るだけで
  決定 6 が満たされる**。

## 結果

### 良い影響

- `analysis` が 400 系（モデル不可・コンテキスト超過等）で落ちても、**黙って `Sent=false` へ縮退せず
  `claude-sonnet-5` で回答が返る**（`ADR-0038` 決定 3）。
- **429 と 400 系が実装レベルで分かれた。** これまでは両方が同じ `catch` で `upstream_error` になっていた。
- **フォールバックが起きていることが運用に見える**（`llm_completion_total{llm_result="fallback"}`）。
  「いつの間にか常に第 2 候補で答えていた」という状態が検出できる。
- 鎖を持たない用途の挙動は 1 バイトも変わらない（ループが 1 回で終わる）。

### 悪い影響・トレードオフ

- **1 リクエストで上流を 2 回叩きうる。** 費用とレイテンシがその分増える。回数の上限は鎖の長さ
  （現在 1 段）であり、無制限ではない。
- **カウンタの「補完 1 回」が「上流呼び出し 1 回」の意味に寄った。** フォールバックが起きると
  `llm_completion_total` の総和がリクエスト数より大きくなる。**拒否率の分母（`llm.result="sent"`）は
  従来どおりリクエストあたり最大 1 件**であり、[[IADR-0110]] の拒否率の式は影響を受けない。
- **経路によって挙動が違う**（`/complete` は落ちる、`/complete/stream` は落ちない）。決定 4 の射程内で
  実運用の穴は無いが、読み手が取り違えうる。warn ログとテスト（`PostCompleteStream_Analysis_When400_DoesNotFallBack`）で明示した。
- **429 は依然として縮退する。** レート制限が続く間 `analysis` は答えを返せない。下記フォローアップ 1。
- **`StatusCodeOf` は例外の連鎖を単方向にしか辿らない**（`InnerException` を 1 本ずつ）。
  **`AggregateException.InnerExceptions`（複数）は辿らない。** 複数例外が束ねられる経路が生まれると、
  ステータスが取り出せず**判定が黙って外れる**（＝フォールバックしない側へ倒れる。fail-safe ではあるが無言である）。
  **今これを実装しないのは、束ねる経路が実在しないためである**（`CLAUDE.md`「起こり得ないケースへの
  防御的実装」の禁止）。実測（#867 のレビュー指摘を受けて引き直した）:

  ```console
  $ grep -rn 'Task.WhenAll\|\.Result\b\|\.Wait()\|AggregateException' \
      src/platform/backend/Services/LlmGateway/src/ | grep -v '/obj/\|/bin/'
  （出力なし。ヒットしたのは bin/ の依存 DLL だけである）
  ```

  **`Task.WhenAll` / `.Result` / `.Wait()` をプロバイダ呼び出しの周辺へ入れるときは、
  ここを併せて直すこと。**

### フォローアップ

1. **429 の再試行方針を計画側へ確認し、確定後に実装する**（回数・バックオフ・`Retry-After` の尊重）。
   `ADR-0038` 決定 4 は「429 は再試行である」と述べるだけで、再試行の形を定めていない。
2. **`default` / `rag-answer` の第 2 候補の確定**（`ADR-0038` §未決事項・同 §フォローアップ 5）。
   確定したら鎖へ足す。ストリーム経路（`rag-answer`）に鎖が付く場合は、決定 4 の射程を見直す。
3. フォールバック率のしきい値（アラート）。**実測前に数値を置かない** ——
   [[IADR-0110]] のしきい値と同じく運用開始後の実測で決める。

## 関連

- Supersedes: なし
- Superseded by: なし
- 前提とする実装 ADR: [[IADR-0007]]（設定駆動ルーティング。本 ADR は同じ形を踏襲する）／
  [[IADR-0022]]（ZDR 除外＝`EligibleModels`。鎖にも同じ適格性を適用する）／
  [[IADR-0110]]（`llm.completion.total` と属性の値域）／[[IADR-0111]]（応答が名乗る使用モデル）／
  [[IADR-0164]]（ゲートウェイのメトリクスは 1 本。本 ADR はこれを維持する）
- 触れない決定: [[IADR-0102]]（取引判断のピン留め）は本 ADR で**強化される**（鎖を持たせないことを
  テストで固定した）。ピンの仕組みそのものは変えていない。
