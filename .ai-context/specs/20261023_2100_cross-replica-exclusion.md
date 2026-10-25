---
title: レプリカをまたぐ排他 —— Wiki.js への書き込みの門と個人資料の定期処理（#2100）
type: spec
status: done
related_ids: [FR-13, FR-19, FR-20, FR-22, UC-07, NFR-05, ADR-0011, ADR-0027, ADR-0096, IADR-0050, IADR-0299, IADR-0563, IADR-0599]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-13・FR-19・NFR-05）
issue: "#2100"
---

# 仕様書: レプリカをまたぐ排他（#2100。親 #2047 の指摘 M-3）

## 起点となる計画書（トレーサビリティ）

- FR-13・UC-07（Wiki 同期）、FR-19・FR-20・FR-22（個人資料の定期処理）、NFR-05（可用性 99.9%）。
- 計画 ADR: ADR-0011（Wiki.js）・ADR-0027（Wolverine・再試行・DLQ）・ADR-0096（退職者の資料）。いずれも変えない。
- 実装判断の記録: [IADR-0599](../adr/IADR-0599_cross-replica-wiki-write-gate-and-maintenance-lease.md)。改める: [IADR-0563](../adr/IADR-0563_wiki-js-write-gate.md) 決定 4・残余リスク (a)。前例: [IADR-0299](../adr/IADR-0299_knowledge-health-producer.md) 決定 3。

## 現状の確認（origin/develop `e51fcc74`）

- `deploy/helm/microservices-platform/values.yaml` の `scaling.services` に document・wiki。`hpa.minReplicas: 2`・`maxReplicas: 4`。HPA の対象は Deployment の静的 `replicas` を出さない。
- `WikiJsWriteGate` は `SemaphoreSlim`（プロセスの中だけ）。IADR-0563 残余リスク (a) は「上げるときは本 IADR を改める」。
- `PrivateNoteMaintenanceHostedService` にリースが無い。GraphService は `PostgresKnowledgeHealthLeaseCoordinator` 等で単一実行（IADR-0299）。
- `HpaPdbScalingTests` は wiki を HPA の対象に固定し、IADR-0563 の前提と矛盾したまま緑。

## 計画の可用性要件の確認

- `git show origin/main:projects/microservices-platform/02_requirements/01_requirements.md` を grep: NFR-05「サービス可用性 99.9% 以上」。wiki 固有の HA の記述は無い。
- wiki-service は Wiki 閲覧・検索（SC-04・UC-07）の要求処理系でもある。IADR-0050 は可用性のために要求処理系を minReplicas 2 ＋ PDB にした。
- → **(a1) を採る**（門をレプリカ間で効かせる）。(a2)（replicas 1）は閲覧の可用性を落とし、かつローリング更新の maxSurge の窓が残る。比較は IADR-0599。

## 母集合（規則 9: 誤りの側の文字列で走査）

`git grep -n -e "門はプロセスの中" -e "プロセスに 1 つの門" -e "MaxParallelWrites" -e "PrivateNoteMaintenanceHostedService(" -e "WikiJsWriteGate.From" -e "writeGate.RunAsync"`（src・docs・.ai-context・deploy）:

| 場所 | 扱い |
| --- | --- |
| `WikiJsWriteGate.cs`（注記・`From`・`RunAsync`） | 直す（レプリカをまたぐ枠・待ちの期限の分離・`ReadMaxParallelWrites`） |
| `DocumentSyncConsumer.cs`・`DocumentDeletedConsumer.cs`・`WikiSyncOrdering.cs`（`writeGate.RunAsync`） | 直す（待ちの期限を渡す） |
| `WikiSyncTimeouts.cs`（待ち＝Wiki.js 1 回・既定の実行期限 60 秒の前提） | 直す |
| `WikiService/Program.cs`（`WikiJsWriteGate.From`・リスナーの数） | 直す |
| `IADR-0563`（決定 4・残余リスク (a)） | 日付つき追記（本文は凍結） |
| `docs/operations/operations.md`（`Wiki__MaxParallelWrites`） | 直す（全レプリカで合わせた数・新しい鍵） |
| `PrivateNoteMaintenanceHostedService(` の生成箇所（Program.cs・試験 5 箇所） | 直す（コーディネータを渡す） |
| `.ai-context/specs/20261020_1971_*`（門はプロセスに 1 つ） | 除外（point-in-time の記録。凍結） |
| `IADR-0557`（並列度は上げない） | 変えない（本件は並列度を上げない） |

規則 10（この変更で新たに誤りになる自分の記述）: 「門の待ち＝Wiki.js 1 回」「最悪 50 秒・30 秒」「既定の実行期限 60 秒」を引き直した —— `WikiSyncTimeouts.cs`・`WikiSyncTimeoutTests.cs`（2 件の数値・配線試験の「実行期限は既定のまま」）・IADR-0563 決定 4 に残っていた。すべて直した／追記した。

## 受け入れ基準と試験

| # | 基準 | 試験 |
| --- | --- | --- |
| 1 | 2 つのレプリカ相当（2 つの門・別々の接続）から同時に Wiki.js へ書いても直列になる。窓は実 PostgreSQL の `pg_locks` で B のロック待ちを観測して決定的に開く | `WikiJsReplicaWriteLockTests.二つのレプリカの門から同時に書いても書き込みは直列になる`（対照: `対照_プロセスの中の枠だけの門は二つのレプリカで重なる`）・待ちの上限と破棄のべき等・枠の数 |
| 2 | 2 つのホストで `PrivateNoteMaintenanceHostedService` を回しても本体を回すのは 1 つだけ | `PrivateNoteMaintenanceSingleRunTests.二つのホストで周期を回しても本体を回すのは一つだけ`（対照: `対照_リースを外すと二つのホストが同じ周期に本体へ入る`）・`本体が失敗してもリースは返る`・単体 `PrivateNoteMaintenanceLeaseTests` |
| 3 | 変異（排他を外す）で 1・2 が赤 | 下の §変異 |
| 4 | `HpaPdbScalingTests` と IADR-0563/0598 の記述が一致する | `HpaPdbScalingTests.SingleFlightWork_InScalingServices_IsExcludedAcrossReplicas`（wiki・document） |
| 5 | 門の待ち・実行期限の起動時検査 | `WikiSyncTimeoutTests`（既定 30 秒・90 秒、収まらない構成・再試行の連鎖） |

## 変異

| 変異 | 結果 |
| --- | --- |
| M1: 門がレプリカをまたぐ枠を取らない（`ProcessLocal` に差し替え） | `二つのレプリカの門から…直列になる`（20 秒で待ちに入らず赤）・`ロック待ちは…残らない`（時間切れが起きず赤） |
| M2: 定期処理のリースが常に取れる（`pg_try_advisory_lock` の結果を無視） | `二つのホストで…一つだけ` が赤 |

いずれも戻して緑を確かめた。

## 限界

- 本番の配線がリレーショナルなら PostgreSQL の枠を選ぶことは、配線の文面（`HpaPdbScalingTests`）と InMemory での縮退の試験で抑える。リレーショナルなプロバイダで WikiService / DocumentService のホストを起こす試験は無い。
- Helm は変えていない（wiki・document とも HPA の対象のまま）。`helm template` の差分は無い。
