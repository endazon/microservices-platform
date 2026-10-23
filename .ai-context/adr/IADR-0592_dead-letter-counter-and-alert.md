---
title: IADR-0592 デッドレター到達は共通既定（UsePlatformMessagingDefaults）の再試行の最後の段に束ねた継続で計器 messaging.dead_letter を 1 つ上げて数え、警報 DeadLetterReached（直近 1 時間の前進 > 0・for 5m・warning）で鳴らす。キュー名は見ない
type: impl-adr
status: Accepted
related_ids: [NFR-21, IADR-0593, ADR-0053, ADR-0012, ADR-0027, ADR-0052, ADR-0006, IADR-0245, IADR-0234, IADR-0567, IADR-0420, IADR-0233]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0053_dead-letter-detection-metric.md（決定 1〜5・2026-09-09 補完）
  - planning:projects/microservices-platform/07_adr/ADR-0012_conversion-pipeline.md（§結果 失敗時の縮退方針）
related_specs:
  - ../specs/20261023_2043_dlq-instruments.md
---

# IADR-0592: デッドレター到達の計器と警報（#2043）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-23
- 決定者: claude（#2043。PoC の機能面の全体監査の指摘）

## 起点・関連

- 起点 issue: #2043。警報の届け先は #2044（Alertmanager の既定の route をメールの受信先へ）で整っている。
- 関連する計画書 ID: NFR-21。計画 ADR: **ADR-0053**（決定 1〜5・2026-09-09 補完）・ADR-0012・ADR-0027・ADR-0052 手順 10・ADR-0006。
- 引き継ぐ実装判断: [IADR-0245](./IADR-0245_mt-wolverine-interop-and-edge-cutover.md) 決定 9（宛先トポロジ＝共有 1 本 `wolverine-dead-letter-queue`。ADR-0053 決定 4 の記録）・
  [IADR-0233](./IADR-0233_wolverine-shared-helper-confinement.md)（共通ヘルパへの封じ込め）・
  [IADR-0567](./IADR-0567_ingestion-budget-exhausted-alert.md)（総枠の使い切りの警報。本 IADR はそれと重ならないように数える）・
  [IADR-0420](./IADR-0420_unit-subject-identification-and-project-missing-metric.md)（「0 が正常」の器の `increase(...[1h]) > 0` の形）。
- 作業仕様書: [20261023_2043_dlq-instruments](../specs/20261023_2043_dlq-instruments.md)（母集合・受け入れ基準・変異）。

## コンテキストと課題

ADR-0053 はデッドレター到達の検知を「アプリが出す計器（カウンタ）」とし（決定 1）、契機を「自動再試行を使い切ったこと」（決定 2）、
置き場所を共通の 1 箇所（決定 3）と定めた。実装には計器が無く、警報 `RabbitMqDeadLetter` はコメントアウトのまま、しかもキュー名の照合
（`.*_error|.*_dlq`）で共有 1 本の `wolverine-dead-letter-queue` に一致しない。DLQ に落ちた `DocumentUpdated` / `DocumentDeleted`
（取り込み・Wiki 同期・グラフ同期・検索からの削除）を誰も検知できず、決定 5 の暫定手段（SC-07 の標識）は変換ジョブにしか効かない。

## 検討した選択肢

1. **共通既定の再試行の規則の最後の段（移送）に、計器を上げる継続を `.And(...)` で束ねる（採用）**。
2. **Wolverine 組み込みの計器 `wolverine-dead-letter-queue`（Meter `Wolverine:<ServiceName>`）を収集して警報を張る**。
   全ての移送（受け口の規則・既定の移送・逆シリアル化の失敗）を数えるので射程は広い。
3. **キュー名の照合へ `wolverine-dead-letter-queue` を足して `RabbitMqDeadLetter` を有効化する**（RabbitMQ の exporter を配線する）。

## 決定

選択肢 1 を採る。

**決定 1（計器）**: `DeadLetterMetrics`（`Platform.Shared.Infrastructure/Foundation/Messaging/`）を置く。Meter は受け口の時間切れと共有の
`microservices-platform.messaging`（`AddPlatformObservability` が既に収集している）、カウンタは `messaging.dead_letter`
（Prometheus では `messaging_dead_letter_total`）、タグは `messaging.message.type`（メッセージ本体の型の短い名前。本体が無ければエンベロープの型名）と
`error.type`（例外の型名）。入力（文書 ID・例外の文面）は載せない。サービスは OTLP のリソース属性（`job`）が持つ。

