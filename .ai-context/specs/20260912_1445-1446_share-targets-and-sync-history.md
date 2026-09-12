---
title: SC-19 公開範囲の指定先（個人指定）と SC-20 同期履歴の契約を足し、画面へ描く（#1445 / #1446。planning#618 の裁定 ADR-0098 / ADR-0099）
type: spec
status: in-progress
related_ids: [FR-19, FR-20, UC-11, SC-19, SC-20, ADR-0036, ADR-0037, ADR-0096, ADR-0098, ADR-0099, IADR-0131, IADR-0139, IADR-0253, IADR-0270, IADR-0301, IADR-0401, IADR-0444, IADR-0445, IADR-0446]
author: Claude
created: 2026-09-12
updated: 2026-09-12
plan_refs:
  - planning:projects/microservices-platform/05_screens/01_screens.md
  - planning:projects/microservices-platform/07_adr/ADR-0098_share-target-is-keycloak-group-and-ui-waits-for-binding.md
  - planning:projects/microservices-platform/07_adr/ADR-0099_sync-history-is-the-audit-log-opened-to-the-owner.md
  - planning:projects/microservices-platform/10_feedback/20260912_share-target-unit-and-sync-history.md
---

# 仕様書: 公開範囲の指定先（個人指定）と同期履歴 —— planning#618 の裁定後の残作業

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: `FR-19`（個人資料）/ `FR-20`（Obsidian 連携）
- 非機能要件（NFR）: なし（製品機能の実装。個別番号を当てない）
- ユースケース（UC）: `UC-11`
- 画面（SC）: `SC-19`（主要素 3 公開範囲の変更ダイアログ）/ `SC-20`（主要素 6 同期履歴）
- 関連 ADR: `ADR-0098`（決定 1 共有先は Keycloak グループ・個人は利用者識別子・表示名を出し識別子は出さない／決定 2 グループ指定の
  UI は `${current_groups}` の配備まで描かない・台帳は受け付けたまま／決定 3 グループ木は管理者が Keycloak 側で作る）/
  `ADR-0099`（決定 1 供給元は既存の同期監査ログ・専用ストアを持たない／決定 2 本人のみ／決定 3 保持 3 年／決定 4 表示 50 件／
  決定 5 行の内容・タイトルとパスを含めない／決定 6 失敗も記録）/ `ADR-0036`（D-06・§未確定事項 5 は解消）/
  `ADR-0037`（決定 9 監査は「誰が・いつ・何件」）/ `ADR-0096`（完全削除。題名を残さない理由の 1 つ）/
  `IADR-0131`（openapi 手書き・状態文字列は enum にしない）/ `IADR-0139`（同型の契約追加はドメイン単位で 1 PR）/
  `IADR-0253`（共有台帳 段 4）/ `IADR-0270`（同期の中核）/ `IADR-0301`（`IIdentityAdminClient` の抽象）/
  `IADR-0401`（s2s の名簿は列挙を持たない）/ `IADR-0444`（前段。指定先と同期履歴を裁定待ちで切り離した）
- 計画書リンク: `05_screens/01_screens.md` §SC-19「供給元と契約の現状」・§SC-20 主要素 6、`10_feedback/20260912_share-target-unit-and-sync-history.md`

## 目的・背景

planning#618 の裁定（2026-09-12・ADR-0098 / ADR-0099）で、IADR-0444 が切り離した 2 件の前提が揃った。
本作業はその残りを契約に足し、画面へ描く（#1445 / #1446）。**2 issue を 1 PR に束ねる**（IADR-0139 決定 1。同じ資源
`private-notes` に対する同型の契約追加で上限 4 件以内。#1444 と同じ形）。

🔴 **実測で裁定の前提と違っていた点が 2 つある**（設計で吸収し、環流する）。

