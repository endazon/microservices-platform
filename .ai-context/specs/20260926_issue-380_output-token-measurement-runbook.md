---
title: 作業仕様書 — #380 出力トークン実測を所有者が判断・実行するための Runbook を置く
type: spec
status: draft
related_ids:
  - FR-11
  - SC-08
  - NFR-18
  - NFR-19
  - ADR-0010
  - ADR-0025
  - ADR-0038
  - ADR-0044
  - ADR-0095
  - IADR-0101
  - IADR-0110
  - IADR-0212
  - IADR-0225
  - IADR-0369
  - IADR-0374
  - IADR-0400
  - IADR-0456
author: Claude Opus 5.5 (worker)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md
related_specs:
  - 20260830_issue-380_opus5-max-tokens-measurement
issue: "380"
---

# 作業仕様書 — #380 出力トークン実測の Runbook

## 起点

- issue #380（IADR-0101 のフォローアップ 1・2）。2026-09-05 の blocked 再検証で、**残る阻害は 1 つだけ**になった ——
  「所有者が `microservices-platform/llm-provider-credentials` へ実 `ANTHROPIC_API_KEY` を投入し、統計的に意味のある
  サンプル数に達するまで費用の出る `/complete` 呼び出しを許可すること」。環境側の 4 阻害（転送構成・develop 相当
  image・Prometheus 永続化・429 を区別する軸）は解消済み（同コメント）。
- **AI はキーを入れない・実 LLM を呼ばない・費用を出さない・稼働クラスタに触れない。** 本作業の成果物は、所有者が
  **数字を見て判断し、判断した後に正確に実行できる**ための手順書である。

## 射程

| 対象 | 扱い |
| --- | --- |
| `docs/operations/llm-output-token-measurement-runbook.md`（新規・`type: runbook`） | **作る** |
| 本作業仕様書 | 作る |
| `docs/operations/operations.md` / `llm-cost-monthly-review-runbook.md` | **触らない**（別 PR が編集中。依頼者の指示） |
| コード（`max_tokens` の値・計器・ダッシュボード） | **触らない**（値は実測が出るまで動かさない。2026-08-30 の結論を維持） |
| 他の Runbook の誤り（下の §見つけたもの） | 本 PR では直さない。報告に留める |

### 索引への登録

`docs/README.md` は種別の一覧だけを持ち、Runbook 個別の一覧を持たない。Runbook を列挙しているのは
`operations.md`（触らない）だけである。文書検査（`check-trace-blocks` / `check-doc-type-vocabulary` /
`gen-knowledge-graph --check`）は索引への登録を要求しない —— §検証 で実走して確かめる。**索引は触らない。**

## 走査した母集合（記憶で挙げない）

### 軸 1: `max_tokens` 4096 の設置箇所（値そのもので `src/` 全体を引いた）

```
$ grep -rn "4096" --include=*.cs src | grep -v "Tests\|/obj/" | grep -v ai-stock
```

| 箇所 | 役割 | Runbook に載せるか |
| --- | --- | --- |
| `src/platform/backend/Shared/Platform.Shared.Contracts/Dtos/CompletionDto.cs` `CompletionApiRequest(int MaxTokens = 4096)` | HTTP 経路の既定（省略した呼び出し元＝グラフの AI 提案・クラスタ要約に効く） | 載せる（issue の 3 箇所の 1） |
| `src/platform/backend/Services/LlmGateway/Domain/Ports/ILlmProvider.cs` `CompletionRequest(int MaxTokens = 4096)` | プロバイダを直接呼ぶ内部経路の既定 | 載せる（3 箇所の 2） |
| `src/knowledge/backend/Services/AiAnalysisService/Infrastructure/ExternalServices/RagOrchestrator.cs` 2 か所（`MaxTokens: 4096`） | 一括（`rag-answer` / `analysis` 共用）と逐次（`rag-answer`） | 載せる（3 箇所の 3）。**1 つの値が 2 用途・2 モデルに効く**ことを明記する |
| `src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/Llm/LlmGrpcMapping.cs` `DefaultMaxTokens = 4096` | gRPC 経路で `max_tokens=0`（未指定）を写す既定 | **載せる（4 か所目）**。issue 起票（2026-07）の後に gRPC 面が入って増えた。issue 本文の「3 箇所」は古い |
| `LlmCompletionMetrics.cs` のバケット境界 `…3072, 4096, 8192` | 計器の境界 | 載せる（上げるなら境界の見直しも要る、の注記として） |
| `GrpcService.cs` / `AnalysisBffEndpoints.cs` の `4096` | コメント・バッファ長 | 除外（値ではない／無関係） |

