---
title: 作業仕様書 — #780 の残作業（受け入れ基準 8 項目の実測と、ツール側 issuer 追随の完了）
type: spec
status: done
related_ids:
  - NFR-09
  - NFR-11
  - ADR-0023
  - ADR-0026
  - ADR-0047
  - IADR-0076
  - IADR-0086
  - IADR-0090
  - IADR-0091
  - IADR-0092
  - IADR-0094
  - IADR-0095
  - IADR-0103
  - IADR-0206
  - IADR-0220
  - IADR-0227
  - IADR-0243
  - IADR-0294
  - IADR-0310
  - IADR-0317
  - IADR-0320
author: claude
created: 2026-08-31
updated: 2026-08-31
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/07_adr/ADR-0023_edge-cert-automation-cert-manager-letsencrypt.md
  - planning:projects/microservices-platform/07_adr/ADR-0047_edge-cert-scope-local-route.md
---

# 作業仕様書: #780 の残作業（#442 の最後の子）

- Issue: **#780**（親 **#442**。兄弟 #779 / #781 / #782 / #783 はすべて closed）
- 実装 ADR: 本作業で **IADR-0320** を起こす
- 基点: `develop` `c45533bc`

## 起点となる計画書（トレーサビリティ）

- 非機能要件: **NFR-09**（認証・認可）／**NFR-11**（全経路の HTTPS 化。**適用範囲は環境を問わない**）
- 計画 ADR: **ADR-0023**（エッジ証明書の自動化）／**ADR-0047**（経路B にも及ぶ）／**ADR-0026**（認証ポリシー）
- 実装 ADR: [IADR-0243](../adr/IADR-0243_keycloak-edge-issuer-migration.md)（#780 第2段。issuer をエッジへ移した）／
  [IADR-0076](../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md)（手順A / 手順B）／
  [IADR-0091](../adr/IADR-0091_local-edge-aggregation-traefik.md)（決定 5＝issuer 最小案）／
  [IADR-0317](../adr/IADR-0317_istio-ingressgateway-edge-and-strict-mtls.md)（エッジが Istio へ移った）

## 目的・背景

#780 は「Keycloak をエッジへ出し OIDC issuer を https のエッジ host へ移す」である。
**インフラ側（Keycloak のエッジ露出・`KC_HOSTNAME_URL` の変更・pod 側の名前解決）は
第1段（IADR-0227）・第2段（IADR-0243）で既に着地している。** 残っているのは次の 3 種である。

1. **配線の積み残し** —— IADR-0243 **決定 3** は「非 .NET の 6 ツールの『Keycloak を探す設定』を
   `https://keycloak.localhost/...` へ更新する」と決めたが、**PR #933 は 1 ファイルも更新していない**
   （変更されたのは realm.json / keycloak.yaml / values-local.yaml / docs / 検査器のみ。§2 の実測）。
   その結果、**Grafana と ArgoCD のブラウザログインは現在壊れている**（§2 で実測）。
2. **担保** —— `REQUIRED_CLIENT_URLS` が 7 クライアント中 1 つしか見ていない。
   `verify-oidc-edge-flow.sh` が同一スタックで 2 回目に完走できない。
3. **記録** —— IADR-0076 の改定 / IADR-0091 決定 5 の Supersede。

## 1. 母集合を自分で引く（IADR-0141 決定 1 / traceability.repo.md 規則 9・10）

### 軸 1 — 誤りの側から引く: 旧 issuer 文字列 `keycloak:8080`

`git grep -n "keycloak:8080"`（`.ai-context` と `CHANGELOG.md` を除外）で 39 件 / 18 ファイル。
**live な設定・スクリプトに限ると 8 ファイル**である。

