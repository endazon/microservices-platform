---
title: 作業仕様書 — 基盤の資格情報の段階の fail-closed を続ける（管理者トークンの作り直しの持続・対象の引き直し・dev のクラスタの 2 つ目の手掛かり。#2407）
type: spec
status: in-progress
related_ids: [NFR-18, ADR-0124, IADR-0672, IADR-0574, IADR-0517, IADR-0457, IADR-0676]
author: claude
created: 2026-11-05
updated: 2026-11-05
issue: "#2407"
---

# 作業仕様書 — 基盤の資格情報の段階の fail-closed を続ける（#2407）

> 本仕様書は実装着手前に作成した（着手 2026-11-05。develop `31bd4833`＝#2400 のマージ後）。判断の記録は **IADR-0676**（新規。番号は仮）に置く。
> 🔴 稼働中のクラスタには何も実行しない。試験はすべて PATH のスタブの下で行う。値は表示しない・リポジトリに置かない。

## 起点（トレーサビリティ）

- 非機能要求: **NFR-18**。計画 ADR: **ADR-0124 決定 1**。
- 前提の IADR: **IADR-0672**（段階の対象・検査と作り直しのスクリプト）、**IADR-0574**、**IADR-0517**（kube context の許可集合）、**IADR-0457**（秘匿管理の永続化のラッパー）。

## 要件（コーディネーターの指示 2026-11-05）

1. 秘匿管理の固定の管理者トークンを作り直したとき、その値が秘匿管理の再起動と起動器の再実行を跨いで保たれる。これを試験で固定する。
2. 段階の対象の母集合を走査で引き直し、まだ入っていない基盤の資格情報を足す（規則 9）。
3. dev のクラスタの判定に、kube context の名前のほかの手掛かりを足す。既存の dev のクラスタを壊さないよう段階で入れる。
4. 稼働を止め得る変更は、段 1 を報告だけにし、強制は明示の切り替えにする。

## 現状の確かめ（develop `31bd4833` で読み直した）

| 項目 | #2400 で入ったもの | 本件で足すもの |
| --- | --- | --- |
| 1 | 起動器は既存の Secret の値を引き継ぐ（IADR-0672 決定 2）。作り直した値は起動器の再実行で戻らない | 作り直しの後の再起動・再実行を跨ぐ持続を、試験で固定する。ラッパーが固定のトークンを作る条件を、初回の作成に絞れるようにする（段階）。トークンの値の受け渡しを stdin に揃える |
| 2 | 5 つの対象（IADR-0672 決定 1） | 走査の語を足して引き直し、2 つを対象に足す（下の表） |
| 3 | — | 判定の手掛かりを context の名前のほかに 1 つ足す（段階） |

## 母集合（規則 9。走査で引いた）

### 走査語

IADR-0672 の 2 つの語に、次の 2 つを足した（`scripts/`・`deploy/local/`・`deploy/helm/`・`deploy/keycloak/` の追跡ファイル）。

- マニフェストの `name: <…PASS…|…SECRET…|…TOKEN…|…_KEY…>` に **`value:` の直書き**が続くもの
- **製品が内蔵する既定の管理者**: `deploy/local/infra`・`deploy/local/observability`・`deploy/local/argocd`・`deploy/local/headlamp`・`deploy/local/wikijs-setup` の各製品について、管理者の資格情報を env か Secret で与えているかを 1 つずつ読んだ

### 新たに対象にするもの

| 対象 | 現状 | 本件の形 |
| --- | --- | --- |
| 可観測性の画面（Grafana）の内蔵の管理者 | 資格情報をリポジトリから与えていない（OIDC が使えないときの break-glass） | Secret `platform-infra/grafana-admin`（`password`）を、**明示 ＞ 既存の値 ＞ 乱数**で作る（開発用の値を持たない。セッションストアと同じ形）。Pod は optional で読む。永続化の overlay は起動のたびに Secret の値を DB へ合わせる（初回の作成のときしか env を読まないため）。検査と作り直しの対象に足す |
| DB の初期化が作る DB 利用者 `kp`（IADR-0672 残余 1） | 初期化が Secret の値を読まない | 起動器が `platform-infra/postgres-init-users`（`kp-password`。値は `APP_DB_PASSWORD` と同じ）を置き、初期化は在ればその値で作る（無ければ従前の値。段 1）。値は psql の `\getenv` で読み、引数に載せない |

### 除外とその理由

| 走査で当たったもの | 除外の理由 |
| --- | --- |
| Keycloak の `KC_DB_USERNAME` / `KC_DB_PASSWORD`（直書き） | `start-dev` の埋め込みファイル DB（H2）の資格で、ネットワークから届かない。Pod の外へ出ない値（keycloak.yaml の注記） |
| DB 利用者 `ai`（取引ユニット） | IADR-0672 残余 2 と同じ（消費側の Secret は取引ユニットが持ち、作り直しは対で行う） |
| Wiki.js の管理者・セッションストア・観測の前段・ブローカのサービスごとのユーザー・ArgoCD の初期の管理者 | 既存の値か、初回だけ乱数で作る（ArgoCD は製品が乱数で作る） |
| OIDC の client secret（`*_OIDC_*` 等） | IADR-0517 / IADR-0572 の範囲（client の資格情報で、基盤のストアの管理者ではない） |
| `deploy/docker-compose.yml` ほか compose の経路 | IADR-0574 §残余 2 |
| `deploy/cloud/` | 並行の作業（#2401 / #2402）の範囲 |