除外: `DiagramCodingInterpretation.cs` の `MaxTokens = 1024`（別用途・別の値。本 issue の射程外）。

### 軸 2: 既定層（`claude-opus-5`）へ実際に流れる経路

`appsettings.json` の `PurposeModels` と呼び出し元の `Purpose` を突き合わせた。

| 呼び出し元 | purpose | 割当モデル | メトリクス上の `llm_purpose` |
| --- | --- | --- | --- |
| AI 分析ダッシュボード → `/analysis/analyze` | `analysis` | `claude-opus-5`（鎖: `claude-sonnet-5`） | `analysis` |
| 検索チャット → `/analysis/ask`（`/ask/stream`） | `rag-answer` | `claude-sonnet-5`（鎖: `claude-haiku-4-5`） | `rag-answer` |
| グラフの AI 提案（`DocumentUpdated` 購読で発火） | `graph-suggestion`（`PurposeModels` に無い） | エンドポイント既定 `claude-opus-5` | **`other`** |
| クラスタ要約（日次・**既定で無効**） | `graph-cluster-summary`（同上） | `claude-opus-5` | `other` |
| 図のコード化 | `diagram-coding` | `claude-sonnet-5` | `diagram-coding` |
| purpose 未指定 | `default` | `claude-opus-5` | `default` |

→ **本リポジトリ内で `claude-opus-5` を人が意図して発生させられる経路は AI 分析ダッシュボードだけ**である。
Runbook はこれを標本の発生源にする。`default` の実呼び出し元はリポジトリ内に無い。

### 軸 3: 認可・合成監視・キーの空値

- `/complete` は `ServiceCaller` ポリシーを要する（`Complete/Endpoint.cs`）→ **所有者が利用者トークンで直接叩く経路は無い。**
  標本は製品の画面から発生させる。
- 合成監視の主体は既定で LLM を呼ばない（`RagOrchestrator.SuppressLlmForSynthetic`）→ 標本は**通常の利用者**で作る。
- 画面（秘密情報の管理）は `kind=value` の**空文字を拒否**する（`SecretItemBffEndpoints.cs:184-185`）→ **止めるときに画面でキーを空へ戻せない。**
  止め方は「発行元での失効」を一次、「コンソールで保管先の値を空にする」を二次にする。
- キーが空でもゲートウェイは Anthropic クライアントを作る（`Program.cs:64`。`?? "placeholder"` は null のときだけ効く）
  → 空キーの呼び出しは**上流が認証で拒否する形で失敗する**と読むのが正しい（「外部を呼ばない」ではない）。
  Runbook では「`llm_result="sent"` が増えなくなること」を停止の確認に使い、挙動を断定しない。

### 軸 4: 計器名・ラベル名（コードとダッシュボードの両方で確かめた）

- `LlmCompletionMetrics.cs`: `llm.completion.total`（タグ 7: result / stop_reason / purpose / model / provider / confidentiality / upstream_status）、
  `llm.completion.output_tokens`（Histogram・境界 `0,16,64,128,256,512,1024,2048,3072,4096,8192`・タグは result と upstream_status を落とした 5）。
