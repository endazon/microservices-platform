---
title: SC-06 データソース管理 テスト仕様書
type: test-spec
status: completed
related_ids:
  - SC-06
  - UC-04
  - FR-01
  - FR-02
  - FR-05
  - IADR-0039
  - IADR-0127
  - IADR-0136
  - IADR-0044
  - IADR-0128
  - IADR-0199
author: claude
created: 2026-07-09
updated: 2026-08-16
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/INDEX.md"
related_specs:
  - "../screens/SC-06_datasource-management.md"
  - "../specs/20260805_issue-503_sc05-08-admin-screens.md"
  - "../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md"
  - "../adr/IADR-0136_next-sync-at-from-worker-cadence.md"
  - "../specs/20260806_issue-538_next-sync-at.md"
---

# テスト仕様書: データソース管理（SC-06）

> **［2026-08-05 / #503］新スタックでの再実装に合わせて全面改訂した。**
>
> **［2026-08-05 / #510］API 側（BFF）の節を復帰させた。** #503 の全面改訂はフロントエンドの構造で
> 置き換えたため §バックエンド（BFF）が落ちていたが、**当該テストは実在し続けている**
> （`BffDataSourceEndpointTests`）。落としたままにすると「画面のテストしか無い」と読め、
> 次に触る人が重複して書くか消してよいと判断する。**本復帰は当時の記載をそのまま戻したのではなく、
> 現在のテストの実物（クラス名・メソッド名・ファイルパス）と突き合わせて書き直したものである。**
> 同種の欠落の再発は [`check-test-spec-coverage.js`](../../scripts/check-test-spec-coverage.js) が止める。
>
> **［2026-08-06 / #538］§DataSourceService（xUnit・次回同期）を追加した。** 裁定（planning#200 Q15）で
> `NextSyncAt`（共通間隔の次回実行時刻・全ソース同値）が契約へ入ったことに伴う。**画面への「次回同期」列の
> 追加は本作業の範囲外**であり、§テストケース 13 は当面そのまま（列が無いことを見る）である。

対象（画面）: `src/knowledge/frontend/src/features/sc06-datasources/`
テスト: `syncState.test.ts`（純関数）／ `DataSourceManagementPage.test.tsx`（Vitest + Testing Library）／
導線は `src/knowledge/frontend/src/features/adminFlow.test.tsx`／
E2E は `src/platform/frontend/e2e/sc06-datasources.smoke.spec.ts`

対象（API）: [`src/platform/backend/Bff/Platform.Bff.Tests/BffDataSourceEndpointTests.cs`](../../src/platform/backend/Bff/Platform.Bff.Tests/BffDataSourceEndpointTests.cs) ／
[`src/knowledge/backend/Services/DataSourceService/tests/DataSourceService.Api.Tests/SyncScheduleTests.cs`](../../src/knowledge/backend/Services/DataSourceService/tests/DataSourceService.Api.Tests/SyncScheduleTests.cs)（**次回同期**・#538）

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: SC-06 ／ ユースケース（UC）: **UC-04**（データソースを登録・同期する）／ 機能要求（FR）: FR-01・FR-02

## UC-04 のフロー → テストの写像

| UC-04 のフロー | 画面での現れ方 | テスト |
| --- | --- | --- |
| **基本 1. 管理者がソース（ファイルサーバー／Wiki／SaaS／業務DB）を登録する** | 登録フォーム → `POST /bff/datasources`（既定の機密区分つき） | `registers a data source with a default confidentiality attribute` |
| **基本 1（既定の部門）** | **［2026-08-15 / #767］**同じ登録フォームに **既定の部門**の欄を足し、非空なら `defaultAttributes.department` として送る（09_datasource-connectors §システム投入経路の **2 段目**。[[IADR-0199]]） | `registers a data source with a default department attribute` ／ `does not require a department to enable the register button` |
| **基本 1（既定のライフサイクル状態）** | **［2026-08-16 / #796］**同じ登録フォームに **既定のライフサイクル状態**の欄を足し、**選んだときだけ** `defaultAttributes.lifecycle` として送る（09_datasource-connectors §システム投入経路の **2 段目**。計画が「ソース単位で下書き扱いにしたい場合は既定属性で `draft` を指定する」と定める） | `registers a data source with a default lifecycle attribute` ／ `offers exactly the three lifecycle states the plan defines, plus an unspecified choice` ／ `does not require a lifecycle to enable the register button` |
| **代替. 手動同期を実行する** | 行操作「手動同期」→ `POST /bff/datasources/{id}/sync` | `triggers a manual sync` |
| **例外. 接続失敗時は再試行し、継続失敗はアラートする** | **［2026-08-08 / #537］注記に加えて状態そのものを表示する**（契約が同期健全性を持った。[[IADR-0148]]） | `states that credentials live in Vault and that repeated failures raise an alert` ／ `shows an amber sync-fault state with the redacted last error` ／ `shows an amber retrying state below the retry limit` |
| 基本 2. システムが定期的に原本を取得し、変換へイベント送出する | **写像しない**（サーバ側の hosted service） | — |

