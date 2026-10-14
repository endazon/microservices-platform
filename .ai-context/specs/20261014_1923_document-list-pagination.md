---
title: 作業仕様書 — 文書管理の一覧にページネーションを足す（gRPC ListDocuments のページングと BFF のオフセット窓・権限で絞った後の総件数。#1923 段 1 / #1897）
type: spec
status: done
related_ids: [FR-06, SC-05, SC-03, UC-03, NFR-09, NFR-16, ADR-0029, ADR-0075, IADR-0009, IADR-0041, IADR-0045, IADR-0379, IADR-0402, IADR-0447, IADR-0509, IADR-0533, IADR-0537]
author: claude
created: 2026-10-14
updated: 2026-10-14
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md（文書管理画面）
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-06）
issue: "#1923"
---

# 作業仕様書 — 文書管理の一覧のページネーション（#1923 段 1 / #1897）

> 本仕様書は実装着手前に作成した（着手 2026-10-14）。判断の記録は **IADR-0537** に置く。
> 基点は MSP `origin/develop` `22554203`。

## 起点

- 起点 issue: **#1923**（オーナーの確認。SC-05 の一覧が約 3.9 万件を一度に並べて見にくい）の **段 1（ページネーション）だけ**。
  段 2 以降（絞り込み・並べ替え・表題の部分一致・列の見直し・件数の要約・SC-03 への導線・機械取り込みの見分け）は入れない。
- 同じ作業として **#1897**（`ListDocuments` の gRPC 応答が受信上限 4 MB を超える件）の恒久対応を閉じる。
- 計画: FR-06（文書の CRUD）・SC-05（文書管理画面）・UC-03。**計画は一覧の送り方（ページングの有無・方式・大きさ）を定めていない**
  （画面仕様書 §未決事項 5）。送り方は実装判断として IADR-0537 に残し、計画への環流は下書きに留める（起票しない）。
- 守る判断: [IADR-0009](../adr/IADR-0009_wiki-browsing-404-hides-existence.md)（権限外の存在を秘匿する）・
  IADR-0041 / IADR-0045（文書の ABAC の実施点は BFF の `BffScopeResolver` ＋ `IsManageable`）・
  [IADR-0533](../adr/IADR-0533_east-west-rest-retirement.md)（読み取り 4 口の輸送は gRPC だけ）。

## 現状の確認（develop `22554203` の実物。issue の記述を検証した）

| issue の記述 | 実物 | 判定 |
| --- | --- | --- |
| `useDocumentAdmin.ts` は `GET /bff/documents` を 1 回呼び全件を並べる | `useAdminDocuments()` は `useBffDocumentList` を引数なしで呼び、`okArray` で配列を受ける。`DocumentManagementPage.tsx` は `rows.map` で全行を描く | 正しい |
| 画面仕様 §267 は「BFF が返す一覧をそのまま表示する」 | `docs/screens/SC-05_document-management.md:267` §未決事項 5 に同文 | 正しい（§ではなく 267 行目） |
| サーバも全件を返す（ListDocuments） | `DocumentReadUseCase.ListAsync` が台帳の全件を `OrderByDescending(UpdatedAt)` で読み、読める全件を返す。`GrpcService.ListDocuments` はそれを 1 応答へ詰める | 正しい |
| #1899 で受信上限を 64 MiB に上げた | `DocumentReadGrpcClient.DefaultMaxReceiveMessageSize = 64 * 1024 * 1024`、構成キー `Services:DocumentServiceGrpcMaxReceiveMessageSize` | 正しい（この PR では戻さない） |
| BFF は ABAC で絞る | `GET /bff/documents` は全件を引いてから `IsManageable`（`BffScopeResolver.Matches` ∧ 個人資料でない）で絞る。**後段は内容の ABAC の門が閉じている間、組織文書の ABAC を持たない** | 正しい。**ここが総件数の扱いを決める**（下記） |

## 設計（正は IADR-0537）

### gRPC `ListDocuments`（後段）

- `ListDocumentsRequest` に `page_size = 2`・`page_token = 3`、`ListDocumentsResponse` に `next_page_token = 2` を**足すだけ**（番号の追加のみ。後方互換）。
- `page_size`: 0 は既定 **500**、上限 **1000** を超えたら 1000 に丸める。負は `INVALID_ARGUMENT`。
- 並び: **更新日時の降順・同時刻は ID の降順**（従前の `UpdatedAt` 降順に ID のタイブレークを足して全順序にする）。
- 送り方: **キーセット**（前ページの末尾の `UpdatedAt` と `Id`）。トークンは不透明（base64url の `u1:<ticks>:<Id N 形式>`）。
  改ざん・不正は `INVALID_ARGUMENT`。**トークンは返した文書（読める文書）の末尾からしか作らない**（読めない文書の ID・時刻を載せない）。
