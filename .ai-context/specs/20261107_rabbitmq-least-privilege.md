---
title: 作業仕様書 — ブローカの権限を利用者ごとの最小権限へ寄せる（既定の exchange への write・応答キュー・exchange の configure）
type: spec
status: done
related_ids: [NFR-18, IADR-0681, IADR-0689, IADR-0691]
author: claude
created: 2026-11-07
updated: 2026-11-07
plan_refs: []
---

# 作業仕様書 — ブローカの権限を利用者ごとの最小権限へ寄せる

> 本仕様書は、生成規則を変える前に作成した（着手 2026-11-07。develop `0f08179a2`）。洗い出しと実測は同じ日の手元の作業である。

## 起点となる計画書（トレーサビリティ）

- 非機能要件: NFR-18（サービス間の通信の保護）。
- 機能要求（FR）・ユースケース（UC）・画面（SC）: なし。
- 関連 IADR: IADR-0681（配線から導く生成器・段 1 / 段 2）、IADR-0689 決定 12（クラウドは段 2 だけ）、本作業の決定は IADR-0691。

## 目的・背景

- ブローカのサービスごとのユーザーの権限（生成物 `deploy/local/rabbitmq-setup/permissions.txt` と、取引ユニットの行を写したクラウドの
  `deploy/cloud/infra/rabbitmq-permissions.yaml`）を、利用者ごとの最小権限へさらに絞る。
- 絞る対象は 3 つ:
  1. 既定の exchange（`amq.default`）への write が全利用者にあった。RabbitMQ は既定の exchange への発行に宛先のキューの権限を求めない。
  2. configure に購読の exchange が入る。configure は宣言と削除を区別しない。
  3. 応答キューの正規表現が `wolverine\.response\..*`（接頭辞の後ろは任意）だった。

## 対象範囲

- 対象: `scripts/gen-rabbitmq-permissions.js`（生成規則）、生成物 2 つ、`scripts/rabbitmq-permissions.test.js`、`deploy/local/README.md`（段 2 の注記）、
  IADR-0691（新規）と IADR-0681（日付つきの追記）、`.ai-context/adr/README.md`（索引）。
- 対象外:
  - 両方のユニットの Wolverine の配線（購読側の宣言・戻しの経路）。§残余リスク。
  - `reconcile-users.sh`・ブローカの manifest・起動の直後の復元（段の切り替えと写し方は変えない）。
  - 取引ユニット（submodule）のコード。読んだだけで変えていない。

## 洗い出し: Wolverine が要る操作（コードと設定から）

### 読んだもの

- 基盤: `Platform.Shared.Infrastructure` の `WolverineExtensions.cs`（`UsePlatformRabbitMq`・`ListenToPlatformQueue`・`RoutePlatformEvent`・`BindPlatformQueue`）と
  各サービスの `Program.cs`（いずれも `.AutoProvision()`）。
- 取引ユニット: `AiStockTrading.TestSupport.PlatformShim` の `WolverineExtensions.cs`（`UseAiStockTradingRabbitMq`。`.AutoProvision()`・`UseConventionalRouting`・
  リスナーごとの `<キュー>_error`）と各 Worker。
- WolverineFx.RabbitMQ 6.24.5 の実装（逆アセンブルして読んだ）:
  - `RabbitMqListener.RequeueAsync`: 受け取り済みの未処理のメッセージを nack（requeue なし）し、自分のキューの送り手で送り直す。
    キューの送り手は既定の exchange（空名）を使う。呼ぶのは `RabbitMqEnvelope.DeferAsync`（リスナーの停止のときの戻し）と `TryRequeueAsync`。
  - `RabbitMqListener.CreateAsync`: `AutoPingListeners` のときだけ、起動時に既定の exchange で ping を送る。`AutoPingListeners` の既定は偽で、どちらのユニットも有効にしていない。
  - `RabbitMqTransport.tryBuildSystemEndpoints`: 応答キュー `wolverine.response.{Guid.NewGuid()}`（auto-delete・x-expires）を全サービスで宣言・購読する。
    応答を返す側は既定の exchange を使うが、要求・応答（`InvokeAsync<T>` の遠隔の呼び出し）はどちらのユニットも使っていない。
  - `RabbitMqExchange.DeclareAsync`: AutoProvision で、passive でも外部所有でもない exchange は `exchange.declare`（passive でない）で宣言する。
    名前が空（既定の exchange）なら宣言しない。
