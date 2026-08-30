---
title: 作業仕様書 — Alertmanager の配備と、経路B の scrape 断の是正（#546）
type: spec
status: done
related_ids:
  - NFR
  - NFR-21
  - SC-10
  - ADR-0006
  - ADR-0044
  - IADR-0164
  - IADR-0165
  - IADR-0304
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md (§LLM 費用の上限アラートと暫定の統制・決定 39〜42)
related_specs:
  - "20260810_issue-665_grafana-alerting.md"
issue: "#546"
---

# 作業仕様書 — Alertmanager の配備（#546）

## 目的と射程

`ADR-0006` は「アラートは Alertmanager を用いる」と定めているが**本体が未配備**で、
`prometheus.yml` の `alertmanagers.targets` は compose・経路B の**両方とも空**だった。
5 つの SLO ルールは評価されるだけでどこへも出ていかなかった。**これを塞ぐ。**

## 🔴 射程から外したもの —— 月次予算の上限アラート

issue の主題は「SC-10 から LLM コストの KPI カードを外すと予算超過に気づく手段がゼロになる」である。
素直に読めば「`llm_cost_total` に月次予算のしきい値を置く」が答えに見える。**採らない。**

計画 `05_observability-ops` §LLM 費用の上限アラートと暫定の統制（**利用者裁定 2026-08-08**）が明示的に禁じている:

> **月次予算の金額（しきい値）は定めない。実測を待って確定する。** 稼働実績が無い段階で置いた絶対額は、
> 超過しても過少でも判断の根拠にならない。**暫定期間は絶対額のしきい値を持たず、前月比の増減と月内累計の推移で見る。**

同節はさらに「**しきい値が定まるまで、上限アラートは配備しても配線できず**」と、
本作業で起こる状態をそのまま予期している。**確定の前提は実績が数か月分そろうことである。**

したがって本作業が埋めるのは「**Alertmanager が未配備である**」という前提条件だけであり、
**#546 の「自動で気づく」部分は埋まらない。** 月次の手動確認（`IADR-0164`）が引き続き唯一の統制である。
相対比較（前月比の急増）を自動化する案も検討したが、**比率という別のしきい値が要る**ため同じ裁定に触れる。採らない。

## 決めたこと

判断の記録は [IADR-0304](../adr/IADR-0304_alertmanager-deployment-and-null-receiver.md)。要点:

1. compose と経路B の両方へ配備し、`targets` を実際に埋める（経路B は可観測性オーバーレイ＝ `OBSERVABILITY=1` の opt-in）
2. **既定の受信先は `default-null`（どこへも送らない）**。空の `receivers` は起動を拒否されるので 1 つは要るが、
   そこへ本物でない宛先を書くと**設定ファイルの見た目が「配線済み」になる**
3. 抑止規則は `critical` → `warning` の 1 本だけ
4. silence / notification log は永続化しない（`emptyDir`。消えたときの向きは fail-safe 側）
5. 月次予算のしきい値は置かない（上記）

## 配備して初めて分かったこと

🔴 **経路B の Prometheus は、唯一の scrape 対象へ最初から到達できていなかった。**

```console
$ kubectl -n platform-infra get svc otel-collector \
    -o jsonpath='{range .spec.ports[*]}{.name}:{.port}{"\n"}{end}'
otlp-grpc:4317
otlp-http:4318

$ ... /api/v1/targets
http://otel-collector:8888/metrics | down | context deadline exceeded
```

**Service にも Deployment にも 8888 が無い。** compose 側は `8888:8888` を公開しており**正しかった**
（乖離は経路B だけ）。結果 `up{job="otel-collector"}` は恒常的に 0 で、**`OtelCollectorDown` は鳴り続けていた。**

**Alertmanager が無かったので誰も気づかなかった。** 配備した瞬間、これは
`IADR-0165` 決定 5 が防ごうとしている「既知の誤報」そのものになる。**同一 PR で塞ぐ。**

## 受け入れ基準

- [x] compose・経路B の両方で `alertmanagers.targets` が到達可能な Alertmanager を指す
- [x] Prometheus が Alertmanager を認識する（`/api/v1/alertmanagers` の `activeAlertmanagers` が 1 件）
- [x] **実ルールの発火が Alertmanager へ届く**（合成ルールではなく `deploy/prometheus/alerts.yml` の 5 件のいずれか）
- [x] 経路B の scrape 対象が `up` になり、常時発火していた `OtelCollectorDown` が `inactive` へ戻る
- [x] `node scripts/check-grafana-alerting.js` が緑（ルールを増やしていないので 5 対 5 のまま）
- [x] `kubectl kustomize` が両オーバーレイで通る
- [ ] **実環境の受信先（メール / チャット）へテスト通知が届く** —— **利用者の判断が要る。本作業では満たせない**

## 実測（2026-08-30・稼働クラスタ `rancher-desktop` / k3s v1.35.4+k3s1）

### 1. Prometheus が Alertmanager を認識した

```console
$ kubectl -n platform-infra exec deploy/prometheus -- wget -qO- http://localhost:9090/api/v1/alertmanagers
{"status":"success","data":{"activeAlertmanagers":[{"url":"http://alertmanager:9093/api/v2/alerts"}],
 "droppedAlertmanagers":[]}}
```

### 2. 実ルールの発火が Alertmanager に届いた（**合成ルールではない**）

配備直後、`OtelCollectorDown`（下記 3 の欠陥により恒常発火していた）が Alertmanager に現れた。

```console
$ ... /api/v1/rules
OtelCollectorDown  state=firing alerts=1

$ kubectl -n platform-infra exec deploy/alertmanager -- wget -qO- http://localhost:9093/api/v2/alerts
count: 1
  OtelCollectorDown | status: active | startsAt: 2026-08-30T04:18:02.387Z
```

**運用仕様書の閉じる条件 3「下表 5 ルールの発火が Alertmanager 経由で通知されることを 1 件以上、実際に確かめた」
は、この実測で満たした。** 疎通確認用の合成ルールを一時的に入れる案も試したが、
**実ルールが現に発火していたので不要になった**（合成ルールは撤去し、ConfigMap は宣言状態へ戻した）。

### 3. scrape 断を塞いだ後（before / after）

```console
$ ... /api/v1/targets        # 是正後
http://otel-collector:8888/metrics | up | (no error)

$ ... /api/v1/rules          # 是正後
  OtelCollectorDown                inactive
  ServiceRequestMetricsAbsent      inactive
  HighHttp5xxRate                  inactive
  SearchLatencyP95High             inactive
  RagLatencyP95High                inactive
```

## 主張の限界（実測を超えて言わない）

- **「通知が人に届く」ことは確かめていない。** 確かめたのは
  **Prometheus → Alertmanager** までである。その先は受信先が `default-null` なので存在しない。
- **Grafana の暫定経路は閉じない。** 閉じる条件 2 が未達である。併存させるが、
  **暫定側にも宛先が無いため二重通知は構造的に起き得ない**（併存を禁じた理由は現時点では働かない）。
- **本番像（`deploy/helm/`）に Prometheus / Alertmanager は無い。** 本作業の射程は compose と経路B に閉じる。
