---
title: SC-05 文書管理 テスト仕様書
type: test-spec
status: completed
created: 2026-07-09
updated: 2026-10-17
author: claude
---
<!-- trace:
ids: [FR-06, FR-09, SC-03, SC-05, SC-06, SC-07, UC-03]
adrs: [ADR-0031, ADR-0066]
iadrs: [IADR-0009, IADR-0019, IADR-0035, IADR-0041, IADR-0127, IADR-0308, IADR-0537, IADR-0545, IADR-0561]
specs: [20260805_issue-503_sc05-08-admin-screens, 20260830_issue-1065_feature-import-isolation, 20261014_1923_document-list-pagination, 20261015_1923_document-list-sort-filter, 20261017_1923_document-list-facets-preview]
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
| P7 | 読み込み中 | 次のページを読む間は前のページの行を出したまま、ページ送り・件数の選択を止める（`keepPreviousData` と busy の変異で落ちる） | `keeps the previous page on screen and the pager busy while the next page loads` |
| P8 | 最後のページ | 「次へ」を押せない | `disables "next" on the last page` |
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
| B7 | 改ざん | 負の `offset`、数でない `offset` / `limit` は 400（負の `limit` は 400 にせず 1 に丸める。B5） | `負のoffsetと数でないoffset_limitは400` |
| B8 | 縮退 | スコープが無ければ空のページ（総件数 0） | `スコープが無ければ空のページを返す` |
| B9 | **失敗を 0 件に畳まない** | 後段のページを辿る途中で失敗したら 502（空のページにも部分の数にも畳まない。本文に表題・件数を載せない）。WARN を 1 行出す | `後段のページを辿る途中で失敗したら空にも部分にも畳まず502` |

### 後段（DocumentService・xUnit）

テスト: [`DocumentListPagingTests.cs`](../../src/knowledge/backend/Services/DocumentService/Tests/Features/Documents/DocumentListPagingTests.cs) ／
[`GrpcDocumentReadTests.cs`](../../src/knowledge/backend/Services/DocumentService/Tests/Features/Documents/GrpcDocumentReadTests.cs)

| # | 観点 | 検証内容 |
| --- | --- | --- |
| S1 | ページの境界 | 5 件を 2 件ずつで 2・2・1 件、重複・抜けなし、最後に印なし（倍数ちょうどでも空のページを返さない） |
| S2 | 安定順序 | 更新日時が同じ文書は ID の降順。境界が同時刻の間に落ちても抜けない |
| S3 | 読めない文書 | ページに入らず、続きの印も読めない文書を指さない（陽性対照: 所有者には返る）。長く続いても先へ届く |
| S4 | 印の改ざん | 壊れた印・作成日時の口の印・表せない時刻は読めず、gRPC では `INVALID_ARGUMENT` |
| S5 | ページの大きさ | 0 は既定 500、上限 1000 に丸める（rpc を通して: `page_size=5000` は 1000 件で止まり続きの印が付く、`0` は 500 件）。負は `INVALID_ARGUMENT` |
| S6 | REST と同値 | 印を辿った全件が REST の一覧と順序まで一致する |
| S7 | 実 PostgreSQL | uuid の順・マイクロ秒の丸めをまたいで抜け・重複なし。`(UpdatedAt, Id)` の索引を後ろから、印の位置から引く（`DocumentListKeysetPostgresTests`。Docker が要る） |

## 一覧の並べ替え・絞り込み（#1923 段 2）

> **［2026-10-15 追記 / #1923 段 2］** 並べ替え（更新日時・作成日時・表題）・表題の部分一致・状態の絞り込み・件数の要約・本文と更新日時の列。
> 並べ替えは後段のキーセット、絞り込みは BFF が ABAC の後に当てる。**件数が権限外の文書の存在を漏らさないこと**は BFF の層で測る。

### 画面（Vitest）

テスト: `DocumentManagementPage.test.tsx` ／ `documentBody.test.ts`（純関数）

| # | 観点 | 検証内容 | ケース |
| --- | --- | --- | --- |
| F1 | 並べ替え | 選ぶと `sort` を載せて `offset=0` から引く。既定（更新日時が新しい順）は載せない | `requests the chosen sort from the first page and omits the default` |
| F2 | 表題の検索 | 入力が止まってから 1 回だけ、前後の空白を除いて `q` を送る。要約は「絞り込み m 件（全 n 件）中 a–b 件」 | `requests the title query once after typing stops, trimmed` |
| F3 | 状態 | 選択肢に状態別の件数を添え、選ぶと `status` を送る。「条件を解除」で条件を外して先頭から引く | `filters by status with per-status counts and clears the filters` |
| F4 | 空の区別 | 絞り込みの結果だけが 0 件なら「条件に一致する文書はありません。」（「文書はまだありません。」を出さない） | `distinguishes "no match" from "no documents yet"` |
| F5 | 列 | 本文の状態（本文あり／本文なし（原本を参照）／本文未設定）と更新日時。長い表題は省略し、全文は `title` とリンクの名前に残す | `shows the body state and the update time, and keeps the full title on hover` |
| F7 | **解除で入力欄も戻す** | 表題の検索で 0 件にした後、空の表示の「条件を解除」で入力欄が空になり、待ち時間の後も古い語で引き直さない | `clearing from the empty result also clears the title input and does not re-apply the query` |
| F6 | 本文の状態の導出 | `hasBody=false` は「本文なし」、参照が無ければ「本文未設定」 | `documentBodyState`（3 ケース） |

