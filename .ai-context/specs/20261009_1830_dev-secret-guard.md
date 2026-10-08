---
title: 作業仕様書 — dev 以外のクラスタで、管理権限を持つ機密クライアントの secret が公知の dev の値で作られるのを止め、稼働中の dev の値を検知する（#1830）
type: spec
status: done
related_ids: [NFR-18, ADR-0124, ADR-0123, IADR-0517, IADR-0516, IADR-0485, IADR-0369, IADR-0404, IADR-0329]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0124_paired-secrets-outside-sc22-and-vault-audit-for-fallback.md 決定 1（対になる秘密・初期投入は無いときだけ作る・本番の秘密を realm の宣言から外す）
  - planning:projects/microservices-platform/07_adr/ADR-0123_mcp-service-account-attributes-source-of-truth-is-idp.md 決定 2（管理用の機密クライアント `mcp-client-admin`）
issue: "#1830"
---

# 作業仕様書 — dev の値の機密クライアントを dev 以外のクラスタで作らせない（#1830）

> 本仕様書は実装着手前に作成した（着手 2026-10-09）。判断の記録は **IADR-0517**（新規）に置く。
> 計画は project-planning `origin/main`（`82be7dc`）の隣接クローン（読み取り専用）で読んだ。基点は MSP `origin/develop` `2f5d9c04`。
> 🔴 **稼働中のクラスタ・Keycloak には何も実行しない。** 試験はすべて PATH のスタブ・偽の Keycloak の下で行う。

## 起点となる計画書（トレーサビリティ）

- 非機能要求: **NFR-18**（シークレット管理）。
- 計画 ADR: **ADR-0124 決定 1**（対になる秘密は相手と対で書く。初期投入は無いときだけ作る）・**ADR-0123 決定 2**（`mcp-client-admin`）。
- 起点 issue: **#1830**（PR #1827〔#1817〕の独立監査 🟡4 から分離）。利用者裁定 2026-10-09（issue コメント）。
- 実装 ADR: **IADR-0517**（本作業で新設）。前提 **IADR-0485**（realm の client `secret` は作成時にだけ運ぶ・Vault の種は無いときだけ作る）・**IADR-0516**（`mcp-client-admin`）・**IADR-0404**（`reset-gate`）・**IADR-0329**（`identity-admin`）・**IADR-0369**（realm の後追い Job）。

## 利用者裁定（2026-10-09。issue #1830 のコメント）

1. **判定の入口は kube context の許可集合。** `k3d-*` / `rancher-desktop` / `docker-desktop` / `kind-*` を dev とみなし、従来どおり dev の値で起動する。それ以外の context で、レルム管理のロールを持つ 3 クライアント（`identity-admin`・`reset-gate`・`mcp-client-admin`）の `*_CLIENT_SECRET` が未設定（＝ dev の値で作る）なら、理由を名指しして非 0 で止まる。明示の上書き `ALLOW_DEV_CLIENT_SECRETS=1` で通す（警告を出す）。
2. **稼働中の検知（AC3）は `reconcile-realm.js --check-dev-secrets`。** 3 クライアントの secret が宣言の dev の値と一致したら非 0 で名指しする。読むだけ。運用手順書から呼ぶ。

## 受け入れ基準 → 試験

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| AC1 | dev 以外の context で `k8s-local-up.sh` / `bootstrap.sh` が 3 クライアントの secret を dev の値で作ろうとする → 止まる（非 0・名指し） | `k8s-local-up.test.js` #1830 節（真理値表・起動器の実走で書き込み前に止まる）・`scripts.repo.test.js` #1830 節（bootstrap の実走）・`keycloak-realm-reconcile.test.js` #1830 節（後追いが作らない） |
| AC2 | 否定形: dev のクラスタ（k3d / Rancher Desktop・integration-stack・cutover-rehearsal）は従来どおり | `k8s-local-up.test.js`（既定の実走は `k3d-testcluster` で緑のまま・2 本のワークフローの context 名が許可集合に入る） |
| AC3 | 稼働中の dev の値を検知する手段 | `keycloak-realm-reconcile.test.js` #1830 節（偽の Keycloak。dev の値なら非 0 で名指し・値を出力しない・書かない）・`k8s-local-up.test.js`（`reconcile-realm.sh --check-dev-secrets` が Job のモードを差し替える） |
| AC4 | 判定の真理値表 | `k8s-local-up.test.js` #1830 節（context × env 未設定/dev の値/別の値 × 上書きの全組） |

