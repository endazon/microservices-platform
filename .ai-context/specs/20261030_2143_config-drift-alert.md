---
title: 作業仕様書 — 構成ドリフトの検出結果をゲージ config.drift.findings にし、警報 ConfigDriftDetected を有効にする（#2143）
type: spec
status: done
related_ids: [FR-15, NFR-21, ADR-0018, ADR-0053, IADR-0652, IADR-0029, IADR-0592]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2143"
---

# 作業仕様書 — 構成ドリフトの警報（#2143）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `e760d82e`）。

## 現状（develop で確かめた）

- 不一致の通知は、`LoggingDriftAlertSink` が構造化ログ（`ConfigDrift=true` の Warning）へ出すだけである。
- `deploy/prometheus/alerts.yml` の `ConfigDriftDetected` はコメントアウトのままで、元になる計器（`knowledge_platform_config_drift_warnings`）は存在しない。
- 検出は BFF の `DriftRunner` が単一の経路である。呼び元は次の 2 つ。
  - 定期（`DriftDetectionHostedService`。起動直後に 1 回、以後 5 分ごと）
  - ArgoCD PostSync の即時検出（`POST /internal/config/drift-run`）

## 方針

- **計器はゲージにする**（`config.drift.findings`。Prometheus では `config_drift_findings`）。値は直近の検出で見つかった不一致の件数である。
  - ドリフトは「状態」である。計数の前進（`increase`）だと、窓が過ぎると直っていなくても収まってしまう。ゲージなら直すまで鳴り続け、直せば次の検出で 0 に戻って収まる。
  - 検出が 1 度も走っていない間は系列を出さない（0 と「まだ見ていない」を混ぜない）。
  - 判定は `HasDrift` が正である。`HasDrift` なのに findings が空のときは 1 と数える。
  - #2097 の「初回の腕」は計数の話なので、ゲージには要らない。最初の検出でドリフトが無くても 0 を出す。
- 記録するのは `DriftRunner`（単一の経路）である。計器の登録と Meter の収集の宣言（`AddMeter`）は `AddPlatformConfigInspection` に置く。BFF は既にこれを呼んでいる。
- 警報は `ConfigDriftDetected` で、`max by (job) (config_drift_findings) > 0` が for 5m 続くと鳴る。BFF はレプリカごとに検出するので、job ごとの max を採る。
- 警報は 4 か所に同じ形で置く。
  - `deploy/prometheus/alerts.yml`（経路 A）
  - `deploy/local/observability/prometheus.yaml`（経路 B）
  - Grafana の `slo-alerts.yaml`
  - `deploy/local/observability/grafana.yaml` の写し
- 場面は `deploy/prometheus/tests/config-drift.test.yml`（promtool）で固定する。

## 母集合（規則 9・10）

- **規則 9**: `git grep -n "ConfigDriftDetected\|knowledge_platform_config_drift\|構成ドリフト"` で走査した（docs・deploy・scripts）。
  - 追随したもの:
    - `alerts.yml` の前書きの注意とコメントアウト
    - 運用仕様書の 4 か所（§適用直後のドリフト即時検出・follow-up・展開の残件・障害対応の表）
  - 凍結記録（`.ai-context/`）は書き換えない。
- **規則 10**（導出値は計算し直す）: Grafana のルールの件数を計算し直した。`grep -c "^      - uid:" slo-alerts.yaml` が 27 で、1 件足して 28 になる。合わせて直した箇所は次のとおり。
  - `slo-alerts.yaml` と `grafana.yaml` の件数と内訳
  - `check-grafana-alerting.js` の注記
  - 検査器の実測（Prometheus 28 / Grafana 28）が一致した。

## 受け入れ基準 → 試験

| 基準 | 試験 | 修正前 |
| --- | --- | --- |
| ドリフト検出で計器が差し替わる（検出前は系列なし・件数・直れば 0・HasDrift で findings が空なら 1） | `DriftDetectionChainTests.検出のたびに不一致の件数をゲージへ差し替える` | 赤（コンパイル不可。計器が無い） |
| 計器の Meter が収集対象に入る | `ConfigInspectionGrpcRegistrationTests.Registers_the_drift_metrics_and_collects_its_meter` | AddMeter の名前を変える変異で赤（実測） |
| promtool の試験で警報が鳴る（現れて 5 分で鳴る・直れば収まる・片方のレプリカだけでも鳴る・系列なしは鳴らない） | `deploy/prometheus/tests/config-drift.test.yml` | — |

- 🔴 **promtool は手元に無い**。導入（取得）は自動モードで拒否されるので、手元では走らせていない。
  - CI の `static-checks-units`（`promtool test rules deploy/prometheus/tests/*.test.yml`）で走る。
  - 手元で確かめたのは次の 2 つである。
    - YAML として読めること
    - 検査器（`check-grafana-alerting` / `check-prometheus-alerts-parity` / `check-grafana-provisioning-parity`）が 28 件で一致すること
