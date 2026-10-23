---
title: 作業仕様書 — Document の期待版の照合と保存の間の窓を塞ぐ（同じ版を前提にした並行更新の一方を 409 にし、500 にも黙った上書きにもしない）
type: spec
status: done
related_ids: [FR-06, UC-03, SC-05, FR-19, FR-20, ADR-0027, IADR-0597, IADR-0596, IADR-0455]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-06）
  - planning:projects/microservices-platform/03_usecases/（UC-03）
issue: "#2103"
---

# 作業仕様書 — Document の楽観的同時実行（#2103）

> 本仕様書は実装着手前に作成した（着手 2026-10-23）。判断の記録は **IADR-0597**（新規）に置く。
> 親 issue #2047 の指摘 M-6。直前の #2102（IADR-0596・PR #2151）は「カタログの 2 つの再正規化が並行に台帳を書く窓は #2103 が閉じる」と境界を引いている。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-06**（文書の CRUD・版管理・メタデータ管理）。ユースケース: **UC-03**。画面: **SC-05**（文書管理。版の競合を「注意」として出す）。
- 計画に**楽観ロックの明文は無い**（`projects/microservices-platform/**` を「楽観」「競合」「ExpectedVersion」で走査し、SC-05 文書管理の該当は 0 件。SC-19・SC-20 の「競合」は Obsidian 同期のもの）。
  409 `version_conflict` の契約は実装の機能仕様書（FR-06）が定めたもので、本変更はその契約を並行の下でも守らせる修正である。計画との矛盾は無い。
- 計画 ADR: ADR-0027（取り込みの反映は少なくとも 1 回・再試行。カタログの受け手が保存で負けたときの扱いの前提）。
- 実装 ADR: **IADR-0597**（新設）。引き継ぐ判断: IADR-0596（カタログの受け手の順序。並行の窓は本件へ渡された）・IADR-0455（書き換える前に門の判定を取る。保存で負けたら発行しない）。

## 範囲

- 本 PR: DocumentService の `Document.Version` を並行トークンにする（モデルのみ。列は既存でスキーマは変わらない）。
  `PUT /documents/{id}`（`Update`）と `PATCH /documents/{id}/metadata`（`UpdateMetadata`）で、保存の負けを 409 `version_conflict` へ写す。
- 範囲外（境界）:
  - **Obsidian 同期の `baseVersion`（`POST /sync/push`・`move`）**: 同型の照合だが、本文をオブジェクトストレージへ**保存より前に**書き、競合台帳（`SyncConflict`）と同期履歴の失敗を残す契約を持つ。
    保存で負けたときに 409 へ写すには「先に書いた本文の扱い」と「競合台帳へ記録するか」の判断が要るので本 PR では写さない（トークンにより黙った上書きは起きなくなり、負けは例外になる）。§限界。
  - **ConversionService の `retry`（`ConversionJobStore.cs`）**: issue が「同型か確かめる」とした点。確かめた結果、同型である（§母集合）。別サービス・別の表で、本 PR の変更（DocumentService のモデル）の影響を受けないので、本 PR では直さない。
  - 409 の本文の形（RFC7807 にしない・`error` / `expectedVersion` / `currentVersion`）は変えない。

## 母集合（規則 9・10。着手前に引いた）

- **期待版を受ける口**（`ExpectedVersion` を `src/knowledge/backend` 全体で走査）: DocumentService の `Update/Command.cs`・`UpdateMetadata/Command.cs` の 2 つ、BFF の `DocumentUpdateRequest`（`PUT /bff/documents/{id}` が素通しで転送。409 は透過）。
  BFF に `PATCH …/metadata` は無い（`UpdateMetadata` の注記どおり。機械クライアントは DocumentService を直接呼ぶ）。
