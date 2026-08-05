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
author: claude
created: 2026-07-09
updated: 2026-08-05
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
  - "../specs/20260805_issue-503_sc05-08-admin-screens.md"
  - "../tests/SC-06_datasource-management.md"
---

# 画面仕様書: データソース管理（SC-06）

> **［実装状態］`status: completed` は「本仕様書が記述する範囲の実装とテストが揃った」ことを表す**
> （`docs/README.md` 運用ルール 6）。**未実装のまま残っている要素がある**——hi-fi の「次回同期」列・
> 「⚠ 再試行中（3/5）」・「設定」（いずれも契約に載る先が無い）と、共通シェルのパンくず・右レール。
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
- アクセス: **`platform-admin` または `platform-operator`**（[[IADR-0039]]）。権限外は `NotFound`（存在秘匿）。
  サーバ側 `/bff/datasources` も同ロールに限定（実効境界）。
  計画は §共通シェル に加え、**§SC-05（`01_screens.md:234`）・§SC-06（`:242`）・§SC-07（`:250`）**
  の各節でも独立して「管理者ロール限定」と定める。**どちらが正かは計画側の裁定を要する**（planning#198 提案 8）。

## hi-fi モックアップとの対応（実装する要素／実装しない要素）

行番号は planning `d980a01` の [hi-fi/sc-06.html](../../planning/projects/microservices-platform/05_screens/mockups/hi-fi/sc-06.html) に対するものである。
粒度の規則は [SC-05](./SC-05_document-management.md) と共通である。

| # | モックの要素（行） | 実装 | 備考 |
| --- | --- | --- | --- |
| 1 | 見出し「データソース」（416 左） | **する** | `<h1>` |
| 2 | 「＋ ソース登録」（416 右） | **する** | `Button variant="primary"`。押すと登録フォームを開く |
| 3 | 一覧の**ソース**列（418・420-423） | **する** | 名前 ＋ 接続先 URI（`<code>`）。モックはパス（`\\fs01\share\規程集`）とサービス名を同じ列に描く |
| 4 | 一覧の**種別**列（418・420-423。ファイルサーバー／Wiki／SaaS／業務DB） | **する** | `Tag`（分類名）。`filesystem` / `wiki` / `saas` / `db` を**日本語の表示名へ写像する**（計画 §SC-06 主要素が 4 種の名前を与えている） |
| 5 | 一覧の**同期状態**列（418・420-423） | **する（導出できる範囲で）** | `StatusBadge`。**色 ＋ アイコン ＋ テキスト**（INDEX 決定 21）。導出規則は §同期状態の導出 |
| 6 | 同期状態の**「⚠ 再試行中（3/5）」**（422） | **しない** | **契約の不在**。§実装しない要素の理由 (a) |
| 7 | 一覧の**次回同期**列（418・420-423） | **しない** | 同上 (b) |
| 8 | 行操作「**手動同期**」（420-421） | **する** | `POST /bff/datasources/{id}/sync`（UC-04 **代替フロー**） |
| 9 | 行操作「**設定**」（422-423） | **しない** | 同上 (c) |
| 10 | 「変換ジョブの状況を見る →」（426） | **する** | `/admin/conversions`（SC-07）への内部リンク。計画の遷移図 `SC06 → SC07` |
| 11 | 注記「接続情報（認証情報）は Vault 管理。接続の継続失敗はアラート（UC-04 例外フロー）。」（427） | **する** | `Alert tone="info"`（静的な注記のため `role` を付けない） |
| 12 | **共通シェル**: 右レール「AIチャットパネル」（429-434） | **しない** | 移行**第 4 段**（[[IADR-0121]] 決定 1・5） |
| 13 | **共通シェル**: パンくず（413）・ブランド／アバター（412）・左ナビ（414） | **本画面では作らない** | パンくずは #452 系。他は `foundation/ui/Layout` が既に持つ |

### モックに無いが実装する要素

| 要素 | 計画上の根拠 |
| --- | --- |
| 登録フォームの項目（名前・種別・接続先 URI・既定の機密区分） | 05_screens §SC-06 主要素「ソース登録ボタン」「コネクタ設定」／ FR-01 ／ FR-05（既定機密区分は [[IADR-0019]] のフェイルセーフ） |
| 行操作: **無効化** | FR-01「データソースを**登録・同期し、カタログ化**する」のライフサイクル／ [[IADR-0039]]（Accepted） |

### 同期状態の導出（[[IADR-0127]] 決定 2）

| 条件 | 表示 | `StatusBadge` の `tone` | アイコン（`tone` から自動） |
| --- | --- | --- | --- |
| `status = disabled` | 無効 | `neutral` | `Info` |
| `status = active` かつ `lastSyncedAt` あり | 同期済み（日時） | `success` | `CircleCheck` |
| `status = active` かつ `lastSyncedAt` なし | 未同期 | `neutral` | `Info` |
| （同期異常） | — | **`warning`（琥珀）を空けてある** | — |

