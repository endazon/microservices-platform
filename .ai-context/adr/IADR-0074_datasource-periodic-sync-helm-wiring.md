---
title: IADR-0074 データソース定期同期は Helm の専用 dataSourceSync ブロックで配線し、本番有効・経路B で検証する
type: impl-adr
status: Accepted
related_ids:
  - FR-01
  - UC-04
  - NFR
  - IADR-0051
  - IADR-0066
author: claude
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-01 データソース同期 / NFR 15分以内反映)
  - planning:projects/microservices-platform/03_usecases/ (UC-04 定期取得・継続失敗アラート)
---

# IADR-0074: データソース定期同期は Helm の専用 `dataSourceSync` ブロックで配線し、本番有効・経路B で検証する

- 状態: Accepted
- 日付: 2026-07-19
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-01（データソース登録・同期）／UC-04（定期取得・継続失敗アラート）／NFR（「文書更新後 15 分以内に検索結果へ反映」）
- 関連 ADR: [IADR-0051](./IADR-0051_datasource-connector-port-and-filesystem.md)（定期同期ワーカー本体・増分 watermark の既存決定。既定無効の出自）／[IADR-0066](./IADR-0066_local-k8s-dev-environment.md)（ローカル k8s dev 環境＝経路B。`values-local.yaml` の出自）
- 関連仕様書: `docs/specs/20260719_issue-299_datasource-periodic-sync-helm.md`
- Issue: #299（本 issue・High）／出所: 実環境構築前監査（2026-07-18・コミット `10d79e0`）

## コンテキストと課題

定期同期ワーカー `DataSourceSyncHostedService`（[IADR-0051](./IADR-0051_datasource-connector-port-and-filesystem.md)・#195）は実装済みだが **既定無効**
（`DataSourceSync:Enabled=false` で `ExecuteAsync` 冒頭 return）。本番は config で有効化する設計だが、
`deploy/helm/microservices-platform/values.yaml` を含む `deploy/` 配下に `DataSourceSync` 設定が**一切無い**。
このまま実環境を構築すると手動 `POST /datasources/{id}/sync` のみになり、**UC-04 の定期取得と NFR「15 分以内反映」が
満たせない**（監査の Go 条件に含まれる High ギャップ）。

配線には 4 つの論点がある：(1) Helm での env 注入方式、(2) 本番間隔の根拠、(3) 検証環境（経路B）での扱い、
(4) 本番マルチレプリカ（HPA minReplicas 2）での多重実行。

## 決定

### 1. 専用 `dataSourceSync` ブロックで env を条件描画する（`extraEnv` 生列挙は却下）

`values.yaml` の `services.datasource` に `dataSourceSync:{enabled, intervalSeconds}` を追加し、
`templates/deployment.yaml` が存在時のみ `DataSourceSync__Enabled` / `DataSourceSync__IntervalSeconds` を描画する。
これは既存の dedicated-toggle（`objectStorage` / `configVersion` / `pipelineSteps`）と一貫し自己文書化される。
env 名は ASP.NET の `__`→`:` 規約で `DataSourceSyncOptions`（`SectionName="DataSourceSync"`・`Program.cs` の
`GetSection` バインド）へ結線する。`extraEnv` に生の 2 行を書く案は、意味が values に現れず腐りやすいため却下。

### 2. 本番間隔＝300 秒（5 分）

反映総遅延 = 検出遅延（≤ 間隔）＋ 下流パイプライン遅延（fetch→convert→ingest→index）。間隔 300 秒なら
検出 ≤5 分・下流に ≥10 分の予算を残し、**NFR 15 分に十分な余裕**を持つ。実効間隔はワーカーが最短 30 秒へ
丸める（過負荷防止）。下流実測（#196）は未了のため、余裕を厚く取る保守値とし、実測後に調整可能とする。
既定 300（`DataSourceSyncOptions.IntervalSeconds` の既定）と一致させ、値の意外性を排除する。

### 3. 経路B（ローカル k8s / k3d）で定期同期を有効化して検証する

監査の Go 判定は経路B（[IADR-0066](./IADR-0066_local-k8s-dev-environment.md)・`values-local.yaml`）で行う。ここで定期同期が**実際に回り**「15 分以内
反映」が成立する形にする。`values-local.yaml` で `dataSourceSync.enabled=true` を明示し、反映を素早く確認できるよう
間隔を 60 秒へ短縮する（本番像 `values.yaml` は不変）。経路B は `scaling.enabled=false`＝replicas 1 で
**多重実行が起きず検証環境として clean**。active データソース／ファイル共有が無い状態では sync 対象ゼロで
**安全に空回り**する（fail-safe）。実データ疎通（実コネクタ・SMB/NFS マウント）は本 ADR のスコープ外の live 手順。

> issue 本文は「compose / ローカル k8s(dev) は既定無効のままでよい（挙動不変）」と**許容**しているが、
> 本タスクは経路B を検証手段に用いるため、経路B のみ意図的に有効化する。compose は無効のまま（挙動不変）。

### 4. 本番マルチレプリカの多重実行は「冪等ゆえ安全・冗長は許容」とし、単一書き手化は先送り

`datasource` は `scaling.services` に含まれ本番 HPA は minReplicas 2 → 2 pod が同時に sync ループを回す。
`DataSourceSyncService` は「成功済みファイルの再発行は決定的 DocumentId により下流が冪等 upsert する」ため
**多重実行でも不整合を生まない（安全）**。ただし原本 fetch は**冗長**（二重取得）になる。本 PR のスコープ
（Helm 配線）では冗長を許容し、単一書き手化（leader election / sidecar / 専用 CronJob 化）は**フォローアップ
issue（medium）**へ切り出す。

## 却下した代替案

- **`extraEnv` 生列挙**: 意味が values に現れず、他サービスの dedicated-toggle と不揃い。却下（決定 1）。
- **本番間隔を極短（例 60 秒）に**: fetch/イベント負荷が増え、下流実測前の過剰最適化。300 秒で NFR を余裕充足。却下。
- **経路B も既定無効のまま**: それでは「経路B で 15 分以内反映が成立」を検証できない。却下（決定 3）。
- **本 PR で leader election を実装**: スコープ（Helm 配線）を超え、冪等性で安全は担保済み。フォローアップへ（決定 4）。

## 影響・結果

- 良い影響: 実環境（GitOps 適用）で定期同期が回り UC-04・NFR 15 分反映の配線が成立。経路B で検証可能。
  fail-safe（watermark 前進条件・縮退・継続失敗アラート・下流冪等）は既存実装で担保され無改修。
- トレードオフ: 本番 2 pod で冗長 fetch が発生（安全だが無駄）。→ フォローアップ issue。
- 後方互換: compose・既定値・手動 /sync は不変。`dataSourceSync` 未設定サービスは env 非描画で従来通り。
- 検証: `helm template`（本番／経路B）で env 描画を確認。C# 単体テストで env→Options バインド契約を回帰ガード。