1. **同期の監査ログに貯蔵が無い。** `IAuditLogger.Record` は構造化ログ（`ILogger` → OTel）へ書くだけで、テーブルもエンティティも
   無い。ADR-0099 決定 1〜4（本人へ読ませる・3 年保持・50 件表示）は貯蔵が無いと成立しない。**監査ログに貯蔵を与える**形で実装し
   （`SyncAuditEntry`）、**専用の履歴ストアを別に作らない**ことで決定 1 に沿う（IADR-0446 決定 1）。
2. **一般利用者が他の利用者を表示名で検索する口が無い。** 表示名を出す口は AdminOnly の `/authz/users`（全件）だけで、s2s の
   `UserDirectory` gRPC は列挙を意図的に持たない（IADR-0401）。SC-19 主要素 3「指定先を検索して追加」には新しい読み口が要る。
   **面に出す項目を利用者名・表示名・有効状態の 3 つに閉じた**認証必須・ロール不問の口を足す（IADR-0445 決定 2）。

## 対象範囲

- 対象（契約・親が実施済み）: `docs/api/openapi.yaml`（`/bff/private-notes/{id}/shares` GET/POST・`/{subjectType}/{subjectId}` DELETE、
  `/bff/private-notes/sync-history` GET、`/bff/users/lookup` GET・`/bff/users/resolve` POST。スキーマ `DocumentShareDto` /
  `CreateShareRequest` / `SyncHistoryEntryDto` / `UserSummaryDto` / `ResolveUsersRequest`）、`Knowledge.Contracts/Dtos/PrivateNoteShareDto.cs`・
  `SyncHistoryDto.cs`、`Platform.Shared.Contracts/Dtos/UserLookupDto.cs`、orval 生成物、`scripts/contract-schema-baseline.json`
- 対象（後段 DocumentService）: `DocumentShareDto` / `CreateShareRequest` を契約型へ付け替え（内部型の削除）、`SyncAuditEntry` と
  Migration、`SyncAuditRecorder`（Push / Pull / Delete / Move の成功と失敗）、`GET /private-notes/sync-history`、定期処理 ⑦（3 年超の削除）
- 対象（後段 AuthorizationService）: `IIdentityAdminClient.SearchUsersAsync`（Keycloak `search=`・InMemory）、
  `Features/Users/Lookup/`（`GET /authz/users/lookup`・`POST /authz/users/resolve`。認証必須・ロール不問）
- 対象（BFF）: Knowledge BFF `PrivateNoteBffEndpoints` に共有 3 口（POST / DELETE は write ゲート）と同期履歴 1 口（透過中継）、
  Platform BFF `Foundation/Endpoints/UserLookupBffEndpoints.cs` に `/bff/users/lookup`・`/bff/users/resolve`（利用者の `Authorization` を転送）
- 対象（画面）: SC-19 の行操作「共有先を変更する」とダイアログ（現在の指定先＝表示名／検索して追加／取り消し）、SC-20 の
  「同期履歴」区画、単体テスト・E2E・i18n（ja/en）、`docs/screens/SC-19_*.md` / `SC-20_*.md`・`docs/tests/SC-19_*` / `SC-20_*`・
  `docs/api/BFF_bff-surface.md`
- 対象外:
  - 🔴 **グループ指定の UI**（ADR-0098 決定 2）。台帳・契約は `group` を受け付けたままとし、拒否する改修も行わない
  - `${current_groups}` の束縛・共有先ベースの分岐の認可スコープへの配線・`BffScopeResolver.MatchesAll` の集合値突合
    （ADR-0098 フォローアップ 1・2。別 issue で追う）
  - 同期の監査ログの第三者閲覧（ADR-0099 決定 2 で未確定）
  - 資料 ID の記録（SC-20 §未確定のまま）
  - 「さらに読み込む」の追加取得（ADR-0099 決定 4 は実装設計に委ねたが、本作業は 50 件固定。要望が出てから）

## 設計