**決定 2（契機と置き場所。ADR-0053 決定 2・3）**: `UsePlatformMessagingDefaults` の `OnAnyException().RetryWithCooldown(...).Then.MoveToErrorQueue()` に
`.And(RecordDeadLetterAsync, ...)` を束ねる。Wolverine はこの段を「移送 ＋ 計器」の合成の継続にするので、**計器は移送と同じ試行（上限到達の 4 回目）でだけ上がり**、
再試行される試行では上がらない。計器は `options.Services` へ `TryAddSingleton` で入れ、継続は `runtime.Services` から引く。
**継続は例外を投げない**（引けなければ何もしない）—— 検知のための計器が回復の経路（移送）を壊さない。登録が消えたことは試験が捕まえる。

**決定 3（警報）**: 群 `messaging-dead-letter` に `DeadLetterReached` を置く。`for: 5m`・`severity: warning`。式は次のとおり（L＝`job, messaging_message_type, error_type`）。

```promql
sum by (L) (increase(messaging_dead_letter_total[1h])) > 0
  or (sum by (L) (messaging_dead_letter_total) unless sum by (L) (messaging_dead_letter_total offset 1h))
```

- **後半の腕（「1 時間前に無かった組が今ある」）が要る理由**: 計器は最初のデッドレターで初めて系列が立ち、OTel の累積値なので最初の標本が 1 になる。
  `increase()` は窓に前の標本が無い系列を 0 とみなすので、前半だけでは**最初の 1 通で鳴らない**（PR の AI レビュー 🟡。promtool v2.52.0 で実測し、
  前半だけの式は「系列が初めて 1 で現れる」場面と「前の系列が 1 時間より前に途絶えた後の 1 通目」の場面で発火しなかった）。0 が正常で稀に起きる計器では、
  守りたいのはまさにその初回である。後半の腕は、系列が現れてから 1 時間だけ立ち、その後は前半（前進の有無）に引き継ぐので恒常発火しない。
- **退けた案: 起動時に既知のタグの組へ `Add(0)` して系列を先に立てる。** タグの組（メッセージ型 × 例外の型）は閉じてはいるがコードの変更で増え、
  列挙を足し忘れた組の初回だけが黙って鳴らなくなる。例外の型は事前に列挙できない。式の側で塞ぐ方が漏れない。
- **集計に `error_type` を残す**（通知のラベルに例外の型が付く）。運用仕様書と注記は「通知のラベル `error_type`」で原因を引くよう案内しており、集計で落とすと案内が偽になる。
  基数は（サービス × メッセージ型 × 例外の型）で閉じる。
- **試験**: `deploy/prometheus/tests/messaging-dead-letter.test.yml`（`promtool test rules`。初回・前進・再起動のリセット・途絶後の初回・平常の 5 場面）を
  CI（`static-checks-units`）が配備と同じ版の promtool で回す。前半だけの式へ戻す変異で場面 1・5 が赤になることを確かめた。
- **同じ性質の既存の警報**（`IngestionBudgetExhausted`・`UnitDocumentsMissingProjectAttribute`・`DepartmentSyncNotCorrecting`。promtool で同じ場面を与えて発火しないことを実測）は
  本変更へ広げず #2097 で扱う。
**キュー名で絞らない**（ADR-0053 決定 1）。Prometheus（compose・k8s）と Grafana（compose・k8s）の 4 か所に同じ式で置き、コメントアウトの `RabbitMqDeadLetter` は消す
（次に読む人が有効な選択肢と受け取るため）。届け先は既定の route（#2044）。

**決定 4（トポロジ。ADR-0053 決定 4）**: 宛先は変えない。IADR-0245 決定 9（共有 1 本）がその記録である。本変更は `MoveToErrorQueue()` の宛先を上書きしない。

**決定 5（数えないもの）**:
- 受け口が独自の規則で**再試行せずに**デッドレターへ送る経路（取り込みの総枠の使い切り）。再試行を使い切った失敗ではなく、既に専用の計器と
  `IngestionBudgetExhausted` がある。両方で数えると同じ事象で 2 通鳴る。
- 下の §残余。

## 理由

- **選択肢 1 は ADR-0053 の 3 つの決定をそのまま写す。** 契機は「再試行を使い切った」で、置き場所は封じ込め済みの共通ヘルパ 1 ファイル（IADR-0233）である。
  計器名・タグは本リポの規約（`messaging.*`・基数を閉じる）で決められ、警報と C# の定数を試験で突き合わせられる。
