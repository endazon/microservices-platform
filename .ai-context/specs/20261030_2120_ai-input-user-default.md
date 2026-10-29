---
title: 作業仕様書 — AI 入力の利用者単位の既定を同期設定に置き、新しく作る個人資料の初期値にする（ADR-0143）（#2120）
type: spec
status: done
related_ids: [FR-19, FR-20, FR-21, SC-19, SC-20, UC-11, ADR-0143, ADR-0061, ADR-0105, ADR-0037]
author: claude
created: 2026-10-30
updated: 2026-10-30
issue: "#2120"
---

# 作業仕様書 — AI 入力の利用者単位の既定（#2120）

> 本仕様書は実装着手前に作成した（着手 2026-10-30。develop `0fe6947d`）。親 #2047（指摘 M-23）。

## 計画（確定済み）

計画 ADR-0143（Accepted）が振る舞いを決めている。issue の「計画の判断が要る」は既に満たされている。

- 決定 1: 利用者単位の既定は、**新しく作る資料の AI 入力の初期値**である。対象は SC-19 の新規作成と Obsidian 同期の新規 push。作成後は資料単位の値だけが効く。既定は初期状態で OFF。未設定と OFF は区別しない。作成時点の既定を使う。
- 決定 2: 既定を変えても既存の資料は変わらない。
- 決定 3: 同期競合の「両方を残す」で作る別名コピーには既定を当てない（サーバ側の解決と、プラグインの `sourceNoteId` 付きの新規 push の両方）。
- 決定 4: 横断検索・ナレッジグラフには利用者単位の既定を置かない（AI 入力だけ）。既定を ON にする操作の近くで、同期フォルダの一括取り込みでも ON になることを伝える（SC-20 主要素 8）。
- 決定 5（3 点セット）の「配備後に効く」の実装が本 issue である。フォローアップ 1: 別名コピーの経路へ既定を当てない試験を置く。

## 現状（develop で確認）

- `SyncSettings` は同期対象フォルダだけを持つ。利用者単位の AI 入力の既定は無い。
- 新規作成（`PrivateNotes/Create`）と新規 push（`ObsidianSync/Push`）は、どちらも `PrivateNoteEndpoints.PrivateNoteDefaults(owner)`（3 トグルとも OFF）で作る。同期競合の解決（`SyncConflicts/Resolve`）の別名コピーも同じ。

## 方針

1. **台帳**: `SyncSettings` に `AiInputDefault`（bool、既定 false）を足す。マイグレーション `AddSyncSettingsAiInputDefault`（列 boolean・not null・既定 false。既存の行は false）。
2. **契約**:
   - `SyncSettingsDto` に `AiInputDefault`（bool）を足す。設定が無い利用者は false。
   - `UpdateSyncSettingsRequest` に `AiInputDefault`（bool?。省略・null は変えない）を足す。既存の呼び出し（フォルダだけを送る）を壊さないためである。フォルダの全量置き換えの意味は変えない。
   - BFF は既存の中継のまま（契約の型を透過する）。`docs/api/openapi.yaml` の 2 つのスキーマに項目を足し、orval で生成し直す。
3. **初期値**: `PrivateNoteDefaults(owner, includeInAi)` にし、新規作成と新規 push（`sourceNoteId` が無いときだけ）は、作成時点の既定を渡す。別名コピー（push の `sourceNoteId` 付き・競合の解決）は false のまま。横断検索・グラフは常に OFF。
   - 新規 push は既存の「露出のどれかが ON なら発行する」門（`SaveChangesAndPublishUpdatedIfIndexableAsync`）を通るので、既定 ON の利用者の新規 push は索引へ載る（ADR-0143 決定 4・ADR-0061 決定 1）。
   - SC-19 の新規作成は本文を持たない（0 バイト）。従前どおり発行しない。本文が入ったとき（push・本文の差し替え）に門を通る。
4. **画面（SC-20）**: 露出設定の節に「新しく作る資料を AI の入力に含める（既定）」のトグルを置く。ON にすると同期設定へ `aiInputDefault` を送る（フォルダは現在の値のまま）。近くに固定文言を置く:「この設定は、これから作る資料（SC-19 の作成・同期の新規）の初期値です。作成済みの資料は変わりません。同期対象フォルダへまとめて入れたメモも、作成の時点ですべて AI の入力に含まれます。」（決定 2・4）。
5. 新しい IADR は作らない（振る舞いは ADR-0143 が決めている。PUT の null の扱いと SC-19 の作成で発行しないことは、既存の作法の当てはめ）。

