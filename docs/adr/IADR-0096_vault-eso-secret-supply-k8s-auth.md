---
title: IADR-0096 手動 apply_secret を Vault＋ESO(k8s auth) の ExternalSecret 供給へ段階移行する。PR-1 は llm-provider-credentials 1本で疎通し、ESO=1 opt-in・既定バイト等価・fail-safe を担保する
type: impl-adr
status: Accepted
related_ids:
  - ADR-0006
  - ADR-0010
  - IADR-0077
  - IADR-0094
author: claude
created: 2026-07-21
updated: 2026-07-21
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ (ADR-0006 運用基盤)"
---

# IADR-0096: Vault＋ESO(k8s auth) による secret 自動供給への段階移行（PR-1 疎通）

- 状態: Accepted
- 日付: 2026-07-21
- 決定者: claude（実装）

## 起点・関連

- 関連 ADR: ADR-0006 / ADR-0010（llm-provider-credentials の消費）。Vault dev/ESO opt-in は [[IADR-0077]]、Vault の
  runtime bootstrap 先例は [[IADR-0094]]。
- 仕様書: `docs/specs/20260721_issue-310_vault-eso-secret-supply.md`。
- Issue: #310（Vault/ESO 本番同等化）。番号採番: develop 最新 IADR max=0094、0095 は in-flight #367（Wiki.js）に予約 → **0096**。

## コンテキストと課題

MSP secret は手動 `apply_secret` 供給。手動 patch を廃し Vault＋ESO で Pod へ自動供給する本番同等構成にしたい（#310）。
一度に全 secret を移すのはリスクが高いため段階移行し、**PR-1 は `llm-provider-credentials` 1本で end-to-end 疎通**する。
制約は「本番/既存 byte 等価」「opt-in・移行中も手動経路を壊さない fail-safe」「平文 secret 非コミット」。

## 決定

### 1. 認証は k8s auth（本番同等）だが、既定の store は token 認証のまま（VAULT=1 単独を壊さない）

ESO の `ClusterSecretStore vault-backend`（既存）を本番同等の **kubernetes 認証**にする（静的 root トークンを store に
持たない）。Vault の kubernetes auth を有効化し、**Vault 自身の in-cluster SA を reviewer** に使う
（`kubernetes_host=https://kubernetes.default.svc`・local CA/JWT）。Vault SA に `system:auth-delegator`（TokenReview）を bind。
role `eso` を ESO の SA（`external-secrets`/ns `external-secrets`）に束縛し policy `msp-read`（`secret/data/msp/*` read）を付与。

**重要（byte 等価・既存フロー保護）**: 既定の `deploy/local/vault/clustersecretstore.yaml` は **token 認証のまま不変**とし、
`VAULT=1` 単独（既存の opt-in）では従来どおり token 認証で store が立つ（AST の ExternalSecret 等の既存 consumer を壊さない）。
`ESO=1` ブロックでは **bootstrap.sh で k8s auth backend／policy／role `eso` を設定した「後に」**、同名 `vault-backend` の
**kubernetes 認証版**（`deploy/local/vault/eso/clustersecretstore-k8s.yaml`）を apply して store を上書きする。これにより
「k8s auth backend 未設定なのに store が k8s auth」という不整合（`VAULT=1` 単独破壊）を回避する。消費側は store 名参照のみで透過。

### 2. path→Secret マッピングは既存 Secret 名・同一キー（消費側不変）

ExternalSecret は Vault `secret/msp/<name>`（KV v2）→ **既存 k8s Secret 名・同一キー**へ供給する。PR-1 は
`secret/msp/llm-provider-credentials`（`anthropic-api-key`/`openai-api-key`）→ Secret `llm-provider-credentials`。
llmgateway の `secretKeyRef`（[ADR-0010]）は無改変。

### 3. bootstrap / seed は runtime（IADR-0094 と同型・平文非コミット）

k8s auth の enable/config・policy・role・seed は Vault API/CLI の runtime 操作で、`kubectl exec` 経由の `bootstrap.sh`
（再実行可・出力パース不要でスタブ安全）で行う。**seed 値は env 由来 or 空既定**（`ANTHROPIC_API_KEY`/`OPENAI_API_KEY`）で
Vault へ投入し、**リポジトリに平文値を置かない**（gitleaks green）。空＝外部 LLM を呼ばない現行 fail-safe と同値。

### 4. opt-in `ESO=1`・二重所有回避・既定バイト等価

`scripts/k8s-local-up.sh` に `ESO=1`（`VAULT=1` 併用）を新設。ON で ESO 本体を install＋RBAC＋bootstrap＋ExternalSecret を
適用し、**`llm-provider-credentials` の手動 `apply_secret` をスキップ**（ExternalSecret が Secret を所有＝二重所有の競合回避）。
**既定（`ESO` 未設定）は手動 apply のままバイト等価**（fail-safe）。本番 `values.yaml`/chart・消費側は無改変（ESO は経路B
opt-in オーバーレイに限定）。回帰は smoke test で固定。

## 影響・トレードオフ

- `ESO=1` で secret が Vault→ESO→Pod へ自動供給され、手動 patch を廃せる（本番同等の第一歩）。
- ESO 同期は helm install 後に走るため、`llm-provider-credentials` は一時的に未作成→llmgateway Pod が数秒
  `CreateContainerConfigError` になりうる（ESO 同期で自己回復）。消費側は無改変（`secretKeyRef` を optional にしない）。
- dev Vault はインメモリ（Recreate）＝再起動後は bootstrap＋seed を再実行（README 明記）。
- store を k8s auth へ移行するため AST ExternalSecret も k8s auth になる（role/policy は MSP path を対象・AST は別 path）。

## 代替案

- **token 認証のまま PR-1**: 本番同等でない（静的 root トークンを store 保持）。ユーザー合意 (A)/(B) により k8s auth を採用。
- **MSP 用に別 store 新設**: store が分散。既存 `vault-backend` を k8s auth へ一元化する（合意 (B)）。
- **seed をリポジトリに同梱**: 平文コミット禁止。env 由来 or 空既定に一元化。
- **消費側 secretKeyRef を optional 化**: 消費側改変を避け不採用（ESO 同期で自己回復する eventual consistency を許容）。
