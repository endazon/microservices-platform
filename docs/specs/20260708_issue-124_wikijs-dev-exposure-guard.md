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

**dev 公開は残す＋本番系(Helm)非公開の回帰ガード**（ユーザー判断 A）を採用。詳細は [IADR-0032](../adr/IADR-0032_wikijs-dev-exposure-opt-in.md)。

- dev の compose は管理 UI セットアップ便宜のため `ports: 3001:3000` を維持（dev 公開は残す）。
- 本番系（Helm）は `wikijs.ingress.enabled: false` で公開しない（ClusterIP 限定）。
- 「本番系構成では 3001 が公開されない」ことを `NetworkIsolationTests` が機械的に回帰ガードする。
- **compose profiles の補足**: docker compose のサービスレベル profiles は「サービスの起動有無」を制御するもので、
  常時稼働サービスの個別ポート公開だけを条件化できない。Wiki.js は WikiService の後段として dev でも常時稼働が
  必要なため、dev/本番系の公開境界は「dev＝compose（3001 公開）／本番系＝Helm（Ingress 無効・回帰ガード）」で表現する。

## 対象範囲

- 対象:
  1. `deploy/docker-compose.yml`: `wiki-js` の `ports: 3001:3000`（dev 公開）を維持し、方針を明記。
  2. `NetworkIsolationTests`: (a) 本番系（Helm）`wikijs.ingress.enabled: false`、(b) dev 公開が wiki-js に限定され
     他内部サービスへ波及しないことを検証。
  3. `IADR-0032` を起票、`operations.md` を更新。
- 非対象: Wiki.js 認可ロジック（既存 ABAC ゲートウェイ）・SPA。

## 受け入れ基準

- [x] dev 公開の扱いが判断され、根拠が文書（IADR-0032・operations.md）に記録されている（dev 公開は残す）。
- [x] 本番系相当の構成で Wiki.js がゲートウェイ迂回で到達できないことを検証する回帰ガードが存在する
      （Helm `wikijs.ingress.enabled: false`）。
- [x] 本番系構成では 3001 が公開されない（NetworkIsolationTests で常時検証）。

## テスト

- `NetworkIsolationTests.WikiJs_DevExposureIsRetainedOnComposeOnly` / `WikiJs_HelmIngressDisabledByDefault`
  / `InternalServices_MustNotPublishHostPorts`（計 4 件緑）。
- `docker compose config`（dev＝wiki-js 3001 公開を確認）。
