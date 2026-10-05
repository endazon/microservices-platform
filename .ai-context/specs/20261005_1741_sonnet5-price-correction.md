---
title: claude-sonnet-5 の単価表を $2/$10 の 1 区間へ戻し、中止された値上げの計上をやめる（#1741）
type: spec
status: done
related_ids: [NFR-19, ADR-0006, ADR-0044, IADR-0265]
author: claude
created: 2026-10-05
updated: 2026-10-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0044 §コンテキストと課題［2026-09-09 訂正］（$3/$15 への引き上げは行われない）
  - planning:projects/ai-stock-trading/07_adr/ADR-0037 実測 1（一次情報の確認）
issue: "#1741"
---

# 仕様書: claude-sonnet-5 の単価訂正への追随（#1741）

> 本仕様書は実装着手前に作成する。起点は #1741。判断の記録は既存の単価表の実装 ADR（IADR-0265）への日付つき追記に置く
> （仕組みは変えず値と区間の数だけを直すため、新しい IADR は起こさない）。

## 起点となる計画書（トレーサビリティ）

- 要求: **NFR-19**（可観測性）。ADR-0044 は ADR-0006（NFR-19 の可観測性スタック）§結果 のフォローアップ
  「外部 LLM API コストも可視化する」を部分改定したものであり、費用の計測はその下にある。費用そのものの NFR は計画に無い。
- 計画 ADR: **ADR-0044**（単価表の扱い）。2026-09-09 の訂正で「提供元は $2/$10 を標準価格とし、
  2026-09-01 に予定されていた $3/$15 への引き上げは行わない」と記録された。
- 関連 IADR: IADR-0265（有効期間つき単価表・半開区間・重なりの起動時検証）。
- 先行例: AST は同じ公表を 2026-08-28 に確認し（AST 作業仕様書 `20260828_243_sonnet5-pricing-update`）、
  数値を据え置いて期限つきの警告だけを畳んだ。AST の単価表は期間を分けていなかったので値の変更は不要だった。
  本リポは**期間を分けて $3/$15 を実効値として入れていた**ため、値の変更が要る。

## 母集合（規則 9: 誤りの側の文字列で走査した）

`origin/develop` `59dead84` で次を引いた（`.ai-context/specs/`・`.ai-context/superpowers/`・`CHANGELOG.md`・submodule は除外）。

```
git grep -n -E 'sonnet-5|OutputPerMillionTokens|EffectiveFrom' -- . ':!*.md'
git grep -n -E '\$3 ?/ ?\$15|\$3/\$15|3\.0 USD|15\.0 USD|9 月以降|2026-09-01 以降|2026-08-31 まで' -- . ':!.ai-context/specs' ':!.ai-context/superpowers' ':!CHANGELOG.md'
git grep -n -E 'Pricing__|PerMillion|Pricing:' -- deploy src ':!*.cs'
git grep -n -E '0\.10944|0\.29184|0\.07344|21\.888|14\.688|0\.06144|10\.944|7\.344' -- .
```

| # | 箇所 | 内容 | 扱い |
| --- | --- | --- | --- |
| 1 | `src/platform/backend/Services/LlmGateway/appsettings.json` | **実効値**。2026-09-01 以降 $3/$15 | **是正**（1 区間 $2/$10） |
| 2 | `LlmGateway/Domain/Pricing/ModelPricingOptions.cs` 冒頭コメント | 「9 月以降 $3/$15」 | **是正**（中止の事実と「中止を忘れると過大」の形を書く） |
| 3 | `LlmGateway/Domain/Routing/LlmRoutingOptions.cs` コメント | 鎖の単価例「sonnet-5 $3/$15」 | **是正**（$2/$10。鎖が安価側へ向かう主張は変わらない） |
| 4 | `LlmGateway/Tests/Domain/Pricing/ModelPriceTableTests.cs` | 境界テストの合成表を「単価改定の実例」と称する | **説明のみ是正**。合成表（$2/$10 → $3/$15）は**半開区間の仕組みの検査**として残す |
| 5 | `LlmGateway/Tests/Domain/Pricing/ModelPricingOptionsValidatorTests.cs` T-14 | 「実際に配備する appsettings の単価表」の写しが 2 区間 | **是正**（写しを 1 区間へ） |
| 6 | `LlmGateway/Tests/Common/Observability/LlmUsageMetricsTests.cs` | 計器テストの合成単価 3.0/15.0 | **対象外**。計器の配線検査の任意値で、実価格を主張していない |
| 7 | `docs/operations/llm-output-token-measurement-runbook.md` | 単価の写し・sonnet-5 の最悪額・フォールバック合算 | **是正**（再計算） |
| 8 | `.ai-context/adr/IADR-0106_rag-answer-sonnet-5.md` §比較表 | 「標準単価 $3/$15（2026-08-31 まで導入価格 $2/$10）」 | **日付つき追記**（凍結記録。本文は書き換えない） |
| 9 | `.ai-context/adr/README.md` IADR-0106 行 | 「標準単価は Sonnet 4.6 と同一（2026-08-31 まで導入価格）」 | **対象外**。索引は本文の要約であり、訂正は IADR-0106 本文の追記が持つ |
| 10 | `deploy/helm/**`・`deploy/**` | 単価の設定は無い（Grafana は単価を持たない設計） | 該当なし |
| 11 | `.ai-context/specs/` の過去分 | point-in-time の記録 | 対象外（凍結） |

