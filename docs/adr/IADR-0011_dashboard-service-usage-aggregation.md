---
title: IADR-0011 業務指標ダッシュボードは専用サービスで集計し、回答品質は FeedbackService を単一の出所とする
type: impl-adr
status: Accepted
related_ids:
  - FR-10
  - UC-05
author: claude
created: 2026-07-03
updated: 2026-07-03
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-10)"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md (UC-05)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0006_observability-otel-prom-loki.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0002_service-boundaries-db-per-service.md"
---

# IADR-0011: 業務指標ダッシュボードは専用サービスで集計し、回答品質は FeedbackService を単一の出所とする

- 状態: Accepted
- 日付: 2026-07-03
- 決定者: claude（実装）
- 関連: ADR-0006（可観測性スタック）、ADR-0002（サービス境界 / DB-per-service）、FR-10（可視化ダッシュボード）、FR-08（回答フィードバック）

## コンテキストと課題

FR-10 は「利用状況・検索傾向・回答品質を可視化するダッシュボードを提供する」（UC-05）を求める。実装にあたり 3 点の判断が要った。

1. **インフラ指標との責務分担**: ADR-0006 は可観測性スタック（OTel/Prometheus/Loki/Tempo/Grafana）を確定し、
   フォローアップに「ダッシュボードのテンプレート化」を挙げる。FR-10 をこの Grafana 側で賄うか、アプリ側で集計するか。
2. **利用イベントをどこに置くか**: 検索・回答の利用状況／検索傾向を集計するデータの持ち主をどのサービスにするか。
3. **回答品質の出所**: 満足率は FR-08 の `FeedbackService` が既に集計する。ダッシュボード側に複製するか、参照するか。

## 検討した選択肢

1. **専用 `DashboardService`（専用 DB）で業務指標（利用状況・検索傾向）を集計し、回答品質は FeedbackService を
   BFF で集約**。ダッシュボードの契約は BFF `/bff/dashboard/summary` に集約する。
2. すべてを Grafana（ADR-0006）で可視化し、アプリ側に集計 API を持たない。
3. `AiAnalysisService` / `FeedbackService` に集計 API を相乗りさせ、専用サービスを作らない。

## 決定

選択肢 1 を採用する。

- **責務分離（アプリ集計 vs インフラ指標）**: ADR-0006 の Grafana は SLO/SLI・技術メトリクス（レイテンシ・
  エラー率・トレース）を担う。FR-10 が求める**業務指標**（何が・どれだけ検索され、回答がどれだけ満足されたか）は
  ドメインデータの集計であり、アプリ側の API として提供する。両者は対象が異なるため二重実装ではない。
- **専用サービス化**: ADR-0002（DB-per-service）に従い、利用イベントは独立した `DashboardService`
  （専用 DB `dashboard_svc`）で保持・集計する。既存サービスと同一構成（EF Core + Postgres、最小 API、
  起動時 `MigrateAsync`、ヘルスチェック）。`UsageEvent`（`EventType` = search|answer、`Query?`、`UserId`、`OccurredAt`）。
- **回答品質は複製しない**: 満足率は FR-08 の `FeedbackService`（`/feedback/stats`、PII 非含有）を**単一の出所**とし、
  BFF `/bff/dashboard/summary` が DashboardService の利用側サマリと並行取得して 1 応答（`DashboardSummaryDto`）へ集約する。
- **認可**: 利用傾向・検索語は運用情報のため、集計 API（`/dashboard/usage|trends|summary`、`/bff/dashboard/summary`）は
  `AdminOnly`（`platform-admin`）で保護する。BFF は DashboardService（AdminOnly）へ資格情報を伝播する。

  > **［2026-08-09 追記 / #544］保護の範囲を `platform-admin` ＋ `platform-operator` へ広げた。**
  > 計画 §SC-10 は閲覧を「**運用者・管理者**ロール限定」と定めており、裁定 **Q19 / Q28** で**計画が正**となった
  > （[[IADR-0129]] 決定 4 の追記を参照）。**両層（BFF・DashboardService の集計 3 口）を同時に広げてある。**
  >
  > **「運用情報だから保護する」という本決定の骨子は変わらない** —— 保護の**相手**が
  > 「管理者以外」から「**管理系ロール以外**」へ変わっただけである。
  > **イベント記録（`POST /dashboard/events`）の扱いも変えていない**（下段のとおり認証済みユーザへ開放）。
  一方、イベント記録（`POST /dashboard/events`）は集計の入力を欠かさないよう認証済みユーザに開放する
  （記録には検索語のみで文書本文は含めない）。
- **無制限集計の抑止**: 期間 `days`（既定 7・上限 90）、上位件数 `top`（既定 10・上限 50）をクランプする。
  期間索引 `(OccurredAt, EventType)` を張り、集計を軽量化する。

## 理由

- **専用サービス**: 利用イベントは書き込み主体・分析用集計という特性で、回答生成やフィードバック収集とは
  ライフサイクルが異なる。ADR-0002 の「サービスごとに DB を持ち独立してデプロイ・ロールバックする」に沿い、
  受け入れ基準④を満たす。相乗り（選択肢 3）は各サービスに別スキーマと集計負荷を持ち込み境界を曖昧にする。
- **Grafana に寄せない（選択肢 2 を採らない）**: 業務指標を PromQL/ログ集計で表現するのは、検索語トップや
  満足率のようなドメイン集計に不向きで、ABAC や画面契約（BFF）との整合も取りにくい。アプリ API の方が
  UC-05 の画面提供に直結する。
- **回答品質の単一出所**: 満足率を DashboardService に複製すると FeedbackService と二重管理になり乖離する。
  BFF 集約なら出所は 1 つで、FR-08 の集計ロジックをそのまま再利用できる。

## 結果

- 良い影響: 受け入れ基準④（独立デプロイ・ロールバック）を満たす。業務指標が 1 つのサマリ API で取得でき、
  画面（将来の SC）は BFF 契約を消費するだけでよい。回答品質は FeedbackService に一元化され乖離しない。
- 悪い影響・トレードオフ: サービス数が 1 つ増える（運用・配備の対象増）。利用イベントの発火（各サービスからの
  記録呼び出し）は段階的接続とし、本 PR では記録 API・集計 API の提供に留める。
- フォローアップ:
  - 検索実行・回答生成の経路（BFF/RetrievalService/AiAnalysisService）から `POST /dashboard/events` を発火する配線。
  - LLM API コストの可視化（ADR-0006 フォローアップ）は LlmGateway のメトリクスで別途対応。
  - 画面（SC）確定後にグラフ描画を実装。

## 関連

- Supersedes: なし
- Superseded by: なし
- 作業仕様書: [20260703_FR-10_usage-dashboard](../specs/20260703_FR-10_usage-dashboard.md)
- 関連: [IADR-0010](IADR-0010_feedback-service-and-upsert.md)（回答品質＝満足率の出所）
