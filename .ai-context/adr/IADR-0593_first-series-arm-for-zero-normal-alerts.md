---
title: IADR-0593 「0 が正常」の警報（increase(...) > 0）は系列ごとに初回の腕（(m unless m offset 1h) or increase(m[1h])）を持ち、集計の後の外側の > 0 だけで比べる。DeadLetterReached の組ごとの腕（IADR-0592 決定 3）もこの形へ改める
type: impl-adr
status: Accepted
related_ids: [NFR-21, ADR-0006, ADR-0053, ADR-0085, ADR-0115, ADR-0127, IADR-0592, IADR-0567, IADR-0473, IADR-0420]
author: claude
created: 2026-10-23
updated: 2026-10-23
related_specs:
  - ../specs/20261023_2097_first-series-zero-normal-alerts.md
---

# IADR-0593: 「0 が正常」の警報の初回の腕（#2097）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-23
- 決定者: claude（#2097。#2043 の PR の AI レビュー 🟡 から派生）

## 起点・関連

- 起点 issue: #2097。直前の判断: [IADR-0592](./IADR-0592_dead-letter-counter-and-alert.md) 決定 3（`DeadLetterReached` の初回の腕。**本 IADR がその式の形を改める**）。
- 関連する計画書 ID: NFR-21。計画 ADR: ADR-0006・ADR-0053・ADR-0085 決定 4・ADR-0115 決定 3・ADR-0127 決定 1。
- 各警報の元の判断: [IADR-0420](./IADR-0420_unit-subject-identification-and-project-missing-metric.md)（`UnitDocumentsMissingProjectAttribute`）・
  [IADR-0473](./IADR-0473_department-attribute-follows-department-group.md)（`DepartmentSyncNotCorrecting`）・
  [IADR-0567](./IADR-0567_ingestion-budget-exhausted-alert.md)（`IngestionBudgetExhausted`）。
- 作業仕様書: [20261023_2097_first-series-zero-normal-alerts](../specs/20261023_2097_first-series-zero-normal-alerts.md)（母集合・除外の理由・変異）。

## コンテキストと課題

「0 が正常で、1 件でも起きたら鳴らす」警報は `increase(m[1h]) > 0` で判定している。計器（OTel の累積カウンタ）は最初の `Add` で系列が立ち、
最初の標本が 1 になる。`increase()` は窓に前の標本が無い系列を 0 とみなすため、**最初の 1 件では鳴らない**（守りたいのはまさにその初回）。
issue は `UnitDocumentsMissingProjectAttribute`・`IngestionBudgetExhausted`・`DepartmentSyncNotCorrecting` の不発を promtool v2.52.0 で再現した。

加えて本作業の実測で、IADR-0592 決定 3 の `DeadLetterReached` の腕（`sum by (L) (m) unless sum by (L) (m offset 1h)`。**組ごと**）にも穴があった。
系列の `instance` は OTel の `service.instance.id`（`AddService` の既定でプロセスごとに新しい GUID）を prometheusremotewrite が写したもので、**再起動のたびに替わる**。
古いプロセスの系列が 1 時間以上前から在る組で、再起動の後の新しいプロセスが 1 通目を数えると、組は 1 時間前に在ったので `unless` が落とし、
新しい系列は標本が 1 つ目なので `increase` にも現れない（promtool で不発を確認）。

## 検討した選択肢

1. **式の側で、系列ごとに「1 時間前に無かった系列は計器の値、在った系列は前進量」を採る（採用）**:
   `sum by (L) ((m unless m offset 1h) or increase(m[1h])) > 0`。
2. **IADR-0592 決定 3 の形（組ごとの腕・内側の `> 0`）をそのまま 3 本へ写す**: `sum by (L) (increase(m[1h])) > 0 or (sum by (L) (m) unless sum by (L) (m offset 1h))`。
3. **計器の側で、起動時に既知のタグの組へ `Add(0)` して系列を 0 で先に立てる**。

## 決定

選択肢 1 を採る。「0 が正常」の 4 本（`UnitDocumentsMissingProjectAttribute`・`IngestionBudgetExhausted`・`DeadLetterReached`・`DepartmentSyncNotCorrecting` の 3 項）を
次の形にし、Prometheus（compose・k8s）と Grafana（compose・k8s）の 4 か所に同じ式で置く。

