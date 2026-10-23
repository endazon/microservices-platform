---
title: デッドレター到達（自動再試行を使い切った失敗）を共通既定の 1 箇所で数える計器 messaging.dead_letter を置き、警報 DeadLetterReached（直近 1 時間の前進 > 0・5 分・warning）を Prometheus・Grafana の経路 A/B の 4 か所に張る（#2043）
type: spec
status: done
related_ids: [NFR-21, ADR-0053, ADR-0012, ADR-0027, ADR-0052, ADR-0006, IADR-0592, IADR-0245, IADR-0234, IADR-0567, IADR-0420]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0053_dead-letter-detection-metric.md（決定 1〜5・2026-09-09 補完）
  - planning:projects/microservices-platform/07_adr/ADR-0012_conversion-pipeline.md（§結果 失敗時の縮退方針「管理者に通知する」）
issue: "#2043"
---

# 仕様書: デッドレター到達の計器と警報（#2043）

> 本仕様書は実装着手前に作成した（着手 2026-10-23）。実装の基点は MSP `origin/develop` `c8825ca3`。判断は IADR-0592。

## 起点となる計画書（トレーサビリティ）

- 非機能: NFR-21（可観測性・警報）
- 計画 ADR: **ADR-0053**（決定 1: アプリの計器を正とする／決定 2: 契機は「自動再試行を使い切った」／決定 3: 共通の 1 箇所に封じ込める／
  決定 4: 宛先トポロジの記録／決定 5: 配備までの暫定手段。2026-09-09 補完: WikiService を含む全サービスに当たる）・ADR-0012（管理者に通知する）・
  ADR-0027（Wolverine）・ADR-0052（手順 10 の再試行・デッドレターの等価性）・ADR-0006（可観測性）
- 関連 IADR: IADR-0245 決定 9（宛先トポロジ＝共有 1 本の記録。ADR-0053 決定 4 の条件を既に満たしている）・IADR-0234 決定 6・IADR-0567（総枠の警報）・IADR-0420（`increase(...[1h]) > 0` の形）
- 警報の届け先: #2044（Alertmanager の既定の route がメールの受信先へ向く。マージ済み）

## 受け入れ基準（ADR-0053 と issue から確定）

1. **AC1（決定 1・2）**: 自動再試行を使い切ってデッドレターへ送ったとき、アプリの計器が 1 つ上がる。**キュー名・ブローカを契機にしない。**
   上限の手前の試行（再試行される失敗）では上がらない。例外の種類によらない。
2. **AC2（決定 3・補完）**: 計器は共通既定 `UsePlatformMessagingDefaults` の 1 箇所でだけ上がり、Wolverine を載せる**全サービス**（WikiService を含む）が
   その既定と OTLP への計装（`AddPlatformObservability`）を通す。
3. **AC3（決定 1・ADR-0012）**: 計器の前進で警報が鳴る。**最初の 1 通（系列が初めて 1 で現れる）でも鳴る**（PR の AI レビュー 🟡 で追加。promtool で固定）。式は `increase(...[1h]) > 0` ＋「1 時間前に無かった組」の腕、キュー名で絞らない。Prometheus・Grafana の経路 A/B の 4 か所が同じ。
   キュー名に依存した旧案（コメントアウトの `RabbitMqDeadLetter`）は消す。
4. **AC4（試験で固定）**: 計器の継続を外す・計器の登録を外す・計器を上げない・計器名を変える・サービスが共通既定を呼ばない、のいずれでも試験が赤。
5. **AC5（決定 4）**: 宛先トポロジの記録は IADR-0245 決定 9 にある（共有 1 本）。本変更はトポロジを変えない（`MoveToErrorQueue()` の宛先を上書きしない）。
6. **AC6（決定 5 の解消の準備）**: 運用仕様書に警報・数えないもの・鳴ったときの手当てを書く。**決定 5 の暫定手段の解消（計画側の §結果 フォローアップ 1）は配備後**
   （PoC で警報の評価対象が立つことを確かめてから）に計画へ環流する。

## 判断の材料（母集合の走査。規則 9・10）

### DLQ を持つキュー・発行者・消費者（`origin/develop` `c8825ca3` の各 `Program.cs` を `ListenToPlatformQueue` / `RoutePlatformEvent` / `AddMassTransit` で走査）

| 辺 | 発行者 | 消費者（キュー） | トランスポート | 失敗の行き先 |
| --- | --- | --- | --- | --- |
| RawDocumentFetched | datasource-service・conversion-service（再試行の再発行） | conversion-service（`conversion-service.RawDocumentFetched`） | Wolverine | 共有 `wolverine-dead-letter-queue` |
| DocumentNormalized | conversion-service（MassTransit） | document-service（段 catalog。既定の名前 `DocumentNormalized`） | **MassTransit** | `DocumentNormalized_error`（エンドポイント別） |
| DocumentUpdated | document-service | ingestion-service・wiki-service・graph-service（各 `<svc>.DocumentUpdated`） | Wolverine | 共有 `wolverine-dead-letter-queue` |
| DocumentDeleted | document-service | retrieval-service・wiki-service・graph-service（各 `<svc>.DocumentDeleted`） | Wolverine | 共有 `wolverine-dead-letter-queue` |

- Wolverine を載せるサービスは 7 つ（conversion・retrieval・document・wiki・ingestion・graph・datasource）。**全 7 つが `UsePlatformMessagingDefaults` と `AddPlatformObservability` を呼ぶ**（実測）。
  受信キューを持つのは 5 つ（document・datasource は発行のみ）。
