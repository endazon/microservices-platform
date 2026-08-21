---
title: 報告書を種別ごとの用途へ分離し、取引判断の割当モデルを改定する（issue #420 / #421）
type: spec
status: done
related_ids:
  - FR-11
  - ADR-0010
  - ADR-0022
  - ADR-0025
  - IADR-0022
  - IADR-0101
  - IADR-0102
  - IADR-0106
  - IADR-0112
author: claude
created: 2026-07-30
updated: 2026-07-30
related_specs:
  - "../adr/IADR-0112_report-kind-purposes-and-trade-decision-sonnet-5.md"
  - "../adr/IADR-0102_trade-decision-model-pinning.md"
  - "../adr/IADR-0106_rag-answer-sonnet-5.md"
  - "../adr/IADR-0101_default-model-opus-5.md"
  - "./20260726_issue-381_rag-answer-sonnet-5.md"
  - "./20260725_ast-adr-0011_trade-decision-model-pinning.md"
  - "../../docs/functional/FR-11_llm-egress-routing.md"
  - "../../docs/tests/FR-11_llm-egress-routing.md"
---

# 仕様書: 報告書の種別別用途と取引判断モデルの改定（issue #420 / #421）

## 起点となる計画書（トレーサビリティ）

- 起点 issue:
  - [#420](https://github.com/endazon/microservices-platform/issues/420)
    報告書の種別ごと（月報/週報/日報）に purpose を分けてモデルを割り当てる。
  - [#421](https://github.com/endazon/microservices-platform/issues/421)
    `trade-decision` の割当モデルを `claude-sonnet-5` へ改定する（AST/ADR-0011 の手続きを経る）。
- 計画根拠:
  - AST/ADR-0011（計画リポ）
    （取引判断の LLM モデル固定・**Accepted**）§決定・§理由。
  - AST/04_workflows/03_reporting-cycle（計画リポ）
    （報告サイクル。取引方針を **月報→週報→日報** の階層で管理し、確定した日報が翌営業日の取引方針となる）。
  - ADR-0010（計画リポ）
    （LLM ゲートウェイ・本文凍結）§用途別ルーティング。
  - ADR-0025（計画リポ）
    （グローバル既定 Opus 5・Accepted）。本作業は `default` を変更しない。
- 計画への環流: [planning#50](https://github.com/endazon/project-planning/issues/50)
  （AST/ADR-0011 の改定を新 ADR で起案する依頼）。**本作業に先行して起票済み**。
- 要求: FR-11（LLM 送信可否の統制・用途別ルーティング）。
- 設計: [IADR-0022](../adr/IADR-0022_default-opus-and-fable5-copilot-routes.md)（ゲートウェイ経路・ZDR 除外）、[IADR-0102](../adr/IADR-0102_trade-decision-model-pinning.md)（`Models` 未登録による無音失効の罠）、
  [IADR-0106](../adr/IADR-0106_rag-answer-sonnet-5.md)（同罠の集合ガード T-19）。
- 本作業の実装判断は [IADR-0112](../adr/IADR-0112_report-kind-purposes-and-trade-decision-sonnet-5.md)。
- AST 側の対応（種別ごとの purpose 送出・上位方針の feed-forward）は
  [AST#291](https://github.com/endazon/ai-stock-trading/issues/291) /
  [#293](https://github.com/endazon/ai-stock-trading/issues/293)。**別リポ・別 PR**。

## 背景と問題（原因の確定）

利用者は本システムを「生成 AI を活用した金融商品の完全自動取引システム」と定義し、**月報/週報/日報を
「次の取引に活かす方針書」**と位置づけたうえで、用途ごとの割当モデルを仕様として指定した。

| 用途 | 指定モデル |
| --- | --- |
| 月報 | `claude-fable-5` |
| 週報 | `claude-opus-5` |
| 日報 | `claude-sonnet-5` |
| 取引判断（`trade-decision`） | `claude-sonnet-5` |

現行の `Llm:Routing:PurposeModels`（`LlmGateway.Api/appsettings.json:41-47`）は次のとおりで、
**報告書用途のエントリが存在せず**、`trade-decision` は `claude-opus-4-8` にピン留めされている。

```jsonc
"PurposeModels": {
  "rag-answer":     "claude-sonnet-5",
  "analysis":       "claude-fable-5",
  "diagram-coding": "claude-haiku-4-5",
  "trade-decision": "claude-opus-4-8",   // ← 指定は claude-sonnet-5
  "default":        "claude-opus-5"
}
```

### 問題 1: 報告書は種別によらず単一モデルで生成されている

AST の report-service は単一の purpose `report-narrative` で `/complete` を呼ぶ
（`ReportService.Worker/Program.cs`。既定値。`Model: null`）。`report-narrative` は `PurposeModels` に
存在しないため `LlmRouter.ResolveModel` は `DefaultModel`（`claude-opus-5`）へ着地する。結果、
**月報・週報・日報のすべてが `claude-opus-5`** で生成されている。

方針階層の最上位である月報（翌月の投資方針・リスク上限案を含む）と、最下位の日報（翌営業日の
監視銘柄・売買条件）が同一モデルで書かれており、「上位ほど難度が高い」という階層の意味が
モデル選択に反映されていない。

### 問題 2: 週報の一致は偶然であり、`default` 改定で無音に失効する

指定値のうち週報＝`claude-opus-5` は現行の実効モデルと一致する。しかしそれは `default` 追随の結果で
あって、割当として固定されていない。`default` を改定した瞬間（[IADR-0101](../adr/IADR-0101_default-model-opus-5.md) が実際に行った操作）に
週報のモデルは**何の通知もなく変わる**。[IADR-0102](../adr/IADR-0102_trade-decision-model-pinning.md) が取引判断で踏んだ失効と同じ構造であり、
「現在値が一致しているから設定不要」は誤りである。

### 問題 3: `trade-decision` の改定は設定書き換えだけでは行えない

AST/ADR-0011 §決定は次を定める。

> モデルを更新する場合は、新モデルで Stage 0（コスト2倍・ウォークフォワード・DSR/PBO 補正）を再実行し、
> エッジが維持されることを確認してから採用する。更新は月報レビュー時のみとし、更新前後のモデル ID を
> 報告書へ記録する。

[IADR-0102](../adr/IADR-0102_trade-decision-model-pinning.md) §結果も「**本エントリの更新には Stage 0 再検証が要る。設定値の書き換えだけで更新しては
ならない。**」と明記している。手続きの詳細と判断は [IADR-0112](../adr/IADR-0112_report-kind-purposes-and-trade-decision-sonnet-5.md) §決定 3 に記す。

### 踏んではならない罠（[IADR-0102](../adr/IADR-0102_trade-decision-model-pinning.md) / #376 の再発）

`LlmRouter.ResolveModel` の用途別解決は `eligible.Contains(purposeModel)` を条件とする。

```csharp
// LlmRouter.cs:104-106
if (_options.PurposeModels.TryGetValue(request.Purpose, out var purposeModel)
    && eligible.Contains(purposeModel))
    return purposeModel;
```

`eligible` はエンドポイントの `Models`（利用許可集合）から導出されるため、`PurposeModels` へ用途を
追加しても対象モデルが `Models` に無ければ、**例外もログも出さずに `DefaultModel` へ黙って落ちる**。

本作業で追加する 3 モデル（`claude-fable-5` / `claude-opus-5` / `claude-sonnet-5`）と改定後の
`claude-sonnet-5` は、いずれも `claude-managed` の `Models` に**登録済み**である。よって `Models` への
追加は不要だが、[IADR-0106](../adr/IADR-0106_rag-answer-sonnet-5.md) の集合ガード（T-19 `PurposeModels_AreAllRegisteredInClaudeEndpointModels`）が
新エントリも自動的に検査するため、これを恒久ガードとして機能させる。

### 追加の罠: `NonZdrModels` による月報の無音失効

`claude-managed` の `NonZdrModels` は `["claude-fable-5"]` である。`EligibleModels` は
`EgressMatrix.RequiresZeroDataRetention(sensitivity)` が真のとき（`confidential` / `restricted`）に
`NonZdrModels` を除外するため、**機密区分を上げると月報だけが `claude-fable-5` を失い
`DefaultModel` へ落ちる**。

report-service の既定 `LlmGateway:Confidentiality` は `internal`（ZDR 要件なし）であり現状は成立する。
ただしこれは「設定次第で無音に失効する」構造であるため、テストで挙動を固定し IADR に明記する。
`analysis` = `claude-fable-5` が既に同じ性質を持っており（T-13 / `PostComplete_ConfidentialAnalysis_FallsBackToZdrModel`）、
本作業は新しい脆さを持ち込むのではなく、既存の性質を月報にも適用する。

## 対象範囲

### 変更する

| 対象 | 変更内容 |
| --- | --- |
| `LlmGateway.Api/appsettings.json` | `PurposeModels` に `report-monthly` / `report-weekly` / `report-daily` を追加。`trade-decision` を `claude-opus-4-8` → `claude-sonnet-5` へ改定 |
| `LlmRoutingOptions.cs` | 用途別モデルの説明コメントを新しい割当へ追随 |
| `LlmRouterTests.cs` | 固定値フィクスチャへ 3 用途を追加し `trade-decision` を改定。T-22 を追加 |
| `CompletionRoutingEndpointTests.cs` | 用途別モデル Theory へ 3 用途 + `trade-decision` を追加。ZDR 区分での月報フォールバックを固定 |
| `docs/functional/FR-11_llm-egress-routing.md` | 既定設定表の用途一覧を更新 |
| `docs/tests/FR-11_llm-egress-routing.md` | T-22 を追加 |
| `docs/adr/README.md` | IADR-0112 の索引行を追加 |

### 変更しない（意図的に対象外）

- **`Models`（利用許可集合）**。追加する 4 モデルはすべて登録済み。`claude-opus-4-8` も**残す**
  （`Models` は「割当」ではなく「利用を許可するモデル集合」であり、削除すると明示 `Model` 要求の
  呼び出し側が黙って別モデルへ落ちる破壊的変更になる。[IADR-0106](../adr/IADR-0106_rag-answer-sonnet-5.md) の判断を踏襲）。
- **`NonZdrModels`**。`claude-fable-5` の ZDR 非対応は事実であり、月報が `confidential` 以上で
  除外されるのは**仕様どおりの安全側**である。
- **`default`（`claude-opus-5`）・`rag-answer` / `analysis` / `diagram-coding`**。ADR-0025 §決定により不変。
- **既定 `max_tokens`（4096）**。report-service は明示 4096 を送っており（`HttpReportNarrativeDrafter`）、
  本作業でモデルが変わっても要求値は変わらない。実測による再調整は #380 / AST#243。
- **`report-narrative` purpose の削除**。AST 側が種別ごとの purpose へ移行するまでの間、および
  `LlmGateway:Purpose` を明示設定した既存デプロイのために、未知 purpose として `default` へ
  落ちる従来挙動を維持する（非破壊）。
- **AST 側の実装**（種別ごとの purpose 送出・上位方針の feed-forward）。別リポ・別 PR（#291 / #293）。
- **Stage 0 再検証そのもの**。実行不能（AST#208）であり、実弾解禁の必須ゲートとして追跡へ回す（AST#296）。

## 受け入れ基準

- [x] `PurposeModels` に `report-monthly`=`claude-fable-5` / `report-weekly`=`claude-opus-5` /
      `report-daily`=`claude-sonnet-5` が存在する
- [x] `PurposeModels.trade-decision` が `claude-sonnet-5` である
- [x] 4 用途がいずれも `DefaultModel` へ落ちずに指定モデルを返すことをテストで固定した
- [x] 全 `PurposeModels` の値が `claude-managed` の `Models` に含まれる（T-19 の集合ガードが新用途も検査する）
- [x] `confidential` × `report-monthly` が `claude-fable-5` を除外し `DefaultModel` へ落ちることを
      テストで固定した（無音失効の明示化）
- [x] AST/ADR-0011 の手続き（計画への環流・Stage 0 再検証の要否判断）を [IADR-0112](../adr/IADR-0112_report-kind-purposes-and-trade-decision-sonnet-5.md) に記録した
- [x] Stage 0 再検証のゲートを追跡可能なブロッカー（AST#296）として実体化し、AST#217 / AST#208 から参照させた
- [x] 計画側 ADR が非承認だった場合のロールバック条件を追跡可能な形（[#423](https://github.com/endazon/microservices-platform/issues/423)）にした
- [x] 取引判断のバージョン固定原則（`default` に自動追随しない）が維持されている

## 判断: AST/ADR-0011 の手続きをどう踏むか

利用者は仕様として取引判断 = `claude-sonnet-5` を指定した。AST/ADR-0011（Accepted）は版数変更に
Stage 0 再検証を要求している。両立の手順を次のとおり定める（詳細と根拠は [IADR-0112](../adr/IADR-0112_report-kind-purposes-and-trade-decision-sonnet-5.md) §決定 3）。

1. **計画への環流を先行させる。** ADR は Accepted 後に本文を実質変更しない規約
   （`planning/.claude/rules/adr.md`）に従い、**ADR-0011 を書き換えず新 ADR を起案**する依頼を
   [planning#50](https://github.com/endazon/project-planning/issues/50) へ起票済み。
   起案はまだ Accepted ではない＝実装は「起案済み・未承認」で先行する。承認可否の確認と、
   非承認時に `claude-opus-4-8` へ戻す動作は [#423](https://github.com/endazon/microservices-platform/issues/423) で追跡する（IADR 本文は運用を強制しないため）。
2. **Stage 0 再検証は「要る」。ただし実弾解禁の必須ゲートとして課す。**
   - ADR-0011 が Stage 0 一致を求める理由は「検証したモデルと**本番**モデルの乖離により検証妥当性が
     失われる」ことにある。現状は実弾 OFF（`TrdEnv=real` は起動時停止・閂がコード固定。AST#217）であり、
     実資金の取引が存在しない。したがって現時点の設定変更で毀損する検証妥当性は存在しない。
   - Stage 0 再検証は**現時点で実行不能**である（バックテストの実過去データ源が未接続。AST#208）。
     先行条件とすると利用者の仕様指定が無期限に凍結される。
   - よって設定は改定し、`claude-sonnet-5` での Stage 0 再検証を**実弾解禁の必須ゲート**として課す。
     このゲートは IADR の記述だけでは運用を強制しないため、[AST#296](https://github.com/endazon/ai-stock-trading/issues/296)
     を「実弾解禁ブロッカー」として起票し、実弾解禁の設計 issue（AST#217）と前提 issue（AST#208）の
     双方から参照させた。
3. **バージョン固定の原則は維持する。** 改定するのはピンの値であって、ピンする仕組みではない。
   `trade-decision` は引き続き明示エントリを持ち、`default` の改定に自動追随しない。

## 実装方針（TDD）

1. **Red**: `CompletionRoutingEndpointTests` の用途別 Theory へ `report-monthly` / `report-weekly` /
   `report-daily` / `trade-decision` の期待値を追加する。このテストは `TestWebApplicationFactory` が
   **実 `appsettings.json` のルーティング設定を読む**ため、設定を変える前は失敗する。
   `confidential` × `report-monthly` のフォールバック（T-20）も追加する。
   `LlmRouterTests` の固定値フィクスチャも同様に更新する。
2. **Green**: `appsettings.json` の `PurposeModels` を更新する。
3. **Refactor/追随**: コード内コメント・機能仕様書・テスト仕様書・IADR・索引を更新する。
4. **検証**: `dotnet build` / `dotnet test`（platform・knowledge 両ユニット）/
   `dotnet format --verify-no-changes`。

## テスト観点

| ID | 観点 | 期待 |
| --- | --- | --- |
| T-19（既存・自動拡張） | `PurposeModels ⊆ Models` の集合ガード | 追加した 3 用途の割当モデルも `Models` に含まれる |
| T-22（新規） | 報告書の種別別モデル | `report-monthly`→`claude-fable-5` / `report-weekly`→`claude-opus-5` / `report-daily`→`claude-sonnet-5`。いずれも `DefaultModel` へ落ちない |
| T-22（新規） | 取引判断の改定 | `trade-decision`→`claude-sonnet-5`。`DefaultModel`（`claude-opus-5`）へ落ちない＝固定が生きている |
| T-22（新規） | 月報の ZDR 除外 | `confidential` × `report-monthly` は `claude-fable-5` を除外し `claude-opus-5` へ落ちる（無音失効の明示化） |

## 完了条件（DoD）

- `dotnet build` / `dotnet test` が platform・knowledge 両ユニットで通る
- `dotnet format --verify-no-changes` が両ユニットで通る
- 上表の受け入れ基準がすべてチェック済み
- `docs/DEFINITION_OF_DONE.md` を満たす
