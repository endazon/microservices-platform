---
title: IADR-0086 backend の OIDC 検証を metadata 取得アドレス（in-cluster 名）と issuer 検証値（エッジ host）に分離する。既定は現行バイト等価、手順B は opt-in。issuer 検証は弱めず（ValidateIssuer=true・鍵は in-cluster metadata 由来）許可リストを足すだけにする
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0004
  - IADR-0076
author: claude
created: 2026-07-20
updated: 2026-07-20
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0004_authz-abac.md (認証＝Keycloak)"
  - "../../planning/projects/microservices-platform/02_requirements/ (NFR 運用性・セキュリティ)"
---

# IADR-0086: backend OIDC の metadata/issuer 分離による単一エッジ host OIDC（手順B）の CoreDNS 非依存化

- 状態: Accepted
- 日付: 2026-07-20
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: NFR（運用性＝単一エッジ host での OIDC を CoreDNS/hosts 改変なしに成立させ、ブラウザ
  ログインを再現可能にする・セキュリティ＝認証を Keycloak に一元化したまま issuer 検証を弱めない）／
  ADR-0004（認証＝Keycloak）
- 関連 ADR: [[IADR-0076]]（#284。決定3＝ブラウザ OIDC issuer 統一。手順A＝in-cluster 名を browser/cluster で
  共有／手順B＝単一エッジ host 集約は「in-cluster から edge host を解決させる部分＝CoreDNS 追記 or backend の
  metadata/issuer 分離」が live。**本 ADR がその後者を in-repo で実装する**）
- 関連仕様書: `docs/specs/20260720_issue-314_oidc-issuer-metadata-split.md`
- Issue: #314（本 issue・priority:could。#284＝PR #312 のフォローアップ）

## コンテキストと課題

[[IADR-0076]] 決定3 は「ブラウザが受け取る token の `iss` と、サービス側の検証基準（`Auth__Authority`）が
**同一 URL** であること」を原則とし、2 手順で満たす。手順A（既定・推奨）は browser/cluster に in-cluster 名
`http://keycloak:8080` を共有させる。手順B（単一エッジ host 集約・任意）は SPA/`/bff`/`/realms` を同一エッジ
host へ集約するが、この場合 token の `iss` は**エッジ host**（例 `https://edge.example/realms/...`）になる。

現状の `AuthExtensions.AddPlatformAuth` は `options.Authority = Auth:Authority` **一本**で、`Authority` が
**OIDC metadata の取得先**（`.well-known/openid-configuration` → JWKS）と **issuer 検証値**の**両方**を兼ねる。
手順B では in-cluster のサービスがエッジ host を解決できないため、`Authority` にエッジ host を入れると metadata
取得が失敗し、in-cluster 名を入れると issuer 検証がエッジ host token と不一致になる。この二律背反を現状は
CoreDNS 書き換えでしか塞げず、[[IADR-0076]] は README 手順B(iii) に「CoreDNS 追記 or backend の metadata/issuer
分離」の 2 択を live として残していた。本 ADR は後者を実装する。

決めるべき実装論点は 3 点: (1) 分離の方式（どのキーで metadata と issuer を分けるか）、(2) 既定挙動の後方互換、
(3) issuer 許可リスト追加のセキュリティ含意（検証を弱めないこと）。

## 決定

### 1. `Auth:MetadataAddress`（metadata 取得先）と `Auth:ValidIssuers`（issuer 許可リスト）を新設し、`Authority` から分離する

.NET `JwtBearer` の 2 プロパティを設定キーで独立に制御する。

- `Auth:MetadataAddress` → `JwtBearerOptions.MetadataAddress`。OIDC discovery/JWKS の**取得先**。手順B では
  in-cluster から到達できる well-known URL（例 `http://keycloak:8080/realms/microservices-platform/.well-known/openid-configuration`）。
- `Auth:ValidIssuers` → `TokenValidationParameters.ValidIssuers`。token の `iss` として**追加で受理**する issuer
  文字列（エッジ host）。カンマ/空白区切りで複数可。

`AddJwtBearer` 内の分岐:

1. `Auth:MetadataAddress` が非空なら `options.MetadataAddress` を設定し、`Authority` は設定しない。未設定なら
   現行どおり `options.Authority = Auth:Authority`（既定 `http://keycloak:8080/realms/microservices-platform`）。
   両者は**排他**（JwtBearer は `MetadataAddress` 優先だが、両設定時の優先順位に依存しない明快な挙動にするため）。
2. `Auth:ValidIssuers` が非空なら `options.TokenValidationParameters.ValidIssuers` にパース結果を設定する。
3. `RequireHttpsMetadata=false`・`ValidateAudience=false`・`RoleClaimType=ClaimTypes.Role`・
   `NameClaimType=preferred_username`（[[IADR-0031]] 実測由来）・`KeycloakRolesClaimsTransformation`・認可ポリシー
   登録は**現行のまま不変**。

これにより CoreDNS 無しで手順B が成立する: サービスは in-cluster の `MetadataAddress` から鍵を取得し、
エッジ host の `iss` を `ValidIssuers` で受理する。

