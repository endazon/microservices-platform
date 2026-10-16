---
title: 作業仕様書 — 計画の補完（planning#799 / planning#796）を受け、SC-05 の一覧の件数は ABAC で絞った後の数という共通則を、個人資料と許可ポリシー無しの経路まで試験で固定し、画面仕様書を計画の決着へ追随させる
type: spec
status: done
related_ids: [FR-06, FR-19, SC-05, SC-02, SC-09, SC-10, SC-18, SC-21, UC-03, ADR-0004, ADR-0036, ADR-0043, ADR-0056, ADR-0100, IADR-0009, IADR-0537, IADR-0545]
author: claude
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md（§共通シェル「横断の表示規約（一覧の件数）」・§SC-05「一覧の送り方と件数」・§SC-02「送り方と件数」）
  - planning:projects/microservices-platform/10_feedback/20261016_sc05-paging.md
issue: "#1923"
---

# 作業仕様書 — SC-05 の一覧の件数の共通則（計画の補完 planning#799 の実装側の追随）

> 本仕様書は着手前に作成した（2026-10-16）。基点は MSP `origin/develop` `3b49d911`。
> 計画の差分は planning `origin/main` の `fc61ca3`（planning#799。環流元 planning#796）を `git show` で読んだ。

## 起点

- 計画の補完 planning#799（2026-10-16 利用者裁定。環流 planning#796）。実装の起点 issue は **#1923**（SC-05 の一覧が見にくい。段 1 = #1925 / IADR-0537、段 2 = #1942 / IADR-0545 でマージ済み。段 3 以降が残るため **open のまま。本 PR は `Refs #1923`**）。
- 同件検索: MSP の open PR は dependabot と CHANGELOG 自動更新だけで、SC-05 の件数を扱う PR は無い。#796 を起点にした MSP issue は無く、#1923 が環流の出典（planning#796 の `source_ref`）である。新規起票はしない。

## 計画の差分（fc61ca3 の読み）

| 箇所 | 計画が定めたこと | 値の持ち主 |
| --- | --- | --- |
| §共通シェル「横断の表示規約（一覧の件数）」（新設） | **一覧に出す件数（総件数・絞り込み後・状態別）はすべて ABAC で絞った後の数**とする（権限外の存在を数で示さない）。理由は ADR-0043 決定 2 の理由の援用。存在秘匿の正本（ADR-0056・ADR-0004）は件数を射程に入れておらず本則が補う。**例外は SC-10**（集計を権限で絞らずロールを絞る）。**SC-09 の使用件数は未決**。件数を新たに出す画面は、絞った後の数を出せるまで件数を出さない | 計画 |
| §SC-05 主要素・「一覧の送り方と件数」 | **一覧はページで表示する**（件数の選択・ページ送り・総件数の 3 要素）。並べ替え・表題の部分一致・状態の絞り込みを要素として名を挙げる。**件数の選択肢・既定値・送り方の方式・並べ替えのキー・絞り込みの選択肢は計画に持たず実装（IADR-0537 決定 2・3、IADR-0545）を参照する**。状態の語彙のずれ（実装 `draft`/`normalized`/`published`/`archived` と計画 `lifecycle`）は既知で本件では裁定しない。「現在の実現手段: 実装済み」「暫定手段: 要らない」 | 実装 |
| §SC-05 主要素「変換状況」 | 取り消し線で訂正（2026-08-05 に削除確定済み） | — |
| §SC-02「送り方と件数」 | **送り方は SC-05 と揃えない**（関連度の上位 N 件）。件数は共通則に従う。実装の `totalHits` は返した件数そのもの（20 以下）で全体のヒット数ではない。**ページングの要否・全体のヒット数にするかは未決**（利用者の残作業） | 未決 |

計画は「実装: 追随の改修は無い」としている（完了記録 §残るもの）。

## 現状の確認（develop `3b49d911` の実物）と計画との差

