---
title: IADR-0546 アカウントコンソール（SC-16）の構成は realm の宣言で持つ。account と account-console を組み込みと同じ形で対に宣言し、account のロールの写像は account-console に直付けする（共有の roles スコープは変えない）。seed 利用者の自己管理のロールは後追いが既存の利用者にも加える
type: impl-adr
status: Accepted
related_ids: [SC-16, SC-12, ADR-0026, NFR-09, IADR-0369, IADR-0485, IADR-0524, IADR-0532]
author: claude
created: 2026-10-15
updated: 2026-10-15
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md（SC-16 は Keycloak の既定機能）
related_specs:
  - ../specs/20261015_1894_account-console-401.md
---

# IADR-0546: アカウントコンソールの構成を realm の宣言で持つ（#1894）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-15
- 決定者: claude（オーナーの依頼 #1894）

## 起点・関連

- 起点 issue: #1894（#1893 の作業中に Keycloak 26.7.4 で実測。SC-16 の account API が 401、画面は「何らかの問題が発生しました」）。
- 関連する計画書 ID: SC-16・SC-12・NFR-09。関連する計画 ADR: ADR-0026（SC-16 は Keycloak のアカウントコンソール）。
- 関連する実装 ADR: [IADR-0369](./IADR-0369_persist-by-default-and-realm-reconcile-job.md)（realm の後追いと境界。本 IADR が例外を 1 つ足す）・
  [IADR-0485](./IADR-0485_paired-secrets-create-only-declarations.md)（作成時だけのキー）・
  [IADR-0524](./IADR-0524_keycloak-26-upgrade.md)（26.7.4）・
  [IADR-0532](./IADR-0532_keycloak-theme-nocturne-parents-and-token-sync.md)（§フォローアップで本件を送った）。
- 採番: `origin/develop` の最大は IADR-0545（#1942 で入った）。本件は **0546**。

## コンテキストと課題

- realm JSON は `clientScopes` を明示する。このとき Keycloak は組み込みのスコープを作らず（`RealmManager.importRealm`）、組み込みの
  `account-console` はスコープ作成より前に作られるので **既定スコープが空**になる。`basic` が無いのでトークンに `sub` が無く、account API が 401。
- realm import で作られる利用者は既定ロール（`default-roles-platform`）を受け取らない。seed 利用者は `account` の `manage-account` を持たない。
- realm の `roles` スコープはレルムロールの写像だけを持つ。`account-console` のトークンに `resource_access.account.roles` が載らず、
  ロールを足しても account API が 403。
- `account-console` だけを `clients[]` に宣言すると import が組み込みとの重複で落ちる。`account` を宣言すると組み込みの作成
  （`setupAccountManagement`：`account` のロール・`account-console`・スコープの割当・既定ロールの合成）が丸ごと飛ぶ。
- 既存の realm は `--import-realm` では変わらない（`IGNORE_EXISTING`）。後追い（`reconcile-realm.js`）は既存の人間の利用者に触らない（IADR-0369）。
- 決めること: (1) 何を宣言で持つか、(2) client ロールと audience をどこで載せるか、(3) seed 利用者へどのロールをどう付けるか、(4) 既存の realm への当て方。

## 検討した選択肢

1. **共有の `roles` スコープへ audience 解決と client ロールの写像を足し、`account-console` に `basic`・`roles` を付ける**（issue の書き方。Keycloak の既定の形）。
   - `roles` は 23 のクライアント（bff・各サービス・AST・headlamp など）の既定スコープである。それらのトークンに `resource_access` と追加の `aud`
     （利用者が client ロールを持つクライアント）が載り、認可の面が広がる。却下。
2. **`account` と `account-console` を組み込みと同じ形で対に宣言し、`account-console` に `basic`・`profile` と写像（audience 解決・`account` のロールだけ）を直付けする（採用）。**
   - 効くのは `account-console` のトークンだけ。他のクライアントのトークンは変わらない（実測で 100 件一致。仕様書）。
   - 組み込みの作成が飛ぶ分（ロール 8 個・合成・スコープの割当・既定ロールの合成）は宣言で持つ。値は組み込みの realm を export して写した。
3. 後追い（`reconcile-realm.js`）だけで組み込みの client を直す（realm JSON に宣言しない）。
   - compose（import だけで後追いが無い）と空 PVC の初回 import が直らない。宣言が単一情報源でなくなる。却下。