### 契約（`docs/api/openapi.yaml`。手書きが正本。IADR-0131）

| 口 | 認可 | 後段 | 備考 |
| --- | --- | --- | --- |
| `GET /bff/private-notes/{id}/shares` | 認証のみ | DocumentService `GET /documents/{id}/shares` | 所有者のみ・他人は 404。付与順 |
| `POST /bff/private-notes/{id}/shares` | write ゲート | `POST /documents/{id}/shares` | 201 / 400 / 404 / 409。**画面は `user` しか送らない** |
| `DELETE /bff/private-notes/{id}/shares/{subjectType}/{subjectId}` | write ゲート | `DELETE /documents/{id}/shares/{t}/{s}` | 204 / 404 |
| `GET /bff/private-notes/sync-history` | 認証のみ | `GET /private-notes/sync-history?limit=50` | 本人・新しい順・50 件 |
| `GET /bff/users/lookup?q=&limit=` | 認証のみ | AuthorizationService `GET /authz/users/lookup` | `q` 2 文字以上・有効な利用者のみ・上限 50（既定 20） |
| `POST /bff/users/resolve` | 認証のみ | `POST /authz/users/resolve` | 1〜100 件。居ない名前は落ちる。無効化済みも返す |

- `DocumentShareDto(subjectType, subjectId, grantedBy, createdAt)` / `CreateShareRequest(subjectType, subjectId)` は後段の内部型を
  **名前・項目を変えずに契約へ移す**（透過中継の前提。後段はこの型を使う）。値集合 `ShareSubjectTypes`。
- `SyncHistoryEntryDto(id, occurredAt, deviceName, direction, added, updated, deleted, conflicted, outcome, failureReason?)`。
  値集合 `SyncDirections` / `SyncOutcomes` / `SyncFailureReasons`（7 値）。**失敗理由の文言は画面が持つ**（記録はコードだけ）。
- `UserSummaryDto(username, displayName, enabled)` / `ResolveUsersRequest(usernames)`。ロール・属性・内部 ID を持たない。

### 後段 DocumentService

- **共有台帳**: 口の実装は変えない（IADR-0253 段 4 のまま）。`DocumentShareDto` / `CreateShareRequest` の定義を
  `Knowledge.Contracts.Dtos` へ移し、`Features/Documents/DocumentShareEndpoints.cs`・`GrantShare/Command.cs` の内部定義を消す。
- **同期監査台帳 `SyncAuditEntry`**（`Domain/`）: `Id` / `OwnerId`（200）/ `DeviceId` / `DeviceName`（200・記録時点の写し）/
  `OccurredAt` / `Direction`（20）/ `Added` / `Updated` / `Deleted` / `Conflicted` / `Outcome`（20）/ `FailureReason`（40・null 可）。
  索引 `(OwnerId, OccurredAt desc)`。🔴 **`DocumentId`・タイトル・`VaultPath` の列を持たない**（型で ADR-0099 決定 5 を守る）。
  FK は張らない（端末・資料が消えても行は残る＝監査ログ）。
- **`SyncAuditRecorder`**（`Features/ObsidianSync/`）: `Success(db, audit, owner, device, direction, added/updated/deleted, now)` は
  行を Add し（**呼び出し元の SaveChanges と同じトランザクション**）、`Failure(...)` は Add ＋ `SaveChangesAsync`（失敗経路は
  他に保存するものが無い。🔴 **呼ぶのは変更を加える前の早期 return だけ**という不変条件を注記する）。どちらも既存の
  `audit.Record(...)`（`ILogger`）を **方向・内訳・理由を足した detail で**続けて出す（書き込みは 1 つ。ログは同じ書き込みの計器）。
  - Push 新規成功 `added=1` / 更新成功 `updated=1` / 409 `version_conflict` は `conflicted=1`・`failure`（`SyncConflictRecorder.RecordAsync`
    の直後。**競合の記録と同期履歴の失敗は別の行**であり、前者は `SyncConflict`・後者は `SyncAuditEntry`）/ 409 削除済み `deleted` /
    409 パス衝突 `path_conflict` / 507 `quota_exceeded` / 413 `body_too_large` / 400 `invalid_request` / 404 `not_found`
  - Move 成功 `updated=1` / 409 `version_conflict`（競合台帳には入れない。従前どおり）/ 409 削除済み / 409 パス衝突 / 404 / 400
  - Pull 成功 `direction=pull`・`updated=1`（端末が受け取った 1 件。追加か更新かはサーバが知らない）/ 404
  - Delete 成功 `deleted=1` / 404
  - 🔴 **401（端末が解決できない）は記録しない** —— 所有者が決まらない。Manifest も従前どおり記録しない（一覧取得は同期ではない）。
