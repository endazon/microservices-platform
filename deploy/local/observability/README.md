# 経路B ローカル可観測性スタック（opt-in）

> 起点: [ADR-0006](../../../.ai-context/adr/IADR-0077_local-observability-vault-gitops-overlays.md) / IADR-0077（AST#24）

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
| `grafana.yaml` | Grafana（datasource=Prometheus/Loki/Tempo・Keycloak OIDC 認証／local admin フォールバック・IADR-0090） |
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

**OIDC ログインの実効経路は edge（`LOCALEDGE=1`・IADR-0091 PR-2）**。`GF_SERVER_ROOT_URL` を
`https://grafana.localhost:50000/` にしているため、Grafana は `redirect_uri` を一意に edge URL で生成する。

```sh
# 推奨: エッジ集約経由（LOCALEDGE=1 で起動しておく。deploy/local/edge/README.md）
#   → https://grafana.localhost:50000
# 素の port-forward（下記）だけでは OIDC は完了しない（後述）:
kubectl -n platform-infra port-forward svc/grafana 3000:3000   # http://localhost:3000
```

- **SSO ログイン**: `https://grafana.localhost:50000` を開き「Sign in with Keycloak」→ realm ユーザー（例 `developer`/`developer`）。
  role マッピングは realm ロール由来: `platform-admin`→Admin / `platform-operator`→Editor / それ以外→Viewer。
- ⚠️ **port-forward 単独（`LOCALEDGE` 未使用）では OIDC は成立しない**: 認証後の redirect が `grafana.localhost:50000`
  を指すため edge 未起動だと到達できず、ログインは完了しない → **fail-safe の local admin へ落ちる**（下記）。
  port-forward で OIDC したい場合は `GF_SERVER_ROOT_URL` を `http://localhost:3000/` に戻す（realm に旧 redirect 登録済み）。
  詳細は [`deploy/local/edge/README.md`](../edge/README.md) の「OIDC（集約後 URL）」。
- **issuer 整合（#284 手順A）**: Grafana は auth/token/userinfo を `http://keycloak:8080/realms/platform`
  で解決する。browser も `keycloak:8080` を解決できるよう hosts 追記＋`port-forward svc/keycloak 8080:8080` を行う
  （`deploy/local/README.md`「エッジ経路（/bff・ブラウザ OIDC）」手順A と同一の理由：iss 一致）。
- **フォールバック（fail-safe）**: OIDC 未設定/失敗時も匿名フルアクセスへは倒れない。Grafana 組み込みの
  **local admin**（dev 既定 `admin`/`admin`）でログインできる（`grafana-oidc` Secret は optional 参照のため
  未作成でも Pod は起動する）。
- **realm 反映**: `grafana` クライアントは `deploy/keycloak/microservices-platform-realm.json` に定義。realm を
  再インポート（`PERSIST=1` で永続化済みなら管理コンソールで追加 or 再作成）すると有効になる。

## 永続化（opt-in・`PERSIST=1` ＋ `OBSERVABILITY=1`・#787 / IADR-0210）

既定では **4 つとも volume が無く、Pod 再起動でメトリクス / ログ / トレース / Grafana 設定が全消失する**。
`PERSIST=1` を併用すると、対になるオーバーレイ
[`deploy/local/observability-persistence`](../observability-persistence/) が本オーバーレイを**置換**し、
`local-path` PVC を足す（`prometheus-data` 5Gi → `/prometheus` ／ `loki-data` 2Gi → `/tmp/loki` ／
`tempo-data` 2Gi → `/tmp/tempo` ／ `grafana-data` 1Gi → `/var/lib/grafana`）。

```sh
PERSIST=1 OBSERVABILITY=1 bash scripts/k8s-local-up.sh
# 直接当てるなら: kubectl apply -k deploy/local/observability-persistence
```

- **マウント先は上記 config の storage パスと一致させ、config は書き換えない**（IADR-0079 §3 の作法）。
  一致は `node scripts/k8s-local-up.test.js` が**両側から読んで**突き合わせる。
- **Pod は root へ落とさない**（4 種とも `securityContext` を付けない）。compose の `user: "0:0"` は
  **docker の named volume が root:root 0755 で生成される**ことへの対処で、**k8s へは転用できない** ——
  local-path provisioner は `mkdir -m 0777` で作る。実測で loki（uid 10001）/ tempo（10001）/ grafana（472）が
  非 root のまま書けている（IADR-0210 決定 6）。
- **PVC を掴む Deployment は `strategy: Recreate`**（RWO と RollingUpdate は両立しない。IADR-0210 決定 7）。
- 詳細は [`deploy/local/README.md`](../README.md) の「永続化」節と
  [IADR-0210](../../../.ai-context/adr/IADR-0210_local-k8s-observability-persistence.md)。

## 切り戻し

`kubectl delete -k deploy/local/observability` で撤去し、`kubectl apply -k deploy/local/infra` ＋ collector
rollout restart で debug-only（既定）へ戻す（永続化版を当てていたなら
`kubectl delete -k deploy/local/observability-persistence`。PVC は残るので消したいなら別途 `delete pvc`）。

## Tier 境界

本オーバーレイはローカル検証用。稼働率99%の実測・Alertmanager 実配線・**本番相当の**リテンション設計は
**Tier 3**（対象外）。

> ★ 混同しないこと（#787 / [IADR-0210](../../../.ai-context/adr/IADR-0210_local-k8s-observability-persistence.md)）:
> `prometheus.yaml` の `--storage.tsdb.retention.time=7d` / `--storage.tsdb.retention.size=4GB` は
> **dev ローカルの保持期間**であり、上の「本番相当のリテンション」ではない。本番像
> （`deploy/helm/microservices-platform/templates/`）には Prometheus / Loki / Tempo / Grafana が
> **1 つも存在せず**、本 overlay の設定はそこへ波及しない。**Tier 3 の対象外宣言はそのまま生きている。**
