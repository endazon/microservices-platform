---
title: IADR-0041 文書管理（書き込み）の BFF 集約とスコープ内限定・楽観ロック透過
type: impl-adr
status: Accepted
related_ids:
  - SC-05
  - UC-03
  - FR-06
  - ADR-0004
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
---

# IADR-0041: 文書管理（書き込み）の BFF 集約とスコープ内限定・楽観ロック透過

- 状態: Accepted
- 日付: 2026-07-09
- 決定者: claude（実装）

## 起点・関連

- 関連する計画書 ID: SC-05（文書管理）／ UC-03 ／ FR-06（文書管理）・FR-05（ABAC）
- 関連 ADR: [[IADR-0038]]（文書閲覧の BFF 側 ABAC ゲーティング）／ [[IADR-0009]]（存在秘匿）／ [[IADR-0039]]（管理系画面のロールゲーティング）／ ADR-0004（ABAC）
- 関連仕様書: `docs/screens/SC-05_document-management.md`

## コンテキストと課題

SC-05 は文書の CRUD・属性／タグ・公開／アーカイブを行う。読み取り側（`/bff/documents` 一覧・詳細・本文・版履歴）は SC-03（[[IADR-0038]]）で BFF 集約済みだが、**書き込み側は未プロキシ**である。DocumentService の書き込み（POST/PUT/PATCH/publish/archive/DELETE）は認可なしで、楽観ロック（`ExpectedVersion` 不一致=409）・タイトル必須検証（400）を持つ。

決めること:
1. 書き込みの対象ロール。
2. 書き込みに ABAC をどう適用するか。
3. 検証・競合の応答をどう扱うか。

## 決定

1. **書き込みは platform-admin/operator に限定する**（[[IADR-0039]] の管理系ゲーティングに従う）。読み取り（SC-02/03 用）は従来どおり無制限（ABAC スコープのみ）。BFF は `/bff/documents` の**書き込みサブグループのみ** `RequireRole(admin, operator)` で保護し、既存の読み取りマッピングは変更しない。フロントは `RequireRole` で `/documents`（管理一覧）を出し分ける。
2. **既存文書への操作はスコープ内限定**とする。更新・メタ更新・公開・アーカイブ・削除は、対象文書が利用者の ABAC スコープ内であることを先に確認し（[[IADR-0038]] の `FetchAuthorizedAsync` を再利用）、スコープ外・不在はいずれも 404 で秘匿する（**閲覧できない文書は変更もできない**）。新規作成は許可ポリシーがあること（`ResolveAsync` 成功）を要件とし、無ければ 403（deny-by-default）。
3. **検証（400）・楽観ロック競合（409）は後段の応答を透過**する。BFF は status・content-type・本文をそのまま返し、SPA は 409 を検出して「競合（版が変わった）」を通知し最新を再読込する。タイトル必須（400）も透過する。

## 根拠 / 代替案

- **スコープ内限定 vs 役割のみ**: 役割（admin/operator）だけでは、operator が自分の閲覧範囲外（他部門機密）の文書まで操作できてしまう。読み取りと同じ ABAC 境界を書き込みにも課すことで一貫性と最小権限を保つ。
- **透過 vs DTO 変換**: 楽観ロック競合・検証エラーの詳細を SPA が扱えるよう透過する。SC-05 は develop 基盤（`ApiError` は 409 を `status` で識別可能）に載るため、SPA は `err.status === 409` で競合を判定する（[[IADR-0040]] の `ApiError.details` 拡張には依存しない＝ブランチ独立）。
- **作成時の属性スコープ厳密化は将来課題**: 作成は許可ポリシー有無（scope 解決成功）で 403 判定に留め、設定属性が自スコープ内かの厳密検証までは行わない（過剰実装回避。役割＋scope 解決で十分な最小防御）。

## 影響

- BFF `DocumentBffEndpoints` に書き込みサブグループ（create/update/metadata/publish/archive/delete）を追加。BFF ローカル request record（`DocumentCreateRequest` 等）。
- フロント `features/sc05-documents`（`/documents`・admin/operator 限定・作成/編集/公開/アーカイブ/削除・409 通知）。
- 詳細・版履歴は SC-03（`/documents/:id`）へ委譲（本画面は管理操作に集中）。