## テストケース

| # | 観点 | 起点 | 検証内容 |
| --- | --- | --- | --- |
| 1 | 一覧 | SC-06 / FR-01 | `GET /bff/datasources` を呼び、ソース名 ＋ 接続先・**種別（日本語表示名）**・**同期状態**を表示する |
| 2 | **同期状態の導出** | INDEX 決定 21 / [[IADR-0127]] 決定 2 / [[IADR-0148]] | `disabled` → 無効（**中立**）／ `active`＋最終同期あり → 同期済み／ `active`＋なし → 未同期。**tone とテキストが対で決まる**。**［2026-08-08 / #537］琥珀は同期健全性へ充てた**（`0 < 失敗 < 上限` → 再試行中／`失敗 >= 上限` → 同期異常） |
| 2-b | **継続失敗の表示** | **UC-04 例外** / SC-06 裁定 Q14 | 上限到達で「同期異常（n/limit）」を琥珀で出し、**マスク済みの直近エラー**を添える。異常時に「同期済み」を併記しない |
| 2-c | **無効は健全性より優先** | [[IADR-0148]] | 失敗回数が残っていても `disabled` は中立（同期が回らない状態に ⚠ を付けない） |
| 3 | 種別の写像 | SC-06 | 4 種（`filesystem` / `wiki` / `saas` / `db`）に表示名がある。**未知の種別は生値**を出す |
| 4 | 登録 | UC-04 基本 1 | 名前・種別・接続先・既定の機密区分を送る。**［2026-08-15 / #767］部門が未入力なら `department` キーを送らない**（`defaultAttributes` の完全一致で見る。空文字を送る形へ戻すと落ちる）。**［2026-08-16 / #796］ライフサイクル状態が未指定でも `lifecycle` キーを送らない**（同じ `toEqual` に加えて名指しでアサートする） |
| 4-b | **既定の部門を送る** | **UC-04 基本 1** / FR-05 / [[IADR-0199]] | 部門を入力すると `defaultAttributes.department` に**前後空白を落とした値**が乗る。これが無いと画面から登録した全ソースが予約値 `unassigned` へ倒れ、ABAC の判定軸が実質 `confidentiality` 1 本になる |
| 4-c | **部門は任意** | **UC-04** / SC-06 | 部門が空でも「登録する」が押せる（計画に無い必須化を実装が足さない）。未入力時に何が入るか（予約値 `unassigned`）を補助文が伝える |
| 4-d | **既定のライフサイクル状態を送る** | **UC-04 基本 1** / FR-05 / [[IADR-0199]] 決定 4 | `draft` を選ぶと `defaultAttributes.lifecycle` に乗る。これが無いと**ソース単位で下書き扱いにする指定が画面からできない**（計画 09_datasource-connectors が明記する運用が API 直叩きでしか行えない） |
| 4-e | **値域が計画どおり** | 07_abac-attribute-model の `lifecycle` 属性 / 05_screens §SC-05 | 選択肢が「未指定」＋ `draft` / `active` / `archived` の**ちょうど 4 つ**であり、**既定の選択が「未指定」**である。**計画に無い値（`normalized` / `published`）を実装が持ち込まない**（計画が名指しで「計画側の語彙ではない」と書いている）。`active` を初期選択にすると「明示指定した」と「しなかった」の区別が消える |
| 4-f | **ライフサイクル状態は任意** | **UC-04** / SC-06 | 未指定でも「登録する」が押せる。未指定時に何が入るか（**予約値ではなく既定値** `active`）を補助文が伝える |
| 5 | 必須項目 | UC-04 | 名前と接続先が埋まるまで登録できない |
| 6 | 手動同期 | **UC-04 代替** | `POST …/sync` を呼び、完了を伝える |
| 6-b | **再取得** | [[IADR-0127]] 決定 5 | 手動同期の成功後に一覧を取り直す（`invalidateQueries` のみ） |
| 7 | 無効化 | FR-01 | `active` の行だけに操作が出る。`DELETE /bff/datasources/{id}` を呼ぶ |
| 8 | 注記 | **UC-04 例外** | Vault 管理と継続失敗アラートを明示する |
| 9 | **異常系（縮退しない）** | [[IADR-0039]] | 取得失敗を `role="alert"` で出し、**「登録されていません」へ寄せない**（重複登録の誘発を避ける） |
| 10 | 操作の失敗 | — | 一覧を保ったままエラーを出す |
| 10-b | **直近の操作結果だけを出す** | [[IADR-0127]] 決定 7 | 失敗 → 成功・成功 → 失敗のどちらの並びでも、**前の操作のバナーが残らない** |
| 11 | 0 件 | — | 「データソースは登録されていません。」 |
| 12 | **権限別の出し分け** | [[IADR-0035]] / [[IADR-0009]] | ロールを持たない利用者には画面が無い（`NotFound`）。**要求も出さない** |
| 12-c | **書き込みの出し分け（運用者）** | SC-06 §アクセス制御 / 裁定 Q19（#628） / [[IADR-0127]] 決定 1 | 運用者へは「＋ ソース登録」「無効化」を**出さない**。**無言で消さず理由の文言を出す**（権限の問題と状態の問題を読み分けられるようにする） |
| 12-d | **狭めすぎない（運用者）** | planning#299（#628） | 運用者にも一覧と「手動同期」は**出る**（一次対応を潰さない） |
| 12-e | **管理者には 3 つとも出る** | SC-06 | 登録・手動同期・無効化がすべて出る |
| 12-b | **SC-07 への導線** | 05_screens 遷移図 `SC06 → SC07` | 「変換ジョブの状況を見る →」が `/admin/conversions` を指す（画面単体でリンク先を固定する。実際に遷移することは §導線 A が見る） |
| 13 | **未実装の要素** | 画面仕様書 §hi-fi 対応 #7・#9 | 「次回同期」列・「設定」操作が無い。**先に手動同期の操作が在ることを確かめてから**無いことを見る。**［2026-08-08 / #534・#537］2 件が動いた**——「再試行中」表示は**実装した**ので本ケースの対象から外れ（ケース 2-b が見る）、「設定」は**契約（`PUT` / `PATCH`）が揃って**残るのが画面実装だけになった。**3 件とも契約の不在ではなくなった** |
| 14 | ロケール `en` | ADR-0031 | 見出しと種別が英語で描画される。**［2026-08-15 / #767］登録フォームを開いて「既定の部門」のラベルも英語で出ることを見る**（ja だけ足して en を空のまま残さない）。**［2026-08-16 / #796］「既定のライフサイクル状態」のラベルと「未指定」の選択肢も同様に見る。値（`draft` 等）は訳さないので英語でも生値のまま出ることを併せて見る**。**ただし未翻訳そのものを止めているのは `scripts/check-i18n-catalogs.js` と `lingui compile --strict` である** —— 実行時に読まれるのはコンパイル済みの `messages.ts` であり、`.po` だけが未訳でも再コンパイルするまで本ケースは緑のままになる（変異試験で実測。作業仕様書 §変異試験 M5） |

