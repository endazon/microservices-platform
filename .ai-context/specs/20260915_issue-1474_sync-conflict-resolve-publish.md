---
title: 同期競合の解決（local / both）を発行の門へ通す
type: spec
status: in-progress
related_ids: [FR-19, FR-20, UC-11, SC-20, ADR-0037, ADR-0061, IADR-0352, IADR-0396, IADR-0455]
author: claude
created: 2026-09-15
updated: 2026-09-15
---

# 仕様書: 同期競合の解決（`local` / `both`）を発行の門へ通す（#1474）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-19（露出 3 トグル・既定 OFF）／FR-20（Obsidian 同期・競合の 3 択）
- ユースケース / 画面: UC-11 / SC-20 主要素 5（競合の解決）
- 関連 ADR: ADR-0061 決定 1・2（1 つでも ON なら載せる／全 OFF なら載せない）／ADR-0037 決定 7（競合は利用者の 3 択）
- 関連 IADR: IADR-0455 決定 1（`DocumentUpdated` を出す本番経路はすべて門を通す）／IADR-0396 決定 4（発行の門）／IADR-0352（競合の 3 択）
- 起票: #1474（#1471 / PR #1473 の作業仕様書「対象外」「未決事項」に残された件）

## 目的・背景

`Features/SyncConflicts/Resolve/Endpoint.cs` は競合を解決するときに本文を書き換える（`local`）／新しい資料を作る（`both`）が、
**`DocumentUpdated` を 1 度も発行しない**。露出 ON の個人資料で `local` を選ぶと、本文は新しくなるのに
**索引（検索・グラフ等の下流）は古い本文のまま**残る。次に別の経路で発行されるまで食い違う。

同じ Obsidian 同期の `Features/ObsidianSync/Push/Endpoint.cs` は、本文を書いたあと `DocumentEndpoints.PublishUpdatedIfIndexableAsync` を通して発行している。

## 実測の前提（2026-09-15・`origin/develop` = `1196f400`・`git rev-parse --is-shallow-repository` → `false`）

### 走査した母集合

`.claude/rules/traceability.md` 規則 1〜6・`traceability.repo.md` 規則 9 に従い、**誤りの側（本文を書く／資料を作るのに発行しない）から、軸を変えて**引いた。
作業ディレクトリは `src/knowledge/backend/Services/DocumentService`。全コマンド共通の除外は `--exclude-dir=Tests --exclude-dir=bin --exclude-dir=obj`（テストとビルド出力は本番経路ではない）。出力は加工せずに読んだ。

| # | 軸 | コマンド（共通の除外を省略） | 結果（生の行数） |
| --- | --- | --- | --- |
| 1 | 本文の実体を書く | `grep -rn 'PutTextAsync(' .` | **7 行** |
| 1b | 同（別名のストレージ書き込み） | `grep -rn -E 'storage\.Put[A-Za-z]*Async\(\|\.PutObjectAsync\(' .` | 7 行（軸 1 と同一。別名の書き込み口は無い） |
| 2 | 資料を作る | `grep -rn -E 'Document\.Create[A-Za-z]*\(' .` | **6 行** |
| 3 | 資料を台帳へ足す | `grep -rn -E 'Documents\.Add(Range)?\(' .` | 6 行 |
| 4 | 本文の指紋・台帳の本文サイズを進める | `grep -rn -E '\.(RecordBody\|RecordContentFingerprint)\(' .` | 4 行 |
| 5 | `Document` の変更操作 | `grep -rn -E '\b(doc\|d\|document)\.(Update\|Archive\|Publish\|Restore\|SoftDelete\|SetStatus)[A-Za-z]*\(' .` | 6 行 |
| 6 | 門の利用（正しい側） | `grep -rln -E 'PublishUpdatedIfIndexable' .` | 14 ファイル（`DocumentEndpoints.cs` の定義を含む） |
| 7 | `SaveChangesAsync` を持つが門を呼ばないファイル（取りこぼしの検出） | Grep `SaveChangesAsync`（`Features/`・件数モード）の 37 ファイルから軸 6 の 13 ファイルを引く | **24 ファイル**（下表） |

