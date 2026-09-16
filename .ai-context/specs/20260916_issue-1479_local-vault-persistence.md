---
title: "経路B の Vault を file ストレージ＋PVC で永続化し、Pod 内ラッパーで init / unseal / 固定 root トークンを自動化する（#1479）"
type: spec
status: in-progress
related_ids: [NFR-18, SC-22, ADR-0095, IADR-0077, IADR-0094, IADR-0096, IADR-0369, IADR-0456, IADR-0457]
author: claude
created: 2026-09-16
updated: 2026-09-16
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-18
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md
---

# 仕様書: 経路B の Vault を永続化する（#1479）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 非機能要件（NFR）: `NFR-18`（シークレット管理）
- 画面（SC）: `SC-22`（秘密情報・接続設定の管理。画面が書く先が本 Vault）
- 関連 ADR: `ADR-0095`（秘密の投入面は製品の画面）
- 関連 IADR: `IADR-0077`（dev Vault の opt-in）／`IADR-0094`（OIDC bootstrap）／`IADR-0096`（ESO の k8s auth）／`IADR-0369`（永続化は既定オン・`PERSIST=0` で opt-out）／`IADR-0456`（seed-if-absent）／**`IADR-0457`（本作業で起こす。事前割り当て）**
- 起票: #1479。利用者裁定 2026-09-16: 案 A（file ストレージ＋PVC・Pod 内ラッパー・既定オン）で進める

## 目的・背景

`deploy/local/vault/vault-dev.yaml` は `vault server -dev`（`storage_type: inmem`）で、Pod 再起動で全状態（k8s auth・policy・role・KV・OIDC）が消える。2026-09-16 18:17 の k3s 再起動で `ClusterSecretStore vault-backend` が `InvalidProviderConfig`、全 ExternalSecret が `SecretSyncedError` になった。SC-22 で入れた値も消え、再 seed（空文字）で `ast-secrets` が空に置き換わり AST 連携が無言で止まる。

## 決定の要点（IADR-0457 に記録）

1. **非 dev モード＋ `file` ストレージを PVC `vault-data`（`local-path`・1Gi）に置く。** 単一 Pod なので `raft` は要らない。
2. **Pod 内ラッパー `vault-entrypoint.sh`（ConfigMap）が起動時に行う**: サーバ起動 → API 到達待ち → 未初期化なら `operator init -key-shares=1 -key-threshold=1` の出力を PVC 上の `0600` ファイルへ保存 → 毎回 unseal → 固定 root トークン（Secret `vault-dev-token` の値＝`VAULT_DEV_ROOT_TOKEN_ID`）が無ければ root policy の orphan トークンとして作成 → `secret/` に kv-v2 が無ければ mount → サーバを待つ（SIGTERM を転送）。
   **これにより `bootstrap.sh`（`$VAULT_DEV_ROOT_TOKEN_ID` で `kubectl exec`）・ESO store（token 認証版）・OIDC bootstrap・BFF の k8s auth は無改変で動く**（Vault 1.16 の使い捨てコンテナで実測: 固定 ID のトークン作成・kv-v2 mount・再起動後に同じトークンで KV を読めること）。
3. **配線は kustomize オーバーレイ `deploy/local/vault-persistence/`**（`../vault` ＋ PVC ＋ ConfigMap ×2 ＋ Deployment patch）。`k8s-local-up.sh` は既存の `PERSIST` ゲートに従い**既定でこれを apply**し、`PERSIST=0` は従来の `deploy/local/vault`（`-dev`・バイト等価）。ESO CRD 不在のフォールバック（`vault-dev.yaml` だけ）は従来どおり非永続で、WARN にその旨を書く。
4. **readinessProbe は `vault status`（unseal 済みで 0）**。`k8s-local-up.sh` は apply の直後に `rollout status deploy/vault` で待つ（unseal 前に `kubectl exec` する bootstrap が "sealed" で落ちないように）。
5. **セキュリティの線引き**: unseal 鍵と初期 root トークンを PVC 上の平文ファイルに置く。root トークンが既知の dev 既定（`devroot`）である現状と守りの水準は同じ（ローカル dev 専用・本番充足ではない）。k8s Secret に置く案（B）は Pod 内に kubectl が無く Job が要り、鍵も PVC も同じローカルディスク上で差が無いため不採用。

## 変更の範囲

| 箇所 | 扱い |
| --- | --- |
| `deploy/local/vault-persistence/kustomization.yaml` `pvc.yaml` `local.hcl` `vault-entrypoint.sh` `deployment-patch.yaml` | **新規** |
| `deploy/local/vault-persistence/vault-entrypoint.test.sh` | **新規**（`vault` スタブで init / unseal / 固定トークン / kv mount の分岐を固定。`scripts.repo.test.js` から bash で起動） |
| `scripts/k8s-local-up.sh` VAULT ブロック | **変更**: `PERSIST` で apply 先を切替、`rollout status deploy/vault` を追加、CRD 不在 WARN に非永続の注記 |
| `scripts/k8s-local-up.test.js` | **変更**: 既定で `vault-persistence`／`PERSIST=0` で `deploy/local/vault`／rollout 待ち／`OPTIN_TOKENS` に追加 |
| `.ai-context/adr/IADR-0457` ＋ 索引 | **新規** |
| `docs/operations/local-sso-recovery-runbook.md` | 揮発マトリクス（Vault の行）と STEP 2 の条件を改める |
| `deploy/local/vault/README.md` `eso/README.md` `oidc/README.md`、`eso/bootstrap.sh` `oidc/bootstrap.sh` のヘッダ、`docs/operations/secret-item-console-injection-runbook.md`、`docs/security/security.md` | 「インメモリ・再起動で揮発」の記述を改める |
| `deploy/local/vault/vault-dev.yaml` `clustersecretstore.yaml` | **据え置き**（`PERSIST=0` のバイト等価を保つ。base としてオーバーレイが参照する） |
| `scripts/k8s-local-down.sh` | **据え置き**（`platform-infra` 名前空間の削除で PVC も消える） |

