---
title: k8s-local-up.sh の opt-in フラグ分岐に横断 smoke test を追加（Issue #334）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0066
  - IADR-0084
  - IADR-0087
author: claude
created: 2026-07-20
updated: 2026-07-20
related_specs:
  - "../adr/IADR-0087_k8s-local-up-optin-smoke-test.md"
  - "../adr/IADR-0084_headlamp-oidc-apiserver-flags.md"
  - "../adr/IADR-0066_local-k8s-dev-environment.md"
  - "../../scripts/k8s-local-up.sh"
  - "../../scripts/k8s-local-up.test.js"
  - "../../scripts/scripts.test.js"
---

# 仕様書: k8s-local-up.sh の opt-in フラグ分岐に横断 smoke test を追加（Issue #334）

## 起点となる計画書（トレーサビリティ）

- 機能要求(FR): なし（運用・dev 環境スクリプトの回帰防止。プロダクト機能ではない）。
- 非機能要件(NFR): 運用性・信頼性（`k8s-local-up.sh` の opt-in ゲートが既定オフで副作用ゼロ、
  有効化時のみ該当リソース/引数を付与するという不変条件を機械で固定し、後退を CI で止める）。
- 関連 ADR: 方式判断は [[IADR-0087]]（stub-on-PATH・スクリプト無改変）。検証対象の分岐は
  [[IADR-0084]]（apiserver OIDC フラグ）・[[IADR-0066]]（経路B＝k3d dev 環境）・IADR-0077/0080/0082 等。
- Issue: #334（本 issue・運用/dev・testing・priority:could）。#331（PR・#328・IADR-0084）の
  claude-review 🟡 フォローアップ。

## 目的・背景（As-Is）

`scripts/k8s-local-up.sh` は複数の opt-in 環境変数ゲートを持つ:

| env | 既定 | ON 時の効果（検証対象） |
| --- | --- | --- |
| `HEADLAMP_OIDC_APISERVER`（未設定なら `HEADLAMP` に追従） | off | `k3d cluster create` に apiserver OIDC 4 フラグ付与。`=0` で escape。`HEADLAMP_OIDC_ISSUER_URL`/`HEADLAMP_OIDC_CLIENT_ID` で override |
| `PERSIST` | off | `INFRA_KUSTOMIZE` を `deploy/local/infra` → `deploy/local/infra-persistence` へ切替 |
| `OBSERVABILITY` | off | `kubectl apply -k deploy/local/observability` ＋ otel-collector rollout restart |
| `VAULT` | off | vault-dev-token secret ＋ `deploy/local/vault`（CRD 有時）/`vault-dev.yaml`（CRD 無時） |
| `ARGOCD` | off | argocd namespace ＋ argocd manifest 群 apply |
| `HEADLAMP` | off | headlamp-oidc secret ＋ `kubectl apply -k deploy/local/headlamp` |

#331（IADR-0084）の claude-review で、`HEADLAMP_OIDC_APISERVER` の `CREATE_ARGS` 構築ロジック等に
自動テストが無い点が 🟡 指摘された。レビュー自身が述べるとおり、これは #331 固有の後退ではなく、
既存の他 opt-in フラグ（`OBSERVABILITY`/`VAULT`/`ARGOCD`/`HEADLAMP`/`PERSIST`）も同様に未カバーであった。
よって #331 に単発テストを足すのは範囲不整合で、**全 opt-in フラグ横断の smoke test を別タスクとして整備**する。

## 決定した方式（As-Is → To-Be）

方式判断は [[IADR-0087]] に記録。要点:

- **bash stub-on-PATH（`k8s-local-up.sh` は無改変）** を採用。外部バイナリ（`k3d`/`kubectl`/`helm`/`docker`）を
  PATH 上の記録スタブへ差し替え、**副作用ゼロ**で `k8s-local-up.sh` を実行し、発行コマンド列を採取して
  分岐をアサートする。arg 構築部の sourceable 関数抽出（第2案）は採らない（スクリプト改変＝後退リスク）。
- テスト実体は `scripts/k8s-local-up.test.js`（既存 `scripts/scripts.test.js` と同型・Node 標準 `assert` のみ・
  外部依存ゼロ）。Node が `bash` を spawn し、スタブを噛ませて実行する。
- `K8S_LOCAL_RUNTIME=k3d` を固定し `k3d cluster list` を非0（＝未作成）に返させて `cluster create` 経路を
  決定的に通す。`src/ai-stock-trading` submodule 未取得（CI 既定）で AST 分岐は決定的に skip。

## 受け入れ基準（テストへ写像）

- [x] **既定（全 OFF）**: `k3d cluster create <cluster> --agents 1 -p 8080:80@loadbalancer -p 8443:443@loadbalancer` が
  現行とバイト等価。opt-in 由来リソース（observability/vault/argocd/headlamp kustomize・PVC overlay 等）が一切現れない。
- [x] **HEADLAMP_OIDC_APISERVER=1**: apiserver OIDC 4 フラグ（issuer-url/client-id/username-claim/username-prefix）付与。
- [x] **HEADLAMP=1 追従**: `HEADLAMP_OIDC_APISERVER` 未設定でも `HEADLAMP=1` で 4 フラグ付与。
- [x] **HEADLAMP=1 かつ HEADLAMP_OIDC_APISERVER=0（escape-hatch）**: 4 フラグは付与されない（byte 等価の cluster create）。
- [x] **issuer/client override**: `HEADLAMP_OIDC_ISSUER_URL`/`HEADLAMP_OIDC_CLIENT_ID` が引数に反映される。
- [x] **PERSIST=1**: `deploy/local/infra-persistence` を apply（`deploy/local/infra` 単体ではない）。
- [x] **OBSERVABILITY=1**: `deploy/local/observability` apply。
- [x] **VAULT=1**: `deploy/local/vault`（CRD 有）apply ＋ vault-dev-token secret。
- [x] **ARGOCD=1**: argocd namespace ＋ argocd application manifest apply。
- [x] **HEADLAMP=1**: `deploy/local/headlamp` apply ＋ headlamp-oidc secret。

## CI 配線

- `.github/workflows/ci.yml` に独立ジョブ `k8s-local-up-smoke`（`node scripts/k8s-local-up.test.js`）を追加する。
  既存ステップは保持し追加のみ。外部依存ゼロ（`scripts.test.js`／各 `--self-test` と同じ運用）。

## 非スコープ

- 実クラスタでの起動（live 疎通）。本タスクは stub による分岐検証まで。
- backend/values の OIDC（#314）・Dockerfile 群/images.yml（#320）・datasource・frontend/edge・infra 永続化・
  realm への変更。`k8s-local-up.sh` の挙動変更も行わない（テスト追加のみ）。

## 検証

- `node scripts/k8s-local-up.test.js` がローカル（Git Bash）と CI（ubuntu）で緑。
- 既存 CI（#275 ドリフト・images.yml・scripts.test 群）を壊さない。