`Document` の公開変更操作は `Domain/Document.cs` に 9 つ（`Update` / `UpdateMetadata` / `AddTag` / `ApplyNormalized` / `SetMarkdownUri` / `RecordContentFingerprint` / `SetExposureAttributes` / `Publish` / `Archive`）。軸 4・5 と #1471 の軸 5 で呼び出し元はすべて出ている。

### 軸 1〜5 の内訳と扱い

| 行 | 扱い | 理由 |
| --- | --- | --- |
| `Documents/Create/Endpoint.cs:76` / `:82` / `:85` / `:90` | 対象外 | 門を通っている（`:99`） |
| `Documents/PutBody/Endpoint.cs:56` | 対象外 | 門を通っている（`:66`） |
| `Documents/Catalog/DocumentNormalizedConsumer.cs:61` / `:64` | 対象外 | 撤収の形の門を通っている（`:90`） |
| `Documents/Archive` / `Publish` / `Update` / `UpdateMetadata` | 対象外 | 門を通っている（#1471 で揃えた） |
| `ObsidianSync/Push/Endpoint.cs:95` / `:97` / `:102` / `:194` / `:228` / `:231` / `:232` | 対象外 | 門を通っている（`:121` / `:209`） |
| `ObsidianSync/Push/SyncConflictRecorder.cs:42` | 対象外 | **競合のローカル本文**（`SyncConflict.StorageKey`）を書く。資料の本文ではなく、索引の対象でもない |
| `PrivateNotes/Create/Endpoint.cs:39` / `:42` | 対象外 | タイトルのみ・本文なしで、既定（露出 3 トグル OFF）で作る。門を当てても必ず弾かれる（作成時に発行しないことは IADR-0396 決定 4 の注記どおり） |
| `Documents/GrpcService.cs:42` | 対象外 | 応答の proto へ詰めているだけ（台帳への追加ではない） |
| **`SyncConflicts/Resolve/Endpoint.cs:64` / `:66` / `:67` / `:68`（`local`）** | **対象** | 本文・指紋・版・台帳の本文サイズを書き換えるのに発行しない |
| **`SyncConflicts/Resolve/Endpoint.cs:116` / `:120` / `:124`（`both`）** | **対象** | 本文を持つ新規資料を作るのに発行しない |

### 軸 7（門を呼ばない 24 ファイル）の扱い

| ファイル | 扱い | 理由 |
| --- | --- | --- |
| `SyncConflicts/Resolve/Endpoint.cs` | **対象** | 上表 |
| `ObsidianSync/Move/Endpoint.cs` | 対象外 | `PrivateNote.VaultPath` だけを動かす（`note.MoveTo`）。`Document` の題名・本文・版は変えず、`VaultPath` はイベントに載らない |
| `PrivateNotes/Create/Endpoint.cs` | 対象外 | 上表（全 OFF の新規・本文なし） |
| `PrivateNotes/SoftDelete` / `Restore` / `Purge` / `Maintenance/PrivateNoteMaintenanceService.cs` / `Documents/Delete` / `Documents/DocumentObjectPurger.cs` / `ObsidianSync/Delete` | 対象外 | 台帳（`PrivateNote`）の削除状態、または完全削除（`DocumentDeleted` の向き）。`DocumentUpdated` の射程ではない。削除の伝播は IADR-0296 の射程 |
| `Tags/Create` / `Tags/Delete` | 対象外 | 辞書の行だけを変える。`Tags/Delete` は使用中なら 409 で、資料のタグを書き換えない |
| `ObsidianSync/SyncAuditRecorder.cs` / `ObsidianSync/Push/SyncConflictRecorder.cs` / `ObsidianSync/Manifest` / `ObsidianSync/Pull` / `SyncDevices/*`（4） / `SyncSettings/Update` / `PrivateNotes/List` / `PrivateNotes/PrivateNoteUsage.cs` / `PrivateNotes/SetQuota` | 対象外 | 同期履歴・競合台帳・端末・設定・容量・通知の記録だけを書く。`Document` を変えない |

**対象は `SyncConflicts/Resolve` の 2 経路だけ**である（issue の記述と一致）。

## 対象範囲

