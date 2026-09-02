---
title: IADR-0340 trade-decision-screening を用途登録し、報告書 3 種のフォールバック鎖を追加する
type: impl-adr
status: Accepted
related_ids:
  - FR-11
  - ADR-0038
  - IADR-0007
  - IADR-0225
author: claude
created: 2026-09-02
updated: 2026-09-02
---

# IADR-0340: `trade-decision-screening` を用途登録し、報告書 3 種のフォールバック鎖を追加する

- 状態: Accepted
- 日付: 2026-09-02
- 決定者: 実装エージェント（worker）／AST#571

## 起点・関連

- 関連する計画書: `AST/ADR-0014` §決定1（用途別割当表・スクリーニング層の分離は AST 側 IADR-0212 が実施）、
  `AST/ADR-0017` 決定1（用途別フォールバック順序）・決定2（取引判断はフォールバック禁止）
- 関連する実装ADR（本リポジトリ）: [IADR-0007](IADR-0007_llm-egress-routing-config-driven.md)（config 駆動
  ルーティング）、[IADR-0225](IADR-0225_llm-purpose-fallback-chain-and-429-boundary.md)（用途別フォールバック
  順序・429 の境界）
- 関連する実装仕様書: [20260902 作業仕様書](../specs/20260902_571_trade-decision-screening-purpose.md)
- 起点 issue: AST#571（AST 側 #335／IADR-0212 の受け皿。基盤側の設定不足 2 点を解消する）

## コンテキストと課題

AST 側 IADR-0212（2026-08-28）は、二段判断（一次スクリーニング→二次本判断）の purpose を
`trade-decision-screening` / `trade-decision` に分離する配線を実装した。しかし本リポジトリの
`Llm:Routing:PurposeModels`（`appsettings.json`）に `trade-decision-screening` が未登録であり、
`ResolveModel` は未知 purpose を例外もログも無く `DefaultModel` へ落とす（IADR-0102 / IADR-0106 の
「無音失効」と同型）。この場合スクリーニング層の応答は AST 側の割当照合（本判断＝`claude-sonnet-5` ピン）
と食い違い「割当外」と判定され、AST 側で `Decision:EnableScreening=true` にしても全サイクルが
見送りへ倒れ続ける（安全側だが機能しない。AST 側 IADR-0212 §帰結）。

同様に `PurposeFallbackModels` に `report-daily` / `report-weekly` / `report-monthly` の鎖が未登録であり、
`AST/ADR-0017` 決定1 が定める報告書のフォールバック順序が機能しない。

### 実装の現状（着手前の確認）

- `appsettings.json` の `PurposeModels` は `rag-answer` / `analysis` / `diagram-coding` / `report-monthly` /
  `report-weekly` / `report-daily` / `trade-decision` / `default` の 8 エントリを持つ。
  `trade-decision-screening` は無い。
- `PurposeFallbackModels` は `analysis` / `diagram-coding` / `default` / `rag-answer` の 4 エントリのみ。
  `report-*` の鎖は無い（`trade-decision` も無いが、こちらは意図的な欠落＝フォールバック禁止。
  `TradeDecision_HasNoFallbackChainInProductionConfig` が固定している）。
- `claude-haiku-4-5` は `claude-managed` エンドポイントの `Models`（利用許可集合）・`NonZdrModels`
  除外対象外（ZDR 対応）として既に登録済みであり、追加の許可集合変更は不要。

## 検討した選択肢

| # | 案 | 内容 | 採否 |
| --- | --- | --- | --- |
| 1 | `trade-decision-screening` を `trade-decision` と同じ `claude-sonnet-5` に割り当てる | 本判断・スクリーニングを同一モデルにする | 不採用。二段判断の意図（軽量モデルによる費用統制つき絞り込み）が成立しない。AST 側 `LlmAssignmentsTests` が既に `claude-haiku-4-5` を期待値として固定している |
| 2 | `trade-decision-screening` を `PurposeModels` に登録し `claude-haiku-4-5` を割り当てる。`PurposeFallbackModels` へは追加しない | AST 側の設計・期待値と一致し、フォールバック禁止（決定2）とも整合する | **採用** |
| 3 | 報告書 3 種の鎖は登録を見送り、issue の一部だけを消化する | 手戻りが少ない | 不採用。issue #571 が明示的に要求する 2 点のうち 1 点を放置することになり、報告書生成のフォールバックが未成立のまま残る |

## 決定

選択肢 2 を採る。

### 決定 1: `trade-decision-screening` を `claude-haiku-4-5` へ割り当てる

`PurposeModels` へ `"trade-decision-screening": "claude-haiku-4-5"` を追加する。AST 側
`01_architecture-overview.md` §判断の二段化が定める層別割当（一次スクリーニング＝軽量モデル）を
基盤側の用途登録として反映する。

