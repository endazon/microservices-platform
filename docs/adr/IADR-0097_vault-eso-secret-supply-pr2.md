---
title: IADR-0097 Vault＋ESO secret 供給 PR-2 — minio-credentials/wikijs-db/wikijs-sync を ExternalSecret 化（IADR-0096 の設計踏襲・段階移行）
type: impl-adr
status: Accepted
related_ids:
  - ADR-0006
  - IADR-0077
  - IADR-0096
author: claude
created: 2026-07-21
updated: 2026-07-21
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ (ADR-0006 運用基盤)"
---

# IADR-0097: Vault＋ESO secret 供給 PR-2（minio/wikijs 系）

- 状態: Accepted
- 日付: 2026-07-21
- 決定者: claude（実装）

## 起点・関連

- 関連 ADR: ADR-0006。ESO 基盤（k8s auth・store 上書き・policy `eso-read`・seed/skip・専用 SA・VAULT 併用ガード）は
  [[IADR-0096]]（PR-1）。opt-in オーバーレイは [[IADR-0077]]。
- 仕様書: `docs/specs/20260721_issue-310_vault-eso-pr2-minio-wikijs.md`。
- Issue: #310（Vault/ESO 本番同等化）。**stacked PR**（#368/PR-1 のブランチに積む・#368 マージ後は develop へ非破壊マージで追従）。
  番号採番: PR-1=0096 の次の **0097**。

## コンテキストと課題

[[IADR-0096]]（PR-1）で ESO 基盤を敷き `llm-provider-credentials` を疎通した。PR-2 は同一パターンで
`minio-credentials`・`wikijs-db`・`wikijs-sync` を ExternalSecret 供給へ移行する。PR-1 の破壊系（`VAULT=1` 単独破壊・
policy path 不足）を再発させないことが要件。

## 決定

### 1. PR-1 の設計を機械的に踏襲する

各 secret に ExternalSecret（Vault `secret/msp/<name>`（KV v2）→ 既存 Secret 名・**同一キー**・`creationPolicy: Owner`）を
新設し、`bootstrap.sh` の seed に 3 secret を追加、`k8s-local-up.sh` で `ESO=1` 時は手動 apply をスキップして ExternalSecret に
委譲する。**既定（`ESO` 未設定）は手動 apply のままバイト等価**（fail-safe）。

### 2. policy は追加不要（自己チェック）

3 secret は `secret/msp/*` 配下のため、PR-1 の policy `eso-read`（`secret/data/msp/*`＋AST path read）で既にカバーされる。
policy 追加は不要（PR-1 の 🔴 教訓「共有 store の policy path 不足で 403」は本 PR では発生しない）。

### 3. store・auth・SA・ガードは PR-1 のまま（無改変）

store は既定 token 認証のまま（`ESO=1` で k8s 認証版 `clustersecretstore-k8s.yaml` へ上書き＝PR-1）。専用 vault SA・
auth-delegator・`ESO=1` の VAULT 併用ガードも PR-1 のまま。本 PR は ExternalSecret／seed／手動 skip の追加のみで、
`VAULT=1` 単独の挙動・AST 連携・本番 values/chart・消費側 `secretKeyRef`・realm を一切変えない。

### 4. seed は平文非コミット（現行既定と同値）

seed 値は env 由来 or dev プレースホルダ（`MINIO_ACCESS_KEY`/`MINIO_SECRET_KEY`→`minioadmin`、`WIKIJS_DB_PASSWORD`→`kp`、
`WIKIJS_SYNC_APIKEY`→空）で、現行 `apply_secret` の既定と同一。実 secret はリポジトリに置かない（gitleaks green）。

## 影響・トレードオフ

- `ESO=1` で minio/wikijs 系 secret も Vault→ESO→Pod 自動供給になる。ESO 同期前は Pod が一時的に待つ（PR-1 と同性質・
  ESO 同期で自己回復）。消費側は無改変。
- `VAULT=1` 単独＝完全にバイト等価（手動 apply のまま）。段階移行の 2 歩目で、残りは PR-3（OIDC 群）・PR-4（基盤）。

## 代替案

- **1 ファイルに 3 ExternalSecret を同梱**: レビュー容易性のため secret 別ファイルにする（llm と同じ粒度）。
- **手動 apply と ExternalSecret を併存**: 二重所有で競合するため `ESO=1` 時は手動をスキップ（PR-1 と同じ）。