- **画面**: SC-05 `DocumentManagementPage` は編集の保存で常に `expectedVersion: editing.version` を送り、409 を `warning`・「注意」で出す（`conflictDetails`。本文の `errors` / `detail` が無ければ固定文言）。応答の形を変えないので画面の変更は要らない。
- **同じ `version_conflict` を返す他の口**: Obsidian 同期 `Push`・`Move`（`baseVersion`）。上の範囲外。
- **EF の同時実行トークン**: DocumentService に `IsConcurrencyToken` / `IsRowVersion` / `UseXminAsConcurrencyToken` は 0 件（実測）。knowledge 全体では WikiService の `DocumentSyncMarks.Revision`（IADR-0596）だけ。
- **`Document` を保存する経路（トークンの影響を受ける全経路）**: `Domain/Document.cs` の変更点（`Update`・`UpdateMetadata`・`AddTag`・`ApplyNormalized`・`SetMarkdownUri`・`Publish`・`Archive`・`ReplaceUntrustedBoundaryWrapped` は版を進め版の行を足す。
  `SetExposureAttributes`・`RecordContentFingerprint` は版を進めない）を呼ぶ口を走査した:
  `Update`・`UpdateMetadata`・`Publish`・`Archive`・`ArchiveAstQuotes`・`ArchiveBodyless`・`PutBody`・`StripUntrustedBoundary`・`AddTag`（gRPC）・`Catalog/DocumentNormalizedConsumer`・
  `ObsidianSync/Push`・`SyncConflicts/Resolve`・`PrivateNotes/SetExposure`。文書行を消す経路（`Delete`・`PrivateNotes/Purge`・`PrivateNoteMaintenanceService` の 90 日経過後の物理削除 2 か所）も、DELETE に版の条件が付く。`ExecuteUpdate` / `ExecuteDelete` で文書行を書く経路は 0 件。
- **修正前の並行時の挙動**（版を進める経路どうし）: 2 つとも版 v+1 の行を足し、後から確定する側は版テーブルの一意索引 `IX_DocumentVersions_DocumentId_Version` で 23505 → 500。
  **版を進めない経路**（露出トグル）と版を進める経路の並行は、古い属性で黙って上書きした。
- **ConversionService の `retry`**: `ConversionJobStore.PrepareRetryAsync`（`:117-132`）は `FirstOrDefaultAsync` → `TryRequeue()`（失敗状態かを見て処理中へ戻す）→ `SaveChangesAsync` で、**同型の check-then-write である**（実測）。
  `ConversionDbContext` に並行トークンは無い（`IsConcurrencyToken` / `xmin` / `FOR UPDATE` は ConversionService に 0 件）ので、同じジョブへの 2 つの再試行が並行すると両方が状態の判定を通り、`RawDocumentFetched` を 2 度発行し得る。
  別サービス・別の表であり、本 PR では変えない（報告して別に追う）。

## 要件

1. 同じ版を前提にした 2 つの `PUT`（または `PATCH …/metadata`）が並行すると、一方だけが 200、他方は 409 `version_conflict` になる。
2. 500 にならない。負けた側の内容で黙って上書きしない（文書は勝った側の内容・版。負けた側の版の行は残らない。更新イベントも出さない）。
3. 409 の `currentVersion` は**読み直した**現在版（負けた側が手元で進めた版ではない）。`expectedVersion` は要求に無ければ null。読み直したときに文書が無ければ 404。
4. 期待版を送らない更新も、読んだ後に追い越されたら 409（黙って上書きしない）。
5. 版を進めない保存も、読んだ後に版が進んでいれば負ける（黙って上書きしない）。

## 設計（決定は IADR-0597）

- `DocumentDbContext`: `e.Property(d => d.Version).IsConcurrencyToken()`。マイグレーションは足さない（並行トークンはスキーマに出ない）。モデルスナップショットだけ `IsConcurrencyToken()` を持たせる。
- `Features/Documents/DocumentVersionConflict.cs`: 競合の判定（`DbUpdateConcurrencyException` または `IX_DocumentVersions_DocumentId_Version` の 23505）と 409 の本文、保存で負けた後の読み直しを 1 か所に置く。
- `Update` / `UpdateMetadata`: 既存の照合はそのまま（同じ本文を `DocumentVersionConflict.Problem` で返す）。`SaveChangesAsync` を `catch (DbUpdateException) when (Is)` で囲み、負けたら発行せずに 409。

## 受け入れ基準

| # | 基準 | 試験（`Knowledge.IntegrationTests` の `DocumentConcurrentUpdateTests`。実 PostgreSQL） |
| --- | --- | --- |
| 1 | `PUT`: 一方だけ 200・他方 409（`currentVersion` は読み直した版）、文書は勝った側・版の行は勝った側の分だけ | `Update_OvertakenAfterVersionCheck_LoserGets409_AndWinnerIsKept` |
| 2 | `PATCH …/metadata`: 同上（属性は勝った側のまま） | `UpdateMetadata_OvertakenAfterVersionCheck_LoserGets409_AndWinnerIsKept` |
| 3 | 期待版なしでも 409（`expectedVersion` は null）・黙って上書きしない | `Update_WithoutExpectedVersion_Overtaken_Gets409_NotSilentOverwrite` |
| 4 | 版を進めない保存も負ける | `SaveWithoutVersionBump_AfterVersionAdvanced_LosesWithConcurrencyException` |
| 5 | 実際の同時送信で 500 が出ない（補助・非決定的） | `Update_TrulyConcurrent_OnlyOkOrConflict_AndVersionsMatchSuccesses` |