| ファイル | 役割 | 判定 |
| --- | --- | --- |
| `deploy/local/observability/grafana.yaml` (626-628) | Grafana の AUTH/TOKEN/API URL | 🔴 **要修正**（AUTH_URL がブラウザ向け） |
| `deploy/local/argocd/oidc/argocd-cm-patch.yaml` (10) | ArgoCD の `oidc.config.issuer` | 🔴 **要修正**（server も browser も同じ issuer を見る） |
| `deploy/local/vault/oidc/bootstrap.sh` (25) | Vault `oidc_discovery_url` | 🔴 **要修正**（Vault は discovery の issuer と設定値の一致を要求する） |
| `deploy/local/values-local.yaml` (129) | MinIO `configUrl` | ⚪ **維持**（§3-d。discovery が返す issuer は既にエッジであり、ブラウザは正しく飛ぶ。実測済み） |
| `deploy/local/values-local.yaml` (18) | .NET の `Auth:MetadataAddress` | ⚪ **維持**（IADR-0243 決定 2 がそう決めている） |
| `deploy/local/infra/keycloak.yaml` (15) | コメント（metadata は in-cluster） | ⚪ 維持 |
| `deploy/helm/microservices-platform/values.yaml` | 本番像の既定値 | ⚪ 対象外（経路B の overlay の話） |
| `deploy/docker-compose.yml` | 経路A（compose） | ⚪ 対象外（エッジが無い経路） |

**docs（`deploy/local/**/README.md`）は手順の記述であり、上の設定変更に追随させる。**

### 軸 2 — 「あり得る形をすべて列挙してから引く」: 手順A の前提を語る記述

`#284 手順A` / `hosts 追記` / `port-forward svc/keycloak` の 3 語で走査した。
`deploy/local/argocd/README.md` / `deploy/local/minio-oidc/README.md` /
`deploy/local/observability/README.md` / `deploy/local/vault/oidc/README.md` /
`deploy/local/wiki-oidc/README.md` / `deploy/local/edge/README.md` / `deploy/local/README.md` が該当。

### 軸 3 — パスから引く（行フィルタで絞らない）

`deploy/local/**`・`deploy/keycloak/**`・`scripts/**` を全件見て、OIDC の endpoint を
組み立てている箇所を数えた。軸 1・2 で挙げた以外に
`deploy/local/headlamp/headlamp.yaml`（**既にエッジ issuer。#781 で追随済み**）がある。

### 除外したものと理由

- **`.ai-context/adr/` と `.ai-context/specs/`**: 凍結記録。本文プロズを後から書き換えない
  （traceability.repo.md §Superseded の凍結の射程）。**例外は「状態欄の追記」だけ**であり、
  本作業ではそれ（IADR-0076 / IADR-0091 への追記）を明示的に行う。
- **`CHANGELOG.md`**: 自動生成物。手で書き足さない。
- **`deploy/docker-compose.yml`（経路A）**: エッジそのものが無く、`keycloak:8080` が
  ブラウザからも到達できる（compose は host に port 公開する）。**issuer を移す理由が無い。**
- **`deploy/helm/.../values.yaml`（本番像）**: `edge.oidc` の既定は off。経路B の overlay の話であり、
  本番像の既定値を動かすと fail-safe を崩す（IADR-0076 決定 1 の流儀）。
- **`src/**` の `appsettings*.json`**: IADR-0243 決定 2 により `Auth:Authority` は不変。

## 2. 受け入れ基準 8 項目の着手前実測（稼働 k3s `v1.35.4+k3s1` / Istio エッジ・2026-08-31）

**すべて証明書検証を有効にした curl で測った**（`--cacert <local-edge root CA>`。
Windows の curl は schannel なので失効確認だけ `--ssl-revoke-best-effort` で緩めた。
チェーン検証とホスト名照合は有効である。`-k` は使わない —— #1074 の事故）。

