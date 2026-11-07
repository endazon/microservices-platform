---
title: 作業仕様書 — クラウドの基盤の Pod の権限を最小にし（root でない・capability なし・PSA restricted）、collector の OTLP の受け口を送る Pod だけに絞って受け取りの上限と警報を足す
type: spec
status: in-progress
related_ids: [NFR-18, NFR-21, IADR-0697, IADR-0674, IADR-0675, IADR-0689, IADR-0693]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - 20261107_cloud-keycloak-tls.md
---

# 作業仕様書 — クラウドの基盤の Pod の権限を最小にする

> 着手 2026-11-07（develop `3d36d5ee9`）。判断の記録は IADR-0697。稼働中のクラスタ・外部のサービスには何もしない。秘密の値は扱わない
> （手元の試験の値はその場で乱数で作り、表示しない）。OpenBao（`deploy/cloud/vault/`）は別の作業（OpenBao の受け口の TLS）が扱うので触らない。

## 起点

- 要求 **NFR-18**（統制）・**NFR-21**（運用の監視）。
- 所有者の裁定（2026-11-07）: 稼働の前に中程度もすべて直す。基盤の Pod の capability を外し、OpenBao も TLS にする（後者は別の作業）。
- 前提: IADR-0674（クラウドの最小構成）、IADR-0675（警報と heartbeat）、IADR-0689（OpenBao・ESO）、IADR-0693（Keycloak の入りの名指しの型）。

## 現状（develop `3d36d5ee9` で読んだ）

| 場所 | 現状 |
| --- | --- |
| `infra/postgres.yaml` | initContainer・本体とも securityContext なし（root・既定の capability。NET_RAW を含む）。initContainer は root で鍵を `chown 70:70` |
| `infra/rabbitmq.yaml` | 同じ（postStart も root）。ServiceAccount のトークンが載る（`automountServiceAccountToken` が無い） |
| `infra/keycloak.yaml` | Pod は `fsGroup` だけ。コンテナは既定の capability |
| `infra/otel-collector.yaml` | securityContext なし |
| `observability/prometheus.yaml`・`alertmanager.yaml` | securityContext なし（イメージの利用者は名前の `nobody`） |
| `backup/cronjob-postgres.yaml` | seccomp と権限の昇格なしだけ。root・capability は既定 |
| `db-users/job.yaml` | 既に root でなく capability なし |
| `infra/namespaces.yaml` | 2 つの名前空間とも `enforce: baseline` |
| `infra/networkpolicy.yaml` の collector の入り | 取引ユニットの名前空間・MSP の名前空間の**全 Pod**から 4317・4318 |
| collector の設定 | `memory_limiter`（128MiB）だけ。受け取りの上限なし。拒否・途絶えの警報なし（`OtelCollectorDown` は scrape の `up` だけ） |

## 母集合（規則 9: 誤りの側の字面で走査した）

- Pod を描くもの: `kubectl kustomize deploy/cloud`（infra・backup・observability）・`deploy/cloud/db-users/job.yaml`・chart の values-cloud の描画・
  `deploy/cloud-transition/keycloak-tls-coexist`。`grep -rl 'kind: (Deployment|StatefulSet|Job|CronJob|DaemonSet)' deploy/cloud` で 9 ファイル。
  - 除外: `deploy/cloud/vault/openbao.yaml`（OpenBao。別の作業が受け口の TLS を扱う。名前空間 `openbao` の PSA もそちら）。ESO（helm。名前空間 `external-secrets`）。
- 名前空間の PSA を緩めずに当たる Pod: 上の描画の Pod のほか、手順書が `kubectl create job --from=cronjob/...`（同じ宣言）・`kubectl exec`（Pod を作らない）だけを使うことを
  `grep -n 'kubectl.*\(run\|create job\|debug\)' docs/operations/cloud-*.md deploy/cloud/README.md` で確かめた（`kubectl run` は無い）。取引ユニットの手順にも
  `platform-infra` へ Pod を作るものは無い（取引ユニットの develop `08634c6ab` を同じ字面で走査）。