- **`GET /private-notes/sync-history?limit=`**（`Features/SyncHistory/List/`）: `SubjectOf` の本人の行を `OccurredAt desc` で
  `limit`（1〜200・既定 50）件。JWT 認証（他の `/private-notes/*` と同じ群）。
- **定期処理 ⑦** `PurgeSyncAuditAsync(now)`: `OccurredAt < now.AddYears(-3)` を削除（`SyncAuditEntry.RetentionYears = 3`）。
  順序は既存 ⑥ の後ろ。監査に「いつ・何件」を 1 行。
- Migration: `dotnet ef migrations add AddSyncAuditEntries`（手書きしない）。

### 後段 AuthorizationService

- `IIdentityAdminClient.SearchUsersAsync(string query, int max, CancellationToken)`: Keycloak は
  `GET admin/realms/{realm}/users?search={q}&enabled=true&briefRepresentation=true&max={max}`（Keycloak の `search` は
  username / email / first / last の部分一致）。InMemory は `Username` / `DisplayName` の `OrdinalIgnoreCase` 部分一致。
  🔴 `IdentityAdminContractTests` の禁止語（作成）に触れない読み取りである。
- `Features/Users/Lookup/`: 群 `/authz/users/lookup`・`/authz/users/resolve` は **`RequireAuthorization()`（ロール不問）**。
  既存の `/authz/users` 群（AdminOnly）とは**別の `MapGroup`** にする（同じ群に足すと AdminOnly が掛かる）。
  - lookup: `q` を trim・2 文字未満は 400（`UserAdminEndpoints.ValidationProblem` と同じ形）・`limit` 1〜50（既定 20）・
    `enabled` のみ・`DisplayName` 順・`UserSummaryDto` へ写す。
  - resolve: `usernames` 1〜100（空・超過は 400）・`FindByUsernameAsync` を並列に引き、見つかったものだけ返す（順序は要求順）。
    無効化済みも返す（既存の共有先を表示するため）。

### BFF

- Knowledge BFF: `notes.MapGet("/{id:guid}/shares")` → `ForwardAsync(Get, $"/documents/{id}/shares")`、`MapPost` → `ForwardIfWritableAsync`、
  `MapDelete("/{id:guid}/shares/{subjectType}/{subjectId}")` → `ForwardIfWritableAsync`（`Uri.EscapeDataString`）。
  `notes.MapGet("/sync-history")` → `ForwardAsync(Get, "/private-notes/sync-history?limit=50")`。`WithName` は
  `BffPrivateNoteShareList` / `BffPrivateNoteShareGrant` / `BffPrivateNoteShareRevoke` / `BffSyncHistoryList`。
  🔴 `/{id:guid}/shares` は `/{id:guid}/exposure`・`/{id:guid}/restore` と同じ深さで衝突しない。`/sync-history` は literal 1 段。
- Platform BFF: `Foundation/Endpoints/UserLookupBffEndpoints.cs`。群 `/bff/users` `RequireAuthorization()`（ロール不問）。
  `UserAdminBffEndpoints.Proxy` と同じ透過中継（`AuthorizationService` named client・`Authorization` 転送・502 縮退）。
  `Program.cs` で `MapUserLookupBffEndpoints()`。`check-bff-authz-docs` の `x-roles: []` と一致。

