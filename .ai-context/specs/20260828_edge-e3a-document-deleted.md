---
title: 作業仕様書 — 辺 DocumentDeleted を Wolverine へ移す（E3a・段 c）
type: spec
status: done
related_ids:
  - FR-06
  - FR-13
  - FR-19
  - UC-03
  - UC-07
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

# 作業仕様書: 辺 `DocumentDeleted` の Wolverine 移行（E3a・段 c）

## 起点

移行チェーンの **E3a**（IADR-0234 決定 3 のチェーンを IADR-0245 決定 7 の 3 段刻みで進める）。
切替手順は [IADR-0245](../adr/IADR-0245_mt-wolverine-interop-and-edge-cutover.md) 決定 4 の
**「停止 → 排出 → 切替」の 3 段**に従う。**本 PR は段 c（コードの一括切替）のみ**である ——
段 a・b はデプロイ時の運用であり、コード PR に含めない（E1 仕様書
`20260822_issue-441_edge-rawdocumentfetched.md` の確定に従う。runbook は後述）。

## 対象の辺

| 役割 | 場所 | 検査器から見えるか |
| --- | --- | --- |
| 発行 ①（組織文書の削除 API） | `DocumentService.Api/Foundation/Endpoints/DocumentEndpoints.cs`（旧 :347。移行後はポート呼び） | ✅ 見えていた（`Publish(new DocumentDeleted(`） |
| 発行 ②（個人資料の完全削除 API） | `DocumentService.Api/Foundation/Endpoints/PrivateNoteEndpoints.cs`（旧 :165） | ✅ 見えていた |
| 発行 ③（90 日経過後の自動物理削除） | `DocumentService.Api/Foundation/Services/PrivateNoteMaintenanceService.cs`（旧 :68） | ✅ 見えていた |
| 購読（Wiki.js 実体撤去） | `WikiService.Api/Composable/Steps/DocumentDeletedConsumer.cs` | ✅ 見える |

**辺は原子的**であるから、この 4 箇所を同一コミットで切り替えた。
移行後、発行 3 箇所は `IDocumentDeletedPublisher`（ポート）を呼び、バスへの発行は
`WolverineDocumentDeletedPublisher` の 1 箇所（可視）に集約される。

## 発行の網羅性（IADR-0245 決定 8 の適用結果）

決定 8 の 4 段をそのまま適用した（辺ごとに方法を定義し直さない —— 方法の正本は同決定）。

- 段 1（名前で過剰包含）: `\.(Publish|PublishAsync|Send|SendAsync)\s*[<(]` をテストを除く実装ソースへ。
  **befe3cd 時点で 21 ヒット**（knowledge Services + platform backend）。
- 段 2（バス以外を理由つきで落とす）: 5 件を落とす。
  | 落とすもの | 件数 | 理由 |
  | --- | --- | --- |
  | `llmClient.SendAsync` / `base.SendAsync` / `client.SendAsync` | 3 | HTTP の送信（RagOrchestrator / AnthropicResponseSanitizingHandler / AuthzBffEndpoints） |
  | `transport.SendAsync`（EmailOutboxDispatcher） | 1 | SMTP のメール送出であってメッセージバスではない |
  | `doc.Publish()`（DocumentEndpoints） | 1 | ドメインの状態遷移メソッド |
- 段 3（式の型を辿る）: `ToEvent(...)` の戻り値型は `DocumentUpdated`（`DocumentEndpoints.ToEvent`）。
  `bus.PublishAsync(ev)`（ConversionJobEndpoints:75）の `ev` は `PrepareRetryAsync` 戻り値 = `RawDocumentFetched`。
- 段 4（3 値の記録・**befe3cd 時点**）: **バス発行 16 / 検査器に見える 8 / 見えない 8**。

**条件 B（総数の保存）**: E1 記録（2026-08-22）は 13 / 6 / 7。差 +3 は個別コミットで説明できる ——
個人資料の完全削除 2 箇所（IADR-0270 の波）と FR-21 本文投入経路の `DocumentUpdated` 1 箇所（不可視 +1）。
**本 PR 後は 14 / 6 / 8** —— 減少 2 は本コミット自身が DocumentDeleted の 3 発行箇所を
ポート裏の 1 アダプタへ集約したことで説明される（発行**経路**は 3 つのまま。バス API の呼び出し点が 1 に畳まれた）。

**DocumentDeleted の発行はこの 3 経路（上表 ①②③）で全てである**（段 1〜3 の走査で
`DocumentDeleted` を運ぶヒットは他に無い。テスト・フィクスチャは走査対象外）。

