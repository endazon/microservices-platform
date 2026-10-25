---
title: 作業仕様書 — AST が境界語（<<<UNTRUSTED_DATA）つきで保存した文書の表題・本文から、一回限りの管理操作で枠を剥がす（#1973）
type: spec
status: done
related_ids: [FR-06, FR-02, FR-13, UC-03, SC-05, IADR-0503, IADR-0509, IADR-0549, IADR-0554, IADR-0556]
author: claude
created: 2026-10-17
updated: 2026-10-17
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-06）
  - planning:projects/microservices-platform/05_screens/01_screens.md（SC-05 文書管理）
---

# 作業仕様書: AST の文書から境界語の枠を剥がす（#1973）

> 本仕様書は実装着手前に作成した（着手 2026-10-17）。基点は MSP `origin/develop` `197ff530`。

## 起点

- #1973。AST の情報収集は 2026-10-16 まで、KB へ保存するニュースの表題と本文を `<<<UNTRUSTED_DATA\n…\nUNTRUSTED_DATA>>>` で囲んで保存していた（AST#1359）。
  AST 側は AST#1360（AST の IADR-0542。共有部品 `UntrustedDataBoundary`）で新しい保存を囲まなくした。旧い文書は囲まれたまま残る。
- 害: 文書管理画面・Wiki・MCP の一覧に境界語が出る／表題が約 37 字長く Wiki.js の varchar(255) を超える（#1966。#1972 で切り詰めて同期するようにした）／
  語彙索引・埋め込みの入力に毎回同じ境界語が混ざる。
- AST からは直せない: AST の文書カタログの port は本文の置換と削除だけを持ち、表題を更新できない。基盤に upsert が無く、取り込み直すと重複が増える。
- 指示: #1951（#1931 項目 3・IADR-0549）の口と駆動スクリプトの作法を再利用し、新しい仕組みを作らない。既定は dry-run（件数と ID の一覧）。実行は明示のフラグ。冪等。

## 着手前の調査

### 1. AST が保存した形（AST の develop `6a164a89`＝AST#1360 のマージを読んだ）

- 旧: `PromptSafetySanitizer.Sanitize(text)` ＝ 境界語の除去（1 回の Replace）→ 制御文字の除去 → `$"{Open}\n{text.Trim()}\n{Close}"`。表題・本文の両方に掛けていた。
- 書き手 `HttpKnowledgeBaseWriter` は `POST /documents` へ `Title` と `Body`（本文そのまま）を送る。属性 `project = ai-stock-trading` は必ず付く（AST の IADR-0293）。
- 基盤側: `POST /documents` の本文は `CreateWithBody` で `documents/{id}/body.md` に**そのまま**格納される。つまり台帳の表題とオブジェクトの本文がそのまま枠つきである。
- 枠の中身に境界語は（入れ子の偶然を除き）残らない。AST の判断側は読むときに不変点まで剥がす（`UntrustedDataBoundary.StripMarkers`）。

### 2. 既存の手段で足りるか

- 表題の更新: `PUT /documents/{id}`（管理者だけ）は表題を変えられるが、**属性・タグを全置換**する。本文の差し替え: `PUT /documents/{id}/body` は**状態を `normalized` へ戻す**
  （`SetMarkdownUri`）。2 回呼ぶと版が 2 つ進み、イベントも 2 回出る（取り込みが 2 回埋め込む）。どちらも移行の道具としては副作用が大きい。
- → #1951 と同じ形の**管理者だけの一回限りの口**を足し、表題と本文を 1 つの版で書き換える（状態・属性・タグは動かさない）。下流への伝え方・ページ送り・
  dry-run・監査ログ・駆動スクリプトの作法は #1951 を写す（写すのではなく、駆動スクリプトの本体は共有する。下記）。

### 3. 下流（コード読み）

- 取り込み: `DocumentUpdated` を受けると全コレクションから当該文書の点を消してから本文を読み直す。→ 剥がした本文で索引が作り直される（埋め込みの費用がかかる）。
- Wiki 同期: ページの路は文書 ID 由来（`WikiPage.PathFor`）。表題が変わってもページは同じ（slug は今の表題の候補から選び直すが、路は変わらない）。
  #1966 で DLQ にある文書も、剥がした後の同期は新しいイベントで届く（DLQ のメッセージそのものは残る。運用で捨てる）。
