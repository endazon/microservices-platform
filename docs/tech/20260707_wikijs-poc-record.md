---
title: Wiki.js 稼働 PoC 実測記録（OIDC / GraphQL 同期）
type: tech-note
status: fixed
created: 2026-07-07
updated: 2026-08-21
author: claude
---
<!-- trace:
ids: [FR-13, UC-07]
adrs: [ADR-0011]
iadrs: [IADR-0021, IADR-0023]
specs: [20260707_issue-88-wikijs-verification-and-delete-sync, ADR-0011_wiki-engine]
issues: []
-->

# Wiki.js 稼働 PoC 実測記録（Issue #88 スコープ1・2）

実測環境: Wiki.js **2.5.314**（`ghcr.io/requarks/wiki:2`）＋ Keycloak **24.0** ＋ PostgreSQL 16、
`deploy/docker-compose.yml`（ローカル Docker Desktop / Windows、2026-07-07）。

## 結論（受け入れ基準との対応）

| 確認項目 | 結果 |
| --- | --- |
| Keycloak(OIDC) ログイン（realm `knowledge-platform` / client `wiki-js`） | ✅ 成功（認可コードフロー一式を実測） |
| ローカルログイン無効化 | ✅ 検証済み（無効化後 `errorCode 1003` で拒否・OIDC 単一経路を確認） |
| クレーム受け渡し（clearance/department/groups） | ✅ ID トークン・userinfo 双方で確認。`mapGroups` で Wiki.js グループへ自動割当も確認 |
| `pages.singleByPath` → `create`/`update` スキーマ整合 | ✅ 整合（下記の実測差異を実装へ反映済み） |
| `isPrivate=true` ページの API キー本文取得 | ✅ **取得可能**（fullAccess キーで `singleByPath` が content/render を返す。fail-closed 調整不要） |
| エラー時再送 | ✅ GraphQL errors／`responseResult.succeeded=false` は例外化 → MassTransit リトライで再送（E2E で確認） |
| レイテンシ（Wiki 閲覧の p95 参考値） | ✅ `singleByPath` p95 ≈ **5ms**、`update` p95 ≈ **0.74s**、`create` ≈ 1.0s、`delete` ≈ 0.32s（ローカル・30 回計測） |

## 実測で判明し実装へ反映した差異（重要）

1. **未存在ページの `singleByPath` は GraphQL errors（`PageNotFound` code=6003）を返す**
   （`data.singleByPath=null` と併送）。旧実装は errors を一律例外化していたため、
   **新規ページの create に到達できず同期が全滅**する。→ 6003 は「未存在（null）」として扱うよう
   `WikiJsGraphQlClient` を修正（`WikiJsGraphQlClientTests` で実測応答を再生）。
2. **`pages.update` は content 省略で失敗（6004: Page content cannot be empty）、tags 省略で
   `'map'` エラー**。しかも**失敗時も部分適用される（非トランザクショナル）**。→ update は常に
   全項目シェイプで送る。アーカイブ（非公開化）は現在の content/title/tags を取得してから
   `isPublished=false` で update する。
3. **`pages.update` は `isPrivate` の変更を無視する（create 時のみ有効）**。→ 非公開化の実効手段は
   unpublish（`isPublished=false`）。isPrivate は create 時の多層防御として引き続き設定する
   （公開状態の遷移は ABAC ゲートウェイ＋ネットワーク分離が一次防御であり影響は限定的）。
4. **素の Wiki.js は `en` ロケールのみで、同期実装が使う `ja` が未インストール**。`ja` ページの
   create が FK 違反（`pages_localecode_foreign`）で全滅する。→ 初期セットアップで ja ロケールを
   インストールする（運用仕様書に手順追記。GraphQL: `localization.downloadLocale(locale:"ja")`）。

## 配備構成の不備（実測で発見し修正）

