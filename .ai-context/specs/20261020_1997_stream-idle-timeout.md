---
title: LLM ゲートウェイの逐次呼び出し（CompleteStream）の Claude 経路に、イベントの間の無通信の期限（Llm:AnthropicStreamIdleTimeoutSeconds。未設定は一括の期限と同じ値）を持たせる（#1997）
type: spec
status: done
related_ids: [FR-11, FR-04, NFR-02, ADR-0010, IADR-0565, IADR-0562, IADR-0528, IADR-0374, IADR-0037]
author: claude
created: 2026-10-20
updated: 2026-10-20
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0010（LLM ゲートウェイ）
issue: "#1997"
---

# 仕様書: 逐次呼び出しに無通信の期限を持たせる（#1997）

> 本仕様書は実装着手前に作成した（着手 2026-10-20）。実装の基点は MSP `origin/develop` `fe06bdee`。
> AST は `origin/develop` と、submodule の pin `bc2ab98` を走査した。判断は IADR-0565。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-11（LLM ゲートウェイ）・FR-04（RAG の回答）
- 非機能: NFR-02（初回応答）
- 関連 ADR: ADR-0010
- 関連 IADR: IADR-0562 決定 4（逐次は全体に期限を掛けない）・IADR-0528（期限の設定の規則）・IADR-0374（上流状態の軸）

## 呼び出し側の全体の期限（実測。2026-10-20 にコードと配備物を走査した）

逐次（gRPC `LlmCompletion/CompleteStream`）の呼び出し元を `\.CompleteStream(` で走査した。本番コードで呼ぶのは AI 分析サービスの 1 か所だけである（ほかは試験）。
その経路を、ブラウザまで遡って測った。

| 段 | 箇所 | 全体の期限 | 無通信の期限 |
| --- | --- | --- | --- |
| SPA（検索画面の回答・AI チャット欄） | `useAskStream.ts`・`useAiChatStream.ts` | **なし**（`AbortController` は利用者の「停止」だけ） | なし |
| エッジ（PoC＝経路 B） | Istio を入れない（`values-local.yaml` の `mesh`）。Traefik の Ingress（`platform-frontend-ingress.yaml`）は期限の注記を持たない | なし（リポに設定なし） | なし（リポに設定なし） |
| エッジ（本番像） | `virtualservice-app.yaml` の `/bff/` の経路に `timeout` を置いていない | なし（リポに設定なし） | リポに設定なし（プロキシの既定に依存し、統制できない） |
| BFF `/bff/analysis/ask/stream` | `AnalysisBffEndpoints.cs`（`HttpClient` `AiAnalysisService`。`ResponseHeadersRead`） | `HttpClient` の既定 100 秒は**応答ヘッダの到着まで**しか効かない。AI 分析は出典（citations）を先に書くので、LLM を呼ぶ前にヘッダは届いている | なし |
| AI 分析 `/analysis/ask/stream` | `AskStream/Endpoint.cs` → `RagOrchestrator` → `GrpcLlmCompletionTransport.StreamAsync` | **なし**（gRPC の deadline を付けない。取り消しは要求の中断だけ） | なし |
| LLM ゲートウェイ | `ClaudeProvider.StreamAsync` | `Llm:AnthropicTimeoutSeconds`（100 秒）は**応答ヘッダの到着まで** | **なし**（本 issue） |
| 合成監視 | `synthetic-monitor.yaml` が `/bff/analysis/ask/stream` を叩く | — | — （LLM を呼ばない。`SuppressLlmForSynthetic`。CompleteStream へ届かない） |
| AST | `completion.proto` に rpc の定義があるだけで、呼び出しは 0 件。AST は一括 `Complete` だけを使う（期限は 30〜120 秒） | — | — |

**結論: 足りない。** ヘッダの後に上流が止まると、どの段も切らない。利用者が「停止」を押すかタブを閉じるまで、ゲートウェイ・AI 分析・BFF が要求を握り続ける。

## 既定値の根拠

- 逐次の利用者は RAG の回答（用途 `rag-answer`・`claude-sonnet-5-5`・`max_tokens` 4,096）だけである。
- `max_tokens` は思考と本文の合算上限である（IADR-0101）。運用仕様書の実測の目安（50〜80 トークン／秒）では、4,096 トークンの全部を思考に使っても約 51〜82 秒で終わる。思考の文字列を流さない応答でも、**イベントの間の無通信は生成の全体より短い**。
- 同じ用途の一括（`/analysis/ask`）は、生成の全体を `Llm:AnthropicTimeoutSeconds`（既定 100 秒）の内に終える前提で運用している。
- そこで、既定は**一括の期限の解決値と同じ**にする（未設定なら 100 秒）。一括の期限を延ばす運用（長い出力）をすると、無通信の期限もそれに付いて延びる。
- 期限はイベントの間だけを測る。長く流れ続ける応答は切らない（IADR-0562 決定 4 の懸念は残らない）。

## 母集合（規則 9・10）

- 誤りの側の文字列 `応答ヘッダの到着まで` / `ヘッダの到着後は期限で切れない` / `期限なし` を全文書で走査した。追随先:
  `docs/functional/FR-11_llm-egress-routing.md`（期限の表）・`docs/operations/operations.md`（§Anthropic 呼び出しの期限）・
  `docs/tests/FR-11_llm-egress-routing.md`（T-45 を新設）・`deploy/helm/microservices-platform/values.yaml`（設定のコメント）・`ClaudeProvider.cs` のコメント。
- 除外: `.ai-context/` の凍結記録（IADR-0528・0562・作業仕様書）。決定の変更は IADR-0565 が持つ。

## 受け入れ基準

- [x] チャンクの途中で止まる相手で、期限（試験では 1 秒）が過ぎると `TimeoutException` になる。止まる前の本文は流れている。送信は 1 回。
- [x] 期限切れは `OperationCanceledException` ではない。`CompletionUseCase` は `upstream_error` へ縮退する。計器の上流状態は `transport` になる。
- [x] 呼び出し側の取り消しは、従前どおり `OperationCanceledException` である。
- [x] 期限より短い間隔でイベントが届き続ければ、全体が期限を超えても打ち切らない。
- [x] 受け取り側（gRPC への書き込み）の遅さは数えない。
- [x] 設定 `Llm:AnthropicStreamIdleTimeoutSeconds` の規則: 未設定・空白は一括の期限の解決値に従う。1〜2,147,483 の整数は採る。不正値は一括の期限の解決値へ倒し、warn を 1 行出す。

## 変異の結果

| 変異 | 結果 |
| --- | --- |
| 待つ前の `CancelAfter(期限)` を消す（無通信の期限なし） | ①が待ち続け、xUnit の上限で赤（12 件中 1 件） |
| 時計を最初の 1 回だけ掛けて戻さない | ③が赤（12 件中 1 件） |
| 受け取った後に時計を止めない | ④が赤（12 件中 1 件。上流の次の断片を 100 ms 遅らせ、取り消しが観測される形にした） |

## 範囲外

- Claude 以外のプロバイダ（セルフホスト・Copilot）は逐次を実装していない。既定実装は一括を 1 チャンクで返すので、本 issue の対象ではない。
- 呼び出し側（AI 分析・BFF）へ全体の期限を足すことはしない。長い回答を切る判断になり、NFR の統制値に当たるからである。
