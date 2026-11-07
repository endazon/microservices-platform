---
title: IADR-0693 クラウドの認可サーバー（Keycloak）への通信を、OpenBao の PKI（DB・ブローカと同じ CA）が出す証明書の HTTPS にし、受け手は https を必須にしてシステムの信頼ストアに基盤の CA を足して検証する。平文と HTTPS の併存の段を経て平文を閉じる
type: impl-adr
status: Accepted
related_ids: [NFR-16, NFR-18, NFR-05, IADR-0689, IADR-0674, IADR-0591, IADR-0086, IADR-0585]
author: claude
created: 2026-11-07
updated: 2026-11-07
related_specs:
  - ../specs/20261107_cloud-keycloak-tls.md
---

# IADR-0693: クラウドの Keycloak を OpenBao の PKI の証明書で HTTPS にする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07「OpenBao の PKI で TLS にする」）・claude（方式）

## 起点・関連

- 要求 **NFR-16**（通信の暗号化）・**NFR-18**・**NFR-05**。
- 前提: IADR-0689（OpenBao・ESO・PKI `pki_cloud`。決定 4 の TLS の鍵の型）、IADR-0674（クラウドの最小構成）、IADR-0591（基盤への TLS。CA は信頼ストアへ足す）、
  IADR-0086（Authority と発行元の許可リスト `ValidIssuers`）。
- 作業仕様書: `.ai-context/specs/20261107_cloud-keycloak-tls.md`（現状・母集合・実測）。

## コンテキストと課題

- クラウドの構成の Keycloak は、クラスタの中で平文の HTTP（8080）だけを受けていた。トークンの取得（client credentials の secret を含む）と、
  受け手の鍵（JWKS）の取得が平文で流れる。受け手の `RequireHttpsMetadata` は固定で false だった。
- 単一ノードで、Pod は同じブリッジを通る。DB とブローカは既に TLS（サーバ証明書の検証つき）である。
- Keycloak 26.7.4（宣言の digest）で確かめたこと（作業仕様書 §実測）:
  1. 証明書のファイルを与えると、受け口は 8443、管理の口（9000。health）も同じ証明書の TLS になる。
  2. `KC_HOSTNAME` を固定しないで `KC_HOSTNAME_STRICT=false` にすると、発行元（iss）は要求の URL から決まる（https で引けば https、http で引けば http）。
  3. Keycloak の利用者（uid 1000・gid 0）は、root 所有・0440 の証明書のファイルを、補助グループ 1000 を持つときだけ読める。

## 決定

### 決定 1 — サーバ証明書は `pki_cloud`（DB・ブローカと同じ CA）の role `keycloak` で発行し、ESO が Secret `keycloak-tls` に写す

- role は従来の 3 つと同じ形（短い名前・名前空間つき・`.svc`・`.svc.cluster.local` の 4 つだけ・`no_store`・1 年）。発行は基盤の側（policy `platform-infra-eso`）だけ。
  MSP の側と取引ユニットの側は発行できない（試験で固定）。
- **同じ CA にした理由**: 信頼の範囲は同じ（クラスタの中の基盤の受け口で、発行者は OpenBao。CA の鍵は OpenBao の外へ出ない）。CA は既に 3 つの名前空間へ配られ
  （MSP の ConfigMap と、取引ユニットの ESO の写し）、取引ユニットの Pod は DB・ブローカの検証のために既に信頼している。別の CA にすると、配布と信頼の設定が
  2 組になり、片方の更新漏れが起きやすい。名前の取り違え（DB の role で `keycloak` の名前を出す等）は role の許す名前が拒む。
- role の一覧は `lib.sh` の `OB_PKI_ROLES` の 1 か所に持つ。`configure.sh`（初回の root。root を失効させる前の段）と、稼働中の構成へ足す口
  `root-token.sh apply-pki-roles`（root を作り直して書き、失効させる。IADR-0689 決定 2）が同じ一覧を書く。policy は既存の `apply-policies` で当て直す。
- 採らなかった: 認可サーバー用に別の CA を作る — 上の理由。手元で鍵を作って入れる — IADR-0689 決定 4 と同じ理由（鍵が手元を通る）。

### 決定 2 — Keycloak は HTTPS（8443）だけで受け、平文の口を閉じる。発行元は `https://keycloak:8443/realms/platform`

- `KC_HOSTNAME=https://keycloak:8443`・`KC_HTTP_ENABLED=false`。Service は 8443 だけ。管理の口も TLS になるので probe は HTTPS で測る（kubelet は証明書を検めない）。
- 証明書は Secret のボリュームを `defaultMode: 0440` と Pod の `fsGroup: 1000` で読む（鍵をほかの利用者へ開けない）。
- 証明書は 1 時間ごとに読み直す（`KC_HTTPS_CERTIFICATES_RELOAD_PERIOD=1h`）。ESO が半年ごとに作り直した後に Pod を作り直さなくてよい。
- 到達: 入りは TCP/8443 だけ、送り元は取引ユニットの Keycloak を呼ぶ 11 個の Pod（名前空間 かつ Pod）と LLM ゲートウェイだけ。従来は取引ユニットの名前空間の全 Pod へ
  開いていた。一覧は取引ユニットの chart の values-cloud の描画で Keycloak の URL を持つ Deployment から引いた（呼ばないのは OpenD だけ）。
  取引ユニットが Keycloak を呼ぶサービスを足したら、この一覧も足す（足さなければ、そのサービスの起動・呼び出しが落ちて気付く。開き過ぎにはならない）。

### 決定 3 — 受け手は https を必須にし、基盤の CA をシステムの信頼ストアに足して検証する（指定した CA だけを信頼する HttpClient は作らない）

