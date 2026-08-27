---
title: 作業仕様書 — 辺 DocumentUpdated（fan-out）を Wolverine へ移す（E3b・段 c）
type: spec
status: done
related_ids:
  - FR-02
  - FR-06
  - FR-13
  - UC-03
  - UC-04
  - UC-06
  - ADR-0027
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - "ADR-0027（メッセージング基盤 = Wolverine）"
related_adrs:
  - IADR-0234
  - IADR-0239
  - IADR-0245
---

# 作業仕様書: 辺 `DocumentUpdated`（fan-out）の Wolverine 移行（E3b・段 c）

## 起点

移行チェーンの **E3b**。IADR-0245 決定 4 の 3 段のうち**段 c（コードの一括切替）のみ**
（段 a・b は運用。runbook は後述。作法は E3a 仕様書 `20260828_edge-e3a-document-deleted.md` と同じ）。
`DocumentUpdated` は**本リポジトリ唯一の fan-out 辺**（1 発行 → 2 購読者）であり、
手順 3（キュー名のサービス名前置）の実効がこの辺の主リスクである。

## 対象の辺（一括切替した全メンバ）

| 役割 | 場所（befe3cd 時点） | 検査器から見えたか |
| --- | --- | --- |
| 発行（文書 CRUD 6 口） | `DocumentEndpoints.cs` :131/175/214/243/260/311（`Publish(ToEvent(...))`） | 🔴 不可視 |
| 発行（タグ改名の再発行） | `TagDictionaryEndpoints.cs:136`（同形） | 🔴 不可視 |
| 発行（カタログ登録の連鎖） | `DocumentNormalizedConsumer.cs:61`（`Publish(new DocumentUpdated(`） | ✅ 可視 |
| 購読（取り込み） | `IngestionService.Worker/Composable/Steps/DocumentUpdatedConsumer.cs` | ✅ 可視 |
| 購読（Wiki 同期） | `WikiService.Api/Composable/Steps/DocumentSyncConsumer.cs` | ✅ 可視 |

発行 8 箇所は `IDocumentUpdatedPublisher`（ポート・DocumentService.Application）へ集約し、
バス API の呼び出し点は `WolverineDocumentUpdatedPublisher`（可視）の 1 箇所になった。
**不可視だった 7 箇所が構造的に消えた**（ポート呼びは IADR-0245 決定 8 段 1 の走査キーに一致しない。
イベント構築はアダプタ側 —— 可視性を保つ E1 の作法）。

## 発行の網羅性（IADR-0245 決定 8・条件 B）

- 段 1〜4 の再適用（本コミット後）: **バス発行 7 / 可視 6 / 不可視 1**。
  不可視 1 は `ConversionJobEndpoints.cs:75`（`PublishAsync(ev)` = RawDocumentFetched。E1 で試験固定済み）。
- C1 後の 14 → 7 の差 7 は本コミットで説明される: DocumentUpdated の 8 呼び出し点が
  アダプタ 1 点へ集約（−7）、IngestionCompleted は同数のままファイル移動（±0）。
  **発行経路（業務上の発行契機）は 8 つのまま消えていない** —— ポート越しに数え直すと
  8 経路すべてが残っている（DocumentEndpoints 6 ＋ TagDictionary 1 ＋ DocumentNormalizedConsumer 1）。

## 実装

- **DocumentService**: `IDocumentUpdatedPublisher`（Application/Foundation/Ports）＋
  `WolverineDocumentUpdatedPublisher`（Api/Composable/Adapters。配置判断は E3a 仕様書 §設計と同じ）。
  旧 `ToEvent` は `PublishUpdatedAsync`（識別子→表示名の変換点を 1 つに保つ内部ヘルパ）へ置換。
  MassTransit に残るのは DocumentNormalized の購読（辺 E2）のみ。
- **IngestionService**: `DocumentUpdatedConsumer` を `IPipelineStep<DocumentUpdated>` へ。
  IngestionCompleted の発行（辺は射程外・MassTransit のまま）は `IIngestionCompletedPublisher`
  （ポート）＋ `MassTransitIngestionCompletedPublisher`（別ファイル）へ隔離 —— 1 ファイル
  1 トランスポート（発行側 union 汚染の防止。E1 の `IDocumentNormalizedPublisher` と同じ理由）。
  csproj へ `WolverineFx.RuntimeCompilation`。
- **WikiService**: `DocumentSyncConsumer` を `IPipelineStep<DocumentUpdated>` へ。
  **MassTransit を全撤去**（パッケージ参照・Program の AddMassTransit・テストのハーネス）。
  `backend-library-baseline.json` から WikiService.Api / WikiService.Api.Tests の 2 行を削除
  （ratchet の「消えたのに残っていれば fail」に従う前進方向の更新）。
  Wolverine ホストは 2 段（wiki-delete / wiki-sync）・リスニングキュー 2 本
  （`wiki-service.DocumentDeleted` / `wiki-service.DocumentUpdated`。ハンドラ振り分けは型で決まる）。
- **fan-out の保存**: 購読キューは `ingestion-service.DocumentUpdated` /
  `wiki-service.DocumentUpdated` に分かれる（`ListenToPlatformQueue` の前置）。
