---
title: IADR-0056 リポジトリ最上位のユニット構成（src/<unit>/{backend,frontend} = platform / knowledge）
type: impl-adr
status: Accepted
related_ids:
  - FR-14
  - ADR-0018
  - IADR-0027
  - IADR-0033
author: claude
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md"
---

# IADR-0056: リポジトリ最上位のユニット構成（src/<unit>/{backend,frontend} = platform / knowledge）

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: endazon（オーナー指定: issue #209/#210）・claude（実装詳細）

## 起点・関連

- 関連する計画書 ID（FR/UC/SC/ADR）: FR-14・ADR-0018
- 関連する実装仕様書: [作業仕様書](../specs/20260710_FR-14_repo-restructure-platform-knowledge.md)・
  [IADR-0027](IADR-0027_composability-folder-structure.md)・[IADR-0033](IADR-0033_frontend-spa-foundation.md)
- 関連 issue: #209（位置づけ是正）・#210（最終フォルダ構成）

## コンテキストと課題

本リポジトリの主たる成果物は**プラットフォーム基盤**であり、KnowledgePlatform（ナレッジ活用機能）は
付随する必須の可変機能セットである（オーナー確定・issue #209）。IADR-0027 はプロジェクト内の
`Foundation/`/`Composable/` 分離とサービスユニット規約を定めたが、リポジトリ最上位では基盤と
可変機能が混在しており（`src/` 直下に Bff・Services・Shared が同居、SPA は `frontend/` 直置き）、
「どこまでが基盤か」「可変機能ユニットをどこへ足すか」が構造から読み取れない。
最上位構成はオーナーが issue #210 で確定済みのため、本 IADR では**その実装詳細**
（ユニットへの振り分け・ビルド分割・合成方法・命名の扱い）を決定する。

## 検討した選択肢

最上位構成（**ユニット第一**: `src/{platform,knowledge,...etc}/{frontend,backend}`、各ユニットに
`backend.slnx` / `frontend/package.json`、追加可変機能は `src/<unit>` へのユニット単位 git submodule）は
issue #210 で確定済み・選択肢なし。実装詳細は以下を検討した。

1. **サービスのユニット振り分け**
   - a) 全 11 サービスを knowledge へ置き、platform は Shared のみ（最小 platform）
   - b) 認証・認可（ABAC）・LLM エグレス・エッジ（BFF）を platform へ、ドメイン機能を knowledge へ（採用）
   - c) Retrieval/Ingestion など RAG 機構も platform へ（最大 platform）
2. **ソリューション構成**
   - a) ルート集約 slnx を残し、ユニット slnx を追加（三重管理）
   - b) ユニット slnx のみとし、CI・スクリプトはユニット毎に実行（採用）
3. **.NET 名前空間・アセンブリ名**
   - a) 本再編と同時に `KnowledgePlatform.*` を `Platform.*`/`Knowledge.*` へ改名
   - b) 物理再編を先行し、改名はフォローアップ issue で段階実施（採用）
4. **フロントエンドの分割・合成**
   - a) 単一パッケージのままフォルダだけ分ける（package.json はユニットに置けない）
   - b) npm workspaces（ルート lock・単一 node_modules）＋ platform をアプリホスト、
     knowledge を features ソースパッケージとし、alias（`@foundation`/`@features`/`@knowledge`）で
     ソース参照・単一 vite ビルドに合成（採用）
   - c) knowledge を事前ビルドされたライブラリとして配布（ビルドパイプライン倍増・HMR 不可）

## 決定

1. **振り分け**: `platform/backend` = Shared.Contracts / Shared.Infrastructure / Bff /
   AuthorizationService / LlmGateway。`knowledge/backend` = DocumentService / DataSourceService /
   ConversionService / IngestionService / RetrievalService / AiAnalysisService / WikiService /
   FeedbackService / DashboardService / IntegrationTests。
   `platform/frontend` = foundation ＋アプリホスト（vite・e2e・nginx/config テンプレート）。
   `knowledge/frontend` = features（home・sc01..sc11）。
2. **ビルド**: 各ユニット直下の `backend/backend.slnx` のみ（ルート集約 slnx は廃止）。
   共通 MSBuild 設定（`Directory.Build.props` / `Directory.Packages.props`）は `src/` に置き、
   ディレクトリ継承で全ユニット（submodule ユニット含む）へ適用する。
