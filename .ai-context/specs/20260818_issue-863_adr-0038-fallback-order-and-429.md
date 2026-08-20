---
title: 作業仕様書 — analysis 用途のフォールバック順序・429 と 400 系の区別・発火の可観測化（#863 / 計画 ADR-0038 決定 3・4・6）
type: spec
status: done
related_ids:
  - ADR-0038
  - ADR-0010
  - ADR-0025
  - FR-11
  - UC-02
  - IADR-0022
  - IADR-0102
  - IADR-0104
  - IADR-0110
  - IADR-0112
  - IADR-0113
  - IADR-0225
author: claude
created: 2026-08-18
updated: 2026-08-18
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0038_analysis-purpose-drop-fable-5.md (Accepted)
  - planning:projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md (Accepted)
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md
  - planning:projects/microservices-platform/06_technical/08_data-egress-policy.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0017_llm-fallback-policy.md (ADR-0038 決定 3 が「同一の考え方」として引く AST 側の方針)
related_specs:
  - ./20260818_issue-850_adr-0038-drop-fable5-analysis.md
  - ./20260728_issue-395_refusal-metrics.md
  - ../adr/IADR-0225_llm-purpose-fallback-chain-and-429-boundary.md
  - ../../docs/functional/FR-11_llm-egress-routing.md
  - ../../docs/tests/FR-11_llm-egress-routing.md
  - ../../docs/observability/llm-completion-metrics.md
  - ../../docs/operations/llm-model-pin-runbook.md
---

# 作業仕様書: `analysis` のフォールバック順序と 429 の境界（#863）

## 1. 起点となる計画書（トレーサビリティ）

- 計画 ADR: `ADR-0038`（`Accepted`。利用者裁定／質問票 第 10 回 Q1・planning#83）。
  本作業が実装へ落とすのは **決定 3・決定 4・決定 6** である。
