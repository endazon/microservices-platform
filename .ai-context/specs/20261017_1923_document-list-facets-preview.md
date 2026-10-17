---
title: 作業仕様書 — 文書管理の一覧にデータソース・機密区分・保存日・タグ・プロジェクトの絞り込み、収集と人の文書のタブ、データソース・タグの列、元の URL、本文のプレビューを足す（#1923 の残り）
type: spec
status: done
related_ids: [FR-06, FR-09, SC-05, SC-03, UC-03, NFR-09, NFR-16, ADR-0029, ADR-0075, IADR-0009, IADR-0041, IADR-0045, IADR-0388, IADR-0537, IADR-0545, IADR-0561]
author: claude
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md（文書管理画面「一覧の送り方と件数」）
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-06）
issue: "#1923"
---

# 作業仕様書 — 文書管理の一覧の残り（#1923）

> 本仕様書は実装着手前に作成した（着手 2026-10-17）。判断の記録は **IADR-0561** に置く。
> 基点は MSP `origin/develop` `f0c76c05`（段 1 = #1925 / IADR-0537、段 2 の前半 = #1942 / IADR-0545、試験の追随 = #1962 のマージ後）。

## 起点

- 起点 issue: **#1923**（オーナーの確認。SC-05 の一覧が「非常に見にくい」）。依頼 1〜8 のうち、#1925・#1942・#1962 で入っていない分。
- 計画: §文書管理画面「一覧の送り方と件数」（2026-10-16 補完）は「並べ替えのキー・既定の並び・**絞り込みの選択肢は実装に任せる**」
  「件数はすべて ABAC で絞った後の数」と定める。本件の絞り込みと件数はこの射程の中にある。

## 未実装の表（issue 本文・全コメント・#1942 の差分を読んだ結果）

| 依頼 | 項目 | 状態（develop `f0c76c05`） | 本 PR |
| --- | --- | --- | --- |
| 1 | ページネーション | #1925 で済み | — |
| 2 | 状態の絞り込み | #1942 で済み | — |
| 2 | **データソース**の絞り込み | 無い。`DocumentDto` にデータソースが無い（台帳の `DataSourceName` は応答に出ていない） | **入れる**（契約に `dataSourceName` を足す。AST の収集は属性 `source`） |
| 2 | **機密区分**の絞り込み | 無い | **入れる**（4 値） |
| 2 | **保存日の範囲** | 無い | **入れる**（作成日時＝台帳に保存された日時の範囲） |
| 2 | **タグ**の絞り込み | 無い | **入れる**（タグ辞書の表示名の完全一致） |
| 2 | **プロジェクト**の絞り込み | 無い | **入れる**（属性 `project`。無い文書は「プロジェクトなし」） |
| 3 | 並べ替え（更新日時・表題） | #1942 で済み | — |
| 3 | 並べ替え（**データソース**） | 無い | **入れない**（下記の除外） |
| 4 | 表題の部分一致 | #1942 で済み | — |
| 5 | 表題・機密区分・更新日時・本文の有無の列、長い表題の省略＋ホバー | #1942 で済み | — |
| 5 | **データソース・タグ**の列 | 無い | **入れる** |
| 5 | **索引の状態**の列 | 無い | **入れない**（下記の除外） |
| 6 | 件数の要約（全体・状態別） | #1942 で済み | — |
| 6 | 件数の要約（**データソース別**） | 無い | **入れる**（`sourceCounts`。データソースの選択肢に添える） |
| 7 | SC-03 への遷移 | 在る（表題のリンク） | — |
| 7 | **本文の先頭のプレビュー（横のペイン）** | 無い | **入れる**（既存の本文の口 `GET /bff/documents/{id}/content` を使う） |
| 7 | **元の URL へのリンク** | 無い（台帳の `OriginalUri` が応答に出ていない） | **入れる**（契約に `originalUri` を足す。`http(s)` だけリンクにする） |
| 8 | **機械取り込みと人の文書の見分け** | 無い | **入れる**（タブ。既定は「人の文書・報告書」） |

### 除外したもの（理由）

- **索引の状態の列（依頼 5）**: BFF は返せない（実測）。索引の状態は IngestionService が Qdrant の点として持ち（#1908 で埋め込み待ちの点が入った）、
  文書ごとの状態を引く口は IngestionService にも BFF にも無い（`Program.cs` の口はイントロスペクションだけ）。出すには
  取り込み側に「文書 ID の集合 → 状態」を返す rpc と BFF の結合が要り、本件の窓の述語の形（台帳の 1 周）に載らない別の作業である。
  #1942 / IADR-0545 は「#1908 の側の作業」としたが、#1908 は閉じられており引き受け先が無い → **別 issue 化を提案する**（報告に書く。issue は開けたまま `Refs`）。
