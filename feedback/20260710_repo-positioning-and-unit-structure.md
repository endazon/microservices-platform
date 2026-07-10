---
title: リポジトリ位置づけ（主=プラットフォーム基盤）とユニット第一フォルダ構成の計画側反映
type: plan-feedback
status: open
category: 新たな制約(ADR要)
related_ids:
  - FR-14
  - ADR-0018
source_repo: microservices-platform
source_ref: refactor/FR-14-platform-knowledge-restructure（issue #209/#210、docs/specs/20260710_FR-14_repo-restructure-platform-knowledge.md、IADR-0056）
author: claude
created: 2026-07-10
---

# フィードバック: リポジトリ位置づけ（主=基盤）とユニット第一フォルダ構成の計画側反映

## 種別

新たな制約(ADR要)（＋要求・ビジョンの位置づけ記述の是正）

## 起点となる計画書

- 機能要求（FR）: FR-14（コンポーザビリティ）
- 関連 ADR: ADR-0018（コンポーザブルアーキテクチャ）
- 計画書リンク: `projects/microservices-platform/00_vision/00_vision.md`・`02_requirements/01_requirements.md`・
  `06_technical/01_architecture-overview.md`・`06_technical/10_composability-design.md`・
  `07_adr/ADR-0018_composable-architecture.md`
- 傍証: `projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md`（基盤再利用の前提）

## 現状（計画書の記述 / As-Is）

- ビジョン（00_vision）は「社内ナレッジ活用プラットフォームを構築する」を目的として記述しており、
  ナレッジ活用が主・基盤が従の構図になっている（「その基盤をマイクロサービスアーキテクチャで構築する」
  という記述はあるが、成果物の主従が明示されていない）。
- ADR-0018 / 10_composability-design は固定（土台）/可変（組み替え）の区分を定義しているが、
  リポジトリ・成果物レベルの構成（どの単位で切り出し・再利用・submodule 化するか）は未定義。

## 実装側で確定した事実（オーナー確定・実装済み）

1. **位置づけ**: 本プロジェクトの主たる成果物は**マイクロサービスプラットフォームの基盤（platform ユニット）**。
   KnowledgePlatform（ナレッジ活用機能）は基盤に付随する**必須の可変機能セット（knowledge ユニット）**である
   （impl issue #209。ai-stock-trading の ADR-0001 が前提とする再利用構図とも一致）。
2. **フォルダ構成（ユニット第一）**: 実装リポジトリは以下へ再編済み（impl issue #210 / IADR-0056）。

   ```text
   src/
   ├── platform/   { backend/backend.slnx, frontend/package.json }   # 基盤（主成果物）
   ├── knowledge/  { backend/backend.slnx, frontend/package.json }   # 付随する可変機能
   └── ...etc      # 追加可変機能ユニット（git submodule で src/ 直下へリンク）
   ```

3. **ユニット振り分け**: platform = Shared.Contracts / Shared.Infrastructure / Bff / AuthorizationService（ABAC）/
   LlmGateway（LLM エグレス）＋ SPA 基盤（foundation・アプリホスト）。knowledge = 文書パイプライン〜検索〜
   AI 回答〜Wiki〜フィードバック〜利用集計の 9 サービス＋ナレッジ画面 features。
4. **依存規則**: 可変ユニット → platform は契約・基盤ライブラリのみ許可。platform → 可変ユニットは禁止
   （フロントは合成点 1 ファイルのみ）。submodule 境界はユニット（`src/<unit>` = backend+frontend を含む 1 リポジトリ）。

## 提案（計画側への反映案）

- **00_vision**: 目的を「再利用可能なマイクロサービスプラットフォーム基盤の構築」を主、
  「その最初の適用（必須可変機能）として社内ナレッジ活用を実装」を従とする二層構成へ改訂する。
- **02_requirements**: FR-14 の受け入れ基準に「可変機能ユニットを submodule 追加のみで組み込める」を明記し、
  基盤要求（認証/認可・LLM エグレス・メッセージング・可観測性・エッジ）と機能要求（ナレッジ）の帰属を区分する。
- **06_technical/01_architecture-overview・10_composability-design**: ユニット第一構成
  （`src/<unit>/{backend,frontend}`）・ユニット振り分け・依存規則の節を追加する。
- **07_adr**: 「リポジトリ・成果物のユニット構成（platform 主・機能ユニット従）」を新規計画 ADR として
  確定する（ADR-0018 の系譜。実装側 IADR-0056 を一次資料として利用可）。
- **用語集**: 「ユニット」「platform ユニット」「可変機能ユニット」「合成点」を追加する。
- ai-stock-trading 側: ADR-0001（platform-reuse）の再利用単位を「platform ユニット」と明確化する。

## 計画側で想定される反映先

- 要求更新（00_vision・02_requirements）／新 ADR（ユニット構成）／技術検討更新（01/10）／用語追加

## 備考

- 実装は完了済み（PR: refactor/FR-14-platform-knowledge-restructure）。計画側の確定変更は
  `/triage-feedback` と人間の判断に委ねる。
