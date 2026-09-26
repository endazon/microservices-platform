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
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (FR-01 データソース同期 / NFR 15分以内反映)
  - planning:projects/microservices-platform/03_usecases/ (UC-04 定期取得・継続失敗アラート)
---

# IADR-0083: データソース定期同期の単一書き手化は PostgreSQL advisory lock で行う

- 状態: Accepted
- 日付: 2026-07-19
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: FR-01（データソース登録・同期）／UC-04（定期取得・継続失敗アラート）／NFR（「文書更新後 15 分以内に検索結果へ反映」）
- 関連 ADR: [IADR-0051](./IADR-0051_datasource-connector-port-and-filesystem.md)（定期同期ワーカー本体・増分 watermark の既存決定）／[IADR-0074](./IADR-0074_datasource-periodic-sync-helm-wiring.md)（Helm 配線・本番有効化。決定 4 で「多重実行は冪等ゆえ安全・単一書き手化はフォローアップへ」と先送り）
- 関連仕様書: `docs/specs/20260719_issue-305_datasource-sync-single-writer.md`
- Issue: #305（priority:should）／出所: [IADR-0074](./IADR-0074_datasource-periodic-sync-helm-wiring.md) 決定 4 のフォローアップ

## コンテキストと課題

`DataSourceSyncHostedService`（[IADR-0051](./IADR-0051_datasource-connector-port-and-filesystem.md)）は #299/PR #304（[IADR-0074](./IADR-0074_datasource-periodic-sync-helm-wiring.md)）で Helm に配線され本番で有効化された。
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

> **［2026-09-26 追記 / #1604］定期同期のループは、停止要求ではない取り消し（接続の時間切れ等）で終わらない。コネクタは明示の期限を持つ。**
> 本決定のワーカー（`DataSourceSyncHostedService`）は周期の本体を型だけの `catch (OperationCanceledException) { break; }` で包んでいた。
> `DataSourceSyncService` の探索と 1 件ごとの取得も `when (ex is not OperationCanceledException)` で型だけで絞っており、Wiki / SaaS コネクタの
> HttpClient は期限が未設定（既定 100 秒）だった。1 回の接続の時間切れ（`TaskCanceledException`）で定期同期が**ログも残さず永久に**止まる
> （プロセスは健全・手動の `/sync` は動く・データは失われない。#1601 の監査が読みで確認）。#1598 で見送った件である（同 PR の仕様書の「母集合」）。
>
> - ループは `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }` とし、停止要求の無い取り消しは続く
>   `catch (Exception)` が周期の失敗として記録して次の拍へ進む。拍を待つ間の停止要求は外側の `when (stoppingToken…)` で静かに終える
>   （IADR-0299 決定 3・IADR-0431 決定 5 の #1598 追記と同じ形）。
> - 探索と取得の捕捉は `when (ex is not OperationCanceledException || !ct.IsCancellationRequested)`。時間切れは**そのソース（その 1 件）の失敗**として数え、
>   watermark を進めない（手動の `/sync` でも同じ。要求の ct による打ち切りは従前どおり外へ出す）。
> - Wiki / SaaS の名前付きクライアント（`WikiConnector.HttpClientName` / `SaaSConnector.HttpClientName`）に `Timeout = 30 秒` を与える（1 要求ぶん。
>   SaaS の 429 の待ちは要求の外）。業務 DB（Npgsql）は接続文字列の既定の期限を持つので対象外。
> - 周期は構成から来て最短 30 秒に丸められ試験で待てないので、試験だけが与える口 `internal CycleInterval`（既定 null＝`StartSchedule()` の実効間隔）を足した。
>   SC-06 の「次回同期」の位相は常に構成の間隔で記録する（本番の挙動は変えない）。
> - 試験: `DataSourceSyncHostedServiceTests`（ループ・拍の待ち・コネクタの期限）、`DataSourceSyncServiceTests`（探索・取得の時間切れ・呼び出し側の取り消しの対照）。
>   変異（1 か所ずつ）: ループの捕捉を型だけへ戻す → 赤（10 秒の時間切れ）／失敗の後に待たずに再試行する → 赤（間隔の表明）／探索の絞り込みを戻す → 赤／
>   取得の絞り込みを戻す → 赤／コネクタの期限を外す → 赤。作業仕様書: `.ai-context/specs/20260926_issue-1604_refresher-and-sync-loop-timeouts.md`。

