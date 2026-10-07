---
title: 作業仕様書 — グラフの 2 用途（graph-suggestion・graph-cluster-summary）を PurposeModels へ登録し、呼び出し側の用途名との突合を試験で固定する（#1785）
type: spec
status: done
related_ids: [FR-10, FR-11, FR-17, FR-18, ADR-0022, ADR-0035, ADR-0038, ADR-0044, ADR-0081, IADR-0022, IADR-0102, IADR-0106, IADR-0110, IADR-0266, IADR-0430, IADR-0498, IADR-0511]
author: claude
created: 2026-10-08
updated: 2026-10-08
issue: "#1785"
---

# 作業仕様書 — グラフの 2 用途を PurposeModels へ登録する（#1785）

> 本仕様書は実装着手前に作成した（着手 2026-10-08）。基点は MSP `origin/develop` `b3208a1d`。
> 計画は project-planning `main` `b5b584f`（GitHub 上の最新。読み取り専用）の
> `07_adr/ADR-0081`・`ADR-0044`・`ADR-0038`・`ADR-0022`・`ADR-0035` と `06_technical/04_ai-rag-stack.md` を読んだ
> （隣接クローン `aa068ac` は 2026-09-27 時点で古いため、GitHub の内容を正とした）。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-11**（LLM の呼び出し先を用途・機密度で切り替える）・**FR-17**（知識グラフ。クラスタ要約）・
  **FR-18**（AI 提案）・**FR-10**（LLM 利用実績の用途別計測）。
- 計画 ADR: **ADR-0081** 決定 3・フォローアップ 2（提案生成の費用を用途別の計測で切り分ける）／
  **ADR-0044** 決定 1（用途別・モデル別の計測）／**ADR-0038** 決定 3・5（フォールバックは 1 段下位・利用許可集合に残す）／
  **ADR-0022**（定型・高頻度は `claude-sonnet-5`。割当は実装の構成 `PurposeModels` の領分）／
  **ADR-0035** 決定 3（コミュニティ要約の生成モデルは `claude-opus-5`）。
- 実装 ADR: [[IADR-0511]]（本件で起こす）。先行: IADR-0022（用途→モデルの構成化）・IADR-0102 / IADR-0106（未登録の用途が
  無音で `DefaultModel` へ落ちる罠）・IADR-0110（計器の値域を `PurposeModels` で閉じる）・IADR-0266（提案生成は要求時）・
  IADR-0430（クラスタ要約）・IADR-0498（`rerank` の用途登録。同型の前例）。

## 問題

グラフサービスがゲートウェイへ送る用途 `graph-suggestion`（`LlmGatewaySuggestionClient.PurposeName`）と
`graph-cluster-summary`（`LlmGatewayClusterSummaryClient.PurposeName`）が `Llm:Routing:PurposeModels` に無い。

1. `LlmRouter.ResolveModel` は用途が無いとエンドポイントの `DefaultModel`（`claude-opus-5`・最も高い単価）へ落ちる。
2. 鎖（`PurposeFallbackModels`）も無いので、第 1 候補が 400 系で失敗するとそのまま失敗する。
3. `LlmMetricValues.NormalizePurpose` は `PurposeModels` に無い用途を `other` へ丸める。
   **提案生成・クラスタ要約の費用を他の未登録の用途と切り分けられない**（ADR-0081 フォローアップ 2 の答えが「できない」）。

## 受け入れ基準

- AC1: 用途 `graph-suggestion` で要求するとゲートウェイは `PurposeModels` に登録したモデルを選び、`DefaultModel` へは落ちない。
  `graph-cluster-summary` も同じ（ルーターの試験で固定。割当が既定と同値の用途は、既定を別の値へ差し替えても割当が選ばれることで区別する）。
- AC2: 呼び出し側の用途名の全数と `PurposeModels` のキーを突き合わせる試験があり、未登録の用途が増えたら落ちる（変異で確かめる）。
- AC3: 2 用途の費用（`llm.tokens.total` / `llm.cost.total`）が `other` ではなく用途名で出る。
- AC4: ADR-0081 フォローアップ 2 の環流の下書きを用意する（**起票は人が判断する**。本作業では起票しない）。
- AC5: 全環境に届く。`PurposeModels` を上書きする構成の層を全数確かめる。

## 母集合（規則 9・10。`b3208a1d` 時点）

### 軸 1: 呼び出し側の用途名（誤りの側＝「ゲートウェイへ送っているのに登録が無い」）

