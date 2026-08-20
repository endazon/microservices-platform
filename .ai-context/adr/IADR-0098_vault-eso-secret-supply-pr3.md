---
title: IADR-0098 Vault＋ESO secret 供給 PR-3 — OIDC client secret 群（minio/grafana/vault/headlamp-oidc）を ExternalSecret 化（IADR-0096/0097 の設計踏襲・段階移行）
type: impl-adr
status: Accepted
related_ids:
  - ADR-0006
  - IADR-0077
  - IADR-0096
  - IADR-0097
author: claude
created: 2026-07-24
updated: 2026-07-24
plan_refs:
  - planning:projects/microservices-platform/07_adr/ (ADR-0006 運用基盤)
---

# IADR-0098: Vault＋ESO secret 供給 PR-3（OIDC client secret 群）

- 状態: Accepted
- 日付: 2026-07-24
- 決定者: claude（実装）

## 起点・関連

- 関連 ADR: ADR-0006。ESO 基盤（k8s auth・store 上書き・policy `eso-read`・seed/skip・専用 SA・VAULT 併用ガード）は
  [IADR-0096](./IADR-0096_vault-eso-secret-supply-k8s-auth.md)（PR-1）。同パターンの 2 歩目は [IADR-0097](./IADR-0097_vault-eso-secret-supply-pr2.md)（PR-2）。opt-in オーバーレイは [IADR-0077](./IADR-0077_local-observability-vault-gitops-overlays.md)。
- 対象 OIDC secret の各機能連携: MinIO=[IADR-0093](./IADR-0093_minio-keycloak-oidc.md)、Grafana=[IADR-0090](./IADR-0090_grafana-keycloak-oidc-generic-oauth.md)、Vault=[IADR-0094](./IADR-0094_vault-keycloak-oidc.md)、Headlamp=[IADR-0080](./IADR-0080_headlamp-k8s-management-ui.md)。
- 仕様書: `docs/specs/20260724_issue-310_vault-eso-pr3-oidc-secrets.md`。
- Issue: #310（Vault/ESO 本番同等化）。develop 最新（PR-1/PR-2 反映済み）ベース。番号採番: PR-2=0097 の次の **0098**。

## コンテキストと課題

[IADR-0096](./IADR-0096_vault-eso-secret-supply-k8s-auth.md)（PR-1）で ESO 基盤を敷き、[IADR-0097](./IADR-0097_vault-eso-secret-supply-pr2.md)（PR-2）で minio/wikijs 系を移行した。PR-3 は同一パターンで
**OIDC client secret 群**＝`minio-oidc`・`grafana-oidc`・`vault-oidc`・`headlamp-oidc` を ExternalSecret 供給へ移行する。
PR-1/PR-2 の破壊系（`VAULT=1` 単独破壊・policy path 不足）を再発させないことが要件。

## 決定

### 1. PR-1/PR-2 の設計を機械的に踏襲する

各 secret に ExternalSecret（Vault `secret/msp/<name>`（KV v2）→ 既存 Secret 名・**同一キー `client-secret`**・
`creationPolicy: Owner`）を新設し、`bootstrap.sh` の seed に 4 secret を追加、`k8s-local-up.sh` で `ESO=1` 時は
手動 apply をスキップして ExternalSecret に委譲する。**既定（`ESO` 未設定）は手動 apply のままバイト等価**（fail-safe）。

### 2. namespace 跨ぎ（自己チェック）

`minio-oidc` は MSP ns（`microservices-platform`）、`grafana-oidc`／`vault-oidc`／`headlamp-oidc` は platform-infra ns
（各ツールと同居）。ExternalSecret は namespaced だが、参照する `ClusterSecretStore vault-backend` は cluster-scoped の
ため両 ns から同名 store を参照できる。各 ExternalSecret の `metadata.namespace` を対象 Secret と同じ ns にする。

### 3. 手動 apply の位置と skip（各機能ゲート内）