**琥珀（警告色）の充て先**: 05_screens モック間相違の確定 ②（2026-07-24）は
「SC-06 の**同期異常表示**の警告色＝琥珀（hi-fi を正）」と定める。すなわち琥珀が指すのは**異常**であり、
**管理者が意図して無効化した正常な設定状態ではない**。`disabled` へ琥珀を充てると「⚠ が付いた正常状態」が
生まれ、計画が琥珀へ与えた意味と表示の意味がずれる。同期異常は契約（`DataSourceDto`）に無く表示できないため、
**琥珀は同期健全性が契約に載るまで空けておく**——「異常」の語彙を先に使い切ると、契約が来たときに
区別する色が無くなる（[[IADR-0127]] 決定 2）。`disabled` は中立で示すが、
**色だけで意味を持たせない**（INDEX 決定 21）点は変わらない（`StatusBadge` が色 ＋ アイコン ＋ テキストを型で強制する）。

### 実装しない要素の理由（**いずれも繰り延べであって放棄ではない**）

| # | 計画の記述 | 現在の契約（実測） | 必要な変更 |
| --- | --- | --- | --- |
| (a) 「⚠ 再試行中（3/5）」 | hi-fi 422・§SC-06 アクション「同期異常は警告表示」・UC-04 例外「接続失敗時は再試行し、継続失敗はアラートする」 | `DataSourceDto(Id, Name, SourceType, ConnectionUri, Status, LastSyncedAt, Config, DefaultAttributes, CreatedAt)` で `Status` は **`active` / `disabled` の 2 値のみ**。連続失敗回数・再試行上限を持つフィールドが無い | `DataSourceDto` への同期健全性（連続失敗回数 / 上限 / 直近エラー）の追加 |
| (b) 「次回同期」列 | hi-fi 418・420-423（毎日 03:00 / 毎時） | 同期は `DataSourceSyncHostedService` が**全ソース共通の間隔**（`DataSourceSync__IntervalSeconds`。既定 300 秒）で回す。**ソース別のスケジュールという概念が無い** | ソース別スケジュール（cron 等）のモデル化と `DataSourceDto` への `NextSyncAt` |
| (c) 行操作「設定」 | hi-fi 422-423 | `/bff/datasources` に**更新（`PUT` / `PATCH`）が無い**。あるのは一覧・個別取得・登録・手動同期・無効化のみ | データソース更新 API |

実測の出所: `src/knowledge/backend/Shared/Knowledge.Contracts/Dtos/DataSourceDto.cs` ／
`src/knowledge/backend/Services/DataSourceService/src/DataSourceService.Api/Foundation/Domain/DataSource.cs` ／
`.../Foundation/Services/DataSourceSyncHostedService.cs` ／
`src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/DataSourceBffEndpoints.cs`（対象コミット `de55761`）。

**「押しても結果が変わらないボタン」「常に空の列」を置かない**（#502 が確立した規則）。
3 件は環流の記録に載せた（[feedback/20260805_sc05-07-admin-contract-gaps.md](../../feedback/20260805_sc05-07-admin-contract-gaps.md)。**planning#198 として起票済み・裁定待ち**）。

## データソース（BFF 境界）

| 用途 | エンドポイント | 呼び出し方 | 認可（サーバ側） | 応答 |
| --- | --- | --- | --- | --- |
| 一覧 | `GET /bff/datasources` | **orval 生成フック `useBffDataSourceList`**（#519） | admin / operator | `DataSourceDto[]` |
| 登録 | `POST /bff/datasources` | `useMutation` | 同上 | `DataSourceDto`（201） |
| 手動同期 | `POST /bff/datasources/{id}/sync` | `useMutation` | 同上 | 202 `DataSourceSyncResultDto`（`{ fetched, failed, connectorAvailable, message }`） |
| 無効化 | `DELETE /bff/datasources/{id}` | `useMutation` | 同上 | 204 |

- **orval 生成フックで呼ぶ**（#506 で契約が揃い、**#519** で載せ替えた。[[IADR-0135]] 決定 1）。
- **BFF は後段障害を空一覧へ縮退させない**（502 で可視化する）。「未登録」と誤認させて重複登録を招かないためであり、
  画面もこれに合わせて**取得失敗をエラーとして表示する**（0 件表示へ寄せない）。
- 更新系の成功後は `invalidateQueries({ queryKey: ['bff','datasources'] })` のみを行う（[[IADR-0127]] 決定 5）。

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
- 実装 ADR: [IADR-0127](../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md) / [IADR-0039](../adr/IADR-0039_datasource-management-bff-and-role-gating.md)
- 計画への環流（**planning#198 として起票済み・裁定待ち**）: [feedback/20260805_sc05-07-admin-contract-gaps.md](../../feedback/20260805_sc05-07-admin-contract-gaps.md)

## 未決事項

1. **同期異常（再試行中 N/M）の表示**（§実装しない要素 (a)）。`DataSourceDto` への同期健全性の追加が要る。
   **環流の記録を作成済み・planning#198 として起票済み（裁定待ち）。**
2. **次回同期**（同 (b)）。ソース別スケジュールのモデル化が要る。同上。
3. **コネクタ設定の編集**（同 (c)）。データソース更新 API が要る。同上。
4. **閲覧ロール**（admin/operator か admin のみか）。計画 §共通シェル と [[IADR-0039]] の差異。同上。
