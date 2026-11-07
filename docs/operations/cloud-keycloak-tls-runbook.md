---
title: 運用 Runbook — クラウドの認可サーバー（Keycloak）の TLS（新しく作る・稼働中の構成を移す・取引ユニットの受け取り方・証明書の更新）
type: runbook
status: draft
author: claude
created: 2026-11-07
updated: 2026-11-07
---
<!-- trace:
ids: [NFR-16, NFR-18, NFR-05]
iadrs: [IADR-0693, IADR-0689, IADR-0674, IADR-0591, IADR-0086]
specs: [20261107_cloud-keycloak-tls]
-->

# 運用 Runbook: クラウドの認可サーバー（Keycloak）の TLS

> 対象はクラウドの最小構成（単一ノードの k3s・`deploy/cloud/`）の Keycloak と、それを呼ぶ受け手（MSP の LLM ゲートウェイ・取引ユニットのサービス）。
> 秘匿管理の手順（初期化・unseal・root の作り直し）は[秘匿管理の手順書](cloud-openbao-init-unseal-runbook.md)にある。
>
> **状態を `draft` に留めている理由**: クラウドのノードで移行をまだ流していない（移行の門の稼働の確かめで記録する）。

## 形（何がどう変わったか）

| 項目 | 平文の構成（旧） | TLS の構成（最終の段） |
| --- | --- | --- |
| Keycloak の受け口 | HTTP 8080 | **HTTPS 8443 だけ**（管理の口 9000 も TLS） |
| トークンの発行元（iss） | `http://keycloak:8080/realms/platform` | `https://keycloak:8443/realms/platform` |
| サーバ証明書 | なし | OpenBao の PKI（`pki_cloud`。DB・ブローカと同じ CA）が発行し、ESO が Secret `platform-infra/keycloak-tls` に写す |
| 届く Pod | 取引ユニットの名前空間の全 Pod・LLM ゲートウェイ | 取引ユニットの Keycloak を呼ぶ 11 個の Pod・LLM ゲートウェイ（8443 だけ） |
| 受け手の検証 | メタデータを平文で取る | https を必須にし（`Auth__RequireHttpsMetadata=true`）、基盤の CA で証明書を検証する |

- CA の証明書（公開してよい）は従来と同じ置き場にある: ConfigMap `platform-infra-ca`（platform-infra・microservices-platform）と、KV `cloud/shared/platform-infra-ca`（取引ユニットの ESO が写す）。
- 平文と HTTPS の併存の段は `deploy/cloud-transition/keycloak-tls-coexist`（稼働中の構成を移す間だけ当てる）。

## 1. 新しく作る構成

`deploy/cloud/README.md` §3 の順のままでよい。`configure.sh` が PKI の role `keycloak` を作り、`kubectl apply -k deploy/cloud/vault/eso` が `keycloak-tls` を写し、
`kubectl apply -k deploy/cloud` が最終の段（HTTPS だけ）を当てる。

- `kubectl wait --for=condition=Ready externalsecret --all -n platform-infra` が `keycloak-tls` も待つ。これが Ready になる前に Keycloak を当てると、Pod は Secret を待って起動しない。
- 取引ユニットは最初から https の Authority で入れる（§3）。

## 2. 稼働中の構成を移す（平文 → 併存 → 平文を閉じる）

🔴 **順を守る。** 段を飛ばすと、受け手が鍵を取れずに 401 を返す（取引の判断・報告書が止まる。平文には戻らない）。
各段の後に「確かめ」を見てから次へ進む。スクリプトはどれも `--live` が無ければ何もしない。

### 段 0: PKI の role と policy を足す（root を作り直して書き、失効させる）

```sh
bash deploy/cloud/vault/root-token.sh --live --init-file <init>.age --operator-file <operator>.age --identity <age の識別子> apply-pki-roles
bash deploy/cloud/vault/root-token.sh --live --init-file <init>.age --operator-file <operator>.age --identity <age の識別子> apply-policies
```