1. **realm import が起動失敗**: realm JSON のコメント用フィールド `"//"` を Keycloak 24 が拒否し、
   **Keycloak 自体が起動しない**。→ コメントフィールドを除去。
2. **標準スコープ欠落**: realm import で `clientScopes` を明示すると組み込みの `profile`/`email` が
   生成されず、Wiki.js の `scope=openid profile email` が `invalid_scope` で拒否され **OIDC ログインが
   不可能**だった。→ realm JSON に `profile`（full name / preferred_username）と `email` スコープを定義。
   なお email クレームは `oidc-usermodel-property-mapper` を用いる（attribute-mapper では組み込み
   プロパティ email を拾えず、Wiki.js が「Invalid email」でログイン拒否する）。
3. **issuer のホスト揺れ**: ブラウザ経路（`localhost:8080`）で作られた認可セッションの ID トークン
   `iss` は、トークン交換をコンテナ内経路（`keycloak:8080`）で行っても `http://localhost:8080/...` に
   なる。issuer 不一致で Wiki.js（passport-openidconnect）の検証と Keycloak userinfo が失敗する
   （「Failed to fetch user profile」）。→ compose に `KC_HOSTNAME_URL: http://localhost:8080` を追加し
   issuer を固定。Wiki.js 側 OIDC 設定の Issuer も `http://localhost:8080/realms/knowledge-platform`。
4. **サービス DB の権限不足**: PostgreSQL 15+ では `GRANT ALL ON DATABASE` だけではスキーマ `public`
   への CREATE 権限が付かず、**全サービスの EF Core Migration が 42501 で失敗**する。→
   `create-multiple-dbs.sh` を各 DB `OWNER TO kp` へ修正。

## Wiki.js 側 OIDC 設定値（実測で疎通した構成）

| 項目 | 値 |
| --- | --- |
| Client ID / Secret | `wiki-js` / realm import の値（dev） |
| Authorization Endpoint | `http://localhost:8080/realms/knowledge-platform/protocol/openid-connect/auth` |
| Token / User Info Endpoint | `http://keycloak:8080/realms/knowledge-platform/protocol/openid-connect/{token,userinfo}`（コンテナ内経路） |
| Issuer | `http://localhost:8080/realms/knowledge-platform`（`KC_HOSTNAME_URL` で固定） |
| Email / Display Name Claim | `email` / `name` |
| Map Groups / Groups Claim | 有効 / `groups`（Keycloak サブグループ名 `internal`・`engineering` 等に一致する Wiki.js グループへ自動割当） |
| Self Registration | 有効（初回 OIDC ログインでユーザー自動作成） |

検証ユーザー: `poc-user`（`clearance=internal` / `department=engineering`、realm import に同梱）。
ログイン後、Wiki.js 上にユーザーが自動作成され、groups クレームから `internal`/`engineering`
グループへ割当されることを確認した。

## E2E（実 WikiService ↔ 実 Wiki.js）

修正後の `WikiService.Api` をローカル実行し、RabbitMQ 経由で実イベントを流して確認:

- `DocumentUpdated(published)` → Wiki.js に `doc/<DocumentId>` が **create** され本文が反映（✅）
- `DocumentUpdated(archived)` → Wiki.js ページが **unpublish**、`wiki_svc` メタデータが `archived`（✅）
- `DocumentDeleted` → Wiki.js ページが **delete**、メタデータ行が削除（✅）。同一イベント再送でも
  エラーなし（冪等 ✅）

## 残課題

- `isPrivate` の**事後変更**が Wiki.js 2.5 の update で効かないため、公開→非公開の機密区分遷移で
  Wiki.js 側の isPrivate を追随させるには delete→create の再作成が必要（現状は unpublish で代替可能。
  一次防御は ABAC ゲートウェイ＋ネットワーク分離であり、影響は多層防御の一層に限定）。
- レイテンシは開発機ローカルの参考値。stg/prod 相当環境での再計測を推奨。
