---
title: データソース定期同期の本番デプロイ配線（Helm values / DataSourceSync）（Issue #299）
type: spec
status: done
related_ids:
  - FR-01
  - UC-04
  - NFR
  - IADR-0051
  - IADR-0066
  - IADR-0074
author: claude
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-01 データソース登録・同期 / NFR 15分以内反映)
  - planning:projects/microservices-platform/03_usecases/ (UC-04 データソースを登録・同期する: 基本フロー『システムが定期的に原本を取得』)
related_specs:
  - "../adr/IADR-0074_datasource-periodic-sync-helm-wiring.md"
  - "../adr/IADR-0051_datasource-connector-port-and-filesystem.md"
  - "../adr/IADR-0066_local-k8s-dev-environment.md"
  - "../../docs/operations/operations.md"
  - "20260710_issue-195_filesystem-connector-and-sync.md"
---

# 仕様書: データソース定期同期の本番デプロイ配線（Helm values / DataSourceSync）（Issue #299）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): FR-01（データソース登録・同期・カタログ化）
- ユースケース(UC): UC-04（データソースを登録・同期する。基本フロー「システムが定期的に原本を取得」・例外フロー「接続失敗時の再試行／継続失敗アラート」）
- 非機能要件(NFR): 「文書更新後、定義した時間内（例: 15 分以内）に検索結果へ反映される」
- 実装判断: [IADR-0074](../adr/IADR-0074_datasource-periodic-sync-helm-wiring.md)（本 PR: Helm 配線方式・間隔根拠・多重実行の扱い）／[IADR-0051](../adr/IADR-0051_datasource-connector-port-and-filesystem.md)（定期同期ワーカー本体・増分 watermark の既存決定）／[IADR-0066](../adr/IADR-0066_local-k8s-dev-environment.md)（ローカル k8s dev 環境＝経路B）
- Issue: #299（本 issue・High）／出所: 実環境構築前監査（2026-07-18、対象コミット `10d79e0`。planning `draft/20260718_pre-production-audit.md`）

## 目的・背景（As-Is）

定期同期ワーカー `DataSourceSyncHostedService`（[IADR-0051]・#195）は実装済みだが **既定無効**
（`DataSourceSync:Enabled=false` で `ExecuteAsync` 冒頭 return）。`docs/specs/20260710_issue-195_filesystem-connector-and-sync.md`
は「定期同期は既定無効。本番は config で有効化する」と明記する。

しかし `deploy/helm/microservices-platform/values.yaml` を含む `deploy/` 配下に `DataSourceSync` の設定が
**一切存在しない**。このまま実環境を構築すると手動 `POST /datasources/{id}/sync` のみの運用になり、
**UC-04 基本フロー（定期取得）と NFR「15 分以内反映」が満たせない**。監査の Go 条件に含まれる High ギャップ。

## 調査で確定した事実

- `DataSourceSyncOptions`（`Foundation/Services/DataSourceSyncOptions.cs`）: `SectionName="DataSourceSync"`、
  `Enabled`（既定 false）、`IntervalSeconds`（既定 300）。`Program.cs` は
  `Configure<DataSourceSyncOptions>(GetSection("DataSourceSync"))` でバインド済み。
  → env `DataSourceSync__Enabled` / `DataSourceSync__IntervalSeconds`（ASP.NET の `__`→`:` 規約）で注入できる。
- `deployment.yaml` は `objectStorage` / `configVersion` / `pipelineSteps` と同型の **dedicated-toggle** で
  サービス毎に env を条件描画する（`extraEnv` も併存）。→ 同型の `dataSourceSync` ブロックを追加するのが素直。
- **fail-safe は既存実装で担保済み**（[IADR-0051]・`DataSourceSyncService`）:
  - 増分 watermark（`LastSyncedAt`）は**完全成功時のみ**前進（discover 失敗・一部 fetch 失敗では進めず次回再試行）。
  - 1 サイクルの例外で停止しない（次サイクルで回復）。実効間隔は最短 30 秒に丸める（過負荷防止）。
  - 未対応 SourceType・未構成ストレージは縮退（`ConnectorAvailable=false` / `NullObjectStorageClient`）。
  - 連続失敗 ≥3 で継続失敗アラート（構造化ログ `Alert=true`。UC-04 例外フロー）。
  - 重複発行（多重実行時）は決定的 DocumentId により下流が冪等 upsert（コード内コメントに明記）。
- `datasource` は `scaling.services` に含まれ、本番 HPA は minReplicas 2 → **2 pod が同時に sync ループを回す**。
  上記の冪等性により**安全（不整合を生まない）**だが、原本 fetch は**冗長**（二重取得）になる。
  → 単一書き手化（leader election）は [IADR-0074](../adr/IADR-0074_datasource-periodic-sync-helm-wiring.md) の「却下/先送り」に記録し、フォローアップ issue を別起票。
- `helm` は開発機に導入済み（v4.2.1）。CI には helm ジョブが無い（`helm template` は本 PR ではローカル検証に用い、
  CI 自動テストは C# の env→Options バインド契約で担保する）。

## 対象範囲