```promql
# 集計する警報（L は通知のラベル）
sum by (L) ((m unless m offset 1h) or increase(m[1h])) > 0
# 集計しない警報（UnitDocumentsMissingProjectAttribute）
((m unless m offset 1h) or increase(m[1h])) > 0
# 項を足す警報（DepartmentSyncNotCorrecting。3 項とも）
(sum((m1 unless m1 offset 1h) or increase(m1[1h])) or vector(0)) + … > 0
```

- **腕は集計の前（系列ごと）に置く。** 再起動で `instance` が替わった新しい系列の 1 件目を拾うため（上の課題）。
- **腕を `or` の左に置き、比較は外側の `> 0` 1 つだけにする。** `or` は左を優先するので、新しい系列は計器の値（≥ 1）、在った系列は前進量（0 を含む）になる。
  内側に比較を置く形（`increase(...) > 0 or ...`）は `sum(...)` が比較を包み、`check-grafana-alerting` の値域の解釈が「検証できない」を返す（実測 3 件）。
  外側の `> 0` は、将来計器の側で値 0 の系列を先に立てても誤って鳴らない守りを兼ねる。
- **窓は `increase` と同じ 1 時間**（腕は系列が現れてから 1 時間だけ立ち、その後は前進の有無に引き継ぐので恒常発火しない）。
- **Grafana 版の評価器は `gt 0` のまま**（expr の `> 0` の後に残る値は前進量か初回の計器の値＝正の数）。`noDataState: OK` も変えない。
- **試験**: `deploy/prometheus/tests/first-series-alerts.test.yml`（Unit・Ingestion・DepartmentSync。初回・前進・再起動で instance が替わった初回・絞り込みの外・平常）と
  `deploy/prometheus/tests/messaging-dead-letter.test.yml` の場面 6（再起動で instance が替わった初回）を、CI の `static-checks-units` が配備と同じ版の promtool で回す。
  develop の式へ戻す変異で各警報の初回の場面が、DeadLetter から腕を外す変異で場面 1・5・6 が赤になることを確かめた。
- **機械検査**（同型の 2 回目なので置く）: `scripts.repo.test.js` の #2097 が、4 か所のうち `increase(` を含み外側の `> 0` で比べる式は、どの `increase(SEL[W])` も
  `(SEL unless SEL offset W) or increase(SEL[W])` を持つこと、内側に比較を置かないこと、計器ごとに「初めて 1 で現れる」場面の promtool 試験があることを見る。
  `rate(...) > 0` の形が現れたら赤にする（初回の扱いを決めてから広げる）。

## 理由

- **選択肢 2 は再起動の後の初回を落とす**（上の実測）。0 が正常の計器は稀にしか前進しないので、古いプロセスの系列が 1 時間以上前から在る状態は普通に起きる
  （以前に 1 件あった組で、配備や再起動の後に次の 1 件が起きる）。
- **選択肢 3 はタグの組が閉じていない計器で漏れる**（IADR-0592 決定 3 と同じ理由）。例外の型（`error_type`）・書き手のクライアント（`documents_client_id`）は事前に列挙できず、
  組を足し忘れた初回だけが黙って鳴らなくなる。式の側で塞げば、計器を足すたびの列挙が要らない。

## 結果

- **良い影響**: 4 本とも、計器の系列が初めて現れた最初の 1 件で鳴る（再起動でプロセスが替わった後の初回を含む）。
- **悪い影響 / トレードオフ**: 式が長くなる（`m` を 3 回書く）。読み手のために各群の注記と運用仕様書に形の意味を書いた。

### 残余（閉じていないもの。黙って広げない）

1. **`offset 1h` の時点の欠け**: 1 時間前の時点（`offset 1h` の 5 分の探索幅）に標本が無い系列は「1 時間前に無かった」とみなされる。collector か remote write が
   10 分以上止まった 1 時間後、前から在る（値が 1 以上の）系列が初回として鳴り得る。IADR-0592 の形と共通の性質で、止まったこと自体は `OtelCollectorDown` が先に鳴らす。
2. **`LlmMonthlyBudgetExceeded`**（`increase(llm_cost_total[30d]) > 予算`）は対象外とした。閾値は予算で 0 ではなく、落ちるのは系列ごと（プロセスごと）の最初の標本
   （最初の 1 回の呼び出しの費用）だけである。30 日の累計に対して過小側に小さく、金額の正確さは単価の未解決（IADR-0466）の方が大きく効く。
3. **`HighHttp5xxRate`**（率の閾値）は対象外とした。持続する失敗の割合を見る器で、新しい 5xx の系列は最初の標本だけが落ち、続く失敗は数えられる。