- 起動時の宣言を誰が行うか:
  - 発行側: 発行の exchange を宣言する（送り手の初期化）。
  - 購読側: 自分の受信キュー（取引ユニットは `_error` も）を宣言し、購読の exchange を**宣言し直して**から束ねる
    （基盤は `BindPlatformQueue` の `DeclareExchange`、取引ユニットは規約ルーティングの購読側の exchange）。
  - 運用の初期化（`reconcile-users.sh`）: ユーザーと権限だけを作り、exchange・キューは宣言しない。

### 利用者ごとの表

- 列の読み方:
  - 受信キュー: 自分で宣言（configure）・購読（read）・束ね（write）する。
  - 購読の exchange: 宣言し直し（configure）・束ねの元（read）。
  - 発行の exchange: 宣言（configure）・発行（write）。
  - 既定の exchange: 戻しの発行（write）。
- 全員が、共有のデッドレター `wolverine-dead-letter-queue`（configure・write・read）と応答キュー（configure・read）を使う。
- exchange 名は、取引ユニットでは型の完全名（`AiStockTrading.Shared.Contracts.Events.` ＋ 下の名前）、基盤では型の短い名前である。
  表は短い名前で書く（ただし `DiscordCommandContext` は `NotificationService.Domain` の型）。

| 利用者 | 受信キュー | 購読の exchange | 発行の exchange | 既定の exchange への write |
| --- | --- | --- | --- | --- |
| `msp-document` | `document-service.*`（なし） | なし | DocumentDeleted・DocumentUpdated | 要らない |
| `msp-datasource` | `datasource-service.*`（なし） | なし | RawDocumentFetched | 要らない |
| `msp-conversion` | `conversion-service.*`（受信 1 本） | RawDocumentFetched | RawDocumentFetched | 要る（戻し） |
| `msp-ingestion` | `ingestion-service.*`（受信 2 本） | DocumentDeleted・DocumentUpdated | IngestionCompleted | 要る（戻し） |
| `msp-retrieval` | `retrieval-service.*`（受信 1 本） | DocumentDeleted | なし | 要る（戻し） |
| `msp-wiki` | `wiki-service.*`（受信 2 本） | DocumentDeleted・DocumentUpdated | なし | 要る（戻し） |
| `msp-graph` | `graph-service.*`（受信 2 本） | DocumentDeleted・DocumentUpdated | なし | 要る（戻し） |
| `ast-audit` | `ai-stock-trading.audit-service.*`（受信 67 本＋各 `_error`） | AlternativeProtectiveStopAttempted・AssumptionsChanged・BacktestEvaluated・BorrowFeeAccrualUnavailable・BorrowFeeAccrued・BrokerAccountObserved・BrokerAvailabilityObserved・BrokerPositionsObserved・BuyInInferred・CapitalBaselineObservationMissing・CloseReducedForInFlightCloses・CostThresholdReached・DailyPolicyUnconfirmed・DailyReportNoResponse・FxRateSourceFellBack・FxRateSourcePrimaryRestored・FxRateSourceUsed・FxRateStale・GeneralWebCollectionStateChanged・GoodFaithViolationRecorded・GoodFaithViolationsCleared・InformationCollected・InformationSourceDegraded・InformationSourceRecovered・InformationSourceStateObserved・LlmCostIncurred・LlmFallbackFired・LlmGatewayUnsentDetected・LlmGatewayUnsentRecovered・MaintenanceMarginReductionExecuted・OrderApproved・OrderCancelled・OrderDispatchForgone・OrderExecuted・OrderFillTrackingAbandoned・OrderModified・OrderRejected・PositionCloseAbandoned・PositionCloseCancellationRequested・PositionCloseRequested・PositionClosedWithStaleFxRate・PositionDriftAdopted・PositionQueryStatusChanged・PositionReconciliationDrift・PriceMovementDetected・ProtectiveStopCoverageLost・ProtectiveStopPlaced・ProtectiveStopWaived・RealAccountReadOnlyQueried・ReportConfirmed・ReportDraftPresented・ReportKnowledgeReingested・ReportRegenerated・ScreeningContextReduced・SoftwareStopArmed・SoftwareStopExecuted・SoftwareStopLineWidened・StageTransitioned・StopLossMethodResolved・StopLossTriggered・TradeDecisionFailed・TradeDecisionForgoneBeforeLlm・TradeDecisionHeld・TradeDecisionMade・TradeDecisionSkipped・TradeExpenseRecorded・WithdrawalTriggered | なし | 要る（戻し） |
| `ast-backtest` | `ai-stock-trading.backtest-service.*`（なし） | なし | BacktestEvaluated | 要らない |
| `ast-configuration` | `ai-stock-trading.configuration-service.*`（なし） | なし | AssumptionsChanged | 要らない |
| `ast-cost-control` | `ai-stock-trading.cost-control-service.*`（受信 2 本＋各 `_error`） | AssumptionsChanged・LlmCostIncurred | CostThresholdReached | 要る（戻し） |
| `ast-information-collection` | `ai-stock-trading.information-collection-service.*`（なし） | なし | GeneralWebCollectionStateChanged・InformationCollected・InformationSourceDegraded・InformationSourceRecovered・InformationSourceStateObserved | 要らない |
| `ast-market-monitor` | `ai-stock-trading.market-monitor-service.*`（受信 2 本＋各 `_error`） | TradeDecisionHeld・TradeDecisionMade | PriceMovementDetected・StopLossTriggered | 要る（戻し） |
| `ast-notification` | `ai-stock-trading.notification-service.*`（受信 32 本＋各 `_error`） | AssumptionsChanged・BuyInInferred・CapitalBaselineObservationMissing・CostThresholdReached・DailyPolicyUnconfirmed・DailyReportNoResponse・FxRateSourceFellBack・FxRateSourcePrimaryRestored・FxRateStale・GoodFaithViolationRecorded・LlmFallbackFired・LlmGatewayUnsentDetected・LlmGatewayUnsentRecovered・MaintenanceMarginReductionExecuted・OrderDispatchForgone・OrderExecuted・OrderRejected・PositionCloseAbandoned・PositionClosedWithStaleFxRate・PositionDriftAdopted・PositionReconciliationDrift・ProtectiveStopCoverageLost・ProtectiveStopPlaced・ProtectiveStopWaived・ReportConfirmed・ReportDraftPresented・SoftwareStopArmed・SoftwareStopExecuted・StopLossTriggered・TradeDecisionSkipped・WithdrawalTriggered・DiscordCommandContext | DiscordCommandContext | 要る（戻し） |
| `ast-order-execution` | `ai-stock-trading.order-execution-service.*`（受信 4 本＋各 `_error`） | OrderApproved・PositionCloseCancellationRequested・PositionDriftAdopted・StopLossTriggered | AlternativeProtectiveStopAttempted・BrokerAccountObserved・BrokerAvailabilityObserved・BrokerPositionsObserved・CloseReducedForInFlightCloses・OrderApproved・OrderCancelled・OrderDispatchForgone・OrderExecuted・OrderFillTrackingAbandoned・OrderModified・PositionQueryStatusChanged・PositionReconciliationDrift・ProtectiveStopCoverageLost・ProtectiveStopPlaced・ProtectiveStopWaived・RealAccountReadOnlyQueried・SoftwareStopArmed・SoftwareStopExecuted・SoftwareStopLineWidened・StopLossMethodResolved・TradeExpenseRecorded | 要る（戻し） |
| `ast-report` | `ai-stock-trading.report-service.*`（なし） | なし | BuyInInferred・DailyReportNoResponse・LlmCostIncurred・LlmFallbackFired・MaintenanceMarginReductionExecuted・ReportConfirmed・ReportDraftPresented・ReportKnowledgeReingested・ReportRegenerated | 要らない |
| `ast-risk-management` | `ai-stock-trading.risk-management-service.*`（受信 18 本＋各 `_error`） | BacktestEvaluated・BrokerAccountObserved・BrokerAvailabilityObserved・BrokerPositionsObserved・InformationSourceDegraded・InformationSourceRecovered・InformationSourceStateObserved・OrderApproved・OrderCancelled・OrderDispatchForgone・OrderExecuted・OrderModified・ProtectiveStopCoverageLost・ProtectiveStopPlaced・SoftwareStopExecuted・SoftwareStopLineWidened・StopLossTriggered・TradeDecisionMade | BorrowFeeAccrualUnavailable・BorrowFeeAccrued・BuyInInferred・CapitalBaselineObservationMissing・GoodFaithViolationRecorded・GoodFaithViolationsCleared・MaintenanceMarginReductionExecuted・OrderApproved・OrderRejected・PositionCloseAbandoned・PositionCloseCancellationRequested・PositionCloseRequested・PositionDriftAdopted・PositionReconciliationDrift・StageTransitioned・WithdrawalTriggered | 要る（戻し） |
| `ast-trade-decision` | `ai-stock-trading.trade-decision-service.*`（受信 2 本＋各 `_error`） | InformationCollected・PriceMovementDetected | DailyPolicyUnconfirmed・FxRateSourceFellBack・FxRateSourcePrimaryRestored・FxRateSourceUsed・FxRateStale・LlmCostIncurred・LlmFallbackFired・LlmGatewayUnsentDetected・LlmGatewayUnsentRecovered・PositionClosedWithStaleFxRate・PositionQueryStatusChanged・ScreeningContextReduced・TradeDecisionFailed・TradeDecisionForgoneBeforeLlm・TradeDecisionHeld・TradeDecisionMade・TradeDecisionSkipped | 要る（戻し） |