### BFF（xUnit）

テスト: [`Platform.Bff.Tests/BffDocumentListFilterTests.cs`](../../src/platform/backend/Bff/Platform.Bff.Tests/BffDocumentListFilterTests.cs)

| # | 観点 | 検証内容 | ケース |
| --- | --- | --- | --- |
| G1 | 並べ替えの受け渡し | `sort` の 6 値（と省略）が後段の enum へ写る。後段のページを辿る間も同じ並べ替えで要求する | `並べ替えは後段の要求へ渡す` ／ `後段のページを辿る間も同じ並べ替えで要求する` |
| G2 | 表題の部分一致 | 大文字小文字を区別しない。窓と `total` は一致した文書だけ、`unfilteredTotal` は絞る前の数。前後の空白は除く | `表題の部分一致は大文字小文字を区別せず窓と総件数を絞る` ／ `表題の検索語は前後の空白を除く` |
| G3 | 状態・状態別の件数 | `status` で絞る。`statusCounts` は絞り込みに依らない | `状態で絞り状態別の件数は絞り込みに依らない` ／ `絞り込みの窓は絞った後の順番で切る` |
| G4 | **存在秘匿** | 絞り込み・並べ替えを付けても、権限外の文書を混ぜた台帳と混ぜない台帳で応答が一致する（陽性対照: 区分を広げれば権限外も一致して数える） | `権限外の文書は絞り込みの件数にも状態別の件数にも現れない` |
| G6 | **重複を数えない** | 走査の途中で更新された文書を後段が次のページで再び返しても（昇順）、件数にも窓にも 1 度だけ | `走査の途中で更新されて後段が同じ文書を再び返しても二重に数えない` |
| G5 | 検証 | 未知の `sort` / `status`（大文字違いを含む）は 400 で、後段を呼ばない。`q` は 200 文字まで | `未知の並べ替えと状態は400` ／ `表題の検索語は200文字まで` |

### 後段（DocumentService・xUnit）

テスト: [`DocumentListPagingTests.cs`](../../src/knowledge/backend/Services/DocumentService/Tests/Features/Documents/DocumentListPagingTests.cs) ／
[`GrpcDocumentReadTests.cs`](../../src/knowledge/backend/Services/DocumentService/Tests/Features/Documents/GrpcDocumentReadTests.cs) ／
[`DocumentListKeysetPostgresTests.cs`](../../src/knowledge/backend/Tests/Knowledge.IntegrationTests/DocumentService/DocumentListKeysetPostgresTests.cs)

| # | 観点 | 検証内容 |
| --- | --- | --- |
| T1 | 並べ替えごとの全順序 | 6 通りの並べ替えで、印を 2 件ずつ辿った結果がキー（同値は ID・キーと同じ向き）の全順序と一致し、重複も抜けも無い |
| T2 | 読めない文書 | 表題の並べ替えでも、読めない文書はページにも印にも出ない |
| T3 | 印と並べ替え | 別の並べ替えの印は読まない（使い方の誤りは例外、rpc では `INVALID_ARGUMENT`。陽性対照: 同じ並べ替えなら続きが読める） |
| T4 | 印の往復・改ざん | 並べ替えごとの印は往復で値が変わらない（表題の `:`・日本語を含む）。既定の並びは従前の形の印。壊れた印（キー不明・表題が壊れた・時刻が数でない・ID が壊れた）は読めない |
| T5 | 未定義の並べ替え | enum に無い値は `INVALID_ARGUMENT`（既定へ畳まない） |
| T6 | 実 PostgreSQL | 同じ表題・同じ作成時刻の群（uuid の順の取り違えで前後が入れ替わる Guid）を表題・作成日時・更新日時の昇降で辿って抜け・重複なし。`(Title, Id)` の索引を印の位置から引く（昇順は前から・降順は後ろから。Docker か外部の PostgreSQL が要る） |

## 一覧の追加の絞り込み（#1923 の残り）