### 2. 既定（新キー未設定）は backend・chart ともに現行とバイト等価（手順A 不変・opt-in・後方互換）

- backend: `Auth:MetadataAddress` / `Auth:ValidIssuers` を設定しなければ `options.Authority` 一本の現行挙動に
  縮退する（`MetadataAddress` 未指定・`ValidIssuers` 未設定）。
- chart: `deploy/helm/.../values.yaml` の `global.auth.metadataAddress` / `validIssuers` は**既定 unset/空**。
  `deployment.yaml` は設定されているときだけ `Auth__MetadataAddress` / `Auth__ValidIssuers` env を描画するため、
  既定の `helm template` 出力は現行とバイト等価。

手順B は運用者が `global.auth.metadataAddress`（in-cluster well-known）と `global.auth.validIssuers`
（エッジ host issuer）を明示設定したときだけ有効化される。これは既定オンにしない理由でもある: エッジ host を
既定に焼き込むと、単一 host 運用しない構成にも不要な issuer 許可リストを持ち込む。opt-in なら「集約する人だけが
明示的に有効化」に留まる。

### 3. issuer 検証は弱めない — `ValidateIssuer=true` を維持し、鍵の信頼境界は in-cluster metadata 由来のまま「許可リストを足す」だけにする

- `ValidateIssuer` は **true のまま**。`ValidIssuers` を空にはできても false 化はしない。
- JwtBearer ハンドラは metadata 取得後、**metadata の `issuer` を常に受理集合へ併合**する。したがって
  `MetadataAddress`=in-cluster ＋ `ValidIssuers`=[edge] では、**in-cluster issuer（手順A 由来 token）と edge
  issuer（手順B 由来 token）の両方**が通る＝後方互換。`ValidIssuers` が空なら metadata の in-cluster issuer のみ
  受理＝現行と同一。
- **署名鍵（JWKS）は常に信頼できる in-cluster metadata エンドポイントから取得**する。`ValidIssuers` は「発行元
  URL 文字列の許可リスト」を足すだけで、署名検証・audience・鍵の信頼境界は不変。攻撃者が任意 issuer を名乗っても、
  in-cluster metadata 由来の鍵で署名されていなければ拒否される（fail-safe）。よって issuer 許可リスト追加による
  攻撃面拡大はなく、これは「issuer 検証の緩和ではなく、正しく metadata/issuer を解決する分離」（#314 の要件）に一致する。

## 影響

- **手順B の in-repo 完結**: `global.auth.metadataAddress` ＋ `global.auth.validIssuers` を設定すれば、CoreDNS/hosts
  改変なしで単一エッジ host OIDC が成立する。`deploy/local/README.md` 手順B(iii) に CoreDNS 案と並置して設定例を追記する。
- **fail-safe/後方互換**: 既定（新キー未設定）は backend の `JwtBearerOptions`・chart の `deployment.yaml` env とも
  現行とバイト等価。手順A・既存デプロイ・#334（smoke）・#320（Dockerfile/images）へは一切影響しない。
- **CI**: 新規単体テスト（`JwtBearerOptions` の metadata/issuer/既定を検証）を追加。`dotnet test`・`helm template`・
  既存 CI（#275 image-mapping ドリフト・doc-links・commit-messages）を非回帰で緑に保つ。realm.json・compose は無改変。
- 本番像（argocd/compose）・datasource（#305）・frontend・edge ルーティング本体・infra 永続化（#324）には影響しない。
- **実ブラウザでの手順B end-to-end 疎通**は稼働環境（エッジ host 到達・`spa-web` の redirectUris 追記）依存＝live。
  PR で設定手順を明記し `Refs #314` で残す。

## 却下した代替案

- **CoreDNS 書き換えで in-cluster に edge host を解決させる**: [[IADR-0076]] 既述のとおり侵襲的で dev 環境ごとに
  壊れやすい。backend の metadata/issuer 分離なら宣言的（chart values）で再現性が高く、CoreDNS への依存を排除できる。
  CoreDNS 案は稼働環境依存の運用手順として README に併記するに留める（本 ADR はもう一方の実装を提供）。
- **`ValidateIssuer=false` で issuer 検証を無効化**: 最も簡単だが issuer なりすましを許し、認証境界を壊す。#314 が
  明示的に禁じる「issuer 検証の緩和」に該当するため採らない。許可リスト（`ValidIssuers`）で受理集合を明示する（§3）。
- **`Authority` と `MetadataAddress` を併用（両設定）**: JwtBearer は `MetadataAddress` を優先するが、両設定時の
  暗黙の優先順位に挙動が依存し可読性が下がる。排他にして「metadata 分離時は Authority を使わない」を明示する（§1）。
- **`ValidIssuers` を env 配列（`Auth__ValidIssuers__0`）で注入**: .NET 標準の配列束縛だが Helm 側の描画が煩雑に
  なる。単一 env のカンマ/空白区切り文字列を backend でパースする方が chart が単純で、複数 issuer も表現できる（§1）。
