---
title: 作業仕様書 — GraphService の提案・クラスタ要約のプロンプトに信頼境界を置き、BFF の全応答にセキュリティヘッダを付ける（GHSA-5h4q-4m2j-rjhm の 3 本目）
type: spec
status: done
related_ids: [NFR, FR-17, FR-18, ADR-0032, ADR-0033, ADR-0035, ADR-0051, IADR-0582, IADR-0580, IADR-0579]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0051_ai-suggestion-abac-boundary.md （AI 提案の ABAC 境界）
  - planning:projects/microservices-platform/07_adr/ADR-0035_graphrag-retrieval-strategy.md （クラスタ要約）
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md （BFF セッション・同一オリジン）
issue: "GHSA-5h4q-4m2j-rjhm"
---

# 作業仕様書 — GraphService のプロンプトの信頼境界と BFF の応答ヘッダ

> 本仕様書は実装着手前に作成した（着手 2026-10-22）。判断の記録は **IADR-0582**（新規）に置く。
> 多層防御の強化。参照は GHSA の ID だけとする。GHSA-5h4q-4m2j-rjhm の対応の **3 本目**で、
> IADR-0579（描画の sanitize と CSP）・IADR-0580（ask / analyze のプロンプト）が「射程外」とした 2 点を扱う。

## 起点となる計画書（トレーサビリティ）

- 要求: **FR-17**（クラスタ要約を含むグラフ）・**FR-18**（AI 提案）・**NFR**（セキュリティ）。
- 計画 ADR: **ADR-0051**（AI 提案の ABAC 境界）・**ADR-0035**（クラスタ要約）・**ADR-0033**（GraphService は本文を持たない）・
  **ADR-0032**（BFF セッション・同一オリジン）。いずれとも矛盾しない（送る文書の集合・封の構築経路・BFF の経路は変えない）。

## 要件（コーディネーター指示 2026-10-22）

1. GraphService の提案とクラスタ要約のプロンプトに、IADR-0580 と同じ信頼境界を置く（共有か複写かはユニット境界の規則を確かめて IADR で決める）。
2. BFF の `/bff/*` の応答にセキュリティヘッダ（nosniff など）を付ける。API の応答に CSP が要るかを判断する。

## 受け入れ基準

| # | 基準 | 写像先 |
| --- | --- | --- |
| C1 | 提案の送信本文で、起点・候補の表題は境界つきの囲みに入り、囲みを閉じられず、改行で行を起こせない。決まりは囲みの外（前） | `SuggestionPromptTrustBoundaryTests` |
| C2 | クラスタ要約の送信本文も同じ（表題の囲み・畳み・決まりの位置） | `ClusterSummaryPromptTests.表題は境界つきの囲みに入り囲みを閉じられない` |
| C3 | 同じ封の `Render` は同じ本文を返し、境界は封ごとに変わる。並び順の性質（従前の試験）は保たれる | 同上・`境界は封ごとに変わる`・`入力の並び順を変えても送信本文は変わらない` |
| C4 | ask / analyze のプロンプトは従前どおり（共有化で形を変えない） | 既存の `AiAnalysisService.Tests` がそのまま通る |
| C5 | 共有の部品（改行の畳み・決まりの出し分け）の性質 | `Knowledge.Contracts.Tests.PromptTrustBoundaryTests` |
| C6 | BFF の全応答（成功・401・中継した SVG の図・404）に 4 つのヘッダが付く | `BffResponseSecurityHeadersTests`（ミドルウェアを外すと 4 本が赤。手元で実測） |

## 母集合（規則 9・10）

- LLM へプロンプトを送る場所: `git grep -n "CompletionApiRequest(\|ToProto(" -- src/knowledge/backend src/platform/backend` →
  AiAnalysisService（ask / analyze。IADR-0580 で済み）と GraphService（`SuggestionPrompt.Render`・`ClusterSummaryPrompt.Render`）だけ。
- `PromptTrustBoundary` の参照: AiAnalysisService の 3 ファイルと試験 1 本。共有へ移したので using を足す。
- BFF が文書（HTML）を返す口: `git grep -n "text/html" -- src/platform/backend/Bff src/knowledge/backend/Bff` → 0 件。
  文書として開かれ得る口は中継の口（図画像は後段の Content-Type をそのまま返す。SVG を含む）。

## 実装

- `src/knowledge/backend/Shared/Knowledge.Contracts/Llm/PromptTrustBoundary.cs`（AiAnalysisService から移して共有）。
- `GraphService/Domain/SuggestionPrompt.cs`・`Domain/Clustering/ClusterSummaryPrompt.cs`: 表題を囲む。境界は封ごと。
- `src/platform/backend/Bff/Platform.Bff/Foundation/Http/BffResponseSecurityHeadersMiddleware.cs`（新規）と `Program.cs` の登録。
- `scripts/contract-schema-baseline.json`: 型の追加（非破壊）を記録する。

## 配備前の手順（PoC）

- 配備は閉場後。GraphService・AiAnalysisService・BFF のイメージを差し替えるだけ（値・Secret の変更は無い）。
- 配備後の確認: 画面のログイン → ログアウト → 再ログインが通ること。AI チャットの回答、図画像の表示（SC-07）、取引ユニットの CAPTCHA の画像が従前どおり出ること。
  `curl -sI https://<ホスト>/bff/auth/me` の応答に 4 つのヘッダが付くこと。AI 提案の生成とクラスタ要約が従前どおり動くこと。
