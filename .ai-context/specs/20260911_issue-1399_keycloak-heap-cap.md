---
title: ローカル k8s の Keycloak に JVM ヒープ上限を与え、OOMKilled の繰り返しを止める
issue: "#1399"
plan_refs:
  - NFR
  - ADR-0004
adr_refs:
  - IADR-0091
status: done
created: 2026-09-11
---

# 作業仕様書: Keycloak の JVM ヒープ上限（#1399）

## 起点

- 稼働クラスタで `platform-infra/keycloak` が 7 日で **11 回** OOMKilled（exit 137・limit 2Gi）。再起動 8 分後で 1071Mi。
  再起動中の約 1 分、AST の `/reports/daily-policy` が 401 になる（JWKS / 発行元の一時不達）ことを PoC 観測中に実測。
- `deploy/local/infra/keycloak.yaml` は `JAVA_OPTS_KC_HEAP` を与えておらず、Keycloak 24 の既定 `-XX:MaxRAMPercentage=70`
  （≒ 1.4GiB ヒープ）に metaspace / off-heap が乗って 2Gi を超える。

## 設計

| 対象 | 変更 |
| --- | --- |
| `deploy/local/infra/keycloak.yaml` | env `JAVA_OPTS_KC_HEAP=-XX:MaxRAMPercentage=50`（ヒープ ≤ 1GiB）。limits は据え置き |

`JAVA_OPTS` 全体は置き換えない（Keycloak が公式に用意した上書き点）。本番 chart は Keycloak を内包しない（変更なし）。

## 走査した母集合（規則 2・9）

`quay.io/keycloak/keycloak` で `deploy/` を走査: `deploy/local/infra/keycloak.yaml`（変更）と `deploy/docker-compose.yml`
（compose は `mem_limit` を持たず OOM の実測も無い・据え置き）。`JAVA_OPTS` の既存指定は無し。

## 受け入れ基準

- [x] `kubectl kustomize deploy/local/infra` の keycloak Deployment に env が描画される
- [ ] 稼働クラスタで `kubectl set env` 後 24h の再起動回数が増えない（#1399 に記録）
