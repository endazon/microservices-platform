---
title: 「0 が正常」の警報（increase(...) > 0）が計器の系列が初めて現れたときにも鳴るよう、4 本の式に系列ごとの初回の腕を足し、promtool の単体試験で固定する（#2097）
type: spec
status: done
related_ids: [NFR-21, ADR-0006, ADR-0053, ADR-0085, ADR-0115, ADR-0127, IADR-0593, IADR-0592, IADR-0567, IADR-0473, IADR-0420]
author: claude
created: 2026-10-23
updated: 2026-10-23
issue: "#2097"
---

# 仕様書: 「0 が正常」の警報の初回（#2097）

> 本仕様書は実装着手前に作成した（着手 2026-10-23）。実装の基点は MSP `origin/develop` `bfca304c`（#2096 のマージ直後）。判断は IADR-0593。

## 起点となる計画書（トレーサビリティ）

- 非機能: NFR-21（可観測性・警報）
- 計画 ADR: ADR-0006（可観測性）・ADR-0053（デッドレター到達の計器）・ADR-0085 決定 4（`project` 欠落の計器）・ADR-0115 決定 3（部門の同期の計器）・ADR-0127 決定 1（取り込みの総枠）
- 関連 IADR: IADR-0592 決定 3（`DeadLetterReached` の初回の腕。本変更で形を改める）・IADR-0420・IADR-0473・IADR-0567（各警報の元の判断）

## 課題

OTel の累積カウンタは最初の `Add` で系列が立ち、最初の標本が 1 になる。`increase()` は窓に前の標本が無い系列を 0 とみなすので、
`increase(m[1h]) > 0` だけの式は**最初の 1 件で鳴らない**。issue は promtool v2.52.0 で 3 本の不発を再現している。

## 母集合（規則 9: issue の列挙を使わず、4 か所を全走査した）

走査: `deploy/prometheus/alerts.yml`・`deploy/local/observability/prometheus.yaml`・`deploy/grafana/provisioning/alerting/slo-alerts.yaml`・
`deploy/local/observability/grafana.yaml` のアラートルール（各 27 本。4 か所で同じ集合）から、`increase(` または `rate(` を含む式を拾った
（`grep -nE 'increase\(|rate\('`）。`grafana.yaml` のダッシュボードのパネル（`$__range` の `increase`）は警報ではないので対象外。

| 警報 | 式の形 | 判定 | 理由 |
| --- | --- | --- | --- |
| UnitDocumentsMissingProjectAttribute | `increase(m[1h]) > 0` | **対象** | 0 が正常。最初の違反で系列が立つ |
| IngestionBudgetExhausted | `sum by (target) (increase(m{…}[1h])) > 0` | **対象** | 0 が正常。最初の使い切りで系列が立つ |
| DepartmentSyncNotCorrecting | 3 項の `sum(increase(…[1h])) or vector(0)` の和 `> 0` | **対象**（3 項とも） | 0 が正常。失敗・中断・列挙の未完了は起きたときだけ系列が立つ（`RecordUsers` は `count > 0` のときだけ `Add`） |
| DeadLetterReached | 初回の腕つき（組ごと） | **対象（形の是正）** | 組ごとの `unless … offset 1h` は、再起動で `instance` が替わった新しい系列の 1 件目を落とす（下の実測） |
| LlmMonthlyBudgetExceeded | `increase(llm_cost_total[30d]) > 予算` | 除外 | 閾値は予算で 0 ではない（0 が正常の器ではない）。落ちるのは系列ごとの最初の標本（最初の 1 回の呼び出しの費用）だけで、30 日の累計に対して過小側に小さい。IADR-0593 §残余 |
| HighHttp5xxRate | `rate(5xx)/rate(全体) > 0.05` | 除外 | 割合の閾値。単発ではなく持続する率を見る。新しい 5xx の系列は最初の標本だけが落ち、続く失敗は数えられる |
| ServiceRequestMetricsAbsent | `rate(...) == 0` | 除外 | 沈黙を鳴らす器（0 が異常） |
| SearchLatencyP95High・RagFirstTokenP95High・RagLatencyP95High | `histogram_quantile(...) > 秒` | 除外 | 遅延の閾値。0 が正常の器ではない |

`increase`・`rate` を含まない残りの 17 本（`absent`・`absent_over_time`・ゲージの比較）は対象外。

## 実測（promtool v2.52.0。配備の Prometheus と同じ版）

