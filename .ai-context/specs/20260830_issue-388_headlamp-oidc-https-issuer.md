---
title: 作業仕様書 — Headlamp の OIDC issuer を https エッジへ移す（#388 / #271・#781 の帰結）
type: spec
status: in-progress
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

## 🔴 まだ確かめていないこと

**ブラウザでの実ログイン（`developer` / `developer`）を通していない。**

- **理由**: 資格情報の入力は行わない方針による。dev 専用の平文認証情報であっても同じ扱いにする。
- **したがって「apiserver が Headlamp の id_token を受理する」ことは未検証である。** 確かめられたのは
  「Headlamp が issuer を https で検証つきに解決でき、OIDC モードで動いている」ところまで。
- **apiserver 側の OIDC が実際に効いていることも機械的には未確認**である。試したが決め手にならなかった:
  - `ps` に `--oidc-*` は出ない（k3s は apiserver を内包するため）
  - `/metrics` は本セッションから取得できない（1 行しか返らない）
  - bearer のみのガベージ token は 401 だが、**OIDC 有無のどちらでも 401** なので判別に使えない
  - 唯一の間接証拠は「**http issuer なら起動に失敗していた**（本 issue が記録した前科）ところ、https で起動した」こと

**受け入れ判定はブラウザログインで行う**（下記）。

## 受け入れ基準

- [x] `HEADLAMP_CONFIG_OIDC_IDP_ISSUER_URL` が https エッジ issuer である
- [x] Headlamp が **検証つきで** discovery を引ける
- [x] Headlamp が `auth_type: oidc` を広告する
- [ ] 🔴 **ブラウザで `developer` / `developer` によりログインでき、クラスタのリソースが見える**（**利用者が実施**）
- [ ] 暫定の SA トークン方式が引き続き使える（併存。切り戻し経路として残す）

## 切り戻し

- Headlamp のみ: 本ファイルの `HEADLAMP_CONFIG_OIDC_IDP_ISSUER_URL` を戻して `kubectl apply -k deploy/local/headlamp`
- apiserver ごと: VM 内の `/root/rollback-oidc.sh`
