---
title: 作業仕様書 — IngestionCompleted の発行を Wolverine へ移し、IngestionService から MassTransit を外す（移行単位 E4。#2134）
type: spec
status: done
related_ids: [FR-02, ADR-0027, ADR-0030, IADR-0234, IADR-0314]
author: claude
created: 2026-10-25
updated: 2026-10-25
plan_refs:
  - planning:projects/microservices-platform/10_feedback/20261009_audit-b12-b13-dept-sync-ingestion.md（§裁定 項目 6「結線しない」・§残るもの「E4 で Wolverine へ移すか撤去するかは実装が決める」）
  - planning:projects/microservices-platform/04_workflows/01_ingestion-pipeline.md（`Ing-->>MQ: IngestionCompleted`＝発行だけ）
  - planning:projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md
---

# 仕様書: IngestionCompleted の発行を Wolverine へ移す（#2134）

## 起点となる計画書（トレーサビリティ）

- 要求: FR-02（取り込み）。親: #2047（指摘 L-1）
- 計画: ADR-0027（Wolverine 採用）・ADR-0030（MassTransit 不採用。`scripts/backend-library-baseline.json` の ratchet）。
  planning#741 項目 6 の裁定（結線しない。E4 の中身は実装が決める）。計画のワークフロー図は発行だけを描く（`Ing-->>MQ: IngestionCompleted`）。
- 実装 ADR: IADR-0234（移行単位 E4。本 PR で完了する。日付つき追記を足す）・IADR-0314（購読 0 件）

## 選んだ形と理由（オーケストレーターの判断 2026-10-25）

**(B) 発行を Wolverine へ移す**（撤去しない・契約の型を残す）。

- 撤去（A）は計画のワークフロー図（Ing → MQ）と食い違い、契約の型まで消す（A'）と計画 10_composability-design §3（削除は新版＋移行期間）と
  契約の破壊の承認（`contract-breaking-allowlist.json` の `approvedBy`）に触れる。(B) はどちらにも触れずに ratchet を下げられる。
- 費用は「購読者の居ないイベントを出し続ける」ことである（fan-out exchange に束ねたキューが無いので、ブローカが捨てる）。

### 🔴 発行は非耐久である（残余リスク）

- IngestionService は DB を持たないので、Transactional Outbox も耐久送出（`UsePlatformTransactionalOutbox`）も使えない。
  発行は Wolverine の既定（送出先は非耐久・メモリ上に保持）で出る。送出の前にプロセスが落ちると、その `IngestionCompleted` は失われる。
- 購読者が 0 件なので、いま失われて困る受け手は居ない（取り込みの結果は Qdrant の点が正本である。負荷試験 P-03 は Qdrant の点の数でも測れる）。
- **購読者が付く時点で、耐久化（Outbox または durable な送出）を再検討する。** 付ける PR は、本節を引いて耐久化の要否を決めること。
  IADR-0234 の E4 の追記にも同じ条件を残す。

## 母集合（規則 9）

`git grep -n "IngestionCompleted"`（`src/ai-stock-trading` と凍結記録を除く）と、`git grep -n "MassTransit"` のうち取り込みに関わるもの、
`MassTransit のまま|両スタック|masstransit-bus` で引いた。

