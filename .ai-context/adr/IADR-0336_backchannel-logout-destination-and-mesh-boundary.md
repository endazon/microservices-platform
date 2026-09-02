---
title: IADR-0336 バックチャネルログアウトの宛先は in-cluster の素のサービス名にし、メッシュ境界は BFF の 1 URI だけ開ける
type: impl-adr
status: Accepted
related_ids: [NFR, SC-13, ADR-0005, ADR-0021, ADR-0026, ADR-0032, IADR-0066, IADR-0076, IADR-0103, IADR-0227, IADR-0251, IADR-0273, IADR-0307, IADR-0317, IADR-0327]
author: claude
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md
  - planning:projects/microservices-platform/07_adr/ADR-0026_security-requirements.md
related_specs:
  - ../specs/20260902_issue-1115_backchannel-logout-destination.md
---

# IADR-0336: バックチャネルログアウトの宛先とメッシュ境界（#1115）

> 実装リポジトリ内の意思決定記録。[IADR-0273](./IADR-0273_bff-session-completion.md) 決定 1 / 2 が
> 実装した**受け口**を、配備した状態で実際に働かせるための「送り手の宛先」と「境界」を決める。

- 状態: Accepted
- 日付: 2026-09-02
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID: NFR（セキュリティ / セッション管理）, SC-13, ADR-0026, ADR-0032, ADR-0005, ADR-0021
- 関連する実装仕様書: [`20260902_issue-1115_backchannel-logout-destination.md`](../specs/20260902_issue-1115_backchannel-logout-destination.md)

## コンテキストと課題

realm の `bff` クライアントは `backchannel.logout.url = https://localhost/bff/auth/backchannel-logout`
を持っていた。**pod の中では `localhost` は pod 自身**であり、Keycloak は自分の :443 へ POST して
`Connection refused` になる。**受け口は #1114 で直っていたが、送り手は一度も届いていなかった。**
結果、ADR-0032 が要求する**即時**失効は成立せず、効いていたのは refresh 拒否の第 2 経路
（IADR-0273 決定 3）＝**アクセストークンの寿命（realm 実測 300 秒）ぶん遅れる**失効だけだった。
失敗は静かで、Keycloak 側に `KC-SERVICES0057` が 1 行出るだけ、管理者の画面には
「ログアウトさせた」と映る。

🔴 **起票時の前提のうち 2 つは、実測すると成立しなかった。**

1. 「CoreDNS の書き換えを裸の `localhost` へ広げれば届く（ただし危険）」——
   **危険なのではなく、そもそも効かない。** pod の `/etc/hosts` に `127.0.0.1 localhost` が必ず在り、
   問い合わせは CoreDNS へ届かない。
2. 「エッジ host が `*.localhost` の実 host 名になれば規則がそのまま効く（#780 の着地待ち）」——
   **Keycloak には効かない。** CoreDNS は `keycloak.localhost` / `app.localhost` を
   istio-ingressgateway へ正しく答えるが、**Keycloak イメージ（UBI9 / glibc 2.34）の名前解決が
   `.localhost` を引かない**。同じ pod の netns に musl のコンテナ（ephemeral container）を足すと
   同じ名前が解決する ——**CoreDNS ではなく libc の側の性質である。**

さらに、宛先を in-cluster へ書き換えるだけでも足りなかった。`microservices-platform` の
`PeerAuthentication` は STRICT（ADR-0005 / IADR-0317）で、**`platform-infra` はメッシュ外
（サイドカー無し）**である。メッシュ外からの平文流入は Envoy が落とす（実測: `Connection reset`）。

## 検討した選択肢

| # | 案 | 評価 |
| --- | --- | --- |
| A | エッジ host（`https://<host>/bff/…`）を宛先にする | **不可**。裸の `localhost` は `/etc/hosts`、`*.localhost` は glibc が引かない。https にすると CA truststore が要り、Keycloak の再起動を伴う（#1088 と衝突） |
| B | CoreDNS の書き換えを裸の `localhost` にも広げる | **不可**。`/etc/hosts` が先に当たる。副作用（127.0.0.1 前提の健全性検査）を論じる以前に効かない |
| C | in-cluster の名前を宛先にし、**Keycloak をメッシュへ入れる** | 恒久像としては正しいが、注入ラベルは namespace 単位で、`platform-infra` の Vault / ESO / 観測系まで次の再起動で巻き込む。射程が違う |
| D | in-cluster の名前を宛先にし、**エッジ（ingressgateway）経由で mesh へ入る** | 80 は 301（NFR-11）、443 は SAN と CA truststore が要る。**そもそも Keycloak が解決できる host 名が作れない**（A と同じ壁） |
| E | in-cluster の名前を宛先にし、**BFF ワークロードの 1 ポートだけ PERMISSIVE ＋ 平文は 1 URI だけ許す** | 採用。namespace の STRICT は下げない。開けた口の安全性は署名済み `logout_token` の検証が担う |

## 決定

### 決定 1: 宛先は **in-cluster の素のサービス名** `http://bff-service:8080/bff/auth/backchannel-logout`