- アプリ: `Auth:RequireHttpsMetadata`（既定 false）を読み、true なら JwtBearer の `RequireHttpsMetadata` を true にする。true なのに Authority
  （`Auth:MetadataAddress` を与えたらそちら）が https でなければ**登録の時点で例外**（サービスの起動で止まる。JwtBearer 自身の拒否は最初の要求の時点であり、起動では止まらない）。
- chart: `global.auth.tls.enabled`（既定 false）。有効なら Authority・MetadataAddress が https でなければ描画で止め、各サービスへ
  `Auth__RequireHttpsMetadata=true` を渡し、`global.infraCa` の ConfigMap を `/etc/platform-infra-ca` へマウントして `SSL_CERT_DIR=/etc/ssl/certs:/etc/platform-infra-ca` にする。
- values-cloud だけが有効にする（Authority は `https://keycloak:8443/realms/platform`）。既定・ローカル・compose は変えない（平文の in-cluster のまま。試験で固定）。
- **システムの信頼ストアにした理由**: ブローカ（IADR-0591）と取引ユニット（同じ `SSL_CERT_DIR` の型）に揃う。JwtBearer の鍵の取得・トークンの取得
  （サービスの資格のクライアント）・ほかの HttpClient のどれもが追加の配線なしに同じ検証を受ける。指定した CA だけを信頼する HttpClient は、
  共有の基盤の JwtBearer の BackchannelHttpHandler と、認可サーバーを呼ぶ全ての HttpClient に個別の配線が要り、片方が漏れると平文へ戻すより気付きにくい形で
  検証が欠ける。
- 引き換え: 基盤の CA が外の宛先（提供元の API）の証明書の検証にも効く。CA の鍵は OpenBao の外へ出ず、role はクラスタの中の名前しか許さないので、
  外の名前の証明書は root で role を書かない限り出ない（§残余 2）。

### 決定 4 — 稼働中の構成は「併存 → 平文を閉じる」の 2 段で移す。クラウドの描画は最終の段を既定にする

- 併存の段は `deploy/cloud-transition/keycloak-tls-coexist`（`deploy/cloud` に重ねる kustomize。`deploy/cloud` の下に置くと kustomize が循環として拒むので隣に置く）。
  8080 と 8443 を両方受け、hostname を固定しない（実測 2）。送り元は最終の段と同じ Pod だけ。
- 移す順（手順書 `docs/operations/cloud-keycloak-tls-runbook.md`）: PKI の role と policy → ESO → 併存の段 → MSP の受け手（https。移す間だけ旧の発行元を
  `global.auth.validIssuers` で足す）→ 取引ユニット（同じく）→ 最終の段（平文を閉じる）→ 旧の発行元の併記を外す。
- 新しく作る構成は最初から最終の段（README §3 の順）。
- 採らなかった: 併存の段で `KC_HOSTNAME` を http に据え置く — https で引いた受け手にも http の鍵の URL が返り、https を必須にした受け手が鍵を取れない。
  併存を描画の切り替え（values）にする — 基盤の側は kustomize で、段を値で持つ口が無い。重ねにすれば最終の段の宣言は 1 つのまま。

### 決定 5 — 取引ユニットへの CA の配布は既存の KV の写しを使う（新しい口を作らない）

- CA の証明書は `configure.sh` が `cloud/shared/platform-infra-ca` に置き、policy `ast-eso` が読める（IADR-0689 決定 6）。取引ユニットは既に ESO で Secret
  `platform-infra-ca` へ写して DB・ブローカの検証に使っている。同じ CA なので、認可サーバーの検証にもそのまま使える。鍵は配らない（policy は PKI に触れない。試験で固定）。
- 取引ユニットの側の作業（Authority の https 化・認可サーバーを呼ぶ全 Pod への CA のマウントと `SSL_CERT_DIR`・移す間の発行元の併記・自分の出の NetworkPolicy）は
  取引ユニットのリポジトリで行う。受け取り方は手順書の §取引ユニットの受け取り方。

## 結果

- 良い影響: client credentials の secret・トークン・鍵の取得がクラスタの中でも TLS になり、受け手はサーバ証明書を検証する（鍵の差し替えの余地が塞がる）。
  Keycloak へ届く Pod が名指しの 12 個に絞られる。
- 悪い影響: 稼働中の構成の移行に段が 1 つ増える（併存の段）。取引ユニットが Keycloak を呼ぶサービスを足すとき、NetworkPolicy の一覧も足す。

## 残余

1. **証明書の期限の警報は置かない。** Prometheus のスクレイプは相手の証明書の期限を系列にしないので、期限を測るには blackbox の exporter か ESO の
   controller のメトリクスの収集（ESO の名前空間への到達の許可を含む）が要る。発行は 1 年・作り直しは半年ごと・Keycloak は 1 時間ごとに読み直すので、
   作り直しが止まっても半年の余裕がある。手順書の「期限の確かめ方」を棚卸しで流す。DB・ブローカ・LLM ゲートウェイの証明書も同じ状態にある（共通の別の作業）。
2. 基盤の CA はシステムの信頼ストアに入る（決定 3 の引き換え）。CA に外の名前の role を書けるのは root だけで、root は使うたびに作り直して失効させる（IADR-0689 決定 2）。
3. 併存の段の間は平文の口が開いたままで、発行元が要求の URL で決まる。移行の後に最終の段を当てたことを、Keycloak の Service のポートで確かめる（手順書）。
4. クラスタでしか確かめられないこと（移行の門の条件 5 で確かめる）: ESO が `keycloak-tls` を書くこと・Keycloak の起動・受け手の鍵の取得。
