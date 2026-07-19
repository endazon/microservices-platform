---
title: IADR-0083 データソース定期同期の単一書き手化は PostgreSQL advisory lock で行う
type: impl-adr
status: Accepted
related_ids:
  - FR-01
  - UC-04
  - NFR
  - IADR-0051
  - IADR-0074
author: claude
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (FR-01 データソース同期 / NFR 15分以内反映)"
  - "../../planning/projects/microservices-platform/03_usecases/ (UC-04 定期取得・継続失敗アラート)"
---

# IADR-0083: データソース定期同期の単一書き手化は PostgreSQL advisory lock で行う

- 状態: Accepted
- 日付: 2026-07-19
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-01（データソース登録・同期）／UC-04（定期取得・継続失敗アラート）／NFR（「文書更新後 15 分以内に検索結果へ反映」）
- 関連 ADR: [[IADR-0051]]（定期同期ワーカー本体・増分 watermark の既存決定）／[[IADR-0074]]（Helm 配線・本番有効化。決定 4 で「多重実行は冪等ゆえ安全・単一書き手化はフォローアップへ」と先送り）
- 関連仕様書: `docs/specs/20260719_issue-305_datasource-sync-single-writer.md`
- Issue: #305（priority:should）／出所: [[IADR-0074]] 決定 4 のフォローアップ

## コンテキストと課題

`DataSourceSyncHostedService`（[[IADR-0051]]）は #299/PR #304（[[IADR-0074]]）で Helm に配線され本番で有効化された。
`datasource` は本番 HPA で `minReplicas: 2`（`scaling.services`）のため、**2 pod が同時に定期同期ループを回す**。

`DataSourceSyncService` は成功済みファイルの再発行が決定的 DocumentId により下流で冪等 upsert されるため
**不整合は生じない（安全）**。ただし原本 fetch とイベント発行が**レプリカ数ぶん冗長**になり、コネクタ先
（ファイル共有等）・下流パイプラインに無駄な負荷がかかる。定期同期の実行を**単一書き手**に限定し、
API サービスの可用性（minReplicas 2 / PDB）は維持したまま、レプリカ数に依存せず 1 サイクル 1 回にしたい。

論点は 2 つ: (1) 排他の実現手段、(2) 取得できないときの安全側挙動（fail-safe）と後方互換。

## 決定

### 1. PostgreSQL セッションレベル advisory lock（`pg_try_advisory_lock`）で単一書き手化する

各同期サイクルの実行前に、**専用 `NpgsqlConnection`** を開いて `pg_try_advisory_lock(<固定キー>)` を試行する。
取得できたレプリカのみが `SyncAllActiveAsync` を実行し、`finally` で `pg_advisory_unlock` + 接続破棄する。
固定キーは全レプリカで一致する 64bit 定数（`"DSPS"` = DataSource Periodic Sync の 4 バイト = `0x44535053`）。

- **非ブロッキング**: `pg_try_advisory_lock` は即座に true/false を返す。取得できなければ待たずにスキップできる。
- **セッションスコープ・自動解放**: ロックは接続（セッション）に紐づく。pod crash や接続断でセッションが
  終了すれば Postgres が**自動解放**するため、デッドロック（リース保持者が死んで永久ロック）が起きない。
- **単一書き手の抽象化**: `ISyncLeaseCoordinator.TryAcquireAsync` が「取得できたら破棄可能ハンドル、
  できなければ null」を返す。ワーカーは `null` を受けたら本サイクルをスキップする。実装差し替え可能にし、
  非リレーショナル環境（後述）と単体テストを分離する。

### 2. 取得不可・障害は安全側でスキップし次周期へ（fail-safe）

- 他レプリカがロックを保持中（`pg_try_advisory_lock` = false）→ 本サイクルはスキップ。次周期で再試行する。
  次周期には前回のロックは解放済み（各サイクルで取得→解放するため）で、いずれかのレプリカが必ず実行する。
- ロック取得中の**一時的障害（接続不能等）** → 例外を握り、`null` を返して本サイクルをスキップする
  （実行を強行しない安全側）。datasource は Postgres 必須のため、接続不能時は同期本体も失敗する状況であり、
  スキップは実害を増やさない。継続失敗アラート（UC-04 例外フロー）は同期本体側の既存機構で担保される。

### 3. 単一レプリカ（経路B）・非リレーショナルは従来どおり動く（後方互換）

- **単一レプリカ（経路B・`scaling.enabled=false`＝replicas 1）**: 競合が無いため常に取得でき、
  **従来どおり毎サイクル実行**する。挙動不変。
