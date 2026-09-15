---
title: DocumentUpdated を出す本番経路をすべて発行の門へ寄せる（PR #1281 のレビュー修正の当て直し）
type: spec
status: in-progress
related_ids: [FR-19, FR-06, FR-21, UC-11, SC-19, ADR-0061, ADR-0054, ADR-0058, IADR-0396, IADR-0455]
author: claude
created: 2026-09-15
updated: 2026-09-15
---

# 仕様書: `DocumentUpdated` を出す本番経路をすべて発行の門へ寄せる（#1471）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-19（露出 3 トグル・既定 OFF）／FR-06（文書の更新・公開・アーカイブ）／FR-21（本文の直接受け入れ）
- ユースケース / 画面: UC-11 / SC-19
- 関連 ADR: ADR-0061 決定 1・2・4（1 つでも ON なら載せる／全 OFF なら載せない／ON → OFF は削除まで及ぶ）／ADR-0054（`doc_scope`）／ADR-0058 決定 1・2（`doc_scope` は作成時に確定）
- 関連 IADR: IADR-0396 決定 4・5（発行の門・撤収）／**IADR-0455（本件で起こす）**
- 起票: #1471（PR #1281 の squash マージ後に push されたレビュー修正 `c4830568` が develop に入っていない）

## 目的・背景

PR #1281 は 2026-09-05 に squash マージされた。その**後に**同じブランチ（`feat/FR-19-1184-private-note-exposure-index`）へ
レビュー指摘への修正 `c4830568`（`DocumentUpdated` を出す本番経路をすべて `PublishUpdatedIfIndexableAsync` へ寄せる）が
push され、squash に含まれなかった。develop では門を通るのは GrantShare / RevokeShare / ObsidianSync.Push の 3 経路だけで、
**10 経路が `DocumentEndpoints.PublishUpdatedAsync(` を直接呼んでいる**。

`c4830568` は develop に対し 6 ファイルで競合するため、**機械的には当てず、経路を走査し直して意図を当て直す**。

## 実測の前提（2026-09-15・`origin/develop` = `9ff2d6e4`・`git rev-parse --is-shallow-repository` → `false`）

### 走査した母集合

`.claude/rules/traceability.md` 規則 1〜6・`traceability.repo.md` 規則 9 に従い、**誤りの側（直接呼び出し）から、軸を変えて**引いた。
出力は加工せずに読んだ。

| # | 軸 | コマンド | 結果（生の行数） |
| --- | --- | --- | --- |
| 1 | 発行の直接呼び出し（誤りの側） | `git grep -n "PublishUpdatedAsync(" -- src/knowledge` | **18 行**（内訳は下表） |
| 2 | 門の利用（正しい側） | `git grep -n "PublishUpdatedIfIndexableAsync(" -- src` | 4 行 = 定義 1（`DocumentEndpoints.cs:242`）＋ GrantShare / RevokeShare / ObsidianSync.Push |
| 3 | 発行ポートの保持者（別名で発行する経路の検出） | `git grep -n "IDocumentUpdatedPublisher" -- src/knowledge/backend/Services/DocumentService ":!src/knowledge/backend/Services/DocumentService/Tests"` | 保持する本番ファイルは軸 1・2 の 13 経路 ＋ `DocumentEndpoints.cs` ＋ ポート定義 ＋ Wolverine アダプタ ＋ `Program.cs`（DI 登録）。**軸 1・2 に出ない発行経路は 0** |
| 4 | イベントの直接構築 | `git grep -n -E "new DocumentUpdated\(\|DocumentUpdated\(" -- src/knowledge/backend/Services/DocumentService ":!…/Tests"` | 1 行（`WolverineDocumentUpdatedPublisher.cs:30`。アダプタ本体） |
| 5 | **属性を書き換える操作**（撤収の要否を決める軸） | `git grep -n -E "\.(Update\|UpdateMetadata\|ApplyNormalized\|SetExposureAttributes)\(" -- src/knowledge/backend/Services/DocumentService ":!…/Tests"` | 6 行（下表「属性」列の根拠） |
| 6 | 露出キーを組織文書で拒否する検証の有無 | `git grep -n -E "search_exposure\|graph_exposure\|ai_input\|DocumentExposure\.(SearchKey\|GraphKey\|AiKey\|AllKeys)\|AiInputExposure\.AttributeKey" -- src/knowledge/backend/Services/DocumentService src/knowledge/backend/Shared ":!…Tests"` | 定数定義とコメントのみ。**組織文書が露出キーを持つことを拒否する検証は 0**（後述「判断 2」の根拠） |

