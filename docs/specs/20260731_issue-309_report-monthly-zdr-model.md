---
title: 月報の割当モデルを ZDR 対応の最上位モデルへ改定する（issue #309・案 A）
type: spec
status: done
related_ids:
  - FR-11
  - FR-06
  - ADR-0010
  - ADR-0025
  - IADR-0022
  - IADR-0101
  - IADR-0112
  - IADR-0113
author: claude
created: 2026-07-31
updated: 2026-07-31
related_specs:
  - "../adr/IADR-0113_report-monthly-zdr-model.md"
  - "../adr/IADR-0112_report-kind-purposes-and-trade-decision-sonnet-5.md"
  - "../adr/IADR-0022_default-opus-and-fable5-copilot-routes.md"
  - "../adr/IADR-0101_default-model-opus-5.md"
  - "../adr/IADR-0102_trade-decision-model-pinning.md"
  - "../adr/IADR-0106_rag-answer-sonnet-5.md"
  - "./20260730_issue-420-421_report-and-trade-model-routing.md"
  - "../functional/FR-11_llm-egress-routing.md"
  - "../tests/FR-11_llm-egress-routing.md"
---

# 仕様書: 月報の割当モデルを ZDR 対応モデルへ改定する（issue #309）

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [ai-stock-trading#309](https://github.com/endazon/ai-stock-trading/issues/309)
  「月報の割当 `claude-fable-5` が非 ZDR のため既定機密区分では構造的に到達不能」。
  利用者は 3 案のうち **案 A（月報を ZDR モデルへ割り当て直す）**を採用した。
- 直前の実装判断: [[IADR-0112]]（報告書の種別別 purpose 割当・#422）。本作業はその**月報エントリのみ**を改定する。
- 計画根拠:
  - [AST/ADR-0014](../../planning/projects/ai-stock-trading/07_adr/ADR-0014_llm-model-assignment-revision.md)
    （取引判断・報告書生成の割当モデル改定・**Accepted**）§決定1 の割当表。**本作業で月報行の改定が必要**。
  - [AST/04_workflows/03_reporting-cycle](../../planning/projects/ai-stock-trading/04_workflows/03_reporting-cycle.md)
    （報告サイクル。月報→週報→日報→取引の方針階層）。
  - [ADR-0010](../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md)（LLM ゲートウェイ・本文凍結）§用途別ルーティング。
  - [ADR-0025](../../planning/projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md)（グローバル既定 Opus 5・Accepted）。本作業は `default` を変更しない。
  - [06_technical/08_data-egress-policy](../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md)（機密区分×ティア越境マトリクス・ZDR の注意点）。
- 計画への環流: AST/ADR-0014 §決定1 の月報割当を改定する新 ADR を計画リポへ起案する（**別 PR**）。
- 要求: FR-11（LLM 送信可否の統制・用途別ルーティング）／FR-06（報告書生成）。
- 本作業の実装判断は [[IADR-0113]]。

## 背景と問題（原因の確定）

### 事実 1: `claude-fable-5` は当該エンドポイントで唯一の非 ZDR モデル

`claude-managed` エンドポイントの設定は次のとおりで、`NonZdrModels` は `claude-fable-5` **のみ**である。

| モデル | ZDR | 根拠 |
| --- | --- | --- |
| `claude-fable-5` | 非対応 | `NonZdrModels` に列挙（[[IADR-0022]]） |
| `claude-opus-5` | 対応 | 未列挙。`DefaultModel`（[[IADR-0101]]） |
| `claude-opus-4-8` | 対応 | 未列挙（[[IADR-0102]] で確認済） |
| `claude-sonnet-5` | 対応 | 未列挙（[[IADR-0106]] で明記） |
| `claude-sonnet-4-6` / `claude-haiku-4-5` | 対応 | 未列挙 |

したがって **ZDR 対応モデルのうち最上位は `claude-opus-5`**（[[IADR-0101]] がグローバル既定＝最上位として採用した版数）。

### 事実 2: 月報だけが機密区分で割当を失う

`report-monthly` に非 ZDR の `claude-fable-5` を割り当てた結果、`confidential` / `restricted` では
`EligibleModels` から除外され `DefaultModel` へ**黙って**落ちる。[[IADR-0112]] 決定2 はこれを
「設定次第で割当が無音に変わる構造」として既知事実に留めたが、**割当そのものが機密区分の設定に
左右される状態**は方針階層の最上位である月報にとって望ましくない。

### 事実 3: issue #309 の原因分析には誤りがある（本仕様書で訂正する）

issue #309 は「機密区分 `internal` では非 ZDR モデルへ送れないため `Sent=false` へ縮退する」としているが、
実装はそうなっていない。

- `EgressMatrix.RequiresZeroDataRetention` は `Internal => false`（`Public` も false）。ZDR 除外が効くのは
  `confidential` / `restricted` / 未知区分だけである。
- `LlmRouter.EligibleModels` は ZDR 要件が無い区分では `Models` をそのまま返すため、`internal` では
  `claude-fable-5` が適格であり `Allowed=true` になる。
- ZDR 除外が効く `confidential` でも送信は拒否されず `DefaultModel` へフォールバックする（既存テスト
  `PostComplete_ConfidentialReportMonthly_FallsBackToZdrModel` が `Sent=true` を固定していた）。

`/complete` が `Sent=false` を返す分岐は 3 つある（`CompletionEndpoints`）。

1. `decision.Allowed == false`（越境拒否）
2. プロバイダ未登録（`GetKeyedService` が null）
3. **プロバイダ呼び出しの例外**（`呼び出し先 {Endpoint} が現在利用できません。`）

AST の `HttpReportNarrativeDrafter` は `Sent=false` のすべてに対して
`Sent=false・機密区分による縮退` という**固定文言**を出す。live で観測された WRN はこの文言であり、
3 分岐のどれであるかを示していない。同時刻に週報（`claude-opus-5`）が 30 秒タイムアウトまで到達していた
＝ API キーと経路は有効であることから、**live の実原因は分岐 3（`claude-fable-5` 呼び出しの上流エラー）**
である可能性が高い。稼働 live 環境には触れないため、本作業で実原因の確定は行わない。

**いずれの分岐であっても案 A は有効**である。`claude-fable-5` を月報から外せば、上流エラー説なら
呼び出しモデルが変わって解消し、ZDR 説なら矛盾そのものが消える。

## 対象範囲

### 変更する

| 対象 | 変更 |
| --- | --- |
| `LlmGateway.Api/appsettings.json` | `Llm:Routing:PurposeModels.report-monthly` を `claude-fable-5` → **`claude-opus-5`** |
| `LlmRouterTests` | 月報の期待値を更新。**T-23**（報告書用途は機密区分によらず同一モデル）を追加 |
| `CompletionRoutingEndpointTests` | 月報の期待値を更新（検証区分を `public` → 実運用の `internal` へ）。**T-23**（機密区分横断の同一性・報告書用途は `NonZdrModels` に載らない集合ガード）を追加 |
| `docs/functional/FR-11_*.md` | 用途別モデル解決・ZDR 除外・既定設定の記述を実態へ追随 |
| `docs/tests/FR-11_*.md` | T-22 を改定し T-23 を追加 |
| `docs/adr/IADR-0113_*.md` | 本作業の実装判断（新規） |
| `docs/adr/README.md` | 索引に IADR-0113 を追加 |

### 変更しない（意図的に対象外）

- **`NonZdrModels`**（案 C の否定）。`claude-fable-5` の ZDR 非対応は事実であり、ZDR 提供の有無を
  実契約で確認せずに外すのはポリシー違反になる。
- **report-service の機密区分**（案 B の否定）。報告書には取引実績・建玉・損益が載る。非 ZDR 送信の
  可否は情報分類の判断であり、実装側で独断に下げない。`LlmGateway:Confidentiality` の既定 `internal` は不変。
- **`Models`（利用許可集合）**。`claude-fable-5` は `analysis` が ZDR 非要件区分で使うため残す。削除は
  明示 `Model` 要求をしている呼び出し側に対する破壊的変更（[[IADR-0106]] と同じ理由）。
- **`analysis` の割当**。ZDR 非要件区分に限って fable-5 を使う意図的な設計（[[IADR-0022]]）であり、
  本 issue の対象外。
- **`report-weekly` / `report-daily` / `trade-decision` / `default` / `rag-answer` / `diagram-coding`**。
- **AST 側の実装**。`Model` は引き続き `null`（モデル決定権は基盤の LlmRouter・AST/IADR-0120 決定1）。
  ログ文言の是正（3 分岐を区別しない固定文）は AST リポの別 issue（本 PR ではスコープ外・§残参照）。
- **本番 values / SIMULATE・実弾フラグ**。`deploy/helm` の values は `Llm__ApiKey` の配線のみで
  `PurposeModels` を上書きしないため、本変更は appsettings.json の一箇所で完結する。

## 受け入れ基準

- [x] `Llm:Routing:PurposeModels.report-monthly` が `claude-opus-5`（ZDR 対応の最上位モデル）である
- [x] 採用モデルが claude エンドポイントの `Models`（利用許可集合）に含まれる（#376 / [[IADR-0102]] の罠回避）。
      既存の T-19 集合ガード `PurposeModels_AreAllRegisteredInClaudeEndpointModels` が緑
- [x] 実 `appsettings.json` 経由の `/complete` で、`report-monthly` が `internal` / `confidential` /
      `restricted` のいずれでも **`Sent=true`** かつ同一モデル（`claude-opus-5`）へ解決する
- [x] 報告書用途（`report-*`）の割当モデルが `NonZdrModels` に含まれないことを集合として固定する
- [x] `report-weekly` / `report-daily` / `trade-decision` / `analysis` / `rag-answer` / `default` は不変
- [x] 機能仕様書・テスト仕様書・IADR・ADR 索引が実態と一致する

## 実装方針（TDD）

1. **Red**: テストを先に更新する（月報の期待値を `claude-opus-5` へ、T-23 の 2 本を追加）。
   実 `appsettings.json` を読むエンドポイントテストが 3 件失敗することを確認する。
2. **Green**: `appsettings.json` の 1 行を改定する。
3. **Refactor**: 不要（設定値の改定であり構造は変えない）。
4. 文書（機能/テスト仕様書・IADR・索引）を実態へ追随させる。

## テスト観点

| 観点 | テスト |
| --- | --- |
| 月報が ZDR 対応の最上位モデルへ解決する | `LlmRouterTests.Route_ReportKindPurpose_ResolvesKindSpecificModel` / `CompletionRoutingEndpointTests.PostComplete_ReportKindPurpose_SelectsKindSpecificModel` |
| 機密区分を変えても割当が無音で変わらない | `LlmRouterTests.Route_ReportKindPurpose_ResolvesSameModelAcrossSensitivities` / `CompletionRoutingEndpointTests.PostComplete_ReportMonthly_KeepsAssignedModelAcrossSensitivities` |
| 報告書用途に非 ZDR モデルを割り当てない（設定ガード） | `CompletionRoutingEndpointTests.ReportPurposeModels_AreNotListedAsNonZdr` |
| 割当モデルが `Models` に登録済み（無音失効の防止） | `CompletionRoutingEndpointTests.PurposeModels_AreAllRegisteredInClaudeEndpointModels`（T-19・既存） |
| 他用途の割当が不変 | 既存 T-02 / T-10 / T-11 / T-13 / T-15 / T-19 / T-22 |

## 完了条件（DoD）

- [x] `dotnet build` / `dotnet test`（platform・knowledge 両ユニット）が緑
- [x] `dotnet format --verify-no-changes` が緑
- [x] `node scripts/check-doc-links.js` / `check-unit-dependencies.js` / `check-commit-messages.js` が緑
- [x] 起点 ID をブランチ名・コミット・コード・PR に残す

## 残（本 PR スコープ外）

- **計画 ADR の改定**: AST/ADR-0014 §決定1 の月報行（`claude-fable-5`）を改定する新 ADR を計画リポへ起案する。
  Accepted 済み ADR は本文凍結のため、ADR-0011 → ADR-0014 と同じ手順（新 ADR ＋ 旧 ADR への改訂節追記）を踏む。**別 PR**。
- **AST のログ文言**: `HttpReportNarrativeDrafter` が `Sent=false` の 3 分岐すべてに
  「機密区分による縮退」と出すため、今回の誤診が生じた。応答の `RoutingReason` / `Endpoint` を
  ログへ載せて分岐を区別できるようにする是正が要る（AST リポ・別 issue）。
- **live の実原因の確定**: 稼働環境に触れないため未確認。改定後も月報がプレースホルダのままなら、
  分岐 2/3 を疑い `RoutingReason` を確認する。
- **週報のタイムアウト**（30 秒）は別原因・別 issue。