## 純関数（`syncState.test.ts`）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| P1 | 無効の表示 | `disabled` が `neutral` になり、「同期済み（日時）」を出さない |
| P1-b | **琥珀を健全な状態へ充てない** | 失敗 0 の状態のいずれにも `warning` を充てない |
| P1-c | **無効は健全性より優先** | 失敗回数が残っていても `disabled` は中立（[[IADR-0148]]） |
| P1-d | **再試行中 / 同期異常** | `0 < 失敗 < 上限` → 「再試行中（n/limit）」・`失敗 >= 上限` → 「同期異常（n/limit）」。いずれも `warning` |
| P1-e | **しきい値は契約から取る** | 上限が 3 なら 3 回で「同期異常」へ上がる（画面に定数を複写しない。[[IADR-0148]] 決定 4） |
| P1-f | **健全性が無い応答でも壊れない** | 未指定・`null`・上限だけ欠落のいずれでも既存の 3 状態へ落ちる |
| P2 | 同期済み / 未同期 | `lastSyncedAt` の有無で `success` / `neutral` が決まる |
| P3 | 種別の値集合 | 計画が挙げる 4 種と表示名 |
| P4 | 未知の種別 | 生値をそのまま返す |
| P5 | 日時整形 | 空は `—`、解釈できない値はそのまま出す |

