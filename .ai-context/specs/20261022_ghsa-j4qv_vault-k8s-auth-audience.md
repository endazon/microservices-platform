---
title: 作業仕様書 — 秘匿管理の kubernetes 認証の対象者（audience）の検証（GHSA-j4qv-2w99-xg5p の 5 点目）
type: spec
status: done
related_ids: [NFR-18, ADR-0095, ADR-0132, IADR-0585, IADR-0096, IADR-0433, IADR-0495, IADR-0548]
author: claude
created: 2026-10-22
updated: 2026-10-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0132 （秘匿管理は OpenBao）
  - planning:projects/microservices-platform/07_adr/ADR-0095 （SC-22 の秘密情報の投入面）
issue: "GHSA-j4qv-2w99-xg5p"
---

# 作業仕様書 — 秘匿管理の kubernetes 認証の対象者（audience）

> 本仕様書は実装着手前に作成した（着手 2026-10-22）。判断の記録は **IADR-0585**（新規）に置く。
> 多層防御の強化・最小権限。参照は GHSA の ID だけとする。
> GHSA-j4qv-2w99-xg5p の対応の **5 点目**（発行者・対象者の検証）。

## 起点となる計画書（トレーサビリティ）

- 要求: **NFR-18**（秘匿情報の管理）。
- 計画 ADR: **ADR-0132**（秘匿管理は OpenBao）・**ADR-0095**（SC-22 の投入面）。
  矛盾しない（製品・供給の仕組み〔ESO・`ClusterSecretStore`・k8s auth〕は変えず、role とトークンに対象者を足すだけ。
  ADR-0095「供給の仕組みには触れない」は計画側の宣言であり、実装側の強化を妨げない）。
  計画に k8s auth の audience を定める記述は無い（`grep -rn -i audience projects/microservices-platform/07_adr | grep -i -E "vault|openbao|秘匿"` が 0 件）。
- 実装 ADR: **IADR-0585**（新設）。前提: IADR-0096（ESO の k8s auth）・IADR-0433（BFF の書き込み role）・IADR-0495（datasource の読み取り role）・IADR-0548（バックアップの role）。

## 要件（コーディネーター指示 2026-10-22）

1. 秘匿管理の kubernetes 認証の role は、対象者（audience）を秘匿管理に限ったトークンだけを受ける。
2. 名乗る側（ESO・BFF・datasource・バックアップ）は、その対象者のトークンを渡す。
3. 稼働中の PoC を止めない配備の順序を決め、PR 本文に書く。
4. 発行者（issuer）の検証の要否を判断し、記録する。アプリの JWT・Keycloak の broker・RabbitMQ の OAuth の該当性を記録する。

## 受け入れ基準

| # | 基準 | 写像先 |
| --- | --- | --- |
| A1 | deploy/ の bootstrap が書く k8s auth の role は全て `audience=vault`（母集合は走査。想定の 4 つが見つかる） | `scripts/vault-k8s-auth-audience.test.js` 1・`platform-backup.test.js` 11・`SecretItemVaultPolicyTests`・`ConnectorSecretVaultPolicyTests` |
| A2 | kubernetes 認証の store は `serviceAccountRef.audiences` ＝ `[vault]` | `vault-k8s-auth-audience.test.js` 2 |
| A3 | helm の描画で、Vault の所在を持つ Deployment は全て audience `vault`・期限 600〜3600 秒の projected token を読み取り専用でマウントし、`Vault__ServiceAccountTokenPath` がそれを指す。所在が空の Deployment には置かない（既定 0・所在を与えて 2・values-local 1） | `vault-k8s-auth-audience.test.js` 3 |
| A4 | BFF・datasource のコードの既定のトークンの置き場はチャートのパスと同じで、k8s API 用の既定のトークンではない | `vault-k8s-auth-audience.test.js` 3b・.NET の 2 本 |
| A5 | バックアップの CronJob の projected token の audience は `vault` | `vault-k8s-auth-audience.test.js` 4・`platform-backup.test.js` 11 |
| A6 | 起動器は chart（名乗る側）を当ててから role に audience を付ける | `vault-k8s-auth-audience.test.js` 5 |
| A7 | 判定が空振りしない（各判定に変異） | 同上の各試験の変異 |
| A8 | 稼働中の環境を 2 段で配備できる: role の書き込みは全て口（`vault_role_write_cmd`）を通り、既定は `audience=vault` を書き、`VAULT_ROLE_AUDIENCE_DEFER=1` のときだけ書かない（1 以外の値は既定。他の語は触らない） | `vault-k8s-auth-audience.test.js` 5b |

## 母集合（規則 9・10）

秘匿管理へ login する全経路を、次の走査で引いた（作業ツリー＝develop e67918c2。`.ai-context/`・`CHANGELOG.md`・submodule を除く）。

- `git grep -nIiE "auth/kubernetes|kubernetes/login|serviceaccount/token|serviceAccountRef|bound_service_account|TokenPath"`
- `git grep -nIE "VAULT_ADDR|vault\.platform-infra|:8200"`（クライアントの所在から逆引き）
- AST（`/home/user/ai-stock-trading`）: `git grep -nIiE "kubernetes/login|auth/kubernetes|serviceAccountRef"` は 0 件（AST は ESO の store を参照するだけ）。

