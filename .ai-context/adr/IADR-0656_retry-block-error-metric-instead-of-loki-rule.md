---
title: IADR-0656 Wolverine の耐久化の操作のメモリ上の再試行の失敗は、ログのテンプレートの照合で計器にし、Prometheus の警報で鳴らす（Loki の ruler は新設しない）
type: impl-adr
status: Accepted
related_ids: [FR-12, NFR-21, ADR-0006, ADR-0053, IADR-0609, IADR-0592, IADR-0593]
author: claude
created: 2026-10-30
updated: 2026-10-30
related_specs:
  - ../specs/20261030_2178_outbox-retry-error-alert.md
---

# IADR-0656: 送出の再試行中の欠落の窓の検知（#2178）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-30
- 決定者: claude（#2178）。方式はコーディネータの承認（2026-10-30）による。

## 起点・関連

- 起点 issue: #2178（[IADR-0609](./IADR-0609_transactional-outbox-other-services.md) 決定 2 の残余リスク。#2177 の AI レビューの 🟡）。
- 計画: ADR-0006（観測の統一計装）、ADR-0053 決定 1〜3（デッドレターの検知）。
- 前提:
  - [IADR-0592](./IADR-0592_dead-letter-counter-and-alert.md)（`DeadLetterReached`。初回の腕つきの計数の警報の形）
  - [IADR-0593](./IADR-0593_first-series-arm-for-zero-normal-alerts.md)（初回の腕を系列ごとに判定する）

## 文脈

IADR-0609 は、送出表への書き込み（`StoreOutgoingAsync`）の一時失敗の間に起きる欠落の窓を、残余リスクとして受容した。

- Wolverine は例外を出さず、JasperFx の `RetryBlock<T>` でメモリ上に再試行する。
- その間に呼び出し側の確定（DataSourceService の watermark など）が成功し、再試行が済む前にプロセスが止まると、イベントが欠落し得る。
- 気づく手段は、ブロックのエラーログ `Error while trying to retry {Item}` だけだった。

issue は「このログを検知するアラートを、既存の監視（Loki と Alertmanager）に足す」と書く。🔴 **しかし develop に Loki の ruler は無い**（`deploy/loki-config.yaml`・`deploy/local/observability/loki.yaml` とも ruler の設定 0 件、LogQL の警報 0 件。2026-10-30 実測）。既存の警報はすべて Prometheus のルール（経路 A / B の 2 か所の写しと、暫定の Grafana の 2 か所の写し）である。

### 再試行の上限（［2026-10-30 / #2360 のレビュー］実測）

`JasperFx.Blocks.RetryBlock<T>`（JasperFx 2.37.0）には**上限がある**。IADR-0609 は「メモリ上で再試行する」とだけ書き、捨てる経路に触れていなかった。

- **実測**: 常に失敗する処理を渡すと、最初の 1 回 ＋ 再試行 3 回（間隔は約 0.1／0.1／0.25 秒）の後、約 0.5 秒で `Discarding message {Message} after {Attempts} attempts`（**Information・例外なし**）を出し、その項目を捨てた。
  - 型は `MaximumAttempts`・`Pauses` の公開プロパティを持つ。
- **Wolverine は上限を変えていない**: WolverineFx 6.24.4 の各アセンブリ（Wolverine・Wolverine.RDBMS・Wolverine.RabbitMQ）のメタデータに、`set_MaximumAttempts`・`set_Pauses` への参照が 0 件（`RetryBlock` 型の参照はある）。既定のまま使われる。
- **帰結**: 欠落は「再試行の途中でプロセスが止まった」ときだけでなく、**一時失敗が約 0.5 秒より長く続いたとき**にも起きる。プロセスが動き続けていても、捨てた操作は戻らない。
  - 運用の手順は「既定で埋め直す」に改めた。省けるのは、`Discarding message` も再起動も無いと確かめたときだけである。
  - 警報の説明文も合わせた。

## 決定

1. **ログを計器に変える。** ロガーの提供者 `RetryBlockErrorLoggerProvider` を、`AddPlatformLogging` で全サービスに足す。
   - 計器は `messaging.retry_block.errors`（Prometheus では `messaging_retry_block_errors_total`）である。
   - 照合に当たった記録だけを数え、ログは書かない。
   - Meter は受け口の時間切れ・デッドレターと同じ（`AddPlatformObservability` が収集対象へ入れている）。