- 読めない文書（他人の個人資料・門が開いたときの内容の ABAC）は**切り出す前**に除く（`GET /documents/page` の塊読みと同じ形）。
- **総件数は gRPC に足さない。** 後段が数えられるのは「後段が読める件数」であり、BFF の ABAC で絞る前の数である。
  返せば権限外の文書の存在を数で漏らす経路になる。
- 索引: `(UpdatedAt, Id)` の複合索引を足す（移行 1 本）。キーセットの開始位置に使う。
- REST `GET /documents` と MCP の `document.list_documents` は**変えない**（全件のまま。呼び出し元の母集合は下記）。

### `GET /bff/documents`（BFF）

- クエリ `offset`（既定 0。負は 400）・`limit`（既定 **50**、1〜**100** に丸める）。
- 応答を `DocumentListPageDto { items, total, offset, limit }` に変える（呼び出し元は SC-05 の 1 画面だけ。実測）。
- 送り方: **オフセット**。BFF が後段のページ（1000 件ずつ）を辿り、`IsManageable` で絞りながら**絞った後の順番**で窓（`offset`〜`offset+limit`）を切り出し、
  **絞った後の件数**を `total` に数える。権限外の文書は窓にも数にも入らない。
- 後段のトークンは BFF の外へ出さない（画面には `offset` / `limit` / `total` だけ）。
- 後段の不達・失敗は従前どおり**空のページ（total 0）**へ畳み、WARN を 1 行出す（途中のページで失敗しても部分の数を返さない）。
  ［2026-10-14 追記 / #1925 のレビュー］**空へ畳まず 502（ProblemDetails）を返す**に改めた（空へ畳むと本当の 0 件と区別できない。#1897 の本質）。画面はエラー表示。
- 費用: 1 要求ごとに台帳を辿る（O(N)。総件数を権限で絞った後に数えるため）。1 応答の大きさは台帳の件数に比例しない。

### 画面（SC-05）

- 件数の選択（25 / 50 / 100、既定 50）・前後のページ送り・ページ番号（先頭・末尾・現在の前後 1 つ、間は省略記号）・総件数と表示範囲。
- 前のページの表示を保ったまま次を読む（`placeholderData: keepPreviousData`）。
- 削除などで総件数が減り現在のページが末尾を越えたら、末尾のページへ戻す。
- 文言は Lingui（ja / en）。カタログを再生成する。

## 母集合（規則 9・10）

走査語: `ListDocuments` / `useAdminDocuments` / `useBffDocumentList` / `bff-document-list` / `BffDocumentList` /
`BFF が返す一覧をそのまま` / `ページングは恒久` / `"/bff/documents"`（`.ai-context/specs`・`.ai-context/superpowers`・`CHANGELOG.md`・生成物を除く全追跡ファイル）。

### 全件取得の呼び出し元

| 呼び出し元 | 経路 | 対応 |
| --- | --- | --- |
| BFF `FetchListAsync`（`GET /bff/documents`） | gRPC `ListDocuments` | **ページを辿る形へ変える**（本件） |
| `DocumentReadGrpcService.ListDocuments` | `DocumentReadUseCase.ListAsync` | **ページング版（`ListPageAsync`）へ替える**（本件） |
| REST `GET /documents`（`List/Endpoint.cs`） | `DocumentReadUseCase.ListAsync` | 変えない。BFF の呼び出しは IADR-0533 で撤去済みで、残る呼び出し元は試験と直接の運用だけ。射程外（#1897 は gRPC の件） |
| MCP `document.list_documents`（`McpTools/Execute/GrpcService.cs`） | `DocumentReadUseCase.ListAsync` | 変えない。射程外（エージェント向けの別の面。必要なら別 issue） |
| `GET /documents/page`（#1575） | `DocumentPageQuery.ReadPageAsync` | 変えない（作成日時昇順の別の口。並びを共有しない） |
| 画面 `useAdminDocuments` | `useBffDocumentList` | ページ引数を渡す形へ変える（本件） |
| AST（`ai-stock-trading`） | — | `/bff/documents` も `ListDocuments` も呼んでいない（`grep` で 0 件） |

### 追随する文書・コード