| # | 基準 | 着手前 |
| ---: | --- | --- |
| 1 | 稼働 realm 名がリポジトリと一致 | 達成（`kcadm get realms` → master / **platform** / ai-stock-trading） |
| 2 | discovery の issuer と token の `iss` が完全一致 | 達成（両方 `https://keycloak.localhost/realms/platform`） |
| 3 | ブラウザ OIDC 7 クライアントすべてでログイン成立 | 🔴 **未達**（realm 側は 7/7 で code を発行するが、**Grafana / ArgoCD はツール側の設定が旧 issuer**） |
| 4 | `verify-oidc-edge-flow.sh` が hosts 追記・port-forward 無しで完走 | 🔴 **未達**（段 4 で停止。原因は hosts / port-forward ではなく **TOTP 登録済みで再走できない**） |
| 5 | `REQUIRED_CLIENT_URLS` に https 版 URL | 🔴 **未達**（`wiki-js` 1 件のみ） |
| 6 | IADR-0076 の改定 or 新 IADR | 🔴 **未達**（IADR-0243 が実質の新 IADR だが、IADR-0076 側に何も残っていない） |
| 7 | IADR-0091 決定 5 の Supersede | 🔴 **未達**（IADR-0243 の §Superseded に片側だけ在り、IADR-0091 側は無注記） |
| 8 | admin:50000 の TLS 化の扱い | **既に TLS**（IADR-0220 が終端。#1109 で Istio `msp-admin-edge` へ移っても維持。7 host すべて検証つき https で応答） |

## 3. 対象範囲（この作業でやること）

### a. Grafana — ブラウザ向けだけをエッジへ移す（**metadata / issuer 分離の一般化**）

`GF_AUTH_GENERIC_OAUTH_AUTH_URL` のみ `https://keycloak.localhost/...` にする。
`TOKEN_URL` / `API_URL` は **in-cluster のまま**にする。

**理由**: `AUTH_URL` は**ブラウザが開く URL**であり、`TOKEN_URL` / `API_URL` は
**Grafana pod がサーバ側で叩く URL**である。前者はエッジでなければ解決できず、
後者は in-cluster のままならローカル CA を Grafana コンテナへ配る必要が無い。
これは IADR-0086 が .NET へ入れた分離（metadata=in-cluster / issuer=エッジ）と同じ形であり、
**Grafana の generic_oauth が 3 つの URL を別々に取る設計をそのまま使う。**

### b. ArgoCD — issuer をエッジへ移し、CA を `oidc.config.rootCA` で渡す

ArgoCD は **server も browser も同じ `issuer` を使う**ため分離できない（IADR-0243 §最大の技術リスク）。
`argocd-cm` の `oidc.config` に `rootCA` を持たせ、`url` を https にする。
**CA の実体は cert-manager が実行時に作る**ため、リポジトリへ PEM を焼き込まない ——
`scripts/k8s-local-up.sh` が `local-edge-root-ca` から読んで注入する。

### c. Vault — `oidc_discovery_url` をエッジへ移し `oidc_discovery_ca_pem` を渡す

Vault は `oidc_discovery_ca_pem` を一次サポートしている。bootstrap は再実行可能な runtime 手順であり、
CA はその場でクラスタから読む。

### d. MinIO — **変更しない**（実測にもとづく）

MinIO は `configUrl` を **サーバ側で**引き、ブラウザへ返す認可 URL は
**discovery が返した `authorization_endpoint`** をそのまま使う。実測でブラウザは既に
`https://keycloak.localhost/...` へ飛んでいる。**動いているものを触らない。**

### e. Headlamp / platform-spa（BFF） — **変更しない**（既にエッジ issuer）

### f. Wiki.js — 手順書の値をエッジ issuer へ直し、**実測もする**

`deploy/local/wiki-oidc/README.md` の DB seed（`authentication` テーブル）の endpoint 5 値を、
決定 1 と同じ分離で書き直す（authorization / issuer / logout はエッジ、token / userinfo は in-cluster）。

> **［着手後の訂正］着手前は「実測できない」と書いていたが、誤りだった。**
> 当初は稼働クラスタの `wikijs.authentication` が **0 行**だったため OIDC ストラテジ自体が無く、
> IADR-0095 が「manifest 自動化不可」と書いていることから**対象外にしようとしていた**。
> しかし README には **DB seed の SQL 手順が既にあり**、それを流せばストラテジは登録できる。
> **「自動化できない」は「実測できない」ではない。** 実際に seed を流して
> ログイン成立まで測った（§6.2）。**自動化のほうは #1127 として分割起票した。**

### g. `check-realm-constraints.js` の `REQUIRED_CLIENT_URLS` を 7 クライアント＋`bff` へ広げる

### h. `verify-oidc-edge-flow.sh` を同一スタックで再走できるようにする

