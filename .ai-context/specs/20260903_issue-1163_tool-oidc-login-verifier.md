---
title: 作業仕様書 — ツール側 7 クライアントの OIDC ログイン開始を検証器へ載せる（人が手で測った主張を機械の実測へ置き換える）
type: spec
status: done
related_ids:
  - NFR-09
  - NFR-11
  - ADR-0023
  - ADR-0032
  - ADR-0047
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - "NFR-09（セキュリティ｜認証・認可: 全 API / 全ツールで OIDC を通す）"
  - "NFR-11（平文 HTTP を残さない。admin entrypoint も TLS 終端）"
  - "ADR-0047（エッジ証明書の適用範囲。ローカル経路）"
related_adrs:
  - IADR-0363
  - IADR-0328
  - IADR-0316
  - IADR-0310
  - IADR-0342
  - IADR-0255
  - IADR-0095
  - IADR-0094
  - IADR-0093
  - IADR-0092
  - IADR-0091
  - IADR-0090
  - IADR-0084
  - IADR-0103
  - IADR-0206
  - IADR-0220
  - IADR-0227
  - IADR-0243
issue: "#1163"
---

# 作業仕様書: ツール側 OIDC ログイン導線の検証器（#1163）

## 背景と問題

`scripts/verify-oidc-edge-flow.sh` は **SPA → BFF の 1 経路専用**である。経路B にはブラウザ OIDC を
持つツール側のクライアントが他にもあり、**それらのログイン導線を測る検証器は無い**。

IADR-0328 §実測 は「ブラウザ OIDC を持つ 7 クライアントすべてで端から端まで通した」と表を残しているが、
これは **2026-08-31 に人が手で 1 つずつ curl した結果**である。再現も回帰検知もできない。
同型の縮退（ストラテジが消える / issuer がずれる / Site URL と realm の redirect が食い違う）は
**Pod が Running のまま**起きるため、誰も再実行しないかぎり気付かない。

🔴 **本作業の着手前の実測で、その縮退が既に 1 件起きていた**（後述 §実測 の Vault）。
「7/7 で通る」は 4 日で偽になっていた。

## 母集合 —— 自分で走査した結果（issue の数えを転記しない）

### 走査 1: realm JSON のブラウザ OIDC クライアント

`deploy/keycloak/microservices-platform-realm.json` の `clients[]` を全件（11 件）読み、
`standardFlowEnabled: true` かつ `redirectUris` が非空のものを取った（＝ブラウザが認可コード
フローを開始しうるクライアント）。**8 件**が該当する。

| clientId | public | redirectUris（エッジ経路のもの） |
| --- | --- | --- |
| `wiki-js` | false | `https://wiki.localhost:50000/*` |
| `bff` | false | `https://localhost/bff/auth/callback` |
| `platform-spa` | true | `https://localhost/*` |
| `headlamp` | false | `https://headlamp.localhost:50000/*` |
| `grafana` | false | `https://grafana.localhost:50000/login/generic_oauth` |
| `argocd` | false | `https://argocd.localhost:50000/auth/callback` |
| `minio` | false | `https://minio.localhost:50000/oauth_callback` |
| `vault` | false | `https://vault.localhost:50000/ui/vault/auth/oidc/oidc/callback` |

残る 3 件（`abac-seeder` / `identity-admin` / `ai-stock-trading-kb-writer`）は
`standardFlowEnabled: false` かつ `redirectUris: []`＝**ブラウザを開かない**（client_credentials）。
母集合外。

### 走査 2: 8 件から「ブラウザが実際に開く URL を持つ」ものへ絞る

**`platform-spa` を除外する。** ADR-0032（BFF セッション方式 / Token Handler）の移行後、
SPA はトークンを扱わず、**ブラウザのログイン開始は `bff` の `/bff/auth/login`** である
（`oidc-client-ts` は撤去済み。CLAUDE.md「認証」）。`platform-spa` の登録は
`verify-oidc-edge-flow.sh` の段 3〜5（旧経路の実測）が使っており、**そちらの検証器が既に測っている**。
本検証器で二重に測らない。

