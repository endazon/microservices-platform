---
title: IADR-0457 経路B の Vault は file ストレージを PVC に置いて永続化し、Pod 内ラッパーが init / unseal / 固定 root トークン / kv-v2 mount を毎回行う（既定オン・PERSIST=0 で -dev）
type: impl-adr
status: Accepted
related_ids:
  - NFR-18
  - SC-22
  - ADR-0095
  - IADR-0077
  - IADR-0094
  - IADR-0096
  - IADR-0369
  - IADR-0456
author: claude
created: 2026-09-16
updated: 2026-09-16
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-18
  - planning:projects/microservices-platform/07_adr/ADR-0095_secret-input-face-is-the-product-screen.md
---

# IADR-0457: 経路B の Vault を file ストレージ＋PVC で永続化し、Pod 内ラッパーで自動復帰させる（#1479）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-16
- 決定者: endazon（利用者裁定 2026-09-16・案 A）/ Claude Code（起案）

## 起点・関連

- issue: #1479（経路B の Vault がインメモリで、クラスタ再起動のたびに SC-22 で入れた秘密と ESO の設定が消える）
- 作業仕様書: `.ai-context/specs/20260916_issue-1479_local-vault-persistence.md`
- 関連 IADR: [IADR-0077](IADR-0077_local-observability-vault-gitops-overlays.md)（dev Vault の opt-in）／
  [IADR-0094](IADR-0094_vault-keycloak-oidc.md)（OIDC bootstrap は runtime）／
  [IADR-0096](IADR-0096_vault-eso-secret-supply-k8s-auth.md)（ESO の k8s auth・`bootstrap.sh`）／
  [IADR-0369](IADR-0369_persist-by-default-and-realm-reconcile-job.md)（永続化は既定オン・`PERSIST=0` で opt-out）／
  [IADR-0456](IADR-0456_sc22-property-kinds-force-sync-reloader-and-seed-if-absent.md)（seed-if-absent。空で再 seed される前提が本 ADR で崩れる）
- 計画: ADR-0095（秘密の投入面は製品の画面。画面が書く先が本 Vault）／NFR-18

## コンテキストと課題

`deploy/local/vault/vault-dev.yaml` は `vault server -dev`（`storage_type: inmem`）である。2026-09-16 18:17 に Rancher Desktop / k3s が
全 Pod を再起動した際、Vault は `token/` 認証と `default` / `root` policy だけの空の状態で復帰し、`auth/kubernetes`・policy・role・
KV（`secret/msp/*`・`secret/ai-stock-trading/*`）・OIDC 設定がすべて消えた。`ClusterSecretStore vault-backend` は
`InvalidProviderConfig`、全 ExternalSecret が `SecretSyncedError` になった。復旧は `k8s-local-up.sh` の再実行だが、
画面 SC-22 で入れた値は戻らず、`bootstrap.sh` が `ai-stock-trading/app-secrets` を空文字で再 seed するため ESO が既存の
`ast-secrets` を空の値で置き換え、AST の外部連携が無言で止まる。RSA 鍵を生成し直せば OpenD に登録済みの鍵と食い違う。

IADR-0369 は Keycloak / Postgres / Qdrant の永続化を既定にしたが、Vault は対象外のままだった。

## 検討した選択肢

| 案 | 内容 | 判定 |
| --- | --- | --- |
| **A** | 非 dev モード＋ `file` ストレージを PVC に置く。Pod 内ラッパーが init（初回）・unseal（毎回）・固定 root トークン・kv-v2 mount を行う。unseal 鍵と初期 root トークンは PVC 上の 0600 ファイル | **採用** |
| B | A ＋ unseal 鍵を k8s Secret に置く | Pod 内に kubectl が無く、init 後に Secret を書く Job が要る。鍵も PVC も同じローカルディスク上で防御としての差が無い。不採用 |
| C | `raft` ストレージ | 単一 Pod では `file` より設定が増えるだけ（`cluster_addr` 等）。本番用途ではない。不採用 |
| D | `-dev` のまま、再起動後に bootstrap を自動で再実行する | 画面で入れた値は戻らない（問題の本体が解けない）。不採用 |

## 決定

### 1. 永続化は既定オン。`PERSIST=0` で従来の `-dev`（バイト等価）

`scripts/k8s-local-up.sh` の `VAULT=1` ブロックは既定で `deploy/local/vault-persistence`（kustomize オーバーレイ）を apply し、
`PERSIST=0` のときだけ base の `deploy/local/vault`（`-dev`・インメモリ）へ戻る。IADR-0369 決定 1 と同じ形で、
StorageClass の不在は [4/7] のガードが先に止める。ESO CRD 不在のフォールバック（`vault-dev.yaml` だけ）は従来どおり
非永続で、WARN にその旨を書く（滅多に通らない縮退経路。黙って落とさない）。

### 2. オーバーレイの形: base の Deployment を patch で差し替える