窓は `SaveChangesInterceptor` で決定的に開く（先の要求が照合を通った後、保存の直前に、同じ版を前提にした別の要求を確定させる）。
`Task.WhenAll` で送るだけでは、片方が読む前に他方が保存を終えることが多く、既存の照合が 409 を返して修正を外しても緑になる（基準 5 が全変異で緑なのはこのため）。
**EF InMemory では再現できない**（並行するトランザクションも一意索引の強制も無い）ので単体テストには置かない。

### 変異（修正を外すと赤になることの確認。実測）

| 変異 | 1 | 2 | 3 | 4 | 5 |
| --- | --- | --- | --- | --- | --- |
| 並行トークンを外す | 緑 | 緑 | 緑 | **赤** | 緑 |
| 409 への写像（`catch`）を外す | **赤**（500） | **赤** | **赤** | 緑 | 緑 |
| 両方を外す（修正前） | **赤** | **赤** | **赤** | **赤** | 緑 |
| 一意索引違反の分岐を外す（`DbUpdateConcurrencyException` だけ拾う） | **赤** | **赤** | **赤** | 緑 | 緑 |
| 読み直しをやめ、手元の版を `currentVersion` に返す | **赤** | 緑 | 緑 | 緑 | 緑 |

- **トークンを外しても 1〜3 が緑**なのは、版を進める 2 つの保存は版テーブルの一意索引で必ずぶつかるからである。トークンが要るのは版の行を足さない保存（4）。
- **一意索引違反の分岐は必須**（実 PostgreSQL では文書行の 0 行より先に 23505 が出る。EF のバッチの並べ方による）。

## 限界（残余）

- **Obsidian 同期 `Push`**: 追い越されると 409 ではなく例外（500）。本文は保存より前にオブジェクトストレージへ書かれる（既存の順。負けた側の本文が正準キーに残る）。修正前も同じ並行は版テーブルの一意索引で 500 だったので悪化はしない。写像には本文の書き戻しと競合台帳の扱いの判断が要る。
- **その他の書き込み口**（公開・アーカイブ・一括アーカイブ・本文投入・境界語の剥がし・タグの追加・競合の解決・露出トグル・削除）: 追い越されると例外（500）。版を進める経路は修正前も一意索引で 500 だった。
  **露出トグル・削除は修正前は黙って通っていた**（露出トグルは古い属性で上書き、削除は追い越した更新ごと消す）ので、並行時にだけ 500 が新たに出得る。いずれも同じ文書への同時操作が要る。
  個人資料の物理削除（完全削除の口・90 日経過後の定期処理）は、その瞬間に同じ資料が同期で書かれていると負けて例外になる（定期処理は次の回で拾い直す）。
- **カタログの受け手**（`DocumentNormalizedConsumer`）: 負けると例外で、メッセージングの再試行が読み直して IADR-0596 の順序判定からやり直す（ADR-0027）。

## 影響範囲

- コード: `DocumentService`（`DocumentDbContext`・モデルスナップショット・`DocumentVersionConflict`・`Update`・`UpdateMetadata`）。BFF・画面は変えない。
- 試験: `Knowledge.IntegrationTests/DocumentService/DocumentConcurrentUpdateTests.cs`（新規）。
- 文書: 機能仕様書 FR-06（処理・異常系・受け入れ基準・制約）、テスト仕様書 FR-06（T-113〜T-117）、`docs/api/openapi.yaml`（`PUT /bff/documents/{id}` の説明）。
- 配備: **マイグレーションは無い**（スキーマは変わらない）。DocumentService のイメージを差し替えるだけでよい。

## 検証

- `dotnet test …/DocumentService/Tests/DocumentService.Tests.csproj`（1124 件緑）。
- `dotnet test Knowledge.IntegrationTests --filter FullyQualifiedName~Knowledge.IntegrationTests.DocumentService`（43 件緑。新規 5 件を含む）、`McpTools`・`Messaging.DocumentUpdated`（5 件緑）。
- `dotnet format backend.slnx --verify-no-changes --include <変更ファイル>`（差分なし）。
- 上の変異表（実測）。