> **［2026-10-17 追記 / #1923］** データソース・機密区分・タグ・プロジェクト・保存日の絞り込み、データソース別・プロジェクト別の件数。
> 絞り込みは段 2 と同じく BFF が ABAC の後に当てる。BFF はタブ（機械の収集か否か）の絞り込みも受ける（画面のタブは続く変更）。

### 画面（Vitest）

テスト: `DocumentManagementPage.test.tsx` ／ `documentSource.test.ts`（純関数）

| # | 観点 | 検証内容 | ケース |
| --- | --- | --- | --- |
| H2 | 帯の条件 | データソース（件数つき・表示名）・機密区分・タグ（辞書）・プロジェクト（「プロジェクトなし」）・保存日がそれぞれのクエリに写る。「まで」は翌日 0 時を含まない終わりとして送る。「条件を解除」は帯の条件を外す | `sends each facet, the inclusive date range and clears them` |
| H7 | 鍵の導出 | データソース → 収集 → 報告書 → 手入力の順（BFF と同じ例）。表示名・タブの件数・リンクにしてよい URL | `documentSourceKey` ／ `sourceLabel / originCounts / linkableOriginalUri` |

### BFF（xUnit）

テスト: [`Platform.Bff.Tests/BffDocumentListFacetTests.cs`](../../src/platform/backend/Bff/Platform.Bff.Tests/BffDocumentListFacetTests.cs)

| # | 観点 | 検証内容 | ケース |
| --- | --- | --- | --- |
| J1 | 鍵の導出 | `ds:` → `col:` → `report`（`kind` と `periodKey` の両方）→ `manual` の順で 1 つに決まる。タブは `col:` だけが収集 | `データソースの鍵はデータソース_収集_報告書_手入力の順で決まる` |
| J2 | 各絞り込み | タブ・データソース・機密区分・タグ・プロジェクト（`(none)`）・保存日（オフセットを読む・終わりは含まない）が窓と `total` を絞り、`unfilteredTotal` は絞る前の数 | `絞り込みは窓と総件数を絞り全体の件数は絞る前の数` |
| J3 | 件数 | `sourceCounts` / `projectCounts` は絞り込みに依らず、合計は `unfilteredTotal` | `データソース別とプロジェクト別の件数は絞り込みに依らない` |
| J4 | 後方互換 | 新しいクエリを付けない要求は従前どおり全件（ABAC 後）。データソースの表示名と原本の所在が gRPC を往復して応答に載る | `新しいクエリを付けない要求は従前どおり全件を返す` ／ `データソースの表示名と原本の所在が一覧の応答に載る` |
| J5 | **存在秘匿** | 新しい絞り込みを付けても、権限外の文書を混ぜた台帳と混ぜない台帳で応答（全件数・各件数・窓）が一致する（陽性対照つき） | `権限外の文書は新しい絞り込みの件数にもデータソース別の件数にも現れない` |
| J6 | **存在秘匿（個人資料）** | 他人の個人資料も、新しい絞り込み・データソース別・プロジェクト別の件数のどれにも現れない（陽性対照つき） | `他人の個人資料は新しい絞り込みの件数にもプロジェクト別の件数にも現れない` |
| J7 | 許可ポリシーなし | 件数はどれも 0 / 空 | `許可ポリシーが無いときデータソース別とプロジェクト別の件数は空` |
| J9 | **件数の鍵は選べる** | 長い出典名の鍵は受理の上限で切って数え（サロゲートペアは割らない）、切った鍵で絞ると 400 にならずその文書に当たる | `長い出典名の鍵は受理の上限で切って数え選べる` |
| J8 | 検証 | 未知の `origin` / `confidentiality`、壊れた日時、`createdFrom >= createdBefore` は 400 で後段を呼ばない。`source` / `tag` / `project` は上限ちょうどまで | `未知の値と壊れた日時と逆向きの範囲は400で後段を呼ばない` ／ `長すぎる値は400で上限ちょうどは通る` |

### 契約・後段（xUnit）

テスト: `Knowledge.Contracts.Tests/DocumentReadGrpcMappingTests.cs` ／ `DocumentService.Tests/.../DocumentMapperTests.cs`

| # | 観点 | 検証内容 |
| --- | --- | --- |
| K1 | gRPC の写像 | データソースの表示名・原本の所在は presence で運び、null と空文字を分ける（全項目の往復の器にも足した） |
| K2 | REST の写像 | 台帳の `DataSourceName` / `OriginalUri` が同名の列から写り、無ければ null |

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
- `dotnet test src/platform/backend/Bff/Platform.Bff.Tests --filter BffDocumentListFilterTests`（一覧の並べ替え・絞り込み）
- `PLATFORM_TEST_POSTGRES=<接続文字列> dotnet test src/knowledge/backend/Tests/Knowledge.IntegrationTests --filter DocumentListKeysetPostgresTests`（実 PostgreSQL）

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