- 旧い式 4 本に「系列が 10 分目に値 1 で初めて現れる」を与えると、Unit・Ingestion・DepartmentSync（3 項とも）が鳴らない（issue の再現と一致）。
- **`DeadLetterReached`（#2096 の形）にも穴がある**: 古いプロセス（`instance="a"`）の系列が 1 時間以上前から在り、再起動の後の新しいプロセス
  （`instance="b"`）が 1 通目を数える場面で鳴らない。組（`job, messaging_message_type, error_type`）は 1 時間前に在ったので `unless` が落とし、
  新しい系列は標本が 1 つ目なので `increase` にも現れない。`instance` は OTel の `service.instance.id`（`AddService` の既定でプロセスごとに新しい GUID）を
  prometheusremotewrite が写したもので、**再起動のたびに替わる**。#2096 の場面 3（同じ `instance` で 0 へ戻る）は OTel の実際の挙動ではない。

## 受け入れ基準

1. **AC1**: 4 本とも、計器の系列が初めて 1 で現れたとき `for: 5m` の後に鳴り、前進が無いまま 1 時間を過ぎると収まる（恒常発火しない）。
2. **AC2**: 4 本とも、再起動で `instance` が替わった新しい系列の 1 件目で鳴る（古い系列が 1 時間以上前から在っても）。
3. **AC3**: 既存の系列の前進（2 件目以降）・リセット・途絶後の初回（#2096 の場面 2・3・5）では従来どおり鳴る。平常（系列が無い）・
   絞り込みの外の系列（取り込みの `content`・部門の同期の `corrected` / `completed`）では鳴らない。
4. **AC4**: 4 か所の式が一致し（`check-prometheus-alerts-parity`）、Grafana 版が評価器 `gt 0` と組んで発火し得る（`check-grafana-alerting`）。
5. **AC5**: 上を promtool の単体試験が固定し（CI の `static-checks-units` が既に `deploy/prometheus/tests/*.test.yml` を回す）、初回の腕を外す変異で赤になる。
6. **AC6**: 今後「0 が正常」の `increase(...) > 0` の警報を足したとき、初回の腕が無ければ `scripts.repo.test.js` が赤になる（同型の 2 回目なので機械で止める）。

## 方式

- 式は系列ごとに `(m unless m offset 1h) or increase(m[1h])`（1 時間前に無かった系列は計器の値、在った系列は前進量）を採り、集計してから外側の `> 0` 1 つで比べる。
  - 内側に比較（`increase(...) > 0 or ...`）を置かない: `sum(...)` が比較を包むと `check-grafana-alerting` が値域を解釈できない（実測で 3 件の「検証できない」）。
    `or` は左を優先するので、腕を左に置けば新しい系列は計器の値（≥ 1）、在った系列は前進量（0 を含む）になり、外側の `> 0` で足りる。
  - 外側の `> 0` は、計器の側で将来 `Add(0)` で系列を先に立てても（値 0 の新しい系列）誤って鳴らない守りを兼ねる。
- 計器の側で系列を 0 で先に立てる案は退ける（タグの組が増えるたびに漏れる。IADR-0592 決定 3 と同じ理由。詳細は IADR-0593）。
- 説明文（description）に「初めて現れた場合を含む」を足す（Unit・Ingestion。DeadLetter は既にある）。運用仕様書・指標仕様書の式の引用を追随させる。

## 変更対象（規則 9: 旧い式の文字列で全文書を走査して挙げた）

- 4 か所のルール（式・注記・説明文）
- `deploy/prometheus/tests/first-series-alerts.test.yml`（新規）・`deploy/prometheus/tests/messaging-dead-letter.test.yml`（場面 6 を追加・前書き）
- `scripts/scripts.repo.test.js`（#2043 の式の検査を新しい形へ・#2097 の検査を追加）・`.github/workflows/ci.yml`（注記のみ）
- `docs/operations/operations.md`・`docs/observability/knowledge-health-indicators.md`（式の引用と追記）
- 除外: `.ai-context/adr/IADR-0420・0473・0567・0592`・`.ai-context/specs/` の旧い式（凍結記録。書き換えない。IADR-0593 が後継の判断を持つ）・`CHANGELOG.md`（生成物）

## 変異（AC5）

| 変異 | 赤になった場面 |
| --- | --- |
| 4 本を develop の式へ戻す | Unit 場面 1・3／Ingestion 場面 1・3／DepartmentSync 場面 1・2・3／DeadLetter 場面 6 |
| DeadLetter から初回の腕を外す | DeadLetter 場面 1・5・6 |

## 検証

- `promtool check rules deploy/prometheus/alerts.yml`・`promtool test rules deploy/prometheus/tests/*.test.yml`（v2.52.0）
- `node scripts/check-prometheus-alerts-parity.js`・`node scripts/check-grafana-alerting.js`
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・文書検査器

## PoC 配備前の手順（閉場後）

配備は閉場後に行う。ルールの ConfigMap（経路 B）と Grafana の provisioning を当て直した後、`/api/v1/rules` で 4 本の式が新しい形であること、
`health: ok` であることを確かめる（発火の確認は計器を上げる事象が要るので、promtool の単体試験を正とする）。
