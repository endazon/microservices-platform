---
title: データソース管理 画面仕様書
type: screen-spec
status: completed
related_ids:
  - SC-06
  - UC-04
  - FR-01
  - FR-02
  - IADR-0039
  - IADR-0121
  - IADR-0124
  - IADR-0125
  - IADR-0127
  - IADR-0136
  - IADR-0044
  - IADR-0128
author: claude
created: 2026-07-09
updated: 2026-08-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
  - "../../planning/projects/microservices-platform/INDEX.md"
related_specs:
  - "./SC-05_document-management.md"
  - "./SC-07_conversion-jobs.md"
  - "../adr/IADR-0039_datasource-management-bff-and-role-gating.md"
  - "../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md"
  - "../adr/IADR-0136_next-sync-at-from-worker-cadence.md"
  - "../specs/20260805_issue-503_sc05-08-admin-screens.md"
  - "../specs/20260806_issue-538_next-sync-at.md"
  - "../tests/SC-06_datasource-management.md"
---

# 画面仕様書: データソース管理（SC-06）

> **［実装状態］`status: completed` は「本仕様書が記述する範囲の実装とテストが揃った」ことを表す**
> （`docs/README.md` 運用ルール 6）。**未実装のまま残っている要素がある**——hi-fi の「次回同期」列
> （**契約は #538 で揃った。残るのは表示だけ**）・行操作「設定」（**契約は #534 で揃った。残るのは
> 編集フォームだけ**）と、共通シェルのパンくず・右レール。
> **［2026-08-08 / #537］「⚠ 再試行中（3/5）」は実装した**（契約が同期健全性を持ったため）。
> 詳細と引き受け先は §hi-fi モックアップとの対応 と §未決事項 を見ること。

> **［2026-08-05 / #503］新スタックでの再実装に合わせて全面改訂した。**
> ルート `/admin/sources` は #490（[[IADR-0124]] 決定 6）で計画へ是正済みであり、本改訂でも変えていない。

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: **SC-06 データソース管理画面**（[05_screens/01_screens.md](../../planning/projects/microservices-platform/05_screens/01_screens.md) §SC-06・遷移図 `SC06 → SC07`）
- 関連ユースケース（UC）: **UC-04**（データソースを登録・同期する。基本 1・**代替「手動同期を実行する」**・**例外「接続失敗時は再試行し、継続失敗はアラートする」**）
- 関連機能要求（FR）: **FR-01**（データソースの登録・同期・カタログ化）・**FR-02**（取り込み）
- モックアップ（**実装の正**）: [hi-fi/sc-06.html](../../planning/projects/microservices-platform/05_screens/mockups/hi-fi/sc-06.html) ／ [wireframe/sc-06.html](../../planning/projects/microservices-platform/05_screens/mockups/wireframe/sc-06.html)
- 関連 IADR: [[IADR-0039]]（BFF とロールゲート）・[[IADR-0127]]（本作業の設計判断）・[[IADR-0019]]（機密区分のフェイルセーフ既定）

## 画面概要・目的

データソース（コネクタ）の登録・一覧・同期状態の確認・手動同期を行う管理画面。
取り込み → 変換の運用フローとして SC-07（変換ジョブ）への導線を持つ。

- ルート: `/admin/sources`（05_screens §共通シェル「ルートパス」）
- アクセス（**閲覧**）: **`platform-admin` または `platform-operator`**（[[IADR-0039]]）。権限外は `NotFound`（存在秘匿）。
  サーバ側 `/bff/datasources` も同ロールに限定（実効境界）。
  **［2026-08-09 / #628］計画側の裁定は着地済みである** —— 従前ここに書いていた「計画 §SC-05 / §SC-06 /
  §SC-07 の各節が独立して『管理者ロール限定』と定めており、どちらが正かは planning#198 提案 8 の裁定を要する」
  という保留は、**裁定 Q19 が「閲覧は管理者・運用者に開く。破壊的操作は管理者限定を維持する」と定めて解消した**
  （計画 §SC-05「管理系 3 画面の閲覧ロール」）。**閲覧は本記述のまま**であり、割れているのは書き込みだけだった。