登録した TOTP シークレットを**状態ファイルへ保存**し、次回以降はそこから読む。
併せて **`OIDC_CA_BUNDLE` を与えたときは証明書検証を有効にする**（#1074 の事故の再発防止）。

### i. 記録 — IADR-0076 / IADR-0091 への追記、IADR-0320 の新設

## 4. やらないこと

- **`Auth:Authority`（.NET）の変更**（IADR-0243 決定 2 が決めている）
- **経路A（compose）の issuer 変更**（§1 の除外理由）
- **本番 values の既定変更**（fail-safe を崩さない）
- **realm の redirect URI 追加**（IADR-0243 決定 3 の実測どおり追加は要らない。§2 で 7/7 が code を発行した）

## 5. 受け入れ基準（この作業の完了条件）

1. Grafana の `/login/generic_oauth` が **`https://keycloak.localhost/...`** へ 302 する（実測）
2. ArgoCD の `/api/v1/settings` の `oidcConfig.issuer` が **エッジ issuer**で、`/auth/login` が
   認可エンドポイントへ 302 する（実測）
3. `check-realm-constraints.js` が 7 クライアント＋`bff` の https 版 URL を検査し、
   **1 件消すと落ちる**（変異試験）
4. `verify-oidc-edge-flow.sh` が **同じスタックで 2 回連続完走**する
5. IADR-0076 / IADR-0091 に traceability.repo.md の Superseded 書式で追記が入る
6. 検査器一式が緑

## 6. 実行結果（2026-08-31・稼働 k3s `v1.35.4+k3s1` / Istio エッジ）

**計測はすべて証明書検証を有効にした curl である**（`--cacert <cert-manager/local-edge-root-ca>` ＋
schannel 向けに `--ssl-revoke-best-effort`。**`-k` は 1 回も使っていない**）。

### 6.1 受け入れ基準 8 項目の到達点

| # | 基準 | 結果 |
| ---: | --- | --- |
| 1 | 稼働 realm 名がリポジトリと一致 | **達成**（`kcadm get realms` → master / **platform** / ai-stock-trading） |
| 2 | discovery の `issuer` と token の `iss` が完全一致 | **達成**（両方 `https://keycloak.localhost/realms/platform`。PKCE で取った実 token を base64url デコードして突合） |
| 3 | ブラウザ OIDC 7 クライアントすべてでログイン成立 | **達成（7/7）**。§6.2 |
| 4 | `verify-oidc-edge-flow.sh` が hosts 追記・port-forward 無しで完走 | **達成**。しかも**連続 2 回**・**証明書検証を有効にしたまま**（PASS 19 → PASS 18・いずれも FAIL 0 / 段 11/11 / EXIT=0） |
| 5 | `REQUIRED_CLIENT_URLS` に https 版 URL | **達成**（`wiki-js` 1 件 → **7 クライアント＋`bff`**。`##` 連結の post-logout も対象へ。変異試験で検出を確認） |
| 6 | `IADR-0076` の改定 or 新 IADR | **達成**。**「新 IADR を起こす」を選んだ**（IADR-0243 が既にそれである）。IADR-0076 決定 3 へ Supersede 注記 |
| 7 | `IADR-0091` 決定 5 の Supersede | **達成**。決定 5 と却下代替案「Keycloak も 50000 集約」の両方へ注記。**ID は付け替えていない** |
| 8 | admin:50000 の TLS 化の扱い | **既に TLS 済み。別 issue へ送るものは無い**。§6.3 |

### 6.2 7 クライアントの end-to-end 実測

`ツールの login → Keycloak の認可 → ツールの callback → セッション確立` を通した。

