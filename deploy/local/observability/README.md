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

## Grafana ログイン（Keycloak OIDC・IADR-0090・#353）

Grafana は **Keycloak OIDC(generic OAuth)** で認証する（匿名 Admin は廃止）。`OBSERVABILITY=1` の起動で
`scripts/k8s-local-up.sh` が client secret 用 Secret `grafana-oidc` を作成する（dev 既定 `grafana-dev-secret-change-me`・
`GRAFANA_OIDC_CLIENT_SECRET` env で上書き可・平文コミットなし）。

```sh
kubectl -n platform-infra port-forward svc/grafana 3000:3000   # http://localhost:3000
```

- **SSO ログイン**: ログイン画面の「Sign in with Keycloak」→ realm ユーザー（例 `developer`/`developer`）。
  role マッピングは realm ロール由来: `platform-admin`→Admin / `platform-operator`→Editor / それ以外→Viewer。
- **issuer 整合（#284 手順A）**: Grafana は auth/token/userinfo を `http://keycloak:8080/realms/microservices-platform`
  で解決する。browser も `keycloak:8080` を解決できるよう hosts 追記＋`port-forward svc/keycloak 8080:8080` を行う
  （`deploy/local/README.md`「エッジ経路（/bff・ブラウザ OIDC）」手順A と同一の理由：iss 一致）。
- **フォールバック（fail-safe）**: OIDC 未設定/失敗時も匿名フルアクセスへは倒れない。Grafana 組み込みの
  **local admin**（dev 既定 `admin`/`admin`）でログインできる（`grafana-oidc` Secret は optional 参照のため
  未作成でも Pod は起動する）。
- **realm 反映**: `grafana` クライアントは `deploy/keycloak/microservices-platform-realm.json` に定義。realm を
  再インポート（`PERSIST=1` で永続化済みなら管理コンソールで追加 or 再作成）すると有効になる。

## 切り戻し

`kubectl delete -k deploy/local/observability` で撤去し、`kubectl apply -k deploy/local/infra` ＋ collector
rollout restart で debug-only（既定）へ戻す。

## Tier 境界

本オーバーレイはローカル検証用。稼働率99%の実測・Alertmanager 実配線・本番相当のリテンションは **Tier 3**（対象外）。