3. **依存方向**: knowledge → platform は `src/platform/backend/Shared/` の 2 プロジェクトのみ許可。
   platform → knowledge は禁止（フロントエンドも同様: platform の合成点以外は knowledge を参照しない。
   合成点＝アプリホストがユニットを束ねる 1 ファイルで、IADR-0027 の合成ルート概念の最上位版）。
   - 例外: 統合テスト（`<unit>/backend/Tests/`）は検証対象サービスへの ProjectReference を許可する
     （例: IntegrationTests → platform の AuthorizationService.Api。テストはユニット横断の振る舞い検証が
     目的であり、プロダクションコードの依存には含めない。`src/README.md` 依存規則の例外 1 と同一）。
4. **フロントエンド合成**: `src/` を npm workspaces ルートとする（`workspaces: ["*/frontend"]`・
   単一 lock）。platform 側 `platform/frontend/src/features/index.ts` を合成点とし、可変ユニットの
   features を束ねる（ユニット追加＝submodule 配置のみで workspaces に自動認識＋合成点へ import 1 行）。
   単体テスト・カバレッジはワークスペースルート（`src/vitest.config.ts`）で両パッケージを横断計測し、
   既存しきい値（ラチェット）を維持する。
5. **命名**: .NET 名前空間・アセンブリ名（`KnowledgePlatform.*`）と Helm チャート名
   （`knowledge-platform`）は本再編では変更しない。改名はフォローアップ issue で段階実施する。
6. **submodule 境界**: 追加可変機能ユニットは `src/<unit>/`（`backend/`・`frontend/` を含む
   1 リポジトリ）として git submodule でリンクする。IADR-0027 の「サービス単位のサブモジュール」
   規約は本決定で**ユニット単位**へ変更する（サービスユニット規約はユニット内レイアウトとして存続）。

## 理由

- 振り分け b): `planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md` が再利用対象と
  する基盤能力（認証・認可・LLM 呼び出し・イベント連携・可観測性・エッジ）と一致する。
  a) は基盤の実体が Shared だけになり再利用単位として不足、c) は検索・取り込みがナレッジドメインの
  データモデル（文書・チャンク）に強く結合しており普遍化の実態がない。
- ソリューション b): ルート集約を残すとユニット追加（submodule）ごとに集約 slnx の更新が必要になり、
  「submodule 追加のみで組み込める」（FR-14）に反する。CI をユニット毎の実行にすれば集約は不要。
- 命名 b): 名前空間改名は数百ファイルの機械的差分になり、物理再編と混ぜるとレビュー不能になる。
  「1 コミット = 1 論理変更」の原則に従い分離する。
- フロントエンド b): ソース参照合成は import・テスト・カバレッジの既存資産を変更せず、
  HMR・単一ビルドを維持できる。c) は将来ユニットが増えて独立リリースが必要になった時点で再検討する。

## 結果

- 良い影響: 基盤と可変機能の境界がリポジトリ最上位で自明になり、可変機能ユニットの追加手順が
  「submodule 追加＋合成点 1 行」に確定する。ai-stock-trading 等の別プロジェクトが再利用する
  基盤の範囲（platform ユニット）が物理的に特定できる。
- 悪い影響・トレードオフ: 巨大な（ただし機械的な）移動差分が一度発生する。名前空間
  `KnowledgePlatform.*` とフォルダ（`platform/backend` 等）の不一致が改名完了まで残る。
  submodule ユニットは `src/` の共通 props に依存するため、単独ビルドには自前の設定が必要。
- フォローアップ（issue 起票）:
  1. .NET 名前空間・アセンブリ名・フロント package 名のユニット整合改名
  2. Helm チャート（`knowledge-platform`）・k8s リソース名の改名
  3. Shared.Contracts 内ナレッジドメインイベント契約の分離（platform 契約とナレッジ契約の区別）
  4. 追加可変機能ユニットの submodule 運用整備（テンプレート・CI 連携・単独ビルド規約）
  5. ユニット依存方向（platform→knowledge 禁止）の CI 検査

## 関連

- Supersedes: IADR-0027 の「サブモジュールとして追加する場合」の節（サービス単位 → ユニット単位。
  Foundation/Composable 規約・サービスユニット内レイアウトは存続）
- Superseded by: なし
