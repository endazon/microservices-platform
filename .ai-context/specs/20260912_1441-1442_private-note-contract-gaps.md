---
title: SC-19 / SC-20 の「契約に無いので描かない」6 要素のうち裁定に依存しない 4 要素の BFF 契約を足し、画面へ描く（#1441 / #1442）
type: spec
status: done
related_ids: [FR-19, FR-20, UC-11, SC-19, SC-20, ADR-0036, ADR-0037, ADR-0046, ADR-0054, ADR-0063, IADR-0131, IADR-0139, IADR-0270, IADR-0352, IADR-0437, IADR-0444]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
  - planning:projects/microservices-platform/07_adr/ADR-0037_obsidian-sync-method.md
  - planning:projects/microservices-platform/10_feedback/20260912_mock-elements-without-contract.md
---

# 仕様書: 個人資料の契約の穴（公開範囲・同期状態・タグ／同期対象範囲・競合）を埋める

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: `FR-19`（個人資料）/ `FR-20`（Obsidian 連携）
- 非機能要件（NFR）: なし（製品機能の実装。個別番号を当てない）
- ユースケース（UC）: `UC-11`
- 画面（SC）: `SC-19`（主要素 1・2・5・6）/ `SC-20`（主要素 3・5）
- 関連 ADR: `ADR-0036`（D-06 共有の単位は個人とグループ。§未確定事項 5 は未決）/ `ADR-0037`（決定 3・4 同期対象の判定と
  フォルダ粒度、決定 7 競合は利用者が 3 択、決定 9 監査はタイトルを書かない、決定 14 KB が正）/ `ADR-0046`（本文編集は
  Obsidian 経路のみ）/ `ADR-0054`（`doc_scope`）/ `ADR-0063`（タグ提案は個人資料も対象）/ `IADR-0131`（openapi 手書き・
  3 つを開いて起こす・状態文字列は enum にしない）/ `IADR-0139`（同型の契約追加はドメイン単位で 1 PR）/ `IADR-0270`
  （バックエンド中核）/ `IADR-0352`（push は 409 を自動解決しない・3 択）/ `IADR-0437`（QueryState）
- 計画書リンク: `projects/microservices-platform/05_screens/01_screens.md` §SC-19・§SC-20「供給元と契約の現状」、
  `10_feedback/20260912_mock-elements-without-contract.md`（planning#614 の裁定）

## 目的・背景

planning#614 の裁定（2026-09-12）は、SC-19 の公開範囲・同期状態・タグと SC-20 の同期対象範囲・競合解決・同期履歴の
6 要素を「計画は定めているが **BFF 契約が追いついていない**」と裁定し、各要素の供給元を画面設計へ書いた。
本作業はその契約を足し、画面に描く（#1441 / #1442）。

🔴 **2 件は計画側の裁定待ちである**（planning#618 を起票。2026-09-12）。
- 公開範囲の**指定先の型**（ADR-0036 §未確定事項 5。Keycloak グループ／部門／プロジェクト）
- 同期履歴の **N と保持期間**（SC-20 主要素 6）

**依存しない 4 要素を先に実装する**: SC-19 の公開範囲 **3 状態のみ**（指定先の一覧・変更ダイアログは裁定後）・同期状態・
タグ、SC-20 の同期対象範囲・競合の一覧と解決。同期履歴は従前どおり描かない。

**2 issue を 1 PR に束ねる**（IADR-0139 決定 1。同じ資源 `private-notes` に対する同型の契約追加で、上限 4 件以内）。

## 対象範囲

- 対象（契約）: `docs/api/openapi.yaml`（`PrivateNoteDto` の 5 項目追加、`/bff/private-notes/sync-settings`・
  `/bff/private-notes/conflicts*` の新設）、`Knowledge.Contracts/Dtos/PrivateNoteDto.cs`、orval 生成物、
  `scripts/contract-schema-baseline.json`（`--update`）、`docs/api/BFF_bff-surface.md`