- collector へ OTLP を送る Pod: 取引ユニットの develop（`08634c6ab`）の chart を values-cloud（＋ ci の digest の fixture）で描いて引いた。
  OTLP の宛先の env（`Otlp__Endpoint=http://otel-collector:4317`）を持つ Deployment は 11 個（audit・backtest・configuration・cost-control・
  information-collection・market-monitor・notification・order-execution・report・risk-management・trade-decision の `-service`）。持たないのは `opend` だけ。
  Job・CronJob は描かれない。MSP の chart の values-cloud の描画は LLM ゲートウェイだけで、宛先は同じく 4317。4318（HTTP）を宛先に持つ Pod は無い。
- 規則 10（この変更で新たに誤りになる自分の記述）: `rabbitmq.yaml` の起動の直後の復元の注記（「postStart は root で走る」）、`namespaces.yaml` の注記（baseline）、
  `networkpolicy.yaml` の冒頭の注記（collector ← 取引ユニット・MSP）、`deploy-cloud-postgres.test.js` の注記（initContainer を root で走らせる）を直した。

## 方式

1. 全 Pod: `runAsNonRoot`＋数の `runAsUser`（イメージの利用者: postgres 70・rabbitmq 100/101・keycloak 1000・collector 10001・Prometheus/Alertmanager 65534）、
   `allowPrivilegeEscalation: false`、`capabilities.drop: [ALL]`（足すものは無い）、`seccompProfile: RuntimeDefault`、`automountServiceAccountToken: false`。
   書く物が emptyDir だけのもの（collector・Prometheus・Alertmanager・initContainer）は `readOnlyRootFilesystem: true`。
2. Postgres・RabbitMQ は **fsGroup を置かない**（local-path の PVC にも所有の付け替えが効くと実測。データの dir・Erlang の cookie の mode が変わると起動しない）。
   鍵（Secret は root の所有・0440）は initContainer が **gid 0（root の利用者ではない）**で読み、自分の uid の所有の写しを作る（chown が要らない）。
   RabbitMQ のサービスごとのユーザーのパスワード（Secret `rabbitmq-users`。起動の直後の復元が uid 100 で読む）も同じ形で写す。
3. 名前空間 `platform-infra`・`microservices-platform` の PSA を enforce・warn・audit とも restricted。
4. collector の入りを「名前空間 かつ Pod」で 11 個＋LLM ゲートウェイ、4317 だけに絞る。gRPC の受け取りに 1 回の大きさ（4MiB）と同時の流れの数（16）の上限、
   HTTP の 1 回の大きさの上限、memory_limiter の spike を明示する。
5. 警報: `OtelCollectorRefusingData`（受け取りの拒否）・`OtelCollectorExportFailing`（remote write の失敗）・`OtlpMetricsPipelineSilent`（受け取り 0）・
   `OtlpSendersMissing`（送り手の数が一覧の 12 を下回る。`target_info` の job）・`PrometheusHeadSeriesHigh`（系列の数）。
6. 試験: `deploy-cloud.test.js`（H1〜H5・陰性対照・宣言のファイルの変異）、`cloud-ops.test.js`（promtool の単体試験）、`deploy-cloud-postgres.test.js`
   （宣言の securityContext を docker の旗へ写して実物で起動）。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| AC1 | クラウドの描画の全 Pod（initContainer を含む）が root でなく、capability を全部落とし（足す物なし）、権限の昇格を許さず、seccomp は RuntimeDefault、SA のトークンを載せない | `deploy-cloud.test.js` H1 |