走査: `git grep -n "PurposeName\|Purpose *= *\"\|purpose: *\""`（`*.cs` / `*.ts` / `*.tsx`）・
`git grep -n -E '"(rag-answer|analysis|report-…|trade-decision…)"'`・`git grep -n "Purpose"`（knowledge の非試験 `*.cs` 全件）。

| 呼び出し側 | 用途名 | 宣言の形 | 登録 |
| --- | --- | --- | --- |
| AiAnalysis `RagOrchestrator`（検索チャット） | `rag-answer` | **引数の文字列リテラル**（`GenerateAsync(…, "rag-answer", ct)`・`StreamCompletionAsync(…, "rag-answer", ct)`）。別に `RagStreamMetrics.PurposeRagAnswer` | あり |
| AiAnalysis `RagOrchestrator`（AI 分析） | `analysis` | **引数の文字列リテラル**のみ（定数なし） | あり |
| Conversion `DiagramCodingInterpretation` | `diagram-coding` | `const string PurposeName` | あり |
| Retrieval `SearchRerankOptions` | `rerank` | `const string Purpose` | あり |
| Graph `LlmGatewaySuggestionClient`（REST・gRPC 共用） | `graph-suggestion` | `const string PurposeName` | **無い** |
| Graph `LlmGatewayClusterSummaryClient` | `graph-cluster-summary` | `const string PurposeName` | **無い** |

- 除外 E1: `src/ai-stock-trading`（submodule・別リポジトリ）。用途（`trade-decision`・`trade-decision-screening`・`report-*`）は
  AST の `LlmPurposes` が持ち、登録済み（IADR-0112 / IADR-0340）。**本リポの試験は submodule の未取得時にも走らせたいので対象にしない。**
  AST の `LlmPurposes.Stage0Recording` はゲートウェイへ送らない（費用計上イベントの区分）ので登録の要否に関わらない。
- 除外 E2: `EmbedPurpose.Query` / `EmbedPurpose.Index`（埋め込みの enum。補完の `PurposeModels` とは別の軸）。
- 除外 E3: `RagStreamMetrics.PurposeTag = "ai.purpose"`（計器の属性名であって用途名ではない）。
- 除外 E4: ゲートウェイ自身の `default`（`LlmMetricValues.DefaultPurpose`・`LlmBudgetOptionsValidator`）。用途未指定時の補い値。

→ 未登録は issue の主張どおり 2 件。**ただし `analysis` は定数を持たず、文字列リテラルを引数で中継している** ——
定数を拾うだけの検査は `analysis` を数え落とす（設計 3 で手当てする）。

### 軸 2: `PurposeModels` を上書きする構成の層（AC5）

走査: `git grep -n "Llm__Routing\|Llm:Routing\|PurposeModels"`（`deploy/`・全 `appsettings*.json`・`.env.example`・compose）、
`git grep -n -i "Llm__\|purpose" -- deploy`。

| 層 | `Llm__Routing__*` / `PurposeModels` | 扱い |
| --- | --- | --- |
| `LlmGateway/appsettings.json` | `PurposeModels`・`PurposeFallbackModels` の**唯一の宣言** | **対象**（ここへ足す） |
| `LlmGateway/appsettings.Development.json` | 無し（`Otlp`・`Auth` だけ） | 該当なし |
| `deploy/docker-compose.yml` | `Llm__ApiKey`・`Llm__Model` だけ | 該当なし（イメージ内の appsettings.json が効く） |
| `deploy/helm/microservices-platform/values.yaml`（本番） | `Llm__ApiKey`（secretKeyRef）だけ | 該当なし |
| `deploy/local/values-local.yaml`（経路 B） | `Llm__ApiKey` だけ | 該当なし |
| `.env.example` | LLM は API キーの節だけ | 該当なし |
| 試験の器 `TestWebApplicationFactory` | `Llm:Model` だけ | 該当なし（試験は実 appsettings.json を読む） |

→ **全環境が同じ appsettings.json（イメージに焼かれる）を読む。足す層は 1 つだけ**であり、上書きで消える層は無い
（IADR-0499 表 2 の「deploy 配下に `Llm__Routing__*` の上書きは 0 件」と同じ実測）。

### 軸 3: 用途の一覧を持つ文書（規則 10: この変更で古くなる自分の記述）

走査: `git grep -n "trade-decision-screening"`・`git grep -n "rerank\|rag-answer" -- docs deploy`・`git grep -n "graph-suggestion\|graph-cluster-summary"`。