- 対象（後段 DocumentService）: 一覧の写像（公開範囲 3 状態・同期状態・タグ名の導出）、`SyncSettings`・`SyncConflict` の
  エンティティと Migration、`/private-notes/sync-settings`・`/private-notes/conflicts*` の口、push の 409 時の競合記録、
  解決の 3 択の実装
- 対象（BFF）: `Knowledge.Bff.Endpoints/PrivateNoteBffEndpoints.cs` への透過中継 5 口（書き込みは write ゲート）
- 対象（画面）: SC-19 の 3 列と絞り込み軸、SC-20 の同期対象範囲区画と競合区画（一覧・差分・3 択・確認ダイアログ）、
  単体テスト・E2E・i18n（ja/en）、`docs/screens/SC-19_*.md` / `SC-20_*.md` の「実装していない」節
- 対象外（裁定待ち。planning#618）: 公開範囲の指定先の一覧と変更ダイアログ（SC-19 主要素 3）、同期履歴（SC-20 主要素 6）
- 対象外（本作業の主題でない）: 同期の一時停止／再開（SC-20 主要素 7）、緊急アクセス、利用者単位の露出既定、
  プラグイン（`obsidian-plugin/`）の変更（プロトコル `/private-notes/sync/*` の要求・応答は変えない）

## 設計

### 契約（`docs/api/openapi.yaml`。手書きが正本。IADR-0131）

**`PrivateNoteDto` に 5 項目を足す**（すべて required。応答側は非 null）:

| 項目 | 型 | 値集合（`type: string` ＋ description。enum にしない） | 供給元（後段の導出） |
| --- | --- | --- | --- |
| `visibility` | string | `private`（非公開・既定）/ `users`（個人指定）/ `groups`（グループ指定） | `DocumentShare` の行。0 行 → `private`、`group` 行が 1 つでもあれば `groups`、それ以外 → `users`（SC-19 主要素 2 の 3 状態。グループ共有は個人共有より広い状態として優先） |
| `sharedUserCount` | int32 | 0 以上 | `DocumentShare` の `user` 行数 |
| `sharedGroupCount` | int32 | 0 以上 | `DocumentShare` の `group` 行数 |
| `syncState` | string | `conflict`（競合あり）/ `target`（同期対象）/ `excluded`（対象外） | 未解決の `SyncConflict` が 1 件でもあれば `conflict`。それ以外は「所有者に有効な同期端末が 1 台以上ある」かつ「同期対象フォルダが未設定、または `vaultPath` がいずれかの対象フォルダ配下」なら `target`、さもなくば `excluded`（ADR-0037 決定 3・4。判定順は `conflict` → `target` → `excluded`。未解決の競合が無い削除済みの資料は常に `excluded`） |
| `tags` | string[] | タグ辞書の表示名 | `Document.Tags`（辞書 ID）を `TagResolver.ToNames` で表示名へ |

🔴 **指定先（共有相手の識別子・表示名）は載せない**（planning#618 の裁定待ち。型が決まるまで契約に出さない）。

**同期設定（SC-20 主要素 3）**:

- `GET /bff/private-notes/sync-settings` → 200 `SyncSettingsDto { targetFolders: SyncTargetFolderDto[], updatedAt: date-time|null }`
  - `SyncTargetFolderDto { path: string, noteCount: int32, lastSyncAt: date-time|null }` —— `noteCount` は削除済みを除く配下の資料数、
    `lastSyncAt` は配下の資料の `updatedAt` の最大（無ければ null）
  - フォルダ未設定は `targetFolders: []`（= 全資料が対象。ADR-0037 決定 3 の既定）