## 設計

### 決定: 期間の区切りを外し、1 区間（期限なし）の $2/$10 とする

- 価格は一度も変わっていない。区切りを残して両区間を $2/$10 にすると、**起きなかった改定の日付**が設定に残り、
  次に読む人が「9/1 に何かあった」と誤読する。区切りを外すのが事実に一致する。
- **有効期間つき単価表の仕組み（`EffectiveFrom`/`EffectiveTo`・半開区間・重なりの起動時検証・期間外の警報）は変えない。**
  境界の挙動は `ModelPriceTableTests` の合成表が引き続き固定する。
- 過去の計上への影響: 区切りを外すと 2026-09-01 より前も $2/$10 で換算される（従前と同値）。区間外（`OutOfEffectivePeriod`）
  になる時刻も生じない（`claude-opus-5` / `claude-haiku-4-5` と同じ期限なしの形）。
- **既に発行済みの `llm_cost_total` は遡って直らない**（カウンタは発行時点で確定する）。2026-09-01 から配備までの
  `claude-sonnet-5` の金額は 1.5 倍に過大である。月次確認ではこの期間を補正して読む（Runbook には書かない。事実は本書と issue に残す）。

> **［2026-10-05 追記 / #1741］監査指摘への対応: 上の「Runbook には書かない」は撤回する。**
> 月次確認は前月比 1.5 倍を目安に判定するため、過大計上がそのまま誤判定（8→9 月の見かけの増加・配備翌月の約 0.67 倍の見かけの減少）を起こす。
> `docs/operations/llm-cost-monthly-review-runbook.md` §過大計上の期間 に、期間・補正（1.5 で割る）・影響する用途を書いた。
> あわせて `docs/observability/llm-usage-and-cost-metrics.md` §単価表 の「警報が漏れを検知する」が**過小側にしか成り立たない**
> （中止された改定の区間を消し忘れても単価は解決でき、警報は 0 のまま過大になる）ことを日付つき訂正で明記した。
> 監査の🟢 3 件（T-14 の説明を検証器の陽性対照へ改める・`LlmUsageMetricsTests` の合成モデル名を中立化・索引の IADR-0106 行から誤った括弧書きを削る）も同じコミットで直した。
> 期間外の警告ログと `out_of_period` 属性の写像が未検査である既存の穴は別 issue（#1743）に切り出した。

> **［2026-10-05 追記 / #1741］AI 再レビューへの対応。** 上表の走査語は「標準単価が Sonnet 4.6 と同一」という言い回しを捕まえておらず
> （規則 10 の型）、IADR-0106 §理由 の同じ誤った前提が残っていた。言い回しで引き直し（`4\.6 と(同|並)|同(一|額|じ)(の)?(標準)?単価|導入価格`）、
> 該当は IADR-0106 §理由 の 1 箇所だけであった（他は是正済み・凍結した過去の仕様書・過小側について正しい記述）。同所へ日付つき追記を足した。
> あわせて月次確認 Runbook の trace ブロック `iadrs:` に IADR-0265 を足した。

### 追加するテスト

`LlmGateway.Tests` に、**実配備の `appsettings.json` をホスト経由で束縛し**、`ModelPriceTable` で
`claude-sonnet-5` を 2026-09-01 ちょうど・2026-10-05・2026-08-31 に見積もって **$2/$10** になることを固定するテストを足す。
合成 config ではなく実設定を通すことに意味がある（設定の値そのものが本件の欠陥だった）。

## 受け入れ基準

- [x] `appsettings.json` の `claude-sonnet-5` が期限なしの 1 区間 $2/$10 である。
- [x] 実設定を通したテストが、2026-09-01 以降の `claude-sonnet-5` を $2/$10 で換算することを固定する（値を 3/15 に戻すと赤くなる）。
- [x] 単価表の仕組み（区間・検証）のテストは無変更で緑。
- [x] 母集合の是正対象（上表の「是正」「追記」）がすべて直っている。
- [x] ビルド警告ゼロ・`dotnet format --verify-no-changes`・文書系検査が通る。

## テスト方針

- 新規: 実設定の束縛テスト（上記）。変異（3.0/15.0 へ戻す）で赤くなることを確認する。
- 既存: `ModelPriceTableTests`（境界）・`ModelPricingOptionsValidatorTests`（T-14 の写しを更新）を回す。

## 計画書との差異

- 差異: なし（本件は計画 ADR-0044 の訂正への実装側の追随である）。

## 未決事項

- なし。
