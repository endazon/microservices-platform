---
title: 作業仕様書 — knowledge 検索・RAG・AI 分析の受け入れ基準の実測突き合わせ（#448）
type: spec
status: done
related_ids:
  - FR-03
  - FR-04
  - FR-05
  - FR-07
  - FR-08
  - UC-01
  - UC-02
  - SC-01
  - SC-02
  - SC-08
  - ADR-0009
  - ADR-0038
  - ADR-0043
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/03_usecases/01_usecases.md
  - planning:projects/microservices-platform/04_workflows/02_rag-query-flow.md
  - planning:projects/microservices-platform/07_adr/ADR-0038_analysis-purpose-drop-fable-5.md
related_specs: []
---

# 作業仕様書: #448 の受け入れ基準を実測で突き合わせる

## 1. 起点と本作業の性質

#448 は「再実装」を表題に持つが、**着手時点で RetrievalService / AiAnalysisService /
FeedbackService は実装済みである**（ハイブリッド検索・検索モード 3 値・並び順 2 値・
ABAC 多値スコープ・属性値照会・RAG オーケストレータ・引用写像・SSE ストリーム・
フィードバックの upsert と認可）。

したがって**本作業の主眼は「実装を足すこと」ではなく「受け入れ基準を満たすと言えるかを
実測で確かめること」である**。判定は次の 2 軸で行う。

- **実装の有無** — 当該の振る舞いを持つコードが実在するか
- **テストで固定されているか** — その振る舞いを**壊したときに落ちるテスト**が実在するか

🔴 **テストが無い基準は「未検証」であり「満たしている」ではない。** 「実装がある」を
「満たしている」と読み替えないために、判定は必ず後者を根拠に書く。

GraphRAG（グラフ展開を使う検索戦略）は #448 のスコープ外である（issue 本文が明示。
別 issue で作業中の `GraphExpandingSearchService` は既存として扱い、壊さない）。

## 2. 母集合（受け入れ基準の全項目）

母集合は次の 3 出典を**自分で引いて**作った。issue 本文の「反映先」を転記していない。

1. issue #448 本文の「スコープ」と「退行防止（テスト必須）」（GitHub から取得・全文）
2. 計画 `02_requirements/01_requirements.md`（FR-03 / FR-04 / FR-07 / FR-08 の要求文と §受け入れ基準）
3. 計画 `03_usecases/01_usecases.md`（UC-01 / UC-02）・`04_workflows/02_rag-query-flow.md`（fixed）

| # | 出典 | 基準 |
| --- | --- | --- |
| A1 | issue 退行防止 1 | 検索の回帰テストセット（代表クエリ×期待文書を固定し、リランク・チャンク化変更時の劣化を検知） |
| A2 | issue 退行防止 2 | ABAC の否定形テスト（権限外文書が**検索結果・RAG 出典のどちらにも**現れない） |
| A3 | issue 退行防止 3 | RAG 応答の構造（出典リンク必須・ストリーミング完了・**エラー時の縮退文言**） |
| B1 | FR-03 | キーワードと自然文のハイブリッド検索（ベクトル＋全文） |
| B2 | FR-03 | 検索モード 3 値（hybrid〔既定〕/ keyword / semantic） |
| B3 | FR-03 | 並び順 2 値（relevance〔既定〕/ updated） |
| B4 | FR-03 | 検索結果に更新日時を含める |
| B5 | FR-05 / issue スコープ | ABAC フィルタの**全経路**適用（ベクトル・全文・属性値照会） |
| C1 | FR-04 | 検索結果を根拠に AI が回答し、**出典（元文書へのリンク）**を提示する |
| C2 | FR-04 / ADR-0043 | 対象範囲（タグ・部門・プロジェクト）を指定して問える。**候補は権限内に限る** |
| C3 | FR-04 | **出典には機密区分を含める** |
| C4 | 02_rag-query-flow | ストリーミング（citations → token* → done） |
| C5 | UC-01 例外フロー | **LLM 不調時は検索結果のみを返す**（縮退運転） |
| D1 | FR-07 | 指定データ範囲での**分析・比較・抽出** |
| D2 | FR-07 / FR-05 | データ範囲は ABAC と交差し**権限を広げない**（narrowing-only） |
| D3 | issue スコープ / ADR-0038 | **用途 `analysis` のモデルは `claude-opus-5`** |
| E1 | FR-08 | 👍/👎・コメントの収集 |
| E2 | FR-08 | **投稿は認証必須**（無認証は 401） |
| E3 | FR-08 | **統計は運用者・管理者に限る**（権限外は 403） |
| E4 | FR-08 | **同一利用者の同一回答への再投稿は 1 件**（上書き） |

**除外したもの（理由つき）**

- GraphRAG（ADR-0035）— issue 本文が着手保留と明示。別 issue が作業中。
- p95 レイテンシ・15 分以内の索引反映 — 計画側で `pending`（実測未了）。**負荷試験・実環境が要り、
  本環境（Docker 不可）では測れない。**
- nDCG@10 の実測（#336）— 実モデル配備が要る稼働環境依存。A1 は「実測値」ではなく
  「**順位の回帰を固定する決定論的なセット**」として実装する（issue の文言は「評価データを固定し
  …劣化を検知する」であり、nDCG 実測は #336 側の仕事である）。

## 3. 各項目の検証方法（先に決める）