**機密区分の値集合**（登録フォームの「既定の機密区分」）は SC-05 と共有する語彙であり、
`features/abac/confidentiality.test.ts` が固定する（[テスト仕様書 SC-05 §純関数](./SC-05_document-management.md)）。

### 語彙（`features/abac/department.test.ts`。［2026-08-15 / #767］）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| D1 | 属性キー | `DEPARTMENT_KEY` が `department`（バックエンド `DataSource.DepartmentKey` と同値） |
| D2 | 予約値 | `UNRESOLVED_DEPARTMENT` が `unassigned`（同 `DataSource.UnresolvedDepartment`。[[IADR-0199]]） |

**どちらも後段と一致していなければ意味を失う文字列である。** キーがずれると属性辞書の別のキーへ書き込まれ、
後段はフェイルセーフで `unassigned` を入れるため、**画面上は何も起きずに管理者の入力だけが消える**。
画面テスト経由の間接被覆では文字列そのものを固定できないため、`confidentiality` と同じく直接固定する。

### 語彙（`features/abac/lifecycle.test.ts`。［2026-08-16 / #796］）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| L1 | 属性キー | `LIFECYCLE_KEY` が `lifecycle`（バックエンド `DataSource.LifecycleKey` と同値） |
| L2 | 値域 | `LIFECYCLE_VALUES` が `['draft', 'active', 'archived']`（正本は計画 07_abac-attribute-model の `lifecycle` 属性） |
| L3 | **計画に無い値を持ち込まない** | `normalized` / `published` / `publish` / `archive` のいずれも値域に含まれない（前 2 つは計画が名指しで否定した語、後 2 つは**端点名＝動詞**であって状態ではない） |
| L4 | 終端の既定値 | `DEFAULT_LIFECYCLE` が `active` で、かつ値域に含まれる（同 `DataSource.DefaultLifecycle`。裁定 planning#361） |

**`department` と壊れ方が違う。** キーがずれると後段が既定値 `active` を入れるので**画面上は何も起きずに
管理者の指定だけが消える**のは同じだが、値がずれた場合は ABAC ポリシーの `allowedLifecycles` に一致せず
**文書が到達不能になる**。終端値がずれれば補助文が嘘になる。いずれも画面テスト経由では固定できない。

## 導線（`adminFlow.test.tsx`）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| A | SC-06 → SC-07 | 「変換ジョブの状況を見る →」から変換ジョブ画面へ遷移する（計画の遷移図 `SC06 → SC07`） |

## BFF（xUnit）

対象: [`Knowledge.Bff.Endpoints/DataSourceBffEndpoints.cs`](../../src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/DataSourceBffEndpoints.cs)
テスト: [`Platform.Bff.Tests/BffDataSourceEndpointTests.cs`](../../src/platform/backend/Bff/Platform.Bff.Tests/BffDataSourceEndpointTests.cs)

