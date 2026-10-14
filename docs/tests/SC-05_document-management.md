---
title: SC-05 文書管理 テスト仕様書
type: test-spec
status: completed
created: 2026-07-09
updated: 2026-10-14
author: claude
---
<!-- trace:
ids: [FR-06, FR-09, SC-03, SC-05, SC-06, SC-07, UC-03]
adrs: [ADR-0031, ADR-0066]
iadrs: [IADR-0009, IADR-0019, IADR-0035, IADR-0041, IADR-0127, IADR-0308, IADR-0537]
specs: [20260805_issue-503_sc05-08-admin-screens, 20260830_issue-1065_feature-import-isolation, 20261014_1923_document-list-pagination]
issues: [#501, #1065, #1923, #1897]
-->

# テスト仕様書: 文書管理

> **［2026-08-05 / #503］新スタックでの再実装に合わせて全面改訂した。**
>
> **［2026-08-05 / #510］API 側（BFF の書き込み・DocumentService の状態遷移ガード）の節を復帰させた。**
> #503 の全面改訂はフロントエンドの構造で置き換えたため §バックエンド 2 節が落ちていたが、
> **当該テストは実在し続けている**（`BffDocumentWriteEndpointTests` / `DocumentVersioningTests` /
> `DocumentEndpointVersioningTests`）。落としたままにすると「画面のテストしか無い」と読め、
> 次に触る人が重複して書くか消してよいと判断する。**本復帰は当時の記載をそのまま戻したのではなく、
> 現在のテストの実物（クラス名・メソッド名・ファイルパス）と突き合わせて書き直したものである。**
> 同種の欠落の再発は [`check-test-spec-coverage.js`](../../scripts/check-test-spec-coverage.js) が止める。

対象（画面）: `src/knowledge/frontend/src/features/sc05-documents/`
テスト: `DocumentManagementPage.test.tsx`（Vitest + Testing Library）／
導線は `src/knowledge/frontend/src/features/adminFlow.test.tsx`／
E2E は `src/platform/frontend/e2e/sc05-documents.smoke.spec.ts`

対象（API）: [`src/platform/backend/Bff/Platform.Bff.Tests/BffDocumentWriteEndpointTests.cs`](../../src/platform/backend/Bff/Platform.Bff.Tests/BffDocumentWriteEndpointTests.cs) ／
[`src/knowledge/backend/Services/DocumentService/Tests/`](../../src/knowledge/backend/Services/DocumentService/Tests/)

## 起点となる計画書（トレーサビリティ）

- 画面: 文書管理画面 ／ ユースケース: **文書を管理する** ／ 機能要求: 文書 CRUD・版管理／属性・タグ管理

## ユースケースのフロー → テストの写像

| 文書管理のフロー | 画面での現れ方 | テスト |
| --- | --- | --- |
| **基本 1. 管理者が文書を登録／更新し、属性・タグを設定する** | 登録は `POST /bff/documents`、更新は `PUT`（`expectedVersion` つき） | `creates a document with the required confidentiality attribute and tags` ／ `updates a document with the optimistic-lock version and the change note` |
| **例外. 必須属性が未設定の場合は保存を拒否する** | タイトルが空（空白のみを含む）では保存ボタンが無効。注記も画面に出る | `refuses to save until the required title is filled (UC-03 exception flow)` |
| 基本 2. システムが取り込みイベントを発行し、索引と Wiki へ反映する | **写像しない**（サーバ側の責務）。画面は「保存 → 取り込み・Wiki同期をトリガ」を補助文で示すだけ | — |

## テストケース

| # | 観点 | 起点 | 検証内容 |
| --- | --- | --- | --- |
| 1 | 一覧 | —| `GET /bff/documents` を呼び、タイトル（`/docs/$id` へのリンク）・機密区分（**生値**）・版（`v{n}`）を表示する |
| 2 | 登録 | 文書管理 基本 1 / 属性・タグ管理 | タイトル・機密区分・タグを送る（`{ title, attributes: { confidentiality }, tags }`） |
| 3 | **必須属性** | **文書管理の例外フロー** | 空・空白のみでは保存できず、要求も出ない。注記が画面に出ている |
| 4 | 更新（楽観ロック） | —| 現在版を `expectedVersion` として送る。**既存の属性（部門）を落とさない** |
| 4-b | **再取得** | 管理画面の実装方針（決定 5） | 保存の成功後に一覧を **1 回だけ**取り直す（`invalidateQueries` のみ。手書きの再取得を持たない） |
| 5 | **版競合（409）** | —| 「版が変わっています」と読める文言を `role="alert"` で出す |
| 6 | 状態遷移 | 文書 CRUD・版管理 / 文書管理の BFF 集約 | 公開は未公開（`draft`/`normalized`）の行のみ・アーカイブは `archived` 以外の行のみ |
| 7 | 削除 | —| `DELETE /bff/documents/{id}` を呼び、完了を伝える |
| 8 | **存在秘匿（404）** | 権限外は 404 とする存在秘匿 / 文書管理の BFF 集約 | スコープ外・不在をいずれも中立に扱い、「権限がありません」を示唆しない |
| 8-b | **直近の操作結果だけを出す** | 管理画面の実装方針（決定 7） | 削除が 409 で失敗した後に別の操作（編集 → 保存）が成功したとき、**成功バナーの隣に古い失敗バナーが残らない** |
| 9 | 異常系 | — | 一覧の取得失敗で `role="alert"` |
| 10 | 0 件 | — | 「文書はありません。」 |
| 11 | **権限別の出し分け** | ロールベース・ナビゲーションと存在秘匿 | ロールを持たない利用者には画面が無い（`NotFound`）。**要求も出さない** |
| 12 | **契約の不在**（実装しない要素） | 画面仕様書 §hi-fi 対応 #6 | 「変換」列が無い。**先に「機密区分」「版」の列が在ることを確かめてから**無いことを見る |
| 13 | ロケール `en` | —| 見出し・保存ボタンが英語で描画される |

## 純関数（`src/knowledge/frontend/src/lib/abac/confidentiality.test.ts`）

機密区分の値集合は **文書管理画面（文書の機密区分。必須）とデータソース管理画面（既定の機密区分）が共有する語彙**であり、
`lib/abac/confidentiality.ts` に 1 つだけ置く。値集合は **ABAC の一次情報**
（計画 06_technical/07_abac-attribute-model の 4 値）に由来し、**増減は機密区分の取り違えに直結する**
（減れば選べない区分が生まれ、増えれば後段が知らない区分で保存される）。
画面テスト経由の間接被覆では「4 値であること」自体を固定できないため、直接固定する。

| # | 観点 | 検証内容 |
| --- | --- | --- |
| C1 | 値集合 | `public` / `internal` / `confidential` / `restricted` の **4 値ちょうど**である |
| C2 | フェイルセーフ既定 | 既定は `internal`（`public` の過剰公開でも `restricted` の過剰制限でもない。既定 ABAC 属性の付与規則による） |
| C3 | 属性キー | `confidentiality`（文書管理画面の一覧・フォームと、データソース管理画面の既定属性が同じキーで読み書きする） |

## 導線（`adminFlow.test.tsx`）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| A | 文書管理 → 文書詳細 | 一覧のタイトルから文書詳細へ遷移し、本文が表示される（計画の遷移図 `SC05 → SC03`） |

## BFF（書き込み・xUnit）

対象: [`Knowledge.Bff.Endpoints/DocumentBffEndpoints.cs`](../../src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/DocumentBffEndpoints.cs)（書き込み系）
テスト: [`Platform.Bff.Tests/BffDocumentWriteEndpointTests.cs`](../../src/platform/backend/Bff/Platform.Bff.Tests/BffDocumentWriteEndpointTests.cs)

| # | 観点 | 起点 | 検証内容 | ケース |
| --- | --- | --- | --- | --- |
| 1 | 作成 | —| admin で 201 | `Create_AsAdmin_Returns201` |
| 2 | ロール制限 | 文書管理の BFF 集約 | 非特権ロール（`viewer`）は 403 | `Create_AsNonPrivilegedRole_IsForbidden` |
| 3 | 無認証 | 同上 | 匿名は 401（認証欠如と権限不足を取り違えない） | `Create_WhenAnonymous_IsUnauthorized` |
| 4 | 作成の deny-by-default | 同上 | ABAC スコープが与えられていなければ 403 | `Create_WhenScopeNotGranted_IsForbidden_DenyByDefault` |
| 5 | 検証の透過 | —| 後段の 400（タイトル必須）を素通しする | `Create_WhenTitleMissing_Passes400Through` |
| 6 | 更新 | —| スコープ内で 200 | `Update_AsAdminInScope_Returns200` |
| 7 | スコープ外の更新 | 文書管理の BFF 集約 / 存在秘匿 | スコープ外は **404 秘匿**（403 にしない） | `Update_WhenOutOfScope_Returns404` |
| 8 | 楽観ロック | —| 後段の 409（版競合）を素通しする | `Update_WhenVersionConflict_Passes409Through` |
| 9 | 公開 | —| スコープ内で 200 | `Publish_AsAdminInScope_Returns200` |
| 10 | 削除 | —| スコープ内で 204 | `Delete_AsAdminInScope_Returns204` |
| 11 | スコープ外の削除 | 文書管理の BFF 集約 | スコープ外は 404 秘匿 | `Delete_WhenOutOfScope_Returns404` |

**画面のテスト（§テストケース 8・11）だけでは境界は塞げない。** UI の出し分けはサーバ側の実効境界の
写しであり、API を直接叩く経路は画面テストでは踏めないためである（変換ジョブ画面が #501 で踏んだのと同じ形）。

## バックエンド（DocumentService・状態遷移ガード・xUnit）

対象: [`Domain/Document.cs`](../../src/knowledge/backend/Services/DocumentService/Domain/Document.cs)（`Publish()` / `CanPublish`）と `POST /documents/{id}/publish`
テスト: [`DocumentVersioningTests.cs`](../../src/knowledge/backend/Services/DocumentService/Tests/Domain/DocumentVersioningTests.cs)（ドメイン）／
[`DocumentEndpointVersioningTests.cs`](../../src/knowledge/backend/Services/DocumentService/Tests/Features/Documents/DocumentEndpointVersioningTests.cs)（API）

| # | 観点 | 起点 | 検証内容 | ケース |
| --- | --- | --- | --- | --- |
| 1 | 不正遷移（ドメイン） | —| `archived` からの公開は例外・**状態は変わらない** | `Publish_FromArchived_Throws` |
| 2 | 許可遷移（ドメイン） | —| `normalized`（pipeline 由来）からの公開は許可（`draft` へ絞りすぎない） | `Publish_FromNormalized_IsAllowed` |
| 3 | 不正遷移（API） | —| archive 後の再公開は 409 | `Publish_AfterArchive_Returns409` |

**§テストケース 6（画面は `archived` の行に公開を出さない）と対である。** 画面が出さないことと
サーバが拒むことは別の担保であり、片方だけでは実効境界にならない。

## 一覧のページング（#1923）

> **［2026-10-14 追記 / #1923］** 一覧をページで出すようにした（送り方はオフセット・既定 50 件・総件数は権限で絞った後の数）。
> 3 層で固定する。**総件数が権限外の文書の存在を漏らさないこと**は BFF の層でしか測れない（絞っているのが BFF であるため）。

### 画面（Vitest）

テスト: `DocumentManagementPage.test.tsx` ／ `pageNumbers.test.ts`（純関数）／ `useDocumentAdmin.test.tsx`（無効化）

| # | 観点 | 検証内容 | ケース |
| --- | --- | --- | --- |
| P1 | ページ送り | 総件数と表示範囲を出し、「次へ」で `offset=50` を要求する。先頭では「前へ」を押せず、現在のページに `aria-current` | `shows the total and the range, and requests the next page` |
| P2 | 件数の選択 | 選択肢は 25 / 50 / 100（既定 50）。選ぶと `offset=0` から引き直す | `requests the first page again with the chosen page size` |
| P3 | 末尾を越えた | 総件数が減って現在のページが範囲外になったら末尾のページを引く | `moves back to the last page when the current page falls beyond the total` |
| P4 | ページ番号 | 先頭・末尾・現在の前後 1 つ。間は省略 | `pageNumbers`（6 ケース） |
| P5 | 無効化 | 一覧キーでの無効化が全ページのキーに届く | `reaches every page of the list through the list key` |
| P6 | **失敗を 0 件に見せない** | 一覧の 502 はエラー表示（再試行つき）。0 件の文言もページ送りも出さない | `shows the list failure (502) as an error, not as an empty list` |

### BFF（xUnit）

テスト: [`Platform.Bff.Tests/BffDocumentListPagingTests.cs`](../../src/platform/backend/Bff/Platform.Bff.Tests/BffDocumentListPagingTests.cs)

| # | 観点 | 検証内容 | ケース |
| --- | --- | --- | --- |
| B1 | 窓と総件数 | 権限内 3 件・権限外 2 件で、総件数 3・窓は絞った後の順番 | `窓と総件数は権限で絞った後の並びで数える` |
| B2 | **存在秘匿** | 権限外の文書を混ぜても、総件数・ページの中身・境界が変わらない（陽性対照: 区分を広げれば 5 件と数える） | `権限外の文書は総件数にもページの中身にも境界にも現れない` |
| B3 | 個人資料 | 区分では読める個人資料も総件数に入らない | `個人資料は総件数に入らない` |
| B4 | 後段のページを辿る | 後段が 1 件ずつ返しても結果は同じ。後段へは上限 1000 で求め、印を渡して終端まで辿る | `後段のページを辿っても窓と総件数は変わらない` |
| B5 | 丸め | `limit` は既定 50・1〜100 に丸める | `ページの大きさはサーバ側で丸める` |
| B6 | 末尾を越えた | 空の窓と同じ総件数 | `末尾を越えたoffsetは空の窓と同じ総件数を返す` |
| B7 | 改ざん | 負・数でない `offset` / `limit` は 400 | `負や数でないoffset_limitは400` |
| B8 | 縮退 | スコープが無ければ空のページ（総件数 0） | `スコープが無ければ空のページを返す` |
| B9 | **失敗を 0 件に畳まない** | 後段のページを辿る途中で失敗したら 502（空のページにも部分の数にも畳まない。本文に表題・件数を載せない） | `後段のページを辿る途中で失敗したら空にも部分にも畳まず502` |

### 後段（DocumentService・xUnit）

テスト: [`DocumentListPagingTests.cs`](../../src/knowledge/backend/Services/DocumentService/Tests/Features/Documents/DocumentListPagingTests.cs) ／
[`GrpcDocumentReadTests.cs`](../../src/knowledge/backend/Services/DocumentService/Tests/Features/Documents/GrpcDocumentReadTests.cs)

| # | 観点 | 検証内容 |
| --- | --- | --- |
| S1 | ページの境界 | 5 件を 2 件ずつで 2・2・1 件、重複・抜けなし、最後に印なし（倍数ちょうどでも空のページを返さない） |
| S2 | 安定順序 | 更新日時が同じ文書は ID の降順。境界が同時刻の間に落ちても抜けない |
| S3 | 読めない文書 | ページに入らず、続きの印も読めない文書を指さない（陽性対照: 所有者には返る）。長く続いても先へ届く |
| S4 | 印の改ざん | 壊れた印・作成日時の口の印・表せない時刻は読めず、gRPC では `INVALID_ARGUMENT` |
| S5 | ページの大きさ | 0 は既定 500、上限 1000 に丸める。負は `INVALID_ARGUMENT` |
| S6 | REST と同値 | 印を辿った全件が REST の一覧と順序まで一致する |
| S7 | 実 PostgreSQL | uuid の順・マイクロ秒の丸めをまたいで抜け・重複なし。`(UpdatedAt, Id)` の索引を後ろから、印の位置から引く（`DocumentListKeysetPostgresTests`。Docker が要る） |

## ABAC・存在秘匿の担保

- 読み取りは ABAC スコープ内のみ返る。書き込みは BFF が対象文書のスコープを先に確かめ、
  スコープ外・不在を**いずれも 404** で返す（文書管理の BFF 集約の実装判断）。画面は 404 を中立に扱い、
  「権限がありません」を示唆する文言を出さない（#8 で固定）。
- ロールを持たない利用者にはルートもナビも存在しない（#11 で固定）。

## 実行

- `pnpm run test -- knowledge/frontend/src/lib/abac`（純関数 **3** ケース。データソース管理画面と共有）
- `pnpm run test -- knowledge/frontend/src/features/sc05-documents`（単体。**15 ケース**）
  ——**表の行末番号（13）ではなく実測のケース数**である（`4-b` / `8-b` を含めて 15 行 = 15 ケース）。
- `pnpm run test -- knowledge/frontend/src/features/adminFlow.test.tsx`（導線）
- `pnpm run test:coverage`（カバレッジ・ラチェット維持）
- `dotnet test src/platform/backend/Bff/Platform.Bff.Tests --filter BffDocumentWriteEndpointTests`
- `dotnet test src/knowledge/backend/Services/DocumentService/Tests --filter Publish`
- `dotnet test src/platform/backend/Bff/Platform.Bff.Tests --filter BffDocumentListPagingTests`（一覧のページング）
- `dotnet test src/knowledge/backend/Services/DocumentService/Tests --filter "DocumentListPagingTests|ListDocuments_"`（同・後段）

<!-- trace-table:
row1: SC-05, FR-06
row2: FR-06
row3: FR-06
row4: FR-06
row5: ADR-0031
row6: FR-06
row7: FR-06
row8: FR-06
row9: FR-06
row10: FR-06
row11: FR-06
row12: UC-03
row13: FR-06
row14: UC-03
-->
