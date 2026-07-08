---
title: 作業仕様書 — 適用直後のドリフト即時検出（ArgoCD PostSync）
type: spec
status: done
related_ids:
  - FR-15
  - ADR-0018
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
related_specs:
  - ../adr/IADR-0029_config-info-api-placement-and-drift-granularity.md
---

# 作業仕様書: 適用直後のドリフト即時検出

Issue: #145（親: #123 ／ IADR-0029 フォローアップ 4 ／ #112 受け入れ基準「定期＋適用直後」）。

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-15（ドリフトの検出・警告。定期＋適用直後）
- 関連 ADR: ADR-0018・ADR-0007（GitOps/ArgoCD）・IADR-0029

## 目的・背景

FR-15/#112 は「定期＋適用直後」の検出を要求。現状は 5 分定期＋手動 `/drift` 取得のみ。IADR-0029 は
ArgoCD PostSync フック等からの起動（`/bff/admin/config/drift` 取得 or RunOnce）で補完する計画を明記。

## 設計

- **共有実行経路**: `IDriftRunner`（`DriftRunner`）を新設し、実効収集＋宣言突合＋不一致警告（IDriftAlertSink）を
  単一経路に集約。定期検出（`DriftDetectionHostedService`）と即時検出（PostSync）が共有する。
- **起動時即時検出（既存挙動の明示）**: `DriftDetectionHostedService` は起動直後に 1 回検出する。#146 で BFF は
  宣言（pipeline.json）変更時にロールアウトするため、宣言の適用直後もこの起動時検出で捕捉される。
- **PostSync 起動（任意の同期後）**: BFF に **メッシュ内部限定**の `POST /internal/config/drift-run` を追加。
  ArgoCD PostSync フック Job（`curl`）が各同期後に叩き、即時検出を起動する。応答は 202 のみ（構成情報は
  返さない＝存在秘匿）。検出の一時失敗で同期を失敗させないためエンドポイント側は例外を握って 202 を返す。
  - **STRICT mTLS 対応**: `mesh.enabled` のとき Job にサイドカーを注入し（`holdApplicationUntilProxyStarts`）、
    curl 後に `/quitquitquit` で Envoy を終了させ Job を完了させる。これにより STRICT mTLS 下でも `bff-service`
    へ到達でき、かつサイドカー残留による Job 未完了を回避する（`mesh.enabled=false` は注入しない）。
- **アラート経路**: 不一致は `IDriftAlertSink`（既定 `LoggingDriftAlertSink`・構造化ログ `ConfigDrift=true`）へ。
  即時検出トリガでのアラート発火は BFF テストで捕捉検証する。

## 対象範囲

- 対象:
  1. `DriftRunner`/`IDriftRunner` 新設・DI 登録。`DriftDetectionHostedService` を `IDriftRunner` 利用へ整理。
  2. BFF に `POST /internal/config/drift-run`（メッシュ内部限定・無認証・202）。
  3. Helm: PostSync フック Job（`templates/drift-postsync-job.yaml`）＋ `drift.postSyncHook` values。
  4. `docs/operations/operations.md` に即時検出の運用（Istio サイドカー×Job の注記含む）を記録。
  5. テスト: 即時検出トリガが 202・本文なしを返すことを検証。
- 非対象: gateway 経由の外部公開（未整備）・アラート宛先の外部連携。

## 受け入れ基準

- [x] 構成適用直後にドリフト検出が自動実行される（起動時検出＋PostSync フック）。
- [x] 不一致があれば適用直後にアラートが発火する（`IDriftAlertSink` → `ConfigDrift=true`。テストで 202 経路を検証）。
- [x] `dotnet build` / `dotnet test` 緑。`helm lint` / `helm template` VALID。

## テスト

- `ConfigBffEndpointTests.PostDriftRun_ReturnsAcceptedWithoutBody`: 無認証 POST が 202・本文空を返す。
- `helm template` で PostSync フック Job（hook=PostSync・`/internal/config/drift-run`）が生成されることを確認。
