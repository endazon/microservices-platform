---
title: 指定データ範囲AI分析 テスト仕様書
type: test-spec
status: in-progress
created: 2026-07-04
updated: 2026-08-21
author: claude
---
<!-- trace:
ids: [FR-04, FR-07, UC-02]
adrs: [ADR-0004, ADR-0010]
iadrs: [IADR-0004, IADR-0005]
specs: [01_requirements, 01_usecases]
issues: []
-->

# テスト仕様書: 指定データ範囲での分析・比較・抽出

## 起点となる計画書（トレーサビリティ）

- 機能要求: 指定データ範囲での AI 分析・比較・抽出
- ユースケース: AI 分析を依頼する
- 関連 ADR: LLM ゲートウェイ、認可＝ABAC（deny-by-default）
- 実装 ADR: 多値 allow-list ＋ deny-by-default、データ範囲×ABAC の narrowing-only 交差
- 計画書リンク: `02_requirements/01_requirements.md` / `07_adr/ADR-0010`

## テスト対象・範囲

- 対象: データ範囲×ABAC 交差ロジック（`DataRangeScopeResolver.Resolve`）、種別別プロンプト生成（`AnalysisPromptBuilder.Build`）、`/analysis/analyze`・`/analysis/ask` エンドポイント配線、ABAC 通信失敗時の deny-by-default 縮退（`RagOrchestrator`）。
- 対象外: 実 LLM 生成・実検索（`RetrievalService` / `LlmGateway` はスタブ差し替え）、BFF の Authorization 伝播網羅（AI 回答・出典側で検証）、負荷/p95、画面。

## テスト観点

- 正常系: 範囲と種別（分析/比較/抽出）を指定した依頼が回答＋出典を返す、`/analysis/ask` が回答を返す、種別ごとにプロンプトが切り替わる。
- 異常系: `instruction` 空は 400、ABAC 未許可・範囲が権限外は空回答（deny）へ縮退、ABAC 通信失敗（例外・タイムアウト）は 500 を伝播せず空回答へ縮退、未対応タスク種別は例外。
- 境界値: 範囲なし（ABAC をそのまま）、範囲のみキーの追加（narrowing）、空値集合の無視、値の大文字小文字非依存、指示の最大長超過での切り詰め。
- 非機能（セキュリティ）: 実効スコープが ABAC を決して広げない（narrowing-only 不変条件）、複数キーで 1 つでも積が空なら全体 deny、プロンプトへの見出しインジェクション無害化。

## テストケース一覧

