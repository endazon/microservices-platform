---
title: 作業仕様書 — ingestion-service に health を張り、ingestion・conversion の probe を描く（#2140）
type: spec
status: done
related_ids: [NFR-21, NFR-06, ADR-0052, IADR-0602, IADR-0536, IADR-0540]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（NFR-21・NFR-06）
  - planning:projects/microservices-platform/07_adr/ADR-0052_wolverine-migration-completion-criteria.md（決定 4）
---

# 仕様書: ingestion-service に health を張り、ingestion・conversion の probe を描く（#2140。親 #2047 指摘 L-11）

## 起点となる計画書（トレーサビリティ）

- 非機能要件: NFR-21（障害検出〜復旧。検出 5 分以内・MTTR 30 分以内）、NFR-06（障害時の縮退運転）
- 関連 ADR: ADR-0052 決定 4（readiness に載せる依存は「そのサービスが依存するもの」に限る）
- 実装 ADR: IADR-0602（readiness は自プロセスの必須依存だけ。下流の疎通は入れない。#2164 で develop へ）、
  IADR-0536（埋め込みが使えない間の全文だけの点＝取り込みは LLM ゲートウェイ停止中も縮退して進む）、IADR-0540（本文の欠落の計数）

## 目的・背景

`IngestionService/Program.cs` は `MapPlatformHealthChecks` を呼ばず（gRPC リスナの注記だけが「/health/* は残る」としていた）、
chart も probe を描かなかった。`ConversionService` は health を Map しているのに、chart の `worker: true` が probe を抑止しており、
values のコメント（「バックグラウンドワーカーは /health/* を持たず」）は実態と違った。どちらも落ちても k8s が気づかない ——
NFR-21 の「検出 5 分以内」を支える手段（自己修復・NotReady の可視化）が両ワーカーに無かった。

## 決定

1. **ingestion-service に共通の health を張る**（`AddPlatformHealthChecks` ＋ `MapPlatformHealthChecks`。他サービスと同じ `/health/live`・`/health/ready`、8080）。
   readiness（`ready` タグ）に載せるのは**自プロセスの必須依存だけ**（IADR-0602）:
   - ブローカ: Wolverine（`AddPlatformWolverineBroker`＝DocumentUpdated の購読）と MassTransit（`masstransit-bus`＝IngestionCompleted の発行。`AddMassTransit` が暗黙に登録）。
   - 自前のデータストア: Qdrant（`QdrantHealthCheck`。取り込みが実際に使う**同じ gRPC クライアント**の `HealthAsync`。期限 5 秒・失敗は Unhealthy）。
   - **載せないもの**: LLM ゲートウェイ（下流のサービス。停止中も取り込みは全文だけの点で縮退して進む＝IADR-0536。載せると NFR-06 と逆向き）、
     オブジェクトストレージ（本文の欠落は 1 通ごとの失敗・計数で現れる＝IADR-0540。検索サービスも readiness に載せていない）。
   - Qdrant を REST（6333 `/healthz`）の URL チェックにしなかった理由: 取り込みが使うのは gRPC（`Qdrant:Port`、既定 6334）であり、
     別ポートを決め打ちで問うと実際の経路が切れていても緑になり得る。パッケージ（`AspNetCore.HealthChecks.Uris`）の追加も要らない。
2. **chart の `worker` 旗を撤去する**。旗の唯一の効果は probe の抑止だった（`git grep '\.worker'` で template 1 箇所だけ）。
   両ワーカーとも health を持つようになった以上、旗は意味を持たない。probe は **HTTP のポート（`port`）を持つ全サービスに描く**。
   HPA の対象外であること（IADR-0050）は `scaling.services` のリストが決めており、旗とは独立（変更なし）。
   probe のパス・ポート・間隔は他サービスと同一（`/health/live`・`/health/ready`、`$svc.port`＝8080、初期待ち 10 / 15 秒・周期 15 秒）。
   template の注記は描画物へ出さない（`{{- /* */}}`。全サービスに同じ注記が並ぶだけになるため）。
3. **IADR は起こさない**。readiness の構成は IADR-0602 の原則の適用であり、旗の撤去は実態（両者とも health を持つ）への追随である。

## 母集合（規則 9。誤りの側の文字列で走査）

- `git grep -n 'worker'` を chart（`deploy/helm/microservices-platform/`）で: values の `worker: true` 2 件・その説明のコメント 2 件・
  scaling の注記 1 件（`conversion/ingestion=worker:true`）・template の条件と注記 1 件。すべて是正。
- `git grep -n 'worker: true'`（`.ai-context/` を除く）: `docs/operations/operations.md` の HPA 適用対象の表 1 件。是正（あわせて probe の節にワーカーも probe を持つと明記）。
- 他の「worker」で probe を抑止されていたサービス: 無し（values の `worker:` は 2 件だけ）。
- compose（`deploy/docker-compose.yml`）: ingestion・conversion に healthcheck は無く、実行イメージは curl / wget を持たない（他サービスも同じ）。対象外。
- `.ai-context/` の凍結記録（IADR-0029・IADR-0050 等の「worker は /health/* を持たない」旨）: 書き換えない。

## 受け入れ基準と試験への写像

| # | 基準 | 写像先 |
| --- | --- | --- |
| 1 | `helm template`（既定・values-local）で ingestion・conversion に liveness `/health/live`・readiness `/health/ready` がコンテナの HTTP ポートへ描かれ、HTTP ポートを持つ全 Deployment が probe を持つ。旗 `worker` が chart に残らない | `scripts/helm-worker-probes.test.js`（新規・CI の static-checks-units） |
| 2 | ingestion の `/health/live` は依存がすべて落ちていても 200。`/health/ready` は自前の依存（Wolverine・MassTransit・Qdrant）で決まり、どれか 1 つが落ちれば 503。ready に載るのはその 3 つだけ。Qdrant に届かなければ Unhealthy | `IngestionService.Tests/HealthEndpointTests.cs`（新規 7 件） |
| 3 | 変異で赤: (a) 描画物から ingestion の probe を外す、(b) 抑止の条件 `not $svc.worker` を template へ戻して `worker=true`、(c) Program.cs から `MapPlatformHealthChecks()` を外す（JS の判定）、(d) Map を外す・Qdrant を ready から外す（C# 試験） | (a)〜(c) は同 JS 試験内、(d) は手元で実測（下記） |

## 検証

- `node scripts/helm-worker-probes.test.js`: 6 passed（helm v3.16.4）。
- `dotnet test Services/IngestionService/Tests/IngestionService.Tests.csproj`: 247 passed。
- 変異 (d) 実測: Map を外す → HealthEndpointTests 5 件赤（live・ready 全件）。Qdrant の `ready` タグを外す → 2 件赤（登録の集合・Qdrant 停止で 503）。

## リスク・PoC での確認点

- **probe の追加で起動が遅いと再起動ループになり得る**: liveness は初期待ち 10 秒＋周期 15 秒×失敗 3 回（既定）≒ 55 秒でリスナが立たなければ再起動する。
  conversion は起動時に `MigrateAsync`（DB）を同期で待ち、ingestion は Qdrant のブートストラップ（`StartAsync`。失敗しても起動は止めない）を待つ。
  他の DB を持つサービスも同じ probe で同じ形の起動をしているので同型だが、PoC で両 Pod の `RESTARTS` が増えないことを確かめる。
- readiness が Qdrant・ブローカの停止で NotReady になると Service の endpoints から外れる（ingestion の gRPC 面＝索引の状態の照会が BFF から届かなくなる）。
  その間も Qdrant が無ければ照会はどのみち失敗するので、外すことは実害を増やさない。キューの消費は readiness と無関係に続く（probe は Pod の再起動を liveness だけが決める）。
