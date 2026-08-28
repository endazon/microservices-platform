---
title: 作業仕様書 — 削除を検索索引とグラフへ伝播させる（#1016 の索引・グラフ分）
type: spec
status: done
related_ids:
  - FR-06
  - FR-17
  - FR-19
  - UC-03
  - ADR-0057
  - ADR-0027
  - ADR-0033
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - "ADR-0057（削除の伝播範囲。決定 1: 削除は本文の実体と索引まで及ぶ）"
related_adrs:
  - IADR-0239
  - IADR-0245
issue: "#1016"
---

# 作業仕様書: 削除の伝播（検索索引・グラフ）— #1016

## 起点

計画 **ADR-0057**（Accepted）決定 1 が、完全削除（FR-06 の文書削除／FR-19 の完全削除・
90 日自動物理削除）の伝播範囲を「①オブジェクトストレージの本文・資産 ②ベクトルストアの
チャンク・埋め込み ③監査・法務目的の残余を置かない」へ格上げした。
**本 PR が実装するのは ②（ベクトルストア）と、グラフ（FR-17 の複製・辺・AI 提案）の掃除**である。
前提は E3a（辺 `DocumentDeleted` の Wolverine 切替。仕様書 `20260828_edge-e3a-document-deleted.md`）——
新購読は Wolverine でしか追加できない（MassTransit は baseline 非掲載＝新規参照 fail）ため、
辺の切替が先に要る（#911 コメントの裁定と同じ構造）。

## 実装

| 変更 | 場所 |
| --- | --- |
| DocumentDeleted 購読（索引掃除） | `RetrievalService.Api/Composable/Steps/DocumentDeletedConsumer.cs`（新設。段名 `retrieval-delete`） |
| DocumentDeleted 購読（グラフ掃除） | `GraphService.Api/Composable/Steps/DocumentDeletedConsumer.cs`（新設。段名 `graph-delete`） |
| Wolverine ホスト配線 | 両サービスの `Program.cs`（`AddPlatformWolverineStep` → `ListenToPlatformQueue` → `UsePlatformMessagingDefaults`・`AddPlatformWolverineBroker`・`AddPlatformPipelineConfig`） |
| S4 | `scripts/event-topology-baseline.json`: DocumentDeleted の購読先へ RetrievalService / GraphService（各 wolverine）を追加 |
| S5 | `pipeline.json`: `retrieval-delete` / `graph-delete` の 2 段を追加（新購読 1 つにつき段 1 エントリ。transport 欄は書式に存在しない） |
| S6 | compose: retrieval-service / graph-service へ `*rabbit-env` ＋ `rabbitmq` depends_on。helm values: 両サービスへ `pipelineSteps: true`（RabbitMQ 接続はコード既定の in-cluster DNS `rabbitmq` で解決） |

- **`IVectorStore.DeleteByDocumentAsync` は末尾追加不要だった** —— ポート・Qdrant・InMemory の
  3 実装とも既存（#969 で整備済み）。#1016 の指摘どおり「実装は在るのに製品コードからの
  呼び出し元が 0 件」であり、本段が最初の呼び出し元である。
- **DocumentDeleted のペイロードは足りる**（`DocumentId` / `DeletedAt`）。索引・グラフとも
  文書 ID による削除であり、契約への末尾追加は不要。
- **グラフ掃除の射程**: ノード（属性複製）・両端いずれかが当該文書の辺（provenance 不問 ——
  ADR-0033 決定 6 の「利用者付与は消さない」は*再取り込み*の差分更新の話であり、文書削除には
  適用しない）・当該文書を端点とする AI 提案の全状態（pending / approved / rejected。
  決定 10 の「却下は永久保持」は再提案抑止のためで、端点消滅後は抑止対象の提案が生成され得ない）。
- **既存の Seal / 型ゲート（IADR-0242）は無傷** —— 本段は読み取り経路（AuthorizedNode /
  UnfilteredSubgraph）に触れず、DbContext の行削除のみを行う。

