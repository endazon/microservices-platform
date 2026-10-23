---
title: 作業仕様書 — BFF と AI 分析の readiness から下流サービスの疎通を外す（#2105）
type: spec
status: done
related_ids: [NFR-06, ADR-0032, ADR-0052, IADR-0602, IADR-0522]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（NFR-06）
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md
  - planning:projects/microservices-platform/07_adr/ADR-0052_wolverine-migration-completion-criteria.md（決定 4）
---

# 仕様書: BFF と AI 分析の readiness から下流サービスの疎通を外す（#2105。親 #2047 指摘 M-8）

## 起点となる計画書（トレーサビリティ）

- 非機能要件: NFR-06（可用性・障害時の縮退運転「検索は継続、AI 回答のみ一時停止を許容」/ 実現手段「LLM 依存部の分離」）
- 関連 ADR: ADR-0032（BFF セッション方式。セッションストアは Redis〔現 Valkey〕で「単一障害点となる」と明記）、
  ADR-0052 決定 4（readiness に載せる依存は「そのサービスが依存するもの」に限る。無関係なサービスを落とすと readiness の意味が逆向きに壊れる）
- 実装 ADR: IADR-0602（本件の決定）、IADR-0522（セッションストアのヘルスチェックの構成）

## 目的・背景

`Platform.Bff/Program.cs` の readiness（`ready` タグ）が、セッションストアに加えて下流 4 サービス（retrieval・aianalysis・
feedback・dashboard）の `/health/live` を含んでいた。下流 1 つの停止で BFF の全レプリカが NotReady になり、エッジから全画面が 503 になる。
AiAnalysisService の readiness も retrieval・llm-gateway を含み、LLM ゲートウェイの停止 → AI 分析 NotReady → BFF の aianalysis 検査が赤 → 全画面 503 と連鎖する。
NFR-06 の「検索は継続し AI 回答のみ止める」と逆向きである。

## 対象範囲

- 対象: `Platform.Bff/Program.cs`、`AiAnalysisService/Program.cs`、共有 `HealthCheckExtensions`（タグ定数と報告口 `/health/dependencies` の opt-in ヘルパ）、
  helm values・compose・values-local のコメント、`docs/operations/operations.md` の probe 節、試験。
- 対象外: helm の probe パス（`/health/live`・`/health/ready` のまま。変更しない）、他サービスの readiness（下の母集合で該当なし）、
  startupProbe の新設（下流を startup に移すと、下流停止中に BFF を再起動できなくなり同じ連鎖を起動時に持ち込むため採らない）。

## 母集合（規則 9。誤りの側の文字列で走査）

- `git grep -n '"ready"' -- 'src/*.cs'`（AST submodule と試験を除く）: 該当 18 行。下流**サービス**を readiness に載せていたのは
  BFF（4 件）と AiAnalysisService（2 件）だけ。他は自分の PostgreSQL（`AddNpgSql`）・ブローカ（`AddPlatformWolverineBroker`）・
  RetrievalService の Qdrant と索引（自分のデータストア）・ConversionService の pandoc / pdftotext（自プロセスの道具）で、いずれも自プロセス必須の依存として残す。
- `git grep -n 'UriHealthCheck\|readiness の .*/health/live'`（記録・仕様書を除く）: values.yaml 2 箇所・compose 1 箇所が
  「readiness が読む」と書いており追随する。values-local.yaml の #1389 の注記（「BFF の readiness が落ちる」）も症状が変わるので追随する。
  「readiness の UriHealthCheck には含めない」系の注記（BFF の通知・Wiki・AST 3 サービス、deploy/local の 2 箇所）は本変更後も真なので触らない。

## 設計

1. 共有 `HealthCheckExtensions` に `ReadyTag`（`ready`）と `DependenciesTag`（`dependencies`）を定数として置き、
   `MapPlatformDependencyHealthChecks()`（opt-in）で `/health/dependencies` を張る。`dependencies` タグだけを拾い、**常に 200**、
   本文は `{"status": …, "entries": {名前: 状態}}`（例外文言は出さない）。既存の `MapPlatformHealthChecks` の挙動は変えない。
2. BFF: セッションストア（`AddRedis`）は `ready` に残す。下流 4 件は `dependencies` タグ・`failureStatus: Degraded` へ移す。
3. AiAnalysisService: retrieval・llm-gateway を `dependencies` タグ・`failureStatus: Degraded` へ移す。readiness に載る依存は 0 件になる（自前のデータストアを持たない）。
4. 判断（Redis を残す）は IADR-0602 に記録する。

## 受け入れ基準

- [x] 1. 下流（aianalysis ほか 4 つ全部）の liveness が失敗しても BFF の `/health/ready` が 200。検索（`/bff/search`）も 200。
- [x] 2. 下流の状態は `/health/dependencies` で下流ごとに観測できる（BFF・AI 分析とも）。
- [x] 3. AiAnalysisService の readiness が retrieval・llm-gateway の停止で落ちない。
- [x] 4. BFF のセッションストアが落ちたら readiness は 503。
- [x] 変異: 下流を `ready` タグへ戻すと 1 / 3 が赤、セッションストアを `ready` から外すと 4 が赤。

## テスト方針

- `Platform.Bff.Tests/BffReadinessIsolationTests.cs` / `AiAnalysisService.Tests/ReadinessIsolationTests.cs`。
  本番の配線（タグ・failureStatus）のまま、`HealthCheckServiceOptions` の登録の **Factory だけ**を固定結果へ差し替えて実 HTTP で測る。
  readiness の試験は下流を Unhealthy で落とす（failureStatus を Degraded のまま `ready` へ戻す変異も Degraded=200 に紛れないため）。
  報告口の試験は登録上の failureStatus（本番で接続に失敗したときの値）で測る。

## 計画書との差異

- 差異: なし（NFR-06・ADR-0052 決定 4 に沿う向きへ直す。ADR-0032 の「Redis は単一障害点」は readiness に Redis を残すことで表に出る）。

## 未決事項

- なし（Redis を readiness に残す判断は IADR-0602 決定 2。将来、無人の主体〔Bearer〕の経路を Redis 停止中も通したい要求が出れば見直す）。
