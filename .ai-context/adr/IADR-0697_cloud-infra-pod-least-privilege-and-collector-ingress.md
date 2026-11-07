---
title: IADR-0697 クラウドの基盤の Pod の権限を最小にする（root でない・capability を全部落とす・権限の昇格なし・seccomp RuntimeDefault・SA のトークンなし）。基盤と MSP の名前空間の PSA を restricted に上げ、collector の OTLP の受け口を送る Pod だけに絞り、受け取りの上限と、受け取りの拒否・系列の途絶えの警報を足す
type: impl-adr
status: Accepted
related_ids: [NFR-18, NFR-21, IADR-0674, IADR-0675, IADR-0689, IADR-0693]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - ../specs/20261107_cloud-infra-pod-hardening.md
  - ../specs/20261107_cloud-openbao-sa-token.md
---

# IADR-0697: クラウドの基盤の Pod の権限を最小にし、collector の受け口を絞る

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07「稼働前に中程度もすべて直す」「capability を外し、OpenBao も TLS にする（両方）」）・claude（方式）

## 起点・関連

- 要求 **NFR-18**（統制）・**NFR-21**（運用の監視）。
- 前提: IADR-0674（クラウドの最小構成）、IADR-0675（警報と heartbeat）、IADR-0689（OpenBao・ESO。値の置き場）、IADR-0693（入りを「名前空間 かつ Pod」で名指しする型）。
  OpenBao の受け口の TLS は別の IADR（同じ日の別の作業）が扱う。本件は `deploy/cloud/vault/` に触れない。
- 作業仕様書: `.ai-context/specs/20261107_cloud-infra-pod-hardening.md`（現状・母集合・実測）。

## コンテキストと課題

- クラウドの単一ノードでは、基盤の Pod（Postgres・RabbitMQ・Keycloak・collector・Prometheus・Alertmanager・バックアップ）の多くが root で、実行時の既定の
  capability（NET_RAW を含む）を持ったまま動いていた。名前空間の PSA は baseline（既定の capability を許す）。
- 単一ノードでは全 Pod が同じ CNI のブリッジに載る。1 つの Pod の侵害から同じノードの他の通信への足場を減らすには、各 Pod が要らない権限を持たないことが要る。
- collector の OTLP の受け口は、取引ユニットの名前空間と MSP の名前空間の**全 Pod**へ開いていた。受け取りの上限は memory_limiter だけで、受け取りを拒んでも
  collector は落ちない。`OtelCollectorDown` は scrape の `up` だけを見るので、アプリの系列（費用・取引の警報の元）が届かなくなっても鳴らない。
- RabbitMQ の Pod には名前空間の default の ServiceAccount のトークンが載っていた（ほかの基盤の Pod には `automountServiceAccountToken: false` が在った）。

## 決定

### 決定 1 — クラウドの描画の全 Pod（initContainer を含む）を、root でなく capability を持たない形にする

- どのコンテナも `runAsNonRoot: true` と**数の** `runAsUser`（イメージの利用者。名前の利用者〔`nobody`〕は kubelet が root でないと検められず起動を拒むので数で書く）:
  Postgres・バックアップ・db-users の Job＝70、RabbitMQ＝100（gid 101）、Keycloak＝1000（イメージの gid 0 のまま）、collector＝10001、Prometheus・Alertmanager＝65534。
- `allowPrivilegeEscalation: false`・`capabilities.drop: [ALL]`・`seccompProfile: RuntimeDefault`（Pod の側）。**足す capability は無い。**
  - どの受け口も 1024 より上なので NET_BIND_SERVICE は要らない。
  - 公式の Postgres・RabbitMQ のイメージの起動器は、root で起きたときだけデータの所有を付け替えて利用者を落とす（CHOWN・SETUID・SETGID・DAC_OVERRIDE はそのため）。
    利用者で起きればそのまま起動するので、これらは要らない（実測。作業仕様書）。
  - 足す必要が出たら `scripts/deploy-cloud.test.js` の `HARDENING_ALLOWED_CAPS` に理由つきで足す（理由の無い追加は試験が止める）。NET_RAW は許さない。