### 画面

- SC-19（`sc19-private-notes/`）:
  - `api/usePrivateNoteShares.ts`: `useBffPrivateNoteShareList`（`enabled` は開いているときだけ）・`useBffPrivateNoteShareGrant`・
    `useBffPrivateNoteShareRevoke`（成功後は shares と一覧〔件数・3 状態が変わる〕を invalidate）。
  - `api/useUserLookup.ts`（または foundation 側）: `useBffUserLookup`（`q` 2 文字以上・デバウンス 300ms・`enabled`）・`useBffUserResolve`。
  - `components/ShareTargetsDialog.tsx`: `@platform/ui` の `Dialog`（`initialFocus` は検索入力）。上段「現在の指定先」＝
    `shares.filter(user)` を `resolve` で表示名へ（居ない名前は「（不明な利用者）」・`enabled=false` は「（無効化済み）」を併記）
    ＋各行「取り消す」。下段「利用者を追加」＝検索入力（`Input`）＋候補（`role="listbox"` / `option`。表示名だけ。自分自身と
    既に共有済みの利用者は除く）＋「追加」。🔴 **`subjectType=user` 固定。グループの導線・文言を置かない**（陽性対照:
    「グループ」というテキストが無い／`option` の値集合に group が無い）。🔴 **`subjectId`（利用者名）を表示しない**。
    グループ共有が台帳にある行（`sharedGroupCount>0`）は「グループへの共有 N 件は本画面で変更できません」の `Note` を出す（消さない）。
  - `PrivateNotesPage.tsx`: active タブの行操作に「共有先を変更する」（`secondary`）。`confirming` と同じ形の state で開閉。
- SC-20（`sc20-obsidian-settings/`）:
  - `api/useSyncHistory.ts`: `useBffSyncHistoryList`。
  - `components/SyncHistoryPanel.tsx`: `Panel heading="同期履歴"` → `QueryState`（空: 「同期の記録はまだありません」）→ `Table`
    （実行日時 / 端末 / 方向〔送信・受信〕/ 内訳〔追加 n・更新 n・削除 n・競合 n。0 は出さない〕/ 結果〔`StatusBadge`。
    成功 success・失敗 warning〕/ 失敗理由〔`types/syncHistory.ts` の `failureReasonText(code)` で「次に何をすればよいか」の
    文言へ。未知のコードは「同期に失敗しました（理由: `<code>`）」〕）。末尾に `Note`「直近 50 件を表示します。記録は 3 年間保持されます」。
    🔴 **タイトル・パスの列を持たない**（DTO に無い。テストで固定）。
  - `ObsidianSettingsPage.tsx`: `SyncConflictsPanel` の下に `SyncHistoryPanel`。「同期履歴は描かない」の注記を消す。
- E2E: `sc19-*.smoke.spec.ts` にダイアログ（開く → 候補 → 追加 → 取り消し）、`sc20-*` に履歴の行。`bffSession` に 6 口のモック。
  `a11y` / `keyboard-navigation` は既存の面のまま（ダイアログは Base UI の閉じ込めを継承）。
- i18n: `msg` / `Trans` → `pnpm run i18n` → en は英訳。

### 母集合（規則 1〜10）

- 「指定先は裁定待ち」「同期履歴は描かない／載せない」の記述を `planning#618` / `未確定事項 5` / `同期履歴` / `指定先` で
  追跡下の全ファイルから引く（`git grep`）。是正対象は**生きた文書とコード注記**（`openapi.yaml`・`PrivateNoteDto.cs`・
  `PrivateNoteBffEndpoints.cs`・`ObsidianSettingsPage.tsx`・`PrivateNotesPage.tsx`・`docs/screens/*`・`docs/tests/*`・
  `BFF_bff-surface.md`・`.ai-context/adr/README.md` の IADR-0444 行）。除外: `.ai-context/specs/20260912_1441-1442_*`・
  `IADR-0444` 本文（point-in-time の記録。**追記のみ**〔`［2026-09-12 追記 / #1445・#1446］`〕）、`CHANGELOG.md`（生成物）。
  実測は §検証記録 に書く。

