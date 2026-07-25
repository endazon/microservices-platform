---
title: IADR-0104 dev SSO 管理者 identity とツール別 claim mapper を realm.json に恒久化する（再インポート自動復旧）
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0004
  - IADR-0090
  - IADR-0092
  - IADR-0093
  - IADR-0094
  - IADR-0095
  - IADR-0103
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0004_authz-abac.md (認可＝ABAC。認証は Keycloak に一元化)"
  - "../../planning/projects/microservices-platform/02_requirements/ (NFR 運用性＝再構築の再現性)"
author: claude
created: 2026-07-25
updated: 2026-07-25
---

# IADR-0104: dev SSO 管理者 identity とツール別 claim mapper を realm.json に恒久化する

- 状態: Accepted
- 日付: 2026-07-25
- 決定者: claude（実装）

## 背景

`#353` 系（経路B ローカルツールの Keycloak SSO 一括連携）で Grafana/ArgoCD/MinIO/Vault/Wiki.js を SSO 化したが、
実際に立ち上げると管理者ログインの成立に必要な設定（admin ユーザー・MinIO の単一値 `policy` claim・Wiki.js の
`Administrators` 名前一致）が **realm 再インポート/Pod 再起動で揮発する live 操作**だった。この claim 設計の背景・
却下案・ツール別の理由づけは [[IADR-0103]] に記録済みである。

本 ADR は、そのうち **realm へ恒久定義できる部分（admin identity ＋ ツール別 claim mapper）だけ**を、
レビュー単位を絞って `deploy/keycloak/microservices-platform-realm.json` へ**追加のみ**で焼き込む決定を記録する。
ESO 供給後の rollout・argocd DNS エイリアス・Vault の listing-visibility といった **realm 外の live 操作**は本 ADR の
対象外（別 PR＝#389 系）とし、両者を独立にレビュー・マージできるようにする。

## 決定

`deploy/keycloak/microservices-platform-realm.json` に以下を**追加のみ**で恒久化する（既存 client・既存ユーザー
`developer`/`poc-*`・既存 mapper は無改変）。realm を再インポートすれば admin ログインが自動復元される。

| 対象 | 追加内容 | claim 形と理由 |
| --- | --- | --- |
| `admin` ユーザー（新規） | enabled・emailVerified・dev パスワード `admin`（非一時）。realm ロール `platform-admin`/`platform-operator`/`wiki-editor`/`Administrators`、グループ `/clearance/restricted`・`/department/engineering`、client ロール `minio:consoleAdmin` | この 1 ユーザーで Grafana=Admin / ArgoCD=role:admin / Vault=admin policy / Wiki.js=Administrators / MinIO=consoleAdmin に解決される |
| realm ロール `Administrators`（新規） | Wiki.js の管理者グループ名に文字列一致させる dev ロール | Wiki.js は自前グループ管理で、グループ名の文字列一致が唯一の接点 |
| `minio` client ロール `consoleAdmin`（新規） | MinIO 組込ポリシー名に一致する client ロール | client ロールに閉じることで `policy` claim を単一値に保つ（realm ロール多値だと MinIO がポリシー解決に失敗し callback 500） |
| `minio` client mapper `minio-client-roles` | `oidc-usermodel-client-role-mapper`（`usermodel.clientRoleMapping.clientId=minio`・claim `policy`・multivalued）。旧 realm ロール多値 mapper `minio-realm-roles` は**削除**（差し替え） | 多値だと MinIO が存在しないポリシー名を解決できず 500。client ロール由来なら単一値化する |
| `wiki-js` client mapper `wikijs-realm-roles` | `oidc-usermodel-realm-role-mapper`（claim `groups`・multivalued） | Wiki.js の Map Groups が `groups` claim を見る（他ツールと同型） |
| `headlamp` client mapper `headlamp-realm-roles` | 同型（claim `groups`・multivalued） | 現行 k8s では inert だが HTTPS 化（#388）でそのまま使える |

- **deny-by-default の副作用**（[[IADR-0103]] と同旨）: `minio` を client ロール mapper に閉じた結果、client ロール
  未付与のユーザー（`developer` 等）は `policy` claim が付かず MinIO にログインできない。これは意図した挙動。
- **`policy` 単一値は運用前提に依存する**: mapper 自体は `multivalued=true` のため、1 ユーザーに `minio` client
  ロールを複数付与すると `policy` claim が多値化し callback 500 が再発する。本 realm では `admin.clientRoles.minio`
  を 1 要素に保つ。この不変条件の機械検知（回帰テスト）は別 PR（#389 系）の `scripts/k8s-local-up.test.js` にある。
- redirect URI（`headlamp`/`spa-web` の集約後 URL）は #377（マージ済）で恒久化済みのため本 ADR では触らない。

## 反映確認（realm 再インポート後）

admin が各ツールで管理者に解決される claim を持つこと:

- `minio`: `policy` = `["consoleAdmin"]`（単一値。`developer` は `policy` 無し＝deny-by-default）
- `wiki-js`/`headlamp`: `groups` に `platform-admin`/`platform-operator`/`wiki-editor`/`Administrators` を含む
- Grafana/ArgoCD/Vault: 既存の realm ロール由来 claim（`roles`/`groups`）が admin に付く

静的検査: `node scripts/check-realm-constraints.js`（全フィールド ≤255 文字＝SQLSTATE 22001 回避）と JSON 妥当性。

## 影響・非対象

- **dev 専用**（`deploy/keycloak` の dev realm のみ）。本番 chart（`deploy/helm`）・compose・アプリコードは無改変。
- realm の既存ユーザー（`developer`/`poc-*`）・既存 client・既存 mapper は無改変（**追加のみ**）。
- **平文の本番 secret を入れない**（admin は dev 既定パスワードのみ）。
- **realm 外の live 操作は本 ADR の対象外**: ESO 供給後 rollout・argocd DNS エイリアス・Vault listing-visibility・
  Wiki.js DB seed・up-script は別 PR（#389 系 / [[IADR-0103]]）で扱う。
- 取引/実弾には無関係（不変）。

## 却下した代替案

- **realm.json を含む恒久化を単一 PR（#389）へ同梱する**: realm 追加と realm 外の live 操作（rollout・DNS・
  Vault tune）が 1 PR に混在しレビュー単位が大きくなる。dev realm への追加は副作用が realm 内に閉じるため、
  独立にレビュー・再インポート検証できるよう切り出す。
- **admin を realm に置かず up-script で毎回 live 作成する**: 再インポートで揮発し「自動復旧」にならない（本 ADR の
  目的そのものを満たさない）。ツール別 claim 設計の却下案は [[IADR-0103]] を参照。