軸 1 の 18 行の内訳と扱い:

| 行 | 扱い | 理由 |
| --- | --- | --- |
| `Domain/Ports/IDocumentUpdatedPublisher.cs:18` | 除外 | ポートの宣言（呼び出しではない） |
| `Infrastructure/Messaging/WolverineDocumentUpdatedPublisher.cs:13` | 除外 | アダプタの実装（呼び出しではない） |
| `Features/Documents/DocumentEndpoints.cs:220` / `:228` / `:246` | 除外 | **門の内部実装**（定義・ポート呼び出し・門からの委譲） |
| `Tests/…/IngestTagFilterTests.cs:183` / `NormalizedAssetLedgerTests.cs:33` / `NormalizedBodyPresenceTests.cs:37` | 除外 | テスト用の偽ポートの実装（本番経路ではない） |
| 残り **10 行** | **対象** | 下表 |

### 対象 10 経路と、門の形の選択

「属性」列は軸 5 による。**属性を書き換え得る経路は、書き換えで索引可でなくなったときに撤収のイベントを出す必要がある**
（IADR-0396 決定 5）。単純な門（「今」索引可のときだけ出す）を当てると、**消させるためのイベントが門に弾かれて本文が索引に残る**。

| 経路 | 属性 | 個人資料が到達するか | 当てる門 |
| --- | --- | --- | --- |
| `Documents/AddTag/AddDocumentTagUseCase.cs:74` | 変えない | する（所有者は `CanWrite`、管理者も可） | 索引可のとき |
| `Documents/Archive/Endpoint.cs:24` | 変えない（状態のみ） | する（管理者・文書種別を問わない） | 索引可のとき |
| `Documents/Catalog/DocumentNormalizedConsumer.cs:87` | **全置換（`ApplyNormalized`）** | 想定経路は無いが ID が一致すれば置換される | **索引可、または直前まで索引可だったとき** |
| `Documents/Create/Endpoint.cs:99` | 新規 | しない（`CreateDocumentValidator` が `doc_scope=private-note` を 400） | 索引可のとき |
| `Documents/Publish/Endpoint.cs:37` | 変えない（状態のみ） | する（管理者） | 索引可のとき |
| `Documents/PutBody/Endpoint.cs:66` | 変えない（本文のみ） | する（所有者の動的束縛） | 索引可のとき |
| `Documents/Update/Endpoint.cs:65` | **全置換（`Update`）** | する（管理者。`doc_scope` 不変の検査だけがある） | **索引可、または直前まで索引可だったとき** |
| `Documents/UpdateMetadata/Endpoint.cs:61` | **全置換（`UpdateMetadata`）** | する（同上） | **索引可、または直前まで索引可だったとき** |
| `PrivateNotes/SetExposure/Endpoint.cs:51` | **露出の投影（`SetExposureAttributes`）** | 個人資料だけ | **索引可、または直前まで索引可だったとき**（現行の条件をそのまま門へ移す） |
| `Tags/Rename/Endpoint.cs:81` | 変えない | する | 索引可のとき |

🔴 **`c4830568` をそのまま当てると Update / UpdateMetadata / 正規化の 3 経路で撤収が落ちる。** `c4830568` は SetExposure だけを例外にしていたが、
管理者の属性全置換（露出キーを落とす・`excluded` にする）も**同じ ON → OFF の遷移を作れる**。
現行（直接呼び出し）では消費側（`IngestionService` / `GraphService`）が同じ述語で削除しているので、門へ寄せたことで**漏れる向きの退行**になる。

### 軸 5 に出たが対象外としたもの

| 行 | 理由 |
| --- | --- |
| `ObsidianSync/Push/Endpoint.cs:232` | `doc.Attributes` をそのまま渡す（属性を変えない）。既に門を通っている |
| `SyncConflicts/Resolve/Endpoint.cs:67` | 属性を変えない。**そもそも `DocumentUpdated` を発行していない**（本文を書き換えるのに再発行しない既存の欠け。本 issue の射程外 —— 発行経路を足すのは別判断。報告に残す） |