- 機能要求: `FR-11`（LLM 送信可否とルーティング）／ユースケース: `UC-02`（AI 分析＝用途 `analysis`）。
- 実装 ADR: 本 PR で [IADR-0225](../adr/IADR-0225_llm-purpose-fallback-chain-and-429-boundary.md) を起こす（内部設計の決定であり、計画 ADR への単純な追随ではない）。
- 起点 issue: [#863](https://github.com/endazon/microservices-platform/issues/863)。
  先行作業は #850 / PR #859（決定 1・2 のみを反映）。

### 計画 ADR-0038 の決定 1〜6 と本作業の関係

| 決定 | 内容（条文の要旨） | 本作業での扱い |
| --- | --- | --- |
| 1 | `analysis` の割当を `claude-fable-5` → `claude-opus-5` | **既にある**（#850 / PR #859） |
| 2 | `claude-fable-5` を利用許可集合から外す | **既にある**（#850 / PR #859） |
| 3 | `analysis` のフォールバック順序を `claude-opus-5` → `claude-sonnet-5` と定める | **本作業で実装する** |
| 4 | 429 は再試行でありフォールバックではない。フォールバックは HTTP 400 系で発火する | **本作業で実装する**（§4.3 に射程の限定あり） |
| 5 | フォールバック先は利用許可集合（`Models`）に残す | **既に満たされている**（`claude-sonnet-5` は `Models` に在る）。ただし**ガードの射程は本作業で広げる**（§4.5） |
| 6 | フォールバックの発火を可観測にする | **本作業で実装する** |

## 2. 現状（着手前に実測した事実）

**現行 `LlmRouter` は「明示要求モデル → 用途別モデル → `DefaultModel` → 適格モデルの先頭」という
*解決順序* を持つだけで、HTTP 400 系での *実行時フォールバック機構* を持たない。**
`CompletionEndpoints` の `catch` は例外の種類・HTTP ステータスを一切見ずに
`ResultUpstreamError` へ落とし、`Sent=false` の縮退応答を返す（`CompletionEndpoints.cs:79-90`）。

したがって決定 3・4・6 はいずれも**新設**を伴う。「順序を設定に足す」だけでは決定 3 は満たせない
（発火させる機構が無ければ順序は死んだ設定である）。

### プロバイダ例外の形（実測。2026-08-18）

決定 4 は「400 系」と「429」を実装が区別できることを前提にする。**区別できるかを先に測った。**
`ClaudeProvider` にスタブ `HttpMessageHandler` を噛ませ、各ステータスを返させて例外を観測した
（探索用テストは実測後に削除した。生出力は §7.1）。

| 上流の応答 | 送出される例外 | `StatusCode` |
| --- | --- | --- |
| 400 | `System.Net.Http.HttpRequestException` | `BadRequest` |
| 404 | 同上 | `NotFound` |
| 429 | 同上 | `TooManyRequests` |
| 500 | 同上 | `InternalServerError` |

**`Anthropic.SDK` 4.0.0 は `HttpRequestException.StatusCode` を設定して投げる**（.NET 5 以降のプロパティ）。
`SelfHostedProvider` / `CopilotProvider` は `EnsureSuccessStatusCode()` を使っており、同プロパティは
`HttpResponseMessage.EnsureSuccessStatusCode` が設定する。**よって全プロバイダで同じ判定器が使える。**

## 3. 母集合の引き方と結果（`.claude/rules/traceability.repo.md` 規則 2・6・9・10）

**拡張子で絞らず、パス除外（`:!planning` `:!src/ai-stock-trading` `:!*/bin/*` `:!*/obj/*` `:!CHANGELOG.md`）
だけで追跡下の全ファイルを走査した。** 本件は「誤りの是正」ではなく「機構の新設」だが、
**新設によって偽になる既存記述**こそが母集合である。よって**偽になる側の文字列**で 7 軸引いた。

| # | 軸（コマンド） | 何を捕まえるための軸か |
| --- | --- | --- |
| 1 | `git grep -n 'ADR-0038'` | 決定 3・4・6 を「未実装」と述べている箇所 |
| 2 | `git grep -c 'フォールバック\|fallback\|FallBack\|FallsBack'` | フォールバックを語る全箇所（無関係な語義を含むため 2 段で絞る） |
| 3 | `git grep -niE '429\|レート制限\|rate.?limit'` | 429 の扱いを述べている箇所 |
| 4 | `git grep -nE 'llm\.completion\|llm_completion\|upstream_error\|LlmCompletionMetrics\|ResultSent'` | メトリクスの定義・利用箇所 |
| 5 | `git grep -niE 'フォールバック機構\|フォールバックは実装\|フォールバックを実装\|実装されていない'` | 「機構が無い」と明言している箇所 |
| 6 | `git grep -nE 'egress_denied\|provider_missing'` | `llm.result` の**値域を列挙**している箇所（値を 1 つ足すので全部が対象） |
| 7 | `git grep -n 'PurposeModels'` | 設定キーを列挙している箇所（兄弟キーを 1 つ足すので対象） |

### 3.1 追随する（本 PR で触る）

| 箇所 | 何が偽になるか / 何を足すか | 軸 |
| --- | --- | --- |
| `src/.../LlmGateway.Api/Foundation/Routing/LlmRoutingOptions.cs` | 設定キー `PurposeFallbackModels` を新設 | 7 |
| `src/.../LlmGateway.Api/Foundation/Routing/ILlmRouter.cs` | `RoutingDecision` にフォールバック鎖を載せる | — |
| `src/.../LlmGateway.Api/Foundation/Routing/LlmRouter.cs` | 鎖の解決（適格性・ZDR・重複除去・登録漏れの警告） | — |
| `src/.../LlmGateway.Api/Foundation/Routing/LlmFallbackPolicy.cs`（新規） | 400 系 / 429 の判定器（決定 4） | 3 |
| `src/.../LlmGateway.Api/Foundation/Endpoints/CompletionEndpoints.cs` | `/complete` の再試行ループ、`/complete/stream` の射程外を可観測にする警告 | — |
| `src/.../LlmGateway.Api/Foundation/Observability/LlmCompletionMetrics.cs` | `llm.result` に `fallback` を足す（決定 6） | 4・6 |
| `src/.../LlmGateway.Api/appsettings.json` | `PurposeFallbackModels.analysis = ["claude-sonnet-5"]`（決定 3） | 7 |
| `docs/adr/IADR-0225_*.md`（新規） | 本件の実装判断 | — |
| `docs/adr/README.md` | 索引行 1 行 | — |
| `docs/adr/IADR-0110_llm-completion-stop-reason-metrics.md` | §決定 の `llm.result` 値域表が 4 値で閉じている（**live な ADR**。日付つき追記で 5 値へ） | 6 |
| `docs/functional/FR-11_llm-egress-routing.md` | 用途別モデル解決・例外処理表・可観測性節・受け入れ基準・処理フロー図 | 1・2・4・6 |
| `docs/tests/FR-11_llm-egress-routing.md` | T-25 を追加 | 2 |
| `docs/observability/llm-completion-metrics.md` | 属性値域・クエリ例・しきい値の注記 | 4・6 |
| `docs/operations/operations.md` | `upstream_error` のアラート観点に `fallback` との関係を注記 | 4・6 |
| `docs/operations/llm-model-pin-runbook.md` | **「フォールバック機構は実装されていない（2026-08-11 時点）」が偽になる**。列挙コマンドも鎖を含める | 5・7 |
| `docs/specs/20260818_issue-850_adr-0038-drop-fable5-analysis.md` | §7 の「決定 3・4・6 の実装 issue（未採番）」が解決済みになる。**確定済み仕様書なので日付つき経過追記のみ**（`traceability.repo.md`） | 1・5 |
| `src/.../tests/LlmGateway.Api.Tests/{LlmFallbackPolicyTests,CompletionFallbackEndpointTests}.cs`（新規）ほかテスト 3 ファイル | T-25 の写像（§4.5） | — |
| `scripts/test-spec-coverage-baseline.json` | **上の走査では出ない追随点。** テスト仕様書へクラスを載せると [IADR-0130](../adr/IADR-0130_test-spec-coverage-ratchet.md) のラチェット床を上げる必要があり、`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が「床の上げ忘れ」で fail する。`node scripts/check-test-spec-coverage.js --update` で 2 件（`LlmFallbackPolicyTests` / `CompletionFallbackEndpointTests`）追加した | 検査器 |
| 本仕様書 | — | — |

### 3.2 母集合に入ったが触らない（除外とその理由）

**「黙って除外した」ことでも事故は起きる**（規則 6）。入ったうえで外したものを全部書く。

| 箇所 | 実測した内容 | 除外の理由 |
| --- | --- | --- |
| `docs/specs/20260811_issue-587_pin-migration-runbook.md:71` | 「★ フォールバック機構は存在しない」 | **確定済み（`status: done`）の作業仕様書＝当時の point-in-time 記録**。凍結の射程（`traceability.repo.md`）。**live な正本は同 PR が生んだ Runbook 側**であり、そちらは §3.1 で追随させる |
| `docs/specs/20260728_issue-395_refusal-metrics.md:93` ほか | `llm.result` 値域を 4 値で列挙 | 同上（確定済みの作業仕様書）。**値域の live な正本は `docs/observability/` と `IADR-0110`** であり、両方とも §3.1 で追随させる |
| `docs/specs/20260818_wave12-audit-followup.md:353` | 「5. 計画 `ADR-0038` 決定 3・4・6 …の実装」を残課題として列挙 | **確定済みの作業仕様書。かつ「残課題を #863 として起票した」記録そのもの**である。本 PR がその 5 番を消化するが、**起票時にそう書いた史実は正しい**。経過追記は #850 の仕様書側に 1 か所だけ置く（2 か所に置くと片方が古くなる） |
| `deploy/grafana/provisioning/dashboards/llm-usage.json` / `deploy/local/observability/grafana.yaml` | `sum by (llm_result) (increase(llm_completion_total[$__range]))` のパネルを持つ | **変更不要**。`llm.result` に値を足すだけなので、**この既存パネルに新しい系列が自動で現れる**（決定 6 の「用途別・モデル別の利用実績へ出す」はこの面で満たされる）。**新しい計器を作らないことで、`scripts/scripts.repo.test.js` の「式のメトリクス名は `llm_completion_total` ただ 1 つ」という突合も壊れない** |
| `docs/adr/IADR-0164_llm-cost-monthly-review-interim-control.md:46` | 「LLM ゲートウェイのメトリクスは **`llm.completion.total` の 1 本だけ**」 | **偽にならない。** 本作業は計器を増やさず属性値を 1 つ足すだけである（§4.4 の設計選択がここに効く） |
| `docs/adr/IADR-0054_*`（SaaS コネクタの 429 バックオフ）／`src/.../SaaSConnector.cs`／`src/platform/frontend/.../queryClient.ts` | 429 を再試行する既存実装 | **別系統。** LLM ゲートウェイの egress 経路ではない。ADR-0038 の射程外 |
| `.github/workflows/claude-*.yml` | `@claude fable` のレビュー用モデル選択 | **別系統**（開発時の AI レビュー経路）。#850 と同じ理由で射程外 |
| `feedback/` 配下 | 当時の環流記録 | 凍結の射程（`traceability.repo.md`） |
| `CHANGELOG.md` | 自動生成物 | 手で書き足さない |

### 3.3 規則 10 —— 実装後の語で引き直す

実装後に**新たに偽になる自分の記述**を、`PurposeFallbackModels` / `fallback` / `429` /
`llm.result` の 4 語で引き直す。結果は §7.4 に生のまま残す。

## 4. 設計

正本は [IADR-0225](../adr/IADR-0225_llm-purpose-fallback-chain-and-429-boundary.md)。ここには「何をどのファイルに置くか」を書く。

### 4.1 設定（決定 3）

```jsonc
"Llm": { "Routing": {
  "PurposeModels": { "analysis": "claude-opus-5", ... },      // 既存（第 1 候補）
  "PurposeFallbackModels": { "analysis": [ "claude-sonnet-5" ] }  // 新設（第 2 候補以降・順序つき）
}}
```

- **`analysis` 以外は空にする。** `default` / `rag-answer` の第 2 候補は計画 `ADR-0038` §未決事項で
  **未確定**であり、根拠なく決めない。
- **`trade-decision` は鎖を持たない。** `docs/operations/llm-model-pin-runbook.md` が
  「別のモデルへ切り替えて取引判断を続けてはならない」と定めている（`AST/ADR-0011` の再現性・監査可能性）。
  **これはテストで固定する**（§4.5）。

### 4.2 鎖の解決（`LlmRouter`）

`RoutingDecision` に `FallbackModels`（順序つき・既定は空）を載せる。解決規則:

1. 採用したエンドポイントの**適格モデル**（`EligibleModels`＝ZDR 除外を通した集合）に含まれるものだけ残す。
   → 計画 `ADR-0038` 決定 5（フォールバック先が `Models` に無ければその場で失敗する）を、
   **黙って失敗させず、起動時ではなく解決時に warn ログで見えるようにする**。
2. 第 1 候補と同じモデルは落とす（同じモデルへ 2 回投げない）。
3. 重複を落とし、設定の順序を保つ。

### 4.3 発火（`CompletionEndpoints` の `/complete`）と 429（決定 4）

判定器 `LlmFallbackPolicy.ShouldFallBack(Exception)`:

| 上流ステータス | 判定 | 根拠 |
| --- | --- | --- |
| **400〜499（429 を除く）** | **フォールバックする** | 決定 4「フォールバックは HTTP 400 系で発火する」 |
| **429** | **フォールバックしない** | 決定 4「429 は再試行であってフォールバックではない」 |
| 5xx・通信断・ステータス不明の例外 | フォールバックしない | 決定 4 が挙げるのは 400 系のみ。既存の `upstream_error` 縮退を変えない |

**429 の「再試行」そのものは本作業では実装しない（射程外）。** 計画 `ADR-0038` 決定 4 は
**フォールバックの発火条件**を定めるものであり、再試行の回数・バックオフ・`Retry-After` の扱いは
**どの計画文書も定めていない**。決めていない方針を実装が発明すると、後から計画が別の値を定めたときに
「実装が先に決めた」ことになる。**本作業は「429 でフォールバックしない」ことまでを実装し、
429 の再試行は [IADR-0225](../adr/IADR-0225_llm-purpose-fallback-chain-and-429-boundary.md) §結果 のフォローアップとして残す**（現行の 429 の挙動は変わらず
`upstream_error` の縮退である）。

**射程は非ストリーミング `/complete` に限る。** `analysis` はこの経路を使う
（`RagOrchestrator.AnalyzeAsync` → `GenerateAsync` → `/complete`）。`/complete/stream` は
`rag-answer` 専用であり、`rag-answer` の第 2 候補は**未確定**なので鎖が存在しない。
ただし**将来の設定変更で無音の穴にならないよう**、ストリーム経路は鎖が設定されていたら warn を出す。

### 4.4 可観測化（決定 6）

**新しい計器は作らない。** 既存カウンタ `llm.completion.total` の `llm.result` に
**`fallback` を 1 値足す**。フォールバックが起きた 1 リクエストは 2 回計上される。

| 計上 | `llm.result` | `llm.model` |
| --- | --- | --- |
| 見送った第 1 候補への呼び出し | `fallback` | `claude-opus-5` |
| 成功した第 2 候補への呼び出し | `sent` | `claude-sonnet-5` |

- **「用途別・モデル別の利用実績」（計画 06_technical/05_observability-ops）にそのまま出る** ——
  既存の Grafana パネル `sum by (llm_result) (…)` に新系列が現れ、`llm.model` 別の内訳も既にある。
- **`upstream_error` の意味が濁らない。** フォールバックで回復した呼び出しを `upstream_error` に
  数えると、`upstream_error` 率 > 10%（critical）のアラート方針が誤発火する。
- 発火の詳細（第 1 候補 → 第 2 候補、上流ステータス）は warn ログに残す。**利用者由来の
  `purpose` はログへ入れない**（設定由来のモデル名・エンドポイント名だけを載せ、
  log forging の経路を新たに作らない）。

### 4.5 テスト

| 追加先 | 内容 |
| --- | --- |
| `LlmRouterTests` | 鎖の解決（`analysis` → sonnet-5）／未登録の鎖要素が落ちる／ZDR 要件区分で非 ZDR の鎖要素が落ちる／`trade-decision` は鎖を持たない |
| `LlmFallbackPolicyTests`（新規） | 400/403/404/413/422 は発火・**429 は発火しない**・5xx は発火しない・ステータス不明は発火しない |
| `CompletionFallbackEndpointTests`（新規） | `/complete` が 400 で第 2 候補へ落ちて成功する／429 では落ちず `Sent=false` になる／鎖が尽きたら `upstream_error` へ／`/complete/stream` は落ちない |
| `CompletionMetricsTests` | フォールバック時に `fallback` と `sent` が 1 件ずつ計上され、モデルが別であること |
| `CompletionRoutingEndpointTests` | **既存 T-19 ガードの射程を広げる**（`PurposeModels` の値に加え `PurposeFallbackModels` の値も `Models` 登録済みであること）。**並行するガードを新設しない** —— #863 の指示どおり作り直さず、**同じ 1 本を広げる**（#850 が `PurposeModels_AreNotListedAsNonZdr` で採った形と同じ）。射程を広げるので名前も改める |

## 5. 受け入れ基準

- [x] 用途 `analysis` の第 1 候補が HTTP 400 系で失敗したとき、`claude-sonnet-5` へフォールバックして応答が返る（決定 3）。
- [x] **429 ではフォールバックしない**（決定 4）。
- [x] フォールバックの発火が `llm.completion.total{llm_result="fallback"}` として観測でき、
      第 1 候補・第 2 候補のモデルが `llm.model` で区別できる（決定 6）。
- [x] `trade-decision` はフォールバックの対象にならない（Runbook の禁止事項）。
- [x] 設定した鎖の要素が `Models`（利用許可集合）に登録済みであることをガードが固定する（決定 5 の射程拡大）。
- [x] `dotnet build` / `dotnet test src/platform/backend/backend.slnx` が緑。
- [x] `dotnet format src/platform/backend/backend.slnx --verify-no-changes` が通る。
- [x] 文書検査器（`check-doc-links` / `check-doc-status-vocabulary` / `check-doc-type-vocabulary` /
      `check-cross-repo-refs` / `check-plan-id-qualification` / `check-adr-numbering` /
      `check-reading-budget` / `check-kit-sync` / `check-backend-libraries` / `check-cpm-versions` /
      `REQUIRE_REPO_TESTS=1 scripts.test.js` / `check-doc-updated` / `check-commit-messages`）が通る。
- [x] **変異試験**で、機構が空振りしていないことを実測する（§6）。

## 6. 変異試験の計画（宣言ではなく実測）

**壊すと落ちることを示す。** 3 つの機構それぞれに対し、実装側を 1 箇所だけ壊して赤になることを見る。
**変異はコミットしない。復元は `git diff` が空であることで示す。**

| # | 壊す箇所 | 期待 |
| --- | --- | --- |
| 1 | 400 系の判定を落とす（`ShouldFallBack` を常に `false`） | 400 でのフォールバック系テストが落ちる |
| 2 | 429 の除外を落とす（`!= 429` を外す） | **429 でフォールバックしないテストが落ちる**（決定 4 が効いている証拠） |
| 3 | `fallback` の計上を落とす | 可観測性テストが落ちる |
| 4 | `appsettings.json` の鎖を空にする | 実設定経由のフォールバック経路テストが落ちる |

（結果は §7.3 に生出力で残す。）

## 7. 実測ログ

### 7.1 プロバイダ例外の形（§2）

`ClaudeProvider` にスタブ `HttpMessageHandler`（固定ステータスを返す）を噛ませ、
`CompleteAsync` が送出する例外の型と `StatusCode` を出力させた。**探索用テストは実測後に削除した**
（機構の回帰は `LlmFallbackPolicyTests` が恒久的に持つ）。生出力:

```
PROBE status=500 -> System.Net.Http.HttpRequestException(StatusCode=InternalServerError) msg=Anthropic had an internal server error, which can happen occasionally.  Please retry your request.  {"type":"error","error":{"type":"invalid_request_error","message":"boom"}}
PROBE status=429 -> System.Net.Http.HttpRequestException(StatusCode=TooManyRequests) msg={"type":"error","error":{"type":"invalid_request_error","message":"boom"}}
PROBE status=404 -> System.Net.Http.HttpRequestException(StatusCode=NotFound) msg={"type":"error","error":{"type":"invalid_request_error","message":"boom"}}
PROBE status=400 -> System.Net.Http.HttpRequestException(StatusCode=BadRequest) msg={"type":"error","error":{"type":"invalid_request_error","message":"boom"}}
```

**結論**: 400 系と 429 は `HttpRequestException.StatusCode` で区別できる。決定 4 は実装可能である。

### 7.2 ビルド・テスト・整形

```
$ dotnet build src/platform/backend/backend.slnx        → Build succeeded. 0 Warning(s) 0 Error(s)
$ dotnet test  src/platform/backend/backend.slnx
    Passed! - Failed: 0, Passed:  68 ... AuthorizationService.Api.Tests.dll
    Passed! - Failed: 0, Passed: 182 ... LlmGateway.Api.Tests.dll     （変更前 157 → 182）
    Passed! - Failed: 0, Passed: 231, Skipped: 1 ... Platform.Bff.Tests.dll
$ dotnet format src/platform/backend/backend.slnx --verify-no-changes   → EXIT=0（出力なし）
```

**`src/knowledge/backend/backend.slnx` も走らせた。** 単体テストはすべて緑で、
`Knowledge.IntegrationTests` だけが 20 件失敗する（`Value cannot be null. (Parameter 'client')` ほか）。
**これは本 PR とは無関係な環境依存の既存失敗である** —— `origin/develop`（`6bf215d9`）を別 worktree へ
チェックアウトして同じプロジェクトを走らせ、**同一の「Failed: 20 / Passed: 23」を確認した**
（実 PostgreSQL / RabbitMQ を要する `[DockerFact]` 群）。

### 7.3 変異試験（機構が空振りしていないことの実測）

**壊すと落ちることを実測した。変異はコミットしていない。** 各変異のあと元へ戻し、
**`sha256sum -c`（変異前ハッシュ）が `OK` を返すこと**で復元を確認した
（`LlmFallbackPolicy.cs` は新規ファイルで `git diff` に現れないため、ハッシュで示す）。

| # | 変異 | 結果（`dotnet test …/LlmGateway.Api.Tests.csproj`） |
| --- | --- | --- |
| 1 | `ShouldFallBack` を常に `false` にする（400 系の発火を殺す） | **Failed: 9 / Passed: 173** —— `ShouldFallBack_On4xxOtherThanRateLimit` 5 件 ／ `StatusCodeOf_LooksIntoInnerExceptions` ／ `PostComplete_Analysis_When400_FallsBackToSonnet5` ／ `PostComplete_Analysis_WhenAllCandidatesFail_DegradesWithLastModel` ／ `PostComplete_WhenFallsBack_CountsFallbackThenSentWithDifferentModels` |
| 2 | **`&& status != RateLimitStatusCode` を外す**（429 の除外を殺す） | **Failed: 2 / Passed: 180** —— `ShouldNotFallBack_OnRateLimit429` ／ `PostComplete_Analysis_When429_DoesNotFallBack`。**決定 4 が効いている証拠であり、落ちたのはこの 2 本だけである**（他の挙動を巻き添えにしていない） |
| 3 | フォールバック時の `metrics.RecordCompletion(ResultFallback, …)` を削る | **Failed: 1 / Passed: 181** —— `PostComplete_WhenFallsBack_CountsFallbackThenSentWithDifferentModels`（`Expected probe.Measurements to contain 2 item(s) …, but found 1`） |
| 4 | `appsettings.json` の `analysis` の鎖を `[ ]` にする | **Failed: 3 / Passed: 179** —— 実設定を通す 3 本（endpoint 2 本＋メトリクス 1 本） |
| 4b | `appsettings.json` から `PurposeFallbackModels` ごと削る | **Failed: 4 / Passed: 178** —— 上の 3 本 ＋ **`PurposeModelsAndFallbacks_AreAllRegisteredInClaudeEndpointModels`**（鎖の消滅を捕まえる） |
| 5 | `appsettings.json` の `trade-decision` に鎖 `["claude-haiku-4-5"]` を足す | **Failed: 1 / Passed: 181** —— `TradeDecision_HasNoFallbackChainInProductionConfig`。**ピン Runbook の禁止が機械で守られている** |

復元後: `sha256sum -c` が 3 ファイルとも `OK`、`dotnet test` が **Passed: 182 / Failed: 0**。

### 7.4 規則 10 の引き直し（実装後の語で引く）

| 引いた語 | 結果 |
| --- | --- |
| `PurposeFallbackModels` | 18 箇所 / 12 ファイル。すべて本 PR で書いたもの（設定・実装・テスト・仕様書・Runbook・索引） |
| `provider_missing`（＝ `llm.result` の値域を列挙している箇所） | 6 箇所。**うち 1 件が偽になっていた** —— `docs/functional/FR-11_llm-egress-routing.md` §可観測性 の `llm.result` 列挙が 4 値のままだった。**本 PR で是正した**。`IADR-0110` の決定 2 の表は**当時の値域として原文のまま残し**、追記ブロックで現行値を示す（本文を書き換えない規約）。`docs/specs/20260728_issue-395_refusal-metrics.md` は確定済みの作業仕様書であり触らない（§3.2） |
| `PurposeModels_AreAllRegisteredInClaudeEndpointModels`（改名した旧テスト名） | 7 箇所。**live なのは `CompletionRoutingEndpointTests.cs:58` のコメント 1 件だけ**で、本 PR で新名へ是正した。残り 5 件は確定済みの `docs/specs/`（3 件）とテスト仕様書 T-19 行の説明（改名の事実は T-25 行と本 PR のコード内注記が持つ） |

## 8. 計画書との差異

- **差異: なし。** 決定 3・4・6 に忠実である。
- **意図的に実装しなかったもの（射程外）**:
  - **429 の再試行そのもの**（回数・バックオフ・`Retry-After`）。計画側が方針を定めていない（§4.3）。
  - **`default` / `rag-answer` の第 2 候補**。計画 `ADR-0038` §未決事項で明示的に未確定（§4.1）。
  - **`/complete/stream` のフォールバック**。鎖を持つ用途がストリーム経路に無いため（§4.3）。
    無音にしないため、鎖が設定されたら warn を出す。