- 書く物が emptyDir だけのコンテナ（collector・Prometheus・Alertmanager・各 initContainer）は `readOnlyRootFilesystem: true`。
  Postgres・RabbitMQ・Keycloak の本体はイメージの中の場所（ソケット・作業の dir）へ書くので付けない（残余 3）。
- `automountServiceAccountToken: false` を全 Pod に置く（RabbitMQ に足した。基盤の Pod はどれも API サーバーを呼ばない）。

### 決定 2 — Postgres・RabbitMQ には fsGroup を置かない。鍵は initContainer が gid 0 で読んで自分の所有の写しを作る

- **k3s の local-path の PVC にも fsGroup の所有の付け替えが効く**（実測: `fsGroup` の Pod の次の起動で、PVC の中の 0700 の dir・0600 のファイルが
  `2770`・`0660` になった）。Postgres はデータの dir が 0700 か 0750 でないと起動せず、Erlang は cookie が所有者だけのものでないと起動しない。
  稼働中の PVC へ当てると、最初の起動で付け替えが走り、元に戻せない形で起動が止まる。
- 鍵の Secret（root の所有）を利用者が読むために fsGroup を使わず、initContainer を **`runAsUser`＝本体の利用者・`runAsGroup: 0`** で走らせ、Secret を `defaultMode: 0440`
  にして読む。写した物は初めから本体の利用者の所有なので、chown（CHOWN の capability）が要らず、mode だけを直す（Postgres の鍵は 0600、RabbitMQ の鍵は 0400）。
  gid 0 は root の利用者でも capability でもない（PSA の restricted も許す）。gid 0 を持つのはこの短い initContainer だけ。
- RabbitMQ のサービスごとのユーザーのパスワード（Secret `rabbitmq-users`）も、起動の直後の復元（postStart）が root でなく uid 100 で読むことになったので、
  同じ initContainer が 0400 の写し（メモリの emptyDir）を作る。写しは Pod の起動の時に作るので、Secret を差し替えたら Pod を作り直す
  （従来もユーザーの復元は起動の直後だけで、差し替えの後に作り直すのは同じ）。
- Keycloak は PVC を持たないので fsGroup（1000。鍵の 0440 を読む）を残す。
- 副次: RabbitMQ の probe・postStart が root で走らなくなり、root が cookie を先に作ってブローカが読めなくなる競り（起動の直後の復元の注記）が起きなくなる。

### 決定 3 — 基盤と MSP の名前空間の PSA を enforce・warn・audit とも restricted に上げる

- `platform-infra`・`microservices-platform`（`deploy/cloud/infra/namespaces.yaml`）。描画の全 Pod（基盤の 7 つ・db-users の Job・LLM ゲートウェイ・Keycloak の併存の重ね）が
  restricted を満たすことを、k3s（README の固定の版）の受け付けで確かめた。手順書が名前空間に作る Pod は `create job --from=cronjob`（同じ宣言）だけで、`kubectl run` は無い。
- 上げられない名前空間は本件の範囲に無い。`openbao`（OpenBao）は OpenBao の受け口の TLS の作業が扱い、`external-secrets` は helm の chart の値（`vault/external-secrets-values.yaml`）
  の範囲で、どちらも本件では触れない。取引ユニットの名前空間は取引ユニットの側が持つ。

### 決定 4 — collector の OTLP の受け口を、OTLP を送る Pod だけ（名前空間 かつ Pod）・gRPC（4317）だけに絞る

- 一覧の引き方は IADR-0693 の Keycloak の入りと同じ: 取引ユニットの chart を values-cloud で描き、OTLP の宛先の env（`Otlp__Endpoint`）を持つ Deployment を引く。
  11 個（audit・backtest・configuration・cost-control・information-collection・market-monitor・notification・order-execution・report・risk-management・trade-decision の
  `-service`）。**OpenD は持たないので入れない。** MSP は LLM ゲートウェイだけ。
- 宛先はどれも `http://otel-collector:4317`（gRPC）で、4318（HTTP）を宛先に持つ Pod は無いので、入りは 4317 だけにする（LLM ゲートウェイの出も 4317 だけ。従来どおり）。
  collector の HTTP の receiver は設定に残す（ローカルの経路と設定を揃える。届く Pod が無い）。