- グラフ: 本文指紋が変われば本文を読み直す。表題はノードの表題になる。

## 方式

1. **口 `POST /documents/strip-untrusted-boundary`**（DocumentService・`AdminOnly`・`write` 群）。
   - 要求: `dryRun`（必須）・`limit`（既定 100・1〜200）・`cursor`・`requestedBy`・`reason`（書き換える呼び出しでは必須）。
   - 母集合: アーカイブ済みでない ∩ `project = ai-stock-trading` ∩ 組織文書。ページ送りは一覧の口と同じ `DocumentPageQuery.ReadPageAsync`
     （属性の一致は jsonb の値変換で SQL へ訳せないので、SQL の並びで塊ごとに読み、塊の中で当てる。IADR-0509）。project は口の側で固定する（引数にしない）。
   - 1 ページの文書ごとに: 本文の参照が `storage://` なら本文を読む（NoSuchKey は「本文なし」として表題だけを見る。他の失敗はそのページを何もせず 502。未構成は 503）。
     表題・本文が枠の形なら剥がす計画を立てる。剥がすと空になる表題は剥がさずに数える（表題は必須）。剥がすと空になる本文も剥がさずに数える（空の本文を下流へ流さない。#1978 のレビュー）。
   - 書き換え: 剥がした本文を**新しいキー** `documents/{id}/body-boundary-stripped.md` へ書く（1 件でも書けなければ台帳を保存せず 502）→
     `Document.ReplaceUntrustedBoundaryWrapped`（表題と本文の参照・指紋だけ。版を 1 つ進め、注記は固定）→ 保存 → 単票と同じ発行の門 `PublishUpdatedIfIndexableAsync`。
   - 応答: ページの `selected`・`wrapped`・`titleWrapped`・`bodyWrapped`・`bodyMissing`・`notStored`・`titleLeftWrapped`・`bodyLeftWrapped`・`stripped`・`items`（文書 ID と、表題・本文のどちらか）・`nextCursor`。
   - 監査: 書き換えた文書ごとに 1 行（ID・主体・`requestedBy`・`reason`。表題・本文に由来する値は中身も枠の有無も出さない。#1978 の CodeQL の指摘）とページごとに 1 行（件数だけ）。
2. **枠の剥がし方（純関数 `UntrustedBoundaryFrame`）**: 先頭が開始の境界語・末尾が終了の境界語（重ならない）のときだけ、両端の境界語と隣の改行 1 つ（`\n` か `\r\n`）を剥がす。
   **枠が無くなるまで繰り返す**（入れ子）。中身の途中の境界語・前後の空白には触らない。出力は枠の形をしない → 2 回目は何もしない（冪等）。
3. **駆動スクリプト `scripts/strip-untrusted-boundary.js`**: #1951 の駆動器（`archive-bodyless-documents.js`）の main を `scripts/lib/admin-page-driver.js` へ切り出し、
   口ごとに違う所（経路・引数・状態ファイルの絞り込み・集計・文言）だけを profile で受ける。一括アーカイブの駆動器はその profile の 1 つになる（挙動は変えない。
   #1931 の既存の試験がそのまま通ることで確かめる）。dry-run はページごとに対象の文書 ID を並べ、最後に件数を出す。

## 対象外（触らない）

- 他の project の文書・個人資料・アーカイブ済み・枠の無い文書。中身の途中の境界語。
- 元の本文のオブジェクト（上書きしない。前の版が参照し続ける。文書の削除で全版の参照ごと消える＝ `DocumentObjectPurger`）。
- RAG の文脈の境界（#1973 の「参考」。`RagOrchestrator` / `AnalysisPromptBuilder` が取り出した文書を素で入れること）は本件の対象外。別の判断として残す。
- AST 側の変更。計画への環流（不要。計画の語彙・画面は変えない）。

## 規則 9・10 の母集合（誤りの側の文字列で走査した）

