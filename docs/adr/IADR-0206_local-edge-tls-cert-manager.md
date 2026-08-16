---
title: IADR-0206 経路B のエッジ TLS は cert-manager の selfsigned→CA ClusterIssuer で終端し、CA を k8s Secret として安定させる
type: impl-adr
status: Accepted
related_ids:
  - NFR-11
  - ADR-0023
  - ADR-0021
  - ADR-0008
  - IADR-0076
  - IADR-0086
  - IADR-0091
  - IADR-0103
  - IADR-0105
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0023_edge-cert-automation-cert-manager-letsencrypt.md"
---

# IADR-0206: 経路B のエッジ TLS 終端（cert-manager・selfsigned→CA ClusterIssuer・`edge-tls`）

- 状態: Accepted
- 日付: 2026-08-16
- 決定者: claude（実装）

## 起点・関連

- Issue: **#779**（#442 の子 1／5）。仕様書: `../specs/20260816_issue-779_edge-tls-termination.md`。
- 計画 ADR: **ADR-0023**（エッジ TLS 証明書の自動化を cert-manager ＋ Let's Encrypt に確定。`Accepted`）、
  ADR-0021（エッジ = Istio Ingress Gateway ＋ Caddy）、ADR-0008（k3s）。
- 実装 ADR: [[IADR-0091]]（経路B のエッジは Traefik）、[[IADR-0086]]（issuer と metadata の分離）、
  [[IADR-0076]]（エッジ BFF ルーティングと OIDC hostname）、[[IADR-0103]]（admin entrypoint は平文 http）、
  [[IADR-0105]]（apiserver OIDC フラグ配線の除去）。

## コンテキストと課題

**経路B のエッジには TLS 終端が無い。** 実測（2026-08-16）:

- `deploy/` 配下の cert-manager 資産は **0 件**、クラスタにも Namespace / CRD が**不在**
- Ingress 8 件はすべて `PORTS` が `80` のみ ＝ **`spec.tls` を 1 件も持たない**
- クラスタ内の TLS Secret は `kube-system/k3s-serving`（apiserver 用）の **1 件だけ**
- Traefik の args に `--entryPoints.websecure.http.tls=true` は在るが、
  **その証明書は Traefik がメモリ内で生成する既定の自己署名であり、k8s Secret になっていない**

これが後続を止めている。**k8s 1.30+ は apiserver の OIDC 設定を構造化認証設定 `jwt[0]` へ変換し、
`issuer.url` に https を強制する**（[[IADR-0084]]（Superseded by [[IADR-0105]]）の 2026-07-25 追記が単一情報源。
実測エラー `URL scheme must be https`。`k3s v1.35.4+k3s1` で apiserver が 10 回連続起動失敗＝クラスタ停止）。

**しかも https にするだけでは足りない。** apiserver がその証明書を検証できる、
すなわち **`oidc-ca-file` へ渡せる安定した CA が k8s の中に存在する**必要がある。
同じ CA は、新しい issuer を叩く backend の HttpClient にも要る。

## 決定

### 1. cert-manager を経路B へ導入する（`LOCALEDGE=1` の opt-in・既定オフ）

`ADR-0023` が定めた「**自動化・配布層は cert-manager**」をローカルにも適用する。
既定（env 未設定）では何も適用せず**バイト等価**を保つ（[[IADR-0077]] の opt-in オーバーレイの流儀）。

### 2. CA は selfsigned → CA `ClusterIssuer` の 2 段にする

```
ClusterIssuer(selfSigned)
  └─ Certificate（ルート CA・isCA: true）→ Secret local-edge-root-ca
       └─ ClusterIssuer(ca, secretName: local-edge-root-ca)
            └─ Certificate{ secretName: edge-tls, dnsNames: [localhost, *.localhost] }
```

**2 段にする理由は、1 段目の selfSigned が発行する葉証明書には検証できる CA が無いからである。**
中間に CA を挟むことで **ルート CA が `Secret` の `ca.crt` として安定して存在する**ようになり、
`oidc-ca-file` にも backend の信頼ストアにも同じものを渡せる。**これが本 ADR の要点**である。

**`ADR-0023` の既定 CA は Let's Encrypt（ACME）であり、ローカルの selfsigned はそこから外れる。**
以下はその逸脱の根拠である。**「同 ADR は経路B を対象にしていない」とは書かない** ——
同 ADR の本文に環境を限定する語は無く（`prod` は 0 回出現。pin `4d6a7d6` で実測）、
そう書くのは**本文に無い限定を根拠にすること**になる。