| 経路 | 受ける側（role） | 名乗る側（トークン） | 本件の扱い |
| --- | --- | --- | --- |
| ESO（`ClusterSecretStore vault-backend` の k8s 認証版） | `eso`（`deploy/local/vault/eso/bootstrap.sh`） | ESO が TokenRequest で作る `external-secrets` SA のトークン | role に audience、store に `audiences: [vault]` |
| BFF（SC-22 の書き込み） | `bff-secret-writer`（同） | Pod の SA `bff` のトークン（`VaultKvClient`） | role に audience、chart が projected token を置き、既定のパスも変える |
| datasource（コネクタの資格情報の読み取り） | `datasource-connector-reader`（同） | Pod の SA `datasource-service` のトークン（`VaultConnectorSecretOptions`） | 同上 |
| 日次バックアップ（raft のスナップショット） | `platform-backup-vault`（`deploy/local/platform-backup/vault/bootstrap.sh`） | CronJob の projected token（`backup.sh`） | role に audience、projected token に `audience: vault` |
| ESO の token 認証版の store・運用者の root トークン・OIDC（人の SSO） | k8s auth ではない | — | 射程外（対象者の概念が k8s auth と別） |

- `auth/kubernetes/config` を書くのは 2 つの bootstrap（同じ値）。issuer は設定しない（IADR-0585 決定 3）。
- 除外: `.ai-context/specs/`（point-in-time の記録）。

## 該当しないと判断したもの（根拠）

- **アプリの JWT の発行者・対象者**: 共有の `AuthExtensions` は `ValidateAudience = true`（`Auth:Audiences`。IADR-0523）で、
  `ValidateIssuer` は既定の true のまま弱めていない（IADR-0086 決定 3）。`git grep -nE "Validate(Issuer|Audience)\s*=\s*false" -- src/*/backend ':!*Tests*'` は 0 件。
  MCP の面は `mcp-server` だけを受ける（IADR-0516）。
- **Keycloak の broker（外部 IdP）**: realm の宣言に `identityProviders` が無い（`git grep -n '"identityProviders"' -- deploy` が 0 件）。
- **RabbitMQ の OAuth**: `auth_oauth2` / `rabbitmq_auth_backend_oauth2` を使っていない（`git grep -nIl "auth_oauth2" -- deploy` が 0 件）。
  ブローカへはサービスごとのユーザーで名乗る（IADR-0577）。

## 実装

- `deploy/local/vault/eso/bootstrap.sh`: 3 つの role に `audience=vault`。
- `deploy/local/platform-backup/vault/bootstrap.sh`・`cronjob.yaml`: role に `audience=vault`、projected token に `audience: vault`。
- `deploy/local/vault/eso/clustersecretstore-k8s.yaml`: `serviceAccountRef.audiences: [vault]`。
- `deploy/helm/microservices-platform/templates/deployment.yaml`: `vault.address` が空でないサービスに projected token（audience `vault`・600 秒）を
  `/var/run/secrets/vault/token` へ置き、`Vault__ServiceAccountTokenPath` で渡す。
- BFF `VaultOptions`・datasource `VaultConnectorSecretOptions`: 既定の置き場を同じパスにする（定数 `DefaultServiceAccountTokenPath`）。
- 配備の 2 段の口: `scripts/lib/vault-role-audience.sh`（`VAULT_ROLE_AUDIENCE_DEFER=1` のときだけ role の書き込みから `audience=vault` を除く）。2 つの bootstrap の role の書き込みはこれを通す。
- 試験: `scripts/vault-k8s-auth-audience.test.js`（新規・CI の static-checks-units）・`scripts/platform-backup.test.js`・.NET の 2 本。
- 文書: `docs/operations/platform-infra-backup-runbook.md`（確かめ方）・`deploy/local/vault/eso/README.md`。

## 配備前の手順（PoC）

- 配備は閉場後。値・Secret の変更は無い。
- 順序（IADR-0585 決定 4）: **名乗る側を先、role の audience を後**。起動器は 1 回で role まで当てるので、
  稼働中の環境では **2 段に分ける**（AI レビュー 🟡 の対応。2026-10-22 追記）。手順の正は IADR-0585「結果・残る制約」と PR 本文。
  1. 1 段目: `VAULT_ROLE_AUDIENCE_DEFER=1` を付けて起動器を通常の引数で再実行する（名乗る側だけが入れ替わり、role は audience の無いまま）。
  2. 確かめる: BFF の書き込み（`/admin/secrets` で 1 項目）・`kubectl get externalsecret -A` が SecretSynced（force-sync で再同期させたもの）・
     datasource の読み取り・バックアップの CronJob を手で起こして Complete。失敗したら 2 段目へ進まず、前の版で起動器を再実行して名乗る側を戻す。
  3. 2 段目: 変数なしで起動器を再実行する（role に audience が付く）。同じ確かめと、4 つの role の `audience` が `vault` であること。
- 切り戻し（2 段目の後）: `audience=""` で外せることは読解であり実機未検証。2 段目の直後に 1 つの role で 1 回試して戻す。
  外れなければ代替（role を消して `VAULT_ROLE_AUDIENCE_DEFER=1` で作り直す）による。

## 検証

- `node scripts/vault-k8s-auth-audience.test.js`（7 本。5b は 2 段の口）・`node scripts/platform-backup.test.js`（13 件）・`node scripts/helm-pod-security.test.js`・
  `node scripts/k8s-local-up.test.js`（367 本）・`node scripts/check-deploy-manifests.js`。
- .NET: `Platform.Bff.Tests`（Vault・SC-22 の 187 本。全体は Docker の要る Valkey の 5 本を除き緑）・`DataSourceService.Tests`（463 本）。
  `dotnet format --verify-no-changes`（変更したファイル）。
- 新しい試験は、deploy/ と src/ の変更を外した作業ツリーで赤になることを確かめた。