| 箇所 | 現状 | 処置 |
| --- | --- | --- |
| `IngestionService/Infrastructure/Messaging/MassTransitIngestionCompletedPublisher.cs` | MT の発行口 | **消す**。`WolverineIngestionCompletedPublisher`（`IMessageBus.PublishAsync`）へ置き換える |
| `IngestionService/Program.cs` | `AddMassTransit`・MT の発行口の登録・注記 4 か所 | MT を外し、Wolverine の発行口を登録し、`RoutePlatformEvent<IngestionCompleted>()` で exchange へ出す。注記を直す |
| `IngestionService/IngestionService.csproj`・`Tests/IngestionService.Tests.csproj` | `MassTransit.RabbitMQ` | **外す**（ratchet の 2 行） |
| `scripts/backend-library-baseline.json` | 上の 2 行 | **外す**（9 → 7 行。`check-backend-libraries.js` が「消えたのに残っていれば fail」） |
| `scripts/event-topology-baseline.json` | `IngestionCompleted` の発行元 `masstransit` | `check-event-topology.js --update` で再生成（前進 MT → Wolverine。手では直さない） |
| `IngestionService/Domain/Ports/IIngestionCompletedPublisher.cs`・`Features/Ingestion/Ingest/DocumentUpdatedConsumer.cs` | 注記「MassTransit のまま」 | 直す |
| `IngestionService/Tests/HealthEndpointTests.cs` | readiness の期待に `masstransit-bus` | 外す（MT を外すと暗黙の登録も消える。DataSourceService の E1 と同じ） |
| `IngestionService/Tests/IntrospectionEndpointTests.cs`・`GlobalUsings.cs` | MT のテストハーネス・`global using MassTransit` | 外す |
| `docs/tech/tech-requirements.md:58` | 「MassTransit が残るのは … `IngestionCompleted` の発行 … だけ」 | `DocumentNormalized` だけにする（`docs/` の trace ブロックの規約に従う） |
| `.ai-context/adr/IADR-0234_…md` | E4 の追記（2026-10-07・10-09） | `［2026-10-25 追記 / #2134］` を足す（E4 の完了・非耐久の条件） |

**変えないもの（理由つき）**:

| 箇所 | 理由 |
| --- | --- |
| `Knowledge.Contracts/Events/IngestionCompleted.cs`・`scripts/contract-schema-baseline.json` | 契約は変えない（(B) の前提） |
| `deploy/helm/.../pipeline.json`（`outputs: ["IngestionCompleted"]`） | 段の出力の宣言。発行は続くので正しい |
| `docs/functional/FR-01_*`・`FR-02_*`・`docs/tech/composability-classification.md:54,129` | 「発行する・購読者は無い」は (B) でも正しい（トランスポートを書いていない） |
| `docs/tests/NFR-01_performance-load-test.md` P-03・`perf/k6/README.md` | 計測点は変えない（(B) では不要。オーケストレーターの条件） |
| `docs/tests/FR-02_ingestion.md` T-06 | 観点（取り込み後に発行される・件数 > 0）は変わらない。ポートの試験はそのまま残し、Wolverine の経路の試験を足す（下） |
| ConversionService / DocumentService の「MassTransit のまま」 | 辺 `DocumentNormalized`（単位 E2）の射程 |
| `deploy/prometheus/alerts.yml:353`・`docs/operations/operations.md:1483` の「MassTransit に残る辺」 | 例示は `DocumentNormalized` であり、本件に触れない |

## 受け入れ基準

- [x] AC1: IngestionService と IngestionService.Tests から `MassTransit` の参照が無くなり、`backend-library-baseline.json` が 2 行減る（9 → 7）。`check-backend-libraries.js` が緑。
- [x] AC2: T-06 の観点を落とさない —— 取り込み完了で発行口が呼ばれる（`Consumer_ShouldPublishIngestionCompleted`。ポートの試験は変えない）。
- [x] AC3: Wolverine の発行口は、受け取った文書 ID・チャンク数・完了時刻をそのまま運ぶ `IngestionCompleted` を `IMessageBus` へ 1 件発行する（単体）。
- [x] AC4: 本番の配線（`Program.cs`）で、`IngestionCompleted` は RabbitMQ の exchange（`IngestionCompleted`。`PlatformExchangeName`）へ経路が引かれ、ローカルのキューへは流れない
      （`PreviewSubscriptions`。`RoutePlatformEvent` を外すと赤）。
- [x] AC5: readiness は Wolverine のブローカと Qdrant の 2 つだけになる（`masstransit-bus` が消える）。
- [x] AC6: `event-topology-baseline.json` をツールで再生成し、`check-event-topology.js` が緑（発行元のトランスポートが `wolverine`）。
- [x] 検証: IngestionService の全試験・`dotnet format --verify-no-changes`・文書系検査・`scripts/scripts.test.js`。

## PoC の事前手順

- **不要**（スキーマ・設定の変更なし）。MassTransit が作っていた exchange（`Knowledge.Contracts.Events:IngestionCompleted`）は残るが、
  束ねたキューも購読者も無いので害はない。Wolverine は別の exchange（`IngestionCompleted`）を自動で作る（`AutoProvision`）。
