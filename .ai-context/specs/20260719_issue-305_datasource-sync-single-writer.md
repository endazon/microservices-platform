---
title: 作業仕様書 — データソース定期同期の単一書き手化（本番マルチレプリカでの冗長 fetch 排除）
type: spec
status: draft
related_ids:
  - FR-01
  - UC-04
  - NFR
  - IADR-0051
  - IADR-0074
  - IADR-0083
author: claude
created: 2026-07-19
updated: 2026-07-19
issue: 305
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-01 データソース同期 / NFR 15分以内反映)
  - planning:projects/microservices-platform/03_usecases/ (UC-04 定期取得・継続失敗アラート)
---

# 作業仕様書: データソース定期同期の単一書き手化（#305）

## 起点・関連

- Issue: #305（priority:should・#299/PR #304・IADR-0074 のフォローアップ）
- 計画書 ID: FR-01（データソース登録・同期）／UC-04（定期取得・継続失敗アラート）／NFR（15 分以内反映）
- 関連 ADR: [IADR-0051](../adr/IADR-0051_datasource-connector-port-and-filesystem.md)（定期同期ワーカー本体）／[IADR-0074](../adr/IADR-0074_datasource-periodic-sync-helm-wiring.md)（Helm 配線・本番有効化。決定 4 で単一書き手化を本 issue へ先送り）
- 本作業の設計判断: [IADR-0083](../adr/IADR-0083_datasource-sync-single-writer-advisory-lock.md)

## 背景・課題（As-Is）

`DataSourceSyncHostedService`（[IADR-0051](../adr/IADR-0051_datasource-connector-port-and-filesystem.md)）は #299/PR #304（[IADR-0074](../adr/IADR-0074_datasource-periodic-sync-helm-wiring.md)）で Helm に配線され本番で有効化された。
`datasource` は本番 HPA で `minReplicas: 2`（`scaling.services`）のため、**2 pod が同時に定期同期ループを回す**。

`DataSourceSyncService` は成功済みファイルの再発行が決定的 DocumentId により下流で冪等 upsert されるため
**不整合は生じない（安全）**。ただし原本 fetch とイベント発行が**レプリカ数ぶん冗長**になり、コネクタ先
（ファイル共有等）・下流パイプラインに無駄な負荷がかかる。

## あるべき姿（To-Be）

定期同期の実行を**単一書き手**に限定し、レプリカ数に依存せず 1 サイクル 1 回にする。
datasource API（`/bff/datasources` 後段）の可用性（minReplicas 2 / PDB）は不変。

## 方式（詳細は IADR-0083）

**PostgreSQL セッションレベル advisory lock（`pg_try_advisory_lock`）** による単一書き手化を採用する。

- 各同期サイクルの実行前に、専用 `NpgsqlConnection` を開いて `pg_try_advisory_lock(<固定キー>)` を試行する。
- 取得できたレプリカのみが `SyncAllActiveAsync` を実行し、`finally` で `pg_advisory_unlock` + 接続破棄する。
- 取得できない（他レプリカが保持中／一時的接続障害）場合は**安全側で本サイクルをスキップ**し次周期へ（fail-safe）。
- 単一レプリカ（経路B）は競合が無く常に取得できる → **従来どおり毎サイクル実行**（後方互換）。
- 非リレーショナル DB（単体テストの InMemory 等）では advisory lock を使えないため、**常時取得（no-op）** で従来動作。
- 排他方式の選定理由・トレードオフ（k8s Lease 却下・トランザクションプーラ非対応の注意）は [IADR-0083](../adr/IADR-0083_datasource-sync-single-writer-advisory-lock.md) に記録。

### 実装対象（datasource-service に閉じる）

- 新規: `Foundation/Services/ISyncLeaseCoordinator.cs`（排他リースの抽象）。
- 新規: `Foundation/Services/PostgresAdvisoryLockLeaseCoordinator.cs`（advisory lock 実装）。
- 新規: `Foundation/Services/NoOpSyncLeaseCoordinator.cs`（非リレーショナル用の常時取得）。
- 変更: `Foundation/Services/DataSourceSyncHostedService.cs`（各サイクルでリースを取得してから同期）。
- 変更: `Program.cs`（DB プロバイダに応じてコーディネータを DI 登録）。
- 変更: `DataSourceService.Api.csproj`（`InternalsVisibleTo` をテストへ。既存 2 サービスの慣例に従う）。

### スコープ外（触れない）

- `k8s-local-up.sh`・infra manifest・realm・frontend/edge・他サービスの values ブロック（#328 と領域分離）。
- helm はコード側で完結するため **values 変更は原則不要**（datasource ブロック外は不変。CI ドリフト/images.yml を壊さない）。

## 受け入れ基準（#305）

- [ ] 本番マルチレプリカ環境で、1 同期サイクルあたりの原本 fetch が 1 回（重複しない）
  → advisory lock により同時刻に 1 レプリカのみが `SyncAllActiveAsync` を実行する。
- [ ] datasource API の可用性（minReplicas 2 / PDB）は不変 → `scaling.services` / PDB 未改修。
- [ ] 手動 `/sync`・fail-safe（watermark 前進条件・継続失敗アラート）は不変 → 該当コード未改修・回帰テスト維持。
- [ ] 方式は IADR に記録する → [IADR-0083](../adr/IADR-0083_datasource-sync-single-writer-advisory-lock.md)。

## テスト計画（TDD）

- `NoOpSyncLeaseCoordinatorTests`: 常に非 null のリースを返し、Dispose が安全であること。
- `SyncLeaseCoordinatorTests`（`PostgresAdvisoryLockLeaseCoordinator`）: 接続不能（到達不可なホスト）時に
  **null を返し例外を投げない**（fail-safe）こと。
- `DataSourceSyncHostedServiceTests`（既存 InMemory factory を活用）:
  - リース取得成功 → 1 サイクルで active データソースが同期され watermark 前進・リースが解放される。
  - リース取得失敗（deny）→ 同期が実行されず（fetch/watermark 前進なし）本サイクルは false を返す。
- `DataSourceSyncSingleWriterTests`（`Knowledge.IntegrationTests`・実 PostgreSQL/Testcontainers・`[DockerFact]`）:
  単一書き手化の核心＝2 レプリカ（別セッション）が競合しても同時刻に **1 つのみ取得成功**し、保持中は他方が取得不可、
  **解放後は別レプリカが取得できる**（liveness）ことを実コンテナで自動回帰ガードする（Docker 不在時はスキップ・CI で実行）。
- 既存回帰（`DataSourceSyncServiceTests` / `DataSourceSyncEndpointTests` / 配線テスト）は不変で通過。

## 検証（完了前）

- `dotnet build`/`dotnet test`（knowledge unit）緑。
- `dotnet format --verify-no-changes` 緑。
- `helm template`（本番 values / 経路B values-local）でエラーが出ないこと（datasource ブロック不変を確認）。
- `docs/DEFINITION_OF_DONE.md` を満たす。