## 対象範囲

- 対象: 上表 10 経路の発行を門経由にする／`DocumentEndpoints` の門を 2 つの形で持ち、`PublishUpdatedAsync` を `private` にする／
  直接発行が本番経路に戻ったら落ちる試験／IADR-0396 への日付つき追記／IADR-0455。
- 対象外:
  - 消費側（`IngestionService` / `GraphService` / `WikiService`）の変更
  - `SyncConflicts/Resolve` の再発行漏れ（上記）
  - 既に索引にある点の backfill・掃除

## 設計

### 判断 1: 門を 2 つの形で持つ（IADR-0455 決定 1）

- `PublishUpdatedIfIndexableAsync(bus, db, d, names, ct)` —— **属性を変えない経路**。今の文書が門を通るときだけ出す。
- `PublishUpdatedIfIndexableOrWithdrawingAsync(bus, db, d, wasPublishable, names, ct)` —— **属性を書き換え得る経路**。
  「今通る」または「書き換える前は通った」ときに出す。呼び出し側は**書き換える前に** `DocumentEndpoints.PassesPublishGate(doc)` を取る。
- `PublishUpdatedAsync` は `private` にする（テストからの参照は 0 件。軸 1 で確認）。**`DocumentEndpoints.` を付けた直接呼び出しはコンパイルで止まる。**
  ポートを直接叩く形（`bus.PublishUpdatedAsync(`）は型では止まらないので、ソース走査の試験で止める。

### 判断 2: 門は**個人資料にだけ**効かせる（IADR-0455 決定 2）

`DocumentExposure.IsAllowed` は**明示値を文書種別より優先する**（`excluded` なら組織文書でも false）。軸 6 のとおり、
組織文書が露出キーを持つことを拒否する検証は無い（作成・更新・正規化とも属性は自由な辞書）。
したがって「組織文書は `IsIndexable` が常に true」は**データの前提**であって構造の保証ではない。

門を `IsIndexable` だけで書くと、露出キーを明示的に全 `excluded` にした組織文書は**発行そのものが止まり**、
`WikiService` がアーカイブ（ページの非公開化）を受け取れなくなる（**可視性を落とす操作が届かない＝漏れる向き**）。

よって門の述語は **`!DocumentScopes.IsPrivateNote(attrs) || DocumentExposure.IsIndexable(attrs)`** とする。

- 組織文書: **常に通る** —— 直接呼び出しだった 10 経路の挙動は、データに依らず 1 ビットも変わらない。
- 個人資料: `IsIndexable` そのもの —— IADR-0396 決定 4 の門（「個人資料は 1 つでも ON のときだけ流す」）と同じ。
- 消費側の索引の門（`IngestionService`）は `IsIndexable` のまま変えない。組織文書に対しては従来どおり受け手が削除する。

**既に門を通っていた 3 経路（GrantShare / RevokeShare / Push）への影響**: Push は個人資料だけなので不変。
共有の付与・取り消しは「露出キーを全 `excluded` にした組織文書」でだけ、発行されるようになる（従前は抑止）。
消費側の結果は削除・撤収の冪等な再実行と Wiki の再同期であり、**直接呼び出しの経路が従来からそうしていた挙動に揃う**。

### 経路ごとの挙動の差（組織文書は全経路で不変）

| 経路 | 個人資料での変化 | 正しさの根拠 |
| --- | --- | --- |
| AddTag / Archive / Publish / PutBody / Rename | **全 OFF の個人資料では発行しなくなる**（従前は出していた） | ADR-0061 決定 2・IADR-0396 決定 4「3 つとも OFF はイベントそのものを出さない」。受け手の結果は従来も「削除（空振り）」「撤収（空振り）」「Wiki は個人資料を同期しない（アーカイブの非公開化はページが無いので空振り）」であり、失われる作用は無い |
| Update / UpdateMetadata / 正規化 | 全 OFF → 全 OFF の保存では発行しなくなる。**ON → OFF（露出キーの削除・`excluded` 化）では従来どおり発行する** | 同上 ＋ 決定 5（撤収は再発行が契機） |
| SetExposure | 変化なし（条件を門へ移しただけ） | 決定 4・5 |
| Create | 変化なし（個人資料は 400 で到達しない） | `CreateDocumentValidator` |