対象 4 secret は各機能ゲートで手動 apply される（`minio-oidc` は step 5 で常時・`grafana-oidc`=`OBSERVABILITY`・
`vault-oidc`=`VAULT`・`headlamp-oidc`=`HEADLAMP`）。それぞれを `if [ "${ESO:-}" != "1" ]` でくくり、`ESO=1` の
ときは ExternalSecret に委譲する。**ESO ブロック側の ExternalSecret apply も元の手動 apply のゲート意味論に整合させる**:
`minio-oidc` は常時（元も無条件）、`vault-oidc` は `VAULT` 前提（`ESO=1` は VAULT 併用ガード配下＝常に真）で常時、
`grafana-oidc`／`headlamp-oidc` は `OBSERVABILITY`／`HEADLAMP` が有効なときだけ apply する。これにより機能オフ時に
未使用 Secret を残さず、元の条件付き apply と対称になる。ESO ブロックは既存の VAULT 併用ガード配下にあるため、
`ESO=1` の実行は常に dev Vault 起動を前提とする（PR-1 のまま）。ESO 同期は Pod 起動後に走るため、対象 Secret は
一時的に未作成になりうるが、消費側はいずれも optional 参照（grafana=local admin フォールバック・minio=root
フォールバック・vault=runtime bootstrap が読む・headlamp=optional）で自己回復する（無改変）。

### 4. policy は追加不要（自己チェック）

4 secret は `secret/msp/*` 配下のため、PR-1 の policy `eso-read`（`secret/data/msp/*`＋`secret/metadata/msp/*` read）で
既にカバーされる。policy 追加は不要（PR-1 の 🔴 教訓「共有 store の policy path 不足で AST が 403」は本 PR では発生しない・
AST path も無改変）。

### 5. store・auth・SA・ガードは PR-1 のまま（無改変）

store は既定 token 認証のまま（`ESO=1` で k8s 認証版 `clustersecretstore-k8s.yaml` へ上書き＝PR-1）。専用 vault SA・
auth-delegator・`ESO=1` の VAULT 併用ガードも PR-1 のまま。本 PR は ExternalSecret／seed／手動 skip の追加のみで、
`VAULT=1` 単独の挙動・AST 連携・本番 values/chart・消費側 `secretKeyRef`・realm を一切変えない。

### 6. seed は平文非コミット（現行既定と同値）

seed 値は env 由来 or dev プレースホルダ（`{MINIO,GRAFANA,VAULT,HEADLAMP}_OIDC_CLIENT_SECRET`→`<tool>-dev-secret-change-me`）で、
現行 `apply_secret` の既定と同一。実 secret はリポジトリに置かない（gitleaks green）。realm import の dev client secret と
一致させることは既存の運用制約のまま（env 上書きで実値に合わせる）。

## 影響・トレードオフ

- `ESO=1` で OIDC client secret 群も Vault→ESO→Pod 自動供給になる。`ESO=1` かつ各機能ゲート未有効時は当該 Secret が
  未使用のまま生成されうる（無害・opt-in 経路のみ）。消費側は無改変。
- `VAULT=1` 単独（`ESO` 未設定）＝完全にバイト等価（手動 apply のまま）。段階移行の 3 歩目で、残りは PR-4（基盤）。

## 代替案

- **secret 別ファイル**: レビュー容易性のため secret 別 ExternalSecret ファイルにする（PR-1/PR-2 と同じ粒度）。
- **手動 apply と ExternalSecret を併存**: 二重所有で競合するため `ESO=1` 時は手動をスキップ（PR-1/PR-2 と同じ）。
- **grafana/headlamp-oidc を ESO ブロックで無条件 apply（一括）**: ExternalSecret は namespaced で副作用が無く optional
  参照のため実害はないが、機能オフ時に未使用 Secret が残る。**採用しない**：元の手動 apply が各機能ゲートで条件付き
  である以上、ESO 供給もゲート連動させる方が元の意味論と対称で、未使用 Secret を残さない（本 PR の決定 §3）。
