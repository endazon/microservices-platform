---
title: AST の KB 書き込み用サービスクライアント（platform-operator）を microservices-platform レルムに追加する（AST #18）
type: spec
status: review
related_ids:
  - FR-06
  - IADR-0030
  - IADR-0041
  - IADR-0044
  - ADR-0018
author: claude
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md"
---

# 作業仕様書: AST KB 書き込み用サービスクライアントの realm 追加

> AST endazon/ai-stock-trading#18（FR-08 ナレッジベース保存）の実 s2s 配線に必要な、基盤（本レルム）側の変更。
> 設計判断は [[IADR-0072]]。AST 側は AST リポの `IADR-0093` / PR `feat/FR-08-kb-writer-cross-realm-s2s`。

## 背景（着手前確認）

- `POST /documents` は [[IADR-0044]] で `platform-admin`/`platform-operator` 必須、検証レルムは `microservices-platform`。
- AST の既存 s2s（AST レルム `ai-stock-trading-svc` / `trading-service`）は issuer 不一致（401）＋role 不一致（403）で
  KB へ書き込めない。基盤無改修（ADR-0018）の下、本レルムに AST 用サービスクライアントを用意して書き込み経路を与える。

## 変更内容（本レルムに閉じる・追加のみ）

`deploy/keycloak/microservices-platform-realm.json`:

1. **client `ai-stock-trading-kb-writer`** を追加（confidential・`serviceAccountsEnabled: true`・client_credentials のみ）。
2. **service-account ユーザ `service-account-ai-stock-trading-kb-writer`** を追加し `realmRoles: ["platform-operator"]` を付与。

既存クライアント・ロール・ユーザーには触れない。dev シークレットはプレースホルダ（本番は Vault/Secrets）。

## 受け入れ基準

- [x] realm import が有効な JSON で、`ai-stock-trading-kb-writer`（confidential・service-account 有効）が存在する。
- [x] service-account に `platform-operator` が付与されている（＝client_credentials トークンで `POST /documents` を通過可能）。
- [x] 既存の client/role/user に差分がない（追加のみ）。

## ローカル経路B（AST と合わせた通し確認・分離）

1. 本レルムを再インポート（Keycloak dev）。
2. AST 側で `KnowledgeBase__Documents__BaseUrl`＝DocumentService、`KnowledgeBase__Auth__Authority`＝本レルム、
   `KnowledgeBase__Auth__ClientId=ai-stock-trading-kb-writer`＋シークレットを投入。
3. AST の収集/報告で `POST /documents` が **201** を返すことを確認（AST #18 に手順を記載）。

## スコープ外

- object storage への本文取り込み・Ingestion による検索可能化（AST #9/#14 系・platform 側の別作業）。
- 本番シークレットの Vault 投入・配布（運用）。
