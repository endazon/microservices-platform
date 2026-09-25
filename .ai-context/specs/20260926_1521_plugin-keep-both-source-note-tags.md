---
title: 作業仕様書 — Obsidian プラグインの「両方を残す」で作る写しに、元のノートのタグを引き継がせる（#1521・ADR-0105 決定 3 / ADR-0110 の裁定 1）
type: spec
status: done
related_ids:
  - FR-19
  - FR-20
  - UC-11
  - SC-20
  - ADR-0105
  - ADR-0110
  - ADR-0037
  - IADR-0444
  - IADR-0352
  - IADR-0464
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0105_conflict-alias-note-inherits-tags-only.md (Accepted。フォローアップ 2 の 2026-09-26 訂正)
  - planning:projects/microservices-platform/07_adr/ADR-0110_sc22-supplier-three-values-no-public-key-restart-confirmed-at-write.md (Accepted 2026-09-26)
  - planning:projects/microservices-platform/05_screens/01_screens.md (§SC-20 主要素 5 の 2026-09-26 追記)
related_specs:
  - 20260925_1498_conflict-alias-inherits-tags.md
issue: "#1521"
---

# 作業仕様書 — プラグインの「両方を残す」もタグを引き継ぐ

## 目的と射程

#1503（#1498）はサーバ側の解決（`SyncConflicts/Resolve` の `CreateAliasNoteAsync`）で別名資料にタグを写した。
**Obsidian プラグインの「両方を残す」は別の経路**であり、ローカル本文を `noteId: null` の新規 push として送るため、
サーバは `tags: []` で作っていた（#1498 の作業仕様書 §計画との差異 1 が記録）。

planning#652 の裁定 1（利用者裁定 2026-09-26。ADR-0110 の本文と ADR-0105 フォローアップ 2 の訂正に記録）:

> push の契約に「元のノートの ID」を任意項目として足し、サーバが同じ所有者のノートからタグを写す。経路によって引き継ぐものを変えない。

**射程**: push の契約への任意項目の追加（サーバ・プラグインの型）、サーバの新規作成分岐でのタグの写し、
プラグインの `both` が項目を載せること、陰性試験、文書の追随。**BFF・画面（SC-20）・OpenAPI は変えない**（同期プロトコルは BFF を通らず、
`docs/api/openapi.yaml` に載っていない。契約の正は `docs/api/FR-20_obsidian-sync.md` とサーバ）。

## 計画の読み（逐語で確かめたこと）

- ADR-0105 決定 3: 別名コピーは**タグを引き継ぐ**。版履歴は引き継がない。決定 1・2・4: 露出 3 トグル（明示の OFF）・共有先は引き継がない。
- ADR-0105 フォローアップ 2 の［2026-09-26 訂正］: 「決定 3 は経路を限っておらず、プラグインの経路にも及ぶ。push の契約に『元のノートの ID』を任意項目として足し、**サーバが同じ所有者のノートからタグを写す**必要がある。決定は 1 つも改めていない。」
- 計画 SC-20 主要素 5 の［2026-09-26 追記］: 「以下の定めは、サーバ側で解決した場合と Obsidian プラグインの側で『両方を残す』を選んだ場合の両方に当てる。…経路によって引き継ぐものを変えない。」
- 🔴 **計画は「同じ所有者でない ID」の扱い（無視か拒否か）を定めていない。** → 実装の判断として IADR-0464 に記録する（下記 §設計）。

## 設計（判断は IADR-0464）

1. **項目名は `sourceNoteId`**（uuid・任意・null 可）。**新規作成（`noteId` が null）のときだけ**読む。更新の push では読まない（既存の資料のタグを push で書き換える経路を作らない）。
2. **写す条件は「同期トークンの持ち主が所有する個人資料」**。判定は更新の push と同じ `ObsidianSyncEndpoints.FindOwnedAsync`（個人資料の台帳を ID で引き、所有者が一致するときだけ返す）。組織文書は個人資料の台帳に行が無いので当たらない。
3. 🔴 **同じ所有者でない・存在しない ID は、黙って何も写さない（拒否しない）**。応答は `sourceNoteId` 無しのときと同じ 201・同じ本文。
   - 拒否（404 / 400）にすると、①拒否の理由を他者と不在で分ければ他者の資料の実在が漏れる（同期プロトコルは所有者スコープ外を 404 にして存在を秘匿する）。分けなければ漏れないが、②③が残る。
   - ②写しの push が失敗すると、プラグインは既にローカルへ写しのファイルを書いた後であり（`conflictResolver.ts`）、写しが送られずに残る。**本文を失わせないための操作が、タグのために止まる**のは向きが逆である（ADR-0105 決定 5 が「タグが無いことで漏れる向きの問題は生じない。機能の不足であり統制の不足ではない」と書いている）。
   - ③正当な利用者でも起こり得る（写しを送る直前に元の資料が完全削除された等）。
