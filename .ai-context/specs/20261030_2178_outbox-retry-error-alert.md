---
title: 作業仕様書 — Wolverine の耐久化の書き込みのメモリ上の再試行の失敗を計器にし、警報で検知する（#2178）
type: spec
status: done
related_ids: [FR-12, NFR-21, ADR-0006, ADR-0053, IADR-0609, IADR-0592]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2178"
---

# 作業仕様書 — 送出の再試行中の欠落の窓の検知（#2178）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `05cb882b`）。起点 IADR-0609 決定 2 の残余リスク（#2177 の AI レビューの 🟡）。

## 背景（IADR-0609 の残余）

1. 送出表への書き込み（`StoreOutgoingAsync`）が一時的に失敗する。
2. Wolverine（JasperFx の `RetryBlock`）がメモリ上で再試行している間に、watermark の保存が成功する。
3. その後でプロセスが止まると、イベントが欠落し得る。

今これに気づける手段は、エラーログ `Error while trying to retry {Item}` だけである。

## 実測（2026-10-30）

- ログの出どころは JasperFx 2.37.0（WolverineFx 6.24.4 の依存。`project.assets.json`）の `JasperFx.Blocks.RetryBlock<T>` である。
  - 小さな実験で `RetryBlock` に一度だけ失敗する処理を渡した。
  - Error で `{OriginalFormat}` = `Error while trying to retry {Item}`、例外つきの記録が 1 件出た。
  - その後、Debug の `Completed {Item}` が出た。
- 🔴 **develop に Loki の ruler は無い**（`deploy/loki-config.yaml`・`deploy/local/observability/loki.yaml` とも ruler の設定 0 件、LogQL の警報 0 件）。
  - issue は「Loki と Alertmanager に足す」と書くが、そのままでは置き場所が無い。
  - コーディネータの承認（2026-10-30）を得て、**計器化**を選ぶ（IADR-0656）。

## 方針（IADR-0656）

1. **計器**: `messaging.retry_block.errors`（Prometheus では `messaging_retry_block_errors_total`）。
   - 共通の Meter（`ConsumerTimeoutMetrics.MeterName`）に載せる。
   - ラベルは `error.type`（例外の型の短い名前）だけにする。利用者・文書 ID・メッセージの中身は載せない。
2. **観測点**: ロガーの提供者 `RetryBlockErrorLoggerProvider` を `AddPlatformLogging` で全サービスに足す。
   - 照合は文面の部分一致ではなく、**メッセージテンプレート（`{OriginalFormat}`）の完全一致 ＋ 例外つき ＋ Error 以上**で行う。
   - カテゴリでは絞らない（Wolverine が渡すロガーのカテゴリは版で変わり得る。テンプレートは JasperFx の型が持つ）。
3. **版上げで赤になる試験**: 実物の `JasperFx.Blocks.RetryBlock<T>` を動かし、提供者を通して計器が 1 進むことを確かめる。
   - テンプレートが変われば赤になる。
4. **警報** `MessagingRetryBlockErrors`: `DeadLetterReached` と同じ初回の腕つきの `increase(...[1h]) > 0`。
   - 平常時は系列が立たず、初回 0 系列では鳴らない。`absent` は置かない（0 が正常の計数で、不在は平常である）。
   - 置き場所: `deploy/prometheus/alerts.yml`・`deploy/local/observability/prometheus.yaml` の写し・Grafana の暫定の警報の 2 か所（パリティ）。
   - promtool の試験: `deploy/prometheus/tests/messaging-retry-block.test.yml`。
5. **運用仕様書**: 検知から再取り込み（DataSourceService の再同期）までの手順を 1 節足す。

## 母集合（規則 9・10）

- **規則 9**: `git grep -n "Error while trying to retry\|28 件\|27 → 28\|ConfigDriftDetected"` で、警報の件数の導出値と置き場所を走査する。結果は実装中の追記に書く。
- **規則 10**: Grafana の警報の件数（28 → 29）を計算し直す。運用仕様書の警報の表を更新する。IADR-0609 の「運用で気づく手段はログ」の注記を更新する。

