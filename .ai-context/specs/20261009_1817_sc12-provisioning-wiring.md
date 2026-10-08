---
title: 作業仕様書 — SC-12 の IdP への書き込み口を配備へ配線し、稼働の Keycloak で作成・照会・属性の書き込み・補償を実測する（段 2。#1817）
type: spec
status: done
related_ids: [FR-16, FR-09, UC-09, SC-12, ADR-0123, ADR-0062, ADR-0088, ADR-0124, ADR-0095, IADR-0516, IADR-0329, IADR-0286, IADR-0485, IADR-0103]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0123_mcp-service-account-attributes-source-of-truth-is-idp.md 決定 2〜4・§結果「悪い影響」・フォローアップ 1・3
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 1（対になる秘密）
issue: "#1817"
---

# 作業仕様書 — SC-12 の書き込み口の配備の配線と稼働の Keycloak での実測（段 2。#1817）

> 本仕様書は実装着手前に作成した（着手 2026-10-09）。判断の記録は **IADR-0516 への日付つき追記**に置く（新しい決定は無い。段 1 の IADR-0516 決定 2 と §残余 1・2 が本段の中身を既に決めている）。
> 計画は project-planning `origin/main` の隣接クローン（読み取り専用）で読んだ。基点は MSP `origin/develop` `049a34e5`（段 1 の PR #1816 のマージ）。
> 🔴 **稼働中のクラスタ・Keycloak には何も実行しない。** 稼働の Keycloak での実測は CI の integration-stack（使い捨ての k3d クラスタ）へ載せる検査器で行う。

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: **ADR-0123 決定 2**（SC-12 の登録で Keycloak のクライアントを作り、属性を IdP へ書く）・**決定 3**（部分集合・個人資料の判定は IdP へ書く前）・**決定 4**（暫定手段）・**フォローアップ 1・3**。**ADR-0124 決定 1**（client secret は対になる秘密。SC-22 の対象外）。
- 機能要求: **FR-16**・**FR-09**。画面: **SC-12**。UC: **UC-09**。
- 起点 issue: **#1817**（#1786 の段 2。段 1 は PR #1816）。後続: #1818（食い違いの検知）。

## 緊急度

段 1 のマージ（`049a34e5`）以降、配備では `McpClientProvisioning:Provider` が未宣言なので、**無人の登録・差し替えは 503** である（IADR-0516 決定 2 の「未設定」）。本段はその期間を閉じる。

## 現状（実測。`049a34e5`）

| 事実 | 確かめ方 |
| --- | --- |
| helm の `services.mcp.extraEnv` は `McpClientProvisioning__Keycloak__{BaseUrl,Realm,ClientId}` を持ち、`Provider` と `ClientSecret` はコメントで置いてある | `deploy/helm/microservices-platform/values.yaml` L912-929 |
| realm に `mcp-client-admin` は無い。`realm-management` のロールを持つ SA は `identity-admin`（view-users・manage-users・view-realm）と `reset-gate`（view-realm・manage-realm）の 2 つ。`manage-clients` を持つ主体は 0 | realm JSON の `users[].clientRoles` |
| 同型の先例は `identity-admin`（MSP ns の Pod が env で読む機密クライアントの secret）: realm の宣言（dev の secret）・`externalsecret-identity-admin-oidc.yaml`・`bootstrap.sh` の `vkv_create_if_absent`・`k8s-local-up.sh` の `ESO != 1` の手動 apply と ESO の apply・`sc22-secret-items.json` の `deferred[]`・helm の非 optional な secretKeyRef・compose の dev 既定 | `git grep identity-admin` |
| `SecretItemBootstrapSeedTests` は「`vkv_create_if_absent` で作るパスの集合 ＝ `deferred[] ∪ excluded[]`」を突き合わせる | `Platform.Bff.Tests` |
| `k8s-local-up.test.js` は「`externalsecret-*.yaml` のすべてがいずれかのゲート組み合わせで apply される」を検査する | 同ファイル #1102 節 |
| `check-realm-constraints.js` は「realm-management のロールを持つ SA は `realm-management-roles` スコープを持つ」「SA 利用者が users[] にちょうど 1 つ」を検査する | 同ファイル L1042- |
| realm の後追い（`reconcile-realm.js`）は、無い client を宣言の secret で作り、SA のロールは次の周で当てる（`deferred` → 次の pass）。integration-stack の G9 は宣言と稼働の差分 0 件を要求する | `keycloak-realm-reconcile.test.js` |
| Keycloak の統合試験の器（Testcontainers）は無い。稼働の Keycloak に当たる検査は integration-stack の `--live` の検査器だけ（`seed-abac-policies.js` は port-forward ＋ `abac-seeder` の client_credentials で管理 API を呼ぶ） | `scripts/live-scripts.json` |
| McpServer の登録簿の `DisplayName` は `varchar(200)`、Keycloak のクライアントの `name` は 255 文字まで。**201〜255 文字の表示名は「IdP へは書けて登録簿で落ちる」を稼働の構成のまま作れる**（補償の実測に使う） | `Migrations/20260822201609_InitialCreate.cs` |