- 一覧は `scripts/deploy-cloud.test.js` の `OTLP_AST_SENDERS`・`OTLP_MSP_SENDERS` が固定する。取引ユニットに OTLP を送るサービスが増えたら、
  入り・一覧・警報 `OtlpSendersMissing` の閾値を対で直す（試験が閾値と一覧の数の一致を突き合わせる）。

### 決定 5 — 受け取りの上限と、受け取りの拒否・系列の途絶え・系列の数の警報を足す

- 上限: gRPC の receiver に `max_recv_msg_size_mib: 4`・`max_concurrent_streams: 16`、HTTP に `max_request_body_size: 4194304`、memory_limiter に `spike_limit_mib: 32`
  （上限 128MiB は従来どおり）。.NET の OTLP の exporter は 1 つの接続で単項の呼び出しを送るので、平時は同時の流れが 1〜2 である。
- 警報（`observability/prometheus.yaml` の `cloud-availability`。collector の自己テレメトリは Prometheus が直接 scrape する＝OTLP の経路を通らない）:

  | 警報 | 式の要点 | 重大度 |
  | --- | --- | --- |
  | `OtelCollectorRefusingData` | `otelcol_receiver_refused_{metric_points,spans,log_records}` の 10 分の増分の和 > 0（無い系列は 0） | critical |
  | `OtelCollectorExportFailing` | `otelcol_exporter_send_failed_metric_points{exporter="prometheusremotewrite"}` の 10 分の増分 > 0、5 分 | critical |
  | `OtlpMetricsPipelineSilent` | `otelcol_receiver_accepted_metric_points{receiver="otlp"}` の率が 0（系列が無ければ 0）、15 分 | critical |
  | `OtlpSendersMissing` | `count(count by (job) (last_over_time(target_info[10m])))`（無ければ 0）が 12 未満、15 分 | warning |
  | `PrometheusHeadSeriesHigh` | `prometheus_tsdb_head_series` > 100000、15 分 | warning |

  - `target_info` は remote write が送り手のリソースごと（job＝service.name）に作る（SDK の既定の属性があるとき。.NET の `ResourceBuilder.CreateDefault()` は持つ。実測）。
    サービスの名前に依らず、送ってくるサービスの**数**で途絶えを見る（取引ユニットの系列の名前を基盤が知らなくてよい）。
  - `OtlpSendersMissing` の閾値と注釈（summary・description）の数は、送り元の一覧の数と同じであることを試験が突き合わせる。
  - 系列の名前は collector 0.102.0 の :8888 で実測した。式は promtool の単体試験（鳴るときに鳴り、平時は鳴らない）で固定する（`scripts/cloud-ops.test.js`）。

### 決定 6 — 偽の系列への備えと、OTLP の認証（bearer）は入れない（今は）

- 検討した: collector の receiver に `bearertokenauth` を付け、送り手に `OTEL_EXPORTER_OTLP_HEADERS` で同じトークンを渡す。
  - collector 0.102.0 の `bearertokenauth` は 1 つのトークン（か 1 つのファイル）しか持てず、送り手ごとに分けられない。同じトークンを 12 の送り手が持つので、
    送り手の 1 つが侵害されれば効かない。送り手でない Pod（OpenD ほか）は決定 4 の NetworkPolicy が既に止める。足すもの（OpenBao の値・2 つの ESO・取引ユニットの chart の
    env・MSP の chart の env）に比べて、防げる範囲が NetworkPolicy と同じである。
  - 送り手ごとのトークン（新しい版の `tokens`）か mTLS（OpenBao の PKI で送り手ごとの証明書）にするなら、送り手ごとに名乗りが分かれ、偽の系列を送り手で絞れる。
    collector の版の更新と取引ユニットの chart の変更を伴うので、別の作業とする（残余 1）。
