---
title: IADR-0511 グラフの 2 用途（graph-suggestion・graph-cluster-summary）のモデルと鎖を割り当て、呼び出し側の用途名との突合を試験で固定する
type: impl-adr
status: Accepted
related_ids:
  - FR-10
  - FR-11
  - FR-17
  - FR-18
  - ADR-0022
  - ADR-0035
  - ADR-0038
  - ADR-0044
  - ADR-0081
  - IADR-0022
  - IADR-0102
  - IADR-0110
  - IADR-0112
  - IADR-0266
  - IADR-0340
  - IADR-0430
  - IADR-0498
author: claude
created: 2026-10-08
updated: 2026-10-08
---

# IADR-0511: グラフの 2 用途のモデルと鎖を割り当て、呼び出し側の用途名との突合を試験で固定する

- 状態: Accepted
- 日付: 2026-10-08
- 決定者: 実装エージェント（worker）／#1785

## 起点・関連

- 関連する計画書: `ADR-0081` 決定 3・フォローアップ 2（提案生成の費用を前月比と累計で見る。用途別の計測で切り分けられるか確認して環流する）、
  `ADR-0044` 決定 1（用途別・モデル別の計測）、`ADR-0038` 決定 3・5（鎖は 1 段下位。鎖の要素は利用許可集合に残す）、
  `ADR-0022`（定型・高頻度は `claude-sonnet-5`。割当は実装の構成 `PurposeModels` の領分）、
  `ADR-0035` 決定 3（コミュニティ要約の生成モデルは `claude-opus-5`）、`06_technical/04_ai-rag-stack.md`（用途別モデル表・フォールバック順序表）
- 関連する実装ADR（本リポジトリ）: IADR-0022（用途→モデルの構成化）・IADR-0102 / IADR-0106（未登録の用途が無音で既定へ落ちる罠）・
  IADR-0110（計器の用途の値域を `PurposeModels` で閉じる）・IADR-0112（既定と同値でもエントリを省略しない）・
  IADR-0266（提案生成は要求時・利用者スコープ）・IADR-0340（用途登録の前例）・IADR-0430（クラスタ要約）・
  [IADR-0498](IADR-0498_claude-rerank-stage-at-search-exit.md)（`rerank` の用途登録と費用の軸。同型の前例）
- 関連する実装仕様書: [20261008 作業仕様書](../specs/20261008_1785_graph-purpose-models.md)
- 起点 issue: #1785（第 4 回全体監査 B-14 の切り出し。台帳 #1772）

## コンテキストと課題

グラフサービスは LLM ゲートウェイへ 2 つの用途で補完を要求する。

| 用途 | 呼び出し側 | 仕事 | 発生源 |
| --- | --- | --- | --- |
| `graph-suggestion` | `LlmGatewaySuggestionClient`（REST）・`LlmGatewayGrpcSuggestionClient`（gRPC） | 起点文書と候補文書の**表題**・辺の型・タグの**閉じた一覧から選び**、JSON 配列で返す（AI 提案。FR-18） | 利用者の要求（IADR-0266。生成器を呼ぶのは `Features/AiSuggestions/Generate/Endpoint.cs` だけ） |
| `graph-cluster-summary` | `LlmGatewayClusterSummaryClient` | クラスタの文書群（表題のみ）の要約本文を書く（FR-17。IADR-0430） | 日次バッチ（既定で無効） |

どちらも `Llm:Routing:PurposeModels` に無かった。

1. `LlmRouter.ResolveModel` は用途が無いとエンドポイントの `DefaultModel`（`claude-opus-5`）へ**例外もログも無く**落ちる。
2. 鎖（`PurposeFallbackModels`）も無く、第 1 候補が 400 系で失敗するとそのまま失敗する。
3. 計器（`LlmMetricValues.NormalizePurpose`）は `PurposeModels` に無い用途を `other` へ丸める。
   **提案生成・クラスタ要約の費用を他の未登録の用途と切り分けられない** —— ADR-0081 フォローアップ 2 の答えが「できない」になっていた。

計画は**用途に割り当てるモデルを定めていない**（`graph-suggestion`）か、**生成モデルだけを名指ししている**（`graph-cluster-summary` は ADR-0035 決定 3）。
割当は実装の構成 `PurposeModels` の領分である（ADR-0022・IADR-0022）。

### 構成の層（全環境に届くか）

