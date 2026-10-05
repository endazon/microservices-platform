---
title: 単価の期間外（out_of_period）の警告ログと計器属性の写像をテストで固定する（#1743）
type: spec
status: done
related_ids: [NFR-19, FR-10, ADR-0006, ADR-0044, IADR-0265]
author: claude
created: 2026-10-05
updated: 2026-10-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0044 決定 3（期間外・未登録は警告し、無音で 0 円として扱わない）
issue: "#1743"
---

# 仕様書: 単価の期間外の警告ログと `out_of_period` 属性の写像の検査（#1743）

> 本仕様書は実装着手前に作成する。起点は #1743（PR #1742 の独立監査で見つかった検査の穴）。
> **テストの追加のみ**であり、製品コードの挙動は変えない。判断を伴わないため IADR は起こさない。

## 起点となる計画書（トレーサビリティ）

- 要求: **NFR-19**（可観測性）。費用の計測は ADR-0006（NFR-19 の可観測性スタック）のフォローアップを改定した ADR-0044 の下にある（#1741 の作業仕様書と同じ整理）。
- 計画 ADR: **ADR-0044 決定 3** —— 「期間外の単価で試算した場合は警告を出す。どの単価も有効期間に該当しないモデルが現れた場合も同様に警告する。無音で 0 円として扱ってはならない」。
- 関連 IADR: IADR-0265（有効期間つき単価表）。
- テスト仕様書: `docs/tests/FR-10_dashboard.md` §LLM 利用実績（単価表・金額換算）。

## 現状の穴（issue の変異）

| 変異 | 箇所 | 着手前 |
| --- | --- | --- |
| 期間外の `LogWarning` を消す | `Services/LlmGateway/Domain/Pricing/ModelPriceTable.cs` の期間外の枝 | 生存 |
| `llm.pricing_status` の写像を `no_entry` に固定する | `Services/LlmGateway/Common/Observability/LlmUsageMetrics.cs` の未計上の枝 | 生存 |

既存の T-5（期間外）は戻り値の状態だけを、T-17（未計上）は未登録（`no_entry`）だけを通していた。

## 受け入れ基準

1. 期間外の時刻で `Estimate` を呼ぶと、状態が `OutOfEffectivePeriod` であり、**Warning のログがちょうど 1 件**出て、本文にモデル名を含む（テスト仕様書 T-41）。
2. 未登録のモデルでも同様に Warning が 1 件出て、本文にモデル名を含む（T-41 の対照）。
3. 期間の区切りのある合成の単価表で区間外の時刻に `RecordUsage` を呼ぶと、`llm.pricing.unpriced.total` が 1 計上され、`llm.pricing_status` が **`out_of_period`**（`no_entry` ではない）で、金額は計上されない（T-42）。
4. 上の 2 変異を当て直すと、いずれも新テストが赤になる（当て直し後は元へ戻す）。

## 設計

- ロガーはテストクラス内に最小の記録用ロガー（`ILogger<T>` を実装し `(LogLevel, 本文)` を貯める）を置く。
  同型のものが `OpenAiProviderStopReasonTests` に private で既にあり、新しいパッケージ（`Microsoft.Extensions.Diagnostics.Testing`）は入れない。
- 計器は既存の `Probe`（Meter インスタンスで絞る MeterListener。IADR-0394）をそのまま使い、`Metrics` の組み立てに合成の単価表を渡せる引数を足す。
- T-ID はテスト仕様書の節の続き番号（T-41・T-42）を採る。

## 母集合（規則 9）

`git grep -n -E 'OutOfEffectivePeriod|PricingOutOfPeriod|out_of_period'` を `src/` `docs/` で引いた。
製品コードの参照は `ModelPriceTable.cs`・`LlmUsageMetrics.cs` の 2 箇所、テストは `ModelPriceTableTests` T-5 の 1 箇所、
文書は `docs/observability/llm-usage-and-cost-metrics.md`（属性値の説明。挙動を変えないため追随不要）。

## 対象外

- 製品コードの変更（本 issue はテストの追加のみ）。
- 既存テストのコメント内の T 番号（T-1〜T-8・T-15〜T-19）とテスト仕様書の節内番号（T-30〜T-40）のずれ。本件では新テストを仕様書側の番号に合わせるだけに留める。