`deploy/local/vault-persistence/` は `../vault` ＋ PVC `vault-data`（`local-path`・1Gi）＋ ConfigMap `vault-local-config`
（`local.hcl`: `storage "file"`・`disable_mlock`・`ui`）＋ ConfigMap `vault-local-entrypoint`（ラッパー）＋ Deployment の JSON patch
（`args` を外して `command` をラッパーへ、volume / volumeMount、readinessProbe＝`vault status`、`runAsUser: 100` / `fsGroup: 1000`）。
`vault-dev.yaml` と `clustersecretstore.yaml` は無改変（`PERSIST=0` のバイト等価を保つ）。

### 3. Pod 内ラッパー `vault-entrypoint.sh` が毎回行うこと

1. `vault server -config=local.hcl` を背景で起動し、API 到達を待つ（`vault status` が 0 か 2）。
2. `vault operator init -status` が「未初期化」なら `operator init -key-shares=1 -key-threshold=1` の出力を
   `/vault/data/.local-dev-init`（0600）へ保存する。初期化済みなら何もしない。
3. 保存した鍵で unseal する。初期化済みなのに保存ファイルが無ければ**中断する**（値をでっち上げない）。
4. 固定 root トークン（Secret `vault-dev-token` の値＝`VAULT_DEV_ROOT_TOKEN_ID`）が無ければ、init の root トークンで
   `token create -id=<固定値> -policy=root -orphan` を行う。**これにより `bootstrap.sh`（`kubectl exec` で `$VAULT_DEV_ROOT_TOKEN_ID`）・
   ESO の store（token 認証版）・OIDC bootstrap・BFF の k8s auth は無改変で動く。**
5. `secret/` に kv-v2 が無ければ mount する（`-dev` が自動でしていたこと）。
6. サーバを待つ。SIGTERM は転送する。値（鍵・トークン）はログに出さない。

Vault 1.16 の使い捨てコンテナで、固定 ID のトークン作成・kv-v2 mount・サーバ再起動後に同じトークンで KV を読めることを実測した。

### 4. 起動器は apply の直後に `rollout status deploy/vault` で待つ

永続化版は readinessProbe（unseal 済みで 0）を持つ。待たないと後段の ESO bootstrap が unseal 前に `kubectl exec` して
"Vault is sealed" で落ちる。`-dev` は probe が無く即座に返る。

### 5. セキュリティの線引き

unseal 鍵と初期 root トークンを PVC 上の平文ファイルに置く。root トークンが既知の dev 既定（`devroot`）である現状と守りの
水準は同じ（**ローカル dev 専用・本番の Vault 化充足ではない**。本番は unseal / 監査 / HA / ローテーションを要する）。

## 理由

- 問題の本体は「画面で入れた秘密が消える」ことであり、bootstrap の自動再実行（案 D）では解けない。
- 既存の `PERSIST` ゲートに載せることで、利用者は何も指定せずに永続化される（IADR-0369 の「常用クラスタが誰にも気付かれず
  非永続で立っていた」と同じ轍を踏まない）。
- 固定トークンをラッパーが作ることで、既存の bootstrap / store / OIDC / BFF の配線を 1 行も変えずに済む。

## 影響・結果

- k3s 再起動後、Pod はラッパーで自動 unseal し、ESO の store が `Valid` に戻る。画面の値・policy・role・OIDC 設定が残る。
  `k8s-local-up.sh` の再実行は不要になる（実行しても init は走らず、seed-if-absent は無害）。
- 既存クラスタへの初回適用: Deployment が Recreate で差し替わり、新しい PVC に init する（`-dev` の中身は元々揮発するので失うものは無い）。
  同じ run の bootstrap が seed する。OIDC（STEP 2）は最後にもう一度だけ手で入れる。
- 文書: `local-sso-recovery-runbook.md` の揮発マトリクス（Vault の行）と STEP 2 の条件、`deploy/local/vault/*/README.md`、
  両 bootstrap.sh のヘッダ、`secret-item-console-injection-runbook.md`、`security.md` の「インメモリ」記述を改めた。
- 試験: `k8s-local-up.test.js`（既定で `vault-persistence`／`PERSIST=0` で `deploy/local/vault`／rollout 待ちの順序／CRD 不在の WARN）、
  `vault-entrypoint.test.sh`（`vault` スタブで init / unseal / 固定トークン / kv mount / 鍵ファイル不在の中断 / 秘密をログに出さない / 到達待ちの上限）。

## 残余リスク

- PVC を消して作り直すと保存ファイルも消え、ラッパーは init し直す（新しい鍵・空の KV）。`.local-dev-init` だけを消すと
  「初期化済みなのに鍵が無い」で中断する（復旧は PVC ごと消す）。
- 公式イメージの `docker-entrypoint.sh` を通らないため、`SKIP_SETCAP` 等の環境変数は効かない（`disable_mlock = true` で代替）。
- 本番像（Helm chart）は無改変。本 ADR は経路B（ローカル）に閉じる。
