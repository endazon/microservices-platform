---
title: SC-05 文書管理画面実装（Issue #131）
type: spec
status: completed
related_ids:
  - SC-05
  - UC-03
  - FR-06
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
---

# 仕様書: SC-05 文書管理（Issue #131）

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: SC-05 文書管理画面
- ユースケース（UC）: UC-03（文書管理）
- 機能要求（FR）: FR-06（文書管理）、FR-05（ABAC）、FR-09（属性整合）
- 関連 ADR: [[IADR-0041]]（本 PR で作成・書き込み BFF とスコープ内限定）、[[IADR-0038]]（読み取り BFF）、[[IADR-0039]]（管理系ロール）
- Issue: #131（親 #121）

## 目的・背景

SPA 上に SC-05 を実装する。読み取り側（`/bff/documents`）は SC-03（#129）で集約済み。本 PR で**書き込み側**（作成・更新・公開・アーカイブ・削除）を BFF に追加する（Wave B 方針）。管理系のため admin/operator 限定、既存文書操作は ABAC スコープ内限定（[[IADR-0041]]）。

## 対象範囲

- 対象:
  - BFF: `DocumentBffEndpoints` に書き込みサブグループ（`RequireRole(admin, operator)`）。作成・更新・公開・アーカイブ・削除を提供。既存文書操作は `FetchAuthorizedAsync` でスコープ内確認→404 秘匿、作成は scope 解決成功を要件（403 deny-by-default）。検証 400・楽観ロック 409 を透過。BFF ローカル request record。メタデータ専用 PATCH は SC-05 では未使用のため実装しない（過剰実装回避。レビュー #171 指摘対応）。
  - フロント: `features/sc05-documents`（`/documents`・`RequireRole(admin, operator)`・ナビ）。一覧＋作成＋編集（楽観ロック）＋公開／アーカイブ／削除。詳細・版履歴は SC-03 へ遷移。409 通知＋再読込。公開ボタンは未公開状態（draft/normalized）のみ表示。
  - DocumentService: 公開の状態遷移ガード（archived からの再公開を `Document.Publish()` のドメイン不変条件および `/documents/{id}/publish` の 409 で拒否＝多層防御。レビュー #171 指摘対応）。
  - テスト: BFF（xUnit：ロール 403/401・scope 外 404・作成 deny 403・検証 400 透過・競合 409 透過・公開・削除）、DocumentService（状態遷移ガード：archived 公開の例外/409・normalized 公開許可）、Vitest（一覧・作成必須属性・公開・編集 expectedVersion・409 通知・異常系・archived/normalized の公開ボタン出し分け）。
  - ドキュメント: 本仕様書・画面仕様書・テスト仕様書・IADR-0041。
- 対象外:
  - 文書本文（Markdown）の編集 UI（本文は変換パイプライン由来。本画面はメタデータ／状態管理に集中）。
  - 詳細・版履歴の再実装（SC-03 に委譲）。
  - 作成時の設定属性が自スコープ内かの厳密検証（役割＋scope 解決で最小防御。IADR-0041 §根拠）。

## 受け入れ基準（Issue #131）との対応

- [x] 画面仕様書を作成（[SC-05_document-management.md](../screens/SC-05_document-management.md)）— 計画・UC-03 と整合。
- [x] 文書の作成・更新・削除・バージョン参照が画面から行える（版参照は SC-03 へ遷移）。
- [x] 属性／タグの設定が ABAC 属性（FR-05/FR-09）と整合する（機密区分必須・許可値準拠）。
- [x] 権限外の情報が表示されない（admin/operator 限定・スコープ外 404 秘匿）。
- [x] テスト観点を `docs/tests/SC-05_document-management.md` へ展開。

## 実装判断

- 書き込みにも読み取りと同じ ABAC 境界を課す（閲覧不可＝変更不可）。[[IADR-0041]] §決定 2。
- 楽観ロック競合（409）は `ApiError.status===409` で判定（develop 基盤に依存。SC-09 の `ApiError.details` 拡張とはブランチ独立）。
