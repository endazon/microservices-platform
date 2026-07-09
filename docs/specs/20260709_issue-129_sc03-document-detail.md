---
title: SC-03 文書詳細／プレビュー画面実装（Issue #129）
type: spec
status: draft
related_ids:
  - SC-03
  - UC-01
  - UC-07
  - FR-06
  - FR-12
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
---

# 仕様書: SC-03 文書詳細／プレビュー（Issue #129）

> 本仕様書は実装着手前に作成する。フロントエンド各画面フェーズ Wave B の 1 件目。

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: SC-03 文書詳細／プレビュー
- ユースケース（UC）: UC-01（検索・閲覧）、UC-07（Wiki 閲覧）
- 機能要求（FR）: FR-06（文書管理）、FR-12（変換・正規化）、FR-05（ABAC）
- 関連 ADR: [[IADR-0038]]（BFF 側 ABAC ゲーティング・本文取得）、[[IADR-0009]]（存在秘匿）、[[IADR-0033]]（SPA 基盤）
- Issue: #129（親 #121）

## 目的・背景

SPA 基盤上に SC-03 を feature として実装する。検索結果一覧（SC-02）・文書管理（SC-05）から `/documents/:id` へ遷移し、正規化文書（Markdown）本文とメタデータ（状態・版・属性・タグ）、版履歴、出典元リンク、SC-04（Wiki）への遷移導線を表示する。

バックエンドの `/documents/*`（DocumentService）は BFF に未プロキシだったため、本 PR で読み取り側の BFF 集約（`/bff/documents`）を新設する。単一 ID 取得には ABAC 経路が無いため、BFF 集約点でスコープ解決＋属性照合し、権限外・不在を 404 で秘匿する（[[IADR-0038]]）。

## 対象範囲

- 対象:
  - BFF: `/bff/documents`（一覧）・`/bff/documents/{id}`（詳細）・`/bff/documents/{id}/versions`（版履歴）・`/bff/documents/{id}/content`（本文）。ABAC スコープ解決＋属性照合、404 秘匿、本文はオブジェクトストレージからサーバサイド取得（未配備時プレースホルダ）。
  - BFF 共通化: `BffScopeResolver`（スコープ解決・属性抽出・単一文書照合）を新設し、`SearchBffEndpoints` もこれへ寄せる（挙動不変）。
  - 契約: `DocumentContentDto`（Shared.Contracts）。
  - feature `features/sc03-document`（`/documents/:id` ルート、`RequireAuth` のみ／ロール限定なし・ナビ非表示）。
  - メタデータ・本文（Markdown 原文の等幅表示）・版履歴・出典元リンク・Wiki 導線。404 の中立表示（存在秘匿）、本文領域の独立縮退。
  - テスト: BFF（xUnit：認可・秘匿・一覧絞り込み・本文縮退・版履歴）、Vitest（描画・縮退・404 中立・異常系）。
  - ドキュメント: 本仕様書・画面仕様書・テスト仕様書・IADR-0038。
- 対象外:
  - 文書の作成・更新・削除（書き込み側は SC-05・#131 で `/bff/documents` に追加）。
  - Markdown の HTML レンダリング（ライブラリ非導入。原文を安全表示）。
  - Wiki の文書別ディープリンク（SC-04 の `/wiki` 内部ルートへ遷移。per-document URL は未対応）。

## 設計

### API 境界（BFF）
- `GET /bff/documents` → `DocumentDto[]`（権限内のみ）。
- `GET /bff/documents/{id}` → `DocumentDto`（スコープ外・不在は 404）。
- `GET /bff/documents/{id}/versions` → `DocumentVersionDto[]`（同上）。
- `GET /bff/documents/{id}/content` → `DocumentContentDto{ id, title, markdown, sourceUri }`（同上、本文はストレージ or プレースホルダ）。
- ABAC: `BffScopeResolver.ResolveAsync`（deny-by-default・クライアント Scope 無視）＋ `Matches`（キー間 AND・値集合内 OR）。

### フロント
- `apiFetch` で詳細（notFound ゲート）・本文（独立縮退）・版履歴（補助・失敗許容）を取得。
- 本文は `<pre style="white-space:pre-wrap">` で原文表示（XSS を避け HTML 描画しない）。
- 出典元は `sourceUri`（http(s) のみリンク化、storage:// 等はコード表記）。`wikiBaseUrl` 設定時に SC-04 `/wiki` への内部リンクを出す。

### 権限
- ロール限定なし（一般社員が閲覧）。ABAC はサーバ側（BFF）で適用。UI は権限有無を開示しない（404 → 中立表示）。

## 受け入れ基準

Issue #129 より転記:

- [ ] 画面仕様書が作成され、計画の画面設計・対応 UC と整合している → `docs/screens/SC-03_document-detail.md`
- [ ] 正規化文書（Markdown）が表示され、出典元リンクが機能する
- [ ] 権限外の情報が表示されない（ABAC・存在秘匿の画面適用 → 404 中立表示・一覧絞り込み）
- [ ] テスト観点が `docs/tests/` へ展開されている → `docs/tests/SC-03_document-detail.md`

## テスト方針

- BFF（xUnit + WebApplicationFactory）: スコープ許可時の取得、非許可時の 404/空、属性不一致時の 404、不在時の 404、一覧の権限内絞り込み、本文のプレースホルダ縮退＋sourceUri、版履歴。
- 単体（Vitest + Testing Library）: メタ・本文・版履歴の描画、Wiki 導線、404 中立、5xx alert、本文領域の独立縮退。
- `/verify` 相当（backend build/test、frontend typecheck/lint/build/test）で合否確認。

## 計画書との差異

- 差異なし（計画の SC-03 定義に沿う）。本文の HTML レンダリングは行わず原文表示とする点は実装上の判断（[[IADR-0038]] 対象外に明記）。

## 未決事項

- Wiki の文書別ディープリンク（per-document URL）は未対応。SC-04 の Wiki.js 側スラッグ規約が定まれば接続する。
