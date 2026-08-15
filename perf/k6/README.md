# 負荷試験ハーネス（k6）— NFR 性能目標の実測（#196）

計画 NFR の性能目標を**負荷試験で確認**するためのハーネス。環境非依存の準備物であり、実測はデプロイ済み環境
（`deploy/docker-compose.yml` もしくは k3s ステージング）が用意でき次第、下記手順で実行する。

## SLO（計画 NFR 受け入れ基準）

| 指標 | 目標 | 計測スクリプト |
| --- | --- | --- |
| 検索レイテンシ p95 | ≤ 1.5 秒 | `search-load.js`（threshold `p(95)<1500`） |
| RAG 初回応答 p95 | ≤ 5 秒 | `rag-load.js`（threshold `p(95)<5000`） |
| 取り込みスループット | ≥ 1 万文書／時 | 「取り込みスループット」節（手順） |
| 文書更新後の検索反映 | ≤ 15 分 | 「反映時間」節（手順） |

k6 は threshold 未達で**非ゼロ終了**するため、そのままゲート（CI/nightly）に組み込める。

## 前提

- [k6](https://k6.io/)（`k6 version` で確認）。
- 実行対象の稼働環境（BFF エッジに到達可能）。**本番相当のデータ量**（検索は 1 万件規模のカタログ）を投入しておく。
- 認証: BFF は Keycloak JWT を要求する。以下のいずれか。
  - `TOKEN=<事前取得済みアクセストークン>`（最優先）。
  - Keycloak パスワードグラント: `KC_TOKEN_URL` / `KC_CLIENT_ID`（既定 `platform-spa`）/ `KC_USERNAME` / `KC_PASSWORD`。
    dev realm の `poc-user` を使う場合は、`platform-spa` の direct access grants 有効化が前提。
  - 秘密情報はスクリプトに埋め込まない（env 経由・コミット禁止。`docs/security/security.md`）。

## 実行

```bash
# 検索（p95 ≤ 1.5s）
BASE_URL=http://localhost:5000 TOKEN=<jwt> k6 run perf/k6/search-load.js

# RAG（p95 ≤ 5s）
BASE_URL=http://localhost:5000 TOKEN=<jwt> k6 run perf/k6/rag-load.js

# Keycloak パスワードグラント例
BASE_URL=http://localhost:5000 \
  KC_TOKEN_URL=http://localhost:8080/realms/platform/protocol/openid-connect/token \
  KC_USERNAME=poc-user KC_PASSWORD=*** \
  k6 run perf/k6/search-load.js
```

負荷レベル（VU・stages）はシナリオ内で調整する。まず小さく開始し、SLO を満たす範囲で段階的に上げて限界を探る。

## 取り込みスループット（≥ 1 万文書／時）

k6 の HTTP 負荷では測りにくいため、**パイプライン投入 → 完了までのレート**で測る。

1. filesystem データソース（#195）に N 件（例 1,000）の対応ファイルを配置し `POST /bff/datasources/{id}/sync` を実行する
   （フロントは必ず `/bff/*` 経由。もしくは `RawDocumentFetched` を N 件発行するドライバを用意）。
2. `IngestionCompleted` の到達数（もしくは Qdrant の points 数）が N に達するまでの経過時間 `T` を計測する。
3. スループット = `N / T`（件/時に換算）。RabbitMQ 管理 UI（15672）・Grafana でキュー滞留・処理レートを併せて観察する。
4. ワーカー（conversion/ingestion）のレプリカ数（IADR-0050: ワーカーはワーカー数で水平スケール）を増やして再測し、
   目標 1 万件/時に必要なワーカー数を求める。

## 反映時間（≤ 15 分）

文書更新から検索反映までの end-to-end 時間を測る。

1. 既知の一意語を含む文書を新規投入（sync もしくは `POST /bff/documents`→publish）する。
2. `POST /bff/search`（その一意語）を一定間隔でポーリングし、ヒットするまでの経過時間を測る。
3. 経過時間 ≤ 15 分を確認する。未達ならパイプライン各段（変換・取り込み・埋め込み）のレイテンシを Tempo で分解する。

## 観察・判定

- **ダッシュボード**: Grafana `microservices-platform-overview`（サービス別スループット・5xx・p95/p99・RAG レイテンシ）。
- **アラート**: Prometheus SLO ルール（`deploy/prometheus/alerts.yml`・#198）。`SearchLatencyP95High` / `RagLatencyP95High` /
  `HighHttp5xxRate` の発火有無で SLO 逸脱を判定できる。
- **トレース**: Tempo で遅い経路を段階分解（検索＝ベクトル検索/ABAC、RAG＝検索+LLM）する。

## 結果の記録・追随

- 実測結果（日時・環境・負荷レベル・p95/スループット・合否）を `docs/tests/NFR-01_performance-load-test.md` の「実測記録」へ残す。
- 目標達成に応じて **HPA しきい値（#197 `values.yaml` の `scaling.hpa`）**・リソース requests/limits・RRF k 値等を調整する。
- 目標未達の場合は改善タスクを分割起票する（#196 想定対応方針 4）。
- FR-03 の未チェック受け入れ基準（p95・15 分反映）を実測で更新する。

## 制約（現状）

- **環境ブロック**: 実測にはデプロイ済み環境＋本番相当データ＋（RAG は）LLM 経路構成が必要。本ハーネスは受け皿であり、
  数値実測は環境準備後に行う（#196 は実測完了まで OPEN 維持）。
- ステージング整備（#207 / IADR-0049）と連動して nightly 実行のゲート化を検討する。