- 対象: `Resolve` の `local` と `both` の発行を門経由で足す／挙動の試験（`SyncConflictEndpointTests` へ追加）／IADR-0455 への日付つき追記／`docs/` の該当記述。
- 対象外:
  - `server`（資料を 1 バイトも変えないので発行しない）
  - 既に古い本文のまま索引に残っている資料の backfill（露出を切り替え直せば再発行される）
  - 消費側（`IngestionService` / `GraphService`）の変更

## 設計

### 経路ごとの門の選択

| 経路 | 属性 | 当てる門 | 挙動 |
| --- | --- | --- | --- |
| `local` | **変えない**（`doc.Update(doc.Title, doc.Attributes, doc.Tags.ToList(), …)` —— 現在の属性をそのまま渡す） | `PublishUpdatedIfIndexableAsync`（元の資料） | 露出 1 つでも ON → 1 件発行（新しい本文の指紋を運ぶ）／全 OFF → 発行しない |
| `both` | 新規（`PrivateNoteEndpoints.PrivateNoteDefaults(owner)`） | `PublishUpdatedIfIndexableAsync`（別名資料） | **別名資料は露出 3 トグル OFF で作られる**ため現行は常に発行しない。元の資料は変わらないので発行しない |
| `server` | 変えない | なし | 発行しない |

- **別名資料は元の資料の露出を継がない**（読んで確認した: `CreateAliasNoteAsync` は属性に `PrivateNoteDefaults(owner)`、タグに `[]` を渡す）。
  FR-19 / FR-21 受け入れ基準 ⑩「新規に登録した個人資料は 3 トグルがすべて OFF」の新規作成であり、計画（ADR-0037 決定 7・ADR-0061）にも「両方を残す」の写しが露出を継ぐという定めは無い。
  **継がせるかどうかは本件で決めない**（既定 OFF を崩す向きの変更であり、計画の射程）。
- それでも `both` に門を置くのは、**push の新規作成（`ObsidianSync/Push/Endpoint.cs:121`）と同じ形**にするためである —— 本文を持つ資料を作る経路はすべて門を通す（IADR-0455 決定 1 の「すべての本番経路」）。
  既定が変わったときに発行漏れが再発しない。**現行の既定では観測できる差が無い**（後述「変異」）。
- 発行は **`SaveChangesAsync` と容量の再評価（`RecordUsageAndWarnAsync`。内部で保存する）の後**に置く（push と同じ順。確定していない状態を索引へ流さない）。
- タグ名は他経路と同じく `TagResolver.NamesAsync(db, ct)` で引く。**発行し得る分岐（`local` / `both`）でだけ**引く。
- 新しい設計判断は無い（既存の門を当てるだけ）ため、**IADR は起こさない**。IADR-0455 へ日付つき追記で経路を足したことを残す。

## 受け入れ基準

- [ ] 露出 ON の個人資料で `local` 解決 → その資料の `DocumentUpdated` が 1 件発行され、新しい本文（ローカル版）の指紋を運ぶ
- [ ] 露出 3 トグル OFF の個人資料で `local` 解決 → 発行されない（陽性対照: 本文はローカル版・版は +1）
- [ ] `both` 解決の別名資料は、元の資料が露出 ON でも露出 OFF で作られ、別名資料の ID で発行されない（陽性対照: 元の資料は露出 ON で発行されている）。元の資料も再発行されない
- [ ] 全 OFF の個人資料で `both` 解決 → どちらの ID でも発行されない（陽性対照: 別名資料にローカル本文が入っている）
- [ ] `PublishGateCoverageTests` が緑（素の発行を足していない）
- [ ] `local` の発行を外す変異で陽性の試験が落ちる
- [ ] IADR-0455 に `［2026-09-15 追記 / #1474］`、`docs/` の該当記述を更新

## テスト方針

- `Tests/Features/SyncConflicts/SyncConflictEndpointTests.cs`（Integration・`TestWebApplicationFactory` ＋ `RecordingMessageBus`）へ 4 件を足す。
  既存の競合の起こし方（`ConflictedNoteAsync`）を使い、露出は `PUT /private-notes/{id}/exposure` で ON にする。**新しいテストクラスは作らない**（テスト仕様書の対応表の基準線は動かない）。