## readiness への影響（#911 論点 1 の決着）

GraphService / RetrievalService はこれまでブローカと無関係に起動していた。本 PR で
Wolverine ホストが載るため、**ブローカ不達時は起動・readiness が落ちる**（選択肢 3 = 現状受容）。
理由: (1) プロセス分離（選択肢 1）はデプロイ単位の新設であり #1016 の射程を超える、
(2) 初期化失敗を非致命にする（選択肢 2）と「購読が黙って死んでいる」状態を検知できず、
ADR-0057 が要求へ格上げした削除伝播が無音で止まる。受容の内容は本節に明示する（黙って出荷しない）。

## 限界（#1016 の残り）

- **①オブジェクトストレージの実体削除は本 PR に含まれない**（`IObjectStorageClient` に削除 API が
  無く、API 追加から要る）。SC-19 の固定文言の暫定措置（ADR-0057 決定 4）も未着手。
  **#1016 は本 PR で close しない**（索引・グラフ分の部分実装）。
- **削除対象は RetrievalService が検索に使うコレクション**（`Qdrant:CollectionName`）である。
  モデル別コレクション横断の削除口（`DeleteByDocumentFromAllAsync`）は IngestionService の
  ポートにあり、本サービスの `IVectorStore` には無い。検索も同じ単一コレクションを見るため
  「検索に出ない」は満たすが、**ADR-0057 ② の字義（ストアに残らない）はモデル別コレクション
  運用時に残余があり得る** —— ①の実装（削除経路の残り）と同じ単位で扱うべき事項として記録する。
- **中間状態（DB 行は消えたが実体が残る）の方式 IADR**（#1016 やること 5）は ①と併せて起こす
  （本 PR はブローカの at-least-once 再試行＋冪等削除で「最終的に消える」側に倒している）。

## 受け入れ基準

- [x] 削除 → 検索に出ない（`RetrievalService.Api.Tests/DocumentDeletedConsumerTests`。陽性対照つき）
- [x] 削除 → グラフに出ない（ノード・辺・pending 含む AI 提案。`GraphService.Api.Tests/DocumentDeletedConsumerTests`。陽性対照つき）
- [x] 冪等（再配信・未知 ID で例外にならない）
- [x] 段が宣言的構成に載る（S5）。`validate-pipeline-config.js` 緑（steps=7）
- [x] `check-event-topology.js` 緑（購読 5 → 7。発行側と wolverine を共有）
- [x] fan-out の保存: キューは `retrieval-service.DocumentDeleted` / `graph-service.DocumentDeleted` /
      `wiki-service.DocumentDeleted` に分かれる（サービス名前置。競合購読にならない）
- [ ] 実ブローカ検証は本環境では不可（Docker なし）。CI / 実環境に委ねる

## 変異試験（実測は締めのコミットまでに本節へ追記）

- 変異 D1: RetrievalService の Handle から `DeleteByDocumentAsync` 呼び出しを外す →
  否定形テスト「削除された文書のチャンクは検索に出ない」が赤になること。
- 変異 D2: GraphService の Handle から辺の削除を外す → 否定形テスト（辺）が赤になること。

［2026-08-28 追記 / #1021］**実測（波 2 監査の指摘 R1 の回収）。両方とも予告どおり落ちた:**

- D1 実測: `DeleteByDocumentAsync` 呼び出しを `Task.CompletedTask` へ置換 →
  `DocumentDeletedConsumerTests` **Failed 1 / Passed 1**（`削除された文書のチャンクは検索に出ない_他文書は残る` が赤）。
- D2 実測: `db.Edges.RemoveRange(edges)` を除去 →
  `DocumentDeletedConsumerTests` **Failed 1 / Passed 1**（`削除された文書のノードと辺とAI提案が消える_無関係は残る` が赤）。
- いずれも変異を戻して緑へ復帰することを確認済み。

## 計画書との差異

- ADR-0057 の①③は未実装（上記のとおり #1016 に残す）。②は本 PR の消費者コレクションの範囲で実装。
