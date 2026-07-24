---
title: IADR-0099 Vault＋ESO secret 供給 PR-4 — 基盤 secret（postgres/rabbitmq/keycloak-admin）を ExternalSecret 化（creationPolicy: Merge・bootstrap 手動 apply 保持）
type: impl-adr
status: Accepted
related_ids:
  - ADR-0006
  - IADR-0077
  - IADR-0096
  - IADR-0097
  - IADR-0098
author: claude
created: 2026-07-24
updated: 2026-07-24
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ (ADR-0006 運用基盤)"
---

# IADR-0099: Vault＋ESO secret 供給 PR-4（基盤 secret）

- 状態: Accepted
- 日付: 2026-07-24
- 決定者: claude（実装）

## 起点・関連

- 関連 ADR: ADR-0006。ESO 基盤（k8s auth・store 上書き・policy `eso-read`・seed/skip・専用 SA・VAULT 併用ガード）は
  [[IADR-0096]]（PR-1）。同パターンの継続は [[IADR-0097]]（PR-2）／[[IADR-0098]]（PR-3）。opt-in オーバーレイは [[IADR-0077]]。
- 仕様書: `docs/specs/20260724_issue-310_vault-eso-pr4-infra-secrets.md`。
- Issue: #310（Vault/ESO 本番同等化）。develop 最新（PR-1〜3 反映済み）ベース。番号採番: PR-3=0098 の次の **0099**
  （AST の 0099 は別リポジトリの名前空間で無関係）。**本 PR で #310 の secret 移行は一巡（PR-1〜4）**。

## コンテキストと課題

[[IADR-0096]]〜[[IADR-0098]] で LLM／app／OIDC secret を ESO 供給へ移行した。PR-4 は最後の区分＝**基盤 secret**
`postgres`・`rabbitmq`・`keycloak-admin`（各キー `password`）を対象とする。基盤 secret は他の区分と決定的に性質が異なり、
以下の 2 点が最大リスクである。

1. **bootstrap 順序性**: これら 3 secret は `scripts/k8s-local-up.sh` の step [4/7] infra rollout（`rollout status` で
   **ブロッキング**）で、postgres/rabbitmq/keycloak の各 Pod に **非 optional** な `secretKeyRef`（`optional: true` なし）で
   消費される。ESO ブロックは step 後半（infra Ready 後）に実行されるため、**ESO=1 で手動 apply をスキップすると
   step 4 時点で Secret が存在せず Pod が起動できない**（infra が永久に Ready にならず script が停止）。Vault dev 自体も
   step 後半で起動するため、「基盤 secret を Vault から供給してから infra を起動する」順序はローカル bootstrap では
   成立しない（chicken-and-egg。`vault-dev-token` を除外したのと同種の制約）。
2. **パスワード整合**: DB/broker/keycloak は既存パスワードで初期化済み（特に `PERSIST=1` の PVC・再実行）。ESO が供給する
   値が既存と 1 バイトでも異なると **認証破壊**（Pod env とストア上のパスワード不一致）。

## 決定

### 1. 手動 apply は保持する（`ESO=1` でもスキップしない）

step 3 の `apply_secret postgres/rabbitmq/keycloak-admin` は **無条件のまま**（PR-1〜3 の「`ESO=1` で skip」を
基盤 secret には適用しない）。これにより step 4 infra rollout の bootstrap 順序を壊さない。

### 2. ExternalSecret は `creationPolicy: Merge`

各基盤 secret に ExternalSecret（Vault `secret/msp/<name>`（KV v2）→ 既存 Secret 名・同一キー `password`）を新設するが、
`creationPolicy` は **`Merge`** とする。Merge は「既に存在する Secret にデータをマージするのみ・ESO は Secret を
所有（ownerReference）も再作成もしない」。手動 apply が作成した Secret に、Vault の**同一値**を上書きするだけになる。
これにより本番同等の Vault→ESO 供給経路を配線しつつ、Secret の所有権・ライフサイクルは手動 apply 側に残す。

### 3. seed 値は手動 apply と完全一致（★不一致防止）

`bootstrap.sh` の seed は step 3 の手動 apply と**同じ env・同じ既定**（`PG_PASSWORD:-postgres`／`RABBITMQ_PASSWORD:-guest`／
`KEYCLOAK_ADMIN_PASSWORD:-admin`）を使う。`bootstrap.sh` は `k8s-local-up.sh` から同一プロセス環境を継承して起動する
ため、両者は常に同じ値を見る。値が一致するので Merge は実質 no-op（データ変化なし＝Pod 再起動も PVC 初期化済み DB の
不整合も発生しない）。**平文の実 secret はリポジトリに置かない**（gitleaks green）。

### 4. policy は追加不要（自己チェック）

3 secret は `secret/msp/*` 配下のため、PR-1 の policy `eso-read`（`secret/data/msp/*`＋`secret/metadata/msp/*` read）で
既にカバーされる。policy 追加は不要（AST path も無改変）。

### 5. store・auth・SA・ガードは PR-1 のまま（無改変）

store は既定 token 認証のまま（`ESO=1` で k8s 認証版へ上書き＝PR-1）。専用 vault SA・auth-delegator・`ESO=1` の VAULT
併用ガードも PR-1 のまま。本 PR は ExternalSecret（Merge）／seed／ESO ブロックの apply 追加のみで、`VAULT=1` 単独の
挙動・AST 連携・本番 values/chart・消費側 `secretKeyRef`・realm を一切変えない。**`VAULT=1` 単独・`ESO` 未設定は
完全にバイト等価**（ESO ブロック不実行＝従来どおり手動 apply のみ）。

## 影響・トレードオフ

- 基盤 secret は「手動 apply（所有）＋ ESO Merge（同期）」の二層になる。本番では ESO 単独供給が理想だが、ローカル
  bootstrap の順序制約（infra が Vault より先に起動）を尊重し、Owner+skip ではなく Merge+保持を採る。sync 順序の
  リスクは値一致により無害化される（Merge が no-op）。
- `ESO=1` で ESO が同期に失敗しても（role/policy 未設定等）、基盤 Secret は手動 apply 済みで infra は起動する
  （fail-safe が PR-1〜3 より強い）。

## 代替案

- **Owner＋手動 apply skip（PR-1〜3 と同型）**: 却下。step 4 で Secret 不在→infra 起動不能。ESO ブロックが後段のため
  ローカル bootstrap の順序を満たせない。
- **ESO を infra より前へ再配置**: 却下。Vault dev も後段起動でありスクリプト全体の大規模再構成が必要。opt-in
  オーバーレイ構造（ESO は末尾の opt-in ブロック）を壊す。
- **`creationPolicy: Orphan`**: Orphan は Secret を作成し得る（所有はしないが上書き管理）。手動 apply と作成タイミングが
  競合し得るため、既存 Secret へのマージに限定する `Merge` の方が意図に忠実（作成は手動・同期のみ ESO）。