- `git grep -l "archive-bodyless"`（`.ai-context/specs` を除く）＝ 17 件。口・駆動器を並べる場所はこのうち
  `docs/api/openapi.yaml`・`docs/functional/FR-06_document-crud-versioning.md`（口の表と節）・`docs/tests/FR-06_document-crud-versioning.md`・
  `docs/operations/operations.md`・`scripts/README.md`（2 つの表）・`scripts/live-scripts.json`・`scripts/scripts.repo.test.js`（検査器でない入口の一覧）・
  `DocumentEndpoints.cs`（合成点）。残り（IADR-0540/0541/0549/0551・README の索引・取り込みの注記・口と試験の本体）は #1931 の記録・実装であり追随不要。
- `git grep -n "UNTRUSTED_DATA"`（MSP）＝ 0 件（着手前）。AST の境界語を前提にした既存の処理は MSP に無い。
- 規則 10（この変更で新たに誤りになる自分の記述）: 駆動器の本体を切り出したので、`archive-bodyless-documents.js` の冒頭の「本スクリプトは…持つ」に追記した。
  scripts/README の一括アーカイブの行（「再発行の駆動器と同じ作法・同じ I/O」）は切り出し後も正しい。

## 受け入れ基準 → 試験

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| 1 | 管理者だけが呼べる | `StripUntrustedBoundaryEndpointTests.管理者だけが…`・`口は_AdminOnly_を積む`（T-96） |
| 2 | 必須の指定が無い呼び出しを通さない（何も読まない） | `必須の指定が無い_…`（T-97） |
| 3 | dry-run は件数と ID だけ。本番は枠のある AST の文書だけを剥がし、対象外（他の project・project なし・個人資料・アーカイブ済み・枠なし）に触らない。状態・属性は動かず、前の版は元の本文を指す | `dry_run_は件数と対象のIDだけを…`（T-98） |
| 4 | 冪等（2 回目は版もイベントも増えない）・取りこぼしと重複が無い | `二回流しても…`（T-99） |
| 5 | 枠だけを、無くなるまで剥がす（中身・枠でない値に触らない） | `枠は先頭と末尾の…`（T-100） |
| 6 | 読めない・書けないときはそのページを何もしない。未構成は 503 | `本文が_NoSuchKey_以外で…`・`剥がした本文を書けないと…`（T-101） |
| 7 | 駆動器: dry-run は ID と件数を出して何も変えない・カナリア・冪等・失敗で止まって再開・状態ファイルの取り違えを拒む | `scripts.repo.test.js` の #1973 節（T-102） |
| 8 | 一括アーカイブの駆動器の挙動は変わらない | `scripts.repo.test.js` の #1931 項目 3 の節（既存。無修正で通る） |

## 変異試験（手元で赤になることを確かめた）

`dotnet test --filter FullyQualifiedName~StripUntrustedBoundary`（23 件）で、1 つずつ入れて戻した。7 つとも赤（陽性対照: 変異なしで 23 件緑）。

| 変異 | 結果 |
| --- | --- |
| `AdminOnly` を外す（認証だけにする） | 赤（2 件） |
| dry-run でも書き換える | 赤（1 件） |
| アーカイブ済みを母集合から外さない | 赤（1 件） |
| project の絞り込みを外す | 赤（1 件） |
| 本文の枠の判定を反転する | 赤（4 件） |
| 本文を書けなくても `continue` してページの残りを書き換える | 赤（1 件） |
| 剥がしを 1 回だけにする（`while` → `if`） | 赤（1 件） |

## 残余

- 保存の後の発行が途中で失敗すると、そのページの残りは「剥がし済み・イベント未発行」になり、再実行では拾われない（枠の形でなくなるため）。
  回収は口のログの `Stripped untrusted boundary from document` の行で ID を引き、`republish-document-updated.js --ids` で再発行する（#1951 と同じ受容）。
- 元の本文のオブジェクトは残る（前の版の参照）。容量は AST のニュースの本文の分だけ増える。
- 取り込みが剥がした文書を埋め込み直す（費用がかかる）。件数は dry-run で先に確かめる。
