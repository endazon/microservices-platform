---
title: IADR-0304 Alertmanager を配備し、受信先は「どこへも送らない」を既定として明示する
type: impl-adr
status: Accepted
related_ids:
  - NFR-21
  - ADR-0006
  - IADR-0164
  - IADR-0165
  - IADR-0210
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/06_technical/05_observability-ops.md
---

# IADR-0304: Alertmanager の配備形と、受信先を持たない既定（#546）

- 状態: Accepted
- 日付: 2026-08-30
- 決定者: claude（実装）

## 起点・関連

- **NFR-21**（MTTR 30 分以内・障害検出 5 分以内）。計画 ADR: **ADR-0006**（アラートは Alertmanager を用いる）
- 実装 issue: **#546**（SC-10 から LLM コストの KPI カードが外れた帰結として、予算超過に自動で気づく手段がゼロになった）
- 先行: [IADR-0164](./IADR-0164_llm-cost-monthly-review-interim-control.md)（月次の手動確認）／
  [IADR-0165](./IADR-0165_grafana-interim-alerting.md)（Grafana 統合アラートへ検知だけ配線した暫定）
- 作業仕様書: [20260830_issue-546](../specs/20260830_issue-546_alertmanager.md)

## コンテキストと課題

`ADR-0006` は「アラートは Alertmanager を用いる」と定めているが、**本体が配備されていなかった**。
`prometheus.yml` の `alertmanagers.targets` は compose・経路B の**両方で空**であり、
5 つの SLO ルールは評価だけされてどこへも出ていかなかった。
`IADR-0165` は暫定として Grafana 統合アラートへ**検知と可視化まで**を配線したが、
**宛先は意図的に書いていない**（届かない宛先を書くと「配線した」と読めるため）。

したがって従来の気づき方は「**人が Grafana の Alerting 画面を見る**」だけだった。

## 決定

### 決定 1 — Alertmanager を compose と経路B の両方へ配備し、`targets` を実際に埋める

- compose: `deploy/alertmanager/alertmanager.yml` ＋ `docker-compose.yml` の `alertmanager` サービス
- 経路B: `deploy/local/observability/alertmanager.yaml`（可観測性オーバーレイの一部＝ **`OBSERVABILITY=1` の opt-in**）
- 両経路の `prometheus.yml` の `alertmanagers.targets` を `['alertmanager:9093']` にする

**可観測性オーバーレイに載せる**のは、Prometheus / Grafana と同じ opt-in 境界に置くためである。
アラートの送り手（Prometheus）が opt-in なのに受け手だけ既定で立つ形にはしない。

### 決定 2 — 既定の受信先は `default-null`（どこへも送らない）とし、それを設定として明示する

**空の `receivers` は Alertmanager が起動を拒否する。** 受信先を 1 つは書かねばならない。
そこへ**本物の宛先を書かない** —— `IADR-0165` 決定 3 と同じ理由である。
届かない宛先（実在しない SMTP・ダミーの Webhook）を書くと、**設定ファイルの見た目が「配線済み」になる。**

`default-null` は名前で「送り先を持たない」ことを宣言する。**設定漏れではなく既定である**と読める形にする。

🔴 **この決定の帰結として、本 ADR は `IADR-0165` の暫定を閉じない。**
運用仕様書が定める閉じる条件 3 つのうち、**条件 2（受信先が設定され、テスト通知が実際に届いた）が満たされない**。
実環境のメール / チャットの宛先は**利用者が決める事柄**であり、実装側で代替値を置けない。

### 決定 3 — 抑止規則は `critical` → `warning` の 1 本だけ置く

同じ対象について critical が出ている間、warning を抑止する（`alertname` ＋ `service_name` が一致するもの）。
**二重通知は「片方は既知の誤報だ」という運用習慣を生み、本物の通知を握り潰す方向に働く**
（`IADR-0165` 決定 5 と同じ判断）。それ以上の抑止規則は実績が無いので置かない。

### 決定 4 — silence / notification log は永続化しない（経路B は `emptyDir`）

再起動で silence が消えることを受け入れる。dev の射程であり、
消えたときの失敗の向き（**抑止していたものが再び鳴る**）は fail-safe 側である。

### 決定 5 — 単価の絶対額しきい値（月次予算のアラート）は**置かない**

**計画が明示的に禁じている。** `05_observability-ops` §LLM 費用の上限アラートと暫定の統制（利用者裁定 2026-08-08）:

> **月次予算の金額（しきい値）は定めない。実測を待って確定する。** 稼働実績が無い段階で置いた絶対額は、
> 超過しても過少でも判断の根拠にならない。

したがって **#546 の「予算超過に気づく手段」のうち、自動検知の部分は本 ADR では埋まらない。**
埋まるのは「Alertmanager が配備されていない」という前提条件だけである。
**月次の手動確認（`IADR-0164`）は引き続き唯一の統制である。**

## 配備して初めて分かったこと（実測 2026-08-30）

🔴 **経路B の Prometheus は、唯一の scrape 対象へ最初から到達できていなかった。**

`otel-collector` の Deployment は `containerPort: 8888` を持たず、Service も 8888 を公開していなかった
（compose 側は `8888:8888` を公開しており**正しかった** —— 乖離は経路B 側だけ）。
その結果 `up{job="otel-collector"}` は恒常的に 0 で、**`OtelCollectorDown` は鳴り続けていた**。

**Alertmanager が無かったので、誰も気づかなかった。** 鳴り続けるアラートは、配備した瞬間に
決定 3 が防ごうとしている「既知の誤報」そのものになる。**同一 PR で塞ぐ。**

- Deployment に `containerPort: 8888`、Service に `port: 8888` を足す
- collector の `service.telemetry.metrics.address` を `0.0.0.0:8888` へ**明示する**（compose 側にも同じ宣言を置く）
  —— collector は版によって既定が `localhost:8888` と `:8888` の間で変わっており、
  **既定任せだと版上げで静かに scrape 不能になる**

**検査器は足さない。** 「同型の事故が 2 回起きたら」の規約に従い、1 回目は記録に留める。

## 結果

- **良い影響**: 5 つの SLO ルールの発火が実際に Alertmanager へ届くようになった（実測で確認）。
  経路B の唯一の scrape 対象が復旧し、常時発火していた偽アラートが消えた。
- **悪い影響 / トレードオフ**:
  - **通知はまだ人に届かない。** 届くのは Alertmanager の画面までである。
    気づくまでの時間は、依然として見に行く間隔に等しい。
  - **Grafana の暫定経路と併存する。** `IADR-0165` が避けたかった状態だが、
    **暫定側に宛先が無い以上、二重通知は起き得ない**ため実害は無い。閉じるのは条件 2 が満たされたときである。
- **フォローアップ**:
  1. **実環境の受信先（メール / チャット）を設定し、テスト通知が届くことを確かめる**（利用者の判断が要る）。
     満たされた時点で `deploy/grafana/provisioning/alerting/` と `scripts/check-grafana-alerting.js` を**同時に**削除する。
  2. **月次予算のしきい値**は計画側の未決事項である（実測数か月分が前提）。確定したら上限アラートを足す。
  3. 本番像（`deploy/helm/`）に Prometheus / Alertmanager は無い。stg/prod への展開は別途。