## 母集合（規則 9・10）

### 規則 9 — 誤りの側の文字列で全文書を走査した

`git grep -n -e '後続の段' -e '配線が入るまで' -e '配備の配線' -e '書き込み口をまだ' -e '未宣言' -e 'identity-admin-oidc' -e '17 本'`（`.ai-context/` と `CHANGELOG.md` を除く）:

| 箇所 | 扱い | 理由 |
| --- | --- | --- |
| `deploy/helm/microservices-platform/values.yaml`（mcp の注記「Provider は未宣言のまま置く」） | **直す** | 本段で宣言する |
| `docs/screens/SC-12_mcp-client-management.md`（冒頭の注記・§計画との対応「クライアント登録」・§状態の写し方の 503 の行・§無効化・§未決） | **直す** | 「配線が入るまで 503」が本段で誤りになる |
| `docs/tests/FR-16_mcp-server.md`（§未実施・残件「稼働の認可サーバーで確かめていない」） | **直す** | 本段で integration-stack に実測を載せる |
| `docs/api/FR-16_mcp-server.md`（503 の行） | **直す**（行の意味を「構成されていない配備」へ） | 配備では宣言済みになる |
| `docs/tests/SC-12_mcp-client-management.md` | **対象外**（範囲は後段の試験に委ねている） | 記述が本段で誤りにならない |
| `scripts/k8s-local-up.sh`（ESO の確認コマンドの本数の注記「常時 17 本」と `msp_es`・`msp_sync`） | **直す**（18 本へ数え直す） | ExternalSecret を 1 本足す |
| `deploy/local/vault/eso/bootstrap.sh`（末尾の確認の echo の列挙） | **直す** | 同上 |
| `docs/operations/paired-secret-rotation-runbook.md`（client の表） | **直す**（行を足す） | 対になる秘密が 1 つ増える |
| `docs/security/security.md`（dev 専用の平文の client secret の列挙・`identity-admin` の項） | **直す**（`mcp-client-admin` の項を足す） | 影響範囲（受け入れ基準 5）の置き場 |
| `src/.../ServiceAccountProvisioningRegistration.cs` の注記「本 PR の後の段で入る」 | **直す**（日付つき追記） | 本段で入る |
| `.ai-context/adr/IADR-0516` §残余 1・2・統制表 | **直す**（日付つき追記のみ。本文は書き換えない） | 凍結記録 |
| `.ai-context/specs/20261008_1786_*` | **直さない** | 確定済みの作業仕様書（point-in-time） |

### 規則 10 — この変更で新たに誤りになる自分の記述

- `k8s-local-up.sh` の「MSP ns は常時 17 本」は 18 本になる（**値は `msp_es` を数え直して出す**）。
- `#1617` の試験（`scripts.repo.test.js`）は「T-25 only red」の `if:` を場面ごとに評価し、`allGreen` に後段の門の id を列挙する。**門を足して `if:` に条件を足すと、この試験の陽性の場面が赤になる** → 試験の `allGreen` と「ほかの門」の列挙に新しい門を足す。
- `#1550` の試験は live の入口を `live-scripts.json` と `scripts/README.md` の節へ載せることを要求する → 新しい検査器を両方へ載せる。
- `SecretItemBootstrapSeedTests` は `vkv_create_if_absent` のパスと `deferred[]` の一致を要求する → 両方へ同時に足す。

## 設計（決定は IADR-0516。本段は配線と実測）