- 🔴 **`both` の「露出 ON の元資料 → 別名資料の ID で発行される」は試験にできない。** 別名資料は必ず露出 OFF で作られるため、端点から発行が観測される状態に到達しない。
  代わりに「露出を継がないこと」と「発行されないこと」を陽性対照つきで固定する。

## 実測記録

### 赤（修正前・試験だけを足した状態）

`dotnet test src/knowledge/backend/Services/DocumentService/Tests/DocumentService.Tests.csproj --filter "FullyQualifiedName~SyncConflictEndpointTests"`

```
[FAIL] SyncConflictEndpointTests.localの解決は露出ONの個人資料で新しい本文のイベントを1件発行する
  Expected published to contain 2 item(s) because 本文を書き換えた解決は 1 回だけ再発行する, but found 1:
  （見つかった 1 件は露出を ON にしたときの発行。ContentFingerprint はサーバ版の指紋のまま）
失敗!   -失敗:     1、合格:    20、スキップ:     0、合計:    21
```

- 落ちたのは陽性の 1 件だけで、陰性の 3 件（`local` 全 OFF・`both` 露出 ON・`both` 全 OFF）は修正前も緑である —— 修正前の端点は何も発行しないので、陰性は元から成り立つ。陰性が意味を持つのは、修正後に陽性と同じ端点で緑になることと、各試験の陽性対照による。

### 緑（修正後）

`--filter "FullyQualifiedName~SyncConflictEndpointTests|FullyQualifiedName~PublishGateCoverageTests|FullyQualifiedName~PrivateNoteExposurePublishTests"` → `成功!   -失敗:     0、合格:    33、スキップ:     0、合計:    33`

### 変異

| # | 変異 | 結果 |
| --- | --- | --- |
| M1 | `local` 分岐の `written = doc;` を外す（`local` の発行を消す） | `SyncConflictEndpointTests` 21 件中 **失敗 1・合格 20**。落ちたのは `localの解決は露出ONの個人資料で新しい本文のイベントを1件発行する`（`Expected published to contain 2 item(s) … but found 1`）。戻した |
| M2 | 発行の条件を `written is not null && req.Resolution != Both` にする（`both` の発行を消す） | `SyncConflictEndpointTests` / `PrivateNoteExposurePublishTests` / `ObsidianSyncProtocolTests` 41 件 **すべて合格**。**想定どおり検出されない** —— 別名資料は必ず露出 OFF で作られ、門に弾かれるので、発行の有無は端点から観測できない（「テスト方針」の 🔴）。戻した |

変異を戻した後、`grep -rn 'MUTATION' src/knowledge/backend/Services/DocumentService/Features/SyncConflicts/` → 0 件（exit 1）。

### 検証（完了前）

| コマンド | 結果 |
| --- | --- |
| `dotnet test src/knowledge/backend/Services/DocumentService/Tests/DocumentService.Tests.csproj` | `成功!   -失敗:     0、合格:   563、スキップ:     0、合計:   563` |
| `dotnet format src/knowledge/backend/backend.slnx --verify-no-changes` | exit 0 |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | `✓ 782 tests passed`（新しいテストクラスは作っていないので `test-spec-coverage-baseline.json` は変えない） |
| `node scripts/check-trace-blocks.js` | `OK: 177 件の Markdown に trace ブロックの違反はありません。` |
| `node scripts/gen-knowledge-graph.js --check` | `OK: in-repo エッジ先の実在に違反はありません。`（参考の 2 件は既存の submodule 参照） |
| `node scripts/check-adr-numbering.js` | `OK: IADR の採番は重複・欠番なし、索引とも双方向で一致し昇順です。`（IADR を足していない） |
| `node scripts/check-doc-links.js` | `OK: 1374 件の Markdown … に破損した相対リンクはありません` |

## 計画書との差異

- 差異: なし。ADR-0061 決定 1・2 と IADR-0455 決定 1 の実装形に `Resolve` の 2 経路を揃えるもの。

## 未決事項

- 「両方を残す」で作る別名資料が元の資料の露出を継ぐべきか（現行は継がない＝既定 OFF）。計画に定めが無い。本件では決めない。