| 該当 | 対応 |
| --- | --- |
| `document_read.proto`（`ListDocumentsRequest` の「一覧に絞り込みは無い」・rpc の注記） | ページングの注記へ書き直す |
| `scripts/proto-contract-baseline.json` | `check-proto-contracts` の手順どおり追加分を反映する |
| `DocumentReadGrpcClient.DefaultMaxReceiveMessageSize` の注記（「ページングは恒久対応の段」） | ページングが入ったことと 64 MiB を据え置く理由へ書き直す |
| `ListPage/Endpoint.cs:13` の「BFF の一覧と gRPC `ListDocuments` が全件・同じ並びを前提」 | gRPC 側の前提が変わったことを書き直す |
| `docs/api/east-west-grpc.md:412`（「一覧の rpc は従来どおり全件を返す」） | ページングへ書き直す |
| `docs/api/openapi.yaml` `bff-document-list` | `offset` / `limit` と `DocumentListPageDto` |
| `docs/api/BFF_bff-surface.md:127` | 応答とクエリを一言足す |
| `docs/screens/SC-05_document-management.md` §データソース・§未決事項 5（267 行目） | ページングの節を足し、未決事項 5 は追記で決着を書く |
| `docs/tests/SC-05_document-management.md` | 新しい試験の行を足す |
| `docs/operations/east-west-grpc-h2c-roundtrip-measurement-runbook.md:198` | rpc 名の列挙だけで、全件の前提を書いていない。**変えない** |
| `.ai-context/adr/IADR-0475_…:63` / `IADR-0160_…:83` | 凍結記録。**変えない** |

除外: `.ai-context/specs/20261010_1897_grpc-receive-limit-stopgap.md`（確定済みの作業仕様書。凍結）。

## 試験

| 層 | 観点 | 期待 |
| --- | --- | --- |
| 後段（gRPC） | ページ境界 | 5 件を `page_size=2` で辿ると 2・2・1 件、重複も抜けも無く、最後の `next_page_token` が空 |
| 後段 | 上限の丸め | `page_size=5000` → 1 応答 1000 件で止まり続きがある。`page_size=0` → 既定 500 |
| 後段 | 負の `page_size` | `INVALID_ARGUMENT` |
| 後段 | 安定順序 | 更新日時が同じ文書は ID の降順。辿った結果が `UpdatedAt` 降順・`Id` 降順の全順序に一致 |
| 後段 | 読めない文書 | 他人の個人資料はページにも件数にも入らず、ページの末尾が読めない文書でもトークンはその ID を含まない |
| 後段 | トークンの改ざん | base64 でない・版が違う・ID が壊れた・範囲外の時刻 → `INVALID_ARGUMENT` |
| BFF | 窓と総件数 | 権限内 3 件・権限外 2 件で `limit=2` → 1 ページ目 2 件・`total=3`、2 ページ目 1 件 |
| BFF | 権限外が数に出ない | 権限外の文書を足しても `total`・ページの中身・境界が変わらない |
| BFF | 後段のページを辿る | 後段が 2 件ずつ返しても、窓と総件数は 1 回で引いた場合と同じ |
| BFF | 丸め・検証 | `limit=1000` → 100、`limit=0` → 1、`offset=-1` → 400、`offset` が末尾を越えたら空の `items` と同じ `total` |
| BFF | 縮退 | ~~後段の失敗は空のページ（`total=0`）と WARN 1 行~~ → 先頭・途中のページの失敗は 502 と WARN 1 行（空にも部分にも畳まない）。スコープ無しは空のページ |
| 画面 | 一覧の失敗 | 502 はエラー表示（再試行つき）。「文書はまだありません。」もページ送りも出さない |
| 画面 | ページ送り | 総件数・表示範囲を出し、次へで `offset=50` を要求する。先頭で「前へ」が無効 |
| 画面 | 件数の選択 | 25 を選ぶと `limit=25&offset=0` を要求する |
| 画面 | 末尾を越えた | 総件数が減って現在のページが範囲外になったら末尾のページを要求する |

## 検証

- `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes`（knowledge・platform の両ユニット）
- `src/` で `pnpm run lint` / `typecheck` / `test` / `format:check`、`pnpm run codegen` / `pnpm run i18n` の再生成差分なし
- `check-proto-contracts`・`check-i18n-catalogs`・`check-openapi-dto-drift`・文書系の検査器（`ci.yml` の docs ジョブの一式）

## 残余

- 1 要求ごとに台帳を辿る費用（O(N)）。内容の ABAC を後段へ寄せる（門が開く）か、絞り込みを後段へ渡す段で、総件数を SQL で数えられるようになる。
- #1899 の受信上限 64 MiB は戻さない（戻す条件は IADR-0537 の残余）。
- 計画への環流は下書き（`planning-feedback.md`）に留める。
