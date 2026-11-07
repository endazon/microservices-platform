---
title: 作業仕様書 — クラウドの認可サーバー（Keycloak）への通信を、OpenBao の PKI が出す証明書の TLS にし、受け手で検証する
type: spec
status: in-progress
related_ids: [NFR-16, NFR-18, NFR-05, IADR-0693, IADR-0689, IADR-0674, IADR-0591, IADR-0086]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - 20261106_2471_cloud-openbao-eso.md
---

# 作業仕様書 — クラウドの Keycloak を TLS にする

> 着手 2026-11-07（develop `79c1039c5`）。判断の記録は IADR-0693。稼働中のクラスタには何もしない。秘密の値は扱わない（値を作るのはスクリプトで、オーナーが流す）。

## 起点

- 要求 **NFR-16**（通信の暗号化）・**NFR-18**（統制）・**NFR-05**（秘匿は OpenBao）。
- 所有者の裁定（2026-11-07）: クラウドの構成のクラスタ内の認可サーバー（Keycloak）への通信を、OpenBao の PKI が出す証明書の TLS にし、受け手は検証を有効にする。
- 前提: IADR-0689（OpenBao・ESO・PKI `pki_cloud`）、IADR-0674（クラウドの最小構成）、IADR-0591（基盤への TLS と CA の信頼の型）。

## 現状（develop `79c1039c5` で読んだ）

| 場所 | 現状 |
| --- | --- |
| `deploy/cloud/infra/keycloak.yaml` | `KC_HOSTNAME=http://keycloak:8080`・`KC_HTTP_ENABLED=true`・証明書なし。Service は 8080 |
| `deploy/cloud/infra/networkpolicy.yaml` | Keycloak の 8080 を取引ユニットの名前空間の**全 Pod**と LLM ゲートウェイへ開く。LLM ゲートウェイの出も 8080 |
| `AuthExtensions.PlatformJwtBearer` | `RequireHttpsMetadata = false`（固定）。既定の Authority は `http://keycloak:8080/realms/platform` |
| chart `global.auth.authority` | `http://keycloak:8080/realms/platform`（values-cloud は上書きしない） |
| PKI | `pki_cloud` の role は postgres・rabbitmq・llmgateway-service（`configure.sh` が初回の root で書く。root は最後に失効） |
| CA の配布 | ConfigMap `platform-infra-ca`（platform-infra・microservices-platform）と KV `cloud/shared/platform-infra-ca`。取引ユニットの policy `ast-eso` はこの KV を読める（取引ユニットの ESO が Secret `platform-infra-ca` へ写している） |

## 母集合（規則 9: 誤りの側の字面で走査した）

- `keycloak:8080` / `8080` / `RequireHttpsMetadata` / `KC_HOSTNAME` / `KC_HTTP_ENABLED` を `deploy/cloud`・`deploy/helm`・`src/platform/backend`・`scripts`・`docs/operations` で走査した。
  - クラウドの描画で Keycloak の 8080 に触れるもの: `infra/keycloak.yaml`（Deployment・Service）・`infra/networkpolicy.yaml`（入り 1・LLM ゲートウェイの出 1）・`infra/aliases.yaml` の注記・chart の `global.auth.authority`（values-cloud が上書きしない）。
  - 観測（`deploy/cloud/observability`）・バックアップ（`deploy/cloud/backup`）は Keycloak を呼ばない。
  - 除外: 既定の chart・ローカル（`deploy/local`）・compose の `keycloak:8080`（段の考え方によりクラウドだけを変える）。chart の `IdentityAdmin__Keycloak__BaseUrl` ほか（クラウドでは描かないサービス）。
- 取引ユニットの Pod のうち Keycloak を呼ぶもの: 取引ユニットの develop（`be3367d1d`）の chart を values-cloud で描いて引いた（`Auth__Authority`・`ServiceAuth__TokenEndpoint`・`*OwnerAuth__TokenEndpoint`・`LlmGateway__Auth__Authority` のどれかを持つ Deployment）。
  11 個（audit・backtest・configuration・cost-control・information-collection・market-monitor・notification・order-execution・report・risk-management・trade-decision の `-service`）。呼ばないのは `opend` だけ。

## 方式

