---
title: 作業仕様書 — Wiki.js dev ホスト公開の方針判断と回帰ガード
type: spec
status: done
related_ids:
  - FR-13
  - UC-07
  - IADR-0020
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md"
related_specs:
  - ../adr/IADR-0032_wikijs-dev-exposure-opt-in.md
  - ../operations/operations.md
---

# 作業仕様書: Wiki.js dev ホスト公開の方針判断と回帰ガード

Issue: #124（関連: #118 監査論点 2 ／ IADR-0020 ／ IADR-0017）。

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-13・UC-07（Wiki 閲覧・ABAC ゲートウェイ）
- 関連 ADR: ADR-0011（Wiki エンジン）・IADR-0020・IADR-0017・IADR-0009

## 目的・背景

`wiki-js`（Wiki.js 実体）の dev host 公開（compose `3001:3000`）が IADR-0020 の ABAC ゲートウェイを
迂回できる経路であり（#118 監査「逸脱の疑い 2」）、公開混入を検出する仕組みが無かった。

## 方針（要判断 → 決定）

**profiles 分離＋回帰ガード**（ユーザー判断）を採用。詳細は [IADR-0032](../adr/IADR-0032_wikijs-dev-exposure-opt-in.md)。

- 既定 compose では Wiki.js を host 公開しない（`expose: 3000`）。
- 直接アクセスは opt-in override（`deploy/docker-compose.wiki-direct.yml`）でのみ公開する
  （compose のサービスレベル profiles は常時稼働サービスの個別ポート公開を条件化できないため override で実現）。
- 回帰ガードを `NetworkIsolationTests` に追加。

## 対象範囲

- 対象:
  1. `deploy/docker-compose.yml`: `wiki-js` を `ports: 3001:3000` → `expose: 3000`。
  2. `deploy/docker-compose.wiki-direct.yml`: 3001 を公開する opt-in override（dev 限定）。
  3. `NetworkIsolationTests`: (a) 既定 compose で `wiki-js` 非公開、(b) Helm `wikijs.ingress.enabled: false` を検証。
  4. `IADR-0032` を起票、`operations.md` を更新。
- 非対象: Wiki.js 認可ロジック（既存 ABAC ゲートウェイ）・SPA。

## 受け入れ基準

- [x] dev 公開の扱いが判断され、根拠が文書（IADR-0032・operations.md）に記録されている。
- [x] stg/prod 相当の構成で Wiki.js がゲートウェイ迂回で到達できないことを検証する回帰ガードが存在する
      （既定 compose 非公開・Helm Ingress 無効）。
- [x] compose profiles 分離（＝override）を採用し、既定では 3001 が公開されない。

## テスト

- `NetworkIsolationTests.WikiJs_IsNotPublishedByDefault` / `WikiJs_HelmIngressDisabledByDefault`（計 4 件緑）。
- `docker compose config`（既定＝公開 0 件、override＝3001 公開）を確認。
