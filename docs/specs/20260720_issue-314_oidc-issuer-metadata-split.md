---
title: backend の OIDC 認証を metadata 取得アドレス（in-cluster 名）と issuer 検証値（エッジ host）に分離し、単一エッジ host OIDC（手順B）を CoreDNS 無しに成立させる（Issue #314）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0004
  - IADR-0076
author: claude
created: 2026-07-20
updated: 2026-07-20
related_specs:
  - "../adr/IADR-0086_oidc-issuer-metadata-split.md"
  - "../adr/IADR-0076_edge-bff-routing-and-oidc-hostname.md"
  - "../../deploy/local/README.md"
---

# 仕様書: backend OIDC の metadata/issuer 分離（Issue #314）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): なし（認証基盤の配線拡張。プロダクト機能ではない）
- 非機能要件(NFR): 運用性（単一エッジ host での OIDC を CoreDNS/hosts 改変なしに成立させ、dev/本番の
  ブラウザログインを再現可能にする）／セキュリティ（認証＝Keycloak 一元管理を維持し issuer 検証を弱めない）
- 関連 ADR: ADR-0004（認証＝Keycloak）。方式判断は [[IADR-0086]]。前提は [[IADR-0076]]（決定3＝ブラウザ OIDC
  issuer 統一。手順A＝in-cluster 名を browser/cluster で共有／手順B＝単一エッジ host 集約は
  「in-cluster から edge host を解決させる部分＝CoreDNS 追記 or **backend の metadata/issuer 分離**」が live）。
- Issue: #314（本 issue・priority:could。#284＝PR #312・IADR-0076 のフォローアップ）。

## 目的・背景（As-Is）

[[IADR-0076]] 決定3 は、ブラウザが受け取る token の `iss` と、サービス側の検証基準（`Auth__Authority`）が
**同一 URL** でなければならない、という原則を 2 手順で満たす。

- **手順A（推奨・既定）**: ブラウザに in-cluster 名 `http://keycloak:8080` を解決させ、browser も cluster も
  同一 issuer 文字列を共有する（hosts に `127.0.0.1 keycloak` ＋ `port-forward`）。realm/manifest 無改変で成立。
- **手順B（単一エッジ host 集約・任意）**: chart の `edge.oidc.enabled=true` で SPA/`/bff`/`/realms` を同一エッジ
  host に集約する。この場合 token の `iss` は**エッジ host**（例 `https://edge.example/realms/microservices-platform`）
  になる一方、in-cluster のサービスはそのエッジ host を解決できないため、`Auth__Authority`（**OIDC metadata 取得＋
  issuer 検証の両用**）が壊れる。README 手順B(iii) は「in-cluster から同 host を解決させる」に **CoreDNS 追記 or
  backend の metadata/issuer 分離** の 2 択を挙げ、いずれも live（稼働環境依存）としていた。

現状の実装 `AuthExtensions.AddPlatformAuth`（`src/platform/backend/Shared/Platform.Shared.Infrastructure/
Foundation/Extensions/AuthExtensions.cs`）は `options.Authority = config["Auth:Authority"]` **一本**で、metadata
取得アドレスと issuer 検証値が同一 URL に束縛されている。したがって手順B では CoreDNS 書き換えでしか塞げない。
本 issue は、README 手順B(iii) の 2 択のうち **「backend の metadata/issuer 分離」を in-repo で実装**し、CoreDNS/hosts
改変なしに手順B を成立させる配線を行う（もう一方の CoreDNS 案は稼働環境依存の運用手順として README に残す）。

## スコープ（To-Be）

### 対象: `AuthExtensions.AddPlatformAuth` の JwtBearer 設定を metadata/issuer に分離（opt-in・後方互換）

.NET の `JwtBearer` は次の 2 つを独立に設定できる:

- `JwtBearerOptions.MetadataAddress` — OIDC discovery（`.well-known/openid-configuration` → 署名鍵 JWKS）の**取得先**。
- `TokenValidationParameters.ValidIssuers` — token の `iss` として**受理する issuer 文字列の許可リスト**。

新設の設定キー（いずれも任意・未設定なら現行と等価）:

| 設定キー（`Auth:*`） | env（`Auth__*`） | 意味・既定 |
| --- | --- | --- |
| `Auth:Authority` | `Auth__Authority` | 既定 `http://keycloak:8080/realms/microservices-platform`（現行どおり。`MetadataAddress` 未設定時に metadata と issuer の双方を担う） |
| `Auth:MetadataAddress` | `Auth__MetadataAddress` | **手順B 用**。in-cluster から到達できる OIDC metadata の URL（例 `http://keycloak:8080/realms/microservices-platform/.well-known/openid-configuration`）。設定時は `Authority` に代えて metadata 取得先に用いる |
| `Auth:ValidIssuers` | `Auth__ValidIssuers` | **手順B 用**。token の `iss` として追加で受理する issuer（エッジ host。例 `https://edge.example/realms/microservices-platform`）。カンマ/空白区切りで複数可 |

分岐（`AddJwtBearer` 内）:

1. **`Auth:MetadataAddress` が設定されていれば** `options.MetadataAddress = <値>`、未設定なら現行どおり
   `options.Authority = <Auth:Authority>`。両者はどちらか一方だけを設定する（JwtBearer は `MetadataAddress`
   優先だが、意図を明確にするため排他にする）。