- アクセス（**書き込み**・[2026-08-09 / #628]）: **破壊的操作は `platform-admin` のみ**である。

  | 操作 | ロール | 根拠 |
  | --- | --- | --- |
  | 一覧・個別取得 | admin ＋ operator | 裁定 Q19（閲覧は運用者へ開く） |
  | **登録**（`POST /datasources`） | **admin のみ** | 計画 §SC-06「登録・更新・無効化は管理者限定」。**#628 で是正**（従前は operator にも開いていた） |
  | 更新（`PUT` / `PATCH`） | **admin のみ** | 同上（#534 が計画どおり実装） |
  | **無効化**（`DELETE /{id}`） | **admin のみ** | 同上。**#628 で是正** |
  | **手動同期**（`POST /{id}/sync`） | admin ＋ operator | **破壊的操作に含めない**（planning#299・2026-08-09 裁定）。既存データを壊さず、**運用者の一次対応**を成立させることを優先する。**［範囲］人手補正（Phase 2）の導入時に再確認する** |

  実効境界はサーバ側（BFF ＋ 後段の二重・[[IADR-0044]]）であり、**画面は表示制御にすぎない**（[[IADR-0039]] 決定 2）。
  画面は運用者へ「＋ ソース登録」「無効化」を出さず、**理由の文言を 1 つ置く**（[[IADR-0127]] 決定 1）。

## hi-fi モックアップとの対応（実装する要素／実装しない要素）

行番号は planning `d980a01` の [hi-fi/sc-06.html](../../planning/projects/microservices-platform/05_screens/mockups/hi-fi/sc-06.html) に対するものである。
粒度の規則は [SC-05](./SC-05_document-management.md) と共通である。

| # | モックの要素（行） | 実装 | 備考 |
| --- | --- | --- | --- |
| 1 | 見出し「データソース」（416 左） | **する** | `<h1>` |
| 2 | 「＋ ソース登録」（416 右） | **する（管理者のみ）** | `Button variant="primary"`。押すと登録フォームを開く。**運用者へは出さず理由の文言を置く**（#628。§アクセス（書き込み）） |
| 3 | 一覧の**ソース**列（418・420-423） | **する** | 名前 ＋ 接続先 URI（`<code>`）。モックはパス（`\\fs01\share\規程集`）とサービス名を同じ列に描く |
| 4 | 一覧の**種別**列（418・420-423。ファイルサーバー／Wiki／SaaS／業務DB） | **する** | `Tag`（分類名）。`filesystem` / `wiki` / `saas` / `db` を**日本語の表示名へ写像する**（計画 §SC-06 主要素が 4 種の名前を与えている） |
| 5 | 一覧の**同期状態**列（418・420-423） | **する（導出できる範囲で）** | `StatusBadge`。**色 ＋ アイコン ＋ テキスト**（INDEX 決定 21）。導出規則は §同期状態の導出 |
| 6 | 同期状態の**「⚠ 再試行中（3/5）」**（422） | **する** | **［2026-08-08 / #537］契約が同期健全性を持ったため実装した**（裁定 Q14）。`StatusBadge tone="warning"`（琥珀）。導出規則は §同期状態の導出 |
| 7 | 一覧の**次回同期**列（418・420-423） | **しない** | **契約は #538 で追加済み**（`DataSourceDto.nextSyncAt`）。**表示が未実装**なので判定は「しない」である。繰り延べの理由が「契約に無い」から「表示が未実装」へ変わっただけで、画面としては出ていない。§実装しない要素 (b) |
| 8 | 行操作「**手動同期**」（420-421） | **する（管理者・運用者）** | `POST /bff/datasources/{id}/sync`（UC-04 **代替フロー**）。**破壊的操作に含めない**（planning#299） |
| 9 | 行操作「**設定**」（422-423） | **しない** | **契約は #534 で追加済み**（`PUT` / `PATCH`）。**編集フォームの画面実装が未了**なので判定は「しない」である（#538 と同じく、繰り延べの理由が「契約に無い」から「表示が未実装」へ変わった）。§実装しない要素 (c) |
| 10 | 「変換ジョブの状況を見る →」（426） | **する** | `/admin/conversions`（SC-07）への内部リンク。計画の遷移図 `SC06 → SC07` |
| 11 | 注記「接続情報（認証情報）は Vault 管理。接続の継続失敗はアラート（UC-04 例外フロー）。」（427） | **する** | `Alert tone="info"`（静的な注記のため `role` を付けない） |
| 12 | **共通シェル**: 右レール「AIチャットパネル」（429-434） | **しない** | 移行**第 4 段**（[[IADR-0121]] 決定 1・5） |
| 13 | **共通シェル**: パンくず（413）・ブランド／アバター（412）・左ナビ（414） | **本画面では作らない** | パンくずは #452 系。他は `foundation/ui/Layout` が既に持つ |

### モックに無いが実装する要素

| 要素 | 計画上の根拠 |
| --- | --- |
| 登録フォームの項目（名前・種別・接続先 URI・既定の機密区分） | 05_screens §SC-06 主要素「ソース登録ボタン」「コネクタ設定」／ FR-01 ／ FR-05（既定機密区分は [[IADR-0019]] のフェイルセーフ） |
| 行操作: **無効化**（**管理者のみ**） | FR-01「データソースを**登録・同期し、カタログ化**する」のライフサイクル／ [[IADR-0039]]（Accepted）。**運用者へは出さない**（#628） |

### 同期状態の導出（[[IADR-0127]] 決定 2）

| 条件 | 表示 | `StatusBadge` の `tone` | アイコン（`tone` から自動） |
| --- | --- | --- | --- |
| `status = disabled` | 無効 | `neutral` | `Info` |
| `status = active` かつ `lastSyncedAt` あり | 同期済み（日時） | `success` | `CircleCheck` |
| `status = active` かつ `lastSyncedAt` なし | 未同期 | `neutral` | `Info` |
| `status = active` かつ `0 < consecutiveFailureCount < retryLimit` | 再試行中（n/limit） | **`warning`（琥珀）** | `AlertTriangle` |
| `status = active` かつ `consecutiveFailureCount >= retryLimit` | 同期異常（n/limit） | **`warning`（琥珀）** | `AlertTriangle` |

**琥珀（警告色）の充て先**: 05_screens モック間相違の確定 ②（2026-07-24）は
「SC-06 の**同期異常表示**の警告色＝琥珀（hi-fi を正）」と定める。すなわち琥珀が指すのは**異常**であり、
**管理者が意図して無効化した正常な設定状態ではない**。`disabled` へ琥珀を充てると「⚠ が付いた正常状態」が
生まれ、計画が琥珀へ与えた意味と表示の意味がずれる。

> **［2026-08-08 / #537］琥珀の充て先が確定した。** [[IADR-0127]] 決定 2 は「同期健全性が契約に載るまで
> 琥珀を空けておく」と予約していたが、裁定 Q14 により `DataSourceDto` が
> `consecutiveFailureCount` / `retryLimit` / `lastSyncError` を持った（[[IADR-0148]]）。
> **`disabled` を中立に置く判断は変わらない** —— 計画も「実装が `disabled` を中立表示に改めて琥珀を
> 空けたまま保留した判断は**妥当であり、そのまま活かす**」と明記している。

**判定の順序は「状態 → 健全性」である。** 無効化されたソースは同期が回らないため、残っている失敗回数を
異常として出し続けると「管理者が意図して止めた正常な状態」に ⚠ が付く（`disabled` を中立に置いた理由と同じ）。

**しきい値は契約が返す `retryLimit` である**（計画 §SC-06「「継続失敗」のしきい値は再試行上限に達した
時点とする」）。**画面に定数を複写しない** —— 複写すると同じ数が 2 箇所に立ち、サーバ側を変えたときに
黙って割れる（[[IADR-0148]] 決定 4）。`disabled` は中立で示すが、
**色だけで意味を持たせない**（INDEX 決定 21）点は変わらない（`StatusBadge` が色 ＋ アイコン ＋ テキストを型で強制する）。

### 実装しない要素の理由（**いずれも繰り延べであって放棄ではない**）

| # | 計画の記述 | 現在の契約（実測） | 必要な変更 |
| --- | --- | --- | --- |
| ~~(a) 「⚠ 再試行中（3/5）」~~ | hi-fi 422・§SC-06 アクション「同期異常は警告表示」・UC-04 例外「接続失敗時は再試行し、継続失敗はアラートする」 | 従前は `Status` が **`active` / `disabled` の 2 値のみ**で、連続失敗回数・再試行上限を持つフィールドが無かった | **［2026-08-08 / #537］解消済み。** `DataSourceDto` へ同期健全性（`consecutiveFailureCount` / `retryLimit` / `lastSyncError` / `lastSyncErrorAt`）を追加し、表示まで実装した（[[IADR-0148]]） |
| (b) 「次回同期」列 | hi-fi 418・420-423（**是正後は全行「本日 14:00」＋「同期は全ソース共通の間隔で実行する」の注記**） | 同期は `DataSourceSyncHostedService` が**全ソース共通の間隔**（`DataSourceSync__IntervalSeconds`。既定 300 秒）で回す。**ソース別のスケジュールという概念が無い**。**2026-08-06 / #538 で `DataSourceDto.nextSyncAt`（共通間隔の次回実行時刻・全ソース同値・無効時 null）を契約へ追加した**（[[IADR-0136]]） | ~~ソース別スケジュール（cron 等）のモデル化~~ → **裁定で不採用**（planning#200 Q15）。残るのは**画面側の列の追加**だけである |
| (c) 行操作「設定」 | hi-fi 422-423 | 従前は `/bff/datasources` に**更新（`PUT` / `PATCH`）が無かった**。**［2026-08-08 / #534］契約は追加済み**（全置換 `PUT` / 部分更新 `PATCH`・**管理者限定**） | ~~データソース更新 API~~ → **契約は揃った**。残るのは**編集フォームの画面実装**だけである（#534 は契約の追加に閉じる。[[IADR-0139]] 条件 F） |

実測の出所: `src/knowledge/backend/Shared/Knowledge.Contracts/Dtos/DataSourceDto.cs` ／
`src/knowledge/backend/Services/DataSourceService/src/DataSourceService.Api/Foundation/Domain/DataSource.cs` ／
`.../Foundation/Services/DataSourceSyncHostedService.cs` ／
`src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/DataSourceBffEndpoints.cs`（対象コミット `de55761`）。

**「押しても結果が変わらないボタン」「常に空の列」を置かない**（#502 が確立した規則）。
3 件は環流の記録に載せ（[feedback/20260805_sc05-07-admin-contract-gaps.md](../../feedback/20260805_sc05-07-admin-contract-gaps.md)）、
planning#198 として起票した。**2026-08-05 に 3 件とも裁定が出て計画本文へ反映済みである**（planning#200。
(a) = Q14 同期健全性を契約へ追加／(b) = Q15 次回同期は共通間隔・ソース別スケジュールは持たない／
(c) = Q16 更新 API を定める）。**3 件とも契約は揃った** —— (b) は #538（[[IADR-0136]]）、
**(a) と (c) は #534 ＋ #537 の束**（[[IADR-0148]] / [[IADR-0139]] 決定 5）である。
**(a) は表示まで実装済み**、(b) と (c) は**画面側の実装だけが残っている**。

## データソース（BFF 境界）

| 用途 | エンドポイント | 呼び出し方 | 認可（サーバ側） | 応答 |
| --- | --- | --- | --- | --- |
| 一覧 | `GET /bff/datasources` | **orval 生成フック `useBffDataSourceList`**（#519） | admin / operator | `DataSourceDto[]` |
| 登録 | `POST /bff/datasources` | `useMutation` | 同上 | `DataSourceDto`（201） |
| 更新（全置換） | `PUT /bff/datasources/{id}` | **未使用**（契約のみ。#534） | **admin のみ** | `DataSourceDto`（200） |
| 更新（部分） | `PATCH /bff/datasources/{id}` | **未使用**（契約のみ。#534） | **admin のみ** | `DataSourceDto`（200） |
| 手動同期 | `POST /bff/datasources/{id}/sync` | `useMutation` | 同上 | 202 `DataSourceSyncResultDto`（`{ fetched, failed, connectorAvailable, message }`） |
| 無効化 | `DELETE /bff/datasources/{id}` | `useMutation` | 同上 | 204 |

- **orval 生成フックで呼ぶ**（#506 で契約が揃い、**#519** で載せ替えた。[[IADR-0135]] 決定 1）。
- **BFF は後段障害を空一覧へ縮退させない**（502 で可視化する）。「未登録」と誤認させて重複登録を招かないためであり、
  画面もこれに合わせて**取得失敗をエラーとして表示する**（0 件表示へ寄せない）。
- 更新系の成功後は `invalidateQueries({ queryKey: getBffDataSourceListQueryKey() })`
  （＝`['/bff/datasources']`）のみを行う（[[IADR-0127]] 決定 5）。
  **［2026-08-06 追記］かつて `['bff','datasources']` と書いていたのは載せ替え前の階層キーである
  （#519 / [[IADR-0135]] 決定 1 で生成キーへ移った）。** 本画面は詳細ページを持たず、無効化の対象は
  一覧 1 本だけなので、SC-05 のような前方一致の破れ（[[IADR-0135]] 決定 3）は生じない。

## レイアウト / 主要素

```text
┌──────────────────────────────────────────────────────────────────────────┐
│ データソース                                            [＋ ソース登録]   │
├────────────────────┬─────────────────┬─────────────────┬───────────────┤
│ ソース              │ 種別             │ 同期状態         │ 操作           │
├────────────────────┼─────────────────┼─────────────────┼───────────────┤
│ 規程集              │［ファイルサーバー］│ ✓ 同期済み（…）  │ 手動同期 無効化 │
│ smb://fs01/share    │                 │                 │               │
└────────────────────┴─────────────────┴─────────────────┴───────────────┘
  変換ジョブの状況を見る →
  ⓘ 接続情報（認証情報）は Vault 管理。接続の継続失敗はアラート（UC-04 例外フロー）。
```

## 表示・入力項目

| 項目 | 種別 | 必須 | 形式・制約 | 説明 |
| --- | --- | --- | --- | --- |
| 名前 | `Input` | **必須** | 1 文字以上（前後空白を除く）・最大 200 文字 | 空では登録不可 |
| 種別 | `Select` | **必須** | `filesystem` / `wiki` / `saas` / `db` | 表示名はファイルサーバー／Wiki／SaaS／業務DB |
| 接続先 URI | `Input` | **必須** | 1 文字以上・最大 500 文字 | **認証情報は入力しない**（Vault 管理。注記で明示） |
| 既定の機密区分 | `Select` | 任意 | `public` / `internal` / `confidential` / `restricted` | 既定 `internal`。未指定でもサーバが `internal` を補完（[[IADR-0019]]） |

## アクション・イベント

| 操作 | 挙動 | 遷移先 |
| --- | --- | --- |
| ＋ ソース登録 | 登録フォームの開閉 | — |
| 登録する | `POST /bff/datasources` → 一覧を再取得 | — |
| 手動同期 | `POST /bff/datasources/{id}/sync` → 一覧を再取得（**UC-04 代替フロー**） | — |
| 無効化 | `DELETE /bff/datasources/{id}` → 一覧を再取得 | — |
| 変換ジョブの状況を見る → | SC-07 へ | `/admin/conversions` |

## 権限・表示条件・存在秘匿

- ロール（admin / operator）を持たない利用者には**ルートもナビ項目も存在しない**（`NotFound`）。
- データソースは文書 ABAC のスコープ対象ではなく**運用資産**であるため、ロールのみで制御する（[[IADR-0039]]）。
- 無効化ボタンは `status !== 'disabled'` の行にだけ出す（既に無効なソースへ再度無効化を送らない）。

## エラー・状態

| 状態 | 表示 |
| --- | --- |
| 取得中 | 「読み込み中…」（`role="status"`） |
| 一覧の取得失敗 | `Alert tone="danger"` `role="alert"`（**0 件表示へ縮退しない**。上記 §データソース） |
| 成功・0 件 | 「データソースは登録されていません。」 |
| 登録・同期・無効化の成功 | `Alert tone="success"` `role="status"` |
| 操作の失敗 | `Alert tone="danger"` `role="alert"`（`toMessages` の詳細を優先） |

**画面が出すのは直近の操作の結果 1 件だけである**（[[IADR-0127]] 決定 7）。新しい操作を始めた時点で、
前の成功メッセージと**各ミューテーションの失敗状態**を捨てる。これが無いと「手動同期が失敗 → 無効化が成功」で
成功バナーと古い失敗バナーが並び、どの操作の結果かが読めなくなる。

## i18n

- 文言はすべて Lingui のカタログ（ja / en）へ載せる。`eslint-plugin-lingui` の適用範囲に本 feature を含める。
- **種別は表示名を翻訳する**（計画が 4 種の日本語名を与えているため）。機密区分の**値**は翻訳しない（SC-05 と同じ）。

## UI 部品（`@platform/ui`）

`Table` 一式 / `Button` / `Input` / `Select` / `Label` / `Card` 一式 / `Alert` / `Tag` / **`StatusBadge`**。
新規プリミティブは追加しない。

## 関連仕様

- 作業仕様書: [20260805_issue-503_sc05-08-admin-screens.md](../specs/20260805_issue-503_sc05-08-admin-screens.md)
- テスト仕様書: [SC-06_datasource-management.md](../tests/SC-06_datasource-management.md)
- 作業仕様書（次回同期の契約）: [20260806_issue-538_next-sync-at.md](../specs/20260806_issue-538_next-sync-at.md)
- 作業仕様書（同期健全性・更新 API）: [20260808_issue-534-537_datasource-contract-bundle.md](../specs/20260808_issue-534-537_datasource-contract-bundle.md)
- 実装 ADR: [IADR-0127](../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md) / [IADR-0039](../adr/IADR-0039_datasource-management-bff-and-role-gating.md) / [IADR-0136](../adr/IADR-0136_next-sync-at-from-worker-cadence.md) / [IADR-0148](../adr/IADR-0148_datasource-sync-health-persistence.md)
- 計画への環流（**planning#198 として起票済み・2026-08-05 に裁定され planning#200 で計画本文へ反映済み**）: [feedback/20260805_sc05-07-admin-contract-gaps.md](../../feedback/20260805_sc05-07-admin-contract-gaps.md)

## 未決事項

1. ~~**同期異常（再試行中 N/M）の表示**~~ —— **［2026-08-08 / #537］解消済み。** 裁定（planning#200 Q14）を受けて
   `DataSourceDto` へ同期健全性を追加し、琥珀の 2 状態を表示するところまで実装した（[[IADR-0148]]）。
2. **次回同期**（同 (b)）。**裁定済み**（planning#200 Q15: ソース別スケジュールは持たない・共通間隔の
   次回実行時刻を全ソース同値で返す）。**契約は #538 が追加した**（`nextSyncAt`。[[IADR-0136]]）。
   **残るのは画面側の列の追加**であり、繰り延べの理由（契約の不在）はもう無い。
   表示にあたっては `nextSyncAt` が `null`（定期同期が無効な環境）になり得ることに注意する。
3. **コネクタ設定の編集**（同 (c)）。**裁定済み**（planning#200 Q16）。**契約は #534 が追加した**
   （`PUT` / `PATCH`・**管理者限定**）。**残るのは編集フォームの画面実装**であり、繰り延べの理由
   （契約の不在）はもう無い。**登録フォーム（`DataSourceForm`）を編集にも使えるようにするのが自然**だが、
   秘密の扱いは**サーバ側が守っている** —— **［2026-08-08 / PR #627 の AI レビュー 🟡］
   マスク値（`***`）は保存されず既存値が保たれる**（[[IADR-0148]] 決定 6）。
   したがって GET の応答をそのまま編集して送り返してよい。`config` を省略する必要はない。
4. ~~**閲覧ロール**（admin/operator か admin のみか）~~ —— **［2026-08-09 / #628］解消済み。**
   裁定 Q19（planning#198）が「**閲覧は管理者・運用者に開く**」と定め、計画側が改訂された。
   [[IADR-0039]] 決定 1（admin **または** operator）が計画と一致した状態になっている。
5. ~~**書き込みロールの計画との差異**~~ —— **［2026-08-09 / #628］解消済み。**
   計画 §SC-06 §アクセス制御「**登録・更新・無効化は管理者限定**」に対し、`POST` と `DELETE` が
   **admin ＋ operator** のままだった（グループ既定をそのまま使っていた）。**#628 が BFF と後段の両方へ
   `AdminOnly` を積んで是正し、画面も運用者へ出さないようにした。**
   **手動同期は別扱いである** —— planning#299（2026-08-09）が「実行系だが破壊的ではない」として
   **運用者に開いたまま**と裁定した（現行実装の追認）。§アクセス（書き込み）の表を正とする。
