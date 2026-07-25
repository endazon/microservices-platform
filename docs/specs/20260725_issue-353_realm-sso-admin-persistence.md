---
title: "dev SSO 管理者 identity とツール別 claim mapper を realm.json に恒久化する（Issue #353）"
type: spec
status: done
related_ids:
  - IADR-0104
  - IADR-0103
  - IADR-0090
  - IADR-0092
  - IADR-0093
  - IADR-0094
  - IADR-0095
author: claude
created: 2026-07-25
updated: 2026-07-25
related_specs:
  - "../adr/IADR-0104_realm-sso-admin-persistence.md"
  - "../../deploy/keycloak/microservices-platform-realm.json"
---

# 仕様書: dev SSO 管理者 identity とツール別 claim mapper の realm.json 恒久化（Issue #353）

## 起点

`#353`（経路B ローカルツールの Keycloak SSO 一括連携）。SSO 化後の管理者ログイン成立に必要な設定が realm
再インポート/Pod 再起動で揮発する live 操作だった。その claim 設計と背景は `[[IADR-0103]]` に、realm へ焼き込む
恒久化の決定は本作業の `[[IADR-0104]]` に記録する。

本作業は #353 系の恒久化のうち **realm へ恒久定義できる部分（admin identity ＋ ツール別 claim mapper）だけ**を
切り出し、`deploy/keycloak/microservices-platform-realm.json` へ**追加のみ**で焼き込む。ESO 後 rollout・argocd
DNS・Vault listing-visibility・Wiki.js DB seed・up-script は**本 PR の対象外**（別 PR＝#389 系）。

## 変更内容（realm.json への追加のみ）

| 対象 | 追加内容 |
| --- | --- |
| `admin` ユーザー（新規） | enabled・emailVerified・dev パスワード `admin`（非一時）／realm ロール `platform-admin`/`platform-operator`/`wiki-editor`/`Administrators`／グループ `/clearance/restricted`・`/department/engineering`／client ロール `minio:consoleAdmin` |
| realm ロール `Administrators`（新規） | Wiki.js の管理者グループ名に文字列一致させる dev ロール |
| `minio` client ロール `consoleAdmin`（新規） | MinIO 組込ポリシー名。`policy` claim を単一値に保つための client ロール |
| `minio` client mapper | `minio-realm-roles`（realm ロール多値）→ **`minio-client-roles`**（`oidc-usermodel-client-role-mapper`・`usermodel.clientRoleMapping.clientId=minio`・claim `policy`・multivalued）へ差し替え（旧 realm ロール mapper は削除） |
| `wiki-js` client mapper `wikijs-realm-roles`（新規） | `oidc-usermodel-realm-role-mapper`・claim `groups`・multivalued |
| `headlamp` client mapper `headlamp-realm-roles`（新規） | 同型・claim `groups`・multivalued |

## 非対象

- ESO 供給後 rollout／argocd DNS エイリアス／Vault listing-visibility／Wiki.js DB seed／up-script（別 PR＝#389 系）。
- 本番 chart（`deploy/helm`）・compose・アプリコード・realm の `developer`/`poc-*`・既存 client・既存 mapper は無改変。
- redirect URI（`headlamp`/`spa-web` 集約後 URL）は #377（マージ済）で恒久化済みのため触らない。
- 取引/実弾は無改変。

## 受け入れ基準と検証

- [x] `realm.json` が JSON として妥当・`node scripts/check-realm-constraints.js` が OK（全フィールド ≤255 文字＝SQLSTATE 22001 回避）
- [x] `admin` が realm ロール `platform-admin`/`platform-operator`/`wiki-editor`/`Administrators` ＋ client ロール `minio:consoleAdmin`、グループ `/clearance/restricted`・`/department/engineering` を持つ
- [x] `minio` の mapper が `oidc-usermodel-client-role-mapper`（claim `policy`）で、旧 realm ロール多値 mapper `minio-realm-roles` が無い
- [x] `wiki-js`・`headlamp` に claim `groups` の realm-role mapper がある
- [x] realm の既存ユーザー（`developer`/`poc-*`）・既存 client・既存 mapper は無改変（`git diff` で確認＝追加のみ）
- [x] 平文の本番 secret 無し（admin は dev 既定パスワードのみ）・gitleaks green
- [x] `node scripts/check-doc-links.js` green（新規 IADR/spec の相対リンク実在）

## 反映確認の観点（realm 再インポート後）

- `minio`: admin の `policy` claim = `["consoleAdmin"]`（単一値）。`developer` は `policy` 無し＝deny-by-default。
- `wiki-js`/`headlamp`: admin の `groups` claim に `platform-admin`/`Administrators` 等を含む。
- Grafana/ArgoCD/Vault: 既存の realm ロール由来 claim が admin に付き、各ツールで管理者に解決される。