→ **母集合 = 7 クライアント**。IADR-0328 §実測 の表と一致する（issue 本文の列挙は
「Keycloak 管理コンソール相当」を 7 番目に数えているが、**realm を走査した結果はそうではない** ——
7 番目は `bff` である。管理コンソール（`security-admin-console`）は Keycloak 組み込みで
realm JSON に無く、ツール側の配線対象でもない）。

### 走査 3: 各クライアントの「ブラウザが開く URL」

各ツールの配備マニフェスト・bootstrap・IADR-0328 §実測 から引いた。

| # | client | ログイン開始 URL（ブラウザが最初に叩く） | 出所 |
| --- | --- | --- | --- |
| 1 | `bff` | `https://localhost/bff/auth/login` | IADR-0316 / IADR-0328 §実測 |
| 2 | `grafana` | `https://grafana.localhost:50000/login/generic_oauth` | `deploy/local/observability/grafana.yaml`・IADR-0090 |
| 3 | `argocd` | `https://argocd.localhost:50000/auth/login` | `deploy/local/argocd/oidc/argocd-cm-patch.yaml`・IADR-0092 |
| 4 | `headlamp` | `https://headlamp.localhost:50000/oidc?cluster=main` | IADR-0084 / IADR-0310 |
| 5 | `minio` | `https://minio.localhost:50000/api/v1/login` の `redirectRules[].redirect`（JSON） | IADR-0093 |
| 6 | `vault` | `POST https://vault.localhost:50000/v1/auth/oidc/oidc/auth_url` の `data.auth_url`（JSON） | `deploy/local/vault/oidc/bootstrap.sh`・IADR-0094 |
| 7 | `wiki-js` | `https://wiki.localhost:50000/login/<strategyKey>` | `deploy/local/wiki-oidc/README.md`・IADR-0095 / IADR-0342 |

🔴 **`wiki-js` の `<strategyKey>` はスクリプトに書けない。** seed の SQL は
`COALESCE((SELECT a.key FROM authentication a WHERE a."strategyKey"='oidc' …), '7c1f6f2e-…')` で
**既存キーを再利用する**ため、DB を作り直した環境では値が変わる。GraphQL
`{authentication{activeStrategies(enabledOnly:true){key strategy{key}}}}` を**未認証で引ける**ことを
実測したので、そこから導く。

### 走査 4: エッジ host が稼働ルーティングと一致するか

`kubectl get ingress -A` / `kubectl get virtualservice -A` で、`*.localhost` の 7 host
（keycloak / argocd / grafana / headlamp / minio / vault / wiki）が Traefik Ingress と
Istio VirtualService の両方で宣言されていることを確認した（`qdrant` は OIDC を持たないため対象外）。

## 決定（詳細は IADR-0363）

1. **別スクリプト `scripts/verify-tool-oidc-logins.sh` を置く**（既存へ段を足さない）。
2. **段は 3 種**: (a) ログイン開始がエッジ Keycloak の認可端点へ向くこと、
   (b) その認可 URL で Keycloak のログインフォームが返ること（＝`redirect_uri` が realm に登録済み）、
   (c) **陰性対照**（未登録の `redirect_uri` は 400 で拒まれること）。
   (c) が無いと (b) の PASS は「Keycloak が何でも通している」ときと区別できない。
3. **資格情報 POST は行わない**（読み取り専用。#1163 §射程外）。TOTP を要する段は持たない。
4. **`-k` を持たない。** CA を解決できなければ **exit 2**（前提未整備）で終える。
   既存スクリプトの fail-safe（CA が無ければ `-k` へ落ちる）は**引き継がない** —— 本検証器は
   TLS 検証そのものが測定対象の一部だからである（#1074）。
5. **段数の合計をゲートする**（`STEPS` vs `TOTAL`）。到達できないツールは **skip ではなく段を消費**し、
   判定は FAIL とする。ゲート未有効（＝ツールが配備されていない）だけを SKIP 扱いにする。
6. **期待値を列挙で持たない**: 認可端点は Keycloak の discovery から、`redirect_uri` の妥当性は
   **Keycloak 自身の判定**（段 b）から、Wiki.js のキーは Wiki.js から引く。

