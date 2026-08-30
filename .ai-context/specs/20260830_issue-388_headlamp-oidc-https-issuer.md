---
title: 作業仕様書 — Headlamp の OIDC issuer を https エッジへ移す（#388 / #271・#781 の帰結）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0005
  - ADR-0023
  - IADR-0066
  - IADR-0076
  - IADR-0080
  - IADR-0084
  - IADR-0105
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0008_kubernetes-runtime.md
related_specs:
  - "20260830_issue-781_edge-cert-explicit-sans.md"
issue: "#388"
---

# 作業仕様書 — Headlamp の OIDC issuer を https エッジへ移す

## 前提が揃った経緯

本 issue は「k8s 1.30+ が OIDC issuer に https を強制するため、現行の http issuer では実現できない」として
blocked だった。**前提が 3 つとも揃った。**

| 前提 | 状態 |
| --- | --- |
| issuer が https である | ✅ `https://keycloak.localhost/realms/platform`（#780 の作業で既に着地していた） |
| その証明書を検証できる | ✅ **#1074 で是正**。従前は `*.localhost` のワイルドカードが標準 TLS クライアントに拒否され、検証すると必ず落ちていた |
| apiserver に OIDC 検証が入っている | ✅ **利用者が #781 の設定を適用**（`/etc/rancher/k3s/config.yaml.d/oidc.yaml` ＋ `oidc-ca.crt`）。k3s 再起動後もクラスタは Ready |

## 変更

`deploy/local/headlamp/headlamp.yaml`:

1. `HEADLAMP_CONFIG_OIDC_IDP_ISSUER_URL` を `http://keycloak:8080/realms/platform` →
   **`https://keycloak.localhost/realms/platform`**
2. `edge-tls` Secret の `ca.crt` を `/etc/ssl/local-edge/ca.crt` へマウントし、`SSL_CERT_FILE` で指す

### なぜ 2 が要るか

エッジ証明書は cert-manager のローカル CA が署名しており、**コンテナの既定ルートに入っていない**。
入れないと discovery が TLS で落ちる。`edge-tls` の `ca.crt` がその root CA であることは実測で確認した
（`subject=CN=microservices-platform local edge root CA`）。

`SSL_CERT_FILE` はルート集合を**置き換える**が、Headlamp が TLS で叩く先はこのエッジだけである
（apiserver へは `InClusterConfig` が SA の `ca.crt` を明示的に使う）。**その前提を注記に書いた。**

### なぜ issuer を揃える必要があるか

Headlamp は **id_token を apiserver へ Bearer として委譲する**（`-in-cluster`）。
**apiserver が検証する issuer と Headlamp が受け取る token の `iss` が文字列一致しないと弾かれる。**
apiserver 側が https を強制される以上、Headlamp 側も https エッジに揃えるほかない。

## 到達性の確認（実測 2026-08-30）

```console
$ kubectl -n kube-system get cm coredns-custom -o yaml
    rewrite name regex (.*)\.localhost traefik.kube-system.svc.cluster.local

$ kubectl -n platform-infra exec deploy/grafana -- nslookup keycloak.localhost
Name:  traefik.kube-system.svc.cluster.local
Address: 10.43.227.97
```

**Pod からエッジ host を引ける。**

## 実測（配備後）

```console
$ kubectl -n platform-infra get pods -l app=headlamp
headlamp-55ddffb45c-w4t4m   1/1   Running   0   11s

$ kubectl -n platform-infra exec deploy/headlamp -- \
    wget -qO- https://keycloak.localhost/realms/platform/.well-known/openid-configuration
{"issuer":"https://keycloak.localhost/realms/platform","authorization_endpoint":"https://keycloak.localhost/realms/platf…
```

🔴 **これが CA マウントの決定的な証跡である** —— `wget` は検証を切っていない。
CA が効いていなければここで TLS エラーになる。

```console
$ curl http://localhost:4466/config           # port-forward 経由
{"clusters":[{"name":"main","server":"https://10.43.0.1:443","auth_type":"oidc",…}]}
```

**Headlamp が `auth_type: oidc` を広告している。** OIDC 設定を受理し discovery に成功したときだけこの値になる。

起動ログのエラー 2 件（`error loading kubeconfig files`）は **`-in-cluster` では kubeconfig を使わないため常に出る既存の無害な行**であり、本変更とは無関係。

## 🎉 ブラウザログインまで通った（2026-08-30 実測）

**利用者がブラウザで `developer` / `Developer-2026` によりログインし、クラスタのリソースが見える状態になった。**

apiserver のログが 4 段階で変わったことが、そのまま検証の記録である。

| 段階 | apiserver の応答 / ログ | 到達点 |
| --- | --- | --- |
| 修正前 | `lookup keycloak.localhost: no such host` → `authenticator not initialized` | 認証器が起動していない |
| `/etc/hosts` 追記後（自前トークン） | **403** `User "oidc:service-account-headlamp" cannot list nodes` | **認証成功**・RBAC で拒否 |
| ログイン直後 | `verify token: token is expired (19:23:55)` | 署名・issuer・audience を検証済み |
| **リロード後** | **認証エラーなし**（19:27:09 を最後に途絶） | 🎉 **通った** |

残る 404（`apiextensions.k8s.io/v1beta1`）は **k8s 1.22 で削除された API** を Headlamp が
念のため叩いているだけで、正常である。

RBAC は既に在った（`headlamp-developer-cluster-admin` が `User: oidc:developer` を `cluster-admin` へ）。

### 到達までに直した 2 つ（どちらも本 PR の外）

| # | 何が壊れていたか | どこで直したか |
| --- | --- | --- |
| 1 | `*.localhost` のワイルドカード証明書は標準 TLS クライアントに拒否される | **#1074** |
| 2 | apiserver（Go）が `.localhost` を解決できない（`curl` は解決できる） | **#1086** |

## 資格情報についての訂正

作業中、`developer` / `developer` を案内したが**誤り**だった。正しくは **`developer` / `Developer-2026`**。
`deploy/local/README.md:188` は正しく記載しており、**古かったのは #271 の issue 本文**である
（#780 第 2 段で realm の `passwordPolicy`（`length(12)` ＋ 3-of-4 文字種）へ適合させるため変更されていた）。

## 稼働環境で見つかった乖離（別 issue で扱う）

- **realm export は `developer` に `CONFIGURE_TOTP` を必須アクションとして持たせているが、
  稼働ユーザーは `requiredActions: []` で credential も `password` のみ。** TOTP が要求されない。
  PVC 永続化で `--import-realm` がスキップされ続けているためであり、**#438 の「TOTP 必須」が宣言だけになっている。**
- **アクセストークンは 5 分で切れる**（realm の `accessTokenLifespan: 300`）。
  Headlamp がリフレッシュしているかは未確認。

## 受け入れ基準

- [x] `HEADLAMP_CONFIG_OIDC_IDP_ISSUER_URL` が https エッジ issuer である
- [x] Headlamp が **検証つきで** discovery を引ける
- [x] Headlamp が `auth_type: oidc` を広告する
- [x] 🎉 **ブラウザで `developer` / `Developer-2026` によりログインでき、クラスタのリソースが見える**（利用者が実施・上記）
- [x] 暫定の SA トークン方式は併存させたまま残した（切り戻し経路）

## 切り戻し

- Headlamp のみ: 本ファイルの `HEADLAMP_CONFIG_OIDC_IDP_ISSUER_URL` を戻して `kubectl apply -k deploy/local/headlamp`
- apiserver ごと: VM 内の `/root/rollback-oidc.sh`