1. **realm**: 機密クライアント `mcp-client-admin`（`publicClient=false`・`serviceAccountsEnabled=true`・`standardFlowEnabled` / `implicitFlowEnabled` / `directAccessGrantsEnabled`=false・`redirectUris` / `webOrigins` は空・既定スコープ `realm-management-roles` だけ・dev 専用の secret）。SA 利用者 `service-account-mcp-client-admin` に `realm-management` の **`manage-clients`・`manage-users` だけ**（realm ロールは無し）。
2. **secret の供給**（`identity-admin-oidc` と同型）: ExternalSecret `mcp-client-admin-oidc`（キー `client-secret` ← `msp/mcp-client-admin-oidc`）・`bootstrap.sh` の `vkv_create_if_absent`（無いときだけ作る。対になる秘密）・`k8s-local-up.sh` の `ESO != 1` の手動 apply と ESO の apply・同期待ち（mcp-service は既に rollout の対象）・`sc22-secret-items.json` の `deferred[]`。
3. **helm**: `McpClientProvisioning__Provider=keycloak` と `McpClientProvisioning__Keycloak__ClientSecret` の secretKeyRef（`mcp-client-admin-oidc` / `client-secret`。**非 optional**）。**アプリ側には既定値を持たない**（IADR-0286。Secret が無ければ Pod が起動しない）。dev の値は realm の宣言と同値で、供給の経路（Vault の種・手動 apply・compose）だけが持つ（`identity-admin` と同じ）。
4. **compose**: `mcp-service` に Provider・BaseUrl・Realm・ClientId・ClientSecret（`${MCP_CLIENT_ADMIN_CLIENT_SECRET:-…dev…}`）。
5. **静的な固定**:
   - `scripts.repo.test.js`: realm の宣言（機密・SA のみ・人の流れは閉・ロールの集合がちょうど 2 つ・否定形 `manage-realm` / `impersonation` / `realm-admin` を持たない・realm ロールを持たない・`manage-clients` を持つ主体は `mcp-client-admin` だけ）、供給の連鎖（ExternalSecret・Vault の種・手動 apply・`deferred[]`・compose の dev 既定 ＝ realm の secret）。
   - `scripts/helm-mcp-client-provisioning.test.js`（新）: 既定と values-local の描画で、`McpClientProvisioning__*` を持つ Deployment はすべて `Provider=keycloak` を持つ（**Provider 未宣言の配備が残らない**）・Provider は mcp-service だけ・ClientSecret は非 optional の secretKeyRef でリテラルを持たない、変異。CI は `static-checks-units`（helm 入り）。
   - `k8s-local-up.test.js`: ESO=1 / 既定の対（identity-admin と同型）。
6. **稼働の Keycloak での実測**（新 `scripts/check-mcp-client-provisioning.js --live`。integration-stack の門）:
   - 接続: `seed-abac-policies.js` と同じ一時 port-forward（mcp-service・keycloak）。登録者は `abac-seeder`（`platform-admin`。secret は realm ファイルから）。照会の主体は**入口と別の**主体（master の管理者。Secret `keycloak-admin` を kubectl で読む）。`mcp-client-admin` の secret は Secret `mcp-client-admin-oidc` から読む（補償の主体の削除の実測）。
   - 測ること:
     - M1 登録（`department` ＋ 集合値 `projects`）が 201（503 にならない）。Keycloak のクライアントに `msp.mcp-client.managed-by=mcp-server`・機密・SA つき・人の流れは閉。
     - M2 `users?username=service-account-<client>&exact=true` がちょうど 1 件で、属性が入っている（集合値は多値）。**フォローアップ 3**。
     - M3 差し替え（入口の印あり）が 200 で、Keycloak の属性が丸ごと置き換わる。
     - M4 部分集合の外れ（`tags`）と `doc_scope=private-note` の登録は 400 で、Keycloak にクライアントも SA 利用者も作られない。差し替えの外れも 400 で属性は変わらない。
     - M5 プラットフォームのクライアント名 `abac-seeder` の登録は 400 で、`service-account-abac-seeder` の属性は変わらない。差し替えは、入口ができる前の行（psql で登録簿へ置く無人の行 `abac-seeder`）に対して 400 で、属性は変わらない（入口の印の確かめ）。
     - M6 補償: 201〜255 文字の表示名の登録は「IdP へ書けて登録簿で落ちる」ので 5xx になり、Keycloak にクライアントも SA 利用者も残らず、登録簿にも行が無い。併せて、`mcp-client-admin` の資格情報で M1 のクライアントを消せる（補償の主体の削除の権限）。
   - 片付け: 作ったクライアント・登録簿の行（psql）を消す（使い捨てのクラスタでも残さない）。

## 受け入れ基準 → 試験

| issue の受け入れ基準 | 試験 |
| --- | --- |
| AC1 realm の宣言（ロールの集合・否定形） | `scripts.repo.test.js` #1817 節 ＋ `check-realm-constraints.js`（スコープ・SA 利用者 1 つ） |
| AC2 secret を既定値なしで供給（ES・Vault・手動経路と試験・SC-22 項目表） | `scripts.repo.test.js` #1817 節・`k8s-local-up.test.js` #1817・`SecretItemBootstrapSeedTests` |
| AC3 helm・compose の配線、Provider 未宣言の配備が残らない | `helm-mcp-client-provisioning.test.js`・`scripts.repo.test.js`（compose） |
| AC4 稼働の Keycloak での実測 | `check-mcp-client-provisioning.js --live`（integration-stack の門）。自己試験（純関数）は `--self-test` を CI の static-checks で |
| AC5 漏えい時の影響範囲とローテーション | `docs/security/security.md`・`docs/operations/paired-secret-rotation-runbook.md` |

## 本段に入れないもの