- 偽の系列（送り手の 1 つが他の送り手の名前の系列を書く）: 本件では止めない。系列の数（`PrometheusHeadSeriesHigh`）と受け取りの拒否で、溢れさせる形は見える。
  費用の警報の元（`llm_*`）を LLM ゲートウェイの送り手からだけ受ける形（collector の `filter` で resource の service.name を見る）は、service.name 自体が送り手の
  自己申告なので、送り手ごとの名乗り（上の mTLS）が無い限り効かない（残余 1）。

### 決定 7 — ［2026-11-07 追記 / #2496］OpenBao の Pod は、自動のマウントを止め、期限を 1 時間に限ったトークンを明示で載せる

- OpenBao の kubernetes 認証は、`auth/kubernetes/config` に `token_reviewer_jwt` を置かず `disable_local_ca_jwt=false`（`deploy/cloud/vault/configure.sh` のクラスタの中の分岐）で、
  **OpenBao 自身の ServiceAccount のトークン**（ClusterRole `system:auth-delegator`）で TokenReview を呼ぶ。このトークンは要る
  （実の k3s v1.35.10 で、トークンが無い Pod では `auth/kubernetes/config` の書き込みが「ca.crt が無い」で通らず、明示のトークンを載せた Pod では
  ESO 役の ServiceAccount のトークン〔audience vault〕でログインでき、audience の違うトークンは拒まれることを確かめた）。
- クライアント（ESO）の JWT を reviewer に使う形（`disable_local_ca_jwt=true`・reviewer なし）は、ESO の 3 つの ServiceAccount に auth-delegator を渡すことになり、
  権限を ESO の側へ広げるので採らない。
- `automountServiceAccountToken: false` にし、同じ置き場（`/var/run/secrets/kubernetes.io/serviceaccount`。configure.sh はこの置き場の `ca.crt` でクラスタの中と判じる）へ
  projected のボリュームで、トークン（audience は API サーバーの既定・`expirationSeconds: 3600`）・API サーバーの CA・名前空間だけを読み取り専用で載せる。
  トークンは OpenBao のコンテナだけが持つ。kubelet が期限の前に作り直し、OpenBao は置き場から読み直す（出典と実測は下）。
- **読み直すことの出典**（OpenBao `v2.7.1`。タグのコミット `a5db72cef75c24b920ade02065b18dd8eb666bac`。kubernetes 認証は組み込みの
  `internal/builtin/credential/kubernetes`）:
  - `path_config.go`: `localJWTPath = "/var/run/secrets/kubernetes.io/serviceaccount/token"`・`localCACertPath = "…/serviceaccount/ca.crt"`。
    本件のマウント先（`/var/run/secrets/kubernetes.io/serviceaccount` の `token`・`ca.crt`）と一致する。
  - `backend.go` の `loadConfig`: `DisableLocalCAJwt` が false で、保存した設定の `TokenReviewerJWT` が空のときだけ、
    `localSATokenReader.ReadFile()` で置き場のトークンを読む（CA も保存が空なら置き場から読む）。`path_login.go` は login のたびに `loadConfig` を呼ぶ。
  - `backend.go`: `jwtReloadPeriod = 1 * time.Minute`（`newCachingFileReader(localJWTPath, jwtReloadPeriod, …)`）、`caReloadPeriod = 1 * time.Hour`。
    `caching_file_reader.go` の `ReadFile` は、写しの期限（1 分）を過ぎると `os.ReadFile` で読み直す。
  - つまり、**設定に `token_reviewer_jwt` を保存すると読み直さない**（保存した 1 つのトークンが 1 時間で切れ、ESO の同期が全部止まる）。
    configure.sh のクラスタの中の分岐が `token_reviewer_jwt`・`disable_local_ca_jwt`・`kubernetes_ca_cert` を書かないことと、トークンと CA が上の置き場に
    載ることを `scripts/deploy-cloud.test.js`（`openbaoReviewerTokenProblems`。保存する形・読まない形・置き場の移動の変異で赤）が固定する。
  - 置き場のトークンが読めないとき、プラグインは黙って login の JWT を reviewer に使う（`loadConfig` の注記）。ESO の ServiceAccount は auth-delegator を
    持たないので、その場合 login は TokenReview で拒まれる（黙って広がる側へは倒れない）。