- **データソースでの並べ替え（依頼 3）**: IADR-0545 が「契約に無く索引も無い」として入れなかった。本件で契約に入るのは BFF での導出に使う
  材料（`dataSourceName`・属性 `source`）であり、並べ替えは後段のキーセット（台帳の索引）で行う作法（IADR-0545 決定 1）なので、
  導出したデータソースでは並べられない。代わりに**データソースで絞り込める**ので、棚卸しの用は足りる。
- コメントで不要・別 issue 化と決まった項目は無い（10/14〜10/16 のコメントは段の進め方・配備・PoC の確認だけ）。

## PR の分け方（2026-10-17 追記）

1 本にまとめた差分が約 2,300 行（試験・生成物・カタログを含む）になり、目安の 1,500 行を超えたので 2 つに分けた。

- **PR 1（絞り込み群）**: 契約（`dataSourceName` / `originalUri`）・BFF の全クエリと件数・OpenAPI と生成物・画面の帯の条件（データソース・機密区分・タグ・
  プロジェクト・保存日）・BFF / 契約 / 後段 / 画面の帯の試験・文書。
- **PR 2（列・見分け）**: タブ（既定は人の文書・報告書）・データソースとタグの列・出典のリンク・本文のプレビュー・タブの空の表示とその試験・文書。PR 1 の上に積む。

## 設計（正は IADR-0561）

### 契約（後段 → BFF）

- `DocumentDto` に `DataSourceName`（取り込み元のデータソースの表示名。直接投入・画面からの作成では null）と
  `OriginalUri`（原本の所在。API の書き手が渡した元の URL。AST の収集ではニュースの出典）を**末尾に足す**（既定 null。旧応答はどちらも null として読める）。
- gRPC `DocumentSummary` に `optional string data_source_name = 13` / `optional string original_uri = 14` を足す（番号の追加だけ）。
  写像 `DocumentReadGrpcMapping` は presence で運ぶ。REST の写像（Mapperly）は同名の列から写す。

### `GET /bff/documents`（BFF）

- クエリを足す（どれも省略可。**省略は従前と同じ応答**）:
  - `origin`: `collected`（機械の収集）/ `curated`（人の文書・報告書・データソースの取り込み）。
  - `source`: データソースの鍵（`sourceCounts` の鍵と同じ語彙。最大 300 文字）。
  - `confidentiality`: `public` / `internal` / `confidential` / `restricted`。
  - `tag`: タグの表示名（完全一致。最大 100 文字）。
  - `project`: 属性 `project` の値（完全一致。最大 100 文字）。`(none)` は「属性 `project` を持たない」。
  - `createdFrom`（含む）/ `createdBefore`（含まない）: ISO 8601 の日時。`createdFrom >= createdBefore` は 400。
- データソースの鍵（BFF が `DocumentDto` から導く。1 か所 `DocumentListSources`）:
  `ds:<DataSourceName>`（データソースの取り込み）→ `col:<属性 source>`（機械の収集）→ `report`（属性 `kind` と `periodKey` を持つ確定報告書の写し）→ `manual`（それ以外。画面・API からの作成）。
  `origin=collected` は `col:` の鍵、`curated` はそれ以外。
- 応答 `DocumentListPageDto` に `sourceCounts`・`projectCounts` を足す（`statusCounts` と同じく **ABAC 後・絞り込み前**の集合）。
- 🔴 絞り込みは IADR-0545 と同じく **ABAC の後**に窓の述語で当てる。件数はどれも ABAC 後。権限外の文書はどの件数にも入らない。

### 画面（SC-05）

- 表の上に**タブ**: 「人の文書・報告書（n）」（既定）・「機械の収集（m）」・「すべて（N）」。件数は `sourceCounts` から導く。
- 操作の帯に、データソース（件数つき）・機密区分・タグ（辞書）・プロジェクト（件数つき）・保存日（から・まで）を足す。
  「条件を解除」は帯の条件をすべて外す（タブと並べ替えは見せ方なので残す）。
- 列: 表題（元の URL があれば「出典 ↗」を添える）・データソース・機密区分・状態・本文・タグ（3 個まで＋残数）・更新日時・版・操作。
- 行に「プレビュー」。横のペイン（編集フォームの上）に本文の先頭 1,000 文字・SC-03 への導線・元の URL を出す。
  本文の状態（本文なし・未設定・大きすぎる・引けない）は文言で出す。
- 空の区別: 全体（`unfilteredTotal`）が 0 なら「文書はまだありません。」、タブだけで 0 件なら「このタブの文書はありません。」、
  帯の条件で 0 件なら従前の「条件に一致する文書はありません。」と「条件を解除」。

## 母集合（規則 9・10）

走査語: `DocumentListFilter` / `DocumentListPageDto` / `statusCounts` / `DocumentSummary` / `bff-document-list` / `BffDocumentList` /
`部分射影` / `入れていないもの（次の段）` / `データソースでの並べ替えは入れていない` / `索引の状態`
（`.ai-context/specs`・`.ai-context/superpowers`・`CHANGELOG.md`・生成物を除く全追跡ファイル）。