- **消費側が違う。** `ADR-0023` の決定は「Istio Ingress Gateway（Envoy）が Secret を参照して TLS 終端する」形で
  書かれている。エッジを Istio と定めたのは `ADR-0021` であり、**経路B が Traefik なのは実装側の決定**
  （[[IADR-0091]]）である。したがって本 ADR は `ADR-0023` の**配布層（cert-manager）と設計要件は踏襲し、
  消費側だけが違う**という関係にある。
- **`*.localhost` では同 ADR が示す 2 択のどちらも取れない。** 同 ADR の §結果 は
  「当初から社内限定・閉域ドメインの場合、HTTP-01 は成立せず、**DNS-01 か Vault PKI 直行**を選ぶ必要がある」と述べるが、
  **DNS-01 は `.localhost` を持つ DNS プロバイダが存在しないため成立せず**、
  **Vault PKI は `VAULT=1` の opt-in であり `LOCALEDGE=1` の前提に置けない**（既定オフの資産に依存させると
  fail-safe が壊れる）。残るのが selfsigned CA である。
- **差し替えコストは上げていない。** 下記の設計要件 3 点を守っているため、
  Let's Encrypt / Vault PKI への移行は `ClusterIssuer` の追加と `issuerRef` の差し替えに閉じる。

> **★ `ADR-0023` の適用範囲（経路B を含むのか）は計画側で明示されていない。**
> 本 ADR は「含むと読んだうえで、消費側と CA を局所的に外した」という立場を採る。
> **範囲の明確化は計画へ環流済み** —— 記録は
> [`feedback/20260816_adr-0023-scope-local-route.md`](../../feedback/20260816_adr-0023-scope-local-route.md)、
> 伝達先は **`planning#383`**（本 ADR では計画の決定を変えない）。
> **約束の行き先を作らずに書くと黙って消える** —— 本 PR 自身が `IADR-0091` 決定 5 について同じ型を指摘されている。
> **「環流した」と書けるのは `feedback/README.md` 手順 3 まで済んだときだけ**である（`docs/README.md` 運用ルール 5）。

### 3. `ADR-0023` の設計要件をローカルでも守る

CA を将来 Let's Encrypt / Vault PKI へ差し替えられるように、同 ADR の 3 要件をそのまま踏襲する。

- **CA 固有設定は `ClusterIssuer` に閉じ込める。** 消費側（Ingress・アプリ）は CA を直接知らない
- **`secretName` を `edge-tls` に固定し、`dnsNames` を安定させる**（`ADR-0023` が例示している名前をそのまま使う）
- **切り替えは `ClusterIssuer` を足して `issuerRef` を差し替えるだけ**にする

### 4. TLS は追加のみ。http を残す

**443（websecure）に載る Ingress 1 件（`platform-frontend-edge`）へ** `spec.tls`（`secretName: edge-tls`）を
**追加**し、**http 経路は残す**。

**admin:50000 に載る 7 件（grafana / headlamp / vault / qdrant / minio / wiki / argocd）へは足さない。**
その entrypoint に TLS が設定されていない（Traefik の args に `--entryPoints.admin.http.tls` が無い）ため、
**足しても効かず、「TLS になったつもり」の記述だけが残る**。admin:50000 の TLS 化は
[[IADR-0103]] の改定と 7 OIDC クライアントの redirect 追記に波及するので #780 と同時に扱う。
この境界は静的検査で固定する（`admin:50000 の Ingress には spec.tls を足さない`）。
`--entryPoints.web.http.redirections.*`（http→https の恒久リダイレクト）は**足さない**。
足すと `http://*.localhost:50000` を前提にした既存 docs と realm の redirectUris が全部一段回り道になり、
本 ADR のスコープ（TLS 基盤の新設）を越えて 7 クライアントの再設定を巻き込む。

> **★ `NFR-11`（全経路の HTTPS 化）との関係を明示しておく。** 同要件は
> 「**外部から到達し得るすべてのエンドポイントを HTTPS とし、平文 HTTP を残さない**。対象は…
> **運用系ツール（Grafana・Kiali・ArgoCD UI・Headlamp）を含む**」と定めている。
> 本決定（http を残す・admin:50000 は平文のまま）は、字面だけ見ると逆を向く。
>
> **本 ADR は `NFR-11` を満たしたと主張しない。** 経路B は `LOCALEDGE=1` が
> **loopback（`127.0.0.1`）へ bind する閉域のローカル開発環境**であり（[[IADR-0091]] 決定 4 の公開範囲）、
> `NFR-11` が言う「外部から到達し得る」に当たらない、という整理で**適用外**とする。
> **`NFR-11` の充足先は本番像であり、#780（issuer の https 化）・#782（Istio Ingress Gateway）が担う。**
> 本 ADR が担うのは、そこへ至るための**検証可能な CA を k8s 内に置く**ところまでである。

### 5. `tls/` を別 kustomization に切る

