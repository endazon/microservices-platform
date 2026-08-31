---
title: IADR-0321 SC-17 の反映先を実 Keycloak にし、偽の身元プロバイダを配備ホストで選べなくする
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-09, UC-05, SC-17, NFR-09, ADR-0004, ADR-0026, ADR-0032, ADR-0036]
author: implementation-agent
created: 2026-08-31
updated: 2026-08-31
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0004_authn-authz-platform.md
  - planning:projects/microservices-platform/07_adr/ADR-0026_authentication-ux-and-account-management.md
---

# IADR-0321: SC-17 の反映先を実 Keycloak にし、偽の身元プロバイダを配備ホストで選べなくする

- 状態: Accepted
- 日付: 2026-08-31
- 決定者: implementation-agent（実装担当）

## 起点・関連

- 関連する計画書 ID: FR-05 / FR-09 / UC-05 / SC-17 / ADR-0004 / ADR-0026 / ADR-0032
- 関連する実装仕様書: `.ai-context/specs/20260831_issue-1101_identity-admin-keycloak-provider.md`
- 先行: IADR-0301（SC-17 の抽象・権限範囲・provider 選択。**本 IADR は同 ADR 決定 3 が「暫定」と
  明記した状態の解消である**）/ IADR-0286（既定資格情報を持たず未注入は起動失敗）/
  IADR-0251・IADR-0273（BFF セッションとバックチャネルログアウト）/ IADR-0197（realm import の罠）
- issue: #1101（親 #438・関連 #439 / #1088 / #1114）

## コンテキストと課題

IADR-0301 決定 3 は provider の宣言を必須にし、**配備側は当面 `in-memory` を明示する**と定めた。
その暫定が 2026-08-30 の棚卸しで「解けていない」ことが実測され、#1101 が起票された。

着手前に 3 点を実測した（詳細と出力は作業仕様書 §着手前の実測）。

1. **実プロバイダの実装は在る**（`KeycloakIdentityAdminClient`・267 行）。射程は「実装を作る」ではない。
2. 🔴 **issue と #439 のコメントが書く「`realm-management` クライアントが realm に無い」は誤りである。**
   `realm-management` は Keycloak が realm ごとに自動生成する組み込みクライアントで、**稼働 realm に
   在った**。export JSON の `clients` 配列を数えた結果を realm を数えた結果と取り違えていた。
   欠けていたのは**その 3 ロールを service account に持つ機密クライアント**である。
3. **`in-memory` の実害は静かな縮退である。** 画面は 200 を返し、Keycloak に居ない 4 名の偽データを
   描き、保存も無効化も成功に見える。**Pod が再起動した瞬間に全部消える。**

さらに、**実 Keycloak に対して測って初めて分かったことが 3 つあった**（スタブでは絶対に出ない）。

- 🔴 **A. 機密クライアントに `realm-management` のロールを割り当てても、Admin API は 403 を返す。**
  本 realm は `clientScopes` を明示宣言しており（Issue #88 の経緯）、**組み込みスコープが生成されない**。
  宣言済みの `roles` スコープは realm ロールしか載せないため、トークンに `resource_access` が出ない。
  Keycloak の Admin API は**トークンの `resource_access["realm-management"].roles`** で認可するので、
  role-mappings を正しく持っていても通らない。
- 🔴 **B. `PUT /users/{id}` は部分更新ではない。** `{"enabled": false}` だけを送ると
  `firstName` / `lastName` / `email` が**実際に消えた**（204 が返るので気付けない）。
- 🔴 **C. realm の user profile が unmanaged 属性の書き込みを許していないと、ABAC 属性は 204 を
  返しながら黙って捨てられる。** `clearance` / `department` は user profile に宣言していない
  unmanaged 属性であり、Keycloak 24 の既定（`unmanagedAttributePolicy` 無し＝無効）では
  **書き込みが no-op になる。** import では入るので「realm には在るのに書けない」状態になる。
  **B と C はどちらも「成功を返して静かに壊す」** —— #1101 が潰そうとしている穴が、配備の設定から
  realm の設定へ 1 段ずれて再発する形である。

## 決定

### 決定 1: 機密クライアント `identity-admin` を realm へ登録し、権限は 3 ロールに限る

`serviceAccountsEnabled: true` / `standardFlowEnabled: false` / `directAccessGrantsEnabled: false`
（後者は #438 検査 5 の MFA 迂回禁止）。service account へ与えるのは **`realm-management` の
`view-users` / `manage-users` / `view-realm` だけ**で、realm ロールは 1 つも与えない
（IADR-0301 決定 2 の再確認。取り込み経路の `abac-seeder` とは別主体）。

必要最小であることは端点との対応で示せる。

| 操作 | 端点 | 要るロール |
| --- | --- | --- |
| 一覧・ロールマッピングの読み | `GET /users`, `GET /users/{id}/role-mappings/realm` | `view-users` |
| 割当可能ロールの列挙 | `GET /roles` | `view-realm` |
| 属性更新・`enabled` 切替・ロール付け外し・セッション失効 | `PUT /users/{id}`, `POST\|DELETE /users/{id}/role-mappings/realm`, `POST /users/{id}/logout` | `manage-users` |

**`manage-users` は落とせない**（SC-17 の主要素が全部書き込みだからである）。過剰でないことは
稼働クラスタで陰性対照として測った —— クライアント作成も realm 更新も 403 になる。