- 確かめ: 1 つ目の出力に「書いた: PKI の role keycloak」、両方の出力に「作り直した root を失効させた」。在る role は同じ値で書き直されるだけ（CA と発行済みの証明書は変わらない）。

### 段 1: 証明書を写す

```sh
kubectl apply -k deploy/cloud/vault/eso
kubectl -n platform-infra wait --for=condition=Ready externalsecret/keycloak-tls --timeout=300s
kubectl -n platform-infra get secret keycloak-tls -o jsonpath='{.data.tls\.crt}' | base64 -d | openssl x509 -noout -subject -enddate -ext subjectAltName
```

- 確かめ: SAN に `keycloak`・`keycloak.platform-infra`・`keycloak.platform-infra.svc`・`keycloak.platform-infra.svc.cluster.local`。鍵（`tls.key`）は読まない。

### 段 2: Keycloak を併存にする（8080 と 8443 の両方）

```sh
kubectl apply -k deploy/cloud-transition/keycloak-tls-coexist
kubectl -n platform-infra rollout status deploy/keycloak --timeout=600s
```

- この段では hostname を固定しない。トークンの発行元は、呼び手が引いた URL で決まる（`http://keycloak:8080` で引けば http、`https://keycloak:8443` で引けば https）。
- 確かめ: `kubectl -n platform-infra logs deploy/keycloak | grep Listening` が「http://0.0.0.0:8080 and https://0.0.0.0:8443」と
  「Management interface listening on https://0.0.0.0:9000」を出す。Pod が Ready になる（probe は HTTPS で測る）。

### 段 3: MSP の受け手（LLM ゲートウェイ）を https にする

```sh
helm upgrade --install msp deploy/helm/microservices-platform -n microservices-platform \
  -f deploy/cloud/values-cloud.yaml --set-string global.image.tag="$TAG" \
  --set-string global.auth.validIssuers=http://keycloak:8080/realms/platform    # 移す間だけ。取引ユニットが平文で取ったトークンも受ける
```

- 確かめ: `kubectl -n microservices-platform rollout status deploy/llmgateway-service`（Authority が https でなければ起動で止まる）。
  取引ユニットからの呼び出し（報告書・取引判断）が 401 にならず、LLM ゲートウェイのログにメタデータの取得の失敗（`IDX20803`）が出ない
  （出たら証明書の検証か到達に失敗している。CA のマウントと NetworkPolicy の 8443 を確かめる）。
- 旧の発行元を足すのは、取引ユニットがまだ平文の URL でトークンを取る間だけである（段 6 で外す）。

### 段 4: 取引ユニットを https にする

取引ユニットのリポジトリの手順で入れる（§3 の受け取り方）。取引ユニットの受け手も、移す間だけ旧の発行元を足す。

- 確かめ: 取引ユニットの全サービスが Ready で、Keycloak のログに平文の口（8080）への要求が来ない
  （Keycloak の受け手の側でも、取引ユニットの Pod の env に `http://keycloak:8080` が残っていない）。

### 段 5: 平文を閉じる（最終の段）

```sh
kubectl apply -k deploy/cloud
kubectl -n platform-infra rollout status deploy/keycloak --timeout=600s
kubectl -n platform-infra get svc keycloak -o jsonpath='{.spec.ports[*].port}'   # 「8443」だけ
```

- 発行元は `https://keycloak:8443/realms/platform` に固定される。

### 段 6: 旧の発行元の併記を外す

トークンの寿命（realm の既定）が過ぎてから、段 3 の `--set-string global.auth.validIssuers=…` を付けずに helm を流し直す。取引ユニットも同じく外す。

- 確かめ: `kubectl -n microservices-platform get deploy llmgateway-service -o yaml | grep -A1 Auth__ValidIssuers` が何も出さない。

### 切り戻し