- 既定の exchange への write が要るのは、受信キューを持つ 12 利用者である。発行だけの 6 利用者（基盤 2・取引ユニット 4）には要らない。
  - 名前を指定した直接の送信（`ToRabbitQueue`・`SendAsync` でキューへ送る）は、どちらのユニットにも無い（`ListenTo`・`ToRabbitQueue`・`InvokeAsync` で走査した）。
  - 発行だけの 4 利用者（取引ユニット）には、Wolverine 以外の送受信（ブローカの直接の利用）も無い。

## 設計

1. **既定の exchange**: 導出に `hasListener` を足す（基盤は `BindPlatformQueue` か `ListenToPlatformQueue` / `ListenToRabbitQueue` の呼び出し、取引ユニットは受ける型が 1 つ以上）。
   真の利用者だけ write に `amq\.default` を入れる。要る操作の表（`requiredAccess`）も同じ条件にする。
2. **応答キュー**: configure・read を Guid の形（小文字の 16 進 8-4-4-4-12）だけに当てる。awk の互換のため、区間 `{n}` を使わず文字クラスを並べる。
3. **exchange の configure**: 購読と発行の exchange ちょうどに限る（今の規則と同じ範囲）。購読の exchange を外すのは §残余リスク 2 の変更の後。
4. **錨**: 正規表現は全体を `^( … )$` で囲み、各名前は字を逃がして並べる（今の規則と同じ）。錨の外の名前に当たらないことを陰性対照で固定する。
5. 段の関係は変えない:
   - ローカル: 段 1（既定 `RABBITMQ_PERMISSIONS=vhost`）は従前どおり vhost 全体。段 2（`narrow`）が新しい行を当てる。点検・ConfigMap の写し・復元は従前どおり。
   - クラウド: 段 2 だけ。生成物の取引ユニットの行が変わる（既定の exchange は 4 利用者から外れ、応答キューは 11 利用者すべてで Guid の形になる）。