## 受け入れ基準

- [ ] SC-19: 所有者が個人資料の共有先（利用者）を一覧・検索・追加・取り消しできる。画面に表示名だけが出て利用者名は出ない
- [ ] SC-19: グループ指定の導線が無い（陽性対照つき）。台帳にグループ共有があっても消えない（`Note` で告知）
- [ ] 共有先の検索は 2 文字以上・有効な利用者のみ・上限 50。resolve は無効化済みも返す。どちらも一般利用者で 200
- [ ] SC-20: 同期履歴が新しい順に 50 件、各行に 実行日時 / 端末名 / 方向 / 内訳 / 結果 / 失敗理由（文言）。タイトル・パスは無い
- [ ] Push / Move / Pull / Delete の成功と失敗（7 理由）が `SyncAuditEntry` に残る。401 は残らない。409 応答の本文・状態は不変
- [ ] 3 年超の行が定期処理で消える（時計を進めたテスト）
- [ ] 後段の口: `/authz/users/lookup`・`/resolve` は認証のみで通り、AdminOnly の `/authz/users` の認可は変わらない（陽性対照）
- [ ] ゲート: 契約 4 検査・両 slnx build/test/format・typecheck/lint/format/knip/i18n/route-manifest/build/chunk/static-egress/
  coverage/E2E・文書検査

## テスト方針

- 後段: `SyncAuditRecorder` の記録（成功 4 経路・失敗 7 理由・401 なし）は `ObsidianSyncProtocolTests` と同じ `TestWebApplicationFactory`
  で。`GET /private-notes/sync-history` の本人絞り・順序・limit。定期処理 ⑦ は `PrivateNoteMaintenanceService` の既存テストと同じ形。
  AuthorizationService は `UserAdminEndpointTests` と同じ形で lookup / resolve（一般利用者トークンで 200・`q` 1 文字で 400・
  無効化済みは lookup に出ず resolve には出る）。BFF は `BffPrivateNoteEndpointTests` の形で 4 口、Platform.Bff で 2 口。
- 画面: `PrivateNotesPage.test.tsx` にダイアログ（`apiRequest` モック）、`ShareTargetsDialog.test.tsx`、`SyncHistoryPanel.test.tsx`、
  `types/syncHistory.test.ts`（文言写像・未知コード）。陽性対照: グループ文言／タイトル列。

## 検証記録

### 後段の口の全数（`grep -rnE "Map(Get|Post|Put|Delete)\("`。実装後）

- DocumentService の個人資料・同期系（`PrivateNotes` / `SyncHistory` / `SyncConflicts` / `SyncSettings` / `SyncDevices` /
  `ObsidianSync` / `Documents/{ListShares,GrantShare,RevokeShare}`）: **30 口**（#1444 時点の 23 ＋ 共有台帳 3〔従前から在り、
  母集合に入れていなかった〕＋ 同期履歴 1 ＋ 前段の数え方の差 3）。BFF 公開: `PrivateNoteBffEndpoints` **20 口**（#1444 の 16 ＋
  共有 3 ＋ 同期履歴 1）。非公開のまま: `/private-notes/quotas/*` 2・同期プロトコル 5（理由は IADR-0444 のとおり）。
- AuthorizationService `Features/Users/Lookup`: **2 口**（lookup / resolve）。Platform BFF `UserLookupBffEndpoints`: **2 口**。

### 母集合（規則 1〜10 の実測）

