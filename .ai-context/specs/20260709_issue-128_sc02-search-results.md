---
title: SC-02 検索結果一覧画面実装（Issue #128）
type: spec
status: completed
related_ids:
  - SC-02
  - UC-01
  - FR-03
  - FR-05
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/03_usecases/01_usecases.md
---

# 仕様書: SC-02 検索結果一覧（Issue #128）

> 実装着手前に作成する。フロントエンド各画面フェーズ Wave B。

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: SC-02 検索結果一覧
- ユースケース（UC）: UC-01（検索・閲覧）
- 機能要求（FR）: FR-03（ハイブリッド検索）、FR-05（ABAC）
- 関連 ADR: [IADR-0009](../adr/IADR-0009_wiki-browsing-404-hides-existence.md)（存在秘匿）、[IADR-0033](../adr/IADR-0033_frontend-spa-foundation.md)（SPA 基盤）、[IADR-0037](../adr/IADR-0037_llm-sse-streaming.md)（SC-01 検索実装で `/bff/search` を新設）
- Issue: #128（親 #121）

## 目的・背景

SPA 基盤上に SC-02 を feature として実装する。ハイブリッド検索の結果を一覧表示し、各件から SC-03（文書詳細）へ**内部遷移**する。BFF の `/bff/search` は SC-01（#127/PR #161）で既に実装済み（ABAC スコープをサーバ側で解決し deny-by-default で空を返す）であり、本画面は**フロントエンドのみ**で成立する（Wave B の中で BFF ギャップの無い唯一の画面）。

SC-01 の検索結果は出典を外部 URI で開くのに対し（`// SC-03 文書詳細は #129 実装後に内部遷移へ` と留保されていた）、本画面は SC-03 実装済みを前提に内部導線 `/documents/:id` を提供する。

## 対象範囲

- 対象:
  - feature `features/sc02-results`（`/results` ルート、`RequireAuth` のみ・ナビ「検索結果一覧」）。
  - `POST /bff/search` を呼ぶ検索フォーム＋結果一覧（タイトル→SC-03 内部リンク・スニペット・スコア・属性・タグ）。
  - `?q=` ディープリンク（マウント時・URL 変化時に自動検索、送信時に URL 反映）。
  - deny-by-default／0 件の中立表示（存在秘匿）、loading／error 表示。
  - テスト: Vitest（検索・一覧・SC-03 リンク・?q= 自動検索・空表示・異常系）。
  - ドキュメント: 本仕様書・画面仕様書（SC-02）・テスト仕様書（SC-02）。
- 対象外:
  - BFF・バックエンドの変更（`/bff/search` は既存を再利用。scope はクライアントから送らない）。
  - AI 回答・フィードバック（SC-01 の責務）。
  - ファセット絞り込み・ページング（計画外。将来拡張）。

## 受け入れ基準（Issue #128）との対応

- [x] 画面仕様書を作成（[SC-02_search-results.md](../../docs/screens/SC-02_search-results.md)）— 計画の画面設計・UC-01 と整合。
- [x] 検索結果が一覧表示され、閲覧権限のある文書のみ表示される（`/bff/search` の deny-by-default）。
- [x] 権限外の情報が表示されない（ABAC・存在秘匿。0 件と権限外を同一表示）。
- [x] テスト観点を `docs/tests/SC-02_search-results.md` へ展開。

## 実装判断

- 新規 BFF・バックエンド・IADR は不要（既存契約の再利用のみ）。scope 非送信・404/空秘匿は既存方針（[IADR-0009](../adr/IADR-0009_wiki-browsing-404-hides-existence.md)）に従う。
- `topK=20`（一覧向けに SC-01 の 10 より広め。BFF 側 `MaxTopK=50` の範囲内）。