## 母集合（規則 9・10）

- **規則 9**: `git grep -n "PrivateNoteDefaults("` で、個人資料を作る経路を走査した。3 か所だけである。
  - `PrivateNotes/Create/Endpoint.cs` —— 既定を当てる。
  - `ObsidianSync/Push/Endpoint.cs` —— `sourceNoteId` が無いときだけ既定を当てる。
  - `SyncConflicts/Resolve/Endpoint.cs` —— 別名コピー。当てない（決定 3）。
  - ほかの作成の経路（`Document.Create*` で `doc_scope=private-note` を書く口）は無い。
- **規則 10**: Push の注記「新規作成は必ず 3 つとも OFF である」と、Create の注記「3 トグル OFF で作成」は、既定 ON の利用者で誤りになる。直す。SC-20 の画面仕様書の「利用者単位の既定は無い」の記述も直す。

## 受け入れ基準 → 試験

| 基準 | 試験 | 修正前 |
| --- | --- | --- |
| 既定を設定・読める（未設定は false） | DocumentService の同期設定の試験 | 赤（項目が無い） |
| 既定 ON の利用者の新規作成・新規 push は AI 入力だけ ON（検索・グラフは OFF）。既定 OFF は 3 つとも OFF | 同 新規作成・push の試験 | 赤 |
| 別名コピー（push の `sourceNoteId` 付き・競合の解決の「両方を残す」）は既定 ON でも 3 つとも OFF | 同 | 新規（決定 3 の固定） |
| 既定を変えても既存の資料は変わらない | 同 | 新規 |
| フォルダだけを送る更新は既定を変えない | 同 | 新規 |
| マイグレーションがモデルと一致する | `HasPendingModelChanges` の試験 | 新規 |
| SC-20 のトグルと固定文言 | SC-20 の画面の試験 | 赤 |

## 実装中の追記（2026-10-30）

- **規則 9 の母集合の追加**: 露出は**資料の属性**（`PrivateNoteDefaults`）と**台帳の行**（`PrivateNote.IncludeInAi`。一覧の `includeInAi` はこちらから読む）の 2 か所に持たれている。最初の走査は属性の側だけを引いていた。試験は赤のままで（新規作成の一覧が OFF のまま）、それで気付いた。`git grep -n "PrivateNote.Create("` で走査すると、同じ 3 経路が出る。`PrivateNote.Create` に `includeInAi`（既定 false）を足し、作成と新規 push では属性と同じ値を渡すようにした。競合の解決は false のままにした。
- **規則 10 で直した記述**:
  - `PrivateNoteEndpoints` の冒頭の注記「作成は必ず 3 つとも OFF」と、競合の解決の「既定は作成経路と同じ」。
  - `docs/api/openapi.yaml` の作成・露出の説明 3 か所。
  - 機能仕様書 FR-19 の作成の既定値。
  - SC-19 の画面仕様書（主要素 13・未決事項 5）。
  - SC-20 の画面仕様書（主要素 14・24 を追加、項目・操作・計画との対応・未決事項 3）。
  - SC-20 のテスト仕様書の行 6。
  - SC-20 の露出の案内文（「既定はいずれもオフ」を、横断検索・グラフの既定はオフで、AI 入力は作成時点の既定で始まる旨へ改めた）。
  - 走査語: `3 つとも OFF`・`3 トグル OFF`・`既定はいずれも`・`利用者単位の既定`。
  - 直さなかったもの: ゴミ箱・復元・再発行の注記にある「3 つとも OFF なら発行しない」は、門の条件を述べているだけで、作成時の既定の話ではない。
- **固定文言**: 計画（SC-20 主要素 8）の文言「これから作る資料と、同期対象フォルダに新しく入れたメモが AI の入力に含まれます。既存の資料は変わりません」をそのまま使った（方針 4 の草案から差し替えた）。
- **床**: 初期ロード合計の床を、実測で 649,417 B から 650,379 B へ上げた（`--update`）。増えたのは新しい文言の ja / en カタログである。テスト仕様書の床も `--update` で上げた。
- **環境の注記**: `Knowledge.IntegrationTests` の `WikiSlugNonUniquePostgresTests` が手元で 1 件落ちる。原因は接続文字列が無いことである（他の 137 件は同じ理由で skip になる）。本変更とは関係しない。CI では接続文字列が与えられる。
