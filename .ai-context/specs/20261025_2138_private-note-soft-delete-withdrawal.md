---
title: 作業仕様書 — 論理削除した個人資料を見つかる面から外し、復元で元の露出のまま戻す（#2138）
type: spec
status: done
related_ids: [FR-19, FR-20, UC-11, SC-03, SC-19, ADR-0142, ADR-0061, ADR-0037, ADR-0057, ADR-0056, ADR-0027, IADR-0636, IADR-0270, IADR-0396, IADR-0455, IADR-0595, IADR-0608, IADR-0610]
author: claude
created: 2026-10-25
updated: 2026-10-25
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0142_private-note-soft-delete-withdraws-exposure.md（決定 1〜6・2026-10-24 の決定 2 の補完・フォローアップ 1〜4）
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-19 の受け入れ基準「論理削除の間」）
  - planning:projects/microservices-platform/05_screens/01_screens.md（SC-19「削除の確認ダイアログ」の 2026-10-23 追加）
---

# 仕様書: 論理削除した個人資料を見つかる面から外し、復元で戻す（#2138）

## 起点となる計画書（トレーサビリティ）

- 要求: FR-19（個人資料。論理削除・90 日・復元）、FR-20（Obsidian 側の削除も論理削除）。UC-11。画面: SC-03（閲覧）・SC-19（削除済みタブ）
- 計画 ADR: **ADR-0142**（利用者裁定 2026-10-23・2026-10-24。環流 planning#836 ← 本 issue）。補完の対象 ADR-0037 決定 5・ADR-0061 決定 1〜6。変えないもの ADR-0057（完全削除の伝播）
- 実装 ADR: **IADR-0636**（本件の手段の決定。ADR-0142 決定 6 が「手段と試験を IADR に書く」と定める）
- 親: #2047（指摘 L-9）

## 計画の読み（実装前に確かめたこと）

- 復元時に何を出すかは**計画が定めている**（ADR-0142 決定 4: 保っていた露出 3 トグルの値のとおりに戻す。1 つでも ON なら載せ直し、3 つとも OFF なら載せない）。止めて報告する曖昧さは無い。
- 決定 2 の補完（2026-10-24）: MCP の `get_document` / `list_documents` も拒む。判定は SC-03 と共有したまま。
- 未決は ADR-0142 フォローアップ 3（削除中の共有の変更）だけ。実装が選び、環流する（IADR-0636 決定 4: 許す）。

## 母集合（規則 9）

非試験の `src/knowledge/backend/**/*.cs` を、誤りの側の文字列で引いた。

| 軸 | 引いた文字列 | 件数 | 箇所 | 扱い |
| --- | --- | --- | --- | --- |
| 論理削除の経路 | `.SoftDelete(` | 2 | `PrivateNotes/SoftDelete/Endpoint.cs`・`ObsidianSync/Delete/Endpoint.cs` | **直す**（撤収の発行） |
| 復元の経路 | `.Restore(` | 1 | `PrivateNotes/Restore/Endpoint.cs` | **直す**（載せ直しの発行） |
| 露出の変更 | `.SetExposure(` / `.SetExposureAttributes(` | 1 経路 | `PrivateNotes/SetExposure/Endpoint.cs` | **直す**（削除中は 409） |
| `DocumentUpdated` の発行 | 唯一の発行点 `DocumentEndpoints.PublishUpdatedAsync`（`private`。全経路が門を通すことは `PublishGateCoverageTests` が走査で固定） | 1 | `Features/Documents/DocumentEndpoints.cs` | **直す**（削除中なら撤収の形）。これで共有の付与・取り消し・タグの改名・再発行・管理者の属性の全置換・同期の衝突の解決が削除中に門を通っても載り直さない |
| 読み取りの判定 | `CanReadAsync(` の呼び出し | 5 | `DocumentReadUseCase`（一覧・ページ・閲覧・個別 3 操作の共通）・`DocumentManageScope`・`ListPage/Endpoint` | **直す**（必須の引数で論理削除を渡す）。SC-03・REST の読み取り 5 口・gRPC `DocumentRead`・MCP の `get_document` / `list_documents`・本文（`DocumentContentUseCase`）がここを通る |
| 削除状態の読み手 | `.IsDeleted` / `DeletedAt ==\|!=` | 13 | 緊急アクセス（`BreakGlass/Activate`・`BreakGlassViewGate`）・同期（`Manifest`・`Move`・`Pull`・`Push`）・定期処理・一覧の導出・`Purge`・`Restore`・`SyncConflicts/Resolve`・`SyncSettings` | 変えない（下の「やらないもの」） |
| 受け手 | `DocumentExposure.IsIndexable` / `IsGraphAllowed`（取り込み・グラフ）・Wiki の `IsWikiPublishable` | 3 | `IngestionService`・`GraphService`・`WikiService` | 変えない（既存の ON → 全 OFF の撤収で外れる。Wiki は個人資料を元から同期しない） |

**やらないものとその理由**:

- 緊急アクセス: 既に論理削除中を拒んでいる（IADR-0610 条件 ⑤。`BreakGlassViewGate.cs:68`）。
- Obsidian の同期（`Pull`・`Manifest` が `IsDeleted` を運ぶ）: 所有者の端末の経路で、端末側で消させるための情報である。ADR-0142 の 5 面と SC-03・MCP の対象外。
- SC-19 の削除済みタブ（一覧の導出）: 所有者が論理削除中の資料を見る唯一の面（ADR-0142 決定 2）。変えない。
- SC-19 の削除確認の文言（ADR-0142 フォローアップ 4。配備の後に 1 文を足す）: フロントエンドとカタログ（ja / en）の変更で、別の PR に分ける（本 PR の配備が前提。後述）。
- 版を持たない状態変化の順序づけ（IADR-0636 の残余リスク）: 露出の変更に従前からある窓と同じで、別 issue。

## 受け入れ基準

発行の側（DocumentService の端点。`PrivateNoteSoftDeleteWithdrawalTests`）:

- [x] S1: SC-19 の削除で、露出 3 キーを `excluded` にした撤収の形の `DocumentUpdated` が 1 件出る。台帳の露出（`IncludeInSearch` 等）は ON のまま残る。
- [x] S2: Obsidian 側の削除でも同じ撤収の形が出る。
- [x] S3（陰性・境界）: 露出が 3 つとも OFF の資料は削除しても何も出ない（削除そのものは確定する＝陽性対照）。削除済みへの再削除は何も出さない。
- [x] S4: 復元で台帳の露出のまま出し直す（ON の軸は included、OFF の軸は excluded、共有先も運ぶ）。3 つとも OFF なら何も出ない。
- [x] S5: 削除中の共有の付与・取り消しは許し（201 / 204）、出る発行はすべて撤収の形である。
- [x] S6: 削除中の露出の変更は 409 `deleted` で、台帳もイベントも変えない。復元すれば変えられる（陽性対照）。
- [x] S7（発行失敗の注入）: 確定（`CommitAsync`）が失敗すれば、削除の印も残らず撤収も出ない。同じ資料は正常な確定で削除でき撤収が出る（陽性対照）。

読み取りの側:

- [x] R1: 削除中は、共有先も所有者も `GET /documents/{id}`・版の一覧・版の取得で 404、`GET /documents` の一覧に出ない。削除の前は両者とも開ける（陽性対照）。復元の後は共有先が再び開け、一覧に出る（`PrivateNoteSoftDeleteWithdrawalTests`）。
- [x] R2: 判定点 `DocumentReadAccess` は、門が閉じた枝・開いた枝の両方で、削除中の個人資料を所有者にも共有先にも返さない。印は組織文書の判定を変えない（`DocumentReadContentAbacTests`）。

伝播（受け手。撤収の形は本番と同じ `DocumentExposure.WithAllExcluded` で作る）:

- [x] P1: 取り込み: 撤収で全コレクション（語彙索引を含む）から点が消え、**同じ版**の復元で載り直す。撤収の後に古い版が届いても載り直さない（`IngestionOrderingAndAtomicityTests`。版の印を持つ索引の偽物）。
- [x] P2: 検索: 削除の前は当たり、撤収の後は全文・ハイブリッドとも当たらず（同じ語の別の文書は当たる＝陽性対照）、復元で再び当たる（`IngestToSearchInProcessTests`。本番の取り込み → 本番の検索ホスト）。
- [x] P3: グラフ: 撤収でノードが消え、同じ版の復元で共有先の属性ごと作り直される。撤収は却下済みの AI 提案を残す（`GraphDocumentSyncConsumerTests`）。
- [x] P4（ADR-0142 フォローアップ 1）: 撤収の後の完全削除が失敗しない —— グラフ（却下済みの提案まで消え、無関係のノードは残る）と、実 Qdrant の検索の削除（`IngestToSearchQdrantTests.PurgeAfterSoftDeleteWithdrawal_SucceedsWithNothingLeft`。`Category=Integration`。PR の CI では走らず、`integration.yml` で走る）。
- [x] 変異: 撤収の形を外す（5 本赤）・読み取りの判定を外す（3 本赤）・削除の保存を確定の外へ出す（1 本赤）・409 を外す（1 本赤）。
- [x] 既存の試験の改め: `PrivateNoteLifecycleTests` の陽性対照「保管中の資料は所有者に SC-03 で返る」は ADR-0142 決定 2 と逆になったので、台帳の行の有無で測る形に改め、保管中は 404 を主張した。
- [x] DocumentService 1224・IngestionService 254・GraphService 857 が緑。`Knowledge.IntegrationTests` の検索 12 件（Docker を要する 4 件は skip）。`dotnet format --verify-no-changes` 緑。

## PoC の事前手順

- **スキーマ・設定の変更は無い**（migration なし）。
- 🔴 **既に論理削除中で露出が ON の資料は、配備しただけでは索引・グラフから外れない**（撤収は削除の時点で出す）。配備の後に一度だけ、削除中の資料を撤収の形で出し直す必要がある。
  再発行の端点（`Republish`）は門を通るので、削除中の資料は本変更により撤収の形で出る。手順は PR 本文に書く。