- `PUT /bff/private-notes/sync-settings` 要求 `UpdateSyncSettingsRequest { targetFolders: string[] }` → 200 `SyncSettingsDto`
  - 検証: 各要素は空でない・先頭末尾の `/` を正規化（`a/b`）・重複不可・最大 100 件・各 1024 文字以内 → 違反は 400 ProblemDetails
  - 🔴 **対象から外したフォルダの資料は削除しない**（決定 4。同期停止＝`syncState` が `excluded` になるだけ）
- `x-roles: []`（認証のみ）。PUT は BFF の write ゲート（`ForwardIfWritableAsync`）を通す

**競合（SC-20 主要素 5）**:

- `GET /bff/private-notes/conflicts` → 200 `SyncConflictSummaryDto[]`（未解決のみ・検出日時の新しい順）
  - `SyncConflictSummaryDto { id: uuid, noteId: uuid, title: string, vaultPath: string, detectedAt: date-time, deviceId: uuid, deviceName: string, localBaseVersion: int32, serverVersion: int32 }`
- `GET /bff/private-notes/conflicts/{id}` → 200 `SyncConflictDetailDto`（Summary ＋ `localContent: string`, `serverContent: string`）/ 404
  - 2 ペイン差分（ローカル版／サーバ版）の材料。本文は詳細だけが返す（一覧を重くしない）
- `POST /bff/private-notes/conflicts/{id}/resolve` 要求 `ResolveSyncConflictRequest { resolution: string }`（`local` / `server` / `both`）
  → 200 `ResolveSyncConflictResponse { conflictId, noteId, resolution, noteVersion: int32, createdNoteId: uuid|null }` / 400 / 404 / 409（解決済み）
  - `local`: 保存してあるローカル本文を**新しい版**として資料へ書く（`serverVersion` を土台に 1 版進める）
  - `server`: サーバ版をそのまま採る（資料は変えない）
  - `both`: サーバ版はそのまま、ローカル本文を**別名の新規資料**（`<元パス> (競合 yyyy-MM-dd HHmm).md`・タイトルも同様）として作る。
    `createdNoteId` に新資料の ID。容量上限（ADR-0037 決定 17）の新規作成拒否が適用される（507）
  - いずれも競合を解決済みにする（`resolvedAt` / `resolution`）。**自動解決は行わない**（決定 7）
- `x-roles: []`。resolve は write ゲート

**競合の記録（後段 push の 409 経路。プロトコルの応答は不変）**: `PushNoteEndpoint` が `version_conflict` を返す直前に
`SyncConflict` を 1 行起こし、要求の最終本文（`edits[^1].content`）をオブジェクトストレージ（鍵 `private-notes/conflicts/{conflictId}`）へ保存する。
同じ資料・同じ端末の未解決競合が既にあれば**上書き**（最新のローカル本文に更新。行は増やさない）。
push が成功したとき（正しい `baseVersion` で書けた＝プラグイン側で解決済み）は、その資料の未解決競合を `resolution = client` で閉じる。
🔴 監査は ADR-0037 決定 9 のとおり「誰が・いつ・何件」でタイトルを書かない。

### 後段の永続化（DocumentService）

- `SyncSettings`（PK `OwnerId`、`TargetFolders` jsonb（`List<string>`）、`UpdatedAt`）
- `SyncConflict`（PK `Id`、`DocumentId`（→ `PrivateNote` Cascade）、`OwnerId`、`DeviceId`、`LocalBaseVersion`、`ServerVersion`、
  `LocalContentUri`、`DetectedAt`、`ResolvedAt?`、`Resolution?`（`local`/`server`/`both`/`client`）。索引 `(OwnerId, ResolvedAt)`）
- Migration は `dotnet ef migrations add AddSyncSettingsAndConflicts`（ローカルの .NET 10 SDK と dotnet-ef で生成。手書きしない）

### BFF

透過中継（`RelayAsync`）を 5 口足す。`WithName` は operationId のケバブケースに一致（`BffSyncSettingsGet` / `BffSyncSettingsUpdate` /
`BffSyncConflictList` / `BffSyncConflictGet` / `BffSyncConflictResolve`）。PUT / resolve は `ForwardIfWritableAsync`。

