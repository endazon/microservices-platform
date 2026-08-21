---
title: Vault＋ESO secret 供給 PR-4: 基盤 secret（postgres/rabbitmq/keycloak-admin）を ExternalSecret 化（Issue #310・最終）
type: spec
status: done
related_ids:
  - IADR-0077
  - IADR-0096
  - IADR-0097
  - IADR-0098
  - IADR-0099
author: claude
created: 2026-07-24
updated: 2026-07-24
related_specs:
  - "../adr/IADR-0099_vault-eso-secret-supply-pr4.md"
  - "../adr/IADR-0096_vault-eso-secret-supply-k8s-auth.md"
  - "../../deploy/local/vault/eso/README.md"
  - "../../scripts/k8s-local-up.sh"
  - "../../scripts/k8s-local-up.test.js"
---

# 仕様書: Vault＋ESO secret 供給 PR-4（Issue #310・最終）

## 起点となる計画書（トレーサビリティ）

- 計画根拠: Vault（秘匿管理）の採用は `planning/.../06_technical/03_tech-stack-selection.md`（L42/L54）。Vault 専用の計画 ADR は無し。
  ESO 基盤・k8s auth・段階移行は [IADR-0096](../adr/IADR-0096_vault-eso-secret-supply-k8s-auth.md)（PR-1）〜[IADR-0098](../adr/IADR-0098_vault-eso-secret-supply-pr3.md)（PR-3）。opt-in 統括は [IADR-0077](../adr/IADR-0077_local-observability-vault-gitops-overlays.md)。
- 決定: 本作業の設計判断は [IADR-0099](../adr/IADR-0099_vault-eso-secret-supply-pr4.md)（基盤 secret 特有の bootstrap 順序・パスワード整合・`creationPolicy: Merge`）。
- Issue: #310（Vault/ESO 本番同等化）。develop 最新（PR-1〜3 反映済み）ベース。**本 PR で secret 移行は一巡**。

## 背景と問題

基盤 secret `postgres`・`rabbitmq`・`keycloak-admin`（各キー `password`）は他区分と決定的に異なる:
1. step [4/7] infra rollout（ブロッキング）で **非 optional** に消費される → ESO ブロック（後段）で skip すると infra 起動不能。
2. DB/broker/keycloak は既存パスワードで初期化済み → 供給値が既存とズレると認証破壊。

## 受け入れ基準（PR-4）

1. `ESO=1` で 3 secret を Vault `secret/msp/<name>` → **既存 Secret 名・同一キー `password`**（消費側 `secretKeyRef` 不変・
   platform-infra ns）へ ExternalSecret 供給する。**`creationPolicy: Merge`**（既存 Secret にマージのみ・所有/再作成しない）。
2. **手動 apply は保持**（`ESO=1` でも step 3 の `apply_secret` をスキップしない）＝ bootstrap 順序を壊さない。
   これは PR-1〜3（Owner＋skip）と意図的に異なる。
3. **seed 値は手動 apply と完全一致**（`PG_PASSWORD:-postgres`／`RABBITMQ_PASSWORD:-guest`／`KEYCLOAK_ADMIN_PASSWORD:-admin`）。
   `bootstrap.sh` は同一プロセス環境を継承するため常に同値＝Merge は no-op（値不変・Pod 再起動/PVC 不整合なし）。平文非コミット。
4. **policy 充足の自己チェック**: 3 secret は `secret/msp/*` 配下＝PR-1 の `eso-read` でカバー済み（追加不要・AST path 無改変）。
5. **`VAULT=1` 単独破壊なし／バイト等価**: `ESO` 未設定は ESO ブロック不実行＝手動 apply のみ（従来どおり）。store 上書きは `ESO=1` のみ。
6. **本番/既存無改変**: 本番 `values.yaml`/chart・消費側 `secretKeyRef`・realm は無改変。
7. CI 緑: `k8s-local-up.test.js`（`ESO=1` で 3 ExternalSecret＋手動 apply 保持／既定で 3 手動 apply・ES 無）・`doc-links`・
   `check-image-mapping`(#275)・gitleaks。

## 対応方針（変更範囲・PR-4）

- **`deploy/local/vault/eso/externalsecret-{postgres,rabbitmq,keycloak-admin}.yaml`（新）**: `creationPolicy: Merge`・platform-infra ns・キー `password`。
- **`deploy/local/vault/eso/bootstrap.sh`**: 3 基盤 secret の seed（step 3 と同 env/既定・平文非コミット）を追加＋完了メッセージ。
- **`scripts/k8s-local-up.sh`**: step 3 の手動 apply は**無改変（保持）**。ESO ブロックで 3 ExternalSecret を apply＋確認コマンド/メッセージ更新。
- **回帰（TDD）**: `k8s-local-up.test.js` に (a) `ESO=1`=3 ExternalSecret apply＋基盤手動 apply 保持、(b) 既定=3 手動 apply＋ES 無。
- **docs**: `eso/README.md`（対象一覧・基盤特有の挙動・切り戻し・段階移行）／IADR-0099＋索引。

## リスクと自己チェック（基盤特有）

- **sync 順序**: ESO の初回 sync は infra Ready 後だが、基盤 Secret は手動 apply 済みで既に存在するため infra 起動には影響しない
  （ESO は同一値を Merge するだけ）。dependent サービスの再作成起点にならない。
- **パスワード整合**: seed=手動 apply=同一 env/既定のため値一致＝Merge no-op。`PERSIST=1` の PVC 初期化済み DB でも不整合なし。
- **fail-safe**: ESO 同期失敗時も手動 apply 済みで infra は起動（PR-1〜3 より強い fail-safe）。

## 非対象・除外

- 除外: `vault-dev-token`（root・chicken-egg）／`argocd-secret`（merge patch）／AST secrets（AST リポ管轄）。
- **これで #310 の secret 移行は一巡（PR-1〜4）**。以降は運用（実 secret を Vault へ seed する runbook）に委ねる。

## 検証

- `node scripts/k8s-local-up.test.js` / `node scripts/check-doc-links.js` / `node scripts/check-image-mapping.js`
- `bash -n scripts/k8s-local-up.sh` / `bash -n deploy/local/vault/eso/bootstrap.sh` / gitleaks（平文 secret なし）