| 文書 | 記述 | 扱い |
| --- | --- | --- |
| `docs/functional/FR-11_llm-egress-routing.md` 入力・既定設定・鎖の既定値（「7 用途」） | 用途の列挙 | **対象**（2 用途を足す。鎖は 9 用途） |
| `docs/tests/FR-11_llm-egress-routing.md` | T-28（`rerank`）が前例 | **対象**（T-30 を足す） |
| `docs/operations/llm-model-pin-runbook.md` §用途は 1 つではない | 用途の列挙（`rerank`・`trade-decision-screening` も欠けていた） | **対象**（現況の列挙なので 4 用途を足す） |
| `docs/operations/llm-cost-monthly-review-runbook.md` §過大計上の期間 | 2026-09-01〜是正配備の `claude-sonnet-5` 利用用途 | 除外 E5（**過去の期間の記述**。その期間グラフの 2 用途は `claude-opus-5` で走っており、足すと誤りになる） |
| `docs/operations/llm-model-pin-runbook.md` 2026-08-21 の追記ブロック（「鎖を持つのは 4 用途」） | 日付つきの記録 | 除外 E6（日付つき追記は当時の記録。本件で書き換えない） |
| `docs/observability/knowledge-health-indicators.md`・IADR-0430 | 「用途名 `graph-cluster-summary` で費用を見る」 | 変更不要（本件で初めて**事実になる**） |
| `.ai-context/specs/20260926_issue-380_…`（`other` と書いた表） | 確定済みの作業仕様書 | 除外 E7（凍結記録） |

## 設計

1. **割当**（[[IADR-0511]] 決定 1・2）:
   - `graph-suggestion` → `claude-sonnet-5`、鎖 `claude-haiku-4-5`。候補・辺の型・タグを**閉じた一覧から選んで JSON で返す**
     短い選別の仕事で、`diagram-coding`（分類・短い抽出）・`rag-answer`（定型・高頻度）と同じ層である。
     利用者の操作ごとに発生する（ADR-0081 決定 3）ので単価が効く（`claude-opus-5` $5/$25 → `claude-sonnet-5` $2/$10 per 1M）。
   - `graph-cluster-summary` → `claude-opus-5`、鎖 `claude-sonnet-5`。**ADR-0035 決定 3 がコミュニティ要約の生成モデルを
     `claude-opus-5` と名指ししている**ので、実装が下げる根拠を持たない。現状（既定へ落ちて `claude-opus-5`）と同じモデルだが、
     **既定と同値でもエントリは省略しない**（IADR-0112 決定 1。既定の改定で無音に失効する）。鎖は `analysis`・`default` と同じ 1 段下位。
   - ZDR: 2 用途とも `ZeroDataRetentionPurposes` へは足さない（決定 3）。送る文書の最高区分を名乗っており区分の規則が効く。
     `claude-managed` の `NonZdrModels` は空で、3 モデルとも ZDR 対応である（T-23 が固定）。
   - 新しいモデル ID は使わない（3 つとも `claude-managed` の `Models` に既に在る）。
2. **登録**: `appsettings.json` の `PurposeModels` と `PurposeFallbackModels` へ 2 用途ずつ足す。
3. **呼び出し側の用途名との突合**（AC2。[[IADR-0511]] 決定 4）:
   - `scripts/lib/llm-purposes.js` に純関数（C# 本文から用途名を拾う・appsettings.json のキーと突き合わせる）と実ツリーの走査を置き、
     `scripts/scripts.repo.test.js` が実ツリーと合成の本文（変異）の両方を叩く。**新しい `check-*.js` は作らない** ——
     本検査は CI のどのジョブでも走る `scripts-tests` に載れば足り、検査器の本数ラチェットを動かす理由が無い。
   - 拾う形: ① 名前に `Purpose` を含む `const string`（`PurposeName`・`Purpose`・`PurposeRagAnswer`）
     ② 名前付き引数・初期化子への文字列リテラル（`Purpose: "x"` / `Purpose = "x"`）。
   - **`RagOrchestrator` の文字列リテラル中継を定数へ改める**（`RagAnswerPurpose`・`AnalysisPurpose`）。値は変えない。
     中継の形（引数の文字列リテラル）を一般に拾う解析は、引数の分割に入れ子のラムダ・括弧が絡み脆い。**宣言の形をそろえる側で塞ぐ。**
   - 0 件走査は赤（fail-closed）。走査範囲は `src/platform/backend`・`src/knowledge/backend`（submodule・`/Tests/`・`bin`/`obj`・ゲートウェイ自身を除く）。
4. **計測**（AC3）: `NormalizePurpose` は変えない（値域は `PurposeModels` が閉じる。IADR-0110）。登録したことで用途名が出ることを
   実 appsettings.json を読むホストの試験で固定する（`rerank` の T-28 と同型）。

