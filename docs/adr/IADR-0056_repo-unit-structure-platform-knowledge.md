---
title: IADR-0056 リポジトリ最上位のユニット構成（src/<unit>/{backend,frontend} = platform / knowledge）
type: impl-adr
status: Accepted
related_ids:
  - FR-14
  - ADR-0018
  - IADR-0027
  - IADR-0033
  - IADR-0117
  - IADR-0121
author: claude
created: 2026-07-10
updated: 2026-08-04
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

> **［2026-08-03 追記］決定 3 の「2 プロジェクト」は [[IADR-0117]] で 3 プロジェクトへ部分改定された（#455）。**
> 計画 [ADR-0030](../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md)
> が定める SharedKernel（Result / Error を自前実装。Domain 層は外部依存ゼロ）を、ユニット第一構成のまま
> 置ける場所が現行の 2 プロジェクトに存在しないためである。**ユニット外から参照できる
> `src/platform/backend/Shared/` のプロジェクトは、現行値では
> `Platform.Shared.Contracts` / `Platform.Shared.Infrastructure` / `Platform.Shared.Kernel` の 3 つである**
> （現行値は [IADR-0117](IADR-0117_platform-shared-kernel-placement.md) を正とする。
> `Platform.Shared.Kernel` の実体は未作成で、最初にそれを必要とするサービス再実装 issue が作成する）。
> 改定はこの 1 点に限り、決定 3 のうち「platform → 可変ユニットは禁止」「統合テストの例外」および
> 決定 1・2・4・5・6 は本 IADR が引き続き有効である（したがって状態は `Accepted` のまま）。
> 上記本文は 2026-07-10 時点の決定としてそのまま残す。

> **［2026-08-04 追記］決定 3 のうち「フロントエンドの可変ユニットが参照してよい共有物」は
> [[IADR-0121]] 決定 4 で 1 → 2 へ部分改定された（#446）。**
> 計画 [ADR-0031](../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md) が
> shadcn/ui ベースの共有 UI パッケージを 2 ユニットで共用すると確定し、その切り出し単位を実装側で
> 決めた結果である。**フロントエンドの可変ユニットが参照してよい共有物は、現行値では
> `@foundation`（`platform/frontend` の基盤）と `@platform/ui`（`src/packages/ui`）の 2 つである**
> （現行値は [IADR-0121](IADR-0121_spa-stack-migration-staging.md) 決定 4 と
> [`src/README.md`](../../src/README.md) 依存規則 例外 2 を正とする）。`@platform/ui` は
> デザイントークンとプリミティブのみを持ち、ドメイン・BFF 通信・ルーティング・認証を持たないため、
> ユニットの切り出し可能性は損なわれない。逆向き（`@platform/ui` → ユニット）の参照は禁止である。
> 改定はこの 1 点に限り、決定 3 の「platform → 可変ユニットは禁止」「合成点以外から可変ユニットを
> 参照しない」は本 IADR が引き続き有効である（したがって状態は `Accepted` のまま）。

4. **フロントエンド合成**: `src/` を npm workspaces ルートとする（`workspaces: ["*/frontend"]`・
   単一 lock）。platform 側 `platform/frontend/src/features/index.ts` を合成点とし、可変ユニットの
   features を束ねる（ユニット追加＝submodule 配置のみで workspaces に自動認識＋合成点へ import 1 行）。
   単体テスト・カバレッジはワークスペースルート（`src/vitest.config.ts`）で両パッケージを横断計測し、
   既存しきい値（ラチェット）を維持する。

> **［2026-08-04 追記］決定 4 の「npm workspaces ルート（`workspaces: ["*/frontend"]`・単一 lock）」は
> [[IADR-0121]] 決定 2 で pnpm workspace へ置換された（#446）。**
> 計画 [ADR-0031](../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md) が
> パッケージ管理を pnpm と確定したためである。**現行値は
> [`src/pnpm-workspace.yaml`](../../src/pnpm-workspace.yaml)（`'*/frontend'` ＋ `'packages/*'`）と
> 単一 lock `src/pnpm-lock.yaml`** である。決定 4 の趣旨——(1) 合成点は
> `platform/frontend/src/features/index.ts` の 1 ファイル、(2) ユニット追加は submodule 配置のみで
> workspace に自動認識され合成点へ import 1 行、(3) 単体テストとカバレッジは
> [`src/vitest.config.ts`](../../src/vitest.config.ts) で横断計測しラチェットを維持——は**すべて
> そのまま維持されている**（`'*/frontend'` のパターンが (2) を、`'packages/*'` が共有パッケージを
> 受け持つ）。したがって置換されたのはパッケージマネージャの名前と lock ファイルの形式だけであり、
> 状態は `Accepted` のままとする。

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
- Superseded by: なし（部分改定が 2 件ある。いずれも改定範囲が限定的で、依存の一方向性・合成点の
  一意性・横断カバレッジのラチェットといった本 IADR の骨格は有効なため `Accepted` を維持する）
  1. [IADR-0117](IADR-0117_platform-shared-kernel-placement.md): §決定 3 の「ユニット外参照を許す
     `platform/backend/Shared/` のプロジェクト数」のみを 2 → 3 へ（2026-08-03 / #455）
  2. [IADR-0121](IADR-0121_spa-stack-migration-staging.md): §決定 3 の「フロントエンドの可変ユニットが
     参照してよい共有物」を 1 → 2（`@foundation` ＋ `@platform/ui`）へ、§決定 4 の「npm workspaces」を
     pnpm workspace へ（2026-08-04 / #446）