- **queue 宣言の意味論変化**（記録）: MassTransit 経路の `Endpoint(e => e.Name = step.Queue)` は
  宣言値をそのまま使ったため、同一 queue 宣言で競合コンシューマを作れた。Wolverine 経路の
  適用点は **queue 宣言にも必ずサービス名を前置する**ため、宣言経路から競合コンシューマを
  作ること自体ができなくなった。`QueueOverrideFanOutTests` は「同一宣言値でも fan-out が
  保たれる」ことを固定する形へ書き換えた（旧テストは「丁度 1 つが受信」を固定していた）。

## S4 / S5 / S6

- **S4**: `--update` 前の検査出力は前進 3 件のみ（発行 1・購読 2 が同時に反転）——
  **辺の全メンバが同時に移ったことを実測で確認**。baseline の DocumentUpdated 3 値が wolverine へ。
- **S5**: 変更なし（段の consumer 型完全名・input は不変。transport 欄は書式に存在しない）。
- **S6**: 変更なし（両購読サービスは compose の `*rabbit-env`・helm の `pipelineSteps: true` を既に持つ）。

## テスト

- `WikiService.Api.Tests` 59 件緑（sync/delete/archive とも Handle 直接呼び ＋ 登録経路は
  `PipelineRecomposeTests` の Wolverine ホスト器）。
- `IngestionService.Worker.Tests` 28 件緑（Handle 直接呼び。IngestionCompleted はポートの引数で観測。
  一時障害は `ThrowAsync<EmbeddingTransientException>` で固定 —— ブローカ再試行へ委ねる形）。
- `DocumentService.Api.Tests` 184 件緑（DocumentUpdated の発行観測を `RecordingMessageBus` へ）。
- `Knowledge.IntegrationTests` ローカル 31 合格 / 40 skip（Docker 無し）。
  - `DocumentUpdatedFanOutTests` / `QueueOverrideFanOutTests`: 発行を **E1 の
    RawDocumentFetchedEdge と同じ形の専用発行ホスト**（実行ごとに一意な exchange を宣言し
    両購読キューへ束縛・`PublishMessage<DocumentUpdated>`）へ書き換えた。購読側は本番 Program
    配線のまま。⚠️ **本環境では実行できない**（Docker 無し）—— **マージ後に `integration.yml` の
    実行を必ず確認すること**（E1 の教訓。PR の CI は `Category!=Integration` で走らない）。
  - `WikiSyncTests` の発行スモークは `IMessageBus.PublishAsync` へ（消費確認は従来どおり範囲外）。

## 受け入れ基準（段 c）

［2026-08-28 追記 / #1021］波 2 監査の指摘 R2 の回収 —— 本仕様書だけ本節を欠いていた。
既に S4・テスト節へ記録済みの実測を、E3a と同形のチェックボックスへ写す（新規の主張は無い）。

- [x] 発行（DocumentService）・購読（Ingestion / Wiki）の全メンバが同一コミットで Wolverine へ移っている
      （S4: `--update` 前の検査出力が前進 3 件のみ＝辺の全メンバ同時反転を実測）
- [x] 各 consumer が `IPipelineStep<DocumentUpdated>` を実装し、`AddPlatformWolverineStep` 経由で登録される
- [x] fan-out の保存: 購読キューがサービス名前置で分かれ、競合購読にならない（テスト節の専用発行ホストで固定）
- [x] `check-event-topology.js` 緑（baseline の DocumentUpdated 3 値が wolverine）
- [x] `check-backend-libraries.js` 緑（MassTransit 新規混入なし）
- [x] 単体テスト緑: WikiService 59 / IngestionService.Worker 28 / DocumentService 184
- [ ] 実ブローカ試験: **本環境では実行不可**（Docker なし）。**マージ後に `integration.yml` を確認すること**

## 段 a・b の runbook（デプロイ担当。コード PR に含めない）

1. **段 a（停止）**: `DocumentUpdated` の発行 8 経路はすべて**人間契機の API**（文書 CRUD・本文投入・
   タグ改名）または **DocumentNormalized の連鎖**である。切替窓の間、文書の作成・更新・タグ改名を
   行わない運用合意を取り、連鎖の上流は E1 の手段（`DataSourceSync:Enabled=false`）で止める
   （同期停止 → RawDocumentFetched が出ない → DocumentNormalized → DocumentUpdated の連鎖が止まる。
   手動 /sync・再変換 API は E1 と同じく窓の間叩かない合意）。
2. **段 b（排出）**: 旧キュー `DocumentUpdated` の **`messages`（ready + unacked）が 30 秒間隔で
   3 回連続 0**（T=90s / N=3。E1 の値）。`messages_ready` / `consumers` は使わない（IADR-0245 決定 5）。
   1 回でも 0 以外なら streak をリセット。⚠️ fan-out 辺なので旧構成のキューが複数在り得る ——
   `rabbitmqctl list_queues` で `DocumentUpdated` を含む全キューを対象にする。
3. **段 c（切替）**: 本 PR をデプロイ。
4. **事後確認**: `ingestion-service.DocumentUpdated` / `wiki-service.DocumentUpdated` の 2 本が生え、
   **両方**にコンシューマが付くこと。旧キューに新たな滞留が生じないこと。

## 計画書との差異

差異なし（辺単位・一括切替。ADR-0050 決定 4「移行 → 契約変更」の順にも整合 —— 本文指紋の
契約変更（#911 = C4）は本辺の移行後に行う）。
