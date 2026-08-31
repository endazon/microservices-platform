---
title: IADR-0320 非 .NET ツールの OIDC は「ブラウザが開く URL だけをエッジ host にする」で追随させ、分離できないツールにはローカル CA を渡す
type: impl-adr
status: Accepted
related_ids:
  - NFR-09
  - NFR-11
  - ADR-0023
  - ADR-0047
  - IADR-0076
  - IADR-0086
  - IADR-0090
  - IADR-0091
  - IADR-0092
  - IADR-0093
  - IADR-0094
  - IADR-0095
  - IADR-0206
  - IADR-0220
  - IADR-0227
  - IADR-0243
  - IADR-0294
  - IADR-0310
  - IADR-0317
author: claude
created: 2026-08-31
updated: 2026-08-31
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/07_adr/ADR-0047_edge-cert-scope-local-route.md
---

# IADR-0320: ツール側 OIDC の issuer 追随（#780 第3段・最終段）

- 状態: Accepted
- 日付: 2026-08-31
- 決定者: claude（実装）
- Issue: **#780**（#442 の子 2／最後の子）。作業仕様書:
  [`20260831_issue-780_keycloak-edge-issuer-completion`](../specs/20260831_issue-780_keycloak-edge-issuer-completion.md)

## コンテキスト —— 決定は在ったが、実装されていなかった

[IADR-0243](./IADR-0243_keycloak-edge-issuer-migration.md)（#780 第2段・2026-08-22 Accepted）の **決定 3** は
こう書いている:

> 非 .NET の OIDC クライアント（Grafana/ArgoCD/Vault/MinIO/Headlamp/Wiki.js）は……
> 各ツールの「Keycloak を探す設定」……を `https://keycloak.localhost/realms/platform/...` へ更新する。

🔴 **その PR（#933）は、6 ツールの設定ファイルを 1 つも変更していない。**

```console
$ git show --stat 640effb0     # feat(NFR-09,IADR-0243): … (#933)
 deploy/keycloak/microservices-platform-realm.json  |   8 +-
 deploy/local/README.md                             |  13 +-
 deploy/local/infra/keycloak.yaml                   |  16 +-
 deploy/local/values-local.yaml                     |  11 ++
 …（grafana.yaml / argocd-cm-patch.yaml / vault の bootstrap.sh は 1 行も無い）
```

**結果として、稼働クラスタの Grafana と ArgoCD のブラウザログインは壊れていた**（2026-08-31 実測。
Grafana は `http://keycloak:8080/...` へ 302 し、ArgoCD は `/auth/login` が 500・`oidcConfig.issuer` は
**旧 realm 名のまま** `http://keycloak:8080/realms/microservices-platform` だった）。
Vault は `auth/oidc` が未有効、Wiki.js は OIDC ストラテジ未登録だった。

**「ADR に書いた」と「配線した」は別である。** IADR-0243 は決定 3 の検証項目を持っておらず
（§検証 は discovery の issuer 一致・`check-realm-constraints.js`・values の静的検査の 3 つだけ）、
**ツールが実際にどこへブラウザを飛ばすかを一度も測っていない。** 本 IADR はその穴を埋める。

## 決定 1 — 🔴 **3 つの URL を揃えない。「ブラウザが開く URL」だけをエッジ host にする**

OIDC の設定項目は、見た目が似ていても**誰が叩くか**が違う。

| 種別 | 誰が叩くか | 採る host |
| --- | --- | --- |
| authorization / logout / **issuer**（`iss` の照合値） | **ブラウザ** | **エッジ `https://keycloak.localhost`** |
| token / userinfo / discovery（metadata 取得） | **ツールの pod** | **in-cluster `http://keycloak:8080`** |

ブラウザは in-cluster 名を引けないので前者はエッジでなければならない。後者を in-cluster に残すのは、
**ローカル CA（cert-manager の `local-edge-ca`）を各コンテナの信頼ストアへ配らずに済ませる**ためである。

