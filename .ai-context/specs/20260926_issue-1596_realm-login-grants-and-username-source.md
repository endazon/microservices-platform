---
title: "realm の検査に、デバイスグラント・CIBA・軽量アクセストークン・利用者登録・preferred_username の出どころを足す（#1596）"
type: spec
status: done
related_ids: [NFR-09, SC-17, ADR-0032, IADR-0420, IADR-0429]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md §決定（SPA はトークンを扱わない・BFF セッション方式）
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-09
related_specs: [20260926_issue-1589_realm-machine-judgement-premises.md]
issue: "#1596"
---

# 作業仕様書 — realm の検査 7 に、人を機械と読ませる残りの経路を足す（#1596）

## 起点

- issue: #1596（#1590〔#1589〕の監査で出た、ブロックしない強化の候補。いずれも今の realm には無い）。
- 起点 ID: **NFR-09**（エッジ〔BFF〕での認証）／**SC-17**（利用者作成の運用上の注意の置き場）。計画 ADR: **ADR-0032**。実装 IADR: **IADR-0420**（`MachinePrincipal.IsMachine`）／**IADR-0429 決定 3**。
- **新しい IADR は起こさない。** IADR-0420 の判定規則（トークンが名乗る形だけ・許可集合なし）は変えず、その前提を realm の宣言で守る範囲を広げるだけである（#1589 と同じ扱い）。
- 稼働クラスタには当たらない（LIVE 未設定）。

## 着手時の現況（`origin/develop` = 8a51ec30）