- **IADR-0516 決定 4a（無効化の IdP の `enabled` への写し）**: 段 1 の仕様書は段 2 に含めたが、issue #1817 の受け入れ基準に無い。503 を閉じる本段を小さく保つため外し、残余として IADR-0516 の追記と報告に書く。
- 食い違いの検知（#1818）。

## 検証

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`node scripts/k8s-local-up.test.js`・`node scripts/helm-mcp-client-provisioning.test.js`・`node scripts/check-mcp-client-provisioning.js --self-test`・`node scripts/check-realm-constraints.js`・`node scripts/keycloak-realm-reconcile.test.js`。
- `check-deploy-manifests`・文書系の検査器一式・`dotnet build` / `dotnet test`（McpServer・Platform.Bff.Tests の SecretItem*）・`dotnet format --verify-no-changes`。

## ［2026-10-09 追記］実装後の記録

### 母集合の引き直し（規則 10）で見つかったもの

- `scripts.repo.test.js` #683 の「検査器の母集合が 61 本」は、新しい検査器 `check-mcp-client-provisioning.js` で 62 本になった（ラチェットが発火。理由を同箇所に記録）。
- `docs/operations/secret-rotation-runbook.md` の「`deferred[]` の 17」「計 24 KV」「クライアントシークレット 17 項目」と `bootstrap.sh` の「24 KV」は、18・25 へ数え直した（`docs/operations/secret-item-console-injection-runbook.md` の 2026-09-28 の日付つき追記の「24 KV」は、その日の事実として据え置く）。
- 実測の登録者に realm の `abac-seeder` を使えない（既定スコープに `profile` が無く `preferred_username` が載らない → 部分集合の判定が「検証できません」の 400 になる）。使い捨ての登録者を検査器が作って消す形にした（IADR-0516 の 2026-10-09 追記）。

### 変異試験（すべて戻し、`#1817` 節 10 本が再び緑であることを確認）

| # | 変異 | 落ちた試験 |
| --- | --- | --- |
| 1 | SA に `manage-realm` を足す | 2 件（ロールの集合・否定形） |
| 2 | SA に `impersonation` を足す | 2 件（同上） |
| 3 | `standardFlowEnabled=true` | 1 件 |
| 4 | `identity-admin` に `manage-clients` を足す | 1 件（保持者は 1 つ） |
| 5 | `deferred[]` から外す | 1 件 |
| 6 | compose の Provider を消す | 1 件 |
| 7 | Vault の種を消す | 1 件 |
| 8 | 手動 apply の dev 既定を realm と違える | 1 件 |
| 9〜13 | helm の描画: Provider の欠落・値違い・ClientSecret の optional 化・リテラル化・別ワークロードへの混入 | `helm-mcp-client-provisioning.test.js` の変異 5 本がそれぞれ赤を確かめる |

### 検証の結果

- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`: 1009 件緑。`node scripts/k8s-local-up.test.js`: 265 件緑。`node scripts/helm-mcp-client-provisioning.test.js`: 8 件緑。`node scripts/check-mcp-client-provisioning.js --self-test`: 9 件緑（独立監査の是正で M6 の陰性対照 2 件を追加。指定なしは exit 3）。
- `check-realm-constraints`・`keycloak-realm-reconcile.test.js`（38）・`check-deploy-manifests`（chart 1 / overlay 17）・`check-trace-blocks`・`check-adr-numbering`・`check-workflow-job-refs`・`gen-knowledge-graph --check`・`check-cross-repo-refs`・`check-plan-id-qualification`・`check-doc-links`・`check-default-credentials`・`check-reading-budget`・`actionlint`（変更した 2 本）: 緑。
- `dotnet test` McpServer.Tests 298 件・Platform.Bff.Tests の SecretItem* 186 件: 緑。`dotnet build src/platform/backend/backend.slnx`: 警告 0。
- 🔴 **稼働の Keycloak での実測（M1〜M6）は本 PR の中では走っていない。** integration-stack は PR で起動しないので、develop へのマージ後の最初の実行が初回の実測になる（稼働中のクラスタには何も実行していない）。

## ［2026-10-09 追記 / PR #1827 の監査（条件付き GO）への対応］

- 🟡2 M6 を 500 に限定し（`evaluateCompensationResponse`）、管理イベントで `mcp-client-admin` の「作成 → 削除」を確かめる（`evaluateCompensationEvents`）。自己試験 9 件（502・503・504・400・201 と、作成なし・削除なし・削除が作成より前・資源違い・主体違いの陰性対照）。
- 🟡3 影響範囲の「届かないもの: レルムの設定」を改めた（`reset-gate` / `identity-admin` の secret を経由して間接的に届く。レルムの全権の漏えいとして扱う）。
- 🟡4 「本番流用の禁止」と runbook に `mcp-client-admin` と「dev 以外では起動の直後に回す」を足した。機械の守りは #1830 へ分離（起動器に dev かどうかの文脈が無い）。