## 受け入れ基準 → 試験

| 基準 | 試験 | 修正前 |
| --- | --- | --- |
| 実物の `RetryBlock` の再試行の失敗で計器が 1 進む（版上げでテンプレートが変われば赤） | `RetryBlockErrorMetricsTests` | 赤（計器が無い） |
| 別のテンプレート・例外なし・Error 未満は数えない | 同 | 赤 |
| 計器のラベルは `error.type` だけ | 同 | 赤 |
| ログが出たら警報が上がる（初回を含む）。前進が無ければ収まる。0 系列では鳴らない | `messaging-retry-block.test.yml`（promtool。CI） | 赤 |
| 経路 A / B・Grafana のパリティ | `check-prometheus-alerts-parity`・`check-grafana-alerting` | — |
| 運用仕様書に検知から再取り込みまでの手順がある | 文書 | — |

## 実装中の追記（2026-10-30）

### 母集合（規則 9・10）の走査結果

| 当たり | 扱い |
| --- | --- |
| Grafana の警報の件数（`slo-alerts.yaml`・`grafana.yaml` の「28 件」と内訳、`check-grafana-alerting.js` の冒頭） | 28 → 29 へ計算し直した（内訳に messaging-retry-block 1 を足した） |
| `deploy/prometheus/alerts.yml`・`deploy/local/observability/prometheus.yaml`・Grafana の 2 か所 | 警報を足した（パリティ検査 3 本が OK） |
| `scripts/scripts.repo.test.js` の「0 が正常」の警報の一覧（`EXPECTED_F`・`TEST_FILES_F`） | 足した（足さないと「単体試験が無い」で赤になった。実測） |
| IADR-0609 の「運用で気づく手段はログ」 | 追記で、警報を置いたことを記録した |
| 運用仕様書の障害対応の表・§送出の再試行中の欠落の窓 | 足した |
| `Error while trying to retry` の他の当たり | 上の追加箇所だけ |

### 結果

- `RetryBlockErrorMetricsTests` は 3 件で、赤 → 緑。
  - 実物の `RetryBlock` で計器が 1 進む。
  - テンプレート・例外・段の陰性。
  - `AddPlatformLogging` の登録。
  - テンプレートを別の文字列にする変異で、2 件が赤になることを確かめた。
- `scripts.repo.test.js` に、式を C# の計器名・タグ名から導いて 4 か所で見る試験を足した（1215 件が緑）。
- 🔴 汎用ホストを使わない器（`PlatformLoggingTests`）で、`IMeterFactory` が無く 3 件が赤になった（実測）。
  - 提供者の登録で `AddMetrics()`（TryAdd）を呼ぶように直し、緑にした。
- promtool の試験（`messaging-retry-block.test.yml`）は、手元に promtool が無いので CI（static-checks-units）で初めて走る。
  - 場面は `messaging-dead-letter.test.yml` と同じ型（初回・前進・不在・instance の入れ替わり）である。

### ［2026-10-30 追記 / #2360 の AI レビュー］

- 🟡 1: 運用仕様書の trace ブロックに IADR-0656・IADR-0609・#2178 を足した。
- 🟡 2: 取得の印の巻き戻しは、対象のデータソースに絞る形（`"Id"` ＋ `"LastSyncedAt" >`）を第一にした。全体を戻す形は「特定できない場合に限る」とした。
- 🟡 3: `RetryBlock<T>` の上限を実測した（IADR-0656 §再試行の上限）。
  - 最初の 1 回 ＋ 再試行 3 回・約 0.5 秒の後、`Discarding message`（Information）で捨てる。Wolverine は上限を変えていない（IL のメタデータで確認）。
  - 手順 1 の「再起動が無ければ欠落は起きていない」の断定を改め、**既定で埋め直す**形にした。警報の説明文（4 か所と promtool の期待値）も合わせた。