素の名前にするのは、**同じ realm ファイルを compose も import する**ためである
（IADR-0066 が既に採っている形＝設定は素のサービス名で書き、足りない側にエイリアスを置く）。

- k8s: `platform-infra` に `bff-service` の ExternalName エイリアスを置く
  （`deploy/local/aliases/platform-infra-externalnames.yaml`）。
- compose: `bff` サービスへネットワーク別名 `bff-service` を足す。

**https ではなく http にする。** in-cluster の一区間は Istio の mTLS が包む（決定 2）。
Keycloak に truststore を積む必要が無く、**Keycloak を再起動しなくてよい**（#1088 のため
再起動は runtime state を失う）。

**ブラウザ向けの口とサーバ間の口は別系統である。** realm JSON は注釈を持てないので、
`bff` クライアントの `description`（255 文字以内。`check-realm-constraints.js` が長さを見ている欄）に
どちらがどちらかを明記した。

### 決定 2: メッシュ境界は **BFF ワークロードの 1 ポートを PERMISSIVE にし、平文で通る URI を 1 本に絞る**

`deploy/helm/microservices-platform/templates/istio-mtls.yaml` に 2 枚組を足した
（値 `mesh.backchannelLogout.fromOutsideMesh`。**既定 false**。ローカルの `ISTIO=1` 経路だけが true にする）。

1. `PeerAuthentication`（selector: `app: bff-service`）: namespace 全体の STRICT は**下げない**。
   BFF の受信ポート 8080 だけを PERMISSIVE にする。
2. `AuthorizationPolicy`（DENY）: **principal を持たない要求（＝平文）を
   `/bff/auth/backchannel-logout` 以外へは通さない**（`notPrincipals: ["*"]` が「principal 無し」）。
   エッジ（istio-ingressgateway）から来る要求はメッシュ内の principal を持つので影響しない。

開けた口の安全性は**署名済み `logout_token` の検証**が担う（IADR-0273 決定 1 が iss / aud / exp /
`events` / `nonce` 不在 / `sub` まで検証する）。バックチャネル端点は本来インターネットに面する口であり、
**平文で到達できること自体は資格ではない。**

### 決定 3: 稼働 realm へは **kcadm で冪等に当てる**（再インポートでは届かない）

`start-dev --import-realm` は**同名 realm が在ると黙って飛ばす**（`IGNORE_EXISTING`）。
**realm JSON を直しても既存クラスタには届かない。** Wiki.js の bootstrap（IADR-0327）と同型の
冪等な後追い（`deploy/local/keycloak-setup/reconcile-backchannel-logout.sh`）を置き、
`scripts/k8s-local-up.sh` から best-effort で呼ぶ。期待値は realm JSON から読む（二重に書かない）。

> ★ 抽出に `node -e` を使わない。Git Bash（MSYS）では複数行の `node -e '…'` が
> `node: -e requires an argument` で落ち、**空文字を返したまま先へ進む**。実際にこれを踏み、
> 稼働 realm の宛先を一度空にした。呼び出し側と pod の中の**両方**を fail-closed にしてある。

### 決定 4: 静的検査は「到達するか」ではなく「**到達し得ない形か**」を見る

`scripts/check-realm-constraints.js` に検査 6 を足した。`backchannel.logout.url` の host が
裸の `localhost` / ループバック / `*.localhost` なら落とす。**ブラウザ向けの欄
（`redirectUris` / `webOrigins` / `post.logout.redirect.uris`）は対象にしない**——
あちらは裸の `localhost` が正しい。到達可能性そのもの（DNS・メッシュ）は実行時の性質であり、
静的には測れない。測れる境目をここに引いた。

## 結果

- 良い影響: 「無効化 → 全セッション即時失効」の第 1 経路が、配備した状態で初めて働く。
  同じ 1 行が compose 経路でも成立する。同型の取り違え（サーバ間の口へブラウザ向けの host を書く）は
  以後 CI が止める。
- 悪い影響・トレードオフ: BFF の 8080 が**平文も受け付ける**ようになる（DENY で 1 URI に絞ってはいる）。
  恒久像は選択肢 C（`platform-infra` をメッシュへ入れる）であり、そうすればこの 2 枚組は不要になる。
- フォローアップ:
  - `platform-infra` をメッシュへ入れる恒久像（選択肢 C）。本件の射程外。
  - #1088（PERSIST=1 で立っていない）が解決しても、決定 3 の後追いは引き続き要る
    （`IGNORE_EXISTING` は永続化とは別の問題である）。

## 関連

- 継承: [IADR-0273](./IADR-0273_bff-session-completion.md)（決定 1 / 2 の受け口を、配備で働かせる）
- 前提の訂正: [IADR-0227](./IADR-0227_edge-host-pod-side-resolution.md) の `*.localhost` 解決は
  **glibc のイメージには効かない**（本 IADR で実測。IADR-0227 自身の射程＝非 .NET ツール（alpine 系）は変わらない）
- 境界: [IADR-0317](./IADR-0317_istio-ingressgateway-edge-and-strict-mtls.md)（STRICT の宣言）
- 同型の後追い: [IADR-0327](./IADR-0327_wikijs-setup-bootstrap.md)（manifest だけでは復元できない runtime 状態）
