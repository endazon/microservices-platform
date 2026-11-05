---
title: 作業仕様書 — 取引ユニットの API の audience（ai-stock-trading-api）を載せるクライアントスコープを realm に足し、付ける先を bff と ai-stock-trading-owner に限る（#2418）
type: spec
status: done
related_ids: [NFR-09, NFR-18, IADR-0523, IADR-0680]
author: claude
created: 2026-11-05
updated: 2026-11-05
issue: "#2418"
---

# 作業仕様書 — 取引ユニットの API の audience のスコープ（#2418）

> 着手 2026-11-05（develop `170f1efb`）。判断の記録は **IADR-0680**（新規。番号は仮）。

## 起点

- 要求 **NFR-09**（サービス間の認証）・**NFR-18**。AST 側の検査は AST#1541（取引ユニットの REST が aud / azp を検査し、AST 専用の audience `ai-stock-trading-api` を使う）。
- 前提: **IADR-0523**（platform の API の共有 audience `platform-api` とその範囲の検査＝検査9）。

## 要件（コーディネーターの指示 2026-11-05）

1. クライアントスコープ `ai-stock-trading-api-audience` を作る。写像は `oidc-audience-mapper`・`included.custom.audience=ai-stock-trading-api`・access token だけ。
2. `bff` と `ai-stock-trading-owner` の `defaultClientScopes` に足す。
3. `platform-api` の audience を AST の検査に流用しない。
4. ほかのサービスアカウントに付けない。付ける先を 2 つに限ることを試験で固定する。

## 方針

- realm の宣言（`deploy/keycloak/microservices-platform-realm.json`）に足す。既存の realm へは調停（`reconcile-realm.js`）が `clientScope.create` と `client.scope.add` で当てる（新しい分岐は要らない）。
- 範囲の検査は `scripts/check-realm-constraints.js` の検査9 の 2 として足す（検査9 と同じ分類で数える）:
  - スコープは写像をちょうど 1 つ持つ（上の形。id token に載せない）。
  - `ai-stock-trading-api` を出す写像は、このスコープの外（ほかのスコープ・`platform-api-audience`・クライアント直付け）に置かない。
  - 既定のスコープに持つクライアントは、ちょうど `bff` と `ai-stock-trading-owner`。任意スコープ・realm の既定に置かない。

## 受け入れ基準

- AC-1: 実データの realm で、スコープの形と付ける先（ちょうど 2 つ）が検査を通る（`check-realm-constraints.js --self-test` の実データの節）。
- AC-2: ほかのサービスアカウントに付ける・任意スコープに置く・platform-api の写像へ足す・直付けすると、検査が赤になる（自己試験）。
- AC-3: 既存の realm の検査・調停の試験が通る。

## 残余

1. AST 側（AST#1541）が検査を有効にするまでは、トークンの aud に値が 1 つ増えるだけで振る舞いは変わらない。
