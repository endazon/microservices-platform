---
title: SC-07 変換ジョブ テスト仕様書
type: test-spec
status: completed
related_ids:
  - SC-07
  - UC-06
  - FR-12
author: claude
created: 2026-07-09
updated: 2026-07-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
related_specs:
  - "../screens/SC-07_conversion-jobs.md"
  - "../specs/20260709_issue-133_sc07-conversion-jobs.md"
---

# テスト仕様書: 変換ジョブ（SC-07）

## バックエンド（ConversionService・xUnit）

読み取りモデル: `ConversionJobStoreTests.cs`
| # | 観点 | 検証内容 | ケース |
| --- | --- | --- | --- |
| 1 | 開始 | processing・試行 1 | `Start_marks_job_processing_with_attempt_one` |
| 2 | 成功 | 文書 ID・Markdown 記録 | `Succeed_records_document_and_markdown` |
| 3 | 失敗 | エラー記録 | `Fail_records_error` |
| 4 | 絞り込み | status でフィルタ | `List_filters_by_status` |
| 5 | 再変換 | queued に戻し原本返却 | `PrepareRetry_requeues_and_returns_original_event` |
| 6 | 未知再変換 | null | `PrepareRetry_returns_null_for_unknown_job` |
| 6b | 失敗以外は再変換不可 | 成功ジョブは null・状態不変 | `PrepareRetry_returns_null_for_non_failed_job` |
| 7 | 再試行 | 試行回数加算 | `Start_again_increments_attempts` |

エンドポイント: `ConversionJobEndpointTests.cs`
| # | 観点 | 検証内容 | ケース |
| --- | --- | --- | --- |
| 1 | 一覧・絞り込み | 一覧＋?status=failed | `GetList_ReturnsSeededJobs_AndFiltersByStatus` |
| 2 | 個別 | 取得／404 | `GetById_ReturnsJob_Or404` |
| 3 | 再変換 | 202＋queued 化 | `Retry_KnownFailedJob_Returns202_AndRequeues` |
| 3b | 失敗以外は 409 | 成功ジョブへの再変換は 409・状態不変 | `Retry_NonFailedJob_Returns409` |
| 4 | 未知再変換 | 404 | `Retry_UnknownJob_Returns404` |

コンシューマ記録: `RawDocumentFetchedConsumerJobTests.cs`
| # | 観点 | 検証内容 | ケース |
| --- | --- | --- | --- |
| 1 | 成功記録 | succeeded を記録 | `Consume_success_records_succeeded_job` |
| 2 | 失敗記録＋再送出 | failed を記録し例外再送出（リトライ保持） | `Consume_failure_records_failed_job_and_rethrows` |

## BFF（xUnit）

`BffConversionEndpointTests.cs`
| # | 観点 | 検証内容 | ケース |
| --- | --- | --- | --- |
| 1 | 一覧 | admin で一覧 | `GetList_AsAdmin_ReturnsJobs` |
| 2 | 絞り込み | ?status=failed 透過 | `GetList_FiltersByStatus` |
| 3 | 運用者許可 | operator も可 | `GetList_AsOperator_IsAllowed` |
| 4 | ロール制限 | 非特権 403 | `GetList_AsNonPrivilegedRole_IsForbidden` |
| 5 | 無認証 | 401 | `GetList_WhenAnonymous_IsUnauthorized` |
| 6 | 不在 | 404 透過 | `GetById_WhenMissing_Returns404` |
| 6b | 後段障害の可視化 | 一覧は後段障害を空へ縮退せず伝播（運用画面の誤認防止・レビュー #172） | `GetList_WhenBackendFails_SurfacesFailure_NotEmptyList` |
| 6c | 後段不達→502 | 後段不達（例外）時に catch 分岐で 502 へ縮退（レビュー #172） | `GetList_WhenBackendUnreachable_Returns502` |
| 7 | 再変換 | 202 中継 | `Retry_AsAdmin_Returns202` |
| 8 | 未知再変換 | 404 透過 | `Retry_WhenJobUnknown_Passes404Through` |

## フロントエンド（Vitest + Testing Library）

`ConversionJobsPage.test.tsx`
| # | 観点 | 検証内容 | ケース |
| --- | --- | --- | --- |
| 1 | 一覧・遷移 | 状況・エラー表示、成功→SC-03 リンク | `lists jobs with status and error, and links a succeeded job to its document` |
| 2 | 人手補正 | 失敗の再変換 POST | `retries a failed job (human correction)` |
| 3 | 絞り込み | status クエリ付き取得 | `filters by status` |
| 4 | 異常系 | 取得失敗で alert | `shows an alert when the list fails to load` |

## ロール・存在秘匿の担保

- BFF は admin/operator 限定（4/5 で 403/401）。フロントは `RequireRole` で `/conversions` を出し分け。
- 失敗記録後に例外再送出で MassTransit の再試行→デッドレターを保持（コンシューマ 2）。

## 実行

- `dotnet test src/Services/ConversionService/tests/ConversionService.Worker.Tests`
- `dotnet test src/Bff/KnowledgePlatform.Bff.Tests --filter BffConversionEndpointTests`
- `npm run test -- src/features/sc07-conversions` / `npm run test:coverage`