4. **写すのは `Document.Tags`（辞書の識別子の集合）そのもの**（別のリストにする）。辞書との突き合わせは写すときに行わない（#1503 と同じ判断）。
5. **写すのはタグだけ**。露出は `PrivateNoteDefaults`（3 つとも明示の OFF）・共有台帳へ行を足さない・版は edits の数から・機密区分は `restricted`。いずれも新規作成の既定のままであり、コードは変えない（試験で固定する）。
6. **プラグインが `sourceNoteId` を載せるのは `resolveVersionConflict` の `both` だけ**。通常の新規 push（`pushSync.ts`）とサーバ側削除からの作り直し（`resolveServerDeleted` の `local`）は載せない —— 後者は元の資料と同じパスで作り直すのであって「両方を残す」ではない（ADR-0105 フォローアップ 4 の射程外と同じ向き。元の資料は削除済み）。

## 受け入れ基準 → 試験

| 受け入れ基準 | 試験 |
| --- | --- |
| 自分の資料（タグ 2・露出 ON・共有 1・機密区分 internal）を `sourceNoteId` に新規 push → 写しはタグ 2・使用件数 +1・露出 3 つとも明示の OFF・共有 0・機密区分 restricted・版は edits の数・発行されない（陽性対照: 元の資料の状態を先に確かめる） | `PushSourceNoteTagsTests` › `sourceNoteIdが自分の資料を指すとタグだけを写し露出も共有先も版も機密区分も引き継がない` |
| 他者の資料の ID → 201・タグ空・他者の資料は不変（同じ要求形で自分の資料なら写る＝陽性対照） | `…他者の資料を指すと何も写さず応答も変わらない` |
| 存在しない ID・組織文書の ID → 201・タグ空 | `…存在しない資料や組織文書を指すと何も写さない`（Theory 2 件） |
| `sourceNoteId` 無し → 従来どおりタグ空 | `…sourceNoteIdが無ければ従来どおりタグは空` |
| 更新の push の `sourceNoteId` は読まない | `…更新のpushではsourceNoteIdを読まない` |
| プラグインの `both` が `sourceNoteId` = 元のノート ID を送る | `conflictResolver.test.ts` › `both は…`（既存の試験に要求本文の検査を足す） |
| 通常の新規 push・サーバ側削除からの作り直しは `sourceNoteId` を送らない | `pushSync.test.ts` › 未追跡の新規 push の試験・`conflictResolver.test.ts` › `resolveServerDeleted` の `local` の試験に検査を足す |

## 母集合（規則 1〜6・9・10）

