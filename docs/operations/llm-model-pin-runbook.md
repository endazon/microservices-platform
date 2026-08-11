---
title: 運用 Runbook — ピン留め LLM モデルの版数移行と利用不能時の振る舞い
type: runbook
status: fixed
related_ids:
  - FR-11
  - IADR-0102
  - IADR-0112
author: claude
created: 2026-08-11
updated: 2026-08-11
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0011_llm-model-pinning.md (取引判断の LLM モデル固定・Accepted。本 Runbook が手続きを書き下ろす対象)"
---

# 運用 Runbook: ピン留め LLM モデルの版数移行と利用不能時の振る舞い

> **運用仕様書（[`operations.md`](operations.md)）の下位にあたる手順書である。**
> 起点: **#587**（#382 の後継）／ **[IADR-0112](../adr/IADR-0112_report-kind-purposes-and-trade-decision-sonnet-5.md) 決定 3**

## 対象

**用途別にピン留めしている LLM モデル**の版数を上げるとき、および**そのモデルが使えないとき**の手順。

### ★ ピンの値は**ここに書かない**

**単一情報源は次のファイルである。**

```
src/platform/backend/Services/LlmGateway/src/LlmGateway.Api/appsettings.json
  → Llm:Routing:PurposeModels        （用途 → モデル）
  → Llm:Routing:Endpoints[].Models   （エンドポイントが許可するモデル）
```

**本 Runbook へ値を書き写すと必ず古くなる**（[[IADR-0141]]「参照点を 1 つに畳む」）。
**現在の割り当ては次のコマンドで列挙する。**

```console
$ python3 -c "import json;d=json.load(open('src/platform/backend/Services/LlmGateway/src/LlmGateway.Api/appsettings.json'));\
[print(f'{k:18s} {v}') for k,v in d['Llm']['Routing']['PurposeModels'].items()]"
```

**用途は 1 つではない。** `trade-decision` のほか `rag-answer` / `analysis` / `diagram-coding` /
`report-monthly` / `report-weekly` / `report-daily` / `default` がそれぞれ独立にピンされている。
**監視も移行も用途ごとに要る**（後述 §提供終了の監視）。

---

## 版数移行の手順

**`trade-decision` の版数を上げるときは、次の 3 段を必ずこの順で踏む。**

### 1. AST 側で Stage 0 の 7 条件を再実行し、合格を確認する

**これは省略できない。** 計画 `AST/ADR-0011` の「**版数を上げる際は Stage 0 の再検証が必要**」は
改定後も**維持されている**（planning#50 決定 1「バージョン固定の原則は維持する」）。
**手順の必要性はピンの値が変わっても変わらない。**

> **★ 実行はこのリポジトリではできない。** Stage 0 は **AST リポジトリ**側の手続きである（AST#296）。
> **合格の確認を取らずに次段へ進まない。**

### 2. 設定を更新する

| 更新先 | 内容 |
| --- | --- |
| `Llm:Routing:PurposeModels.trade-decision` | 新版のモデル ID へ |
| `Llm:Routing:Endpoints[claude-managed].Models` | **新版が含まれていなければ追加する** |

**両方を見る。** 用途側だけ変えてエンドポイントの許可一覧に無いと、**ルーティングは解決に失敗する。**

### 3. 実装 ADR に版数変更と再検証結果を記録する

**設定だけ書き換えて手続きを踏まない運用は [[IADR-0102]] §結果が明示的に禁じている。**
新しい実装 ADR（`IADR-XXXX`）へ次を残す。

- 変更前 → 変更後の版数
- **Stage 0 再検証の結果**（実行日・合否・根拠へのリンク）
- 変更の理由

---

## ★ 利用不能時の振る舞い —— 実行せず、発注もしない

**ピン留めしたモデルが使えないとき、取引判断は実行しない。発注もしない。**

### これは**障害ではない**。設計上の正常な結果である

**別のモデルへ切り替えて取引判断を続けてはならない。**
`AST/ADR-0011` がピン留めを定めた目的は**再現性と監査可能性**であり、
**別モデルで下した判断は、その両方を失った別物である。**

> **★ この節は「禁止」だけでなく「なぜ落とさないのか」を書いている。**
> **書かないと、運用時に善意でフォールバックが追加される** —— #382 が明示した懸念であり、
> **禁止の記述だけでは破られる。** 判断を止めることは**不具合ではなく仕様**である。

### レート制限（429）は別物である

| 事象 | 扱い |
| --- | --- |
| **レート制限（429）** | **再試行する。** フォールバックではない |
| **モデルが提供終了・利用不能** | **実行しない。発注もしない**（上記） |

**この 2 つを混同しない。** 429 を「利用不能」と読んで別モデルへ逃がすと、
**上の禁止を実質的に破ることになる。**

### 実装状況（**2026-08-11 時点**）

**フォールバック機構は実装されていない。** `LlmGateway` に該当する実装は無い（実測）。

- **本 Runbook が定めるのは「実装するときの制約」である** —— **`trade-decision` はフォールバックの対象にしない。**
- **フォールバックを実装する issue は #440** である（`analysis` の改定と併せて持つ）。
- Anthropic の `fallbacks` は **HTTP 400 を捕捉しない**ため、**LlmGateway のクライアント実装として持つ**
  （質問票 第 4〜5 回の確定事項）。

---

## 提供終了の監視

**ピン留めしたモデルの提供終了情報を継続的に把握する。**

### 監視対象は用途ごとに分かれている

**単一のモデルを見ていれば足りるわけではない。**
§対象 のコマンドで**現在ピンされているモデルを列挙し、その全部を対象にする。**

### 手段

| 手段 | 内容 |
| --- | --- |
| **一次情報** | Anthropic のモデル提供終了（deprecation）告知 |
| **棚卸しの契機** | **月次の LLM 費用確認**（[`llm-cost-monthly-review-runbook.md`](llm-cost-monthly-review-runbook.md)）に合わせて、ピン一覧と告知を突き合わせる |
| **気づいた後** | §版数移行の手順（Stage 0 再検証を含む）へ入る |

> **★ 「監視の仕組みを新設する」ことはしない。** 既に月次で人が見る手順があり、
> **そこへ 1 項目足すほうが、誰も見ない新しい仕組みを作るより確実である**
> （`doc-links-planning.yml` が記録する「**誰も見ない赤**が常態化する」の教訓）。

### 限界（明示する）

- **自動検知ではない。** 提供終了の告知を機械が読む経路は無い。**月次の棚卸しが唯一の契機である。**
- **告知から提供終了までの猶予が月次周期より短い場合、間に合わない。**
  その場合は §利用不能時の振る舞い が効く —— **実行せず、発注もしない。**

## 関連

- [`operations.md`](operations.md)（上位の運用仕様書）
- [`llm-cost-monthly-review-runbook.md`](llm-cost-monthly-review-runbook.md)（月次棚卸しの契機）
- [IADR-0102](../adr/IADR-0102_trade-decision-model-pinning.md)（ピン留めの決定）
- [IADR-0112](../adr/IADR-0112_report-kind-purposes-and-trade-decision-sonnet-5.md) 決定 3（現行ピンと Stage 0 ゲート）
- 作業仕様書: [20260811_issue-587](../specs/20260811_issue-587_pin-migration-runbook.md)