`PurposeModels` / `PurposeFallbackModels` の宣言は `src/platform/backend/Services/LlmGateway/appsettings.json` の 1 か所だけである。
`appsettings.Development.json`・`deploy/docker-compose.yml`・本番 chart（`deploy/helm/microservices-platform/values.yaml`）・
経路 B（`deploy/local/values-local.yaml`）・`.env.example` のいずれにも `Llm__Routing__*` の上書きは無い（実測。作業仕様書 §母集合 軸 2）。
**全環境がイメージに焼かれた同じ appsettings.json を読む**ので、足す層は 1 つで足りる。

## 検討した選択肢

### `graph-suggestion`

| # | 案 | 採否 |
| --- | --- | --- |
| A1 | 既定のまま（`claude-opus-5`）を明示エントリで固定する | 不採用。費用の軸は直るが、**選別の仕事に最も高い単価を払い続ける**。ADR-0081 決定 3 のとおり費用は利用者の操作回数に比例し、事前に上限を固定できない |
| A2 | **`claude-sonnet-5`、鎖 `claude-haiku-4-5`** | **採用**（決定 1） |
| A3 | `claude-haiku-4-5`（`rerank`・`trade-decision-screening` と同じ層）、鎖なし | 不採用。`rerank` は検索のたびに呼ぶので最安が要る。提案は**承認されれば文書のタグ・リンクとして残る**（ADR-0063）ため、誤提案の損が大きい。haiku は鎖を持てない（さらに安い先が無い）ので、400 系で提案が 0 件になる |

### `graph-cluster-summary`

| # | 案 | 採否 |
| --- | --- | --- |
| B1 | **`claude-opus-5`、鎖 `claude-sonnet-5`** | **採用**（決定 2） |
| B2 | `claude-sonnet-5` へ下げる | 不採用。**ADR-0035 決定 3 が生成モデルを `claude-opus-5` と名指ししている**。実装が下げる根拠を持たない（下げるなら計画へ環流する） |

## 決定

### 決定 1: `graph-suggestion` を `claude-sonnet-5` へ割り当て、鎖を `claude-haiku-4-5` とする

- **仕事の形が `diagram-coding`（分類・短い抽出）・`rag-answer`（定型・高頻度）と同じ層である。** 入力は表題と閉じた値集合だけで、
  出力は一覧から選んだ JSON 配列である（`SuggestionPrompt.Render`）。深い比較・分析（`analysis` の層）ではない。
- **単価**: `claude-opus-5` $5 / $25 → `claude-sonnet-5` $2 / $10 per 1M tokens（入力・出力とも 2.5 分の 1。単価表 `Llm:Pricing:Models`）。
  発生源は利用者の操作であり（ADR-0081 決定 3）、単価がそのまま費用に効く。
- **鎖**: ADR-0038 決定 3 の「1 段下位・安価側」を `rag-answer` と同じ形で当てる（`claude-sonnet-5` → `claude-haiku-4-5`）。
- **品質の確認は残る**: 提案は人の承認を経てから確定する（ADR-0081 決定 2・ADR-0063）。承認率・却下率の実測で割当を見直す（§残るもの）。

### 決定 2: `graph-cluster-summary` を `claude-opus-5` へ割り当て、鎖を `claude-sonnet-5` とする

- ADR-0035 決定 3 の名指しに従う。**現状（既定へ落ちて `claude-opus-5`）と同じモデルだが、エントリは省略しない** ——
  既定の改定（IADR-0101 が実際に行った操作）で無音に失効するためである（IADR-0112 決定 1）。
- 鎖は `analysis`・`default` と同じ 1 段下位（`claude-opus-5` → `claude-sonnet-5`）。日次バッチの 1 回の失敗で要約が付かなくなるより、
  1 段下位で要約が付くほうが利益が大きい（取引判断と違い、別モデルで書いた要約に再現性の制約は無い）。

### 決定 3: ZDR は区分の規則に任せ、`ZeroDataRetentionPurposes` へは足さない

- 2 用途とも、送る文書の**最高区分を名乗る**（提案は封が持つ最高区分。要約は封の区分で、それを超える文書は封に入らない）。
  区分の規則（`EgressMatrix`）で `confidential` / `restricted` は ZDR 必須になる。
- `rerank`（[IADR-0498](IADR-0498_claude-rerank-stage-at-search-exit.md) 決定 5）は計画 ADR-0127 決定 3 が「ZDR に対応するモデルに限る」と名指ししたので
  区分によらず要件にした。グラフの 2 用途に同じ名指しは無い。