| 軸 | 引き方 | 結果 | 扱い |
| --- | --- | --- | --- |
| 1. 誤りの側（空のタグで作る箇所） | `git grep -n "tags: \[\]" -- src/knowledge/backend` | `ObsidianSync/Push/Endpoint.cs`（本件）・`PrivateNotes/Create/Endpoint.cs`（画面からの新規作成。元の資料が無い）・試験の固定データ | Push だけ直す |
| 2. 「両方を残す」の記述（表記ゆれ「両方残す」を含む） | `git grep -n -E "両方残す\|両方を残す" -- . ':!src/ai-stock-trading' ':!CHANGELOG.md' ':!*.po' ':!*/messages.ts'` | 53 行（着手時。本仕様書は未追跡のため含まない）。うち本件で**事実が変わる**もの: `docs/api/FR-20_obsidian-sync.md:70`（「両方残す＝別パスで新規 push」）・`docs/functional/FR-20_obsidian-sync.md:106`・`docs/functional/FR-19_private-notes.md:104`（別名の資料の引き継ぎ）・`docs/tests/FR-20_obsidian-sync.md:98`（P21）・`docs/how-to/obsidian-plugin-install.md:82`・`src/obsidian-plugin/src/protocol/conflictResolver.ts:7`・IADR-0444:156（「この経路の写しはタグを持たない」）・IADR-0352:89 | 列挙した 8 箇所を直す（IADR は日付つき追記）。**除外**: SC-20 の画面・BFF・OpenAPI・生成物（サーバ側解決の記述で、#1503 で既に正しい）、`main.ts:311` / `conflictModal.ts:55`（利用者向けの操作名でタグに触れない）、`.ai-context/specs/` の確定済み仕様書（凍結。ただし #1498 の仕様書は §計画との差異 1 がこの経路を未対応として残すので経過追記を 1 行置く）、IADR-0270（段 1 の記録）、無関係の語（「両方残す」の一般用法: #574・#787・#1246 の仕様書、GraphService の試験） |
| 3. push の契約（要求の形）を書く箇所 | `git grep -n -E "PushNoteRequest\|noteId: null\|baseVersion: null" -- src docs` | サーバ `Push/Command.cs`・プラグイン `types.ts`・呼び出し 3 箇所（`pushSync.ts` の新規・`conflictResolver.ts` の `both` と `resolveServerDeleted`）・偽サーバ `testFakes.ts`・`PushNoteValidatorTests` | 型 2 つに項目を足し、`both` だけが載せる。偽サーバは要求を読むだけ（タグを模さない）なので変えない |
| 4. 所有者の判定 | `git grep -n "FindOwnedAsync" -- src/knowledge/backend` | `ObsidianSyncEndpoints` の定義と、push の更新・pull・delete・move | 同じ判定を使う（別の述語を作らない） |
| 5. IADR | 軸 2 の IADR 行＋ `git grep -n -l "ADR-0105" -- .ai-context/adr` | IADR-0444（#1503 の追記）・IADR-0352 | 両方に日付つき追記。判断（無視か拒否か）は新しい IADR-0464 に置く |
| 6. 本件で新たに誤りになる自分の記述（規則 10） | 追記後に `git grep -n -E "タグを持たない\|tags: \[\] で作る\|射程外" -- .ai-context/adr/IADR-0444* .ai-context/specs/20260925_1498*` | IADR-0444:156〜160・#1498 仕様書:38・80〜82 | いずれも凍結記録の過去の断面であり本文は書き換えない（追記で「#1521 で対応」と示す） |

## 採番

着手時は IADR-0462 を採ったが、push の直前に開いている PR（#1524 が IADR-0462・#1526 が IADR-0463・#1513 が IADR-0461）と衝突したため
**IADR-0464 へ改番した**（先着尊重。ファイル名・索引・本仕様書・コード内コメント・docs の trace ブロック・IADR の追記を追随。PR タイトルには IADR 番号を書いていない）。
3 本がマージされるまで `check-adr-numbering` は 0461〜0463 を欠番として報告する。

## 検証

- `dotnet build src/knowledge/backend/backend.slnx` → エラー 0（警告 2 件は既存の `Knowledge.IntegrationTests` の旧形式ビルダー）。
- `dotnet test …/DocumentService.Tests.csproj` → 574 件合格（うち `PushSourceNoteTagsTests` 6 件）。
- 赤の確認（実測）: ①所有者の判定を `db.PrivateNotes.FindAsync` へ替える → `…他者の資料を指すと何も写さず応答も変わらない` だけが「found at least one item」で失敗。②写しを `List<Guid> tags = []` に戻す → 陽性の試験と、他者の試験の陽性対照の 2 件が失敗。いずれも戻して緑。
- プラグイン: `vitest run obsidian-plugin` → 70 件合格。赤の確認: `both` の `sourceNoteId` を外すと `both は…` が `- "sourceNoteId": "a"` の差分で失敗。
- `pnpm run typecheck`（obsidian-plugin を含む）・`lint`（エラー 0）・`format:check`・`dotnet format knowledge/backend/backend.slnx --verify-no-changes` → いずれも通過。
- 文書検査: `check-trace-blocks`・`check-doc-links`・`gen-knowledge-graph --check`・`check-cross-repo-refs`・`check-plan-id-qualification`・`check-test-name-references`・`check-test-spec-coverage`（`--update` で床に `PushSourceNoteTagsTests` を足した）・`check-doc-updated`・`check-trace-followthrough`・`check-contract-schema`（`PushNoteRequest` は Shared.Contracts の外なので差分なし）→ 通過。`check-commit-messages` → 適合。
- `node scripts/scripts.test.js` → 800 件合格（`check-adr-numbering` の欠番 0461 を一時の未追跡スタブで埋めて実行。スタブは消した）。
