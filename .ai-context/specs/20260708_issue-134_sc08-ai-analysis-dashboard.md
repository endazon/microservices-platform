---
title: SC-08 AI分析ダッシュボード実装（Issue #134）
type: spec
status: done
related_ids:
  - SC-08
  - UC-02
  - FR-07
author: claude
created: 2026-07-08
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/03_usecases/01_usecases.md
---

# 仕様書: SC-08 AI分析ダッシュボード（Issue #134）

> 本仕様書は実装着手前に作成する。フロントエンド各画面フェーズ Wave A の 2 件目。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-07（指定データ範囲での分析・比較・抽出）、FR-05（ABAC narrowing）
- ユースケース（UC）: UC-02（AI分析を依頼する）
- 画面（SC）: SC-08 AI分析ダッシュボード
- 関連 ADR: [IADR-0005](../adr/IADR-0005_data-range-intersect-abac-narrowing-only.md)（範囲×ABAC narrowing-only）、[IADR-0033](../adr/IADR-0033_frontend-spa-foundation.md)（SPA 基盤）、[IADR-0009](../adr/IADR-0009_wiki-browsing-404-hides-existence.md)（存在秘匿）
- Issue: #134（親 #121）

## 目的・背景

SPA 基盤上に SC-08 を feature として実装する。BFF 集約 `POST /bff/analysis/analyze`（実装済）へ「指示＋タスク種別＋データ範囲」を送り、回答と番号付き出典を表示する。範囲は ABAC と narrowing-only で交差し、権限外は空回答へ縮退する（存在秘匿）。SC-10（#136, PR #156）で導入した基盤（`FeatureModule.nav` 等）の上に載せるため本ブランチは #156 にスタックする。

## 対象範囲

- 対象:
  - feature `features/sc08-analysis`（`/analysis` ルート、`RequireAuth` のみ／ロール限定なし）。
  - 分析フォーム（instruction 必須・taskType・range: query/topK/attributeFilters）と結果表示（answer＋citations、model/token 補足）。
  - 空縮退・エラーの中立表示（存在秘匿）。
  - ナビ「AI分析」（全認証ユーザー）。
  - テスト: Vitest（送信ペイロード・結果描画・出典リンク・空縮退・検証・異常系）、Playwright スモーク（未認証 `/analysis`→`/login`）。
  - ドキュメント: 本仕様書・画面仕様書・テスト仕様書。
- 対象外:
  - BFF/バックエンド変更（`/bff/analysis/analyze` は実装済）。
  - フィードバック（👍/👎）は SC-01 のスコープ。SC-08 では扱わない。
  - 出典からの SC-03 内部遷移（#129 実装後に接続）。

## 設計

### API 境界
- `apiFetch<AiAnswerDto>('/analysis/analyze', { json: AnalysisTaskRequest, method:'POST' })`。
- 要求: `{ instruction, taskType, range?: { query?, attributeFilters?, topK? } }`（camelCase、taskType は文字列 enum）。
- 応答: `AiAnswerDto`（answer, citations[CitationDto], model, inputTokens, outputTokens, answerId）。
- 空縮退（answer 空 or citations 空）および 403/404 → 中立メッセージ（存在秘匿。[IADR-0009](../adr/IADR-0009_wiki-browsing-404-hides-existence.md)）。400/5xx/network → alert。
- instruction は上限 2000 文字（バックエンド `AnalysisPromptBuilder.MaxInstructionLength` と整合）をクライアントで抑止し 400 を予防。topK は 1〜50 にクランプ（`0`・負値は下限 1）。

### 属性フィルタ UI
- key＋カンマ区切り値の行を可変追加（最小構成）。空 key の行は送信時に除外し、空なら `range` から省略する。

### 権限
- ロール限定なし（UC-02 は一般社員）。ABAC は後段が narrowing。UI は権限の有無を開示しない。

## 受け入れ基準

Issue #134 より転記:

- [ ] 画面仕様書が作成され、計画の画面設計・対応 UC と整合している → `docs/screens/SC-08_ai-analysis-dashboard.md`
- [ ] 範囲を指定して分析を依頼し、結果と出典が表示される
- [ ] 権限外の情報が表示されない（ABAC・存在秘匿の画面適用 → 空縮退・中立表示）
- [ ] テスト観点が `docs/tests/` へ展開されている → `docs/tests/SC-08_ai-analysis-dashboard.md`

## テスト方針

- 単体（Vitest + Testing Library）: `apiFetch` をモックし、送信ペイロード（instruction/taskType/range）を検証。結果（answer＋citations＋出典リンク）描画、空縮退の中立表示、instruction 空で実行不可、5xx で alert。
- E2E（Playwright, バックエンド不要）: 未認証 `/analysis`→`/login`。
- `/verify` 相当（typecheck/lint/build/test/e2e）で合否確認。

## 計画書との差異

- 差異: あり（記録のみ、コードは実 DTO に追従）。`docs/api/openapi.yaml` の `AiAnswerDto.citations` が `SearchResultDto` を参照しているが、実 DTO は `CitationDto`。UI は実 DTO に合わせる。OpenAPI 是正は別途（本 PR ではフィードバック起票まで行わない）。

## 未決事項

- 出典リンク先は当面 `sourceUri` 直リンク。SC-03（#129）実装後に内部遷移へ差し替える。
