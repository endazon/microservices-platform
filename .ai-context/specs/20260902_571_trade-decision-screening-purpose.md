---
title: 作業仕様書 — trade-decision-screening の用途登録と報告書 3 種のフォールバック鎖登録（AST#571）
type: spec
status: done
related_ids:
  - FR-11
  - ADR-0038
  - IADR-0007
  - IADR-0225
author: claude
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0014_llm-model-assignment-revision.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0017_llm-fallback-policy.md
related_specs:
  - "../adr/IADR-0337_trade-decision-screening-purpose-registration.md"
issue: "AST#571"
---

# 作業仕様書 — trade-decision-screening の用途登録と報告書 3 種のフォールバック鎖登録（AST#571）

## 背景・起点

ai-stock-trading（AST）#335（PR #555・IADR-0212 ほか）は、二段判断（一次スクリーニング→二次本判断）の層別
purpose を配線した。しかし基盤（本リポジトリ）の `Llm:Routing:PurposeModels` に用途
`trade-decision-screening` が未登録であり、AST 側で `Decision:EnableScreening=true` にしても
一次スクリーニングの応答が本判断の割当（`trade-decision`＝`claude-sonnet-5` ピン留め）と照合されて
「割当外」と判定され、全サイクルが見送りへ倒れる（安全側だが機能しない）。

同様に `PurposeFallbackModels` に `report-daily` / `report-weekly` / `report-monthly` の鎖が未登録であり、
報告書生成のフォールバック（計画 `AST/ADR-0017` 決定1 が定める安価側への順序）が機能しない。

AST#571 はこの 2 点の基盤側不足を解消する受け皿 issue である。本作業仕様書は AST#571 のうち
**本リポジトリ（microservices-platform）が担当する範囲**を扱う。AST 側の `Decision:EnableScreening` 既定
反転は別リポジトリ（ai-stock-trading）の別 PR（作業仕様書 `20260902_571_enable-screening-default.md`）で行う。

## 計画書の確認（起案前の確認）

- `AST/ADR-0014` §決定1 の 2026-08-01 改訂表（`AST/ADR-0017` により部分改定）: 用途別の割当モデルと
  第 1・第 2 候補を規定する。取引判断（本判断・二段判断のスクリーニング）の第 1 候補は `claude-sonnet-5`
  だが、**AST 側実装（IADR-0212）は本判断とスクリーニングを別 purpose に分離しており**、スクリーニング層は
  `01_architecture-overview.md` §判断の二段化により軽量モデル（`claude-haiku-4-5`）を充てる設計である
  （AST 側 `LlmAssignmentsTests.割当表は計画の確定値と一致する` が期待値として固定している。本リポジトリの
  対応表はこの期待値をそのまま登録する側に立つ）。
- `AST/ADR-0017` 決定1: 用途別フォールバック順序。月報 `claude-opus-5→claude-sonnet-5`、週報
  `claude-opus-5→claude-sonnet-5`、日報 `claude-sonnet-5→claude-haiku-4-5`。
- `AST/ADR-0017` 決定2: **取引判断（本判断・二段判断のスクリーニングの両方）はフォールバック禁止**。
  「別モデルで下した判断は再現性・監査可能性を失った別物」という理由は本判断・スクリーニングで共通する。

## 対象範囲

- 対象: `src/platform/backend/Services/LlmGateway/appsettings.json`（`PurposeModels` / `PurposeFallbackModels`）、
  `Tests/CompletionRoutingEndpointTests.cs`、`Tests/CompletionFallbackEndpointTests.cs`、
  `docs/functional/FR-11_llm-egress-routing.md`（必須機能仕様書）
- 対象外: AST 側の `Decision:EnableScreening` 既定・`DecisionOptionsLoader`（別リポジトリ・別 PR）。
  実クラスタへの反映・実疎通確認は本 PR のマージ後に別途行う（作業仕様書 §確認手順）。

## 実装内容

1. `appsettings.json`:
   - `PurposeModels` へ `"trade-decision-screening": "claude-haiku-4-5"` を追加。
   - `PurposeFallbackModels` へ `"report-monthly": ["claude-sonnet-5"]` / `"report-weekly":
     ["claude-sonnet-5"]` / `"report-daily": ["claude-haiku-4-5"]` を追加。
   - `trade-decision` / `trade-decision-screening` はいずれも `PurposeFallbackModels` へ**追加しない**
     （`AST/ADR-0017` 決定2。既存の `TradeDecision_HasNoFallbackChainInProductionConfig` と同型のガードを
     `trade-decision-screening` にも新設する）。
   - `claude-haiku-4-5` は既に `claude-managed` エンドポイントの `Models`（利用許可集合）に登録済みであり
     （既存設定を確認済み）、新規追加は不要。
2. `CompletionRoutingEndpointTests.cs`:
   - `TradeDecisionScreening_HasNoFallbackChainInProductionConfig`（新設。`TradeDecision_...` と対）
   - `PostComplete_TradeDecisionScreening_SelectsHaiku45AndDoesNotFallBackToDefault`（新設）
   - 既存の全数検証テスト（`PurposeModelsAndFallbacks_AreAllRegisteredInClaudeEndpointModels`・
     `PurposeModels_AreNotListedAsNonZdr`）は **PurposeModels/PurposeFallbackModels を辞書として走査する
     実装のため無改修のまま新エントリを自動的に検証範囲へ含む**（コード変更不要）。
