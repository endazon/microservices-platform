---
title: IADR-0244 Keycloak の issuer をエッジ host（https://keycloak.localhost）へ移し、.NET は Auth:MetadataAddress（in-cluster）＋ Auth:ValidIssuers（エッジ）で追随する
type: impl-adr
status: Accepted
related_ids:
  - NFR-09
  - ADR-0004
  - ADR-0023
  - IADR-0076
  - IADR-0086
  - IADR-0091
  - IADR-0197
  - IADR-0206
  - IADR-0227
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0004_authz-abac.md
  - planning:projects/microservices-platform/07_adr/ADR-0023_edge-cert-automation-cert-manager-letsencrypt.md
author: claude
created: 2026-08-22
updated: 2026-08-22
---

# IADR-0244 Keycloak issuer のエッジ移行（#780 第2段）

## 状況

`.ai-context/specs/20260817_issue-780_keycloak-edge-https-issuer.md` の第1段（[IADR-0227]）は
Keycloak をエッジへ出し pod 側の名前解決を与えたが、**issuer は 1 バイトも変えていなかった**
（意図的。追加のみで既存挙動を壊さないため）。第2段は issuer 自体を
`http://keycloak:8080`（in-cluster 名。手順A）から `https://keycloak.localhost`（エッジ host。手順B）へ
実際に移す。

### 実測（2026-08-22。着手前の再検証）

- 稼働クラスタの realm は旧名 `microservices-platform` のまま（`https://keycloak.localhost/realms/platform`
  は 404）。リポジトリの設定（`appsettings*.json` 全 18 件・`values.yaml`・`values-local.yaml`・
  `docker-compose.yml`・全 deploy manifest）は**すでに新名 `platform` を指している**
  （多軸走査で旧名の実参照 0 件を確認。`deploy/keycloak/microservices-platform-realm.json` も
  `"realm": "platform"` を宣言済み。ファイル名だけが旧名）。**PVC 永続化により `--import-realm` が
  既存 realm をスキップしていた**（[IADR-0079]）ためのドリフトである。
- `AuthExtensions.AddPlatformAuth`（`Platform.Shared.Infrastructure`）は既に `Auth:MetadataAddress` /
  `Auth:ValidIssuers` の分離機構を持つ（[IADR-0086]、2026-07-20 Accepted）。**未使用のまま**だった
  （全サービスの `appsettings.json` は `Auth:Authority` のみ設定）。

## 決定

### 1. `KC_HOSTNAME_URL` を issuer の単一情報源として `https://keycloak.localhost` へ変更する

`deploy/local/infra/keycloak.yaml` の `KC_HOSTNAME_URL` を変更する。Keycloak（Quarkus/hostname-v2）は
この値をそのまま discovery document の `issuer` として広告するため、**どの host:port 経由で
discovery を取得しても同じ issuer 文字列が返る**（in-cluster `http://keycloak:8080/...` 経由でも、
エッジ `https://keycloak.localhost/...` 経由でも `issuer` は `https://keycloak.localhost/realms/platform`）。
これが決定2を成立させる前提である。

### 2. .NET は `Auth:MetadataAddress`（in-cluster）＋ `Auth:ValidIssuers`（エッジ）で追随する（[IADR-0086] の活用）

`Auth:Authority` の文字列は変更しない。代わりに [IADR-0086] が既に用意した分離機構を
**`deploy/local/values-local.yaml`（`global.auth.metadataAddress` / `global.auth.validIssuers`）で
初めて有効化する**。

| キー | 値 | 理由 |
| --- | --- | --- |
| `Auth:MetadataAddress` | `http://keycloak:8080/realms/platform/.well-known/openid-configuration` | in-cluster から到達できる well-known。エッジの自己署名/ローカル CA（`local-edge-ca`）を .NET 側の信頼ストアへ追加する必要が無い |
| `Auth:ValidIssuers` | `https://keycloak.localhost/realms/platform` | token の `iss`（決定1によりエッジ issuer になる）を追加受理する許可リスト |

**却下**: `Auth:Authority` を直接 `https://keycloak.localhost/...` へ書き換える案。metadata 取得
そのものがエッジ経由になり、.NET の `HttpClient` がローカル CA を信頼しない限り TLS ハンドシェイクで
失敗し得る（`RequireHttpsMetadata=false` は「metadata に https を必須としない」設定であり、実際に
https を使った場合の証明書検証をスキップするものではない）。in-cluster 経由の metadata 取得は
この問題が原理的に起きない。