## 設計

- **発行側**: E1 の `IDocumentNormalizedPublisher` と同じ形。ポート
  `DocumentService.Application/Foundation/Ports/IDocumentDeletedPublisher.cs`（引数は素の値）＋
  アダプタ `DocumentService.Api/Composable/Adapters/WolverineDocumentDeletedPublisher.cs`
  （`using Wolverine;` のみ・イベント構築をアダプタ側に置き可視発行を保つ）。
  - **配置の判断**（IADR-0280 との関係）: ポートは写像どおり `<Svc>.Application` へ（新様式）。
    アダプタは写像では Infrastructure だが、**messaging パッケージ（Platform.Shared.Infrastructure /
    WolverineFx.RuntimeCompilation）を段 2 未着手の Infrastructure 骨格へ持ち込まない**ため
    合成ルート側（Api/Composable/Adapters）へ置いた（「判断に迷う配置は Api 側」の指示に従い理由をここに記録）。
- **DocumentService.Api**: MassTransit ホスト（DocumentNormalized 購読 = 辺 E2・DocumentUpdated 発行 = 辺 E3b）
  は残し、Wolverine ホスト（発行のみ・`DisableConventionalDiscovery`）を併設。
  readiness へ `AddPlatformWolverineBroker()`（W4）を追加。csproj へ `WolverineFx.RuntimeCompilation`。
- **WikiService.Api**: `DocumentDeletedConsumer` を `IConsumer<DocumentDeleted>` →
  `IPipelineStep<DocumentDeleted>` ＋ `Handle(DocumentDeleted, CancellationToken)` へ。
  `Envelope` は取らない —— 本段は試行回数を使わない（E1 が Envelope を取ったのは SC-07 のジョブ記録が
  最終試行判定を要したため。wiki-delete に相当する記録は無い）。
  Program.cs は `AddPlatformWolverineStep<DocumentDeletedConsumer>` ＋
  `ListenToPlatformQueue("wiki-service", step?.Queue ?? nameof(DocumentDeleted))` ＋
  `UsePlatformMessagingDefaults()`。introspection は `AddWolverineStep` へ。
  `AddPlatformWolverineBroker()` 追加。wiki-sync 段（DocumentUpdated）は MassTransit のまま（辺 E3b）。

## S4 / S5 / S6

- **S4**（`scripts/event-topology-baseline.json`）: `--update` で DocumentDeleted の発行・購読が
  `masstransit → wolverine` へ反転（2 行・前進のみ）。更新前の検査出力は前進 2 件のみで、
  **辺の両側が同時に動いたことを実測で確認した**。
- **S5**（`deploy/helm/microservices-platform/files/pipeline.json`）: **変更なし。**
  実物の書式を確認した結果、段宣言（`steps[]`）は transport 欄を持たない（`pipeline.schema.json` /
  `validate-pipeline-config.js` V1〜V6 とも transport を知らない）。wiki-delete 段の
  consumer 型完全名・input は移行前後で不変のため、書き換える行が存在しない。
- **S6**（helm values / docker-compose）: **変更なし。** document-service / wiki-service は
  compose で既に `*rabbit-env` ＋ `rabbitmq` depends_on を持ち、helm でも `pipelineSteps: true` 済み。
  新規サービスの購読追加は無い（それは #1016 = C2 の射程）。

## 受け入れ基準（段 c）

- [x] 発行 ①②③・購読の 4 箇所すべてが Wolverine へ移っている（コード diff で確認）
- [x] `DocumentDeletedConsumer` が `IPipelineStep<DocumentDeleted>` を実装し `Handle` を持つ（IADR-0239）
- [x] `AddPlatformWolverineStep` 経由で登録し、戻り値 `Queue` を `ListenToPlatformQueue` へ渡す
- [x] **登録経路が実際に使われることを試験が確かめている**（`PipelineRecomposeTests` が Wolverine ホストを
      起こし、規約探索を効かせた状態で有効/無効の両方を測る。E1 変異 R の教訓）
- [x] `check-event-topology.js --update` の差分に両側のトランスポートが反転して現れる（上記のとおり実測）
- [x] `check-backend-libraries.js` 緑（MassTransit の新規混入なし。DocumentService / WikiService の
      MassTransit 参照は E2 / E3b が残るため baseline に残る —— 削除は行が落ちる辺で行う）
- [ ] 実ブローカ試験: **本環境では実行不可**（Docker・実ブローカなし）。E1 先例と同じく単体＋
      登録経路試験で固定し、実ブローカは CI（`integration.yml`）/実環境に委ねる。
      **マージ後に `integration.yml` を確認すること**（E1 の教訓: PR の CI は `Category!=Integration`）。