| 該当 | 対応 |
| --- | --- |
| `document_read.proto` の `DocumentSummary` | 2 項目を足す |
| `scripts/proto-contract-baseline.json` | 追加を反映する |
| `DocumentReadGrpcMapping.cs` | presence で写す |
| `DocumentMapper.cs` の注記（「原本 URI・取込元は応答に出さない」） | 2 つを出すように改め、注記を直す |
| `DocumentDto.cs`（`DocumentDto` / `DocumentListPageDto`） | 項目と注記 |
| `scripts/contract-schema-baseline.json` | 追加項目 |
| `DocumentListWindow.cs` / `DocumentBffEndpoints.cs` | クエリの検証・述語・件数 |
| `docs/api/openapi.yaml`・orval 生成物 | クエリ・応答の項目 |
| `docs/api/BFF_bff-surface.md` / `docs/api/east-west-grpc.md` の一覧の行 | 一言足す |
| `docs/screens/SC-05_document-management.md` §絞り込み・並べ替え の「入れていないもの（次の段）」 | 入れた分と残り（索引の状態） |
| `docs/tests/SC-05_document-management.md` | 試験の行 |
| 画面 `useDocumentAdmin.ts` / `DocumentListToolbar.tsx` / `DocumentManagementPage.tsx` / 新規のタブ・プレビュー | 条件・列・ペイン |
| e2e `sc05-documents.smoke.spec.ts` | 既定のタブで要求が `origin=curated` を持つ |
| IADR-0545 | 凍結記録。本文は変えない（残りを入れた旨は IADR-0561 に書く） |

## 試験

| 層 | 観点 | 期待 |
| --- | --- | --- |
| 契約 | 写像 | `dataSourceName` / `originalUri` は null と値が往復で保たれる（presence） |
| 後段 | REST の写像 | 台帳の `DataSourceName` / `OriginalUri` が応答に出る |
| BFF | データソースの鍵 | `ds:` → `col:` → `report` → `manual` の順で 1 つに決まる |
| BFF | 各絞り込み | データソース・機密区分・タグ・プロジェクト（`(none)` を含む）・保存日の範囲・タブで窓と `total` が絞られる |
| BFF | 件数 | `sourceCounts` / `projectCounts` は絞り込みに依らない（合計は `unfilteredTotal`） |
| BFF | 🔴 存在秘匿 | 新しい絞り込みを付けても、権限外の文書・他人の個人資料を混ぜた台帳と混ぜない台帳で応答（全件数・各件数・窓）が一致する（陽性対照つき） |
| BFF | 後方互換 | 新しいクエリを付けない要求の応答は従前と同じ（既存の試験が緑のまま） |
| BFF | 検証 | 未知の `origin` / `confidentiality`、長すぎる `source` / `tag` / `project`、壊れた日時、`createdFrom >= createdBefore` は 400（後段を呼ばない） |
| 画面 | タブ | 既定は `origin=curated`。タブを替えると `origin` と `offset=0` |
| 画面 | 帯 | 各条件の要求・「条件を解除」 |
| 画面 | 列 | データソース・タグ・出典のリンク（`http(s)` だけ） |
| 画面 | プレビュー | 本文の先頭を出す・本文の状態の文言 |

## 検証

- `dotnet build` / `dotnet test`（DocumentService.Tests・Knowledge.Contracts.Tests・Platform.Bff.Tests）/ `dotnet format --verify-no-changes`
- `src/` で `pnpm run lint` / `typecheck` / `test:coverage` / `format:check`、`pnpm run codegen` / `pnpm run i18n` の再生成
- `check-proto-contracts`・`check-trace-blocks`・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` ほか文書系

## レビューの指摘への対応（2026-10-17 追記）

- 収集の鍵 `col:<source>` は属性 `source` に上限が無く、300 文字を超える鍵が `sourceCounts` に出て選ぶと 400 になる経路があった → 鍵を受理の上限で切る（`DocumentListSources.KeyOf`。サロゲートペアは割らない）。BFF の試験を追加（変異 2 点で赤を確認）。
- 本 PR で使わない画面の補助関数（`documentSourceKey` ほか）を外した（knip の床。続く PR で使う形で入れる）。
- プロジェクト別の件数の鍵も受理の上限（100）を超え得た → 同じく切る。BFF の試験を追加（変異で赤を確認）。呼び出し元が無くなった旧い `DocumentListFilter.Parse(q, status, …)` を外した。テスト仕様書の H7 を現存の `sourceLabel` に合わせた。
- IADR の番号を 0561 へ改めた（0559 は #1987、0560 は #1989）。

## 残余

- 索引の状態の列（上の除外）。
- 1 要求ごとに台帳を 1 周する費用（IADR-0537）は変わらない。