### 3. 非 .NET の OIDC クライアント（Grafana/ArgoCD/Vault/MinIO/Headlamp/Wiki.js）はエッジ issuer へ直接到達する

[IADR-0086] の分離が使えない（.NET 専用機構）ため、各ツールの「Keycloak を探す設定」
（`GF_AUTH_GENERIC_OAUTH_*_URL` / ArgoCD `oidc.config.issuer` / Vault OIDC discovery / MinIO `configUrl` /
Headlamp `HEADLAMP_CONFIG_OIDC_IDP_ISSUER_URL`）を `https://keycloak.localhost/realms/platform/...` へ
更新する。到達は [IADR-0227]（stage 1・`coredns-custom`）が既に可能にしている。

**各ツール自身の `redirectUris` / `webOrigins`（realm.json）は変更しない。** これらは
「各ツールが自分の callback を受け取る URL」であり、Keycloak 自身の issuer host とは独立である。
6 ツールとも既に `https://<tool>.localhost:50000/...` 形式の集約後 URL を持つ（[IADR-0091] 決定4。
admin:50000 集約と一体で先に整備済み）。issuer 移行はこの値に影響しない。
これは着手前に issue が想定していた「7 クライアントの redirect/logout URI 更新」の範囲を実測で
絞り込んだものである（platform-spa の post-logout 含め、変更が要る箇所は無かった）。

### 4. realm の作り直し（PVC 再作成）

`keycloak-data` PVC を削除・空 PVC を再作成し、`--import-realm` を実際に走らせて
`deploy/keycloak/microservices-platform-realm.json`（内容は `"realm": "platform"`）を新規 import する。
**実行前に `kcadm.sh get` で realm / clients / roles / groups / users の現況をエクスポートし、
ロールバック材料とした**（読み取り専用 API のみ使用。`partial-export` の POST 系エンドポイントは
自動分類器が書き込みとして拒否したため、個別リソースの `get` を積み上げる形に代えた）。

## 影響

- 稼働クラスタの realm 名が `microservices-platform` → `platform` に変わる（リポジトリの設定と
  ようやく一致する）。
- issuer が `http://keycloak:8080/realms/platform` → `https://keycloak.localhost/realms/platform` に
  変わる。in-cluster 名（手順A）は `Auth:ValidIssuers` に**含めていない**ため、決定2適用後は
  in-cluster 名で発行された token は受理されなくなる —— ただし決定1により **discovery の `issuer` 自体が
  エッジ名を返す**ため、手順Aのport-forward + hosts経由でログインしても発行される token の `iss` は
  最初からエッジ名になる（手順Aは「browserからの到達経路」の話であって「issuerの値」の話ではない）。
- [IADR-0091] 決定5（「OIDC issuer は最小案(keycloak:8080維持)」）は本決定で **Supersede** される。
  決定4（admin:50000 のホスト名ベース集約そのもの）は不変。

## Superseded

- [IADR-0091] 決定5 は本 IADR で Supersede する。決定5 が前提としていた「issuer は
  `keycloak:8080` を維持」は本 IADR の決定1で覆った。決定5 の redirect URI 追加は
  [IADR-0091] 決定4（admin:50000 集約）の一部として既に完了しており、决定5 自体の残作業は無い。

## 検証（実行結果は仕様書 `.ai-context/specs/20260817_issue-780_keycloak-edge-https-issuer.md`
「第2段の実行結果」節に記録する）

- discovery: `http://keycloak:8080/realms/platform/.well-known/openid-configuration` と
  `https://keycloak.localhost/realms/platform/.well-known/openid-configuration` の両方の
  `issuer` フィールドが `https://keycloak.localhost/realms/platform` と完全一致すること
- `scripts/check-realm-constraints.js` が緑（realm.json の既存制約は本 IADR で変更していない）
- 変異試験: `values-local.yaml` の `global.auth.metadataAddress` / `validIssuers` を検査する
  静的検査（新設）が、値を欠落・誤りへ書き換えると実際に落ちること

## 却下した代替案

| 案 | 却下理由 |
| --- | --- |
| CoreDNS 直接書き換えで in-cluster に edge host を解決させる | [IADR-0076] / [IADR-0227] が既に却下（k3s 管理物への侵襲・環境ごとに壊れやすい） |
| `Auth:Authority` をエッジ host へ直接書き換える | 決定2の却下理由を参照（TLS 信頼の問題） |
| 6 ツールの `redirectUris`/`webOrigins` を予防的に追加する | 実測（決定3）で不要と判明。**無いことを確認してから変更しない**方針（母集合規則1: 誤りの側から引く。この場合「無いこと」自体が実測結果） |
