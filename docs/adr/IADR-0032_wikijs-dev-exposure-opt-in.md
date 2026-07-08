---
title: IADR-0032 Wiki.js の dev ホスト公開は既定で無効・opt-in の override でのみ公開する
type: impl-adr
status: Accepted
related_ids:
  - FR-13
  - UC-07
  - IADR-0020
  - IADR-0017
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0011_wiki-engine.md"
---

# IADR-0032: Wiki.js の dev ホスト公開は既定無効・opt-in override でのみ公開する

- 状態: Accepted
- 日付: 2026-07-08
- 決定者: claude（Issue #124 ／ #118 監査論点 2 の是正）

## 起点・関連

- 関連する計画書 ID: FR-13・UC-07・ADR-0011（Wiki 統合）
- 関連する実装 ADR: [IADR-0020](./IADR-0020_wiki-js-deployment-abac-gateway.md)（Wiki.js 配備・ABAC ゲートウェイ）・
  [IADR-0017](./IADR-0017_internal-service-auth-network-isolation.md)（ネットワーク分離）・
  [IADR-0009](./IADR-0009_wiki-browsing-404-hides-existence.md)（存在秘匿）

## コンテキストと課題

Wiki.js（閲覧・編集 UI の実体）の認可は、WikiService の **ABAC ゲートウェイ**が前段で強制する
（deny-by-default 属性フィルタ＋404 存在秘匿）。Wiki.js に**直接**到達できる経路は、この ABAC ゲートウェイを
**迂回**する。

- 従来 dev の compose は開発便宜で `wiki-js` を host 公開（`3001:3000`）していた。stg/prod（Helm）は
  `wikijs.ingress.enabled: false` で公開しておらず ClusterIP 限定だが、これは「**たまたま守られている**」状態で、
  構成変更で公開が混入しても検出する仕組みが無かった（#118 監査「逸脱の疑い 2」）。

## 決定

1. **dev の host 公開は既定で無効化する。** `deploy/docker-compose.yml` の `wiki-js` は `expose: 3000`（コンテナ
   ネットワーク内のみ）とし、host へは公開しない。
2. **直接アクセスは opt-in の override でのみ許可する。** Wiki.js 管理 UI での初期セットアップ（OIDC 構成・
   ロケール導入・API キー発行）等で直接アクセスが必要な場合は、`deploy/docker-compose.wiki-direct.yml`
   を重ねて起動する（`docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.wiki-direct.yml up -d`）。
   これにより **既定では 3001 が公開されない**。
   - 補足: compose のサービスレベル `profiles` は常時稼働サービスの**個別ポート公開**を条件化できない
     （profiles はサービスの起動有無を制御する）。Wiki.js は常時稼働が必要なため、同等の「既定非公開・
     opt-in 公開」を override ファイルで実現する。
3. **回帰ガードを追加する。** `NetworkIsolationTests` で (a) 既定 compose で `wiki-js` が host 公開されない
   こと、(b) Helm の `wikijs.ingress.enabled` 既定が `false`（stg/prod で Ingress を生やさない）ことを検証し、
   公開の混入を多層防御として検出する。

## 理由

- ABAC ゲートウェイ迂回経路を既定で塞ぐことで、dev でも「認可は本システムが単一真実源」という不変条件を保つ。
- override による opt-in で開発利便（管理 UI アクセス）は維持する。
- 回帰ガードで「たまたま守られている」状態を「機械的に守られている」状態へ引き上げる。

## 結果

- 良い影響: 既定で ABAC 迂回経路が無くなり、公開混入が CI で検出される。
- トレードオフ: dev で Wiki.js 管理 UI に直接アクセスする際は override の明示が必要になる（手順は operations.md）。

## 関連

- Supersedes: なし（IADR-0020 の dev 公開方針を具体化）
- Superseded by: なし
