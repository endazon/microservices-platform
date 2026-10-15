---
title: 作業仕様書 — 文書管理の一覧に並べ替え・表題の部分一致・状態の絞り込み・件数の要約・本文の列を足す（#1923 段 2 の前半）
type: spec
status: done
related_ids: [FR-06, SC-05, SC-03, UC-03, NFR-09, NFR-16, ADR-0029, ADR-0075, IADR-0009, IADR-0041, IADR-0045, IADR-0388, IADR-0509, IADR-0537, IADR-0545]
author: claude
created: 2026-10-15
updated: 2026-10-15
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md（文書管理画面）
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-06）
issue: "#1923"
---

# 作業仕様書 — 文書管理の一覧の並べ替え・表題の部分一致・状態の絞り込み・件数の要約（#1923 段 2 の前半）

> 本仕様書は実装着手前に作成した（着手 2026-10-15）。判断の記録は **IADR-0545** に置く。
> 基点は MSP `origin/develop` `02b4d146`（段 1 = #1925 / IADR-0537 のマージ後）。

## 起点

- 起点 issue: **#1923**（オーナーの確認。SC-05 の一覧が「非常に見にくい」）の段 2 のうち、効果の大きいものを先に出す。
  本 PR の射程: **並べ替え（更新日時・作成日時・表題）・表題の部分一致・状態の絞り込み・件数の要約（全体と絞り込み後・状態別）・
  本文の有無の列・更新日時の列・長い表題の省略とホバーでの全文**。SC-03 への導線（表題のリンク）は段 1 以前から在る（実測。下記）。
- 射程外（次の PR 以降。issue は開けたまま `Refs #1923`）: データソースでの絞り込み・並べ替え（`DocumentDto` にデータソースが無い。
  台帳の `DataSourceName` を契約へ足す作業が要る）・機密区分・保存日・タグ・プロジェクトの絞り込み・本文のプレビュー（横のペイン）・
  元の URL へのリンク・機械取り込みの見分け（タブ）・**索引の状態**（下記）。
- 守る判断: IADR-0537 決定 2（`total` は ABAC で絞った後の数だけ）・IADR-0009（存在秘匿）・IADR-0041 / IADR-0045（ABAC の実施点は BFF）・
  IADR-0509（キーセットは SQL で並べて比べる。.NET で並べ直さない）。

## 現状の確認（develop `02b4d146` の実物）

| 依頼の記述 | 実物 | 判定 |
| --- | --- | --- |
| 各行から SC-03 への導線 | `DocumentRow` の表題が `<Link to="/docs/$id">`（段 1 以前から） | **在る**。本件は表題の省略とホバーでの全文だけを足す |
| 索引の状態の列（取り込み済み/保留/失敗） | 台帳（`Document`）にも `DocumentDto` にも索引の状態は無い。索引（埋め込み待ち等）は IngestionService（Qdrant の点）にあり、文書ごとに引く口は無い | **既存データで出せない**。台帳で出せる「本文の状態」（本文あり／本文なし〔原本を参照〕／本文未設定）を列にし、索引の状態は #1908 の側の口が要る（残余） |
| 状態の列 | `StatusBadge`（公開ライフサイクル 4 値）が在る | 在る。絞り込みと状態別の件数を足す |
| 並べ替えは keyset と整合 | 後段 `ListPageAsync` は `(UpdatedAt, Id)` 降順の 1 通りだけ。印は `u1:<ticks>:<id>` | 並べ替えキーごとの印が要る |
| 索引 | `(CreatedAt, Id)`（IADR-0509）と `(UpdatedAt, Id)`（IADR-0537）が在る。表題の索引は無い | 表題の並べ替えに `(Title, Id)` の索引を足す（Title は最大 500 文字で btree の 1 項目の上限に収まる） |

## 設計（正は IADR-0545）

### gRPC `ListDocuments`（後段）

- `ListDocumentsRequest.sort = 4`（enum `DocumentListSort`）を足す（番号の追加だけ。後方互換）。
  `UNSPECIFIED`（0）は従前の更新日時の降順。値: 更新日時の降順・昇順、作成日時の降順・昇順、表題の昇順・降順。未定義の値は `INVALID_ARGUMENT`。