## 試験（受け入れ基準の写像）

| ID | 内容 | 置き場 |
| --- | --- | --- |
| AC1 | 2 用途 × 区分 public / internal / confidential / restricted で `Sent=true`・割当モデル（`DefaultModel` でない／既定差し替えでも割当） | `GraphPurposeEndpointTests`（新設） |
| AC1 | 実設定の `PurposeModels`・`PurposeFallbackModels` の値（鎖が 1 段下位・安価側） | 同上 |
| AC2 | 実ツリーの呼び出し側用途名 ⊆ `PurposeModels` キー・6 用途を拾う・変異（未登録の定数／リテラルを足す）で赤 | `scripts.repo.test.js` |
| AC3 | `llm.tokens.total` / `llm.cost.total` に `llm.purpose=graph-suggestion`・`graph-cluster-summary`、`other` が無い・`NormalizePurpose` が名前を返す | `GraphPurposeEndpointTests` |

## 実測

- ビルド（submodule 初期化済み）: platform `backend.slnx`・knowledge `backend.slnx` とも警告 0・エラー 0。
  `dotnet format --verify-no-changes` は両 slnx とも exit 0。
- 試験: platform `backend.slnx` 全緑（LlmGateway.Tests 369 件。うち新設 `GraphPurposeEndpointTests` 17 件）。
  knowledge `backend.slnx` は AiAnalysisService.Tests 179 件ほか緑。赤は `Knowledge.IntegrationTests` 72 件（本環境に Docker デーモンが無い。
  Testcontainers の `DockerUnavailableException`）と `DocumentService.Tests` の `NormalizedBodyPresenceTests` 1 件（単独の再実行で 3/3 緑。
  消費の待ち合わせの揺らぎで、本件の変更が触れない経路）。
- `node scripts/scripts.test.js`（`REQUIRE_REPO_TESTS=1`）: #1785 の節 6 件を含め緑。**ただし `check-adr-numbering` が `IADR-0510 が欠番` で赤になる** ——
  0510 は並行レーンの予約番号で、本作業は 0511 を使う指示である。0510 の仮ファイルと索引行を一時的に置くと 979 件すべて緑（置いたものはコミットしない）。
  **0510 のレーンが先に develop へ入れば解消する**（FIFO）。
- 変異（C#）: `PurposeModels` から 2 用途を外すと `GraphPurposeEndpointTests` の 10 件が赤（提案生成の 4 区分・既定差し替え 2・既定へ落ちない 1・
  正規化 2・計上 1）。クラスタ要約の 4 区分の解決は既定と同値なので緑のまま —— 既定差し替えの試験がそれを捕まえる。戻すと緑。
- 変異（用途名の突合。`scripts.repo.test.js` #1785 の節）:

  | 変異 | 結果 |
  | --- | --- |
  | M1 `PurposeModels` から `graph-suggestion` を外す | 実ツリーの突合 1 件が赤 |
  | M2 `LlmGatewayClusterSummaryClient` に `const string ExperimentalPurpose = "graph-experimental"` を足す | 同上が赤 |
  | M3 同クライアントの `Purpose: PurposeName` を `Purpose: "graph-inline"` に変える | 同上が赤 |
  | M4 `RagOrchestrator` を develop の版（リテラル中継）へ戻す | 陽性対照「6 用途を拾う」が赤（`analysis` を拾えない） |

  いずれも戻すと緑。
- 文書検査（`git add -A` 後）: `check-doc-links`・`check-trace-blocks`・`gen-knowledge-graph --check`・`check-reading-budget`・`check-nul-bytes`・
  `check-scaffolding-frames`・`check-workflow-job-refs`・`check-test-traceability`・`check-test-spec-coverage`（`--update` で床に T-30 の対を 1 件追加）・
  `check-test-name-references`・`check-cross-repo-refs`・`check-plan-id-qualification`・`check-doc-status-vocabulary`・`check-doc-type-vocabulary`・
  `check-unit-dependencies`・`check-backend-libraries`・`check-cpm-versions` は exit 0。`check-adr-numbering` は上記の欠番で exit 1。
- ADR-0081 フォローアップ 3 の前提の確認: 提案の生成器（`AiSuggestionGenerator`）を呼ぶのは `Features/AiSuggestions/Generate/Endpoint.cs` だけで、
  `DocumentUpdated` の購読からは呼ばれない（要求時のまま。定期・自動起動へは切り替えていない）。
- 計画への環流（AC4）: project-planning の issue を全件（743 件）走査し、ADR-0081 フォローアップ 2 の同件は無かった。下書きは作業の報告に載せ、起票は人が判断する。