- 段 2〜4 の間: 受け手を平文の構成へ戻す（段 3 を `values-cloud.yaml` の前の版で流す）。Keycloak は併存のままなので、平文の呼び手はそのまま動く。
- 段 5 の後: `kubectl apply -k deploy/cloud-transition/keycloak-tls-coexist` で併存へ戻し、原因を直してから段 5 を流し直す。平文だけの構成へは戻さない。

## 3. 取引ユニットの受け取り方

取引ユニットのリポジトリの作業である（ここでは配る側と受け取り方だけを書く）。

| 受け取るもの | 置き場・値 |
| --- | --- |
| CA の証明書（公開してよい） | KV `cloud/shared/platform-infra-ca` のキー `ca.crt`。取引ユニットの SecretStore（role `ast-eso`）が既に読めて、DB・ブローカの検証のために Secret `platform-infra-ca` へ写している。**同じ CA** なので新しい写しは要らない。鍵は配られない（読めるのは証明書だけ） |
| Authority（発行元） | `https://keycloak:8443/realms/platform`（名前空間の別名 `keycloak` で引く。別名は ExternalName のまま変えない） |
| トークンの取得先 | Authority から導く `https://keycloak:8443/realms/platform/protocol/openid-connect/token` |
| 届く Pod | Keycloak の入りは、取引ユニットの次の 11 個の Pod（label `app`）の 8443 だけを許す: audit・backtest・configuration・cost-control・information-collection・market-monitor・notification・order-execution・report・risk-management・trade-decision（それぞれ `-service`）。ほかの Pod（OpenD を含む）からは届かない |

取引ユニットの側で行うこと:

1. Keycloak を呼ぶ全ての Pod（上の 11 個。DB・ブローカを使わない Pod も含む）に CA をマウントし、信頼ストアへ足す（`SSL_CERT_DIR=/etc/ssl/certs:<CA のディレクトリ>`。既定のストアを先に残す）。
2. Authority を https にし、受け手のメタデータの取得に https を必須にする（MSP の共有の基盤を使うサービスは `Auth__RequireHttpsMetadata=true`）。
3. 移す間だけ、受け手は旧の発行元 `http://keycloak:8080/realms/platform` も受ける（段 6 で外す）。
4. 取引ユニットの名前空間に Keycloak への出の NetworkPolicy があるなら、宛先のポートを 8443 にする。
5. Keycloak を呼ぶサービスを足したら、MSP の `deploy/cloud/infra/networkpolicy.yaml` の一覧にも足すよう MSP へ依頼する（足さなければそのサービスの呼び出しが落ちる）。

## 4. 証明書の更新と期限

- サーバ証明書は 1 年。ESO が半年ごとに作り直して Secret `keycloak-tls` を書き換え、Keycloak は 1 時間以内にファイルを読み直す（Pod の作り直しは要らない）。
- 期限の確かめ（棚卸しのたびに。鍵は読まない）:

  ```sh
  kubectl -n platform-infra get secret keycloak-tls -o jsonpath='{.data.tls\.crt}' | base64 -d | openssl x509 -noout -enddate
  kubectl -n platform-infra get externalsecret keycloak-tls -o jsonpath='{.status.refreshTime}{"\n"}'
  ```

- すぐに作り直すとき: `kubectl -n platform-infra annotate externalsecret keycloak-tls force-sync="$(date +%s)" --overwrite`（封印のあいだは発行できない。unseal の後に行う）。
- **警報は置いていない。** Prometheus は相手の証明書の期限を系列にしないため、期限の系列には別の部品（blackbox の exporter か ESO のメトリクスの収集）が要る。
  作り直しは半年ごとで、止まっても半年の余裕がある。上の確かめを棚卸しで流す。
- CA（10 年）の期限は[秘匿管理の手順書](cloud-openbao-init-unseal-runbook.md)と同じく ConfigMap `platform-infra-ca` から読む。