### 決定 2: `trade-decision-screening` は `PurposeFallbackModels` に登録しない

`AST/ADR-0017` 決定2「取引判断はフォールバックしない」の理由（別モデルで下した判断は再現性・監査可能性を
失う）は、二段判断のスクリーニング層にも同様に当てはまる。AST 側 `LlmAssignmentsTests` は
`TradeDecision` / `TradeDecisionScreening` の両方を `FallbackAllowed=false` として固定しており、本
リポジトリも同じ解釈を採る。既存の `TradeDecision_HasNoFallbackChainInProductionConfig` と対称の
`TradeDecisionScreening_HasNoFallbackChainInProductionConfig` を新設し、設定に鎖が誤って足されたら
落ちるようにする。

### 決定 3: 報告書 3 種のフォールバック鎖を登録する

`PurposeFallbackModels` へ次を追加する（`AST/ADR-0017` 決定1 の順序をそのまま反映）。

```
"report-monthly": [ "claude-sonnet-5" ]
"report-weekly":  [ "claude-sonnet-5" ]
"report-daily":   [ "claude-haiku-4-5" ]
```

いずれも第 1 候補より安価側の 1 段下位であり（`analysis` / `diagram-coding` / `default` / `rag-answer` の
既存 4 鎖と同じ設計）、発火で費用が上振れすることはない。

### 決定 4: 既存の全数検証テストは無改修のまま新エントリを検証範囲に含める

`PurposeModelsAndFallbacks_AreAllRegisteredInClaudeEndpointModels`（T-19）・
`PurposeModels_AreNotListedAsNonZdr`（T-23）はいずれも `PurposeModels` / `PurposeFallbackModels` を
辞書として走査する実装であり、新エントリを追加するだけで検証範囲へ自動的に含まれる。**並行する
ガードを新設しない**（IADR-0225 が既に採った「同じ不変条件を 2 本で守ると片方が古くなる」という
判断を踏襲する）。

### 決定 5: 旧テスト `PostComplete_ReportWeekly_When400_DoesNotFallBack`（T-25e2）を移し替える

決定3 により `report-weekly` が鎖を持つようになったため、「鎖を持たない用途は 400 でもフォールバック
しない」ことを固定していた旧テストの前提が崩れる。**`trade-decision-screening`（恒久的に鎖を持たない
用途。決定2）へ移し替える**。`report-weekly` 自体は新設の `PostComplete_ReportKindPurpose_When400_
FallsBackToKindSpecificModel`（Theory）でフォールバック発火の側を検証する。

## 理由

- 決定1・2 は issue #571 の要求（AST 側の実装 IADR-0212 が既に固定した期待値）をそのまま反映するもので
  あり、基盤側で新たな設計判断を要しない。判断が要るのはフォールバックの扱い（決定2）だけであり、
  `AST/ADR-0017` 決定2 の理由（再現性・監査可能性）がスクリーニング層にも及ぶことを確認して決定した。
- 決定3 は同 ADR 決定1 が定める順序をそのまま反映するだけであり、既存 4 鎖（`analysis` 等）と同型である。
- 決定4・5 は IADR-0225 が既に採用した「並行ガードを作らない」「同じ不変条件は 1 本で守る」という設計
  方針の継続であり、新たな判断を要しない。

## 結果

- 良い影響:
  - AST 側の二段判断が、基盤側の登録待ちという構造的な障害から解放される（AST 側の `Decision:
    EnableScreening` 反転と組み合わさって初めて機能する）。
  - 報告書 3 種のフォールバックが機能し、単発の障害で方針階層（月報→週報→日報）が途切れる不利益が
    軽減される。
  - 全数検証テスト（T-19 / T-23）が無改修のまま新エントリを守り続ける。
- 悪い影響・トレードオフ:
  - 本 PR 単独では実運用への効果が観測できない（AST 側の反転・実クラスタへの反映が別途要る）。
  - `trade-decision` と `trade-decision-screening` という 2 つの「鎖を持たない」用途が並立し、
    今後 3 つ目の同種用途が増えたときに個別ガードを都度足す運用が続く（並行ガードを避けた代償として、
    「鎖を持たないこと」自体の網羅性は個別テストの列挙に依存する）。
- フォローアップ: 実クラスタでの疎通確認（`POST /complete` の実測）は作業仕様書 §確認手順のとおり、
  本 PR のマージ・再デプロイ後に行う。AST 側の実 LLM 二段判断の確認は AST の定時サイクル再デプロイ後、
  別セッションが行う。

## 関連

- Supersedes: なし
- Superseded by: なし