## 受け入れ基準

- [x] 生成物 2 つを作り直し、既定の照合（`node scripts/gen-rabbitmq-permissions.js`。取引ユニットを照合する）が通る。
- [x] 発行だけの利用者の write に既定の exchange が無く、受信キューを持つ利用者には有る（試験）。
- [x] 利用者ごとの陰性対照（既定の exchange・他の利用者のキュー・使わない exchange の configure・応答キューの形でない名前・錨の外）が、どれも権限に当たらない（試験）。
- [x] 規則を元に戻す変異（全員に既定の exchange・`.*` の応答キュー・全 exchange の configure・錨なし）で、陰性対照が赤になる（試験）。
- [x] 新しい試験は develop の生成器・生成物に当てると赤になる（3 件。下の §検証）。
- [x] 実ブローカで、拒否されるべき操作が拒否され、取引ユニットの実配線（購読側・発行だけの側）が拒否なしで動く（§実測）。
- [x] 段 1 の挙動・段 2 の当て方・クラウドの写し方は変わらない（既存の試験が通る）。

## 母集合（規則 9・10）

- **規則 9**: 誤りの側の文字列 `amq.default`・`amq\.default`・`wolverine.response`・`wolverine\.response` を、追跡下の全ファイル（submodule を除く）で走査した。
  当たったのは次のファイル。
  - 改める: `scripts/gen-rabbitmq-permissions.js`・`scripts/rabbitmq-permissions.test.js`・生成物 2 つ・`deploy/local/README.md`（段 2 の注記）・
    IADR-0681（決定 2 の表と §残る制約 1・2 に、日付つきの追記で本作業を指す）。
  - 対象外: `.ai-context/specs/20261105_2412_rabbitmq-narrow-permissions.md`（確定した作業の記録。当時の実測の記述として正しい）。
  - 対象外: `scripts/rabbitmq-permissions.test.js` の合わせの模型（`msp-a … amq\.default …`）。合わせのスクリプトの動きを見る架空の行で、生成規則とは独立である。