- **非リレーショナル DB**（単体テストの InMemory 等）: advisory lock は Postgres 固有機能のため使えない。
  `Program.cs` は DB プロバイダを判定し、非リレーショナルでは**常時取得の `NoOpSyncLeaseCoordinator`** を
  登録する（＝従来どおり実行）。本番（Npgsql）では `PostgresAdvisoryLockLeaseCoordinator` を登録する。
- 既定挙動・手動 `POST /datasources/{id}/sync`・watermark 前進条件・継続失敗アラートは**すべて不変**。
  `DataSourceSync:Enabled=false`（既定）ならワーカー自体が起動せず、単一書き手化のコードは一切動かない。

### 4. helm / infra は不変（コード側で完結）

単一書き手化はアプリ内の advisory lock で完結し、**Helm values・テンプレート・infra manifest・RBAC を変更しない**。
これにより #328（Headlamp OIDC・`k8s-local-up.sh`）や他サービスの values ブロックと領域が完全に分離し、
CI（#275 ドリフト・images.yml）を壊さない。`scaling.services`（minReplicas 2）・PDB も不変で API 可用性は維持される。

## 却下した代替案

- **k8s `Lease`（coordination.k8s.io）リーダー選出**: k8s ネイティブだが、k8s クライアント依存・
  RBAC（Role/RoleBinding for leases）・ServiceAccount を要し、**datasource-service のコード＋values ブロックの
  スコープを超えて infra/helm テンプレートに波及**する。本タスクの領域分離制約（#328 と非干渉）と相容れない。却下。
- **専用 Deployment / sidecar（replicas 1・非 HPA）にワーカーを分離**: infra manifest・イメージ起動構成の
  変更を伴い、API と別プロセスの運用が増える。既存の単一イメージ構成を崩す。スコープ超過で却下。
- **k8s `CronJob` 化**: 同上（新規 manifest・スケジュール二重管理）。既存の in-process ワーカー（PeriodicTimer）
  設計を破棄する規模のため却下。
- **DB 行ベースのリーダーテーブル（advisory lock でなく明示行 + TTL）**: TTL・ハートビート・期限切れ回収の
  自前実装が必要でバグ余地が増える。advisory lock はセッション終了で自動解放され、その複雑性が不要。却下。
- **単一書き手化を無効化するトグルを追加**: 既に `dataSourceSync.enabled=false` で定期同期ごと停止でき、
  advisory lock 経路が不安な場合の退避手段は存在する。単一書き手を選択制にすると #305 が解こうとする冗長 fetch を
  既定で温存する矛盾が生じるため、専用トグルは追加しない。

## トレードオフ・注意

- **トランザクションプーラ非対応**: セッションレベル advisory lock は PgBouncer 等の**トランザクションプーリング**
  では正しく機能しない（別トランザクションで別接続に割当てられ得る）。本サービスは `postgres:5432` へ**直接接続**し、
  かつ advisory lock 用に**専用接続**を張るため現状は問題ない。将来プーラを挟む場合は
  トランザクションレベル（`pg_advisory_xact_lock`）へ切替が必要。この前提を本 ADR に明記する。
- **粒度**: ロックはサービス全体（全 active データソース）で 1 つ。データソース単位の並列同期は将来最適化余地
  （キー第 2 引数にデータソース ID を用いる等）。現状は「1 サイクル＝1 レプリカが逐次同期」で十分。
- **接続コスト**: サイクルごとに短命接続を 1 本張る（既定 300 秒間隔で軽微）。

## 影響・結果

- 良い影響: 本番マルチレプリカで 1 サイクルの原本 fetch が 1 回になり、コネクタ先・下流の冗長負荷が解消される。
  API 可用性（minReplicas 2 / PDB）は不変。fail-safe・後方互換を保つ。
- トレードオフ: プーラ非対応の注意（上記）。サイクルごとの短命接続 1 本。
- 後方互換: 単一レプリカ・非リレーショナル・既定無効・手動 /sync・watermark・継続失敗アラートは不変。
  Helm/infra 無改修（#328 と非干渉）。
- 検証: C# 単体テストで（a）NoOp コーディネータ常時取得、（b）advisory lock コーディネータの接続不能時 fail-safe、
  （c）ワーカーがリース取得時のみ同期・取得不可時はスキップ、を回帰ガードする。さらに（d）実 PostgreSQL コンテナ
  （Testcontainers・`DataSourceSyncSingleWriterTests`）で単一書き手化の核心＝「2 レプリカが競合しても同時刻に 1 つのみ
  取得成功し、解放後は別レプリカが取得できる（liveness）」を統合テストで自動検証する（Docker 不在時はスキップ・CI で実行）。
  実マルチレプリカでのエンドツーエンド疎通のみ live 手順に残す。