### 画面

- SC-19: 一覧に「公開範囲」「同期状態」「タグ」の 3 列。3 状態はいずれも `StatusBadge`（色＋アイコン＋文言。**色だけで意味を持たせない**）。
  絞り込み（主要素 6）に公開範囲・同期状態・タグを足す（URL の `?visibility=` / `?sync=` / `?tag=`）。指定先の表示・変更は置かない
  （裁定待ちの旨を仕様書に書く）。
- SC-20: 「同期対象範囲」区画（フォルダの一覧・追加・削除・配下の資料数・最終同期。**業務関連資料の固定文言をこの区画のすぐ隣へ移す**
  —— docs/screens 未決事項 1 の解消）と「競合」区画（一覧 → 選択で 2 ペイン差分 → 3 択ボタン → `ConfirmDialog`（取消側初期フォーカス。
  `local` / `both` は破壊的ではないが版が進む旨、`server` はローカル本文が失われる旨を本文に書く））。**自動解決の選択肢を置かない**。
  「対象フォルダから外す」と「削除する」を UI 上で明確に区別する（外す操作の確認文言に「削除ではありません」を書く）。
- 待ち・失敗・空・本体は `QueryState`（IADR-0437）。E2E は `sc19-*.smoke.spec.ts` / `sc20-*.smoke.spec.ts` に面を足す。

### 母集合（規則 1〜10）

- 「描かない」「契約に無い」の記述を `契約に無い|描かない|口が無い|planning#614|#1441|#1442` で追跡下の全ファイルから引く →
  `sc19-private-notes/{components/PrivateNotesPage.tsx,hooks/useNoteListView.ts}`、`sc20-obsidian-settings/components/ObsidianSettingsPage.tsx`、
  `docs/screens/SC-19_private-notes.md`（§実装状態・§計画との対応・§未決事項 1・2）、`docs/screens/SC-20_obsidian-settings.md`（同 §未決事項 1・2）、
  `docs/tests/SC-19_*.md` / `SC-20_*.md`、`.ai-context/specs/20260912_frontend-nocturne-and-experience-gaps.md` §B 表（point-in-time。追記のみ）。
  除外: `.ai-context/specs/20260828_issue-451c_sc19-sc20-screens.md`（凍結記録。当時の判断として正しい）、`CHANGELOG.md`（生成物）。
- 後段の口の全数（`Map(Get|Post|Put|Delete)(` を `Features/PrivateNotes` `Features/SyncDevices` `Features/ObsidianSync` で数える）は
  実装エージェントが仕様書の §検証記録へ書く（新設 5 口を足した後の表）。

## 受け入れ基準

- [x] `PrivateNoteDto` の 5 項目が契約・C#・生成物の三者で一致する（`check-openapi-dto-drift` / `check-contract-schema` OK）
- [x] 公開範囲: 共有 0 行 → `private`、`user` 行のみ → `users`、`group` 行あり → `groups`（後段テストで 3 状態を対で固定）
- [x] 同期状態: 未解決競合あり → `conflict`（削除済みでも）、有効端末あり＋フォルダ配下 → `target`、有効端末なし または フォルダ外 または 削除済み → `excluded`
- [x] タグ: 辞書 ID が表示名で返る。辞書に無い ID は落とす（既存 `TagResolver` の規則）
- [x] 同期設定: 未設定は `[]`。PUT の正規化・重複・上限・400 を後段テストで固定。対象から外しても資料は消えない（陽性対照つき）
- [x] 競合: push の 409 で行が起きる（同一資料・端末は上書き）。成功 push で `client` として閉じる。一覧は未解決のみ。詳細は両本文を返す。
  `local` で版が 1 つ進む・`server` で資料が不変・`both` で別名資料ができ元は不変。解決済みへの再解決は 409。他人の競合は 404
