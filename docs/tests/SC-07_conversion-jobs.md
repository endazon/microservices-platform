---
title: SC-07 変換ジョブ テスト仕様書
type: test-spec
status: completed
created: 2026-07-09
updated: 2026-08-21
author: claude
---
<!-- trace:
ids: [FR-12, SC-03, SC-06, SC-07, UC-06]
adrs: [ADR-0031]
iadrs: [IADR-0009, IADR-0035, IADR-0042, IADR-0044, IADR-0127, IADR-0128, IADR-0132, IADR-0154, IADR-0157, IADR-0162]
specs: [01_screens, 01_usecases, 20260805_issue-501_retry-admin-only, 20260805_issue-503_sc05-08-admin-screens, IADR-0127_sc07-retry-admin-only-and-derived-states, IADR-0128_conversion-retry-admin-only-and-downstream-posture, INDEX, SC-07_conversion-jobs]
issues: [#533, #543, #553, #651, #658, planning#198]
-->

# テスト仕様書: 変換ジョブ

> **［2026-08-05 / #503］計画の 2026-08-04 確定（4 状態モデル・状態フィルタ・再変換の管理者ロール限定・
> 同一ジョブの直列化）へ追随して画面側を全面改訂した。**
>
> **［2026-08-05 / #501］API 側（BFF・下流のネットワーク分離）の観点を復帰・追補した。**
> #503 の全面改訂はフロントエンドの表だけを残してバックエンドの表を落としていたが、
> 当該テストは実在し続けており（`ConversionJobStoreTests` / `ConversionJobEndpointTests` /
> `BffConversionEndpointTests`）、**#501 はここへ権限テストを足す**。落としたままにすると
> 「画面のテストしか無い」と読めてしまうため復帰させた（§BFF・§デプロイ・§バックエンド）。

対象（画面）: `src/knowledge/frontend/src/features/sc07-conversions/`
テスト: `jobStatus.test.ts`（純関数）／ `ConversionJobsPage.test.tsx`（Vitest + Testing Library）／
導線は `src/knowledge/frontend/src/features/adminFlow.test.tsx`／
E2E は `src/platform/frontend/e2e/sc07-conversions.smoke.spec.ts`

対象（API）: `src/platform/backend/Bff/Platform.Bff.Tests/BffConversionEndpointTests.cs` ／
`src/knowledge/backend/Services/ConversionService/tests/ConversionService.Worker.Tests/` ／
`src/knowledge/backend/Tests/Knowledge.IntegrationTests/Deployment/NetworkIsolationTests.cs`

## 起点となる計画書（トレーサビリティ）

- 画面: 変換ジョブ画面 ／ ユースケース: **文書を正規化変換する** ／ 機能要求: 原本の正規化変換
- **計画の確定事項（2026-08-04。計画側の画面設計 §変換ジョブ画面 §データソース）を受け入れ基準として写像する。**
- 連携: **#501**（API 側の管理者ロール強制の突合。**解消済み**）。
  **本書は画面側と API 側の両方を固定する** —— 権限は片側だけを固定しても実効境界にならない。

## 計画の確定事項 → テストの写像

| 計画の確定 | テスト |
| --- | --- |
| ジョブ状態モデルは **4 値** | `covers exactly the four statuses the plan fixed` ／ `maps %s to a labelled badge`（4 件）／ `lists jobs with the four-value status model` |
| **人手補正の 2 ペイン編集（Phase 1）** | **［2026-08-10 / #651］実装した**——§テストケース 14〜21。契約は #543（人手補正 Phase 1 の実装判断）、画面の決定は人手補正 UI の実装 ADR による |
| **補正のあるジョブの再実行は明示確認を求める** | **［2026-08-10 / #651］実装した**——**409 を受けてから**確認を出す（§テストケース 18・19） |
| デッドレターの表示は `failed` の**内訳** | **画面では実装しない**（画面側が未着手。理由は画面仕様書 §実装しない要素 (b)）。**契約と後段は #533 で固定した**——§バックエンド の読み取りモデル 8〜12・エンドポイント 4・コンシューマ 3〜4 |
| 照会 API は `GET /jobs` 相当・**状態でのフィルタ**を備える | `sends the status filter to the query API` ／ `starts with the "all" filter so the first view is not narrowed` |
| 再変換 API は `retry` 相当 | `lets an administrator retry a failed job` |
| **再変換の実行権限は管理者ロールに限る**（「画面と API の権限を揃える」） | 画面: `lets an administrator retry a failed job` ／ **`hides the retry button from an operator and says why`**。API: **`Retry_AsOperator_IsForbidden`**（403）／ `Retry_WhenAnonymous_IsUnauthorized`（401）／ `Retry_AsAdmin_Returns202`（§BFF 7 / 7b / 7c） |
| 回数上限は設けない。**同一ジョブの再変換は直列化**し、実行中（`processing`）の要求は拒否する | `allows retry only for failed jobs`（純関数）／ `offers no retry for jobs that are not failed`（画面）／ **`explains the 409 rejection as a serialisation conflict`**（サーバ側の拒否） |

## ユースケースのフロー → テストの写像

| 正規化変換のフロー | 画面での現れ方 | テスト |
| --- | --- | --- |
| **代替（2026-08-04 追記）. 変換ジョブの状況を照会する** | 一覧 ＋ 状態フィルタ | `lists jobs with the four-value status model` ／ `sends the status filter to the query API` |
| **代替（2026-08-04 追記）. 失敗した変換を再実行する** | `failed` の行の再変換ボタン（管理者のみ） | `lets an administrator retry a failed job` |
| **例外. 恒久失敗は再試行し、継続失敗はデッドレターへ送る** | `failed` として表示する（**画面では**内訳を区別しない） | 画面: `lists jobs with the four-value status model`（`failed` の表示）。**契約・後段**: `Consume_failure_exhausting_retries_marks_dead_lettered` ／ `Fail_at_attempt_limit_marks_dead_letter_without_changing_status` |
| 基本 1〜4（受領・pandoc・図の LLM コード化・登録） | **写像しない**（ワーカー側の責務） | — |

## テストケース

| # | 観点 | 起点 | 検証内容 |
| --- | --- | --- | --- |
| 1 | 一覧 | 正規化変換の代替フロー | `GET /bff/conversion/jobs` を呼び、ジョブ ID・原本・**状態（4 値）**・備考を表示する |
| 2 | 状態フィルタ | 計画確定 | 選択で `?status=failed` を送る。**既定は「すべて」** |
| 3 | **再変換（管理者）** | **計画確定 2026-08-04** | `POST …/retry` を呼び、受付を伝える |
| 3-b | **再取得** | 管理画面の実装方針（決定 5） | 再変換の成功後に一覧を取り直す（`invalidateQueries` のみ） |
| 4 | **再変換（運用者に出さない）** | **計画確定 2026-08-04** / 管理画面の実装方針（決定 1） | 画面は見えるがボタンが無く、**「再変換は管理者のみ実行できます」と理由が出る**。**先に失敗ジョブの行が描かれていることを確かめてから**無いことを見る |
| 5 | 直列化（画面側） | 計画確定 | `failed` 以外の行に再変換を出さない |
| 6 | **直列化（サーバ側 409）** | 計画確定 | `not_retryable` を「実行中、または失敗以外の状態です」と伝える（`role="alert"`・`warning`） |
| 7 | 変換結果への導線 | 遷移図 `SC07 → SC03` | `succeeded` かつ `documentId` があれば `/docs/$id` へリンクする |
| 8 | **異常系（縮退しない）** | 変換ジョブ読み取りモデルの実装判断 | 取得失敗を `role="alert"` で出し、**「ジョブはありません」へ寄せない** |
| 9 | 0 件 | — | 「該当する変換ジョブはありません。」 |
| 10 | **権限別の出し分け** | ロールベース・ナビゲーションと存在秘匿 | ロールを持たない利用者には画面が無い（`NotFound`）。**要求も出さない** |
| 11 | 導線 | 遷移図 | 「← データソース管理へ戻る」が `/admin/sources` を指す |
| 12 | **Phase 2 が射程外**（実装しない要素） | 画面仕様書 §hi-fi 対応 #10・#12 / `05_screens:330` | **［2026-08-10 / #651］この項目は反転した。** 従前は「2 ペインが**無い**こと」を理由「保存先の契約が無い」とともに固定していたが、契約は #543・画面は #651 で載り、**その理由は成り立たなくなった**。現在は「**図ごとの編集欄は在り、変換結果 Markdown 全体の編集欄は無い**」を固定する——本口はコード片しか受け付けないため、全体編集欄を置くと契約を超える |
| 13 | **導出標識**（縮退・補正あり・Mermaid n図） | 人手補正 UI の実装 ADR 決定 3 / 計画側の画面設計 `:320` | `diagramsRetained` / `hasCorrection` / `diagramsCoded` から導出する。**`status` は 4 値のまま**（縮退したジョブは `succeeded`） |
| 14 | **人手補正（管理者）** | 計画側の画面設計 `:314` / 人手補正 Phase 1 の決定 6 | 縮退した図のある行から 2 ペインを開き、コードを投稿して成功を伝える。**対象は `coded === false` の図だけ** |
| 15 | **人手補正（運用者に出さない）** | 同上 / 管理画面の実装方針（決定 1） | ボタンが無く「人手補正は管理者のみ実行できます」と理由が出る。**先に行が描かれていることを確かめてから**無いことを見る |
| 16 | **★ 右ペインの画像が解析層を通る** | 人手補正 UI の実装 ADR 決定 1・5 | `/image` を呼び、**`<img>` まで届く**（オブジェクト URL）。`storage://` を `src` に入れない。**「要求が飛んだ」だけを見てはいけない**——変異試験で、それだけでは `bffFetch` の欠陥を素通りすると実測した |
| 17 | **画像 404 の縮退** | 権限外は 404 とする存在秘匿 | 「元の画像を表示できません。」へ倒す。**空白にしない**（読み込み中と区別が付かなくなる） |
| 18 | **補正が失われる確認（409 駆動）** | 計画側の画面設計 `:313` / 人手補正 UI の実装 ADR 決定 2 | **409 `corrections_would_be_lost` を受けてから**確認を出し、`correctedFigures` を件数として示す。確認前に `discardCorrections` を送らない。確認後は `?discardCorrections=true` で再送する。**確認の同定は role ではなく「破棄ボタンが在ること」で行う**——本確認は非モーダルなので `alertdialog` を名乗らない（同決定 2） |
| 19 | **`not_retryable` との区別** | 同決定 2 | 同じ 409 でも**拒否であって確認ではない**。取り違えると実行中のジョブに補正破棄を勧めることになる |
| 20 | **直近の操作の結果だけを出す** | 管理画面の実装方針（決定 7） | **retry の失敗 → correct の成功**で古い失敗バナーが消える。**別々のミューテーションでしか再現しない**——同じミューテーションを 2 回叩く形では TanStack Query が自分の `isError` を戻すため、`reset` を外しても緑になる（実測） |
| 21 | 図の取得失敗・補正対象なし | — | 空パネルを見せず、それぞれ理由を出す |
| 22 | ロケール `en` | —| 見出しと状態が英語で描画される |
| 23 | **確認へキーボードで到達できる** | 人手補正 UI の実装 ADR 決定 2 / AI レビュー 🟡 | 確認が出たら**非破壊側（取り消す）へ焦点が移る**（既定の焦点は `Enter` の当たり先でもあり、破壊側に置くと反射的な連打で補正が消える）。確認は表より**前**に描かれ、それを出すボタンは**表の行の中**にあるため、**前方 Tab では届かない**——焦点を移さないと読み上げは届くのに操作手段が無い |

## 純関数（`jobStatus.test.ts`）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| P1 | 値集合 | 計画確定の 4 値と完全一致する |
| P2 | 4 値の写像 | 各値に文言と tone が対で決まる（INDEX 決定 21） |
| P3 | **未知の状態** | 生値をそのまま出す（`—`・「不明」へ丸めない） |
| P4 | 再変換可否 | `failed` のみ `true` |
| P5 | **縮退の導出** | `diagramsRetained > 0`。**フィールドが無い場合は「縮退なし」へ倒す**（**［#658］契約は `required` へ移ったが、防御は残す** —— 応答スキーマの `required` 規則が言う「契約上は必須」と「実行時に必ず来る」は別） |
| P6 | **補正可否** | 縮退した図があること。**権限は混ぜない**（人手補正 UI の実装 ADR 決定 3） |

## 導線（`adminFlow.test.tsx`）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| A | データソース管理 → 変換ジョブ → 文書詳細 | データソース → 変換ジョブ → 変換結果の文書まで 1 本で通る |
| B | 変換ジョブ → データソース管理 | 「← データソース管理へ戻る」で戻れる |

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
| 8 | **試行上限・標識の初期値** | 全ジョブが `MaxAttempts` を持ち、標識は既定で立たない | `Job_carries_max_attempts_and_is_not_dead_lettered_initially` |
| 9 | **上限前の失敗** | 再試行の余地がある失敗に標識は立たない（`failed` の内訳を区別する） | `Fail_without_reaching_attempt_limit_does_not_mark_dead_letter` |
| 10 | **上限到達の失敗** | 標識が立ち、**状態値は `failed` のまま**（4 値モデル不変） | `Fail_at_attempt_limit_marks_dead_letter_without_changing_status` |
| 11 | **再受信で標識が落ちる** | 処理再開で `processing` ＋ 標識 false | `Reprocessing_clears_dead_letter_marker` |
| 12 | **手動再変換で標識が落ちる** | 試行回数が上限を超えていても受け付け、`queued` ＋ 標識 false | `PrepareRetry_clears_dead_letter_marker` |

エンドポイント: `ConversionJobEndpointTests.cs`
| # | 観点 | 検証内容 | ケース |
| --- | --- | --- | --- |
| 1 | 一覧・絞り込み | 一覧＋?status=failed | `GetList_ReturnsSeededJobs_AndFiltersByStatus` |
| 2 | 個別 | 取得／404 | `GetById_ReturnsJob_Or404` |
| 3 | 再変換 | 失敗ジョブは 202（queued 化は store 単体で担保） | `Retry_KnownFailedJob_Returns202` |
| 3b | 失敗以外は 409 | 成功ジョブへの再変換は 409・状態不変 | `Retry_NonFailedJob_Returns409` |
| 3c | **処理中は 409 not_retryable**（2026-08-04 確定「実行中の再変換要求は拒否」・#501 回帰） | `processing` への再変換は 409・本文 `error=not_retryable`・状態不変 | `Retry_ProcessingJob_Returns409NotRetryable` |
| 3d | **標識の HTTP 露出** | 応答に `deadLettered` / `maxAttempts` が載る（状態は `failed` のまま）。**再変換での消滅はここで見ない**——3 と同じレース理由。読み取りモデル 12 で担保 | `GetById_ExposesDeadLetterMarkerAndMaxAttempts` |
| 4 | 未知再変換 | 404 | `Retry_UnknownJob_Returns404` |

コンシューマ記録: `RawDocumentFetchedConsumerJobTests.cs`
| # | 観点 | 検証内容 | ケース |
| --- | --- | --- | --- |
| 1 | 成功記録 | succeeded を記録 | `Consume_success_records_succeeded_job` |
| 2 | 失敗記録＋再送出 | failed を記録し例外再送出（リトライ保持）。**試行上限前なので標識は立たない** | `Consume_failure_records_failed_job_and_rethrows` |
| 3 | **再試行を使い切った失敗** | 本番と同じ試行上限で消費させ、最後の失敗で標識が立つ（`Fault<T>` 発行で待つ） | `Consume_failure_exhausting_retries_marks_dead_lettered` |
| 4 | **試行上限の単一情報源** | 契約の `ConversionJobRetryPolicy.MaxAttempts` が再試行設定（`UsePlatformRetry`）と一致する | `MaxAttempts_contract_constant_matches_platform_retry_policy` |

## BFF（xUnit・#501 で権限テストを追加）

`BffConversionEndpointTests.cs`
| # | 観点 | 検証内容 | ケース |
| --- | --- | --- | --- |
| 1 | 一覧 | admin で一覧 | `GetList_AsAdmin_ReturnsJobs` |
| 2 | 絞り込み | ?status=failed 透過 | `GetList_FiltersByStatus` |
| 3 | 運用者許可 | operator も可 | `GetList_AsOperator_IsAllowed` |
| 4 | ロール制限 | 非特権 403 | `GetList_AsNonPrivilegedRole_IsForbidden` |
| 5 | 無認証 | 401 | `GetList_WhenAnonymous_IsUnauthorized` |
| 5b | **照会の据え置き**（個別取得も operator 可・#501 回帰） | retry を絞ってもグループが巻き添えで絞られていない | `GetById_AsOperator_IsAllowed` |
| 5c | 個別取得の無認証 | 401（一覧の 5 と対。既存の非対称を解消） | `GetById_WhenAnonymous_IsUnauthorized` |
| 6 | 不在 | 404 透過 | `GetById_WhenMissing_Returns404` |
| 6b | 後段障害の可視化 | 一覧は後段障害を空へ縮退せず伝播（運用画面の誤認防止・レビュー #172） | `GetList_WhenBackendFails_SurfacesFailure_NotEmptyList` |
| 6c | 後段不達→502 | 後段不達（例外）時に catch 分岐で 502 へ縮退（レビュー #172） | `GetList_WhenBackendUnreachable_Returns502` |
| 7 | 再変換 | 202 中継（admin は従来どおり成功） | `Retry_AsAdmin_Returns202` |
| 7b | **再変換は管理者限定**（2026-08-04 確定・#501 の核心） | **operator は 403**（照会は許されるロールでも実行は不可） | `Retry_AsOperator_IsForbidden` |
| 7c | 再変換の無認証 | 401（認証欠如と権限不足を取り違えない） | `Retry_WhenAnonymous_IsUnauthorized` |
| 8 | 未知再変換 | 404 透過 | `Retry_WhenJobUnknown_Passes404Through` |
| 8b | 再変換不可の透過 | 後段 409（`not_retryable`）を素通し | `Retry_WhenNotRetryable_Passes409Through` |

## デプロイ（Knowledge.IntegrationTests・#501）

`Deployment/NetworkIsolationTests.cs`
| # | 観点 | 検証内容 | ケース |
| --- | --- | --- | --- |
| 1 | 下流の到達性（compose） | `conversion-service` は host 非公開（`expose` のみ）。BFF で retry を絞っても後段へ直接到達できれば同じ穴が残るため、**認可を課さない前提（再変換の管理者限定と代償統制の決定 3）を機械検査で固定**する | `InternalServices_MustNotPublishHostPorts` |
| 2 | 下流の到達性（本番系 Helm） | Service を `type: NodePort` / `LoadBalancer` にすると BFF 以外の公開エッジができる。`service.yaml` に `type:` / `nodePort:` が現れないことを固定する | `InternalServices_HelmServicesMustStayClusterIp` |

> **本表が固定するのは到達不能の論拠 4 本のうち 2 本である。** 残る 2 本
> （NetworkPolicy への `istio-system` 例外追加・Istio VirtualService への内部サービス向けルート追加）は
> 機械では止まらない（同実装 ADR のフォローアップ 4。対象が conversion に限らないため別 issue）。

## ロール・存在秘匿の担保

- **画面と API の両側で同じ境界を固定する。** 画面側は `hides the retry button from an operator and says why`
  （テストケース 4）、API 側は BFF の 7b / 7c。**画面のテストだけでは穴は塞げない**——
  UI 制御はサーバ側の実効境界の写しであり、API を直接叩く経路はテストできないためである。
- BFF の照会は admin/operator 限定（4 / 5 で 403 / 401）。フロントは `RequireRole` で `/admin/conversions` を出し分け。
- **再変換（`retry`）は admin のみ**（7b / 7c。2026-08-04 確定。再変換を BFF で管理者限定に絞る決定 1）。照会側（3 / 5b）と対で
  **ロールの境界を両側から固定**する —— 「admin で通ること」だけでは誰でも通る状態を検出できず、
  照会側のテストが無いと、閲覧権限が巻き添えで絞られたことに気付けない。
  **［2026-08-10 追記 / #553］「計画側へ提案 8 として裁定を仰いでいる」は失効した** ——
  裁定 **Q19**（2026-08-05）が「**閲覧は管理者・運用者／破壊的操作は管理者限定**」と定め、
  **実装（admin/operator の閲覧）が追認された**（計画 `01_screens.md:124` / `:314`）。
  **照会側のテストを置く理由は変わらない** —— 巻き添えの検出は裁定の有無と独立である。
- 失敗記録後に例外再送出で MassTransit の再試行→デッドレターを保持（コンシューマ 2）。

## 実行

- `pnpm run test -- knowledge/frontend/src/features/sc07-conversions`（純関数 **7** ＋ 画面 **15** ケース）
- `pnpm run test -- knowledge/frontend/src/features/adminFlow.test.tsx`（導線）
- `pnpm run test:coverage`（カバレッジ・ラチェット維持）
- `dotnet test src/knowledge/backend/Services/ConversionService/tests/ConversionService.Worker.Tests`
- `dotnet test src/platform/backend/Bff/Platform.Bff.Tests --filter BffConversionEndpointTests`
- `dotnet test src/knowledge/backend/Tests/Knowledge.IntegrationTests --filter NetworkIsolationTests`

## 人手補正 Phase 1（バックエンド。#543）

画面（2 ペイン編集）は別 issue のため、本節は**契約側のテスト**を写像する。

### `ConversionFigureCorrectionTests`（ConversionService）

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| B1 | 縮退した図がジョブから引ける | `Figures_AreRecorded_AndReadable`（コード化 1・縮退 1・`imageUri` と `caption` が返る） |
| B2 | 画面の状態表示が契約から導出できる（管理画面の実装方針） | `JobDto_DerivesDiagramCounts_ForScreenState`（**`status` は `succeeded` のまま**・`diagramsRetained == 1`） |
| B3 | 補正で本文の図ブロックが置換され再発行される | `Correction_ReplacesImageEmbedWithCodeBlock_AndRepublishes`（画像参照が消え、**自動コード化済みの図は触らない**） |
| B4 | **再発行が ABAC 属性を落とさない** | `Correction_Republishes_WithAbacAttributesPreserved`（`confidentiality` とタグが残る。落とすと文書の可視範囲が変わる） |
| B5 | コード化済みの図は補正できない（Phase 1 の範囲） | `Correction_OnCodedFigure_Is409`（`figure_not_correctable`） |
| B6 | 未知の図は 404 | `Correction_OnUnknownFigure_Is404` |
| B7 | **補正のある再変換は止まる** | `Retry_OnCorrectedJob_Is409_UntilDiscardIsConfirmed`（`corrections_would_be_lost` ＋ **本文の `correctedFigures`**。確認つきなら 202 で補正が消える） |
| B8 | ゲートを広げすぎていない | `Retry_OnFailedJobWithoutCorrections_IsStillAccepted` |
| B9 | 本文を読めないときは**補正を保存しない** | `Correction_WhenBodyMissing_Is409_AndDoesNotPersist`（補正だけ保存されて本文に出ない状態を作らない） |

### `BffConversionEndpointTests`（BFF。書き込み・管理 API への認可強制＝多層防御）

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| F1 | 管理者は人手補正に入れる | `Figures_AsAdmin_IsAllowed` / `Correction_AsAdmin_IsAllowed` |
| F2 | **運用者は入れない**（05_screens:314） | `FigureReads_AsOperator_AreForbidden`（`figures` / `image` の 2 ケース）／`Correction_AsOperator_IsForbidden`。**「admin で通ること」だけを見るテストでは、誰でも通る状態を検出できない** |
| F3 | **409 の本文を落とさない** | `Retry_Passes409Body_ThroughVerbatim`。**変異試験で確認済み**——中継を `Results.StatusCode` へ戻すと本テストのみ落ちる |
| F4 | 明示確認が後段へ伝わる | `Retry_ForwardsDiscardConfirmation_ToDownstream`（BFF が握り潰すと補正を永遠に破棄できない） |

<!-- trace-table:
row1: ADR-0031
-->