SetExposure だけは、全 OFF → 全 OFF の保存でタグ辞書の読み取り（`TagResolver.NamesAsync`）が 1 回増える（発行はしない）。応答・イベントは不変。

## 受け入れ基準

- [ ] `Features/` 配下の本番経路に `DocumentEndpoints.PublishUpdatedAsync(` の直接呼び出しが 0 件（門の内部実装を除く）
- [ ] 組織文書の各経路の挙動が変わらない（既存試験が緑）。露出キーを明示的に全 `excluded` にした組織文書でも発行される（試験で固定）
- [ ] 直接呼び出しを 1 つ戻すと落ちる試験がある（ミューテーションで実証）
- [ ] 管理者の属性更新（PUT / PATCH）で露出が外れた個人資料は撤収のイベントが出る（試験で固定。単純な門へ戻すと落ちることをミューテーションで実証）
- [ ] 全 OFF の個人資料を管理者が更新しても発行されない（上の陰性。対で置く）
- [ ] IADR-0396 に日付つき追記（`［2026-09-15 追記 / #1471］`）、`DocumentEndpoints.cs` に注記、IADR-0455 を索引へ登録

## テスト方針

- **ソース走査**（`Tests/Features/Documents/PublishGateCoverageTests.cs`・Unit）: DocumentService の本番 `*.cs`（`Tests/` `bin/` `obj/` を除く）から
  `PublishUpdatedAsync(` を引き、許可 3 ファイル（ポート宣言・アダプタ・`DocumentEndpoints.cs`）以外に 0 件であることを固定する。
  **陽性対照**として、許可ファイル側で実際に一致が取れていること（走査と正規表現が生きていること）を同じ試験で示す。
- **挙動**（`PrivateNoteExposurePublishTests` へ追加・`TestWebApplicationFactory` ＋ `RecordingMessageBus`）: 上記受け入れ基準 2・4・5。
- Docker を要する Integration（Testcontainers）は手元で起こせない。本件の試験は InMemory EF と記録用バスで完結する。

> ［2026-09-15 追記 / #1471］**フェーズ末監査（PR #1473・別文脈のエージェント・条件付き合格）の必須指摘を反映した。**
> 監査の変異 M7（`DocumentNormalizedConsumer` を撤収の門から単純な門へ戻す）が DocumentService.Tests 557 件をすべて通過しており、
> **再正規化での撤収だけ試験が無かった**。`Tests/Features/Documents/Catalog/NormalizedExposureWithdrawalTests.cs`（Unit・MassTransit in-memory ハーネス）を足した:
> 既存の個人資料を台帳へ置き、属性を差し替える `DocumentNormalized` を消費させて、①露出 ON → OFF で撤収のイベントが 1 件出る ②全 OFF のままでは出ない（陽性対照: 台帳の題名は更新されている）。
> **変異の再確認**: 単純な門へ戻すと ① だけが「コレクションが空」で落ちる（失敗 1・合格 1）。戻した後は DocumentService.Tests 559 件通過・`dotnet format` OK・`scripts.test.js` 782 件通過。
> 監査の非ブロッキング指摘（走査試験の死角＝`DocumentEndpoints.cs` 全体の許可・メソッドグループ参照／改行を挟む呼び出し、全 OFF の個人資料で「再送による自己修復」が無くなったこと）は本 PR では直さず記録に留める。`SyncConflicts/Resolve` の件は #1474 で起票した。

## 計画書との差異

- 差異: なし。ADR-0061 決定 1・2・4 と IADR-0396 決定 4・5 の実装形を揃えるもので、門の述語を「個人資料に限る」と明示したのは
  IADR-0396 決定 4 の本文（「個人資料は…のときだけ流す」「組織文書は常に true」）を**データの前提から構造へ移した**ものである（IADR-0455）。

## 未決事項

- 組織文書が露出キーを持つこと自体を許すか（検証で拒否するか）。**本件では決めない**（計画 ADR-0061 は露出を個人資料の概念として定めており、
  組織文書での明示値の意味は計画に無い）。門を個人資料に限ったことで、本件の挙動はこの決定に依存しない。
- `SyncConflicts/Resolve`（`local` 解決）が `DocumentUpdated` を再発行しない件（索引が古い本文のまま残り得る）。