1. PKI: `pki_cloud` に role `keycloak`（platform-infra。SAN は従来の 4 つの形）を足す。role の一覧を `lib.sh` の 1 か所に持ち、`configure.sh`（初回）と `root-token.sh apply-pki-roles`（稼働中の構成。root を作り直して書き、失効させる）が同じ一覧を書く。policy `platform-infra-eso` に `pki_cloud/issue/keycloak` を足す。
2. ESO: `VaultDynamicSecret`/`ExternalSecret` `keycloak-tls`（platform-infra）。既存の 3 つと同じ型（1 年・半年ごとに作り直す）。
3. Keycloak: HTTPS（8443）で受け、`KC_HOSTNAME=https://keycloak:8443`・`KC_HTTP_ENABLED=false`。証明書は Secret `keycloak-tls` を fsGroup で読む（0440）。管理の口（9000）も TLS になるので probe は HTTPS。証明書は 1 時間ごとに読み直す（`KC_HTTPS_CERTIFICATES_RELOAD_PERIOD`）。
4. NetworkPolicy: Keycloak の入りは 8443 だけ、送り元は取引ユニットの 11 個の Pod（名前空間 かつ Pod）と LLM ゲートウェイ。LLM ゲートウェイの出も 8443。
5. 併存の段: `deploy/cloud-transition/keycloak-tls-coexist`（kustomize の重ね。deploy/cloud の下は循環として拒まれるので隣に置く）。8080 と 8443 を両方受け、hostname を固定しない（`KC_HOSTNAME_STRICT=false`。発行元は要求の URL から決まる）。クラウドの既定の描画は最終の段。
6. MSP の受け手: chart に `global.auth.tls.enabled`（既定 false）。有効なら Authority（と MetadataAddress）が https でなければ描画で止め、`Auth__RequireHttpsMetadata=true` を渡し、CA（ConfigMap `platform-infra-ca`）をマウントして `SSL_CERT_DIR` に足す。アプリは `Auth:RequireHttpsMetadata`（既定 false）を読み、true なのに https でなければ起動で止まる。values-cloud は Authority を `https://keycloak:8443/realms/platform` にして有効にする。
7. 取引ユニットへの CA の配布: 既存の `cloud/shared/platform-infra-ca`（policy `ast-eso` が読める）をそのまま使う（同じ CA なので新しい配布は要らない）。受け取り方は手順書に書く。
8. 証明書の更新: 手順書と README §6。警報は置かない（理由は IADR-0693 §残余）。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| AC1 | クラウドの描画の Keycloak は https（8443）だけで受け、`KC_HOSTNAME` は https、平文の受け口（8080・`KC_HTTP_ENABLED=true`）が無い。証明書は Secret `keycloak-tls`、probe は HTTPS | `deploy-cloud.test.js` |
| AC2 | Keycloak の入りは TCP/8443 だけ、送り元は取引ユニットの 11 個の Pod（同じ要素で AND）と LLM ゲートウェイだけ。LLM ゲートウェイの出も 8443 だけ | 同上 |
| AC3 | 併存の重ねは 8080 と 8443 の両方を受け、hostname を固定しない。既定の描画には併存の形が無い | 同上 |
| AC4 | クラウドの chart の LLM ゲートウェイは Authority が https、`Auth__RequireHttpsMetadata=true`、CA を信頼ストアに足す。既定・ローカルの描画は変わらない（RequireHttpsMetadata を描かない・Authority は http のまま） | 同上 |
| AC5 | `global.auth.tls.enabled` で Authority / MetadataAddress が https でなければ描画で止まる | 同上 |
| AC6 | `Auth:RequireHttpsMetadata` は既定 false。true なら JwtBearer の RequireHttpsMetadata が true。true で Authority / MetadataAddress が https でなければ例外（起動で止まる） | `AuthExtensionsTests` |
| AC7 | `pki_cloud` の role `keycloak` は keycloak の名前だけを発行し、platform-infra の ESO だけが発行できる。`root-token.sh apply-pki-roles` が稼働中の構成へ role を書ける | `openbao-scripts.test.sh` |
| AC8 | `keycloak-tls` の ExternalSecret があり、参照の突き合わせ（`secret-inventory.js --strict`）が通る | `secret-inventory.js`・`deploy-cloud.test.js` |
| AC9 | 修正を外すと試験が赤になる（変異） | 各試験の陰性対照と、手で戻して赤を確かめた記録（下） |

## 実測・記録

- Keycloak 26.7.4（宣言の digest）を手元の docker で起こして確かめた（2026-11-07）:
  - 最終の形（`KC_HOSTNAME=https://keycloak:8443`・`KC_HTTP_ENABLED=false`・証明書のファイル・再読の周期）: 「Listening on: https://0.0.0.0:8443. Management interface listening on https://0.0.0.0:9000」。8080 は接続を拒む。発行元は `https://keycloak:8443/realms/master`。SAN に無い名前は検証で落ちる。
  - 併存の形（`KC_HOSTNAME_STRICT=false`・`KC_HTTP_ENABLED=true`）: 8080 と 8443 の両方で受け、発行元は https で引けば https、http で引けば http になる。
  - 証明書のファイルを root 所有・0440・グループ 1000 にすると、補助グループ 1000 を持つときだけ読める（持たなければ `AccessDeniedException` で起動しない）→ fsGroup 1000・defaultMode 0440。
