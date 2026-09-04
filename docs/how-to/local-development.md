---
title: how-to — ローカル開発フロー
type: how-to
status: published
created: 2026-07-09
updated: 2026-09-03
author: claude
---
<!-- trace:
ids: [FR-13, FR-14, UC-07]
adrs: [ADR-0048]
iadrs: [IADR-0017, IADR-0026, IADR-0032, IADR-0046, IADR-0056, IADR-0228, IADR-0331]
specs: [20260831_issue-1092_planning-submodule-residual-refs]
issues: [#1092]
-->

# how-to: ローカル開発フロー

このリポジトリをローカルで動かし、ビルド・テスト・全サービス起動までを行う手順。記載内容は
[`deploy/docker-compose.yml`](../../deploy/docker-compose.yml)・[`scripts/`](../../scripts/README.md)・
`.github/workflows/*.yml` の実際の設定に基づく。

## 1. 前提ツール

| ツール | バージョン | 備考 |
| --- | --- | --- |
| .NET SDK | 10.0.x | [`global.json`](../../global.json) は `8.0.0` + `rollForward: latestMajor`（10.x でビルド可）。ターゲットは [`src/Directory.Build.props`](../../src/Directory.Build.props) で `net10.0` |
| Node.js | 22 | フロントエンド CI（[`frontend.yml`](../../.github/workflows/frontend.yml)）と揃える |
| Docker / Docker Compose | v2 相当 | インフラ・全サービスのローカル起動 |
| git | — | ユニット submodule（`src/ai-stock-trading`）の取得を含む |

## 2. clone・計画リポジトリの参照

```bash
git clone --recurse-submodules <this-repo-url>
# 既存 clone の場合
git submodule update --init --recursive
```

取得されるのは `src/<unit>` のユニット submodule である（現在は `src/ai-stock-trading` の 1 件）。

**計画リポジトリ `project-planning` は本リポジトリの submodule ではない。** 要求（FR）・
ユースケース（UC）・画面設計（SC）・計画 ADR の一次情報は別リポジトリにあり、GitHub 上の URL を
直接開くか、**隣接クローン**（既定パス `../project-planning`。読み取り専用・pin 固定なし）を用意して読む。
参照専用のトークンは要らない。

## 3. バックエンド（.NET）

```bash
dotnet build src/platform/backend/backend.slnx --configuration Release
dotnet build src/knowledge/backend/backend.slnx --configuration Release
dotnet test src/platform/backend/backend.slnx
dotnet test src/knowledge/backend/backend.slnx
```

- ソリューションは新形式 `.slnx` をユニット毎に持つ（[`src/platform/backend/backend.slnx`](../../src/platform/backend/backend.slnx) / [`src/knowledge/backend/backend.slnx`](../../src/knowledge/backend/backend.slnx)。ルート集約ソリューションは置かない。コンポーザビリティ要求に基づくユニット第一のリポジトリ構成による）。
- パッケージバージョンは Central Package Management で [`src/Directory.Packages.props`](../../src/Directory.Packages.props) に集約。
- フォーマット確認（CI と同じ検査）: `dotnet format <ユニットの backend.slnx> --verify-no-changes`。
- devcontainer 経由（Codespaces 等）では `scripts/setup.sh` が `postCreateCommand` として自動実行され、
  各ユニットの `dotnet restore` を行う（[`.devcontainer/devcontainer.json`](../../.devcontainer/devcontainer.json)）。

## 4. フロントエンド（React + TypeScript + Vite）

```bash
cd src   # pnpm workspace ルート（メンバは pnpm-workspace.yaml が正。雛形 templates/*/frontend も含む）
pnpm install
pnpm run dev         # http://localhost:3100（/bff は BFF(5000) へプロキシ）
pnpm run typecheck
pnpm run lint
pnpm run test         # Vitest 単体（jsdom）
pnpm run test:coverage
pnpm run build        # tsc -b && vite build
pnpm run test:e2e     # Playwright（ブラウザ未取得なら pnpm exec playwright install chromium）
```

Keycloak ログインを伴う開発には、dev スタック（`docker compose -f deploy/docker-compose.yml up -d keycloak bff`）
と realm の public client `platform-spa`（redirect `http://localhost:3100/*`。realm import 済み）が必要。
詳細は [`src/platform/frontend/README.md`](../../src/platform/frontend/README.md)。

## 5. インフラ + 全サービスの起動（dev）

`docker-compose.yml` は Postgres / RabbitMQ / Redis / Keycloak / Qdrant / MinIO / 可観測性スタック
（OTel Collector・Prometheus・Loki・Tempo・Grafana）と、全マイクロサービス・BFF・フロントエンドを
定義する（[`deploy/docker-compose.yml`](../../deploy/docker-compose.yml)）。

推奨は `scripts/compose-up.sh`（`docker compose` の薄いラッパ）で起動すること。実行中の Git コミット
ID・日時・作成者を環境変数として自動注入し、BFF の構成情報 API（`/bff/admin/config`）が dev でも
実バージョンを返せるようにする（構成バージョン履歴の正データ源は GitOps 層とし、API は注入スライスを surfacing する）。

```bash
bash scripts/compose-up.sh up -d
# 一部サービスのみ起動する場合
bash scripts/compose-up.sh up -d bff keycloak
```

`docker compose` を直接使う場合は、必要なら手動で同じ環境変数を渡す。

```bash
GIT_COMMIT=$(git rev-parse --short HEAD) docker compose -f deploy/docker-compose.yml up -d
```

### 起動後のエンドポイント（dev の host 公開ポート）

内部サービス（DocumentService・RetrievalService 等）は `expose` のみでホスト非公開
（mesh 導入までの暫定措置としてネットワーク分離を第一防御としていたもの。サービス間認証の第一防御は
すでに Istio STRICT mTLS に移行済みで、
ネットワーク分離は多層防御として存続している）。外部から到達できるのは以下のみである。

| サービス | URL | 備考 |
| --- | --- | --- |
| フロントエンド | http://localhost:3100 | `/bff` は Caddy が BFF へプロキシ |
| BFF | http://localhost:5000 | フロントエンドの唯一の入口（エッジ） |
| Keycloak | http://localhost:8080 | realm `platform` を import 済み |
| Wiki.js（管理UI直接） | http://localhost:3001 | **dev限定**の公開（dev ホスト公開は残し、本番系〔Helm〕の非公開は回帰ガードで保証する）。本番系は非公開 |
| Grafana | http://localhost:3000 | 匿名 Admin（dev 限定） |
| Prometheus | http://localhost:9090 | |
| RabbitMQ 管理UI | http://localhost:15672 | guest/guest |
| MinIO コンソール | http://localhost:9001 | dev 便宜公開（API の 9000 は非公開） |
| Postgres | localhost:5432 | postgres/postgres（各サービスの DB は `create-multiple-dbs.sh` で作成） |

### Wiki.js の初期セットアップ（初回のみ）

Wiki.js を使う機能を試す場合、初回のみ管理 UI（`http://localhost:3001`）で
管理者アカウント作成・ja ロケール導入・OIDC 連携・ローカルログイン無効化・同期用 API キー発行が
必要。手順は [`docs/operations/operations.md`](../operations/operations.md) の
「Wiki.js の起動・初期セットアップ・ヘルスチェック」「Wiki.js 同期シークレットの発行・投入」を参照。

## 6. よくある詰まり

| 症状 | 対処 |
| --- | --- |
| `src/ai-stock-trading/` が空 | `git submodule update --init --recursive` を実行する |
| 計画書（FR/UC/SC/計画 ADR）が見つからない | **本リポジトリには入っていない**（submodule ではない）。隣接クローン `../project-planning` を用意するか、GitHub 上で開く |
| Keycloak の healthcheck が unhealthy のまま | Keycloak 24 イメージは curl/wget 非搭載。compose の healthcheck は bash の `/dev/tcp` で検査するため数十秒〜1分程度は正常な起動待ち（`deploy/docker-compose.yml` のコメント参照） |
| Wiki.js の OIDC ログインが `Failed to fetch user profile` | Issuer は `http://localhost:8080/realms/platform`（ブラウザ経路）で設定する。`keycloak:8080` を指定すると失敗する（`docs/operations/operations.md` 実測記録） |
| フロントエンドから BFF に到達しない | dev は `pnpm run dev` の Vite プロキシ（`VITE_BFF_TARGET` で上書き可）または compose の Caddy `/bff` プロキシ経由。BFF(5000) が起動しているか確認する |
| LLM/埋め込み呼び出しが失敗する | `.env`（gitignore 済み）に `ANTHROPIC_API_KEY` / `VOYAGE_API_KEY` 等を設定する（`deploy/docker-compose.yml` の `llm-gateway` 環境変数を参照）。キー未設定でも起動はするが呼び出しは失敗する |

## 関連ドキュメント

- デプロイ（stg/prod・GitOps）: [`deployment.md`](deployment.md)
- 完了の定義（PR を出す前のチェックリスト）: [`../DEFINITION_OF_DONE.md`](../DEFINITION_OF_DONE.md)
- 実装ワークフロー全体: [`../ai-workflow.md`](../ai-workflow.md)