## 設計（IADR-0517 に記録）

- **判定は純関数 1 本**（`scripts/lib/dev-client-secret-guard.sh` の `dev_client_secret_decide <context> <override> <client>=<value>...`）。kube context は呼び出し側が `kubectl config current-context` で読む（`KUBECONFIG` は kubectl がそのまま尊重する。3 本の呼び出し元はどれも `--context` を使わない＝実測）。読めないときは空 ＝ dev ではない（安全側）。
- **適用点は 3 つ**:
  - `scripts/k8s-local-up.sh`: `[1/7]` の直後（context が確定し、Secret をまだ 1 つも書いていない位置）。`reset-gate` は常に、`identity-admin`・`mcp-client-admin` は `ESO=1` でないとき（`ESO=1` では bootstrap が作る）。
  - `deploy/local/vault/eso/bootstrap.sh`: Vault への最初の書き込みより前。対象は **KV がまだ無いもの**だけ（在る KV は触らないので dev の値は入らない。IADR-0485）。
  - `deploy/local/keycloak-setup/reconcile-realm.js`: Job の中で動き context を持たない。**ホスト側の入口 `reconcile-realm.sh` が同じ判定器で決め、Job の env `DEV_CLIENT_SECRETS_ALLOWED`（`allow` / `deny`）として渡す**（判定の単一情報源は shell の判定器）。マニフェストの既定は `deny`（直接 apply しても安全側）。`deny` で 3 クライアントのどれかを宣言の secret で作る操作（`client.create`・`realm.create`）があれば、その realm には何も書かずに名指しして非 0。
- **検知**: `reconcile-realm.js --check-dev-secrets`（Job では `RECONCILE_MODE=check-dev-secrets`。ホストの入口は `reconcile-realm.sh --check-dev-secrets`）。`GET …/clients?clientId=` と `GET …/clients/<id>/client-secret` だけを打ち、宣言の値と一致するものを名指して非 0。値は出力しない。

## 現状（実測。`2f5d9c04`）

| 事実 | 確かめ方 |
| --- | --- |
| `k8s-local-up.sh` は `reset-gate-oidc` を ESO の有無によらず、`identity-admin-oidc`・`mcp-client-admin-oidc` を `ESO!=1` で、env 未設定なら dev の値で作る | `scripts/k8s-local-up.sh:269`・`:402`・`:413` |
| `bootstrap.sh` は 3 つの KV を無いときだけ env か dev の値で作る | `deploy/local/vault/eso/bootstrap.sh:197`・`:202`・`:239` |
| 後追い Job は無い client と無い realm を宣言（secret を含む）で作る | `reconcile-realm.js` の `plan()`（`client.create`・`realm.create`） |
| 3 本のスクリプトはどれも素の `kubectl`（`--context` なし）で current context へ書く | `grep -n -- '--context' scripts/k8s-local-up.sh deploy/local/vault/eso/bootstrap.sh deploy/local/keycloak-setup/reconcile-realm.sh` が 0 件 |
| k3d は `k3d-<cluster>` の context を作る。CI の 2 本は `CLUSTER=integration-stack` / `cutover-rehearsal`・`K8S_LOCAL_RUNTIME=k3d` | `.github/workflows/integration-stack.yml:63`・`cutover-rehearsal.yml:50` |
| 既存の試験スタブ（`k8s-local-up.test.js` の kubectl・`scripts.repo.test.js` #1728 の kubectl）は `config current-context` に何も返さない | 両ファイル（本作業で `STUB_KUBE_CONTEXT` を足す） |

## 母集合（規則 9・10）

### 規則 9 — dev の値と `*_CLIENT_SECRET` の既定が流れる箇所を全数走査した

`git grep -n -e 'identity-admin-dev-secret' -e 'reset-gate-dev-secret' -e 'mcp-client-admin-dev-secret' -e 'IDENTITY_ADMIN_CLIENT_SECRET' -e 'RESET_GATE_CLIENT_SECRET' -e 'MCP_CLIENT_ADMIN_CLIENT_SECRET' -e 'identity-admin-oidc' -e 'reset-gate-oidc' -e 'mcp-client-admin-oidc' -- ':!.ai-context/specs' ':!CHANGELOG.md'`（26 ファイル）と、全クライアントの dev の値 `git grep -n -- '-dev-secret-change-me'`（シェル・JS・helm・ワークフローを含む）:

| 箇所 | 値の流れ | 扱い | 理由 |
| --- | --- | --- | --- |
| `scripts/k8s-local-up.sh`（`reset-gate-oidc`・`identity-admin-oidc`・`mcp-client-admin-oidc`） | env か dev の値 → k8s Secret | **守る** | 手動経路（裁定 1） |
| `deploy/local/vault/eso/bootstrap.sh`（3 つの KV） | env か dev の値 → Vault（無いときだけ） | **守る**（無い KV だけ） | Vault の初期投入（裁定 1） |
| `deploy/local/keycloak-setup/reconcile-realm.js`（`client.create`・`realm.create`） | realm の宣言 → Keycloak | **守る**（Job の env） | 後追いが無い client を作る（issue 背景） |
| `deploy/local/keycloak-setup/reconcile-realm.sh`・`realm-reconcile-job.yaml` | 判定 → Job の env | **直す**（判定を渡す口） | Job は context を持たない |
| `deploy/keycloak/microservices-platform-realm.json`（宣言の `secret`） | 宣言 → Keycloak の `--import-realm`（空の PVC の初回起動） | **止めない・検知で拾う**（残余） | Keycloak 本体の import は起動器の外。dev の形に限る検査 8 は既存。稼働の値は `--check-dev-secrets` が拾う |
| `deploy/docker-compose.yml`（`identity-admin`・`mcp-client-admin` の既定） | env か dev の値 → コンテナの env | **対象外** | compose は kube context を持たない手元専用の経路（Keycloak も同じ compose の dev の realm）。共有クラスタへの経路ではない |
| `deploy/helm/microservices-platform/values.yaml`・`deploy/mail-relay/reset-gate.yaml`・`deploy/local/vault/eso/externalsecret-*-oidc.yaml` | Secret 名だけ（既定値なし） | **対象外** | 値が流れない |
| `.github/workflows/*.yml` | `*_CLIENT_SECRET` を設定しない・k3d で起動 | **対象外**（陽性対照） | context は `k3d-integration-stack` / `k3d-cutover-rehearsal` で許可集合に入る（試験で固定） |
| `scripts/check-mcp-client-provisioning.js` | Secret を kubectl で読むだけ | **対象外** | 作らない |
| 他クライアント（`bff`・`*-service`・`wiki-js`・道具の OIDC・`synthetic-monitor`・AST の 3 つ） | 同じ形で dev の値 | **対象外** | レルム管理のロールを持たない（裁定 1 は 3 クライアントに限る）。IADR-0517 の残余に書く |
| `src/.../BffSessionOptions.cs`・`ServiceAccountProvisioningRegistration.cs` | コメントだけ | **対象外** | 値が流れない |
| `docs/security/security.md`（本番流用の禁止の項）・`docs/operations/paired-secret-rotation-runbook.md`（`mcp-client-admin` の節） | 暫定の記述（PR #1827）「機械の守りは無い」 | **直す** | 守りと検知が入る |

### 規則 10 — この変更で新たに誤りになる記述

`git grep -n -e '機械の守りは起動器に無い' -e '後続の作業で入れる' -e 'client の secret は読みに行かない' -e "RECONCILE_MODE      apply（既定）| check" -- ':!.ai-context/specs'`:

| 箇所 | 扱い |
| --- | --- |
| `docs/security/security.md`・`paired-secret-rotation-runbook.md` の「機械の守りは起動器に無い（後続の作業で入れる）」 | **直す**（守りと検知の手順へ） |
| `reconcile-realm.js` 冒頭「既存の client の `secret` は … 読みにも行かない」・環境変数の一覧 | **直す**（`check-dev-secrets` だけは読む。比べるだけで出さない） |
| `reconcile-realm.sh` 冒頭の使い方（`--check` だけ） | **直す** |
| `k8s-local-up.sh` 冒頭「fail-safe: 機密は未設定なら dev 既定」 | **直す**（dev 以外の context では 3 クライアントは止まる） |
| `keycloak-realm-reconcile.test.js` の #1682「collectLive は client-secret を読まない」 | **そのまま**（`collectLive` は今も読まない。読むのは別関数 `collectGuardedSecrets` だけ） |

## 変更するファイル