| クライアント | 起点 | 認可 | callback | セッション | 利用者 |
| --- | --- | --- | --- | --- | --- |
| platform-spa（BFF） | `/bff/auth/login` 302 | 302 | `/bff/auth/callback` 302 | `__Host-msp-session` | admin |
| grafana | `/login/generic_oauth` 302 | 302 | 302 → `/` | `grafana_session` | poc-user |
| argocd | `/auth/login` 303 | 302 | `/auth/callback` 303 → `/` | `argocd.token` | admin |
| headlamp | `/oidc?cluster=main` 302 | 302 | `/oidc-callback` 303 | `headlamp-auth-main.0` | admin |
| minio | console の JSON | 302 | `/oauth_callback` → 交換 **204** | `token` | admin |
| vault | `auth_url` 200 | 302 | `/v1/auth/oidc/oidc/callback` 200 | `client_token` 発行 | admin |
| wiki-js | `/login/<key>` 302 | 302 | `/login/<key>/callback` 302 | `jwt` | admin |

🔴 **利用者を書かない実測は取り違える。** Grafana は `admin` だと
`Failed to create user: user already exists`（組み込み local admin と名前が衝突。issuer とは無関係）で落ち、
逆に MinIO は `policy` クレーム（`minio:consoleAdmin` client ロール由来）を持つ `admin` でしか成立しない。

**着手前は Grafana / ArgoCD / Vault / Wiki.js の 4 つが不成立だった**:

```console
（着手前）
grafana   302 -> http://keycloak:8080/realms/platform/protocol/openid-connect/auth   ← ブラウザが解決できない
argocd    500 ；oidcConfig.issuer=http://keycloak:8080/realms/microservices-platform ← 旧 realm 名のまま
vault     403 permission denied（auth/oidc が未有効）
wiki-js   authentication テーブルに oidc 行なし
```

### 6.3 admin:50000 の TLS（基準 8）

7 host すべて、**証明書検証を有効にした https で応答した**。平文 http は接続ごと落ちる（TLS 専用の待受）。

```console
https://grafana.localhost:50000/  -> 302     https://argocd.localhost:50000/   -> 200
https://vault.localhost:50000/    -> 307     https://headlamp.localhost:50000/ -> 200
https://qdrant.localhost:50000/   -> 200     https://minio.localhost:50000/    -> 200
https://wiki.localhost:50000/     -> 200
http://<各 host>:50000/           -> curl: (52) Empty reply from server
```

TLS 終端は IADR-0220（#841）が入れ、#1109（IADR-0317）で Traefik → Istio `msp-admin-edge` へ
移送した後も維持されている。**新規に起票するものは無い。**

### 6.4 通した検査

| 検査 | 結果 |
| --- | --- |
| `check-realm-constraints.js`（＋ `--self-test` 62 件） | OK |
| `check-stack-ready.js` | OK（Deployment 31 件 available・エッジ / issuer / admin entrypoint 成立） |
| `check-trace-blocks.js` | OK（159 件） |
| `check-doc-links.js` | OK（1032 件） |
| `gen-knowledge-graph.js --check` | OK |
| `check-adr-numbering.js` | OK |
| `check-commit-messages.js` / `check-doc-updated.js` | OK |
| `k8s-local-up.test.js` / `scripts.test.js` | OK |
| `dotnet build src/platform/backend/backend.slnx` | OK |
| **`check-deploy-manifests.js`** | 🔴 **測れなかった**。`kubeconform` が PATH に無い（`helm` / `kubectl` は在る）。CI で走る |

### 6.5 測定を誤らせたもの（記録として残す）

- 🔴 **稼働クラスタは他セッションと共有している。** 作業中に Keycloak の realm が再構築され、
  `developer` の**利用者 ID が変わった**（`0e05f5e1…` → `9d41bbb9…`）。同時に `poc-user` に登録済みだった
  TOTP と、`wikijs.authentication` の行が消えた。**同じコマンドを 2 回打って違う答えが返る。**
  実測を貼るときは**いつ測ったか**を必ず添える。
- 🔴 **`check-deploy-manifests.js` の `hasTool` は `command -v` を使うため Windows では常に不在判定になる**
  と申し送られていたが、**本作業では `helm` / `kubectl` は正しく検出された**。落ちた原因は
  `kubeconform` が本当に入っていないことである。**申し送りを検証せずに引くと誤る。**

## 7. 残した follow-up

- **#1127**: Wiki.js の OIDC ストラテジ DB seed の自動化（#397 の再起票）。#780 の基準は満たしているが、
  **新しいスタックでは人手の SQL が要る**状態が残る。