- **実測**（docker の中の k3s v1.35.10。宣言と同じ securityContext・projected で、期限だけ最短の `expirationSeconds: 600`。OpenBao 2.7.1 を dev で起こし、
  クラスタの中の分岐と同じ `kubernetes_host` だけの設定。試験の足場で値はその場の乱数）: 1 分ごとに、新しく作った ESO 役のトークンで login した。
  置き場のトークンは起動の 508 秒後に kubelet が作り直し（期限の 8 割の後）、**最初のトークンの期限を過ぎた後（+10 分・+11 分 …）も login が通り続けた**。
- `scripts/deploy-cloud.test.js` の H1〜H3 の対象に `deploy/cloud/vault` と OpenBao の併存の重ねを入れた。明示のトークンは `SA_TOKEN_ALLOWED` の Pod（今は OpenBao だけ。理由つき）だけ・
  期限 1 時間以内・読み取り専用のマウントに限る。名前空間 `openbao` の PSA も restricted であることを固定する。

## 結果

- 良い影響: 基盤のどの Pod も root でなく capability を持たない（NET_RAW を含む）。PSA の restricted が、宣言の書き漏らし（新しい Pod・initContainer）を受け付けで止める。
  collector へ届くのは 12 の送り手だけで、受け取りの拒否・remote write の失敗・送り手の途絶え・系列の増えすぎが Discord に出る（OtelCollectorDown の外の黙りを見る）。
- 悪い影響: 稼働中の Postgres・RabbitMQ・Keycloak・Prometheus は Recreate で作り直される（適用の順は README §3）。Prometheus のデータは emptyDir なので消え、
  月次予算の進みの警報（7 日の窓）は作り直しの後しばらく過小になる（従来の再起動と同じ）。RabbitMQ のユーザーのパスワードを差し替えたら Pod を作り直す（決定 2）。

## 残余

1. 送り手ごとの名乗り（mTLS か送り手ごとのトークン）が無いので、送り手の 1 つの侵害で偽の系列を書ける（決定 6）。collector の版の更新と取引ユニットの chart の変更を伴う別の作業。
2. 稼働中の構成の PVC の中身が前の起動器（root）で付け替え済みであることは前提で、適用の前に読みだけで確かめる（README §3。root の所有の物が在れば適用しない）。
   前の宣言では probe（`rabbitmq-diagnostics ping`）が root で走るので、ブローカの起動より先に root が cookie を作る競りがあった（在れば前の宣言でも起動できていない）。
3. Postgres・RabbitMQ・Keycloak の本体はルートのファイルシステムを書き込み可のまま（ソケット・作業の dir。emptyDir へ移すのは別の作業）。
4. 手元で確かめられなかったこと: 実の k3s の上で全部を同時に動かすこと（ディスクの都合。k3s では PSA の受け付けと PVC の所有の付け替えを確かめ、各イメージの起動は docker で
   宣言の securityContext を写して確かめた）。取引ユニットの Pod から collector への到達（NetworkPolicy の実効）は、稼働の後に `OtlpSendersMissing` が鳴らないことで見る。
5. `PrometheusHeadSeriesHigh` の閾値（100000）は見込みで、平時の実測が無い。**稼働の開始から 7 日の間の `max_over_time(prometheus_tsdb_head_series[7d])` の実測で見直す**（期限: 稼働の開始の 7 日後。平時の最大の 10 倍ほどへ直す）。
6. ［2026-11-07 追記 / #2496］OpenBao のトークンの切り替わりは、期限 600 秒で実測した（決定 7）。宣言の期限（3600 秒）そのものでは測っていない（kubelet の作り直しは期限の割合で決まり、プラグインの読み直しは 1 分ごとで期限に依らない）。稼働の後に ESO の同期が 1 時間を越えて続くことでも見る。
7. ［2026-11-07 追記 / #2496］OpenBao の Pod は ConfigMap `kube-root-ca.crt`（kube-controller-manager が全名前空間に作る）が無いと起動しない（projected の source は optional にしない）。名前空間を作った直後の数秒だけ起き得る。受容する（Pod は作り直しで起きる）。