これは [IADR-0086](./IADR-0086_oidc-issuer-metadata-split.md) が .NET へ入れた
「metadata は in-cluster / issuer はエッジ」の分離を、**.NET 以外へ一般化したもの**である。
IADR-0243 §最大の技術リスク は「非 .NET のツールは IADR-0086 の分離が使えない」と書いていたが、
**それは正確ではなかった** —— `AddPlatformAuth` の機構は使えないが、**分離という設計は使える**。
Grafana（`AUTH_URL` / `TOKEN_URL` / `API_URL` が独立）と Wiki.js（5 つの endpoint が独立）は
設定項目が最初から分かれており、MinIO は discovery 経由で自動的にこの形になる。

| ツール | 実装 | 変更 |
| --- | --- | --- |
| **Grafana** | `GF_AUTH_GENERIC_OAUTH_AUTH_URL` のみエッジ。TOKEN / API は in-cluster | `deploy/local/observability/grafana.yaml` |
| **Wiki.js** | authorization / issuer / logout はエッジ、token / userinfo は in-cluster | `deploy/local/wiki-oidc/README.md` の seed |
| **MinIO** | **変更しない。** `configUrl` は in-cluster のままでよい | なし |
| **Headlamp** | 既にエッジ（#781 / [IADR-0310](./IADR-0310_apiserver-oidc-edge-host-resolution.md)） | なし |
| **platform-spa / BFF** | 既にエッジ（#1107 / [IADR-0316](./IADR-0316_bff-session-deploy-config.md)） | なし |

**MinIO を触らないことは実測にもとづく判断である。** MinIO は `configUrl` をサーバ側で引き、
ブラウザへ返す認可 URL に **discovery の `authorization_endpoint` をそのまま使う**。
Keycloak（hostname-v2）は `KC_HOSTNAME_URL` に従って**どの経路から引かれてもエッジ URL を広告する**ので、
in-cluster の `configUrl` から取った discovery にもエッジ URL が載る。実測でブラウザは
`https://keycloak.localhost/...` へ飛んでいた。**動いているものを触らない。**

## 決定 2 — 分離できないツール（ArgoCD / Vault）にはローカル CA を渡す。**PEM はリポジトリに焼き込まない**

| ツール | 分離できない理由 | 渡し方 |
| --- | --- | --- |
| **ArgoCD** | `oidc.config.issuer` **1 つ**が discovery（server）とブラウザの飛び先の両方を決める | `oidc.config.rootCA` |
| **Vault** | `oidc_discovery_url` と**その文書の `issuer` の一致を書き込み時に検証する**ため、in-cluster 名を残せない | `oidc_discovery_ca_pem` |

**CA は cert-manager が実行時に作るものであり、クラスタを作り直すと変わる。**
したがってリポジトリには**プレースホルダ**だけを置き、値は適用の直前に live から読む。

- ArgoCD: `deploy/local/argocd/oidc/argocd-cm-patch.yaml` に `__LOCAL_EDGE_ROOT_CA_PEM__` を置き、
  `scripts/k8s-local-up.sh` が `cert-manager/local-edge-root-ca` の PEM を 6 スペース字下げして差し込む。
- Vault: `deploy/local/vault/oidc/bootstrap.sh` が同じ Secret を読んで `oidc_discovery_ca_pem` へ渡す。

🔴 **CA が取れないときは `rootCA` の宣言ごと落とす**（fail-safe）。プレースホルダのまま patch すると
argocd-server が不正な PEM で OIDC を初期化できず、**break-glass の local admin まで巻き添えで落ちる**。
**「CA が無い」を「OIDC が成立しない」だけに留め、「ArgoCD に入れない」へ広げない。**

## 決定 3 — 🔴 **検証器が検証を切らない。** `verify-oidc-edge-flow.sh` は既定で証明書を検証する

同スクリプトは全リクエストで `curl -k` を使っていた。**#1074 はエッジ証明書の SAN が足りず
`https://keycloak.localhost` の検証が落ちていた事故だが、測る側が全員 `-k` を付けていたため
誰も気付かなかった。** 検証器がこれをやっていては、証明書の欠陥は利用者のブラウザで初めて見つかる。

