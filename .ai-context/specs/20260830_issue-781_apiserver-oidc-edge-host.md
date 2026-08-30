---
title: 作業仕様書 — apiserver の OIDC 検証を再導入し、issuer host を解決させる（#781）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0008
  - ADR-0023
  - IADR-0076
  - IADR-0084
  - IADR-0091
  - IADR-0105
  - IADR-0310
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0008_kubernetes-runtime.md
related_specs:
  - "20260830_issue-781_edge-cert-explicit-sans.md"
issue: "#781"
---

# 作業仕様書 — apiserver の OIDC 検証（#781）

## 目的

`IADR-0105` が完全除去した apiserver の OIDC 検証を、https issuer の前提で再導入する。
判断の記録は [IADR-0310](../adr/IADR-0310_apiserver-oidc-edge-host-resolution.md)。

## 前提が揃うまでに直した 2 つ

| # | 前提 | どこで直したか |
| --- | --- | --- |
| 1 | issuer が https である | `#780` の作業で既に着地していた（issue 本文の「Traefik に一切露出していない」は古い） |
| 2 | **その証明書を検証できる** | 🔴 **`#1074` で是正。** `*.localhost` のワイルドカードは標準 TLS クライアントに拒否され、**検証すると必ず落ちていた** |

## 🔴 本体は `/etc/hosts` だった

k8s 1.30+ は issuer に https を強制するので issuer は `https://keycloak.localhost/...` になる。
**apiserver（Go）は `.localhost` を特別扱いしない。**

```
curl / musl   … RFC 6761 の特例で 127.0.0.1 へ解決する
Go のリゾルバ … 特例を持たず /etc/resolv.conf を引く → NXDOMAIN
```

apiserver のログ（`AppData/Local/rancher-desktop/logs/k3s.log`）が 10 秒ごとに繰り返していた:

```
oidc authenticator: initializing plugin: Get "https://keycloak.localhost/.../openid-configuration":
  dial tcp: lookup keycloak.localhost: no such host
"Unable to authenticate the request" err="[invalid bearer token, oidc: authenticator not initialized]"
```

## 検証（401 → 403 → 成功）

apiserver のログとレスポンスが 4 段階で変わったことが、そのまま検証の記録になっている。

| 段階 | apiserver の応答 / ログ | 到達点 |
| --- | --- | --- |
| 修正前 | `lookup keycloak.localhost: no such host` → `authenticator not initialized` | **認証器が起動していない** |
| `/etc/hosts` 追記後（自前トークン） | **403** `User "oidc:service-account-headlamp" cannot list resource "nodes"` | 🎉 **認証成功**・RBAC で拒否 |
| ブラウザログイン直後 | `verify token: token is expired (Token Expiry: 19:23:55)` | **署名・issuer・audience をすべて検証済み** |
| リロード後 | **認証エラーなし**（19:27:09 を最後に途絶） | 🎉 **通った** |

**401 → 403 の変化が `#781` の成立そのものである。** 401 は「認証できない」、403 は「認証できたが権限が無い」。

自前トークンは `headlamp` クライアントの service account を**一時的に**有効化して取得し、
検証後に無効へ戻した（その後の Keycloak 再起動で realm が再作成され、自動的にも戻っている）。

## 🔴 自分が犯した測定の誤り

**`curl` で到達できることを「apiserver から到達できる」根拠にした。**
同じ VM の中でも、`curl` が通ることは Go のプログラムが通ることを意味しない。

さらに **`IADR-0084` は既に答えを書いていた** ——
「ノード `/etc/hosts` に `<keycloak ClusterIP> keycloak` を追記すれば discovery/JWKS ともに 200」。
issuer host が `keycloak` → `keycloak.localhost` へ変わったことに、記録を読み直して追随できていなかった。
**先行 IADR が答えを持っている可能性を、着手前に引き直すこと。**

## 主張の限界

- **`/etc/hosts` の追記は WSL 再起動で消える。** 復旧は `APISERVER_OIDC=1` で本スクリプトを再実行する。
  恒久化は採らなかった（`IADR-0310` §結果）。
- **apiserver 設定はリポジトリ外**（VM の `/etc/rancher/k3s/`）に置かれる。
  スクリプトが冪等に再生成するが、**リポジトリの状態だけでは再現しない**。
- **CI では検証されない。** 経路B（Rancher Desktop）専用であり `integration-stack.yml` は k3d を使う。
