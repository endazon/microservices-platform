---
title: SC-04 Wiki 閲覧導線（Issue #130）
type: spec
status: draft
related_ids:
  - SC-04
  - UC-07
  - FR-13
author: claude
created: 2026-07-08
updated: 2026-07-08
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/03_usecases/01_usecases.md
---

# 仕様書: SC-04 Wiki 閲覧導線（Issue #130）

> Wave A 5 件目（最終）。実体は Wiki.js。本作業は SPA からの遷移導線・SSO・ABAC 整合の確認。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-13（Wiki 閲覧）
- ユースケース（UC）: UC-07
- 画面（SC）: SC-04 Wiki 閲覧画面
- 関連 ADR: [IADR-0020](../adr/IADR-0020_wiki-js-deployment-abac-gateway.md)（Wiki.js デプロイ・ABAC ゲートウェイ）／ [IADR-0009](../adr/IADR-0009_wiki-browsing-404-hides-existence.md)（存在秘匿）／ [IADR-0033](../adr/IADR-0033_frontend-spa-foundation.md)（SPA 基盤）
- Issue: #130（親 #121）

## 目的・背景

Wiki 閲覧の実体は Wiki.js（ABAC ゲートウェイ経由・Keycloak SSO 済み、#118 の Playwright で表示確認済）。
本作業のスコープは **SPA からの遷移導線**。Wiki.js へは同一 Keycloak セッションでシームレスに遷移し、
到達はゲートウェイ（ABAC）経由に限定される。閲覧権限は Wiki.js/ゲートウェイ側で判定するため、UI は
導線のみを提供し、権限の有無を UI で判定しない（[IADR-0009](../adr/IADR-0009_wiki-browsing-404-hides-existence.md) と整合）。

## 対象範囲

- 対象:
  - `features/sc04-wiki`（`/wiki` ルート・`RequireAuth` のみ）: Wiki.js を開く導線（新規タブ）。
  - 接続先（Wiki 基点 URL）を実行時 config（`appConfig().wikiBaseUrl`）から注入。未設定なら導線を出さず注意書き。
  - ナビ「Wiki」。SC-01/SC-03 の出典（sourceUri）も Wiki ページへ遷移し得る（別導線）。
- 対象外:
  - Wiki.js 本体・SSO/ABAC ゲートウェイ設定（既存・#118/IADR-0020）。BFF/バックエンド変更。

## 設計

- `wikiBaseUrl` を `runtimeConfig` に追加（`config.js.template`・entrypoint の `WIKI_BASE_URL` を追随）。
- `/wiki` ページ: `wikiBaseUrl` があれば `<a target="_blank" rel="noreferrer">Wiki を開く</a>`、無ければ `role="note"` の注意書き。
- SSO: ログイン中の Keycloak セッションでそのまま閲覧（追加ログイン不要）。ABAC: 到達はゲートウェイ経由。

## 受け入れ基準（Issue #130）

- [ ] 画面仕様書が作成され、計画の画面設計・対応 UC と整合している
- [ ] SPA（SC-03 等）から Wiki.js の該当ページへ SSO でシームレスに遷移できる（導線）
- [ ] ゲートウェイ（ABAC）経由でのみ到達できることを確認する（接続先は実行時 config・ゲートウェイ URL）
- [ ] 権限外の情報が表示されない（判定は Wiki.js/ゲートウェイ側。UI は導線のみ）
- [ ] テスト観点が `docs/tests/` へ展開されている

## テスト方針

- 単体（Vitest）: 設定時のみ Wiki リンク表示・未設定は注意書き。`runtimeConfig` の `wikiBaseUrl` 注入・空文字→undefined。
- E2E（Playwright）: 未認証 `/wiki`→`/login`。

## 計画書との差異

- 差異: なし（Wiki 閲覧の実体は Wiki.js。SPA 側は導線に徹する計画方針どおり）。

## 未決事項

- なし（接続先は環境の実行時 config で注入。SC-03 からの文脈引き継ぎ導線は #129 実装時に併せて検討）。