- 検査 7（`collectMachineJudgementGaps`）: (1) `service-account-` で始まる人の利用者、(2) 標準フロー / implicit を開いて `profile` を既定に持たないクライアント、(2') `profile` スコープが `claim.name=preferred_username` / `access.token.claim=true` のマッパーを持たないこと。
- 実データ（`deploy/keycloak/microservices-platform-realm.json`・realm `platform`）: `registrationAllowed: false`・`editUsernameAllowed: false`、`registrationEmailAsUsername` と `identityProviders` と `clientProfiles` は無い。
  どのクライアントも `attributes` に device / CIBA / lightweight を持たず、`directAccessGrantsEnabled` はすべて false。`profile` スコープの利用者名マッパーは `oidc-usermodel-property-mapper` / `user.attribute=username`。
  ログイン経路のクライアント（wiki-js / bff / headlamp / grafana / argocd / vault）のクライアント単位のマッパーは、どれも `preferred_username` を出さない（ロールのマッパーだけ）。

## Keycloak の実装で確かめたこと（`gh api repos/keycloak/keycloak/contents/…?ref=24.0.0`。配備の版は `quay.io/keycloak/keycloak:24.0`）

| 項目 | ソース | 読んだこと |
| --- | --- | --- |
| デバイスグラント | `server-spi/…/models/OAuth2DeviceConfig.java` | 属性 `oauth2.device.authorization.grant.enabled` を `Boolean.parseBoolean` で読む（大小を区別しない "true" だけが真） |
| CIBA | `server-spi/…/models/CibaConfig.java` | 属性 `oidc.ciba.grant.enabled` を `Boolean.parseBoolean` で読む |
| 直接アクセスの既定 | `model/jpa/…/JpaRealmProvider.java` の `addClient` | 新しいクライアントで既定 true にするのは `standardFlowEnabled` だけ。`directAccessGrantsEnabled` / `implicitFlowEnabled` は明示しない限り false |
| 軽量アクセストークン | `services/…/mappers/AbstractOIDCProtocolMapper.java`・`OIDCAttributeMapperHelper.java`・`server-spi-private/…/Constants.java` | クライアント属性 `client.use.lightweight.access.token.enabled`（`Boolean.parseBoolean`）かセッション属性で有効になり、有効なら access token に載るのは **`lightweight.claim` が文字列 "true" のマッパーだけ**（`includeInAccessToken` の代わりに `includeInLightweightAccessToken` を見る）。クライアントポリシーの実行器 `use-lightweight-access-token` がセッション属性を入れる |
| access token に載るか | `OIDCAttributeMapperHelper.includeInAccessToken` | `access.token.claim` が文字列 "true" のときだけ（未設定は載らない） |
| 利用者名を選べる経路 | `services/…/userprofile/DeclarativeUserProfileProviderFactory.java` の `editUsernameCondition` | 登録（REGISTRATION）と IdP の初回ログインの確認（IDP_REVIEW）では利用者が入力でき（`registrationEmailAsUsername` ならメールアドレスが利用者名になる）、それ以外は `editUsernameAllowed` のときだけ編集できる |
| 利用者名のプロパティ | `services/…/mappers/UserPropertyMapper.java` | `oidc-usermodel-property-mapper` は `user.attribute` のプロパティを `ProtocolMapperUtils.getUserModelValue` で読む（`username` → `getUsername`） |

## 設計

- **(a) ログイン経路の数え方**: `humanLoginGrants(client)` —— `standardFlowEnabled`（未設定は true）・`implicitFlowEnabled === true`・`directAccessGrantsEnabled === true`・属性 `oauth2.device.authorization.grant.enabled`・属性 `oidc.ciba.grant.enabled`（属性は大小を無視して "true"）。`bearerOnly` は除く。
  **直接アクセス（ROPC）は issue の列挙に無いが、同じく人のトークンを出す grant なので同じ扱いにした**（実データは全クライアントが false なので影響なし）。
- **(b) 軽量アクセストークン —— 禁止ではなく「利用者名が残ること」を求める**: Keycloak は軽量でも `lightweight.claim=true` のマッパーのクレームは載せるので、禁止は過剰であり、属性だけ見て通すのは不足である。
  ログイン経路のクライアントが軽量（クライアント属性、または realm の `clientProfiles` に実行器 `use-lightweight-access-token` がある。条件は評価せず保守側に倒す）なら、そのクライアントに効くマッパーの中に**利用者名から出し `lightweight.claim=true` を持つもの**が無ければ違反。
- **(c) 利用者が利用者名を選べる宣言**: `registrationAllowed` / `editUsernameAllowed` / `registrationEmailAsUsername` / `identityProviders[<alias>]`（無効の IdP も数える）はどれも違反。
  **レビュー済みの例外**を `SELF_CHOSEN_USERNAME_EXCEPTIONS`（キー → 空でない理由）へ載せたものだけ黙る。理由が空の項目と、realm では閉じているのに載っている項目は違反（例外を腐らせない）。今は空で、自己試験が空であることを固定する。
  `registrationEmailAsUsername` は issue の列挙に無いが、上の `editUsernameCondition` のとおり利用者名 ＝ 利用者の入力するメールアドレスになる（`service-account-x@…` を名乗れる）ので同じ扱いにした。
- **(d) preferred_username の出どころ**: 認めるのは `oidc-usermodel-property-mapper` の `user.attribute=username` だけ。
  (2') の `profile` スコープの判定にこの条件を足した。加えて (2'')、ログイン経路のクライアントに効くマッパー（**クライアント単位の `protocolMappers`・既定スコープ・任意スコープ**の中身。任意スコープも要求すれば載るので数える）のうち `preferred_username` を access token か軽量トークンへ出すものは、すべて利用者名から出すこと（出どころの違う上書きを止める）。同じスコープのマッパーは 1 回だけ報告する。
  利用者属性のマッパー（`oidc-usermodel-attribute-mapper`）は `user.attribute=username` でも認めない —— 利用者属性は利用者が編集できる種類であり、宣言だけからは安全と言えない（偽陽性の側に倒す。実データには無い）。
- ログインしないクライアント（SA 専用・`bearerOnly`）のマッパーと、access token にも軽量トークンにも載せないマッパー（ID トークンだけ等）は対象外（`IsMachine` が読むのは access token）。

## 母集合（誤りになる記述の走査。パス除外: `src/ai-stock-trading` / `CHANGELOG.md`。拡張子で絞らない）

`git grep -n "isHumanLoginClient\|profileScopeEmitsUsername\|collectMachineJudgementGaps\|checkRealmMachineJudgementText\|#1589\|registrationAllowed\|editUsernameAllowed\|lightweight\|oauth2.device\|ciba"` を
検査器を除いて `origin/develop`（8a51ec30。本作業の記録を書く前）に対して引いた: 15 行・7 ファイル。

| ファイル | 扱い |
| --- | --- |
| `.ai-context/specs/20260926_issue-1589_realm-machine-judgement-premises.md`（6 行） | 確定済みの凍結記録。書き換えない |
| `deploy/keycloak/microservices-platform-realm.json`（2 行: `registrationAllowed: false` / `editUsernameAllowed: false`） | 正しい宣言。変えない（実データが通ることを試験で固定） |
| `docs/screens/SC-17_user-account-management.md`（trace ブロック） | 運用上の注意を 2 点 → 3 点に。デバイスグラント等・軽量・マッパーの出どころ・利用者名を選べる設定を足し、trace ブロックへ #1596 と本仕様書 |
| `docs/tests/SC-17_user-account-management.md`（trace ブロック） | T-54 を追加し、trace ブロックへ #1596 と本仕様書 |
| `scripts/README.md`（`static-checks` の行） | #1596 で足したものを追記 |
| `scripts/scripts.repo.test.js`（#1589 の CLI 試験） | 残す。#1596 の CLI 試験（実データへの変異 4 種）を隣に追加 |
| `scripts/keycloak-realm-reconcile.test.js`（2 行: `registrationAllowed` を試験の値に使うだけ） | リセットの門の reconcile の試験で、本検査とは無関係。変えない |

## 受け入れ基準

- [x] (a) デバイスグラント / CIBA（と直接アクセス）だけを開いたクライアントが `profile` を持たなければ検出する。属性の大小違いも。"false" / "yes"・`bearerOnly` は数えない
- [x] (b) ログイン経路のクライアントの軽量アクセストークン（属性・クライアントポリシーの実行器）は、利用者名のマッパーに `lightweight.claim=true` が無ければ検出し、あれば通す
- [x] (c) 4 種の利用者名を選べる宣言を検出し、レビュー済みの例外は黙り、理由の空な例外・該当しない例外は検出する。例外は空
- [x] (d) `profile` の利用者名マッパーの出どころ（メールアドレス・利用者属性・固定値）と、クライアント単位・別スコープの上書きを検出する。載せない上書き・ログインしないクライアントは検出しない
- [x] 実データの realm は通る（自己試験の実データ・ラチェットと CLI の試験）

## 検証

- `node scripts/check-realm-constraints.js --self-test` → 136 → 156 件 OK
- `node scripts/check-realm-constraints.js` → OK（exit 0）
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` → 全件 pass（#1596 の CLI 試験: 実データへの変異 4 種がそれぞれ exit 1 で名指しされる）
- **変異試験（検査器のソース）**: 追加分を 1 つずつ戻した写しで `--self-test` を走らせた。**20 通りすべて exit 1**
  （B01 デバイスグラント／B02 CIBA／B03 直接アクセス／B04 属性の大小／B05 軽量の検査／B06 クライアントポリシーの実行器／B07 `lightweight.claim` を見ない／
  B08–B11 利用者名を選べる 4 種／B12 無効の IdP を飛ばす／B13 空の理由の例外／B14 該当しない例外／B15 (2') の出どころ／B16 利用者属性のマッパーを認める／
  B17 クライアント単位のマッパーを見ない／B18 任意スコープを見ない／B19 同じスコープの重複報告／B20 載せない上書きを数える）。

## 未検証・射程外

- 稼働中の realm で管理コンソールから入れた設定は宣言の外（SC-17 §運用上の注意のまま）。
- Keycloak 26 の標準トークン交換（`standard.token.exchange.enabled`）は配備の版（24.0）に無いので数えていない。版を上げるときに見直すこと。
- 利用者プロファイルの `username` の検証規則（接頭辞を拒む pattern）で例外を代替する案は採っていない（例外に理由を書く運用で足りる。必要になったら検討する）。
