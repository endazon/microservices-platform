---
title: プラットフォーム全体の仕様監査・動作検証と整合是正
type: spec
status: done
related_ids: [FR-08, FR-15, SC-11, IADR-0010, IADR-0025, IADR-0029, IADR-0030, IADR-0031]
author: Claude (audit)
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
---

# 仕様書: プラットフォーム全体の仕様監査・動作検証と整合是正（Issue #118）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: 全 FR（横断監査）。是正対象は主に FR-14 / FR-15 / FR-08（送信者特定）
- ユースケース（UC）: —（横断監査）
- 画面（SC）: SC-11（構成ビューア。API 契約の openapi 反映）
- 関連 ADR: IADR-0010（利用者単位 upsert）/ IADR-0025（埋め込みルーティング）/ IADR-0028〜0030
  （宣言的構成・構成情報 API・運用者ロール）/ IADR-0031（送信者名クレームの解決方針・本作業で新設）
- 計画書リンク: `planning/projects/microservices-platform/`
- トラッキング Issue: #118

## 目的・背景

仕様書・ADR/IADR・計画書（FR/SC）と、実装（src/）・デプロイ構成（Helm / docker-compose /
Keycloak realm）との間の齟齬・抜け漏れを横断監査し、修正可能なものを是正する。
併せてビルド・全テスト・compose 起動・Playwright による画面確認で全体動作を検証する。

## 対象範囲

- 対象:
  - 監査で確定した齟齬の是正（ドキュメント・デプロイ構成の修正、および監査で実測した機能不全の
    局所的な回復に限る）
    1. `docs/api/openapi.yaml` への FR-15 構成情報 API（`/bff/admin/config`・`/drift`）追記
    2. 同 openapi.yaml への LlmGateway `/embed`（IADR-0025）追記・BFF パス齟齬（`/search`・`/analysis/*`
       → `/bff/...`）の是正
    3. `docs/functional/` への FR-14・FR-15 機能仕様書、`docs/tests/` への FR-14・FR-15 テスト仕様書、
       `docs/data/` への WikiPage データ仕様書（FR-13）の追加
    4. `deploy/docker-compose.yml` の keycloak / qdrant healthcheck 修正（curl 非搭載イメージで常時
       unhealthy → `KC_HEALTH_ENABLED` + bash /dev/tcp による検査へ変更）
    5. **FR-15 実効構成集約の機能不全の修復**: BFF `Introspection:Services` の既定 URL がローカル用
       ポート（5001/5006）のままで compose/Helm 双方で自己申告収集が常に到達失敗していたため、
       両環境に `:8080` を注入する（`deploy/docker-compose.yml`・`deploy/helm/.../values.yaml`）。
    6. **送信者特定（FR-08 / FR-15）の機能不全の修復**: 実 Keycloak トークンで `Identity.Name` が null
       になり FR-08 フィードバックの userId が全員 anonymous・FR-15 監査ログ subject が unknown へ潰れて
       いたため、共有 `AuthExtensions` に `NameClaimType=preferred_username` を設定する（設計判断は
       [IADR-0031](../adr/IADR-0031_name-claim-preferred-username.md) に記録）。
    7. dev realm に運用者検証ユーザー `poc-operator` を追加（IADR-0030 の E2E 検証を初回起動から再現）。
    8. CHANGELOG 補正（`3d8852f` の規約外件名を IADR-0015 の remap で是正）、Testcontainers CS0618 警告解消。
  - **是正の限定原則**: 機能コードの変更は「監査で実測した既存機能の不全を、既存 ADR の意図
    （IADR-0010 の利用者単位 upsert・IADR-0029 の監査ログ subject 特定）どおりに回復する局所修正」に
    限る。新機能追加・リファクタは対象外とし、方針を変える判断は IADR に残す（項目 6 → IADR-0031）。
- 対象外:
  - フロントエンド（SC-01〜SC-10 / SC-11 画面実装）の新規実装（後続フェーズ）
  - 上記「是正の限定原則」を超える機能追加・設計変更（新たな要求・設計変更を要する発見は Issue #118 で
    論点化し別作業とする）

## 設計

- 監査は traceability-auditor / adr-guardian の検査結果と、デプロイ構成の実測
  （compose 起動・healthcheck 実測・Playwright 画面確認）を突き合わせて確定する。
- openapi.yaml は手書き管理（`.github/workflows/openapi.yml` はスケルトン生成のみ）のため、
  実装済みエンドポイントの DTO（`EffectiveConfigDto` / `DriftReportDto` / embed 系）を写像して追記する。
- keycloak healthcheck は Keycloak 24（UBI ベース・curl/wget 非搭載）の制約に合わせ、
  `KC_HEALTH_ENABLED: "true"` を追加し bash の `/dev/tcp` で `/health/ready` を検査する。

## 受け入れ基準

- [x] openapi.yaml に `/bff/admin/config`・`/bff/admin/config/drift`・`/embed` が実装と一致する形で記載される
- [x] FR-14・FR-15 の機能仕様書、FR-14・FR-15 のテスト仕様書が存在し docs/README の規約に従う
- [x] `docker compose up` で keycloak / qdrant が healthy になる（実測）
- [x] FR-15 実効構成集約が運用者トークンで 200・自己申告収集が成功する（compose 実測）
- [x] 送信者特定が回復し FR-08 の userId が実ユーザー名（`poc-user`）で記録される（compose 実測）
- [x] 全テストが成功（`dotnet test`・13 アセンブリ 346 件・失敗 0・警告 0）し、compose 起動で主要画面
      （Wiki.js / Keycloak / Grafana）が Playwright で表示確認できる
- [x] 監査の発見事項（修正済み・論点）が Issue #118 に整理されている

## テスト方針

- ビルド・全テスト: `dotnet build` / `dotnet test`（既存 346 件）
- compose: 全サービス起動・healthcheck 実測・BFF 経由の疎通確認
- 画面: Playwright で Wiki.js（SC-04 相当）・Keycloak ログイン・Grafana（SC-10 参照先）の表示確認
- ドキュメント: `node scripts/check-doc-links.js` によるリンク検査（CI 同等）

## 計画書との差異

- 差異: なし（本作業は是正のみ。計画書の変更を要する発見は Issue #118 に論点として整理し、
  必要なら /plan-feedback で環流する）

## 未決事項

- SC-01〜SC-10 のフロントエンド着手時期（後続フェーズ。Issue #118 で論点として報告）