- `LlmUsageMetrics.cs`: `llm.tokens.total`（`llm.token_type`）、`llm.cost.total`（`llm.currency`）、`llm.pricing.unpriced.total`（`llm.pricing_status`）。
- Prometheus 側の名前は `deploy/grafana/provisioning/dashboards/llm-usage.json` と `docs/observability/llm-completion-metrics.md` の既存 PromQL と一致
  （`llm_completion_total` / `llm_completion_output_tokens_bucket|_count|_sum` / `llm_tokens_total` / `llm_cost_total` / `llm_pricing_unpriced_total`）。
- **ダッシュボードに出力トークン Histogram のパネルは無い**（`llm-usage.json` のパネル 12 枚を列挙して確認）→ Runbook は Prometheus API を直接叩く。

### 軸 5: 単価（費用の上限の根拠）

`src/platform/backend/Services/LlmGateway/appsettings.json` `Llm:Pricing`（通貨 `USD`・百万トークンあたり）:

| モデル | 入力 | 出力 | 有効期間 |
| --- | --- | --- | --- |
| `claude-opus-5` | 5.0 | 25.0 | 無期限 |
| `claude-sonnet-5` | 3.0 | 15.0 | 2026-09-01 から（それ以前は 2.0 / 10.0） |
| `claude-haiku-4-5` | 1.0 | 5.0 | 無期限 |

換算式は `ModelPriceTable.Estimate`: `input/1e6 × InputPerMillionTokens + output/1e6 × OutputPerMillionTokens`。

## 設計判断

1. **費用の上限は「全呼び出しが 4096 に張り付く」最悪値で出す。** 期待値ではなく上限を承認対象にする（所有者が
   承認するのは「これ以上は出ない」額である）。入力の代表値は 2 通り（4,000 / 16,000）置き、式を併記して所有者が
   置き換えられるようにする。16,000 は「上位 5 チャンク × 最大 2,048 文字」からの保守側の置き値であり、**最初の数回で
   実測した平均入力へ置き換える手順**を Runbook に入れる（推定を推定のまま残さない）。
2. **サンプル数の根拠は「3 の法則」で示す。** 上限到達が 0 件だったとき、到達率の 95% 上側信頼限界はおよそ `3/N`
   （N=50 → 6%・100 → 3%・200 → 1.5%）。判断基準の提案値と噛み合う N を所有者が選べるようにする。
3. **判断基準は「提案値」と明記する。** 実装側は数字を決めない（月次予算と同じ規律）。所有者が承認して初めて基準になる。
4. **陽性対照を全ての「空」の読みに対で置く**（計器・名前・経路・429）。
5. **`docs/` の可視本文に計画 ID・実装 ADR 番号・仕様書名・修飾付き issue 参照を書かない**（trace ブロックへ）。

## 見つけたもの（本 PR では直さない）

- `docs/operations/secret-item-console-injection-runbook.md` 手順 4 の `rollout restart deploy/llm-gateway` は、
  チャートが作る Deployment 名（`llmgateway-service`。`deploy/helm/microservices-platform/templates/deployment.yaml` の
  `{{ $name }}-service` と values のキー `llmgateway`）と一致しない。本 Runbook では正しい名前を使う。
- issue #380 本文の「3 箇所」は gRPC 経路の既定（4 か所目）を含まない。

## 受け入れ基準

1. Runbook が 6 節（前提の確認・費用の上限・投入と標本と停止・測定・`max_tokens` の判断・429）を持ち、
   コマンド・PromQL・名前がすべてリポジトリの実物と一致する。
2. 費用表の数値が単価表と式から再計算して一致する。
3. 値（キー）をどこにも書かない。稼働クラスタに触れていない。
4. `check-trace-blocks` / `check-doc-type-vocabulary` / `gen-knowledge-graph --check` / `check-cross-repo-refs` /
   `check-plan-id-qualification` / `REQUIRE_REPO_TESTS=1 scripts.test.js` が通る。

## 検証

（PR 本文に実出力を貼る。）