| 計画の要求 | 実物 | 判定 |
| --- | --- | --- |
| ページ表示（件数の選択・ページ送り・総件数） | BFF `GET /bff/documents?offset&limit`（`DocumentListWindow`。既定 50・1〜100 に丸め）、画面 `DocumentManagementPage`（25/50/100・前後・番号・「全 n 件中 a–b 件」） | 一致 |
| 並べ替え・表題の部分一致・状態の絞り込み | `sort` / `q` / `status`（IADR-0545）と操作の帯 | 一致 |
| 件数（`total` / `unfilteredTotal` / `statusCounts`）は ABAC の後 | `DocumentListWindow.Collector.AddRange` の順序が「重複除去 → `visible`（`IsManageable` = ABAC スコープ ∧ 個人資料でない） → 全体・状態別 → 利用者の絞り込み → 窓」 | 一致 |
| 試験で固定（存在推知の防止） | `BffDocumentListPagingTests`（`total`・窓・境界の陰性＋陽性対照）・`BffDocumentListFilterTests`（絞り込み・状態別の陰性＋陽性対照） | **一部の穴**（下記） |
| 画面仕様書（docs/screens/SC-05） | §ページング・§絞り込み は「計画は送り方／絞り込みを定めていない」、§未決事項 5 の追記は「計画へ環流するかは計画側の判断（下書き未起票）」のまま | **古い**（計画が補完した） |
| 画面仕様書（docs/screens/SC-02） | §未決事項 2「ページングの要否は計画の裁定を待つ」。§要素の「表示件数が総数より少ない場合は（表示 N 件）を添える」は `totalHits` が返した件数そのものなので出ない | 計画が「揃えない」「要否は未決」と事実を書いた。追記で追随する（検索系サービスのコードは触らない） |

### 試験の穴

1. **個人資料（`IsManageable` の第 2 項）は `total` だけを固定していた。** 段 2 で足した `unfilteredTotal` / `statusCounts` に他人の個人資料が入らないこと、絞り込みを付けても応答が個人資料の有無で変わらないことは固定されていない。区分（ABAC）では読める個人資料なので、ABAC の陰性試験（confidential を混ぜる）では捕まらない。
2. **許可ポリシーが無いときの空のページ**は `items` / `total` だけを確かめ、`unfilteredTotal` / `statusCounts` を確かめていない。
3. いずれも現在の実装は正しい（`Empty()` は既定値 0 / 空、`visible` の後で数える）。**変異で赤になることを確かめて足す**（下の受け入れ基準 3）。

## 是正・追随の母集合（traceability.repo.md 規則 9・10）

「件数」を返す BFF の契約と、件数を表示する画面を、誤りの側の文字列（`Total` / `Count` / `件数` / `件`）で走査した（`grep -rnE` を `src/knowledge/backend/Shared/Knowledge.Contracts/Dtos`・`src/platform/backend/Bff`・`src/knowledge/frontend/src`・`src/platform/frontend/src` に当てた）。

| 画面 | 件数 | 共通則との関係 | 本 PR |
| --- | --- | --- | --- |
| SC-02 検索結果 | `SearchResponse.TotalHits`（ABAC で絞った後の結果の件数。返した件数そのもの） | 合う（ABAC の後）。全体のヒット数にするかは計画で未決 | 画面仕様書へ追記のみ |
| SC-05 文書管理 | `DocumentListPageDto.Total` / `UnfilteredTotal` / `StatusCounts` | 合う | 試験の穴を塞ぐ・画面仕様書を追随 |
| SC-09 管理（ABAC） | `TagDto.UsageCount` / `EdgeTypeDictionaryEntryDto.UsageCount`（admin ＋ operator。辞書の値ごと） | 計画で未決（例外とするか） | 対象外（未決） |
| SC-10 運用ダッシュボード | `FeedbackStatsDto.Total`・集計 | 名指しの例外 | 対象外 |
| SC-18 知識グラフ | `GraphViewDto.TotalNodes` / `TotalEdges`（許可済みの総数。ADR-0049 決定 1） | 合う（前例） | 対象外 |
| SC-21 AI 提案 | 権限外の文書の提案は件数にも出ない | 合う（前例） | 対象外 |
| SC-19 / SC-20 個人資料・同期 | `PrivateNoteDto.SharedUserCount` 等・`SyncTargetFolderDto.NoteCount` | 本人の資料だけ | 対象外 |
| SC-07 変換ジョブ | 再変換で失われる補正の件数（ジョブ単位） | 一覧の件数ではない | 対象外 |
| （画面なし）AST の古い写しの棚卸し | `AstStaleCopyTargets.Total`（文書サービスの運用口） | 画面の一覧の件数ではない | 対象外 |

