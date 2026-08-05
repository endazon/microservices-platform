---
title: 変換ジョブ 画面仕様書
type: screen-spec
status: completed
related_ids:
  - SC-07
  - UC-06
  - FR-12
author: claude
created: 2026-07-09
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
related_specs:
  - "../adr/IADR-0128_conversion-retry-admin-only-and-downstream-posture.md"
  - "../specs/20260805_issue-501_retry-admin-only.md"
  - "../adr/IADR-0042_conversion-job-read-model.md"
  - "../specs/20260709_issue-133_sc07-conversion-jobs.md"
  - "../tests/SC-07_conversion-jobs.md"
---

# 画面仕様書: 変換ジョブ（SC-07）

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: **SC-07 変換ジョブ画面**（[05_screens/01_screens.md](../../planning/projects/microservices-platform/05_screens/01_screens.md) §SC-07・遷移図 `SC06 → SC07`）
- 関連ユースケース（UC）: **UC-06**（変換・正規化の状況確認・人手補正）
- 関連機能要求（FR）: **FR-12**（文書正規化）

## 画面概要・目的
> **［2026-08-04 / #490］ルートは `/admin/conversions` である。** SPA のルータを TanStack Router へ差し替えるにあたり、ルートパスを [05_screens §共通シェル](../../planning/projects/microservices-platform/05_screens/01_screens.md)「ルートパス（wireframe の URL バー準拠）」の値へ是正した（[[IADR-0124]] 決定 6）。画面内容そのものの計画準拠は #452 が担う。


変換状況・失敗ジョブの一覧を表示し、失敗ジョブの人手補正（再変換）を行う運用画面。SC-06（データソース管理）からの遷移先（取り込み→変換の運用フロー）。

- アクセス: **platform-admin/operator 限定**（[IADR-0042](../adr/IADR-0042_conversion-job-read-model.md)・管理系ロール方針は [[IADR-0039]]）。権限外はルート・ナビとも非表示。サーバ側 `/bff/conversion/jobs` も同ロール。**ただし再変換（`retry`）だけは admin のみ**（2026-08-04 確定 / [[IADR-0128]]。下表・下記追記）。

## データソース（BFF 境界）

| 用途 | エンドポイント | 認可 | 応答 |
| --- | --- | --- | --- |
| 一覧 | `GET /bff/conversion/jobs?status=` | admin/operator（403/401） | `ConversionJobDto[]` |
| 個別取得 | `GET /bff/conversion/jobs/{id}` | 同上 | `ConversionJobDto` / 404 |
| 人手補正（再変換） | `POST /bff/conversion/jobs/{id}/retry` | **admin のみ**（operator は 403・無認証 401） | 202 / 404 / 409（失敗以外は再変換不可） |

> **［2026-08-05 追記・再変換は管理者ロール限定（#501 / [[IADR-0128]]）］**
> 計画 [05_screens §SC-07 §データソース](../../planning/projects/microservices-platform/05_screens/01_screens.md)（**2026-08-04 確定**）の
> 「**再変換の実行権限は管理者ロールに限る**。本画面のアクセス制御と API の権限を揃える」に追随し、
> `retry` の認可を `platform-admin` のみへ絞った（[[IADR-0128]] 決定 1・[[IADR-0042]] 決定 3 への［追記］）。
> **照会（一覧・個別取得）は admin/operator のまま据え置く** —— 閲覧ロールの裁定は planning#198 提案 8 で継続中であり、
> グループごと絞ると未確定の閲覧まで変わるためである（[[IADR-0128]] 決定 2）。
> 画面側の再変換ボタンを管理者のみにする作業は **#503 / PR #508** が持つ。

- `ConversionJobDto = { id, sourceId, sourceType, originalPath, status, error?, documentId?, markdownUri?, attempts, createdAt, updatedAt }`
- `status`: queued / processing / succeeded / failed。
- 変換状況は ConversionService の読み取りモデル（インメモリ MVP・[[IADR-0042]]）に由来する。

## 主要素・振る舞い

- 状況フィルタ（すべて／失敗／処理中／待機／成功）→ 選択で再取得。
- 一覧テーブル（原本・種別・状況・試行回数・エラー・更新・操作）。
- 操作: 失敗ジョブに「再変換」（人手補正。原本イベント再発行）。成功ジョブは生成文書（SC-03）へ遷移。
- 再変換は**失敗ジョブのみ**（UI は失敗時のみボタン表示。API 側でも失敗以外は 409 で拒否し、処理中の二重発行・成功済みの再処理を防ぐ）。
- 通知（`role="status"`）／エラー（`role="alert"`）。0 件は中立表示。

## 実装

- ConversionService: `Foundation/Jobs/ConversionJobStore.cs`、`Foundation/Endpoints/ConversionJobEndpoints.cs`、コンシューマの記録。
- BFF: `Foundation/Endpoints/ConversionBffEndpoints.cs`。
- フロント: `src/knowledge/frontend/src/features/sc07-conversions/ConversionJobsPage.tsx` / `index.tsx`。
- 契約: `KnowledgePlatform.Shared.Contracts/Dtos/ConversionJobDto.cs`。
- テスト観点は [tests/SC-07_conversion-jobs.md](../tests/SC-07_conversion-jobs.md)。

## 計画ギャップ（フィードバック）

- 変換ジョブの照会・再変換 API はバックエンドに存在しなかった（本 PR で追加）。計画（UC-06/05_screens）へ API 明示を環流する（[[IADR-0042]] フォローアップ）。