- 権限の段の語（`narrow`・`rabbitmq-permissions`）でも `deploy/cloud`・`docs` を走査した。`deploy/cloud/README.md` の表は「生成物の行だけ」と書くだけで、行の中身に触れないので改めない。
- **規則 10**: 本変更で新たに誤りになり得る自分の記述は「全利用者が既定の exchange に書ける」という前提の文である。上の走査で当たった文は
  `deploy/local/README.md` と IADR-0681 だけで、両方を改めた。
- **規則 11**: 窓を扱う是正ではないので当てない。

## 実測（2026-11-07・手元の RabbitMQ 3.13.7 コンテナ。基盤の manifest と同じイメージの digest。稼働のブローカには触れていない）

### 権限の意味（AMQP の操作を 1 つずつ。管理者で exchange を先に作った）

| 操作 | 権限 | 結果 |
| --- | --- | --- |
| 在る exchange を passive でなく宣言し直す | configure なし | 拒否（403） |
| 在る exchange を passive で宣言する | configure なし | 通る |
| 自分のキューを宣言し、在る exchange へ束ねる | キューの configure・write、exchange の read | 通る |
| exchange を削除する | configure なし / あり | 拒否（403） / 通る |
| 既定の exchange へ発行する | `amq.default` の write なし / あり | 拒否（403） / 通る（宛先のキューの権限は見ない） |

### 生成物の新しい行を当て、取引ユニットの実配線で動かす

- ハーネス: `UseAiStockTradingRabbitMq`（取引ユニットの実配線そのもの）を参照する小さなコンソール。購読側は `AssumptionsChanged` のハンドラを 1 つ持つ。
  発行だけの側はハンドラの発見を切り、`AssumptionsChanged` を発行する。
