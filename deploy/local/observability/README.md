# 経路B ローカル可観測性スタック（opt-in）

> 起点: [ADR-0006](../../../docs/adr/IADR-0077_local-observability-vault-gitops-overlays.md) / IADR-0077（AST #24）

経路B（k8s）で Prometheus / Loki / Tempo / Grafana を `platform-infra` に立てる **opt-in オーバーレイ**。
既定（`deploy/local/infra` のみ）は otel-collector が **debug exporter のみ**＝外部送信なし（fail-safe）。
config は compose（`deploy/prometheus.yml`・`loki-config.yaml`・`tempo.yaml`・`otel-collector-config.yaml`・
`grafana/provisioning/datasources`）と同内容を inline する（kustomize の root 外参照制約に従う二重管理）。

## 構成

| ファイル | 役割 |
| --- | --- |
| `prometheus.yaml` | Prometheus（remote-write receiver 有効・alerts inline） |
| `loki.yaml` | Loki（ログ集約） |
| `tempo.yaml` | Tempo（トレース・OTLP 4317 受信） |
| `grafana.yaml` | Grafana（datasource=Prometheus/Loki/Tempo・dev 匿名 Admin） |
| `otel-collector-forward.yaml` | otel-collector を forwarding 構成へ差し替える ConfigMap（同名上書き） |

メトリクス経路: アプリ → OTLP → otel-collector → prometheusremotewrite / otlp tempo / loki push（push モデル）。

## 適用（opt-in）

```sh
kubectl apply -k deploy/local/observability
# collector を forwarding 構成へ反映（debug-only から切替）
kubectl -n platform-infra rollout restart deploy/otel-collector
```

`scripts/k8s-local-up.sh` は `OBSERVABILITY=1` で上記を実施する。ダッシュボードは Grafana UI から import する
（MSP overview=`deploy/grafana/provisioning/dashboards`、AST overview=`src/ai-stock-trading/deploy/observability/dashboards`）。

## 切り戻し

`kubectl delete -k deploy/local/observability` で撤去し、`kubectl apply -k deploy/local/infra` ＋ collector
rollout restart で debug-only（既定）へ戻す。

## Tier 境界

本オーバーレイはローカル検証用。稼働率99%の実測・Alertmanager 実配線・本番相当のリテンションは **Tier 3**（対象外）。