2. **`Auth:ValidIssuers` が非空なら** `options.TokenValidationParameters.ValidIssuers = <パース結果>` を設定する。
3. `RequireHttpsMetadata=false` / `ValidateAudience=false` / `RoleClaimType` / `NameClaimType` および
   `KeycloakRolesClaimsTransformation`・認可ポリシー登録は**現行のまま不変**。

### issuer 検証を弱めない（安全既定・fail-safe）

- `ValidateIssuer` は **true のまま**（明示的に false にはしない）。
- JwtBearer ハンドラは metadata 取得後、**metadata の `issuer` を常に受理集合へ併合**する。したがって
  `MetadataAddress`=in-cluster ＋ `ValidIssuers`=[edge] の構成では、**in-cluster issuer（手順A 由来）と
  edge issuer（手順B 由来）の両方**が通る（後方互換）。`ValidIssuers` が空なら metadata の in-cluster issuer
  のみ受理＝現行と同一。
- 署名鍵（JWKS）は常に**信頼できる in-cluster metadata エンドポイント**から取得する。`ValidIssuers` は
  「発行元 URL 文字列の許可リスト」を足すだけで、**署名検証・audience・鍵の信頼境界は不変**。攻撃者が任意
  issuer を名乗っても、in-cluster metadata 由来の鍵で署名されていなければ拒否される。

### chart / values（`global.auth.*`）

- `deploy/helm/microservices-platform/values.yaml` の `global.auth` に任意項目 `metadataAddress` / `validIssuers`
  を追加（**既定は unset/空**）。`edge.oidc` ブロックにコメントで手順B との連携（このキーで backend 分離を有効化する）を追記。
- `deploy/helm/microservices-platform/templates/deployment.yaml` の env 注入に、`global.auth.metadataAddress` /
  `global.auth.validIssuers` が**設定されているときだけ** `Auth__MetadataAddress` / `Auth__ValidIssuers` を追加する
  （未設定時は非描画＝現行 manifest とバイト等価）。`validIssuers` はリストをカンマ区切り文字列で env に注入する。

### ドキュメント

- `deploy/local/README.md` 手順B(iii) に、CoreDNS 案に加えて **backend metadata/issuer 分離**の設定例
  （`global.auth.metadataAddress` / `global.auth.validIssuers`）を追記し、CoreDNS 無しで成立する経路を明記する。

## 受け入れ基準（Acceptance Criteria）

1. `Auth:MetadataAddress` / `Auth:ValidIssuers` **未設定時**、`JwtBearerOptions` は現行と等価
   （`Authority` は既定 issuer、`MetadataAddress` は未指定、`ValidIssuers` は未設定）＝**後方互換・fail-safe**。
2. `Auth:MetadataAddress` 設定時、`options.MetadataAddress` が当該値になり、`options.Authority` は使用されない。
3. `Auth:ValidIssuers`（カンマ/空白区切り）設定時、`TokenValidationParameters.ValidIssuers` にパース結果が入る。
4. `ValidateIssuer` は常に true（issuer 検証を弱めない）。`RequireHttpsMetadata=false`・`ValidateAudience=false`・
   `RoleClaimType`・`NameClaimType` は現行のまま不変。
5. chart: `global.auth.metadataAddress`/`validIssuers` 未設定なら `deployment.yaml` の env はバイト等価。設定時のみ
   `Auth__MetadataAddress`/`Auth__ValidIssuers` が描画される（`helm template` で確認）。
6. 設計判断（分離方式・issuer 検証を弱めない根拠・セキュリティ含意）を [[IADR-0086]] に記録。ADR 索引 README は
   自分の 1 行のみ追記。
7. 検証: `dotnet test src/platform/backend/backend.slnx` 緑（新規単体テスト含む）・`dotnet format` 整形済・
   `helm template` 緑・既存 CI（#275 image-mapping ドリフト・doc-links・commit-messages）を非回帰で緑。

## 非スコープ

- #334（opt-in smoke test・scripts/CI）／#320（Dockerfile 群・images.yml）／datasource（#305）／frontend・edge の
  ルーティング本体／infra 永続化（#324）／realm client 定義（`realm.json`）には触れない。`values.yaml` は OIDC 関連
  （`global.auth`・`edge.oidc` コメント）の該当箇所のみ編集する。
- issuer 検証の緩和（`ValidateIssuer=false` 等）は行わない。
- **実ブラウザでの単一エッジ host OIDC 実ログイン（手順B の end-to-end 疎通）**は稼働環境依存＝live。PR で設定手順を
  明記し `Refs #314` で残す。
- 本番像（`deploy/argocd` / `deploy/docker-compose.yml`）の値変更は行わない（compose は手順A 前提で不変）。

## 影響・リスク

- JwtBearer は `MetadataAddress` のみ設定でも issuer 検証が成立する（ハンドラが metadata の issuer を受理集合へ併合）。
  `Authority` と `MetadataAddress` を排他にすることで、両設定時の優先順位に依存しない明快な挙動にする。
- `ValidIssuers` を足しても、鍵の信頼境界（in-cluster metadata の JWKS）と署名/audience 検証は不変のため、
  issuer 許可リスト追加による攻撃面拡大はない（許可した URL 文字列を名乗るだけでは通らない）。
- 既定（新キー未設定）は backend・chart とも現行とバイト等価のため、手順A・既存デプロイ・CI へは一切影響しない。
