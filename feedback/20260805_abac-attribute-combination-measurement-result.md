---
title: ABAC 属性組み合わせ数の実測結果 — 実測値は 1 通り（ADR-0035 の「機密区分単位 4 通り」は安全側）・必須属性 3 種が実データに不在
type: plan-feedback
status: accepted
category: その他
related_ids: [FR-17, FR-18, FR-05, FR-09, ADR-0033, ADR-0034, ADR-0035, ADR-0036]
source_repo: microservices-platform
source_ref: docs/specs/20260805_issue-456_abac-attribute-combination-measurement.md（ブランチ chore/FR-17-abac-attribute-measurement・issue #456）
author: Claude
created: 2026-08-05
updated: 2026-08-08
---

# フィードバック: ABAC 属性組み合わせ数の実測結果（issue #456 の完了報告）

> **計画リポジトリへ起票済み: [planning#203](https://github.com/endazon/project-planning/issues/203)**（2026-08-06）。
> 実装側の測定手段は microservices-platform#515（マージ済み）で用意した。

> **本書は判断を求めるものではなく、planning#187 の裁定に対する実測の報告である。**
> 裁定（案 B）で ADR-0035 は起案済みであり、本実測はその決定を**覆さない**（むしろ安全側であることを裏づける）。
> ただし副次的に**計画と実装の乖離 1 件**が判明したため、あわせて報告する。

## 種別

その他（計画が求めた実測の結果報告 ＋ 実測により判明した実装側の乖離）。

## 起点となる計画書

- 機能要求（FR）: FR-17（知識グラフ）・FR-18（AI 提案）。属性体系は FR-05・FR-09
- ユースケース（UC）/ 画面（SC）: なし
- 関連 ADR: [ADR-0033](../planning/projects/microservices-platform/07_adr/ADR-0033_knowledge-graph-data-model-and-store.md)／
  [ADR-0034](../planning/projects/microservices-platform/07_adr/ADR-0034_graph-traversal-abac-enforcement.md)／
  [ADR-0035](../planning/projects/microservices-platform/07_adr/ADR-0035_graphrag-retrieval-strategy.md)（Proposed）／
  [ADR-0036](../planning/projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md)
- 計画書リンク:
  [`06_technical/14_knowledge-graph-graphrag.md`](../planning/projects/microservices-platform/06_technical/14_knowledge-graph-graphrag.md) §6（粒度と費用の決定手順）／
  [`06_technical/07_abac-attribute-model.md`](../planning/projects/microservices-platform/06_technical/07_abac-attribute-model.md)（属性体系）
- 実装側の対応: [作業仕様書](../docs/specs/20260805_issue-456_abac-attribute-combination-measurement.md)／
  測定スクリプト `scripts/measure-abac-combinations.js`

## 現状（計画書の記述 / As-Is）

- [`14_knowledge-graph-graphrag.md`](../planning/projects/microservices-platform/06_technical/14_knowledge-graph-graphrag.md) §6 は
  粒度の決定手順を「1. 実測する → 2. 試算する → 3. 判定する（属性組み合わせ単位 → ロール単位 → 機密区分単位）→ 4. モデルを見直す」と定める。
- ADR-0035 は planning#187 の裁定（**案 B: 実測なしで起案**）により、
  要約の粒度を「**機密区分単位（4 通り）から始める**」と決定し、**実測は稼働後の検証項目**とした。
- 同裁定は「旧データ破棄（実装側 #457）の前に、属性組み合わせ数を機械的に数えられるスクリプトを用意する」ことを宿題として残した。

## 実装で判明した経緯

実装側の稼働環境（経路B ローカル k8s）が利用できたため、**手段の用意（宿題）と実測の両方**を実施した。

- 測定スクリプト: `scripts/measure-abac-combinations.js`（読み取り専用・外部依存ゼロ・集計は単体試験つき）
- 測定日: 2026-08-05 ／ 対象: realm `microservices-platform` ＋ `document_svc` の実データ 2,368 件

## 実測結果

| 粒度（§6 手順 3 の段階） | 実測値 | 備考 |
| --- | --- | --- |
| 属性組み合わせ単位 | **1** | `confidentiality=internal` のみ（2,368 件すべて） |
| ロール単位 | **4** | 利用者 4 人の realm ロール保有集合 |
| 機密区分単位 | **1** | 設計上は 4 通り。`public` / `confidential` / `restricted` は実データに不在 |

参考値: 利用者属性（`clearance` × `department`）の実在組み合わせ **3**／
属性辞書（`AttributeDefinitions`）**0 件**／ABAC ポリシー（`Policies`）**0 件**／
現行ポリシーで到達可能な 利用者 × 文書 の組 **0**（deny-by-default）。

### 結論 1: ADR-0035 の決定は実測に対して安全側である

**実測（1 通り）は ADR-0035 の前提（4 通り）より粗い側にある。** したがって
「機密区分単位から始める」という決定は費用面で過大にならず、**改訂は不要**である。
§6 手順 2 の費用試算には **上限 4・実測 1** を入力すればよい。

### 結論 2: 本実測は本番相当ではない（限界の明示）

実データは取り込み経路（datasource / ingestion）由来のみで、人手投入の組織文書を含まない。
属性の多様性が 1 通りに留まるのは**データ源が単一であることの反映**であり、
本番でも 1 通りであることを意味しない。**稼働後の再測定は引き続き必要**である
（そのために測定スクリプトを残し、`--dump` / `--input` で後日の追試も可能にした）。

## 問題点 / あるべき姿（To-Be）

実測の副産物として、**計画の属性体系と実装の取り込み経路の乖離**が判明した。

[`07_abac-attribute-model.md`](../planning/projects/microservices-platform/06_technical/07_abac-attribute-model.md)
§文書の基本属性は `confidentiality` / `department` / `owner` / `lifecycle` を**必須**と定めるが、
実データ 2,368 件のうち付与されているのは **`confidentiality` のみ**である。
とくに `owner` は [ADR-0036](../planning/projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md)
の所有者ベース裁量制御（`doc.owner ∈ { ${current_user} }`）の基礎であり、
**現行の取り込み経路は ADR-0036 の前提を満たしていない**。

これは**実装側の欠落**であり、計画書の誤りではない。計画側の操作は不要と考えるが、
FR-17/18 の着手（#450）に先立って解消すべき前提であるため、計画側の認識合わせのために報告する。

## 提案（計画への反映案）

- 反映先候補: **その他（記述の追補のみ。決定の変更は不要）**
- 提案内容:
  1. [ADR-0035](../planning/projects/microservices-platform/07_adr/ADR-0035_graphrag-retrieval-strategy.md) §結果 の
     「実測は稼働後の検証項目」に、**2026-08-05 の実測値（属性組み合わせ 1 / ロール 4 / 機密区分 1）と
     「決定は実測に対し安全側」**である旨を 1〜2 行で追補する（決定そのものは変更しない）。
  2. [`14_knowledge-graph-graphrag.md`](../planning/projects/microservices-platform/06_technical/14_knowledge-graph-graphrag.md) §6 の
     手順 1 に、**測定手段が実装側に存在する**こと（`scripts/measure-abac-combinations.js`）と、
     本測定が**本番相当ではない**という限界を注記する。
  3. `07_abac-attribute-model.md` は**変更不要**。乖離は実装側で解消する（下記 §影響範囲）。

## 影響範囲

- **ADR-0035**: 決定は不変。§結果 への実測値の追補のみ。FR-17/18 の着手保留（実装側 IADR-0119）は
  planning#187 の裁定時点ですでに解除されており、本実測はそれを追認する。
- **実装側**: 必須属性 `department` / `owner` / `lifecycle` の付与を取り込み経路へ実装する必要がある
  （FR-17/18 の実装 #450 の前提。実装側 issue で追跡する）。
- **#457（旧データの破棄・切替）**: 測定手段と生データの保存（`--dump`）が用意できたため、
  「破棄すると測れなくなる」という制約は解消した。破棄の判断を実測待ちで止める必要はない。