- [x] BFF: 5 口とも無認証 401・資格情報転送・他人は 404（陽性対照と対）・PUT / resolve は write スコープで 403（読み取りは通る）
- [x] SC-19: 3 列が出る。3 状態がそれぞれ色以外（文言）で区別される。絞り込み 3 軸が効く。指定先の表示・変更ダイアログを置いていないことを陽性対照つきで固定
- [x] SC-20: フォルダの追加・削除、配下の資料数・最終同期の表示、固定文言が区画の隣にあること、競合の一覧・2 ペイン差分・3 択・確認ダイアログ、自動解決の選択肢が無いこと、「外す」≠「削除」の区別
- [x] i18n: ja/en の差分なし・未訳 0。E2E: SC-19 / SC-20 の smoke に新区画の面を足す。a11y（axe）5 面 × 2 テーマ違反 0
- [x] `docs/screens` の「実装していない」節と `docs/api/BFF_bff-surface.md` を更新。planning の SC-19 / SC-20「供給元と契約の現状」表の更新を環流

## テスト方針

- 後段: `DocumentService/Tests/Features/PrivateNotes/*`（一覧の導出 3 状態 × 2）、`Features/SyncSettings/*`、`Features/SyncConflicts/*`、
  `ObsidianSyncProtocolTests`（409 で記録・成功で閉じる）。InMemory DB ＋ `RecordingObjectStorageClient`
- BFF: `Platform.Bff.Tests/BffPrivateNoteEndpointTests.cs` の Theory に 5 口を足す（401 / 転送 / 404 / 403 と陽性対照）
- 画面: `*.test.tsx`（列・状態・絞り込み・3 択・確認ダイアログ・陰性 3 件）、純関数 `types/*.test.ts`（状態の導出・差分の行分割）
- E2E: `sc19-private-notes.smoke.spec.ts` / `sc20-obsidian-settings.smoke.spec.ts`（役割で引く）

## 検証記録

### 後段の口の全数（`grep -rnE "Map(Get|Post|Put|Delete)\("`。実装後 23 口）

| 群 | 口 | BFF 公開 | 理由 |
| --- | --- | --- | --- |
| PrivateNotes | GET `/`、POST `/`、DELETE `/{id}`、POST `/{id}/restore`、POST `/purge`、PUT `/{id}/exposure`（6） | ○ 6 | SC-19 / SC-20 の画面が直接使う |
| PrivateNotes（quotas） | GET / PUT `/quotas/{ownerId}`（2） | ✕ | 載せる画面が計画に無い（管理者が他人の個人資料を扱う導線は禁止） |
| SyncDevices | GET `/`、POST `/`、POST `/{id}/reissue`、DELETE `/{id}`、POST `/revoke-all`（5） | ○ 5 | 同上 |
| ObsidianSync | GET `/manifest`、POST `/notes`、GET `/notes/{id}`、POST `/notes/{id}/delete`、POST `/notes/{id}/move`（5） | ✕ | 資格情報が別系統（同期トークン）。SPA は呼ばない |
| **SyncSettings（新）** | GET `/`、PUT `/`（2） | ○ 2 | SC-20 主要素 3 |
| **SyncConflicts（新）** | GET `/`、GET `/{id}`、POST `/{id}/resolve`（3） | ○ 3 | SC-20 主要素 5 |
| 合計 | 23 | 16 | 非公開 7（quotas 2 ＋ 同期プロトコル 5） |

### ゲート（2026-09-12・ローカル .NET 10.0.400 / Node 22）