| # | 観点 | 起点 | 検証内容 | ケース |
| --- | --- | --- | --- | --- |
| 1 | 一覧（管理者） | FR-01 | admin で一覧が返る | `GetList_AsAdmin_ReturnsDataSources` |
| 2 | 一覧（運用者） | [[IADR-0039]] | operator も許可 | `GetList_AsOperator_IsAllowed` |
| 3 | ロール制限 | [[IADR-0039]] | 非特権ロールは 403 | `GetList_AsNonPrivilegedRole_IsForbidden` |
| 4 | 無認証 | [[IADR-0039]] | 匿名は 401（認証欠如と権限不足を取り違えない） | `GetList_WhenAnonymous_IsUnauthorized` |
| 5 | 不在 | FR-01 | 後段の 404 を透過 | `GetById_WhenMissing_Returns404` |
| 5-b | **後段障害の可視化** | FR-01 / [[IADR-0039]] | 一覧は後段障害を**空一覧へ縮退させず**伝播する（管理画面の誤認＝重複登録の誘発を避ける。レビュー #169） | `GetList_WhenBackendFails_SurfacesFailure_NotEmptyList` |
| 6 | 登録 | FR-01 | 201 で中継 | `Create_AsAdmin_Returns201` |
| 7 | 同期 | FR-01 / FR-02 | 202 で同期トリガを中継 | `Sync_AsAdmin_Returns202` |
| 8 | 無効化 | FR-01 | 204 で論理削除を中継 | `Delete_AsAdmin_Returns204` |
| 9 | **次回同期の透過** | SC-06 裁定 Q15 / [[IADR-0136]] | 後段が返す `nextSyncAt` を欠落させず、**ソースごとに変えもしない**（BFF は `DataSourceDto` で中継するだけなので実装は変わらないが、契約のメンバーが増えたとき落ちる場所が要る） | `GetList_PassesThroughNextSyncAt` |
| 10 | **登録は管理者限定** | SC-06 §アクセス制御 / 裁定 Q19（#628） | 運用者の `POST /bff/datasources` は **403** | `Create_AsOperator_IsForbidden` |
| 11 | **無効化は管理者限定** | 同上（#628） | 運用者の `DELETE /bff/datasources/{id}` は **403** | `Delete_AsOperator_IsForbidden` |
| 12 | **手動同期は運用者へ開いたまま** | planning#299（2026-08-09 裁定・#628） | 運用者の `POST /bff/datasources/{id}/sync` は **202**（破壊的操作に含めない） | `Sync_AsOperator_IsAllowed` |
| 13 | **閲覧を狭めない** | 裁定 Q19 | 運用者の個別取得は **200**（10・11 と対で固定する） | `GetById_AsOperator_IsAllowed` |

**10〜13 は「狭める」と「狭めすぎない」を対で固定する。** 登録・無効化だけを 403 にし、
閲覧と手動同期は通ることまで見ないと、**計画の裁定（運用者の一次対応を成立させる）を壊しても緑になる。**

**5-b は画面側の §テストケース 9（縮退しない）と対である。** 画面が縮退しない実装でも、
BFF が後段障害を空一覧へ丸めてしまえば画面には何も届かない。両側で固定して初めて担保になる。

## DataSourceService（xUnit・次回同期）

対象: [`.../DataSourceService.Api/Foundation/Services/SyncSchedule.cs`](../../src/knowledge/backend/Services/DataSourceService/src/DataSourceService.Api/Foundation/Services/SyncSchedule.cs) ／
[`.../Foundation/Services/DataSourceSyncHostedService.cs`](../../src/knowledge/backend/Services/DataSourceService/src/DataSourceService.Api/Foundation/Services/DataSourceSyncHostedService.cs) ／
[`.../Foundation/Endpoints/DataSourceEndpoints.cs`](../../src/knowledge/backend/Services/DataSourceService/src/DataSourceService.Api/Foundation/Endpoints/DataSourceEndpoints.cs)
テスト: [`DataSourceService.Api.Tests/SyncScheduleTests.cs`](../../src/knowledge/backend/Services/DataSourceService/tests/DataSourceService.Api.Tests/SyncScheduleTests.cs)

計画の裁定（planning#200 Q15）は「`NextSyncAt` は**共通間隔の次回実行時刻**として全ソース同じ値を返す」である。
**時刻依存は `TimeProvider` を固定して決定的にする**（`DateTimeOffset.UtcNow` をテストから呼ばない）。