- 割り当てた 3 モデル（`claude-opus-5`・`claude-sonnet-5`・`claude-haiku-4-5`）はいずれも `claude-managed` の `Models` に在り、
  `NonZdrModels`（空）に無い。全 `PurposeModels` の割当が非 ZDR でないことは T-23 が、`Models` 登録は T-19 が固定する。**新しいモデル ID は使わない。**

### 決定 4: 呼び出し側の用途名の全数と `PurposeModels` のキーを試験で突き合わせる

- `scripts/lib/llm-purposes.js` が、本リポが所有する backend（`src/platform/backend`・`src/knowledge/backend`）の非試験 `*.cs` から
  呼び出し側の用途名を拾い、ゲートウェイの appsettings.json の `PurposeModels` のキー（大小を区別しない・`default` は既知）と突き合わせる。
  `scripts/scripts.repo.test.js`（#1785 の節。`scripts-tests` ジョブ）が実ツリーと合成の本文の両方を叩く。
- **同型の事故は 3 回目である**（`trade-decision` IADR-0102・`trade-decision-screening` IADR-0340・本件）。列挙を持たず、宣言から母集合を引く。
  **0 件走査は赤**にする（`check-secret-injected-options.js` と同じ姿勢）。
- **拾う形**: ① 名前に `Purpose` を含む `const string`（名前が `Tag` で終わる属性名は除く） ② `Purpose:` / `Purpose =` への文字列リテラル。
- 🔴 **拾わない形と、その塞ぎ方**: 文字列リテラルを**位置引数で中継**する形（`GenerateAsync(…, "analysis", ct)`）。
  引数の分割に入れ子のラムダ・括弧が絡むので正規表現では脆い。**宣言の形をそろえる側で塞ぐ** ——
  `RagOrchestrator` の `"rag-answer"`・`"analysis"` を定数（`RagAnswerPurpose`・`AnalysisPurpose`）へ改めた（値は同じ）。
  **新しい呼び出し側は用途名を ① の定数で宣言する**（`LlmRoutingOptions.PurposeModels` のコメントに書いた）。
- **新しい `check-*.js` にしない。** `scripts-tests` は全 PR で走るので、ここに載れば足りる。検査器の本数ラチェット
  （`scripts.repo.test.js`）を動かす理由が無い。
- **submodule（`src/ai-stock-trading`）は対象外**。用途は AST の `LlmPurposes` が持ち、登録済み（IADR-0112・IADR-0340）である。
  submodule を取得しない PR の CI でも同じ結果になるよう、本リポの所有物だけを見る。

## 結果

- 提案生成・クラスタ要約の費用が `llm.tokens.total` / `llm.cost.total` の `llm.purpose=graph-suggestion` / `graph-cluster-summary` に、
  他の用途と分けて積まれる。**ADR-0081 フォローアップ 2 の答えが「できる」になる**（計画への環流は人が起票する）。
- 提案生成の単価が 2.5 分の 1 になる。クラスタ要約のモデルは変わらない（既定と同値の明示エントリになる）。
- 2 用途とも 400 系の失敗で 1 段下位へ落ちるようになる（従来は鎖が無くそのまま失敗）。
- 用途を足して `PurposeModels` へ書き忘れると `scripts-tests` が赤になる。

### 試験（変異で確かめた）

- `GraphPurposeEndpointTests`（T-30）: 2 用途 × 4 区分の解決・既定を差し替えても割当が選ばれる・鎖・計器の正規化・費用の軸。
  `PurposeModels` から 2 用途を外すと 10 件が赤（クラスタ要約の 4 区分の解決は既定と同値なので緑のまま —— だから既定差し替えの試験を置いた）。
- `scripts.repo.test.js` #1785: `PurposeModels` から `graph-suggestion` を外す／未登録の用途の定数を足す／`Purpose:` に未登録のリテラルを書く
  の 3 変異でいずれも赤。`RagOrchestrator` をリテラルへ戻すと陽性対照（6 用途を拾う）が赤。

### 残るもの

1. **`graph-suggestion` の品質差を実測していない。** `claude-opus-5` 比で提案の精度が下がり得る。稼働後に承認率・却下率
   （`AiSuggestion` の状態）で比べ、悪ければ割当を戻す（設定 1 行）。
2. **位置引数でリテラルを中継する新しい呼び出し側は拾えない**（決定 4）。宣言の規約とレビューで守る。
3. **AST の用途は本試験の対象外**。AST 側で用途を足したら、AST の作業で基盤の登録を求める（IADR-0340 の経路）。
4. ADR-0081 フォローアップ 2 の環流（「切り分けられるようになった」の記録）は人が起票する。