## 方針（IADR-0676）

### 1. 固定の管理者トークン（ラッパー）

- ラッパーは、固定のトークンを作ったとき、または在ることを確かめたときに、PVC へ印（値を持たない）を置く。
- 印が在るのに Secret の値のトークンが無いとき:
  - 段 1（既定）: 従前どおり作るが、名指して警告する。
  - 段 2（`INFRA_CREDENTIALS_ENFORCE=1` の起動器の実行）: 作らずに起動を失敗させ、手順書を名指す。
  - 切り替えは ConfigMap `platform-infra/vault-local-switches` の `root-token-init-only` を Pod が optional に読む。起動器が実行のたびに書く。
- 印が無いとき（初回・#2407 より前のストア）は従前どおり作る（既存のクラスタは、次の起動で在ることを確かめて印を置く）。
- トークンの値は stdin で渡す（`vault write auth/token/create-orphan id=-`）。作り直しのスクリプトの作成・失効も同じ。

### 2. 対象の引き直し

- 上の表の 2 つ。

### 3. dev のクラスタの 2 つ目の手掛かり

- 印は ConfigMap `kube-system/msp-dev-cluster`（`cluster-kind: dev`）。値は秘密ではない。
- 置く口は 2 つ: 起動器が**この実行で k3d のクラスタを作った**とき、と利用者の明示（`MARK_DEV_CLUSTER=1`。名前が許可集合に在るときだけ）。
- 判定の段:
  - 段 1（既定）: 名前だけで判定する（従前どおり）。名前が dev で印が無いときは警告する。
  - 段 2（`DEV_CLUSTER_MARKER_REQUIRED=1`）: 名前と印の両方が揃うときだけ dev とする。
- 印は入口（起動器・Vault の種・realm の追随）が先頭で 1 回読み、環境変数で渡す（判定は純関数のまま）。

### 4. 秘匿管理の NetworkPolicy（コーディネーターの追加の指示 2026-11-05）

- 現状: `deploy/local` の秘匿管理の Pod に NetworkPolicy は無い（`kind: NetworkPolicy` の走査で、valkey・rabbitmq・可観測性だけ）。
- 秘匿管理の送り手（`vault.platform-infra…:8200` と `number: 8200` の走査）:
  - `values-local.yaml`（bff・datasource）、`platform-backup/vault/cronjob.yaml` と `script/backup.sh`（バックアップ）、
    `edge/admin-ingress-infra.yaml`・`edge-istio/virtualservice-admin.yaml`（管理ツールのエッジ）、
    `vault/clustersecretstore.yaml`・`vault/eso/clustersecretstore-k8s.yaml`（ESO）。
  - 除外: `vault-persistence/local.hcl`（秘匿管理自身の待受）、`backup.test.sh`（試験）。起動器・種は `kubectl exec`。
- 形: Ingress だけを絞る。段 1 は opt-in（`VAULT_NETWORK_POLICY=1`）。

## 受け入れ基準

- AC-1: `rotate-infra-credentials.sh --live vault-root-token` で作ったトークンが、ラッパーの再起動（段 2 を含む）で作り直されず、起動器の再実行でも Secret が新しい値のまま保たれる（試験）。
- AC-2: 印が在り Secret の値のトークンが無いとき、段 1 は警告して作り、段 2 は作らずに失敗する（試験）。
- AC-3: ラッパーと作り直しのスクリプトが、トークンの値を `vault` の引数に載せない（試験）。
- AC-4: Grafana の管理者の Secret を、明示 ＞ 既存 ＞ 乱数で作り、検査と作り直しの対象に入る（試験）。
- AC-5: DB の初期化が `kp` を Secret の値で作り、値を引数に載せない（試験）。
- AC-6: 印の段 1 は判定を変えずに警告し、段 2 は印の無い dev 名の context を dev と見なさない（試験）。
- AC-7: 既定（切り替え無し）で、既存の dev のクラスタの起動器の再実行が止まらない（既存の試験が通る）。
- AC-8: 秘匿管理の NetworkPolicy は 8200 を走査で引いた送り手だけへ開け、段 1 は opt-in。新しい送り手は試験が止める（試験）。

## 検証（/verify。2026-11-05）

- `vault-entrypoint.test.sh` 77 passed。`scripts.repo.test.js` を個別に全件: 失敗は `check-adr-numbering`（仮番号の前の未マージ分による欠番）だけ。
- `k8s-local-up.test.js`: 本件の試験と関連（#1793 ほか）を個別に実行して通過。全件はマシンの負荷で手元で完走できず、CI で確かめる。
- 文書・静的の検査は通過。状態は、改番と CI の全件の通過を確かめた後に done へ進める。

## 残余

1. 段 2 の既定化（`INFRA_CREDENTIALS_ENFORCE`・`DEV_CLUSTER_MARKER_REQUIRED`）は、PoC で検査が 0 を返し、印を置いた後に別の PR で入れる。
2. 初期の root トークン（PVC 上の初期化の出力）は失効させていない（IADR-0457 の判断のまま）。
3. `PERSIST=0`（`-dev`）の秘匿管理は毎回作り直す（使い捨てのスタック専用）。
