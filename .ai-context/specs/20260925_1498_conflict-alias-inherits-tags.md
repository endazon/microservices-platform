---
title: 作業仕様書 — 同期競合の「両方を残す」で作る別名資料にタグを引き継がせる（#1498・ADR-0105 決定 3）
type: spec
status: done
related_ids:
  - FR-19
  - FR-20
  - FR-21
  - UC-11
  - SC-20
  - ADR-0105
  - ADR-0061
  - ADR-0037
  - IADR-0444
  - IADR-0455
author: claude
created: 2026-09-25
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0105_conflict-alias-note-inherits-tags-only.md (Accepted 2026-09-17)
  - planning:projects/microservices-platform/05_screens/01_screens.md (§SC-20 主要素 5 の 2026-09-17 確定ブロック)
related_specs: []
issue: "#1498"
---

# 作業仕様書 — 別名資料はタグだけを引き継ぐ

## 目的と射程

計画 ADR-0105（planning#636 の裁定）は、同期競合を `both`（両方を残す）で解決したときに作る別名資料が
**タグだけを引き継ぐ**と定めた（決定 3）。露出 3 トグル（決定 1・4）・共有先（決定 2）・版履歴（決定 3）・
機密区分（SC-20 主要素 5 の表）は引き継がない。

ADR-0105 決定 5 自身が「決定 3 のタグの引き継ぎは未実装（`tags: []` で作る）」と書いており、
MSP に対応する issue が無かったため #1498 を起票した。

**射程**: サーバ側の `SyncConflicts/Resolve` の `CreateAliasNoteAsync` 1 箇所の変更と、引き継がない 4 項目の
陰性試験（陽性対照つき）、利用者向け・API の説明の追随。**プラグイン側の「両方残す」は射程外**（下記 §計画との差異）。

## 計画の読み（逐語で確かめたこと）

- 「タグだけ」は要約ではなく ADR の表題そのもの（`ADR-0105 同期競合の別名コピーはタグだけを引き継ぐ — 露出 3 トグル・共有先・版履歴は引き継がず、OFF は資料単位の明示の値で表す`）。
- 決定 3 の表: タグ＝引き継ぐ（現状の実装を改める）／版履歴＝引き継がない。
- 決定 4: OFF は資料単位の明示の値で書く（属性の不在で表さない）。→ 既存の `PrivateNoteDefaults` がそのとおり。
- 決定 3 の受け入れる副作用: タグの使用件数が 1 増え、写しがある間は削除できない。→ 試験で固定する。
- 計画 SC-20 主要素 5 の表は機密区分（`restricted` へ戻る）も「引き継がない」に挙げる。→ 試験で固定する。

## 実装

1. `Features/SyncConflicts/Resolve/Endpoint.cs` の `CreateAliasNoteAsync`: `tags: []` → `tags: [.. sourceDoc.Tags]`
   （別のリストにする。元の資料と実体を共有しない）。コメントに 5 項目の扱いと機序の表を置く。
2. 辞書との突き合わせは写すときに行わない（元の資料に付いた時点で値域は通過・参照ありのタグは削除拒否）。
   **起こり得ないケースへの防御を足さない**という規約に従う。
3. 発行の門（IADR-0455）は変えない。別名資料は露出 OFF なので門に弾かれる（既存試験が固定）。

## 受け入れ基準 → 試験（`DocumentService.Tests` の `SyncConflictEndpointTests`）

| 受け入れ基準 | 試験 |
| --- | --- |
| タグ 2 つの資料の `both` → 別名資料のタグが同じ 2 つ・元の資料は不変・使用件数が各 1 増える | `bothの解決で作る別名資料は元の資料のタグを引き継ぎ使用件数が1増える` |
| 露出 ON・共有 1 件・版 3 以上・機密区分 `internal` の資料の `both` → 別名資料は露出 3 つとも明示の OFF・共有 0 件・版 1（版履歴 1 行）・`restricted`・発行されない（陽性対照: 元の資料の状態を先に確かめる） | `bothの解決で作る別名資料は露出も共有先も版履歴も機密区分も引き継がない` |
| タグの無い資料の `both` → 別名資料のタグは空 | `bothの解決はタグの無い資料では別名資料のタグも空になる` |

**赤の確認**: 実装を `tags: []` に戻すと 1 本目が「Expected ... to be a collection with 2 item(s) ... but found an empty collection」で落ちることを実測した（下記 §検証）。

## 母集合（規則 1〜6・9・10）