## 段 a・b の runbook（デプロイ担当が実施する。コード PR に含めない）

1. **段 a（停止）**: `DocumentDeleted` の発行 3 経路はいずれも**人間契機の API**（削除 API・完全削除 API）
   または**日次の定期処理**（PrivateNoteMaintenanceHostedService の purge）である。切替窓の間、
   削除操作を行わない運用合意を取り、定期 purge は窓と重ならない時刻に切り替える
   （メンテナンスは 24h 周期・起動時初回実行。窓を短く保てば重なりは避けられる）。
   ⚠️ 機械的強制は無い（E1 と同じ受容。強制が要るなら別単位）。
2. **段 b（排出）**: `GET /api/queues/%2F/DocumentDeleted` の **`messages`（ready + unacked）が
   30 秒間隔で 3 回連続 0**（T=90s / N=3。E1 の値を踏襲 —— 再試行窓 42 秒 + 保守的上乗せ）。
   🔴 `messages_ready` と `consumers` は使わない（IADR-0245 決定 5）。
   🔴 1 回でも 0 以外が出たら streak をリセットして数え直す（緩めると再充填を検出できない）。
3. **段 c（切替）**: 本 PR をデプロイする。
4. **事後確認**: 前置つきキュー `wiki-service.DocumentDeleted` が生え、旧キュー `DocumentDeleted` に
   新たな滞留が生じないことを確認する。

## テスト

- `WikiService.Api.Tests`: 59 件 緑。削除系 2 件は `Handle` 直接呼び（測るのは削除の写像）、
  登録経路は `PipelineRecomposeTests`（Wolverine ホスト・規約探索を効かせた器）が持つ。
- `DocumentService.Api.Tests`: 184 件 緑。発行の観測は `RecordingMessageBus`（`IMessageBus` ダブル）へ
  切替。**同ダブルは 3 つ目の複製**（DataSource / Conversion に続く）—— 各テストプロジェクトは
  自己完結で共有ヘルパを持たないため、共通化は見送り複製を受容する（E1 引き継ぎの「3 つ目が要るなら
  検討」に対する判断）。
- テストホストは `DisableAllExternalWolverineTransports()` で実ブローカから切り離す
  （E1 実測: 無いと約 135 秒ハング）。

## 変異試験（実測は締めのコミットまでに本節へ追記）

- 変異 W1: `AddPlatformWolverineStep<DocumentDeletedConsumer>` の登録を外す →
  `PipelineRecomposeTests.有効な削除段は登録経路を通って処理される_Wolverine側` が落ちること。

［2026-08-28 追記 / #1021］**実測（波 2 監査の指摘 R1 の回収）。予告した名前のテストは実在せず、
予告した変異は落ちなかった。事実をそのまま記録する:**

- 上で名指しした `…_Wolverine側` というテストは**実装確定後に存在しない**（予定名のまま書いた誤り）。
  実在するのは `PipelineRecomposeTests` の 3 件（有効な同期段は登録経路を通って処理される／
  構成のみで同期段を外し削除段だけを有効化できる／無効化した削除段は登録されず購読されない）。
- **実測 1**: WikiService `Program.cs` の削除段登録
  （`AddPlatformWolverineStep<DocumentDeletedConsumer>`）を null 代入へ置換して knowledge ユニットの
  **全テストを実行 → 1,037 件すべて緑（落ちない）**。ユニットテストは自前ホストで登録経路を通す
  ため、**本番 Program.cs の結線の欠落はユニットでは検出されない**。
- **何が守っているか**: ①共通ヘルパの規則 2〜7（宣言 ↔ 実装のずれ・未宣言登録は**起動失敗**。
  向きは「登録したのに宣言と合わない」側）②`PipelineRecomposeTests` の対（有効 → 処理される／
  無効 → 購読されない）が**ヘルパの登録・除外の両向き**を固定する。
  ③「宣言したのに登録しない」向き（本変異の形）は**ユニットの射程外**で、実配線の検証は
  統合スタック（`integration.yml`。本仕様書の「実ブローカ検証は CI / 実環境に委ねる」と同じ残件）
  に委ねる。検査器の新設は行わない（同型の事故は本件が 1 回目。記録に留める）。

## 計画書との差異

差異なし（ADR-0027 の辺単位切替に忠実。dual-publish / dual-subscribe は採らない —— IADR-0245 決定 1・2）。
