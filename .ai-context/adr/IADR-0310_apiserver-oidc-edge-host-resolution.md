---
title: IADR-0310 apiserver の OIDC は issuer host を /etc/hosts で解決させる（curl の到達性確認は根拠にならない）
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0008
  - ADR-0023
  - IADR-0076
  - IADR-0084
  - IADR-0091
  - IADR-0105
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0008_kubernetes-runtime.md
---

# IADR-0310: apiserver の OIDC 検証と issuer host の名前解決（#781）

- 状態: Accepted
- 日付: 2026-08-30
- 決定者: claude（実装）／apiserver 設定の適用は利用者が実施

## 起点・関連

- 実装 issue: **#781**（#442 の子 3）／ 波及 **#388** / **#271**
- 先行: [IADR-0084](./IADR-0084_headlamp-oidc-apiserver-flags.md)（apiserver フラグの調査）／
  [IADR-0105](./IADR-0105_remove-apiserver-oidc-flag-wiring.md)（同経路の完全除去）
- 作業仕様書: [20260830_issue-781](../specs/20260830_issue-781_apiserver-oidc-edge-host.md)

## コンテキストと課題

`IADR-0105` が `k8s-local-up.sh` から完全除去した apiserver の OIDC 検証を、
https issuer（`#780` で成立・`#1074` で証明書を検証可能に是正）の前提で再導入する。

## 決定

### 決定 1 — `APISERVER_OIDC=1` の opt-in とし、経路B 専用にする

`RUNTIME != rancher` なら何もしない。k3d は apiserver への設定投入方法が違う。
**既定オフ。** 未設定なら `k8s-local-up.sh` の挙動は 1 バイトも変わらない。

### 決定 2 — 🔴 **issuer host を `/etc/hosts` へ入れる。これが本体である**

k8s 1.30+ は issuer に https を強制するため issuer は `https://keycloak.localhost/...` になる。
**apiserver（Go）は `.localhost` を特別扱いしない。**

| 実装 | `.localhost` の扱い |
| --- | --- |
| `curl` / musl | RFC 6761 の特例で **127.0.0.1 へ解決する** |
| **Go のリゾルバ**（apiserver） | **特例を持たない。** `/etc/resolv.conf` を引き **NXDOMAIN** |

**実測（2026-08-30）**: apiserver のログが 10 秒ごとに次を繰り返していた。

```
oidc authenticator: initializing plugin: Get "https://keycloak.localhost/.../openid-configuration":
  dial tcp: lookup keycloak.localhost: no such host
"Unable to authenticate the request" err="[invalid bearer token, oidc: authenticator not initialized]"
```

`/etc/hosts` へ `127.0.0.1 keycloak.localhost` を入れると、**再起動なしで**次の再試行が成功した
（`IADR-0084` が記録したとおり apiserver は起動をブロックせず背景で再試行する）。

### 決定 3 — 切り戻しを設定と同時に置く

`/root/rollback-oidc.sh` を必ず置く。**apiserver の起動失敗はクラスタ停止を意味する**
（`#388` が記録した 10 回連続失敗の前科）。

## 🔴 この作業で犯した測定の誤り（同型の再発を防ぐために残す）

**`curl` でエンドポイントに到達できることを「apiserver から到達できる」ことの根拠にした。**

```console
$ rdctl shell curl -sk https://keycloak.localhost/.../openid-configuration
→ HTTP 200        ← これを「到達性は問題ない」と読んだ
```

**同じ VM の中でも、`curl` が通ることは Go のプログラムが通ることを意味しない。**
名前解決の意味論が違うためである。**検証は実際の消費側と同じ解決系で行う。**

`IADR-0084` は既に「ノード `/etc/hosts` に `<keycloak ClusterIP> keycloak` を追記すれば
discovery/JWKS ともに 200」と**答えを書いていた**。当時の issuer host（`keycloak`）が
新しい host（`keycloak.localhost`）へ変わったことに、記録を読み直して追随できていなかった。
**先行 IADR が答えを持っている可能性を、着手前に引き直すこと。**

なお `IADR-0084` の枠組み（netns から測れ）は**別の問題**を指している。
今回のは netns ではなく**リゾルバの意味論**であり、同 IADR は覆らない。

## 結果

- **良い影響**: `#781` が成立し、`#388` / `#271` の前提が揃った。
  実測で 401 → 403 → 認証成功まで観測できた。
- **悪い影響 / トレードオフ**:
  - 🔴 **WSL は起動のたびに `/etc/hosts` を再生成するため、追記は失われる。**
    復旧は `APISERVER_OIDC=1` で本スクリプトを再実行すること。
    恒久化（`/etc/wsl.conf` の `generateHosts=false` や OpenRC の `local.d`）は**採らない** ——
    VM 全体の挙動を変える割に、dev 環境の起動手順を 1 回踏むだけで済むため。
  - apiserver 設定は**リポジトリ外**（VM の `/etc/rancher/k3s/`）に置かれる。
    スクリプトが冪等に再生成するが、**リポジトリの状態だけでは再現しない**。
- **フォローアップ**:
  1. Headlamp のアクセストークンは 5 分で切れる（realm の `accessTokenLifespan: 300`）。
     **リフレッシュが効いているかは未確認**であり、効いていなければ別途起票する。
  2. 稼働 realm の `developer` は `requiredActions: []` で **TOTP が実際には要求されない**
     （PVC 永続化で `--import-realm` がスキップされ続けているため）。`#438` の「TOTP 必須」が
     宣言だけになっている。**別 issue で扱う。**