- 対象（変更）:
  - `deploy/helm/microservices-platform/values.yaml`: `services.datasource` に `dataSourceSync:{enabled,intervalSeconds}` を追加（本番=有効・300 秒）。
  - `deploy/helm/microservices-platform/templates/deployment.yaml`: `dataSourceSync` ブロックの env 描画（`DataSourceSync__Enabled` / `DataSourceSync__IntervalSeconds`）。
  - `deploy/local/values-local.yaml`: 経路B（ローカル k8s）で明示有効化＋間隔短縮（60 秒）。本番像 values.yaml は不変。
  - `docs/operations/operations.md`: 有効化手順・間隔根拠・監視（継続失敗アラート）との関係を追記。
  - `docs/adr/IADR-0074_datasource-periodic-sync-helm-wiring.md`（新規）＋ `docs/adr/README.md`（自分の 1 行のみ追記）。
  - `src/knowledge/backend/Services/DataSourceService/tests/.../DataSourceSyncOptionsBindingTests.cs`（新規）: env→Options バインド契約の回帰ガード。
- 対象外（本 PR で触らない）:
  - `deploy/docker-compose.yml`（compose は既定無効のまま。挙動不変。#275 ドリフト検査・images.yml に影響なし）。
  - `DataSourceSyncHostedService` / `DataSourceSyncService` などワーカー本体（既存で fail-safe 充足のため無改修）。
  - 実コネクタ・ファイル共有マウント・live なデータ疎通（別 issue / 稼働導入手順）。

## 設計判断（[IADR-0074](../adr/IADR-0074_datasource-periodic-sync-helm-wiring.md) 要約）

1. **配線方式**: `extraEnv` の生列挙ではなく専用 `dataSourceSync` ブロック（自己文書化・既存 dedicated-toggle と一貫）。
2. **本番間隔＝300 秒（5 分）の根拠**: 反映総遅延 = 検出遅延（≤ 間隔）＋ 下流パイプライン遅延（fetch→convert→ingest→index）。
   間隔 300 秒なら検出 ≤5 分・パイプライン予算 ≥10 分で **NFR 15 分に十分な余裕**。実効は最短 30 秒丸め（過負荷防止）。
   下流実測（#196）が未了のため、余裕を厚く取る保守値を採用（将来 #196 の実測で調整可）。
3. **経路B（ローカル k8s）で有効化**: 監査の検証は経路B（k3d、`values-local.yaml`）で行う。ここで定期同期が
   実際に回り「15 分以内反映」が成立する形にする。`scaling.enabled=false`＝replicas 1 で多重実行が起きず、
   検証環境として clean。反映を素早く確認できるよう間隔を 60 秒に短縮する（本番像は不変）。
   active データソース／ファイル共有が無い環境では sync 対象ゼロで**安全に空回り**（fail-safe。live 疎通は別手順）。
4. **多重実行**: 本番 HPA(2 pod) の二重 sync は冪等ゆえ安全（不整合なし）。冗長 fetch の排除（leader election）は
   本 PR スコープ外としフォローアップ issue（medium）へ切り出す。

## テスト・検証（受け入れ基準への写像）

- [ ] **本番 Helm values で定期同期が有効・間隔が NFR 根拠づけ** → `helm template`（本番 values）で
      datasource-service Deployment に `DataSourceSync__Enabled="true"` / `DataSourceSync__IntervalSeconds="300"` が描画される（下記「検証ログ」）。
- [ ] **compose / ローカル k8s(dev) の挙動整理** → compose は不変（無効）。経路B は監査検証のため
      **意図的に有効化**（本 PR の設計判断。issue 本文の「dev は無効のままでよい」は許容表現であり、
      経路B を検証手段に用いる本タスク指示を優先）。`helm template`（`-f values-local.yaml`）で `Enabled="true"` / `IntervalSeconds="60"` を確認。
- [ ] **運用仕様書に有効化・監視手順** → `docs/operations/operations.md` に追記（継続失敗アラート `Alert=true` との関係を明記）。
- [ ] **回帰ガード（CI・単体テスト）** → `DataSourceSyncOptionsBindingTests`: env `DataSourceSync__Enabled` /
      `DataSourceSync__IntervalSeconds` が `DataSourceSyncOptions`（`GetSection(SectionName)`）へ正しくバインドする契約を検証。
- [ ] `dotnet build` / `dotnet test`（knowledge backend）緑・`dotnet format --verify-no-changes` 緑。

## 検証ログ（実行結果は PR 本文／issue コメントに転記）

- `helm template msp deploy/helm/microservices-platform | <datasource-service Deployment 抽出>`
- `helm template msp deploy/helm/microservices-platform -f deploy/local/values-local.yaml | <同上>`
- `dotnet test src/knowledge/backend/Services/DataSourceService/tests/...`

## ロールアウト・ロールバック

- ロールアウト: GitOps（ArgoCD / CD）が本番 values で `DataSourceSync__Enabled=true` を適用 → datasource-service
  ロールアウトで定期同期開始。まず active データソースが登録済みの環境でのみ実 fetch が走る（未登録＝空回り）。
- ロールバック: `services.datasource.dataSourceSync.enabled=false`（`--set` もしくは values 差戻し）で即無効化（手動 /sync は不変）。

## フォローアップ（別 issue）

- 単一書き手化（leader election / sidecar / 専用 CronJob 化）で本番マルチレプリカ時の冗長 fetch を排除（medium）。
- 下流パイプライン実測（#196）に基づく間隔の最適化（low）。