- 新規: `scripts/lib/dev-client-secret-guard.sh`・`.ai-context/adr/IADR-0517_dev-client-secret-guard-kube-context-allowlist.md`・本仕様書。
- 変更: `scripts/k8s-local-up.sh`・`deploy/local/vault/eso/bootstrap.sh`・`deploy/local/keycloak-setup/{reconcile-realm.js,reconcile-realm.sh,realm-reconcile-job.yaml,README.md}`・`scripts/k8s-local-up.test.js`・`scripts/scripts.repo.test.js`・`scripts/keycloak-realm-reconcile.test.js`・`scripts/live-scripts.json`（新しい判定器を「source される関数定義」として分類。#1550 の閉包検査）・`docs/security/security.md`・`docs/operations/paired-secret-rotation-runbook.md`・`.ai-context/adr/README.md`。
- 既存の試験の追随: `scripts.repo.test.js` の #1817 節「dev 以外では起動の直後に回す」の表明を、新しい文言（検知の手順・上書き・許可集合があり、暫定の「機械の守りは無い」が無い）へ差し替えた。既存の 2 つの kubectl スタブへ `config current-context` の応答（既定は k3d の形）を足した。

## 検証の計画

- `node scripts/k8s-local-up.test.js`・`node scripts/keycloak-realm-reconcile.test.js`・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・`node scripts/reset-gate.test.js`。
- 変異: 判定器の許可集合へ `*` を足す／`ALLOW_DEV_CLIENT_SECRETS` を常に真にする／後追いの守りを外す → 試験が赤くなることを確かめてから戻す。
- `shellcheck`（あれば）・文書の検査器一式（trace ブロック・IADR 採番・updated・リンク・知識グラフ・deploy マニフェスト）。

## 残余

- Keycloak 本体の `--import-realm`（空の PVC の初回起動）は宣言の dev の値で 3 クライアントを作る。起動器はこれを止めない。dev 以外のクラスタでは起動の直後に `--check-dev-secrets` で検知し、回す（手順書に書く）。
- context 名だけで判定する。共有クラスタの context を `k3d-*` 等の名前にすれば dev とみなされる（名前を付ける人の責任。IADR-0517 に書く）。

## 検証の結果（2026-10-09）

| 実行 | 結果 |
| --- | --- |
| `node scripts/k8s-local-up.test.js` | 277 件 合格（既存 265 ＋ #1830 の 12。真理値表は context 20 種 × 値 3 × 上書き 4 ＝ 240 ケース） |
| `node scripts/keycloak-realm-reconcile.test.js` | 45 件 合格（#1830 の 7 を含む。偽の Keycloak を HTTP で立てて main まで通す） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 1016 件 合格（#1830 の bootstrap 実走 3 を含む） |
| `node scripts/reset-gate.test.js` | 12 件 合格 |
| `shellcheck -x`（4 ファイル） | 新規の指摘 0（既存の SC1091 info 3 件は変更前と同じ） |
| 文書・配備の検査器 | trace-blocks・adr-numbering・doc-updated・cross-repo-refs・plan-id-qualification・reading-budget・doc-links・test-spec-coverage・test-traceability・knowledge-graph・realm-constraints・image-digests が OK。deploy-manifests は helm / kubectl / kubeconform が無く検証を飛ばした（CI が見る） |

### 変異（壊す → 赤 → 戻す）

| 変異 | 結果 |
| --- | --- |
| M1: 許可集合を `*)`（何でも dev）にする | `k8s-local-up.test.js` 赤（真理値表 `ctx="" … 判定が違う（dev）`） |
| M2: 判定器の終了コードを常に 0（上書きと同じ）にする | `k8s-local-up.test.js` 赤（真理値表の終了コード）。`scripts.test.js` は緑 —— `dev_client_secret_guard` が判定の語（deny）でも止めるので、終了コードだけの破れでは止まりが外れない（二重の確認。設計どおり） |
| M3: 後追いの `devSecretsAllowed` を常に真にする | `keycloak-realm-reconcile.test.js` 赤（`拒否のはずが書いた: POST /admin/realms`） |
| M4: `k8s-local-up.sh` の判定の呼び出しを外す | `k8s-local-up.test.js` 赤（`prod-shared: 止まらなかった`） |
| M5: check モードの dev の値の比較を外す | `keycloak-realm-reconcile.test.js` 赤（`devSecretFindings` の表） |

いずれも戻した後に全件緑であることを上の表で確かめた。