- 正常系（新しい行・`ast-cost-control` が購読、`ast-configuration` が発行だけ）: 購読側・発行側とも起動・停止し、20 通を発行して 20 通を処理した。
  ブローカのログに拒否は 0 件（発行だけの側は既定の exchange への write を持たない）。
- 陰性対照（新しい行。すべて 403 で拒否）:
  - 発行だけの利用者が、既定の exchange から他の利用者の受信キューへ発行する。
  - 発行だけの利用者が、他の利用者の受信キューを宣言する・削除する・購読する。
  - 発行だけの利用者が、発行しない exchange へ発行する・使わない exchange を削除する。
  - 購読する利用者が、使わない exchange を削除する・応答キューの形でない名前のキューを宣言する。
- 対照（develop の行）: 発行だけの利用者が、既定の exchange から他の利用者の受信キューへ発行できた。
- 残る経路（新しい行でも通る。§残余リスク）:
  - 購読する利用者が、既定の exchange から他の利用者のキューへ発行する。
  - 購読する利用者が、購読の exchange を削除する。
- 外せない理由の実測: 購読する利用者の configure から購読の exchange を外す（発行側が先に宣言して exchange は在る）と、
  購読側の Wolverine は `exchange.declare` の拒否を繰り返し、起動が `BrokerInitializationException` で失敗した。

### CI の実ブローカ

- Integration・Integration Stack・E2E Live は段 1（vhost 全体）で動く。生成物は使わないので、本変更の段 2 の行を確かめる経路ではない。
  正常系が壊れていないこと（生成器と文書の変更が配線に触れていないこと）の確かめとして回す。

## 残余リスク

1. 受信キューを持つ 12 利用者は、既定の exchange への write を持ち続ける（戻しに要る）。名前を知るどのキューへも直接入れられる経路は、これらの利用者に残る。
   外すには、Wolverine の戻しを既定の exchange を使わない形へ替える変更（上流か、受信を耐久の受信箱にする変更）か、受け手の側で発行元を確かめる仕組みが要る。
2. 購読する利用者は、購読の exchange の configure（削除を含む）を持ち続ける。外すには、両方のユニットで購読側の宣言を passive（または外部所有）に替え、
   exchange を誰が先に宣言するか（発行側か運用の初期化か）と起動の順序を決める変更が要る。
3. 応答キューは名前にサービスを含まない（IADR-0681 §残る制約 2 と同じ）。
4. 実ブローカでの確かめは、取引ユニットの 2 利用者の実配線と、AMQP の操作ごとの意味に限る。残る 16 利用者は生成器の単体の試験（要る操作と陰性対照）で確かめた。
   ローカル・クラウドの段 2 を稼働のブローカで当て直すのは、利用者が点検（`--permissions-report`）の後に行う。

## 検証

- 修正の前（develop の生成器・生成物に新しい試験を当てた）: 3 件が赤（基盤の 1 行の字面・既定の exchange は受信キューを持つ利用者だけ・陰性対照）。
- 修正の後:
  - `node scripts/rabbitmq-permissions.test.js`: 22 件が合格（取引ユニットを照合）。
  - `node scripts/gen-rabbitmq-permissions.js`: 一致（18 行・取引ユニットを照合した）。
  - `node scripts/deploy-cloud.test.js`: 合格（helm・kubectl のオフライン描画。クラウドの ConfigMap が生成物と一致する）。
  - `REQUIRE_REPO_TESTS=1 RABBITMQ_PERMISSIONS_REQUIRE_AST=1 node scripts/scripts.test.js`: 1283 件が合格。
  - 文書の検査（`check-trace-blocks`・`gen-knowledge-graph --check`・`check-cross-repo-refs`・`check-plan-id-qualification`・`check-doc-links`・
    `check-advisory-ids`・`check-reading-budget`・`check-doc-type-vocabulary`・`check-nul-bytes`・`check-trace-followthrough`・`check-test-name-references`）: すべて合格。
  - .NET・フロントのコードは変えていないので、ビルドと単体の試験は CI に委ねる。