## SKIP と FAIL の分け方（#1163 受け入れ基準 6 / 「検証器が検証を切らない」）

| 観測 | 判定 | 理由 |
| --- | --- | --- |
| ツールのエッジ host が応答しない（接続不可 / 404 / 503） | **SKIP**（段は消費・FAIL にしない） | opt-in ゲート未有効＝そのツールを配備していない |
| ツールは応答するが、ログイン開始が認可端点へ向かない | **FAIL** | 配備済みなのに OIDC が縮退している |
| 認可 URL でログインフォームが返らない | **FAIL** | `redirect_uri` 未登録 / client 消失 |
| 陰性対照が 400 にならない | **FAIL** | Keycloak の登録検査が効いていない＝段 (b) が無意味 |
| **7 件すべて SKIP** | **exit 2** | 何も測っていない実行を「緑」と呼ばせない |
| CA を解決できない | **exit 2** | 検証を切って測るくらいなら測らない |

## 受け入れ基準

1. `bash scripts/verify-tool-oidc-logins.sh` が 7 クライアントすべてについて段を刻み、
   落ちたクライアントを**名指し**する。
2. 期待値（issuer host / `redirect_uri` / Wiki.js のキー）を**スクリプト内の列挙で持たない**。
3. `-k` を一切使わない（`grep -c ' -k' ` が 0）。CA はクラスタから自動取得する。
4. 前提未整備は exit 2、導線の失敗は exit 1 で区別する。
5. 読み取り専用（利用者を作らない・ログインを完了させない）。
6. 未配備のツールを FAIL にしない。ただし**全件未配備なら exit 2**。
7. `scripts/scripts.repo.test.js` が (i) 段数の合計 (ii) 7 クライアント全部の段の存在
   (iii) `-k` / `CURL_K` の不使用 を固定する。**(i)(ii) は宣言の grep で済ませず、
   スタブへ向けて実際に走らせ、段を 1 本消すと門が落ちること（変異試験）まで見る。**
8. 稼働 k3s に対する実走の生出力を PR へ貼る（**対照つき** —— 陽性 2 種・前提未整備 2 種）。

## 実測（着手前・稼働 k3s `v1.35.4+k3s1` / Istio エッジ・2026-09-03）

すべて `--cacert`（`cert-manager/local-edge-root-ca`）＋ `--ssl-revoke-best-effort` で測った。

| # | client | 開始 | 認可先 | 判定 |
| --- | --- | --- | --- | --- |
| 1 | `bff` | 302 | `…/auth?client_id=bff&request_uri=urn:ietf:params:oauth:request_uri:…` | OK（**PAR**。`redirect_uri` は URL に出ない） |
| 2 | `grafana` | 302 | `client_id=grafana&redirect_uri=https://grafana.localhost:50000/login/generic_oauth` | OK |
| 3 | `argocd` | 303 | `client_id=argocd&redirect_uri=https://argocd.localhost:50000/auth/callback` | OK |
| 4 | `headlamp` | 302 | `client_id=headlamp&redirect_uri=https://headlamp.localhost:50000/oidc-callback` | OK |
| 5 | `minio` | 200(JSON) | `client_id=minio&redirect_uri=https://minio.localhost:50000/oauth_callback` | OK |
| 6 | `vault` | **403** | —（`auth_url` が `permission denied`・`ui/mounts` の `data.auth` は `{}`） | 🔴 **縮退**（下の対照つき） |
| 7 | `wiki-js` | 302 | `client_id=wiki-js&redirect_uri=https://wiki.localhost:50000/login/<key>/callback` | OK |

**Vault からはログイン開始 URL を得られない。** ブラウザが OIDC ログインを開始できないという
観測そのものは確定である（`auth_url` が HTTP 403）。ただし **403 の理由は 403 だけでは決まらない** ——
存在しない mount（`auth/msp-no-such-mount/...`）も同じ `{"errors":["permission denied"]}` を返すことを
実測した（陰性側の対照）。理由を分けているのは時間と揮発性である:

- 同じ口は **2026-08-31 に `auth_url` を返していた**（IADR-0328 §実測）。
- vault Pod は **2026-09-02T09:40:57Z に 19 回目の再起動**をしている（`restartCount=19`）。
- dev Vault はインメモリで、Pod 再起動で `auth/oidc` ごと消える（runbook の揮発マトリクスの宣言）。
- **未認証で引ける口は生きている**（`sys/health`=200・`sys/seal-status` が `sealed:false` を返す＝陽性対照）。
  つまり「Vault ごと落ちている」ではなく「OIDC の配線だけが消えている」。

IADR-0328 が「7/7 で通した」と書いてから 4 日で偽になっており、
**本 issue が想定したとおりの縮退が、本 issue の着手時点で既に起きていた。**
本作業は検証器を置くところまでで、**Vault の復旧（STEP 2 の再走）は射程外**とする
（読み取り専用の原則を破らない）。

### 側所見: glibc の `*.localhost` は ArgoCD の OIDC を壊していない

PR #1152 §射程外 は「argocd-server（Ubuntu / glibc 2.43）も `keycloak.localhost` を引けない」と
記録している。**再現した**が、**陽性対照つきで測ると結論は違う**:

```console
$ kubectl -n argocd exec deploy/argocd-server -- sh -c \
    "getent hosts keycloak.localhost; echo getent_rc=$?; \
     getent hosts keycloak.platform-infra.svc.cluster.local >/dev/null; echo control_rc=$?"
getent_rc=2      ← glibc の NSS は *.localhost を引かない（RFC 6761 の特別扱い）
control_rc=0     ← 陽性対照。同じ resolv.conf で in-cluster 名は引ける
```

しかし **argocd-server は Go バイナリで、名前解決に glibc NSS を使っていない**（pure-Go resolver）。
実測でも OIDC provider の初期化（＝discovery の取得）は成功している:

```console
$ kubectl -n argocd logs deploy/argocd-server --tail=200 | grep -i oidc
{"level":"info","msg":"Initializing OIDC provider (issuer: https://keycloak.localhost/realms/platform)",…}
{"level":"info","msg":"OIDC supported scopes: [openid abac-attributes email realm-management-roles offline_access profile roles]",…}
```

🔴 **`getent` の失敗を「そのツールの OIDC が壊れている」と読むのは誤りである。**
どちらのランタイムが解決するかで結論が変わる。**本検証器はランタイムの実挙動（ログイン開始が
どこへ向くか）を測るので、この取り違えを構造的に避けている。**

## 影響範囲（宣言ファイル領域）

- `scripts/verify-tool-oidc-logins.sh`（新規）・`scripts/lib/tool-oidc-login.js`（新規・判定ロジック）
- `scripts/scripts.repo.test.js`（追記のみ）
- `scripts/README.md`（行の追加）
- `docs/operations/local-sso-recovery-runbook.md`（STEP 4 の追随）
- `.ai-context/adr/IADR-0363_*.md`（新規）・`.ai-context/adr/README.md`（索引）
- `.ai-context/specs/20260903_issue-1163_tool-oidc-login-verifier.md`（本書）

## 未決事項 / 積み残し

- **ログインの完了**（資格情報 POST → callback → セッション確立）は測らない（#1163 §射程外）。
  完了まで測ると TOTP・利用者ごとの成立条件（IADR-0328 の「Grafana は `admin` で落ちる /
  MinIO は `admin` でしか成立しない」）を検証器が抱え込む。別 issue とする。
- **CI 実行**は #466（`blocked`）に依存する。本検証器は稼働 dev クラスタ向けである。
  なお `scripts.repo.test.js` はスタブへ向けた実走を CI でも回すので、**検証器の結線の回帰は
  クラスタ無しで検知できる**（クラスタの健全性の回帰は依然として #466 待ちである）。
- **runbook のログイン一覧で Vault だけが `（**http**）` と書かれている**（本 PR の射程外の既存の齟齬）。
  同じ節が「admin entrypoint は TLS 終端」と宣言しているので矛盾している。別途直す。
- **Vault OIDC の復旧**（runbook STEP 2 の再走）は本 PR では行わない（読み取り専用）。