- 並びは常に全順序（同値は ID。降順のキーは ID も降順、昇順は ID も昇順 —— 1 本の索引を前からも後ろからも辿れる向き）。
- 続きの印は並べ替えキーを持つ。更新日時の降順は従前の `u1:` のまま（配備の途中で新旧の document-service が混ざっても読める）。
  ほかは `s1:<キー>:<値>:<Id>`（表題は base64url で包む）。**要求の並べ替えと印の並べ替えが違えば `INVALID_ARGUMENT`**（黙って別の位置から読まない）。
- 索引 `(Title, Id)` を足す（移行 `AddDocumentTitleIdIndex`）。3 つのキーはすべて索引で開始位置を引ける。データソースの並べ替えは入れない。
- **絞り込みは rpc へ足さない**（下記の理由）。

### `GET /bff/documents`（BFF）

- クエリを足す: `sort`（`updated-desc` 既定 / `updated-asc` / `created-desc` / `created-asc` / `title-asc` / `title-desc`。未知は 400）、
  `q`（表題の部分一致。前後の空白を除き、空は無指定。大文字小文字を区別しない序数比較。201 文字以上は 400）、
  `status`（`draft` / `normalized` / `published` / `archived`。未知は 400）。
- 応答 `DocumentListPageDto` に足す: `unfilteredTotal`（ABAC で絞った後・利用者の絞り込みを当てる前の件数）、
  `statusCounts`（同じ集合の状態別の件数）。`total` は従前どおり窓の母数（ABAC ∧ 利用者の絞り込み）。
- 絞り込みは **BFF の窓の述語**（`IsManageable` の後）で当てる。全体の件数を出すには、どのみち 1 要求で台帳を 1 周する（IADR-0537 決定 2）。
  同じ 1 周で「全体」と「絞り込み後」と「状態別」を数えられる。絞り込みを rpc へ渡すと、全体の件数のためにもう 1 周が要る。
  → IADR-0537 の残余 (c)（「絞り込みを足したらサーバ側へ移す」）を IADR-0545 で改める。(a)(b)(d) の条件は据え置く。
- 🔴 存在秘匿: 権限外の文書は `total` / `unfilteredTotal` / `statusCounts` / 窓のどれにも入らない（試験で、権限外を混ぜた台帳と混ぜない台帳の応答の一致で固定）。

### 画面（SC-05）

- 一覧の上に操作の帯: 表題の検索（入力の 300 ms 後に引く）・状態の選択（「すべて」と 4 値。各値に件数を添える）・並べ替えの選択・「条件を解除」。
  条件を変えたら先頭のページへ戻す。帯は一覧の取得の状態に関わらず出す（0 件でも条件を戻せるように）。
- 件数の要約: 絞り込みが無ければ「全 n 件中 a–b 件」、在れば「絞り込み m 件（全 n 件）中 a–b 件」。
- 列: 表題（1 行で省略し、`title` 属性で全文。SC-03 へのリンクは従前どおり）・機密区分・状態・本文・更新日時・版・操作。
  本文の列は `hasBody` と `markdownUri` から「本文あり／本文なし（原本を参照）／本文未設定」を出す（色だけに頼らずテキストで出す）。
- 空の区別: 全体が 0 件なら従前の「文書はまだありません。」。絞り込みの結果だけが 0 件なら「条件に一致する文書はありません。」と条件の解除。
- 文言は Lingui（ja / en）。`pnpm run codegen` と `pnpm run i18n` の再生成物をコミットする。

## 母集合（規則 9・10）

走査語: `ListDocumentsRequest` / `DocumentListCursor` / `InListOrder` / `DocumentListPageDto` / `bff-document-list` / `段 2` /
`一覧に絞り込みは無い` / `更新日時の降順`（`.ai-context/specs`・`.ai-context/superpowers`・`CHANGELOG.md`・生成物を除く全追跡ファイル）。