- MassTransit に残る購読は **DocumentNormalized の 1 辺だけ**（`DocumentService` の `AddPlatformPipelineStep<DocumentNormalizedConsumer>`）。conversion・ingestion の `AddMassTransit` は購読を持たない（発行の経路）。

### 既存の計器と警報

| 対象 | 現状 | 出典 |
| --- | --- | --- |
| デッドレター到達の計器 | **無い**（`CreateCounter` で dead / dlq / error_queue に当たるもの 0 件。issue の実測と一致） | `grep -rn CreateCounter src` |
| 共通既定の再試行・デッドレター | `OnAnyException().RetryWithCooldown(2/10/30s).Then.MoveToErrorQueue()`。計器なし | `WolverineExtensions.cs` |
| 受け口ごとのデッドレター直行 | 取り込みの総枠（`EmbeddingBudgetDeadLetterPolicy`）。計器 `messaging_consumer_timeout_total` と警報 `IngestionBudgetExhausted` が既にある | IADR-0567 |
| 共通の Meter | `microservices-platform.messaging`（`ConsumerTimeoutMetrics`）。`AddPlatformObservability` が収集する | `ObservabilityExtensions.cs` |
| デッドレターの警報 | **無い**。`RabbitMqDeadLetter`（`queue=~".*_error\|.*_dlq"`）はコメントアウト。共有 1 本に一致しない | `deploy/prometheus/alerts.yml` 末尾 |
| SC-07 の標識（決定 5 の暫定手段） | 変換ジョブだけ。Wiki 同期・取り込み・グラフ同期・削除の辺には暫定手段が無い | ADR-0053 2026-09-09 補完 |

### 追随する文書（誤りの側の文字列で走査）

- `デッドレターの警報は無い` / `デッドレターの滞留の警報は無い` / `RabbitMqDeadLetter` / `dead-letter queue メトリクス` で全追跡ファイルを走査した。
  追随先: `deploy/prometheus/alerts.yml`（冒頭の注意・総枠の注記・follow-up）・`deploy/local/observability/prometheus.yaml`（総枠の注記）・
  `docs/operations/operations.md`（§埋め込みプロバイダの総枠・DLQ 滞留の監視・監視の表・follow-up の一覧）。
- 除外: `.ai-context/adr/IADR-0567`・`IADR-0234`・`IADR-0245`（凍結記録。当時の事実としては正しい）、`.ai-context/specs/`（凍結）、`CHANGELOG.md`（生成物）。
- 件数の導出値（Grafana のルール数 `26`）を `26 件` / `＝ **26**` / `25 → 26` で走査した。追随先: `slo-alerts.yaml` と `grafana.yaml` の前書き、`check-grafana-alerting.js` の前書き。
  内訳を計算し直した（2+5+6+2+1+2+1+1+1+**1**+1+2+2 = **27**）。

## 形

- **計器**: `DeadLetterMetrics`（`Foundation/Messaging/`）。Meter は共通の `microservices-platform.messaging`、カウンタ `messaging.dead_letter`（Prometheus `messaging_dead_letter_total`）、
  タグ `messaging.message.type`（メッセージ本体の型の短い名前）・`error.type`（例外の型名）。基数はコードの型で閉じる。サービスは `job` が持つ。
- **上げる場所**: `UsePlatformMessagingDefaults` の規則の最後の段に `.And(RecordDeadLetterAsync, ...)` を束ねる（移送と同じ継続）。計器は `options.Services` へ `TryAddSingleton` で入れ、
  継続は `runtime.Services` から引く。**継続は例外を投げない**（検知のための計器が回復の経路を壊さない）。
- **警報**: 群 `messaging-dead-letter`・`DeadLetterReached`・`sum by (job, messaging_message_type, error_type) (increase(messaging_dead_letter_total[1h])) > 0 or (sum by (…) (messaging_dead_letter_total) unless sum by (…) (messaging_dead_letter_total offset 1h))`・`for: 5m`・`warning`（初回の腕の理由は IADR-0592 決定 3）。
  Grafana は評価器 `gt 0`・`noDataState: OK`・uid `messaging-dead-letter-reached`（30 文字）。

## 範囲外（IADR-0592 §残余へ）

- MassTransit に残る DocumentNormalized の辺（`DocumentNormalized_error`）。辺の移行で共通既定の下へ入れば自動で数えられる。
- 試行番号が上限を超えて届いたメッセージ・逆シリアル化の失敗・ハンドラの無いメッセージ（Wolverine の既定の移送で計器を通らない）。
- 計画側の決定 5 の解消（配備後に環流）。

## 変異の結果（手元で実測）

| 変異 | 赤になった試験 |
| --- | --- |
| `.And(RecordDeadLetterAsync, …)` を外す | `DeadLetterMetricsTests` 5 件（上限到達の継続・例外 3 種・実行） |
| `TryAddSingleton<DeadLetterMetrics>()` を外す | 実行の試験・DI 登録の試験 |
| 継続の中で計器を上げない | 実行の試験 |
| `DeadLetterCounterName` を変える | `scripts.repo.test.js` の #2043（名前の固定）・`計器の名前を固定する` |
| `WikiService/Program.cs` から `UsePlatformMessagingDefaults()` を外す | `scripts.repo.test.js` の #2043（全サービスが共通既定を通す） |
| 式を `increase` だけ（初回の腕なし）へ戻す | promtool の場面 1（初回）・場面 5（途絶後の初回）が赤、`scripts.repo.test.js` の #2043 が赤 |

## PoC での確認（配備は閉場後）

運用仕様書 §DLQ の扱い と PR 本文の手順に従う（計器の系列が立つこと・陽性対照・警報の評価対象・firing まで）。