cert-manager の CRD は、**クラスタに CRD が入る前に `kubectl apply -k`（サーバ側検証あり）へ渡すと失敗する**。
`deploy/local/edge` 本体へ混ぜると、cert-manager 未導入の環境で **edge overlay 全体が落ちる**。
`deploy/local/edge/tls/` を分け、スクリプトが
「cert-manager 導入 → CRD Established 待ち → `tls/` apply → 証明書 Ready 待ち」の順で当てる。

### 6. 本 ADR は apiserver に触らない

[[IADR-0105]] が除去した apiserver OIDC フラグ配線を**復活させない**。
同 ADR は「復活は #388 で https issuer と同時に設計し直す」「本 ADR はその再導入を妨げない」と明記しており、
**再導入は #781（#442 の子 3）が扱う**。本 ADR の実装では、
`k8s-local-up.test.js` の apiserver ガード 4 件（`kube-apiserver-arg` 等の不在）が**緑のままであることを受け入れ基準に含める**
—— 落ちたら設計逸脱のサインである。

## [[IADR-0091]] との関係

**決定 3 を Supersede する。** 同決定は
「443 は Traefik 既定の自己署名証明書（ブラウザ警告・実 TLS は別途）」と述べており、本 ADR がその「別途」を与える。
`deploy/local/edge/README.md` の「実 TLS 証明書・admin entrypoint の TLS 化は本オーバーレイのスコープ外（Tier 3）」
という Tier 境界も、**実 TLS の側だけ**動かす。

**決定 5（OIDC issuer は最小案 `keycloak:8080` 維持）と、却下代替案「Keycloak も 50000 集約（issuer 変更）」は
本 ADR では動かさない。** それは #780（子 2）の射程であり、
**本 ADR の実装は issuer 文字列を 1 バイトも変えない**。
決定 4（管理ツールは admin:50000 にホスト名ベース）と [[IADR-0103]]（admin entrypoint は平文 http）も**そのまま**である
—— admin:50000 の TLS 化は 7 クライアントの redirect 追記に波及するため、子 2 と同時に扱うのが安全である。

## 代替案

| 案 | `oidc-ca-file` に渡せるか | 採否 |
| --- | --- | --- |
| **Traefik 既定の自己署名をそのまま使う** | **不可**。Secret 化されておらず（Traefik がメモリ内生成）**再起動ごとに変わる**。さらに SAN にホスト名が入らないため、Go の TLS 検証（apiserver・backend の HttpClient）が `x509: certificate is not valid for ...` で落ちる | 却下 |
| **cert-manager ＋ selfsigned → CA `ClusterIssuer`** | **可** | **採用** |
| **cert-manager ＋ selfsigned 1 段（CA を挟まない）** | **不可**。葉証明書だけでは検証に使える CA が無い | 却下 |
| **mkcert** | 可（`$(mkcert -CAROOT)/rootCA.pem`）。ブラウザ警告が消えるので体験は最良。しかし **CA が開発者マシン固有でリポジトリから再現できない**。`k8s-local-up.sh` の「冪等・fail-safe・env 未設定で既定動作」という原則と、CI の stub-on-PATH（外部バイナリを記録スタブへ差し替える）に噛み合わない | 却下（README に**任意手順**として残す） |
| **Let's Encrypt をローカルにも** | **不可**。`*.localhost` にドメイン所有の検証ができない | 却下 |

## 影響・トレードオフ

- **ブラウザ警告は消えない。** ルート CA を OS / ブラウザの信頼ストアへ入れれば消えるが、
  それは開発者の手元操作であり自動化しない（README に手順を書く）。
  **警告を消すことは本 ADR の目的ではない** —— 目的は**検証可能な CA を k8s 内に置く**ことである。
- `*.localhost` のワイルドカード証明書は **2 段以上のサブドメイン**（`a.b.localhost`）を覆わない。
  現行のホストはすべて 1 段（`grafana.localhost` 等）なので問題にならないが、README に明記する。
- cert-manager の導入は CRD を伴い、**大 CRD の annotation 262144B 上限**に当たるため
  `kubectl apply --server-side --force-conflicts` を使う（[[IADR-0088]] が ArgoCD で同じ問題を是正した先例）。
- 既定オフのため既存環境に影響しない（smoke test で default バイト等価を固定する）。

## 検出しないこと（明示）

- **証明書が実際にブラウザで信頼されるか**は検査しない（信頼ストアは環境の側にある）。
- **CA が apiserver / backend へ実際に配布されるか**は本 ADR の範囲外（#781 / #780）。
  本 ADR が担うのは「**Secret として安定して在る**」ところまでである。
- **本番像（`deploy/helm/`）** は触らない。Istio Ingress Gateway が `edge-tls` を参照する形は #782 が扱う。