`git grep -nE "裁定待ち|planning#618|描かない|載せない"`（除外: `.ai-context/specs/`・`CHANGELOG.md`・IADR-0444/0445/0446 本文・
別紙 `plan-id-range-history-annex.md`〔いずれも point-in-time の記録〕）で、指定先・同期履歴に関する行を全件読んだ。
是正した陳腐化: `PrivateNoteDto.cs`（2 か所）・`useNoteListView.ts`（1 か所）・`PrivateNoteBffEndpoints.cs` 冒頭注記・
`openapi.yaml`（3 か所）・`ObsidianSettingsPage.tsx` / `PrivateNotesPage.tsx` 冒頭注記・`docs/screens`・`docs/tests`・
`BFF_bff-surface.md`・`.ai-context/adr/README.md` の IADR-0444 行は「裁定待ちで契約に出さない」を当時の記述として残置
（索引の要旨は決定の要約であり、追記は IADR-0444 本文の日付つき追記ブロックで行った）。残るヒットは ADR-0098 / ADR-0099 を
引く新しい記述だけである。

### ゲート（2026-09-12・ローカル .NET 10.0.400 / Node 22）

| 面 | 結果 |
| --- | --- |
| 契約 | `check-openapi-dto-drift` OK（同名 79 件一致）/ `check-contract-schema` OK（136 型・非破壊の型追加のみ・未消化 0）/ `check-bff-authz-docs` OK（19 ファイル / 103 端点。前 97）/ `check-bff-downstreams` OK / orval 再生成差分なし |
| 後段・BFF | `dotnet build` 両 slnx 0 warning / 0 error。`dotnet format --verify-no-changes` OK。テスト件数は §テスト件数 |
| 画面 | typecheck 6 パッケージ Done / lint 0 error（既存 warning 12）/ format OK / knip 床どおり 36 / i18n 差分なし・未訳 0（新規 **44 msgid × 2**）/ build OK / chunk **594,006 B**（床 588,890 → 594,006 へ `--update`。A/B: カタログ +5,108 B・コード +8 B。`ui` / `vendor-react` / `vendor-query` はハッシュまで同一）/ static-egress OK / route-manifest OK（17 画面・83 件）/ **E2E 67 passed**（前 64。sc19 +1・sc20 +1・a11y +1） |
| 文書 | trace-blocks（174 件）/ doc-links / cross-repo-refs（3,638 件）/ plan-id（3,025 件）/ knowledge-graph / adr-numbering / reading-budget（3 集合とも 51,200 B 内）OK |

### テスト件数（前 → 後）

| アセンブリ | 前 | 後 |
| --- | --- | --- |
| DocumentService.Tests | 524 | 546（+22: recorder 14 / history 6 / retention 2） |
| AuthorizationService.Tests | 280 | 293（+13） |
| Platform.Bff.Tests | 570 | 590（+20） |
| frontend（vitest） | 1,678 | 1,708（+30） |

（backend の全アセンブリ再走と coverage の 4 値は本節の末尾に追記する。）

## 計画書との差異

- **ADR-0099 決定 1「既存の同期監査ログ」に貯蔵が無い** → 監査ログに貯蔵を与える（`SyncAuditEntry`）。専用の履歴ストアは
  別に作らない。**planning へ環流する**（ADR-0099 §実装の現状 の追記候補）。
- **利用者を表示名で検索する口が無い** → 認証必須・ロール不問の読み口を足す（面は 3 項目に閉じる）。ADR-0098 決定 1 の
  「画面には表示名を出す」に含意されているが、計画は読み口の到達範囲（誰が誰の表示名を引けるか）を定めていない。環流の付記に含める。

## 未決事項

- 未解決競合のローカル本文の保持期限（IADR-0444 の残余リスク）。ADR-0099 は同期履歴の保持（3 年）を定めたが競合の本文は
  監査ログではないため、揃える根拠が無い。**据え置き**。