| ID | 前提条件 | 手順 | 期待結果 | 対応受け入れ基準 | 区分（自動/手動） |
| --- | --- | --- | --- | --- | --- |
| T-01 | ABAC 未許可（granted=false）＋範囲指定あり | `DataRangeScopeResolver.Resolve` | `GrantsAccess=false`、`Filters` 空（deny-by-default 継承） | 権限を広げない | 自動 |
| T-02 | ABAC 許可（department∈{sales,hr}）、範囲なし | 同上（range=null） | `GrantsAccess=true`、フィルタは {sales,hr} のまま不変 | 範囲未指定は現状維持 | 自動 |
| T-03 | ABAC={sales,hr}、範囲={sales,finance}（共有キー） | 同上 | 実効フィルタは積集合 {sales} のみ（hr/finance は出ない） | narrowing-only 交差 | 自動 |
| T-04 | ABAC={sales}、範囲={finance}（権限外） | 同上 | 積が空 → `GrantsAccess=false`、`Filters` 空（全体 deny） | 権限外は拒否 | 自動 |
| T-05 | ABAC={department:sales, year:2025}、範囲で year を権限外に | 同上 | いずれか 1 キーの積が空なら全体 deny（漏えい防止の中核不変条件） | 権限外は拒否 | 自動 |
| T-06 | ABAC は department のみ制約、範囲が year を追加 | 同上 | year は narrowing として安全に追加、フィルタ 2 件（department=sales, year=2025） | 範囲での絞り込み | 自動 |
| T-07 | ABAC={sales}、範囲の department 値集合が空 | 同上 | 空値は無制約として無視、{sales} を維持 | 範囲での絞り込み | 自動 |
| T-08 | ABAC={Sales}、範囲={sales}（大小差） | 同上 | 大文字小文字非依存で積が成立、フィルタ 1 件 | 範囲での絞り込み | 自動 |
| T-09 | 種別 Analyze/Compare/Extract | `AnalysisPromptBuilder.Build` | プロンプトに種別語（分析/比較/抽出）・指示本文・文脈を含む | 種別ごとのプロンプト切替 | 自動 |
| T-10 | 任意の依頼 | 同上 | プロンプトに出典指示 `[1]` と「根拠」制約を常に含む | 出典・根拠の厳守 | 自動 |
| T-11 | 未対応の種別値（(AnalysisTaskType)999） | 同上 | `ArgumentOutOfRangeException`（既定へ黙って落とさない） | 種別の妥当性 | 自動 |
| T-12 | 指示に `## 参照文書` 等の偽装見出しを含む | 同上 | 偽装見出しは全角化（`＃#`）され本物のセクションとして残らず、本来の `## 指示` 構造は維持 | プロンプトインジェクション防止 | 自動 |
| T-13 | 指示が最大長 +50 文字 | 同上 | 最大長で切り詰められ、超過分は含まれない | 防御的入力処理 | 自動 |
| T-14 | ABAC サービスへの HTTP が例外（connection refused） | `RagOrchestrator.AskAsync` | 例外を伝播せず空回答（`Citations` 空）へ縮退 | deny-by-default 縮退 | 自動 |
| T-15 | ABAC サービスがタイムアウト（TaskCanceledException） | 同上 | 同様に空回答へ縮退 | deny-by-default 縮退 | 自動 |
| T-16 | スタブ RagOrchestrator でサービス起動 | `POST /analysis/ask`（question） | 200 OK、`Answer` 非空 | 質問回答（AI 回答の共通経路） | 自動 |
| T-17 | 同上 | `POST /analysis/analyze`（instruction, taskType=Compare, range.attributeFilters.year=[2025]） | 200 OK、`Answer` 非空 | 範囲・種別指定の分析 | 自動 |
| T-18 | 実 CitationMapper 経路（TestWebApplicationFactory） | `POST /analysis/analyze`（同上） | 200 OK、`Answer` 非空、`Citations` 非空 | 出典付与 | 自動 |
| T-19 | 同上 | `POST /analysis/analyze`（instruction 空） | 400 Bad Request | instruction 必須 | 自動 |

## テストデータ

- `AccessScopeResponse("u1", filters, granted)`：ABAC 許可スコープのスタブ（T-01〜T-08）。
- `AnalysisDataRange(AttributeFilters)`：`department`/`year` の許可値集合（T-01〜T-08）。
- `AnalysisTaskRequest(Instruction, TaskType)`：種別と指示（T-09〜T-13）。文脈は `"[1] 文書A\n抜粋A\n"`。
- `ThrowingHttpClientFactory`：常に指定例外を投げる `HttpClient` スタブ（T-14/T-15）。設定 `Llm:DefaultModel=claude-sonnet-4-6`。
- 依頼ボディ例（T-17/T-18）: `{ instruction: "2025 年の経費規程を比較して", taskType: "Compare", range: { attributeFilters: { year: ["2025"] } } }`。
- `StubRagOrchestrator`（配線確認用）: 固定の `AiAnswerDto`（Model=claude-sonnet-4-6、Citations 空）を返す。

## 関連仕様

- 機能仕様書: `../functional/FR-07_data-range-analysis.md`
- 作業仕様書: `../../.ai-context/specs/20260627_FR-07_data-range-analysis.md`
- 実装 ADR: `../../.ai-context/adr/IADR-0005_data-range-intersect-abac-narrowing-only.md`, `../../.ai-context/adr/IADR-0004_abac-multivalue-allowlist-deny-by-default.md`
- 関連テスト仕様: `./FR-04_ai-answer-citations.md`（出典付与）、`./FR-05_abac-access-control.md`（ABAC）
- テストコード: `src/knowledge/backend/Services/AiAnalysisService/tests/AiAnalysisService.Api.Tests/DataRangeScopeResolverTests.cs`, `AnalysisPromptBuilderTests.cs`, `AnalysisEndpointTests.cs`, `RagOrchestratorScopeTests.cs`, `src/knowledge/backend/Tests/Knowledge.IntegrationTests/AiAnalysisService/RagOrchestratorTests.cs`

## 未決事項

- タグ（`Tags`）による範囲指定は未対応（現状は属性キーのみ）。
- BFF（`/bff/analysis/analyze`）集約と Authorization 伝播の網羅検証は AI 回答・出典のテスト仕様と共通化。
- 実 LLM・実検索での性能/p95 検証は負荷試験タスクで別途実施。
