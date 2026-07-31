---
title: IADR-0113 月報の割当モデルを ZDR 対応の最上位モデル（claude-opus-5）へ改定する
type: impl-adr
status: Accepted
related_ids:
  - FR-11
  - FR-06
  - ADR-0010
  - ADR-0025
  - IADR-0022
  - IADR-0101
  - IADR-0112
author: claude
created: 2026-07-31
updated: 2026-07-31
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0014_llm-model-assignment-revision.md (取引判断・報告書生成の割当モデル改定・Accepted)"
  - "../../planning/projects/ai-stock-trading/04_workflows/03_reporting-cycle.md (報告サイクル: 月報→週報→日報→取引の方針階層)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md (LLM ゲートウェイ設計・Accepted・本文凍結)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0025_llm-model-opus-5.md (グローバル既定を Opus 5 へ改定・Accepted)"
  - "../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md (機密区分×ティア越境マトリクス・ZDR)"
---

# IADR-0113: 月報の割当モデルを ZDR 対応の最上位モデルへ改定する

- 状態: Accepted
- 日付: 2026-07-31
- 決定者: claude（実装）／利用者（[ai-stock-trading#309](https://github.com/endazon/ai-stock-trading/issues/309) の 3 案から**案 A** を採用）

## 起点・関連

- 起点 issue: [ai-stock-trading#309](https://github.com/endazon/ai-stock-trading/issues/309)
  「月報の割当 `claude-fable-5` が非 ZDR のため既定機密区分では構造的に到達不能」。
- 直前の実装判断: [[IADR-0112]]（報告書の種別別 purpose 割当・[#422](https://github.com/endazon/microservices-platform/pull/422)）。
  本 IADR は同 IADR の**決定1 の月報行と決定2 の帰結**のみを改定する。IADR-0112 本文と ADR 索引の
  同 IADR 行には、改定済みである旨の日付付き追記を入れた（`docs/adr/README.md` の運用規約と
  [[IADR-0084]] / [[IADR-0087]] の前例に倣う。旧 IADR だけを読んだ担当者が古い値を現行と誤認しないため）。
  AST 側の `IADR-0120` の割当表も同様に陳腐化するが、別リポのため
  [ai-stock-trading#285](https://github.com/endazon/ai-stock-trading/issues/285)（実効モデル記述の陳腐化）へ追記して引き渡した。
- 仕様書: `docs/specs/20260731_issue-309_report-monthly-zdr-model.md`。
- 計画への環流: AST/ADR-0014 §決定1 の月報割当を改定する新 ADR を計画リポへ起案する（**別 PR**）。
- ルーティング設計・ティア判定・ZDR 除外ロジック（[[IADR-0022]]）は変更しない。

## コンテキストと課題

[[IADR-0112]] 決定1 は利用者の仕様指定に従い `report-monthly` へ `claude-fable-5`（方針階層の最上位＝
最も難度が高い、の想定）を割り当てた。しかし同じ設定ファイル内で `claude-fable-5` は
`claude-managed` エンドポイントの `NonZdrModels` に列挙されている。

```json
"PurposeModels": { "report-monthly": "claude-fable-5", ... },
"Endpoints": [{ "Name": "claude-managed",
  "Models":       [ "claude-fable-5", "claude-opus-5", ... ],
  "NonZdrModels": [ "claude-fable-5" ] }]
```

[[IADR-0112]] 決定2 はこれを「`confidential` / `restricted` では月報だけが `DefaultModel` へ黙って落ちる」
という**既知事実**として受け入れ、テストで挙動を固定した。しかし方針階層の最上位である月報の割当が
**呼び出し側の機密区分設定に左右されて無音で変わる**状態は、[[IADR-0102]] が取引判断について
「割当が無音で失効する構造を作らない」と定めた方針と整合しない。

利用者は #309 の 3 案（A: 月報を ZDR モデルへ / B: 機密区分を下げる / C: fable-5 を ZDR 扱いにする）
のうち **案 A** を採用した。

### 実 ZDR 分類（設定で確認した事実）

`NonZdrModels` は `["claude-fable-5"]` のみであり、登録済み 6 モデルの分類は次のとおり。

| モデル | ZDR | 根拠 |
| --- | --- | --- |
| `claude-fable-5` | 非対応 | `NonZdrModels` に列挙（[[IADR-0022]]・30 日保持要件） |
| `claude-opus-5` | 対応 | 未列挙。`DefaultModel`（[[IADR-0101]]） |
| `claude-opus-4-8` | 対応 | 未列挙（[[IADR-0102]] で確認済） |
| `claude-sonnet-5` | 対応 | 未列挙（[[IADR-0106]] で明記） |
| `claude-sonnet-4-6` | 対応 | 未列挙 |
| `claude-haiku-4-5` | 対応 | 未列挙 |

### #309 の原因分析の誤り（本 IADR で訂正する）

#309 は「機密区分 `internal` では非 ZDR モデルへ送れない」としているが、実装はそうなっていない。

- `EgressMatrix.RequiresZeroDataRetention` は `Public`/`Internal` で `false`。ZDR 除外が効くのは
  `confidential` / `restricted` / 未知区分のみである。
- ZDR 除外が効く区分でも**送信は拒否されず** `DefaultModel` へフォールバックする
  （改定前の `PostComplete_ConfidentialReportMonthly_FallsBackToZdrModel` が `Sent=true` を固定していた）。

`/complete` が `Sent=false` を返す分岐は ①越境拒否 ②プロバイダ未登録 ③**プロバイダ呼び出しの例外**
の 3 つで、AST の `HttpReportNarrativeDrafter` はそのすべてに対して
`Sent=false・機密区分による縮退` という固定文言を出す。live の WRN はこの文言であり、分岐を示していない。
同時刻に週報（`claude-opus-5`）が 30 秒タイムアウトへ到達していた＝経路と資格情報は有効であることから、
**live の実原因は分岐③である可能性が高い**（稼働環境に触れないため確定はしない）。

原因の帰属が①でも③でも、月報から `claude-fable-5` を外す本改定は有効である。

## 検討した選択肢

1. **月報を ZDR 対応の最上位モデル（`claude-opus-5`）へ改定する（案 A・採用）** — 設定の矛盾が消え、
   機密区分によらず割当が一定になる。`claude-fable-5` の長文脈性を月報で失う。
2. **report-service の機密区分を下げる（案 B）** — 報告書には取引実績・建玉・損益が載る。非 ZDR 送信の
   可否は情報分類の判断であり、実装側が独断で下げてよい範囲を超える。
3. **`NonZdrModels` から `claude-fable-5` を外す（案 C）** — ZDR 提供の有無は契約事実であり、
   確認せずに外すのはデータ越境ポリシー（`08_data-egress-policy`）違反になる。
4. **月報だけ `claude-sonnet-5`（次点の ZDR モデル）にする** — 週報との同値を避けられるが、
   方針階層の最上位に日報と同じモデルを割り当てることになり、階層の意図と逆行する。

## 決定

### 決定 1: `report-monthly` を `claude-opus-5` へ改定する

`Llm:Routing:PurposeModels.report-monthly` を `claude-fable-5` → **`claude-opus-5`** とする。
ZDR 対応 5 モデルのうち最上位は `claude-opus-5` であり（[[IADR-0101]] がグローバル既定＝最上位として
採用した版数）、方針階層の最上位である月報にはこれを充てる。

**週報（`claude-opus-5`）と同値になる**。方針階層は「上位ほど難度が高い」ため本来は月報 > 週報の
モデルを充てたいが、非 ZDR の `claude-fable-5` を除いた集合には opus-5 より上位が存在しない。
**制約下での最善**として同値を受け入れる。より上位の ZDR 対応モデルが提供された時点、または
`claude-fable-5` の ZDR 提供が契約で確認できた時点で再検討する（§フォローアップ）。

種別ごとに用途を分ける仕組み（[[IADR-0112]] 決定1）は維持する。**同値でも `report-monthly` の明示エントリは
残す**——一致は `default` 追随ではなく明示指定の結果であり、`default` の改定で無音に失効させないため。

### 決定 2: `Models` / `NonZdrModels` は変更しない

`claude-opus-5` は既に `Models` に登録済みであり、追加は不要（未登録だと `ResolveModel` が
`eligible.Contains(purposeModel)` を満たさず**例外もログも出さずに** `DefaultModel` へ落ちる。
#376 / [[IADR-0102]] で実際に踏んだ罠）。既存の集合ガード T-19 が全 `PurposeModels` 値 ⊆ `Models` を維持する。

`claude-fable-5` は `Models` に**残す**。`analysis` が ZDR 非要件区分で使う意図的な割当であり
（[[IADR-0022]]）、削除は明示 `Model` 要求をしている呼び出し側に対する破壊的変更になる。
`NonZdrModels` の `claude-fable-5` は契約事実であり不変（案 C の否定）。

### 決定 3: report-service の機密区分は下げない

`LlmGateway:Confidentiality` の既定 `internal` は不変とする（案 B の否定）。報告書に載る情報の
分類を実装側の都合で下げない。

### 決定 4: 「報告書用途に非 ZDR モデルを割り当てない」を設定ガードで固定する

用途が増えるたびに同じ矛盾が再発しうるため、`report-*` の全用途について割当モデルが
`NonZdrModels` に含まれないことを集合として固定する（`ReportPurposeModels_AreNotListedAsNonZdr`）。
T-19（全 `PurposeModels` 値 ⊆ `Models`）と同じ発想の設定ガードである。
`analysis` は ZDR 非要件区分に限って fable-5 を使う意図的な例外（[[IADR-0022]]）のため対象に含めない。

## 理由

- 案 A は 3 案のうち唯一、**実装側の判断だけで完結し、かつ情報分類にもポリシーにも触れない**。
  B は情報分類の決定、C は契約事実の確認を要する。
- 月報の割当を ZDR 対応モデルにすることで、[[IADR-0112]] 決定2 が既知事実として受け入れていた
  「機密区分を上げると月報だけ無音で割当が変わる」構造そのものが消える。運用の既知事項を
  1 つ減らせるのは、注記で運用に注意を促すより強い。
- 週報との同値は**制約の帰結であって設計の後退ではない**。fable-5 が internal 機密区分で使えない
  以上、選べる最上位は opus-5 である。同値であることを IADR とテストで明示すれば、
  「なぜ月報と週報が同じなのか」を後から読んだ人が誤って別モデルへ振り直すことを防げる。
- 決定4 のガードは、今回の矛盾が「1 行の設定ミス」ではなく「2 箇所の設定の突き合わせを人間が
  忘れうる構造」から生じたことへの対処である。[[IADR-0102]] / [[IADR-0106]] が `Models` について
  同じ形のガード（T-19）を置いた前例に倣う。

## 結果

- 良い影響: 月報の割当が機密区分に左右されなくなる（`internal` / `confidential` / `restricted` の
  いずれでも `claude-opus-5`・`Sent=true`）。設定ファイル内の矛盾が解消する。
  報告書用途への非 ZDR モデル割当は設定ガードで再発しない。
- 悪い影響 / トレードオフ: 月報と週報が同一モデルになり、方針階層の「上位ほど難度が高い」が
  モデル選択としては表現できなくなる（プロンプトと文脈量では引き続き差がある）。
  `claude-fable-5` の長文脈性を月報で使えない。
- **本 PR では live の症状が消えることを確認できない**（稼働環境不触）。改定後も月報がプレースホルダの
  ままなら `Sent=false` の分岐②③を疑い、応答の `RoutingReason` / `Endpoint` を確認する必要がある。
- フォローアップ:
  - 計画 ADR の改定（AST/ADR-0014 §決定1 の月報行）。Accepted 済み ADR は本文凍結のため、
    ADR-0011 → ADR-0014 と同じ手順（新 ADR ＋ 旧 ADR への改訂節追記）を踏む。**別 PR**。
  - AST `HttpReportNarrativeDrafter` のログ文言是正（`Sent=false` の 3 分岐を区別せず
    「機密区分による縮退」と断定するため、今回の誤診の直接原因になった）。
    **[ai-stock-trading#315](https://github.com/endazon/ai-stock-trading/issues/315) で追跡**。
  - `claude-fable-5` の ZDR 提供有無を契約で確認する。ZDR 提供があると確認できれば
    `NonZdrModels` からの除外（案 C）と月報の再割当を検討できる。
    **[#428](https://github.com/endazon/microservices-platform/issues/428) で追跡**（確認できないうちに
    `NonZdrModels` から外さない＝案 C を棄却した理由そのもの）。
  - より上位の ZDR 対応モデルが提供された場合の月報の再割当（同じく #428 で扱う）。
  - 費用実測（[#380](https://github.com/endazon/microservices-platform/issues/380) /
    [ai-stock-trading#243](https://github.com/endazon/ai-stock-trading/issues/243)）。月報が fable-5 から
    opus-5 へ変わるため、[[IADR-0112]] 時点の試算は再ベースラインが要る。

## 関連

- [[IADR-0112]]（報告書の種別別 purpose 割当。本 IADR が月報行を改定する）
- [[IADR-0022]]（既定 opus 経路・ZDR 除外の導入・`NonZdrModels`）
- [[IADR-0101]]（グローバル既定 Opus 5）
- [[IADR-0102]] / [[IADR-0106]]（`Models` 未登録による無音失効の罠と集合ガード T-19）
- テスト: T-22（改定）・T-23（追加）— `docs/tests/FR-11_llm-egress-routing.md`