| 該当 | 対応 |
| --- | --- |
| `document_read.proto` の `ListDocumentsRequest` の注記（「一覧に絞り込みは無い」「並びは更新日時の降順」） | 並べ替えを足した形へ書き直す |
| `scripts/proto-contract-baseline.json` | enum とフィールドの追加を反映する |
| `DocumentListCursor.cs` / `DocumentReadUseCase.cs`（`DocumentListOrder`） | 並べ替えキーごとに一般化する |
| `GrpcService.ListDocuments` | `sort` の検証と印の突き合わせ |
| `DocumentDbContext` / 移行 | `(Title, Id)` の索引 |
| `DocumentListWindow.cs` / `DocumentBffEndpoints.cs` / `DocumentReadGrpcClient.cs` | クエリの検証・述語・件数・並べ替えの受け渡し |
| `DocumentDto.cs` の `DocumentListPageDto` の注記（「並びは更新日時の降順」） | 並べ替えに応じる旨へ書き直す。項目を 2 つ足す |
| `scripts/contract-schema-baseline.json` | `DocumentListPageDto` の追加項目 |
| `docs/api/openapi.yaml`（`bff-document-list` と `DocumentListPageDto`） | クエリ 3 つと応答の 2 項目 |
| `docs/api/BFF_bff-surface.md` / `docs/api/east-west-grpc.md` の一覧の行 | 並べ替え・絞り込みを一言足す |
| `docs/screens/SC-05_document-management.md:182`（「段 2 以降は入れていない」） | 入れた分と残りを書き分ける |
| `docs/tests/SC-05_document-management.md` | 新しい試験の行 |
| 画面 `useDocumentAdmin.ts` / `DocumentManagementPage.tsx` / `DocumentPagination.tsx` | 条件・要約・列 |
| e2e `sc05-documents.smoke.spec.ts` / `a11y.smoke.spec.ts` | 応答の形のモックに項目を足す（必要な場合） |
| `.ai-context/adr/IADR-0537_…` | 凍結記録。本文は変えない（改める旨は IADR-0545 に書く） |

## 試験

| 層 | 観点 | 期待 |
| --- | --- | --- |
| 後段 | 並べ替えごとの全順序 | 6 通りの並べ替えで、`page_size=2` で辿った結果がキー（同値は ID）の全順序と一致し、重複も抜けも無い |
| 後段 | 印と並べ替えの不一致 | 表題の昇順の印を更新日時の降順の要求へ渡すと `INVALID_ARGUMENT` |
| 後段 | 未定義の並べ替え | enum に無い値は `INVALID_ARGUMENT` |
| 後段 | 印の改ざん | `s1` の壊れた値（キー不明・base64 でない表題・ID が壊れた）は `INVALID_ARGUMENT` |
| 後段 | 読めない文書 | 並べ替えを変えても、読めない文書はページにも印にも出ない |
| 後段（実 PostgreSQL） | 表題・作成日時の並べ替え | 同じ表題の群（uuid の順の取り違えで前後が入れ替わる Guid）を辿って抜けも重複も無い。`(Title, Id)` の索引が在る |
| BFF | 並べ替えの受け渡し | `sort=title-asc` で後段への要求が `TITLE_ASC`。未知の `sort` は 400 |
| BFF | 表題の部分一致 | 大文字小文字を区別せず一致した文書だけが窓と `total` に入り、`unfilteredTotal` は絞る前の数 |
| BFF | 状態の絞り込み | `status=published` で公開中だけ。`statusCounts` は状態別の数（絞り込みに依らない） |
| BFF | 存在秘匿 | 絞り込み・並べ替えを付けても、権限外の文書を混ぜた台帳と混ぜない台帳で応答が一致する |
| BFF | 検証 | 未知の `status`・201 文字の `q` は 400 |
| 画面 | 条件 | 検索語・状態・並べ替えを変えると `q` / `status` / `sort` と `offset=0` を要求する |
| 画面 | 要約 | 絞り込みが在れば「絞り込み m 件（全 n 件）中 a–b 件」 |
| 画面 | 空の区別 | 絞り込みの結果だけが 0 件なら「条件に一致する文書はありません。」と条件の解除 |
| 画面 | 本文の列 | `hasBody=false` は「本文なし（原本を参照）」、`markdownUri` が無ければ「本文未設定」 |

## 検証

- `dotnet build` / `dotnet test`（DocumentService.Tests・Platform.Bff.Tests・可能なら Knowledge.IntegrationTests の該当）/ `dotnet format --verify-no-changes`
- `src/` で `pnpm run lint` / `typecheck` / `test` / `format:check`、`pnpm run codegen` / `pnpm run i18n` の再生成差分なし
- `check-proto-contracts`・`check-i18n-catalogs`・`check-openapi-dto-drift`・`check-trace-blocks`・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`

## 残余

- 1 要求ごとに台帳を 1 周する費用（IADR-0537）は変わらない。表題の検索を打つたびに 1 周する（入力の 300 ms 待ちと 30 秒の `staleTime` で抑える）。
- 索引の状態（索引済み・埋め込み待ち・未索引）は既存データで出せない。IngestionService から文書ごとの状態を引く口が要る（#1908 の側）。