> **［2026-09-27 追記 / #1608］コネクタの 30 秒の期限は本文の転送まで含む。いつも 30 秒を超える 1 件が watermark を止め続けることは受容する。**
> Wiki / SaaS コネクタは本文を `GetAsync`（既定の `HttpCompletionOption.ResponseContentRead`）で引くので、応答は本文を読み切ってから返り、
> `HttpClient.Timeout` の 30 秒には**ヘッダの到着だけでなく本文のダウンロードも入る**（探索の一覧もページごとに同じ）。したがって転送がいつも
> 30 秒を超える項目が 1 つあると、その項目は毎周期「その 1 件の失敗」になり（上の #1604 追記）、`SyncResult.ShouldAdvanceWatermark` が偽のまま
> **そのソースの同期済みの位置（`LastSyncedAt`）は永久に進まない**。毎周期、止まった位置以降の全項目を探索し直して取得・発行し直し、警告を出す
> （#1604 の前も期限 100 秒で同じ性質だった）。
>
> - **受容する。** 理由: (1) UC-04 の例外フローは「接続失敗時は再試行し、継続失敗はアラートする」であり、挙動はそのとおりである —— 連続失敗が
>   `DataSourceSyncHealth.DefaultRetryLimit`（5 回）に達すると継続失敗アラートが出て、SC-06 に直近エラーが載る（**黙って止まらない**）。
>   (2) 他の項目の鮮度は止まらない —— 止まった位置以降に新規・更新された項目は毎周期探索され取得されるので、「15 分以内に反映」を妨げるのは遅い 1 件だけである。
>   (3) データは失われない。**遅い 1 件を飛ばして位置を進める形（項目ごとの隔離・隔離棚）は採らない** —— その項目は次に更新されるまで二度と取得されず、
>   「再試行する」を黙って破る（fail-safe の向きが逆になる）。
> - 受容する費用: 止まった位置以降に更新された項目の取得・発行が周期ごとに繰り返され、止まっている期間に比例して増える（下流は決定的 DocumentId で冪等に
>   upsert されるので不整合は生じないが、変換・索引の再処理は繰り返される）。**30 秒は構成のつまみを持たない**ので、解消は運用者がソース側で当該項目を
>   直す・移す操作になる。
> - 見直す条件: 単一項目の時間切れに起因する継続失敗アラートが実運用で観測されたとき（期限の構成化か、項目単位の位置の保持を IADR-0051 の改定として検討する）。
>   **計画への環流は要らない**と判断した（計画の定める「再試行＋継続失敗のアラート」は満たしており、変えるとすれば実装の位置の持ち方である）。
>   作業仕様書: `.ai-context/specs/20260927_issue-1608_purger-timeout-isolation.md`。

> **［2026-09-27 追記 / #1622］拍の待ちの試験を偽の時計で決定化し、呼び出し側の取り消しの対照を HttpClient の形で起こす。**
> 上の #1604 追記の「失敗の後に待たずに再試行する → 赤（間隔の表明）」は壁時計の間隔を測っており、負荷で本体が遅れると `PeriodicTimer` が溜まった拍をすぐ発火して正しい実装でも落ち得た。`DataSourceSyncHostedService` に試験だけの口 `internal CycleClock`（既定 `TimeProvider.System`）を `CycleInterval` の隣に足し、試験は `FakeTimeProvider` で拍を手で進める（起動時の 1 回目の後、失敗のたびに拍を進めるまで次の取得が来ないこと、k 回目の取得が見た偽の時刻が (k−1) 拍ぶんであること）。本番の挙動・SC-06 の位相は変えない。
> 探索・取得の呼び出し側の取り消しの対照は、素の `OperationCanceledException`（`ct.ThrowIfCancellationRequested()`）で起こしていたため、絞り込みを「`TaskCanceledException` なら時間切れ」と**型で**判定する変異が生き残り、取得の捕捉には対照そのものが無かった。対照を探索・取得の 2 件にし、コネクタの HttpClient が表す形（呼び出し側の token を持つ `TaskCanceledException`）で起こす。**時間切れと停止要求は型では分けられず、ct でしか分けられない**（本決定の捕捉が ct で絞るのはこのためである）。変異: M1 → 拍の試験が赤／探索・取得の捕捉へ `|| ex is TaskCanceledException` → それぞれの対照が赤（直す前の試験では両方とも緑）。作業仕様書: `.ai-context/specs/20260927_issue-1622_deterministic-tick-tests.md`。

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