| AC2 | NET_RAW を持つコンテナが無い | 同 H2 |
| AC3 | 2 つの名前空間の PSA は restricted | 同 H3 |
| AC4 | collector の OTLP の入りは取引ユニットの 11 個（OpenD を含まない）と LLM ゲートウェイだけ・4317 だけ | 同 H4 |
| AC5 | collector の受け取りの上限と 5 つの警報が在り、送り手の数の閾値は一覧と同じ。警報は鳴るときに鳴り平時は鳴らない | 同 H5・`cloud-ops.test.js` |
| AC6 | 修正を外すと赤（判定の陰性対照・宣言のファイルの変異・develop の描画） | 同（陰性対照・変異）・実測の記録 |
| AC7 | Postgres は宣言の securityContext のまま空のボリュームから起動し、鍵の扱いが成り立つ | `deploy-cloud-postgres.test.js` |

## 実測・記録

- 手元の docker（29.x）と k3s v1.35.10（docker の中。README の固定の版）で確かめた。稼働中のクラスタには触れていない。
- PSA: 描画の Pod の宣言を Pod にして、restricted の名前空間へ `kubectl apply --dry-run=server`。新しい描画（基盤 7・db-users・LLM ゲートウェイ・併存の重ね）は全部通り、
  develop の描画は 7 つとも拒まれる（`violates PodSecurity "restricted:latest"`）。
- local-path の PVC と fsGroup: `fsGroup: 70` の Pod で、PVC の中の 0700 の dir・0600 のファイルが、次の Pod の起動で `2770`・`0660`（gid 70）へ変わった。
- docker で宣言の securityContext を旗（`--user`・`--cap-drop ALL`・`no-new-privileges`・`--read-only`・`--group-add <fsGroup>`）へ写し、Secret（root の所有・defaultMode）と
  emptyDir（0777）を kubelet と同じ形で作って起動した（足場は scratchpad の使い捨て。値は乱数）:
  - Postgres: 初回の起動と初期化・TLS・pg_hba（試験 `deploy-cloud-postgres.test.js`）。PID 1 は uid 70・CapEff/CapPrm/CapBnd が 0。前の宣言（root の起動器）で作ったデータを新しい宣言で起動できる。
  - RabbitMQ: 起動・TLS（5671。CA で検証して TLSv1.3）・起動の直後の復元（exchange の宣言・サービスごとのユーザー 2 件・既定のユーザーの削除）。PID 1 は uid 100・capability 0。
    前の宣言（root の起動器）で作ったデータ（vhost を足した）を新しい宣言で起動でき、vhost が残る。
  - Keycloak: `start --import-realm`・DB へ TLS（verify-full）・realm の取り込み・8443 と 9000 の HTTPS。uid 1000・capability 0。
  - collector: `validate` と起動（読み取り専用のルート）。自己テレメトリの系列の名前（`otelcol_receiver_accepted_metric_points`・`otelcol_receiver_refused_metric_points`・
    `otelcol_exporter_send_failed_metric_points`）を :8888 で確かめた。remote write 先の Prometheus に `target_info` が送り手ごと（job = service.name）に出る
    （SDK の既定の属性があるとき。.NET の `ResourceBuilder.CreateDefault()` は持つ）。
  - Prometheus・Alertmanager: initContainer（規則の取り出し・設定の描画）と本体が読み取り専用のルートで起動し、Ready。
  - バックアップ: 手元でビルドしたイメージを uid 70・capability なしで走らせ、TLS（verify-full）の Postgres から全 DB を取り、S3 互換の口（`rclone serve s3`）へ送って
    読み戻しまで終わった（`完了（保管先 1 か所）`）。
- 確かめられなかったこと: 実の k3s の上で全部を同時に動かすこと（手元のディスクが足りず、k3s では PSA の受け付けと PVC の所有の付け替えだけを確かめた）。
  取引ユニットの Pod から collector への到達（NetworkPolicy の実効）。稼働中の構成の PVC の中身の所有（前の起動器が付け替えている前提。適用の前に読みだけで確かめる。手順は README）。
- 範囲外で見つけたこと: バックアップのイメージの Dockerfile は、`ARG RCLONE_VERSION` が RUN の環境に入り、`rclone version` が自分の旗（`--version`）として読んで失敗する
  （手元のビルドが止まった。`env -u RCLONE_VERSION` で通る）。本件では直さない（報告する）。