- 契約: `check-openapi-dto-drift` OK（同名 74 件一致）/ `check-contract-schema` OK（破壊的 5 件＝`PrivateNoteDto` の必須メンバー追加を allowlist で承認 → baseline へ消費。IADR-0444 決定 1）/ `check-bff-authz-docs` OK（97 端点）/ `check-bff-downstreams` OK / orval 再生成差分なし
- 後段・BFF: `dotnet build` 両 slnx 0 error / `dotnet test --filter "Category!=Integration"`: knowledge 12 アセンブリ全緑（DocumentService.Tests **481 → 524、+43**）、platform 全緑（Platform.Bff.Tests **569 passed・+12**）/ `dotnet format --verify-no-changes` 両 slnx OK
- Migration: `20260912082007_AddSyncSettingsAndConflicts`（`dotnet ef migrations add` で生成。スナップショット差分は追加 74 行・削除 0）
- 画面: typecheck 6 パッケージ / lint 0 error / format / knip 36 / i18n 差分なし・未訳 0（新規 **71 文言 × 2**）/ route-manifest / build / static-egress /
  **test:coverage 1678 件（+58）98.30 / 93.13 / 95.06 / 98.30** / **E2E 64 passed（+4）** / chunk: 初期ロード 580,710 → **588,890 B**（+8,180 B。
  実装側の A/B でロケール分がほぼ全額。`--update` し `$comment_initialTotalBytes_20260912_1441-1442` に内訳）
- 文書: trace-blocks / cross-repo-refs / plan-id / knowledge-graph / doc-links / adr-numbering / reading-budget / backend-libraries / doc-type-vocabulary / test-traceability / test-spec-coverage OK

### 監査（2026-09-12・別エージェント・diff＋受け入れ基準のみ・証跡付き）

条件付き合格 → 推奨 1 件（「削除済み＋未解決競合 → `conflict`」の分岐がテストに無い）を `PrivateNoteListDerivationTests` に
1 ケース足して回収。他 9 観点（契約三者一致・409 不変・自動解決の不在・所有者を運ぶ口の不在・同期停止≠削除・色以外の区別・
trace 規約・過剰実装なし・IADR 整合）は合格。

### 実装で仕様と変えた点（IADR-0444 に反映済み）

- `syncState` の判定順は `conflict` → `target` → `excluded`（未解決競合があれば削除済みでも `conflict`。契約の description と本仕様書 §設計を揃えた）
- `PrivateNoteEndpoints.ToDto(n, doc)` を撤去し `PrivateNoteEnrichment` へ集約（材料を引かずに写せる口を残すと全資料が `private` / `excluded` に化けるため）
- `docs/api/BFF_bff-surface.md` には `/bff/private-notes*` の既存 11 行が本作業の前から無かった。新設 5 行だけを足し、欠落は日付つき追記で明記（埋めるのは別作業）
- 画面の単体テストは既存流儀（`apiRequest` のモック。IADR-0135 決定 4）に従い、MSW は使っていない

## 計画書との差異

- 差異: 公開範囲の**指定先**と**同期履歴**は本作業で描かない（planning#618 の裁定待ち。計画の「供給元と契約の現状」表に従う）
- 差異: `both`（両方を残す）の別名は `<元パス> (競合 yyyy-MM-dd HHmm).md` とする（計画は「別名保存」とだけ定める。名前の形は実装判断。IADR-0444）

## 未決事項

- planning#618（指定先の単位・同期履歴の N と保持期間）
- `both` の別名は分単位（`(競合 yyyy-MM-dd HHmm)`）のため、同一分に 2 回解決すると `PathConflictProblem`（409）になる。利用者は 1 分待てばよい（黙って壊れない）
- 未解決競合のローカル本文は解決時に消す。ストレージ削除を SaveChanges の前に置いた（fail-closed）ため、`both` でストレージ側が落ちると新資料の本文が孤児として 1 つ残り得る
- 競合のローカル本文の保持期限（解決済み競合の本文をいつ消すか）。本作業では解決時に削除する。未解決のまま残る本文の保持期限は planning#618 の
  同期履歴の保持期間と揃える前提で、裁定後に定める