3. `CompletionFallbackEndpointTests.cs`:
   - `PostComplete_ReportKindPurpose_When400_FallsBackToKindSpecificModel`（新設 Theory。report-monthly /
     report-weekly / report-daily の 3 ケース）
   - 旧 `PostComplete_ReportWeekly_When400_DoesNotFallBack`（T-25e2）は前提が崩れる
     （report-weekly が鎖を持つようになったため）。**`PostComplete_TradeDecisionScreening_When400_DoesNotFallBack`
     へ移し替える**（trade-decision-screening は恒久的に鎖を持たない用途であるため、
     「鎖が無い用途は落ちない」という分岐の固定先として適切）。
4. `docs/functional/FR-11_llm-egress-routing.md`（必須機能仕様書）: 用途一覧・用途別モデル解決・
   フォールバック順序・受け入れ基準の各節へ `trade-decision-screening` と報告書 3 種の鎖を反映。
   trace ブロック（frontmatter 直後の HTML コメント）へ `IADR-0337` と `AST#571` を追加。

## 判断が要った点

### 判断1: `trade-decision-screening` の割当モデルは AST 側の設計（`claude-haiku-4-5`）にそのまま従う

計画 `AST/ADR-0014` §決定1 の表は「取引判断（本判断・二段判断のスクリーニング）」を 1 行で
`claude-sonnet-5` と書いているが、これは 2026-08-01 時点でスクリーニング層がまだ独立 purpose に
分離されていなかった頃の記述である。AST 側の実装 IADR-0212（2026-08-28）は「01_architecture-overview
§判断の二段化」の層別割当（スクリーニング層＝軽量モデル）に基づき、独立 purpose
`trade-decision-screening` を新設して `claude-haiku-4-5` を割り当てている。AST 側
`LlmAssignmentsTests.割当表は計画の確定値と一致する` はこの値をスナップショットとして固定済みであり、
**本リポジトリはこの期待値をそのまま基盤側の設定として反映する**（AST 側が検証側・本リポジトリが
供給側という役割分担は IADR-0215 が定めた構図と同型）。計画表記との字面の不一致（1 行 vs 2 用途）は
計画進化の経緯によるものであり、AST 側で解消済みのため本リポジトリで新たな判断を要しない。

### 判断2: `trade-decision-screening` は `PurposeFallbackModels` へ登録しない

`AST/ADR-0017` 決定2「取引判断はフォールバックしない」は文言上 `trade-decision` のみを名指すが、
理由（別モデルで下した判断は再現性・監査可能性を失う）は二段判断のスクリーニング層にも同様に当てはまる。
AST 側 `LlmAssignmentsTests.取引判断系はフォールバックを禁止する` は `TradeDecision` /
`TradeDecisionScreening` の両方に対して `FallbackAllowed=false` を固定しており、AST 側は既にこの解釈を
採用している。本リポジトリも同じ解釈で `PurposeFallbackModels` に鎖を追加しない。

## 受け入れ基準

- [x] `trade-decision-screening` が `PurposeModels` に登録され `claude-haiku-4-5` へ解決する
- [x] `trade-decision-screening` は `DefaultModel`（`claude-opus-5`）にも `trade-decision` の割当
      （`claude-sonnet-5`）にも解決されない
- [x] `trade-decision-screening` は `PurposeFallbackModels` に登録されない（否定形。専用テストで固定）
- [x] `report-monthly` / `report-weekly` / `report-daily` が HTTP 400 系でそれぞれの第 2 候補へ
      フォールバックする
- [x] 既存の全数検証テスト（T-19 / T-23 相当）が無改修のまま新エントリを検証範囲に含む
- [x] `dotnet build` / `dotnet test`（LlmGateway.Tests）が通る
- [x] 必須機能仕様書（`FR-11_llm-egress-routing.md`）を更新した

## 確認手順（実クラスタ。本 PR のマージ・反映後に実施）

1. llmgateway-service を再ビルド・再デプロイする（`scripts/` の該当スクリプト。Docker デーモン停止中のため
   nerdctl 経路 `K8S_LOCAL_RUNTIME=rancher` 相当を使う）。
2. クラスタ内から `POST /complete`（`Purpose=trade-decision-screening`）を 1 回叩き、応答の `Model` が
   `claude-haiku-4-5` であることを確認する。
3. `Purpose=report-daily` で存在しないモデルを指定した失敗を模擬し、鎖（`claude-haiku-4-5`）へ
   フォールバックすることを確認する（費用が発生するため各 1 回まで）。
4. AST 側の実 LLM 二段判断の確認は、AST の定時サイクル（呼び出し元）が develop 再デプロイ後に別途行う
   （AST 側作業仕様書 `20260902_571_enable-screening-default.md` §確認手順を参照）。