### 決定 2: `realm-management` のクライアントロールを載せる専用スコープを作り、`identity-admin` にだけ付ける

課題 A の解消。**既存の `roles` スコープへクライアントロールのマッパーを足さない** ——
足すと `bff` / `platform-spa` / `wiki-js` を含む全クライアントのトークンが太り、影響範囲が
本件と無関係な経路へ広がる。新しいスコープ `realm-management-roles`（`oidc-usermodel-client-role-mapper`
を `usermodel.clientRoleMapping.clientId=realm-management` で絞ったもの）を作り、
`identity-admin` の `defaultClientScopes` にだけ置く。

### 決定 3: realm の user profile に `unmanagedAttributePolicy: ADMIN_EDIT` を宣言する

課題 C の解消。**`clearance` / `department` を managed 属性として個別宣言はしない** ——
値域と属性体系の正は SC-09 の属性辞書であり（IADR-0301 決定 5）、realm へ写すと辞書が 2 か所になる。
`ADMIN_EDIT` は「管理者だけが読み書きでき、本人は触れない」であり、SC-17 の
「システム管理者ロール限定」「権限は本人から変更不可」（計画 05_screens §SC-16 制約）と一致する。

realm export では `components["org.keycloak.userprofile.UserProfileProvider"]` として宣言する。
🔴 **`components` を宣言しても署名鍵の自動生成は止まらないことを実測で確かめた**
（`clientScopes` や `authenticationFlows` と同じ罠を疑って測った。JWKS は生成され、
client_credentials も通った）。**測らずに書かないこと。**

### 決定 4: Keycloak への更新は read-modify-write にし、書けたことを確かめる

課題 B の解消。現在の表現を取得し、変更点だけ上書きし、**全体を PUT する**。サーバ計算の
読み取り専用フィールド（`access` / `disableableCredentialTypes` / `userProfileMetadata`）は送り返さない。

併せて、属性の差し替えは**書き戻した値を読み直して突き合わせ、食い違ったら例外にする**
（`EnsureAttributesWereApplied`）。決定 3 が外れた realm に対して、画面へ 200 と「保存しました」を
返さないためである。**成功を返して黙って捨てるより、失敗として上げる。**

### 決定 5: `in-memory` は**非配備ホスト**でしか選べない（deny by default）

受け入れ基準 6 への回答。**警告ログの強化ではなく起動失敗にする。**

- 宣言を必須にしただけでは「配備が明示的に偽物を宣言すること」を止められなかった（それが #1101 の実態）。
- 警告 1 行は運用が見落とす。画面は 200 を返し、次の再起動まで変更が残って見える。
- **否定形（Production / Staging を弾く）にしない** —— 環境名を `Prod` と書いた配備が素通りする。
  許可集合 `{Development, Testing, Integration}` を持ち、それ以外は落とす。
  **環境変数を与えない配備は `Production` になる**ので、#1101 で壊れていた経路はここで落ちる。
- **dev の利便は壊さない**: `dotnet run` は Development、単体テスト器は Testing、統合テスト器は
  Integration を宣言済みで、いずれも許可集合に載る（**その 3 つすべてで通ることを陽性対照で固定した**）。

## 理由（なぜこの形か）

- **realm 側と実装側の両方が要る。** realm だけ直しても部分更新でデータが消え、実装だけ直しても
  属性は黙って捨てられる。**分割して片方だけ出すと、`in-memory` より悪い状態を配備することになる**
  （名前とメールが消えるため）。よって 1 つの変更として出す。
- **陽性対照と陰性対照を対で置く。** 「実在する利用者が引ける」だけでは「常に空を返さない」ことしか
  言えず、「実在しない利用者は引けない」だけでは「常に 404」と区別できない。両方を測った。

## 影響

- 配備（helm / compose）は `IdentityAdmin__Provider=keycloak` ＋ 4 キー。ClientSecret は
  **非 optional** な `secretKeyRef` で、欠けたら Pod が起動しない（`bff-oidc` と同型）。
- realm import が 1 クライアント・1 クライアントスコープ・1 サービスアカウント利用者・
  1 コンポーネントぶん増える。
- **`in-memory` へ戻す配備は起動しない。** 戻したい場合は環境を非配備ホストとして宣言することになり、
  それ自体が声の大きい宣言になる。

## 測っていないこと（申し送り）

- **無効化した利用者の既存セッションが次の要求で 401 になること**は測れなかった。realm は TOTP 必須で
  直接付与（password grant）を全クライアントで無効にしてあるため、**非対話で人のセッションを作れない**。
  失効の要求が Keycloak へ届いていること（`POST /users/{id}/logout` が成功し `notBefore` が前進すること）
  までは測った。残りは #439 に残す。
- **再有効化の経路**は稼働クラスタでは通していない（`SetEnabledAsync(true)`。無効化と同一メソッドで、
  単体テストが固定している）。

## 記録に留める（検査器は足さない）

課題 A（クライアントロールがトークンに載らず Admin API が 403）は realm JSON だけで静的に判定し得るが、
**同型の事故は今回が 1 回目である**ため検査器は足さない（`CLAUDE.md`「同型の事故が 2 回起きたら」）。
2 回目が起きたら `check-realm-constraints.js` へ「service account に `realm-management` の
クライアントロールを持つクライアントは、クライアントロールを載せるスコープを持つこと」を
陽性対照つきで足すこと。