4. seed 利用者へ `default-roles-platform` を付ける（Keycloak が実行時に作る利用者と同じ）。
   - 既定ロールの `offline_access`・`uma_authorization` まで付き、`fullScopeAllowed` の既定（true）の bff 等のトークンの `realm_access.roles` が変わる。
     `account` の 2 つを直に付ける形を採る。

## 決定

1. **`account` と `account-console` を realm JSON に対で宣言する。** 形は Keycloak 26.7.4 が組み込みで作るものと同じ（公開クライアント・
   `fullScopeAllowed=false`・`rootUrl=${authBaseUrl}`・`/realms/platform/account/*`・PKCE S256）。次も宣言で持つ:
   `roles.client.account`（`view-profile`・`manage-account`〔合成 `manage-account-links`〕・`manage-account-links`・`view-applications`・
   `view-consent`・`manage-consent`〔合成 `view-consent`〕・`view-groups`・`delete-account`）／
   `clientScopeMappings.account`（`account-console` に `manage-account`・`view-groups`）／
   `defaultRole` と `roles.realm[default-roles-platform]`（合成は `offline_access` と `account` の `view-profile`・`manage-account`。
   🔴 `uma_authorization` は書かない —— import の後で Keycloak が作って既定ロールへ足す。書くと import が落ちる）。
2. **`account-console` の既定スコープは `basic`・`profile`。** `account` のロールの写像（`oidc-usermodel-client-role-mapper`・
   `usermodel.clientRoleMapping.clientId=account`・`resource_access.${client_id}.roles`・アクセストークンだけ）と audience 解決を**直付け**する。
   共有の `roles` スコープは変えない。`account` の既定スコープも `basic`・`profile`（ログイン経路を持つクライアントの検査 7 を満たす）。
3. **人の seed 利用者 4 人へ `account` の `view-profile`・`manage-account` を直に付ける**（`users[].clientRoles.account`）。
4. **後追いの境界に例外を 1 つ足す: 既存の人の seed 利用者にも `account` の `view-profile`・`manage-account` だけは加える**
   （`SEED_USER_SELF_SERVICE_ROLES`）。加えるだけで外さない。実効（合成を含む）の割当で比べるので、既定ロール経由で持つ利用者には何もしない。
   宣言の他の client ロール・realm ロールは従来どおり既存の利用者へ当てない。
5. **検知**: `check-realm-constraints.js` 検査 10 が構成の欠落（対の宣言・既定スコープ・`fullScopeAllowed`・写像・ロール・スコープの割当・
   既定ロールの合成・seed 利用者の割当）を静的に止める。

## 理由

- 欠けていたのは「`account-console` が動く最小の構成」であり、共有のスコープを広げずに `account-console` の中で閉じて直せる。
- 例外（決定 4）の 2 つは、Keycloak が実行時に作るすべての利用者へ既定ロールで必ず与える基線である。効くのは本人のアカウント API だけで、
  他人や realm の管理には効かない。seed 利用者だけが import の都合で基線を欠いていた。例外を基線の 2 つに限るので、
  「人の利用者の権限は実行時が所有する」という境界の意味は保たれる。
- 宣言を正にしておけば、compose・空 PVC の初回 import・既存の realm（後追い）の 3 経路で同じ結果になる。

## 結果

- 3 経路（新規 import・旧 realm ＋後追い・旧 realm ＋ kcadm）で SC-16 の 3 画面と account API が 200（Playwright。仕様書）。
- 後追いは旧 realm に対して 11 件（`account` / `account-console` の説明・既定スコープ・写像・seed 利用者 4 人の割当）を当て、再計画 0 件。
- 他のクライアントのトークン（宣言の 25 クライアント × seed 利用者 4 人）は変わらない。
- 悪い影響・トレードオフ:
  - 組み込みの `account` / `account-console` を宣言で持つので、Keycloak の版上げで組み込みの形（ロールの追加など）が変わっても自動では追随しない。
    版上げの際は組み込みの realm を export して突き合わせる（IADR-0532 の同梱テーマの表と同じ扱い）。
  - 管理者が seed 利用者から `manage-account` を外しても、次の起動で後追いが戻す（決定 4。seed 利用者は開発用の固定値である）。
- 残余: AST 専用 realm（単体 E2E 用）は本件の対象外（利用者のログインに使わない）。

## 関連

- Supersedes: なし（IADR-0369 の境界表に例外を 1 つ足す。同 IADR へ日付つき追記で示す）。
- Superseded by: なし。