| 軸 | 引き方 | 結果 | 扱い |
| --- | --- | --- | --- |
| 1. 誤りの側（空のタグで作る箇所） | `git grep -n "tags: \[\]" -- . ':!src/ai-stock-trading' ':!*.po'` | サーバ側 3 件: `SyncConflicts/Resolve/Endpoint.cs`（本件）・`PrivateNotes/Create/Endpoint.cs`・`ObsidianSync/Push/Endpoint.cs`。他はフロントの試験の固定データ・`scripts.repo.test.js` の試験データ | 本件のみ直す。`Create` は新規作成（別名ではない）。**`Push` は新規 push であり、プラグインの「両方残す」はこの経路で写しを作る**（§計画との差異） |
| 2. 「別名」の記述 | `git grep -n -i "別名" -- docs src/knowledge/frontend src/knowledge/backend ':!*.po' ':!*messages.ts'` | `docs/api/openapi.yaml` 3 行・`docs/functional/FR-19_private-notes.md:104`・`docs/functional/FR-20_obsidian-sync.md:106`・`docs/how-to/obsidian-plugin-install.md:79`・`docs/how-to/plan-id-range-history-annex.md:35`・`docs/tests/FR-20_obsidian-sync.md:98`・SC-20 の画面（`SyncConflictsPanel.tsx`）・本件の端点と試験。他は無関係の語（DB 列の別名・別名前空間） | 引き継ぎを述べる／述べるべき箇所だけ直す: **FR-19 機能仕様書**（露出だけを述べていた）・**OpenAPI の resolve の説明**。FR-20 系・how-to はプラグインの操作説明で引き継ぎを述べていない（変えない）。annex はレンジ表で既に正しい |
| 3. 引き継ぎの語 | `git grep -n -E "継がない\|引き継が\|引き継ぐ\|継ぐ" -- src docs ':!*.po' ':!*messages.ts'` | 本件以外で別名資料に触れる行は FR-19:104 のみ（残りは資格情報の伝播・運用文書の「引き継ぐ人」等） | FR-19 を直した |
| 4. IADR | `git grep -n -i "both\|別名\|tags: \[\]" -- .ai-context/adr/IADR-0444* .ai-context/adr/IADR-0455* .ai-context/adr/IADR-0352*` | IADR-0444 決定 4（`both` の別名と新規作成の経路）・IADR-0455（露出だけ述べる。正しい）・IADR-0352（プラグイン CLI） | **IADR-0444 に日付つき追記**。新しい IADR は起こさない（新しい判断が無い。計画の決定の実装であり、決定 4 の経路は変えない） |
| 5. 生成物 | `openapi.yaml` の説明は orval 生成物に jsdoc として写る | `src/platform/frontend/src/lib/api/generated/private-notes/private-notes.ts` に 2 行 | `pnpm run codegen` で再生成（差分は説明 2 行だけ） |

**除外**: 画面（SC-20）の確認文言に引き継ぎを足すこと —— 計画 SC-20 は引き継ぐものを「定める」だけで表示の要求を置いていない（ADR-0105 決定 1 の副作用は「利用者は SC-19 で ON にする」）。計画外の画面追加をしない。

## 計画との差異・未決

1. 🔴 **プラグイン側の「両方残す」は別の経路で写しを作り、タグを持たない。** `src/obsidian-plugin/src/protocol/conflictResolver.ts` の `both` は
   ローカル本文を `noteId: null` で新規 push する（`ObsidianSync/Push/Endpoint.cs:99` が `tags: []` で作る）。サーバはこの push を別名と識別できず、
   push の契約にタグの口も元の資料の口も無い。**ADR-0105 の実測はサーバ側の `CreateAliasNoteAsync` だけ**を挙げており、この経路を射程に含むかは
   計画の判断が要る（含むならプロトコルの変更＝元の資料の ID を push に載せる等が要る）。**本 PR では変えず、PR 本文と IADR-0444 の追記に記録する。**
   ［2026-09-26 追記 / #1521］planning#652 の裁定 1 が「含む」と決め、#1521 で push に `sourceNoteId` を足して対応した（IADR-0464・作業仕様書 `20260926_1521_plugin-keep-both-source-note-tags.md`）。
2. 画面の文言は変えない（上記 §除外）。

## 検証

- `dotnet test src/knowledge/backend/Services/DocumentService/Tests/DocumentService.Tests.csproj --filter FullyQualifiedName~SyncConflictEndpointTests` → 26 件合格。
- 赤の確認: `tags: []` に戻して同じ試験 → `bothの解決で作る別名資料は元の資料のタグを引き継ぎ使用件数が1増える` だけが失敗（25 合格・1 失敗）。戻して緑。
- その他（build / test 全体・format・doc 検査）は PR 本文に記録する。