2. **照合は、メッセージテンプレート（`{OriginalFormat}`）の完全一致 ＋ 例外つき ＋ Error 以上**で行う。文面の部分一致はしない。
   - カテゴリでは絞らない。Wolverine がブロックへ渡すロガーのカテゴリは、呼び出し元ごと・版ごとに変わり得る。テンプレートはブロックの型が持つ。
   - 試験は**実物の `JasperFx.Blocks.RetryBlock<T>`** を動かし、計器が 1 進むことを測る。JasperFx の版上げでテンプレートが変われば、この試験が赤になる（テンプレートを別の文字列にする変異で赤になることを確かめた）。
3. **ラベルは `error.type`（例外の型の短い名前）だけ**とする。
   - 利用者・文書 ID・メッセージの中身（ログの `{Item}`）は載せない。
   - 基数は例外の型の数で閉じる（`DeadLetterMetrics` の `error.type` と同じ）。
4. **警報 `MessagingRetryBlockErrors`** は `DeadLetterReached` と同じ形にする（`sum by (job, error_type) ((m unless m offset 1h) or increase(m[1h])) > 0`、`for: 5m`、warning）。
   - 初回の腕を系列ごとに持つ（IADR-0593）。
   - `absent()` は置かない。0 が正常の計数で、平常時は系列が立たない。不在は平常である。
   - 置き場所は既存の 4 か所の写し（経路 A / B・暫定の Grafana の 2 か所）にそろえ、パリティ検査を通す。
   - 場面は `deploy/prometheus/tests/messaging-retry-block.test.yml`（promtool。CI）で固定する。
5. **数えるのは `RetryBlock<T>` の再試行の失敗のすべて**である（送出表の書き込みに限らない）。Wolverine は耐久化の操作をこのブロックで回すので、どれもメモリ上の再試行の間は欠落の窓になる。

## 却下した案

- **Loki の ruler を新設し、LogQL の警報にする**（issue の文言どおり）: 採らない。
  - 2 経路（compose・k8s）の Loki の設定、ruler の保存先、Alertmanager への配線、ルールの置き場所を新しく作ることになる。1 本の警報のために監視の経路を 1 本増やす。
  - 試験の手段が無い。promtool は LogQL を評価できず、Loki の ruler の単体試験の器もリポジトリに無い。静的な検査どまりになり、「鳴る」ことを測れない。
  - 既存の警報（29 本）はすべて Prometheus 側にあり、パリティ検査・promtool の試験・Grafana の暫定経路の検査がそろっている。そこに載せれば、同じ統制で守れる。
- **文面の部分一致（`Contains("Error while trying to retry")`）で照合する**: 採らない。版上げで文面が少し変わると、黙って 0 になる。
- **カテゴリ（`Wolverine.*` など）で絞る**: 採らない。ブロックへ渡るロガーのカテゴリを、版をまたいで固定する手段が無い。テンプレートの完全一致で十分に狭い。
- **運用手順だけを足し、警報は置かない**: issue の受け入れ基準（ログが出たら警報が上がる）を満たさない。

## 帰結・残余リスク

- issue（#2178）の「Loki と Alertmanager に足す」とは手段が違う。通知の宛先は既存の Alertmanager の経路のままである。issue に記録した。
- 警報は「再試行が失敗した」ことを告げるだけで、欠落が起きたかどうかは告げない。欠落は、上限で捨てたとき（`Discarding message`。Information）と、再試行の途中でプロセスが止まったときに起きる（上の実測）。
  - 運用仕様書 §送出の再試行中の欠落の窓 は、**既定で埋め直す**形にした（下流は冪等）。
  - 捨てたこと自体（`Discarding message`）は計器にしていない。数えるなら、同じ提供者にテンプレートを 1 つ足す形になる（Information・例外なし）。今は「再試行の失敗」の警報から手順で確かめる。
- 提供者は全サービスのロガーに 1 つ足される。照合は `{OriginalFormat}` の 1 回の比較で、`IsEnabled` は Error 以上だけを受ける。費用はエラーログの数に比例する。
- 計器は、ロガーの最小の段（`Logging:LogLevel`）が Error を落とす構成では進まない。既定の構成は Error を出す。