母集合の走査（規則 1〜10）: 誤りの側の語で引いた。`インメモリ` / `inmem` / `揮発` / `再起動` × `vault` で 12 行（`deploy/local/vault/README.md:9-10`・`eso/README.md:64`・`oidc/README.md:6,18`・`eso/bootstrap.sh:3`・`oidc/bootstrap.sh:6`・`vault-dev.yaml:2`・`local-sso-recovery-runbook.md:27,33,88`・`secret-item-console-injection-runbook.md:77,225`・`security.md:255`）。**除外**: `k8s-local-up.sh:709`（Vault の再起動ではなく DB/broker の rollout の話）、`keycloak-smtp-relay-setup-runbook.md:81,84`（MTA の再起動）、`security.md:75`（InMemoryVectorStore）。凍結記録（`.ai-context/specs/` の既存）は書き換えない。

## 受け入れ基準

| # | 基準 | 検証 |
| --- | --- | --- |
| AC-1 | 既定（`VAULT=1`）で `deploy/local/vault-persistence` が apply され、`deploy/local/vault` 単独の apply は現れない。直後に `rollout status deploy/vault` を待つ | `k8s-local-up.test.js` |
| AC-2 | `PERSIST=0`（`VAULT=1`）で `deploy/local/vault` が apply され、`vault-persistence` は現れない（従来とバイト等価） | 同上 |
| AC-3 | CRD 不在のフォールバックは従来どおり `vault-dev.yaml` だけで、WARN に非永続の注記がある | 同上 |
| AC-4 | ラッパー: 未初期化なら init して 0600 で保存し unseal する／初期化済みなら init せず保存済みの鍵で unseal する／固定トークンは無いときだけ作る／kv-v2 は無いときだけ mount する／サーバへ SIGTERM を転送する | `vault-entrypoint.test.sh`（`vault` スタブ） |
| AC-5 | 稼働クラスタ: `VAULT=1 ESO=1` で立てた後に vault Pod を削除しても、`k8s-local-up.sh` を再実行せずに store が `Valid`、ExternalSecret が `Ready`、SC-22 で書いた値の版が残る | 実測（本仕様書に記録） |
| AC-6 | `k8s-local-up.sh` の再実行が冪等（init 済みなら init しない・seed-if-absent は無害） | 実測 |
| AC-7 | `helm`/kustomize の描画: `kubectl kustomize deploy/local/vault-persistence` が通り、`PERSIST=0` 側の `deploy/local/vault` は不変 | `git diff` / `kubectl kustomize` |
| AC-8 | 文書検査（trace-blocks / knowledge-graph / doc-links / plan-id / cross-repo / reading-budget）緑、`check-commit-messages` 適合 | CI |

## 検証の証跡（2026-09-16）

- `node scripts/k8s-local-up.test.js`: 追加前は `どのゲートも発行しないトークン: deploy/local/vault-persistence` で赤 → 実装後 **180 tests passed**
- `bash deploy/local/vault-persistence/vault-entrypoint.test.sh`（Linux コンテナ）: 実装の終了コード採取を直す前 8 passed / 9 failed → **17 passed / 0 failed**
- `kubectl kustomize deploy/local/vault-persistence`: 描画成功（6 リソース＋ClusterSecretStore）。`git diff --quiet -- deploy/local/vault` → 無変更（`PERSIST=0` はバイト等価）
- 文書検査: trace-blocks / plan-id / doc-links / cross-repo / reading-budget / adr-numbering / doc-type / doc-status / doc-updated / knowledge-graph すべて OK
- **稼働クラスタでの実測（ClusterSecretStore を除いた描画を名前空間 `vault-persist-test` へ写して実施。稼働中の platform-infra の Vault は触っていない）**:
  - 初回起動: ラッパーのログ `not initialized: running operator init (1 share)` → `unsealed` → `fixed root token created` → `kv-v2 enabled at secret/` → `ready`。`vault status`: `initialized: true` / `sealed: false` / `storage_type: file`。`/vault/data/.local-dev-init` は `-rw-------`。固定トークンで `kv put secret/persist-probe/x` 成功
  - Pod 削除 → 再作成: `initialized (reusing /vault/data/.local-dev-init)` → `unsealed` → `fixed root token present` → `kv-v2 mounted at secret/` → `ready`。`sealed: false`。**`kv get` で書いた値が残っている**（AC-5・AC-6 の再起動側）
  - 名前空間は削除して片付けた
- 未実施: **platform-infra の稼働 Vault への切替**（`k8s-local-up.sh` の再実行）。稼働中の OpenD が画面ログインの途中で、Vault を作り直すと画面で入れた moomoo の資格情報と RSA 鍵が消えるため、PoC のログイン完了後に行う。切替時は OIDC（STEP 2）を 1 回だけ入れ直す
