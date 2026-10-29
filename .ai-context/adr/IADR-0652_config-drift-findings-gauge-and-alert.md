---
title: IADR-0652 構成ドリフトの検出結果は計数ではなくゲージ config.drift.findings（直近の不一致の件数）にし、警報 ConfigDriftDetected は job ごとの max > 0 で鳴らす
type: impl-adr
status: Accepted
related_ids: [FR-15, NFR-21, ADR-0018, ADR-0053, IADR-0029, IADR-0592, IADR-0593]
author: claude
created: 2026-10-30
updated: 2026-10-30
related_specs:
  - ../specs/20261030_2143_config-drift-alert.md
---

# IADR-0652: 構成ドリフトの計器と警報（#2143）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-30
- 決定者: claude（#2143。親 #2047 の指摘 L-13）

## 起点・関連

- 起点 issue: #2143。計画 ADR-0018（構成ドリフトの検出と警告）、ADR-0053（運用の警報）。
- 同じ型の先例: [IADR-0592](./IADR-0592_dead-letter-counter-and-alert.md)（デッドレターの計数と警報）。
- 作業仕様書: [20261030_2143_config-drift-alert](../specs/20261030_2143_config-drift-alert.md)。

## 決定

1. **計器はゲージ `config.drift.findings` にする**（Prometheus では `config_drift_findings`）。値は直近の検出の不一致の件数で、0 が正常。
   - 検出の前は系列を出さない。
   - `HasDrift` なのに findings が空のときは 1 と数える。
2. 記録は `DriftRunner`（定期と PostSync の単一の経路）で、検出のたびに差し替える。検出が失敗した回は値を変えない。
3. Meter（`microservices-platform.config-inspection`）の収集の宣言は `AddPlatformConfigInspection` に置く（計器の登録と同じ場所）。
4. 警報 `ConfigDriftDetected` の式は `max by (job) (config_drift_findings) > 0` で、for 5m・severity warning とする。
   - 置き場所は経路 A・経路 B・Grafana の 2 写しである。
   - Grafana は `noDataState: OK` とする。

## 却下した案

- **計数（不一致を見つけるたびに +1）と `increase(...[1h]) > 0`**:
  - ドリフトは直すまで続く状態である。計数だと、窓（1 時間）が過ぎれば直っていなくても収まる。
  - 加えて、最初の 1 回で鳴らすために初回の腕（[IADR-0593](./IADR-0593_first-series-arm-for-zero-normal-alerts.md)）が要る。
  - ゲージなら、直すまで鳴り、直せば収まる。
- **ログからの抽出だけで鳴らす（Loki のルール）**: 警報の経路が Prometheus / Grafana の 2 系統に割れる。他の「0 が正常」の警報と同じ器に揃える。

## 帰結・残余リスク

- BFF の検出が止まると、ゲージは最後の値のまま残る（プロセスが生きていれば）。
  - 検出の停止そのものは警報にしていない。検出の失敗は Error のログで残る。
  - BFF が落ちれば系列が途絶え、鳴らない。BFF の停止は可用性の警報が拾う。
- promtool の試験は CI でだけ走る（手元に promtool が無い）。