CA は ①`OIDC_CA_BUNDLE` ②`cert-manager/local-edge-root-ca` から自動取得、の順に解決し、
どちらも無ければ `-k` へ落ちて**警告を出す**（CA を持たない環境でスクリプト全体が使えなくなるほうが
害が大きい）。`OIDC_TLS_INSECURE=1` は切り分け用の明示的な逃げ道である。

Windows の curl は schannel なので私有 CA では失効確認が `unknown` になり接続ごと落ちる。
`--ssl-revoke-best-effort` を**対応しているときだけ**足す —— これが緩めるのは**失効確認だけ**で、
チェーン検証とホスト名照合は有効なまま残る（`-k` とは別物である）。

## 決定 4 — 登録した TOTP シークレットを保存する。**「1 回しか通らない検証器」を直す**

`verify-oidc-edge-flow.sh` は **同じスタックで 2 回目を走らせると必ず落ちる**状態だった。

1 回目は `CONFIGURE_TOTP` の登録画面から hidden の生シークレットを拾えるが、**登録した瞬間に
その画面は二度と出ない**（2 回目以降は `login-otp`）。Keycloak の Admin API は登録済み OTP の
`secretData` を返さないので、**一度登録したら誰にも復元できない。**

```console
（着手前・稼働クラスタ）
[4/11] 資格情報を POST し、redirect の認可コードを取る
  FAIL  OTP の段に入ったがシークレットを解決できない（developer・field=otp・生シークレット=なし）
```

したがって**登録に成功したときだけ**（＝認可コードが返ったときだけ）シークレットを状態ファイルへ
書き、次回はそこから読む。置き場は既定で一時ディレクトリ（`OIDC_TOTP_STATE_DIR` で変更可）とし、
**リポジトリを汚さない**。失敗した値を残すと次回はその値で必ず落ちるので、**間違った状態を持ち越さない。**

## 決定 5 — `REQUIRED_CLIENT_URLS` を 7 クライアント＋`bff` へ広げ、`##` 連結の post-logout も見る

「片方の経路だけ足して片方を忘れる」事故を止めるための宣言表が、**その事故を最も起こしやすい
6 クライアントを見ていなかった**（宣言は `wiki-js` の 1 件だけ）。

併せて **`attributes.post.logout.redirect.uris`（`##` 区切りの 1 本の文字列）** を検査対象へ入れた。
redirect / origin と別フィールドなので、ここでも片方だけ足す事故が起きる（#780 本文が
「3 フィールドすべてに追記が要る」と名指ししていた箇所である）。

## 実測（稼働 k3s `v1.35.4+k3s1` / Istio エッジ・2026-08-31）

**すべて証明書検証を有効にした curl で測った。** ブラウザ OIDC を持つ **7 クライアントすべて**で、
`ツールの login → Keycloak の認可 → ツールの callback → セッション確立` を端から端まで通した。

| クライアント | 起点 | 認可 | callback | セッション |
| --- | --- | --- | --- | --- |
| platform-spa（BFF） | `/bff/auth/login` 302 | 302 | `/bff/auth/callback` 302 | `__Host-msp-session` |
| grafana | `/login/generic_oauth` 302 | 302 | 302 | `grafana_session` |
| argocd | `/auth/login` 303 | 302 | `/auth/callback` 303 | `argocd.token` |
| headlamp | `/oidc?cluster=main` 302 | 302 | `/oidc-callback` 303 | `headlamp-auth-main.0` |
| minio | console の JSON | 302 | `/oauth_callback` → 交換 204 | `token` |
| vault | `auth_url` 200 | 302 | `/v1/auth/oidc/oidc/callback` 200 | `client_token` 発行 |
| wiki-js | `/login/<key>` 302 | 302 | `/login/<key>/callback` 302 | `jwt` |