除外理由: 共通則の射程は「共通シェル配下の画面が一覧に出す件数」。上表の対象外は、例外（SC-10）・未決（SC-09）・本人だけ（SC-19/20）・一覧の件数でない（SC-07・運用口）・既に適合（SC-18/21）。**新たに是正の要る実装は見つからなかった。**

規則 10（この変更で新たに誤りになる自分の記述）: SC-05 画面仕様書の「計画は送り方を定めていない」（§ページング）・「計画は一覧の絞り込み・並べ替えを定めていない」（§絞り込み）・§未決事項 5 の追記「計画へ環流するかは計画側の判断」の 3 箇所。凍結記録（IADR-0537 / IADR-0545・既存の作業仕様書）の同旨の記述は書き換えない。

## 方式

1. **試験**（`src/platform/backend/Bff/Platform.Bff.Tests/`）:
   - `BffDocumentListFilterTests` に、他人の個人資料（区分は internal で読める・状態と表題が絞り込みに一致する）を混ぜた台帳と混ぜない台帳で、`q` / `status` / `sort` / 窓を付けた応答（`total` / `unfilteredTotal` / `statusCounts` / 窓）が一致することを足す。陽性対照として、個人資料は区分では読める（`IsManageable` の第 2 項が落としている）ことを先に確かめる。
   - `BffDocumentListPagingTests` の「スコープが無ければ空のページ」に `unfilteredTotal` 0・`statusCounts` 空を足す。
2. **文書**: `docs/screens/SC-05_document-management.md` の §ページング・§絞り込み・§未決事項 5 に計画の補完を追記（当時の記述は残す）。trace ブロックへ `planning#796` / `planning#799` を足す。`docs/screens/SC-02_search-results.md` §未決事項 2 に計画の決着（揃えない・要否は未決）と `totalHits` の事実を追記。
3. 実装コード・契約・orval 生成物・openapi は変えない（計画が「追随の改修は無い」とし、実測でも差が無い）。**IADR は起こさない**（新しい判断が無い）。

## 受け入れ基準

1. 個人資料を混ぜても、絞り込み・並べ替え・窓を付けた `GET /bff/documents` の応答（全件数フィールドと窓）が変わらない（試験）。
2. 許可ポリシーが無いとき、`total` / `unfilteredTotal` が 0、`statusCounts` が空（試験）。
3. 変異（`DocumentListWindow.Collector.AddRange` で全体・状態別の計数を `visible` の判定の前へ動かす）で 1 の試験が赤になる。
4. `dotnet build` / `dotnet test`（BFF の試験）・`dotnet format --verify-no-changes` が通る。文書系検査（`check-trace-blocks`・`gen-knowledge-graph --check`・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`）が通る。

## 結果

- `dotnet test`（`Platform.Bff.Tests`、`--filter FullyQualifiedName~BffDocumentList`）: 51 件すべて合格（足した試験は 7 件）。
- 変異 1（`AddRange` で全体・状態別の計数を `visible` の前へ動かす）: 19 件が赤（足した個人資料の試験 5 件を含む。既存の ABAC の陰性試験も赤）。
- 変異 2（`IsManageable` から `&& !IsPrivateNote(doc)` を外す）: 足した個人資料の試験 5 件と既存の「個人資料は総件数に入らない」が赤。
- 変異 3（`DocumentListWindow.Empty()` が `UnfilteredTotal = 1`・`StatusCounts` に 1 件を返す）: 足した「スコープが無ければ空のページ」2 件が赤（変異前の同試験は `total` しか見ておらず緑のままだった）。
- 実装コードの差は無い（計画の「追随の改修は無い」と一致）。IADR は起こしていない。