- **B1〜B5 / C2 / C3 / D1 / D2 / E1〜E4**: 既存テストの**実在**を確認し、名前ではなく
  **アサーションの中身**を読んで「壊したら落ちるか」を判定する。
- **A1**: 既存テストの走査で**該当なし**なら新規に作る。RRF の定数・融合順序・並び順を
  変えたときに落ちる、代表クエリ×期待文書の固定セットとする。
- **A2（RAG 出典側）/ C5 / D3**: 走査で該当なしなら新規に作る。
- 追加テストは**必ず変異試験で検出力を確かめる**（実装を壊してテストが赤くなることを実測する）。

## 4. 追加するテストの置き場所（新規ファイルを作らない理由）

`scripts/check-test-spec-coverage.js` は **`docs/tests/` の仕様書 × テストクラス（`*Tests.cs`）の対**を
baseline（`scripts/test-spec-coverage-baseline.json`）と突き合わせる。**新規テストファイルを
docs/tests へ載せると baseline に対が無く fail する**が、本作業は `scripts/` を触らない
（他 issue と並行作業中のため統括が領域を限定している）。

よって**追加は既存のテストファイルへ行う**。

- 検索の回帰セット → `RetrievalService.Api.Tests/HybridSearchServiceTests.cs`
- RAG の ABAC 否定形・縮退文言・用途 → `AiAnalysisService.Api.Tests/RagOrchestratorScopeTests.cs`

## 5. 受け入れ基準（本作業）

1. §2 の全項目について「実装の有無 × テストで固定されているか × 判定」の表を報告に載せる。
2. 未充足・未検証の項目に**検出力のあるテスト**を足す（変異試験の証跡つき）。
3. 3 サービスのテストが緑。`check-test-traceability.js` / `check-test-spec-coverage.js` /
   `check-trace-blocks.js` が通る。
4. **統合テスト（Docker 依存）は skip される。skip を「通った」と書かない。**

## 6. 突き合わせの結果（実測）

判定は「テストで固定されているか」を根拠に書いた。**既存実装は概ね基準を満たしていたが、
3 か所は「実装はあるがテストが無い＝未検証」であり、うち 2 か所は変異試験で
「壊しても既存テストが 1 本も落ちない」ことを実測した。**

| # | 実装 | 固定 | 判定 |
| --- | --- | --- | --- |
| A1 | 該当なし | **無** | **未充足 → 追加**（回帰評価セットが存在しなかった） |
| A2 検索側 | 有 | 有 | 充足 |
| A2 RAG 出典側 | 有 | **無** | **未検証 → 追加**（変異 M4 で既存テストは 0 本しか落ちず） |
| A3 出典リンク | 有 | 有 | 充足 |
| A3 ストリーム完了 | 有 | 有 | 充足 |
| A3 縮退文言（出典が残ること） | 有 | **無** | **未検証 → 追加**（変異 M5 で既存テストは 0 本しか落ちず） |
| B1〜B5 | 有 | 有 | 充足 |
| C1〜C4 | 有 | 有 | 充足 |
| C5 | 有 | **無** | **未検証 → 追加**（A3 と同じ穴） |
| D1・D2 | 有 | 有 | 充足（D1 は後段到達を追加で固定） |
| D3 | 有（設定は別 issue が反映済み） | **無** | **未検証 → 追加**（用途文字列を誰も見ていなかった） |
| E1・E2・E3 | 有 | 有 | 充足 |
| E4 | 有 | **一覧のみ** | **部分 → 追加**（計画の文言は「集計される件数」であり、集計側は未固定だった） |

### 変異試験（追加テストの検出力）

| 変異 | 対象 | 結果 |
| --- | --- | --- |
| M1 `RrfK` 60 → 1 | HybridSearchService | 既存 1 本のみ赤（順位は動かない＝RRF の性質どおり。追加分は緑） |
| M2 融合から全文側を外す | HybridSearchService | **追加の golden が赤**（既存は別経路の 2 本） |
| M3 融合順を昇順に | HybridSearchService | **追加 4 観点すべて赤**（1 位・recall@3・否定形・golden） |
| M4 検索へ渡すスコープを全開に | RagOrchestrator | **追加 5 本が赤。既存は 0 本**（穴が実在した証拠） |
| M5 LLM 不調時に出典を捨てる | RagOrchestrator | **追加 1 本が赤。既存は 0 本** |
| M6 用途 `analysis` → `rag-answer` | RagOrchestrator | **追加 1 本が赤。既存は 0 本** |
| M7 upsert 経路を殺す | FeedbackEndpoints | 追加 1 本＋既存 1 本が赤 |

いずれの変異も**戻した**（3 サービスの `src/` に差分が無いことを `git diff --stat` で確認済み）。

## 7. 残件

- 🔴 **`scripts/test-spec-coverage-baseline.json` の床上げが未了**。新テストクラスを
  `docs/tests/FR-04_ai-answer-citations.md` へ記載したため、`node scripts/check-test-spec-coverage.js`
  が「床の上げ忘れ」1 件で fail する。**`--update` は `scripts/` の変更であり本作業の担当領域外**
  （並行作業中の他 issue も同ファイルを触る）ため、統括側で実行する。
- **統合テスト（Docker 依存）は本環境で走らない。** 実 Qdrant の全文 Match・実埋め込みの精度・
  p95 レイテンシ・索引反映時間は**測っていない**（計画側でも `pending`）。
- nDCG@10 の実測は #336（実配備が要る）。本作業の回帰評価セットは**実測値ではなく順位の固定**である。