- **選択肢 2 は射程が広いが、名前と Meter がライブラリのものである。** Meter 名がサービス名を含み（ワイルドカードで収集することになる）、
  計器名・タグ・どの経路で上がるかが版更新で変わり得る。ADR-0053 決定 2 の「再試行を使い切った」より広い（総枠の使い切りも数え、上の重複が起きる）。
  §残余の経路を拾うための補助として後から足せる（本 IADR はそれを妨げない）。
- **選択肢 3 は ADR-0053 決定 1 が退けた形そのもの**（キュー名に依存する。命名が変わると誰も気づかないまま対象 0 件になる）。

## 結果

- **良い影響**: Wolverine を載せる全 7 サービス（WikiService を含む。ADR-0053 2026-09-09 補完）で、再試行を使い切った `DocumentUpdated` / `DocumentDeleted` /
  `RawDocumentFetched` が警報になる。決定 5 の暫定手段（SC-07）が届かなかった Wiki 同期・取り込み・グラフ同期・削除の辺に、初めて検知の手段ができる。
- **悪い影響 / トレードオフ**: ADR-0053 §結果 のとおり、アプリが計上し損なえば検知も漏れる（キューを外から見る性質は失う）。
- **試験**: `DeadLetterMetricsTests`（判定関数で上限到達の段に計器があること・手前に無いこと・例外の種類によらないこと・本物のホストの runtime で継続を実行して
  1 回だけ数えタグが載ること・DI への登録・残余の固定）、`WolverineExtensionsTests`（回復の挙動の等価性は計器の継続を除いて従来どおり）、
  `scripts.repo.test.js` の #2043（4 か所の式と C# の定数の突合・初回の腕と `error_type` の集計・キュー名に依存しないこと・旧案が残らないこと・
  promtool の単体試験と CI の導入・全サービスが共通既定と計装を通すこと）、`deploy/prometheus/tests/messaging-dead-letter.test.yml`（promtool）。

### 残余（閉じていないもの。黙って広げない）

1. **MassTransit に残る DocumentNormalized の辺**（document-service の段 catalog。既定の名前で `DocumentNormalized_error`）。共通既定の下に無いので数えない。
   辺が Wolverine へ移れば自動で数えられる。それまでは運用仕様書の手順（`_error` のキューを人が見る）が暫定手段である。
2. **試行番号が上限を超えて届いた失敗**: 規則の段は試行番号ごとに置かれ、上限を超えた番号には段が無いので、Wolverine の既定の移送（計器なし）へ倒れる。
   1 回の配信の中の再試行は 4 で必ず止まるので、生じるのは試行回数を持ったまま届き直したとき（DLQ の写しの再投入）だけで、運用は再投入しない。
   挙動は試験で固定した（Wolverine の版更新で変われば赤になる）。
3. **逆シリアル化の失敗・ハンドラの無いメッセージ**: 規則を通らずに移送される。選択肢 2 を補助に足せば拾える（必要になったら起票する）。

### フォローアップ

- 配備後（PoC で `messaging_dead_letter_total` の系列と `DeadLetterReached` の評価対象が立つことを確かめた後）に、計画 ADR-0053 §結果 フォローアップ 1
  （05_observability-ops の暫定手段の記述を閉じ、決定 5 を解消する）とフォローアップ 2（決定 4 の記録＝IADR-0245 決定 9 を §関連 へ）を計画へ環流する。

## ［2026-10-23 追記 / #2097］決定 3 の式の形は IADR-0593 が改めた

- **決定 3（IADR-0592）→ 後継 [IADR-0593](./IADR-0593_first-series-arm-for-zero-normal-alerts.md)**（式の形だけ。警報の名前・`for`・`severity`・集計のラベル・キュー名で絞らないことは変えない）。
- 組ごとの腕（`sum by (L) (m) unless sum by (L) (m offset 1h)`）は、再起動で `instance`（`service.instance.id`。プロセスごとに替わる）が替わった新しい系列の 1 通目を、
  古いプロセスの系列が 1 時間前に在ったために落としていた（promtool v2.52.0 で実測）。腕を集計の前（系列ごと）へ移し、比較を外側の `> 0` 1 つにした:
  `sum by (L) ((messaging_dead_letter_total unless messaging_dead_letter_total offset 1h) or increase(messaging_dead_letter_total[1h])) > 0`。
- 決定 3 の「後半の腕が要る理由」と「退けた案（起動時に `Add(0)`）」は引き続き有効である。試験は `messaging-dead-letter.test.yml` の場面 6 を足した。