| # | 観点 | 起点 | 検証内容 | ケース |
| --- | --- | --- | --- | --- |
| S1 | 未起動なら次回は無い | [[IADR-0136]] 決定 2 | 定期同期が動いていなければ `null` | `NextRunAt_WhenNeverStarted_IsNull` |
| S2 | 起動直後 | UC-04 基本 2 | 「起点 ＋ 共通間隔」 | `NextRunAt_JustAfterStart_IsOneIntervalAhead` |
| S3 | 周期の経過 | [[IADR-0136]] 決定 1 | 何周期経っても**現在より後の最初の境界**（過去を「次回」と呼ばない） | `NextRunAt_AfterSeveralIntervals_IsNextBoundary`（Theory 3 件） |
| S4 | 境界ちょうど | 同上 | その回は今走っているので次の境界へ進む | `NextRunAt_ExactlyOnBoundary_MovesToNextBoundary` |
| W1 | 無効なワーカー | [[IADR-0136]] 決定 2 | 位相を記録しない（compose / dev の既定） | `StartSchedule_WhenDisabled_LeavesScheduleUnset` |
| W2 | 有効なワーカー | [[IADR-0051]] | 起動時刻を起点に共通間隔を刻み始める | `StartSchedule_WhenEnabled_AnchorsAtStartup` |
| W3 | 30 秒床 | ワーカーの過負荷防止 | 実効間隔（`Math.Max(30, …)`）が次回時刻にも効く | `StartSchedule_FloorsIntervalAtThirtySeconds`（Theory 3 件） |
| E1 | **全ソース同値** | **裁定 Q15 の核心** | `GET /datasources` の全要素が同じ `nextSyncAt` を返す（ソース別スケジュールを持たない） | `ListDataSources_ReturnsSameNextSyncAtForEverySource` |
| E2 | 無効時の応答 | [[IADR-0136]] 決定 2 | `nextSyncAt` は `null`（「次回がある」と偽らない） | `ListDataSources_WhenPeriodicSyncDisabled_ReturnsNullNextSyncAt` |

**E1 と BFF の 9（透過）は対である。** 後段が同値を返しても BFF が落とせば画面には届かない。

## ロール・存在秘匿の担保

- BFF はグループ全体を admin / operator に限定し（3 / 4 で 403 / 401 を固定）、
  **破壊的操作（登録・更新・無効化）にはさらに `AdminOnly` を積む**（10 / 11。[[IADR-0128]] 決定 1 の形）。
  **後段（`DataSourceService`）にも同じ制限を置く多層防御**であり（[[IADR-0044]]）、
  `DataSourceAuthorizationTests` の `Create_OperatorRole_Returns403` / `Delete_OperatorRole_Returns403` /
  `Sync_OperatorRole_IsAllowed` / `GetById_OperatorRole_IsAllowed` / `CreateAndDelete_AdminRole_IsAllowed`
  が BFF 側と対で固定する（**片側だけだと BFF 迂回で通る／画面だけ 403 になる**）。
  **画面と API の両側で同じ境界を固定する** —— UI の出し分けはサーバ側の実効境界の写しであり、
  API を直接叩く経路は画面テストでは踏めない（SC-07 が #501 で踏んだのと同じ形）。
- フロントはルート／ナビを `RequireRole` で出し分け、権限外は `NotFound`（§テストケース 12）。

## 実行

- `pnpm run test -- knowledge/frontend/src/features/sc06-datasources`（純関数 **12** ＋ 画面 **26**。
  **［2026-08-16 / #796］数え直した** —— 従前の「7 ＋ 15」は #503 当時の値で、その後の追加（#537 / #538 / #767 /
  本 issue）に追随していなかった。**導出値は走査ではなく計算し直す**という規約に従い、`vitest run` の
  実測値へ置き換えた）
- `pnpm run test -- knowledge/frontend/src/features/abac`（語彙 **9**。機密区分 3 ＋ 部門 2 ＋ **ライフサイクル 4**）
- `pnpm run test -- knowledge/frontend/src/features/adminFlow.test.tsx`（導線）
- `pnpm run test:coverage`（カバレッジ・ラチェット維持）
- `dotnet test src/platform/backend/Bff/Platform.Bff.Tests --filter BffDataSourceEndpointTests`
- `dotnet test src/knowledge/backend/backend.slnx --filter SyncScheduleTests`（次回同期・#538）