> 🔴 **測定中に `platform` realm が 3 回作り直された**（`admin` の `sub` が
> `928cb7ab…` → `ab64c9c8…` → `e96ae04f…`。別セッションとクラスタを共有している）。
> **realm 世代が変わると、その前にログイン済みだった利用者はツール側で必ず落ちる** ——
> Wiki.js は `users_providerkey_email_unique` 違反、Grafana は `user already exists`、
> BFF は JWKS の `kid` 不一致（`IDX10503`）である。**どれも本作業とは無関係の環境要因**だが、
> **これを知らずに測ると「直っていない」と読む。** 上表は世代 `e96ae04f…`・2026-08-31T12:25Z の
> 一括実測であり、その世代で初ログインになる利用者を選んである。
>
> **Grafana を `admin` で測ると落ちる。** `Failed to create user: user already exists` ——
> Grafana 組み込みの break-glass local admin（[IADR-0090](./IADR-0090_grafana-keycloak-oidc-generic-oauth.md)）と
> 名前が衝突するためで、**issuer とは無関係**である。`poc-user` では成立する。
> **逆に MinIO は `admin` でしか成立しない** —— `policy` クレームが `minio:consoleAdmin` client ロール由来で、
> それを持つのは `admin` だけだからである（[IADR-0103](./IADR-0103_local-sso-persistence-and-claim-design.md) 決定 2）。
> **「どの利用者で測ったか」を書かない実測は、この 2 つを取り違える。**

`scripts/verify-oidc-edge-flow.sh` は **hosts 追記も port-forward も無しで、証明書検証を有効にしたまま、
同じスタックで連続 2 回** 完走した（PASS 19 / FAIL 0 → PASS 18 / FAIL 0・いずれも段 11/11・EXIT=0）。

## 却下した代替案

| 案 | 却下理由 |
| --- | --- |
| 6 ツールとも 3 URL をエッジへ揃える | ローカル CA を 6 コンテナへ配る必要が出る。**分けられるものを分けないほうが複雑になる**（決定 1） |
| ArgoCD に `oidc.tls.insecure.skip.verify` を使う | 検証を切るのは #1074 の事故と同型。CA を渡せる以上、切る理由が無い |
| CA の PEM を `argocd-cm-patch.yaml` へ直接コミットする | クラスタを作り直すと変わる値であり、**必ず腐る**。`ADR-0023` の「CA 固有設定は ClusterIssuer に閉じる」にも反する |
| MinIO の `configUrl` もエッジへ移す | 実測で不要（決定 1）。MinIO コンテナへ CA を配る必要が出るだけである |
| TOTP をリセットするコマンドをスクリプトへ足す | 管理者資格が要る **書き込み**を検証器へ持ち込むことになる。保存で足りる（決定 4） |
| Wiki.js の OIDC 登録を `k8s-local-up.sh` で自動化する | [IADR-0095](./IADR-0095_wikijs-keycloak-oidc.md) が「DB / 管理 UI 保持で manifest 自動化不可」と決めている。#780 の射程を越えるため別 issue（#1127）へ送る |

## 影響

- **`IADR-0091` 決定 5 と却下代替案「Keycloak も 50000 集約」に Supersede 注記を入れた**（#780 受け入れ基準 7）。
  Supersede したのは [IADR-0243](./IADR-0243_keycloak-edge-issuer-migration.md) であり、本 IADR は注記を入れただけである
  —— **ID を後継へ付け替えていない。**
- **`IADR-0076` 決定 3 の「手順A 主 / 手順B 任意」の主従に Supersede 注記を入れた**（同基準 6）。
  基準が求めた二択のうち **「新 IADR を起こす」を選んだ**（[IADR-0243](./IADR-0243_keycloak-edge-issuer-migration.md) が既にそれである）。
- `admin:50000` の TLS 化（同基準 8）は **[IADR-0220](./IADR-0220_admin-entrypoint-tls-and-http-redirect.md) が済ませており、
  [IADR-0317](./IADR-0317_istio-ingressgateway-edge-and-strict-mtls.md) の Istio 移行後も維持されている**（7 host すべて検証つき https で応答）。
  **別 issue へ送るものは無い。**
