---
title: "共有先の写し（`DocumentDto.sharedWith`）を所有者にだけ返す（#1451。ADR-0098 フォローアップ 5 の裁定 planning#626）"
type: spec
status: done
related_ids: [FR-19, UC-11, SC-03, SC-19, ADR-0036, ADR-0098, IADR-0253, IADR-0396, IADR-0447, IADR-0448, IADR-0450]
author: Claude
created: 2026-09-13
updated: 2026-09-13
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0098_share-target-is-keycloak-group-and-ui-waits-for-binding.md
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
---

# 仕様書: 共有先の写しを所有者にだけ返す —— ADR-0098 フォローアップ 5 の裁定の実装

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: `FR-19`（個人資料の共有）
- 非機能要件（NFR）: なし（製品機能の実装。個別番号を当てない）
- ユースケース（UC）: `UC-11`
- 画面（SC）: `SC-03`（文書詳細。`GET /bff/documents/{id}` の消費面）／`SC-19`（所有者向けの共有先一覧は別の口＝影響なし）
- 関連 ADR: `ADR-0098`（§結果 フォローアップ 5。裁定 planning#626「所有者だけに返す」）/ `ADR-0036`（D-05 所有者ベース・D-06 共有先ベース）
- 計画書リンク: `planning:projects/microservices-platform/07_adr/ADR-0098_share-target-is-keycloak-group-and-ui-waits-for-binding.md`

## 目的・背景

IADR-0447 決定 4 で応答に載せた共有先の写し（`sharedWith`）が、共有先ベースの分岐で読めた相手（所有者ではない）にも届き、他の共有先の識別子（利用者名・グループ ID）を読める。ADR-0098 フォローアップ 5 の裁定（planning#626・2026-09-13）は「所有者だけに返す」。BFF が利用者へ返す直前で所有者以外の項目を落とす。

## 実装の現状（着手前の実測）

- 利用者へ `DocumentDto` を返す口: `GET /bff/documents/{id}`（`FetchAuthorizedAsync` → `Results.Ok(doc)`）・`GET /bff/documents`（`IsManageable` で絞った一覧）。`/content`・`/versions` は別 DTO。作成・更新は `RelayAsync` の透過（管理者限定・組織文書。作成直後に共有は無い）。
- 検索の `SearchResultDto.Attributes` は `c.Attributes` の写しで、`shared_with` は判定用ペイロードの別項目（`QdrantVectorStore` 510 行・`InMemoryVectorStore` 203 行）。グラフはノード属性を利用者へ返す口を持たない（`Features/` に該当 DTO 無し）。
- SPA の `sharedWith` 参照: 生成物（`bff.schemas.ts`・`documents.faker.ts`）以外 0 件。
- BFF は `${current_user}` の値を持たない（`BffScopeResolver` は `Identity.Name` を `/authz/scope` の `userId` として送るだけ）。分岐の中に束縛済みの値として届く。
- `DocumentDto` は `class`（init-only）。`with` 式が使えない。

## 設計（決定。詳細は IADR-0450）

1. `DocumentBffEndpoints.ForViewer(doc, scope)`: `doc.SharedWith is null || GrantedAsOwner(doc, scope) ? doc : doc with { SharedWith = null }`。詳細は `FetchAuthorizedAsync` の戻り、一覧は `IsManageable` の後で通す（**返す直前の 1 点**。判定の像 `AuthzView` は不変）。
2. `GrantedAsOwner`: 許可した分岐（`Branches`、無ければ `Filters` を 1 分岐）のうち `owner` を条件に持つ分岐が `AttributeFilterMatch.MatchesAll` で一致したか。`PrivateNoteVisibility.OwnerKey` を再利用し、利用者名の比較を BFF に持ち込まない。
3. 所有者以外には**項目ごと落とす（`null`）**。空集合にしない（「共有の有無」も見せない）。
4. `DocumentDto` を `record` にする（`with` 式のため。JSON・既定値は不変）。
5. 文書: `openapi.yaml` の `DocumentDto.sharedWith` 注記、`docs/api/BFF_bff-surface.md` の詳細行、`docs/authz/FR-19_share-target-authorization.md`（§判定 に追記・§未決事項 から削除）、IADR-0447 §結果 残余リスク／フォローアップ 2 に日付つき追記。orval 生成物を再生成。

## 影響範囲（専有領域）

- `src/knowledge/backend/Shared/Knowledge.Contracts/Dtos/DocumentDto.cs`
- `src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/DocumentBffEndpoints.cs`
- `src/platform/backend/Bff/Platform.Bff.Tests/BffSharedDocumentReadTests.cs`
- `docs/api/openapi.yaml`・`src/platform/frontend/src/lib/api/generated/bff.schemas.ts`（再生成）
- `docs/api/BFF_bff-surface.md`・`docs/authz/FR-19_share-target-authorization.md`
- `.ai-context/adr/IADR-0447_*.md`（追記）・`IADR-0450`（新規）・`README.md`（索引）

### 母集合（規則 1〜10）

走査語: `sharedWith` / `SharedWith` / `shared_with` / `他の共有先` / `誰と共有` を、パスの除外なし（`git grep -n`、追跡下の全ファイル）で引いた。

| 出た箇所 | 扱い |
| --- | --- |
| BFF `DocumentBffEndpoints.cs`（像の合成・判定） | **対象**（返す直前の 1 点を足す） |
| DocumentService（`DocumentEndpoints` / `DocumentMapper` / `DocumentReadUseCase` / `DocumentShareEndpoints`） | 対象外。写しを載せる側は変えない（索引・イベントは判定に要る） |
| Retrieval（`IVectorStore` / `QdrantVectorStore` / `InMemoryVectorStore`）・Ingestion `DocumentUpdatedConsumer`・Graph `GraphDocumentSyncConsumer` / `AbacNodeFilter` | 対象外。判定用の内部項目で、利用者への応答に出ない（実測。上記 §実装の現状） |
| 契約 `DocumentDto` / `AttributeValueDto` / `PrivateNoteVisibility` / `DocumentAttributeEncoding` / `DocumentUpdated` | `DocumentDto` のみ対象（`record` 化）。他は語彙・述語で不変 |
| `docs/api/openapi.yaml`・`docs/api/BFF_bff-surface.md`・`docs/authz/FR-19_*.md` | **対象**（注記の追随） |
| IADR-0447（§結果 残余リスク・フォローアップ 2） | **対象**（日付つき追記） |
| 作業仕様書 `20260912_1447-1448_*.md` §監査記録（環流の記述） | 対象外。point-in-time の記録。凍結 |
| SPA 生成物 `bff.schemas.ts` / `documents.faker.ts` | 再生成で追随（手で触らない） |
| planning 側（ADR-0098 フォローアップ 5・IADR-0447 を引く箇所） | 本リポ外。planning#626 の裁定記録と ADR-0101（起案中）で追随 |

## 受け入れ基準（テストへの写像）

1. `owner` 分岐で読んだ所有者には `sharedWith` がそのまま返る（陽性）。
2. 共有先ベースの分岐だけで読めた相手には 200 のまま `sharedWith` が `null`（他の項目は不変）。
3. 所有者分岐と共有先分岐の両方を持つスコープでは、所有者分岐が一致した閲覧者にだけ返る（Theory）。
4. 空集合を運ぶ応答でも、所有者以外には `null`（「共有の有無」を見せない）。
5. SC-05 の一覧も同じ 1 点を通る（所有者以外には `null`）。
6. 既存の判定テスト（読める／404／一覧に個人資料が現れない）は不変で緑。
7. 契約: `check-openapi-dto-drift` / `check-contract-schema` 緑、orval 生成物の差分は注記の JSDoc だけ。
8. 既存ゲート全緑（backend 両 slnx build / test / format、frontend codegen 差分なし、文書検査）。

## 計画書との差異・未決事項

- 所有者向けの `sharedWith` は `GET /bff/documents/{id}` で返り続けるが、SPA は使っていない（所有者の共有先一覧は SC-19 の `DocumentShareDto` の口）。落とさないのは裁定の字義（「所有者だけに返す」）に従うため。
- 計画側の記録は ADR-0101（ADR-0098 の補完）として起案する。本リポの計画 ADR レンジ（`traceability.repo.md`）は planning のマージ後に `0001..0101` へ引き直す（別 PR）。

## 検証記録

実測 2026-09-13（ローカル .NET 10 / Node 22）。

| 面 | 結果 |
| --- | --- |
| 後段 | `dotnet build` knowledge 0 error（warning 2 は既存の Testcontainers CS0618）/ platform 0 warning 0 error。`dotnet test --filter "Category!=Integration"`: Platform.Bff.Tests 640 件（`BffSharedDocumentReadTests` 24 件＝既存 18 ＋ 新規 6）/ knowledge slnx 12 アセンブリ全緑（DocumentService 551・Graph 631・Retrieval 265・Wiki 111・Knowledge.Contracts 89 ほか）。`dotnet format --verify-no-changes` 両 slnx 差分なし |
| 契約 | `check-openapi-dto-drift` OK（同名 81 件一致）/ `check-contract-schema`: `typeKindChanged:Knowledge.Contracts.Dtos.DocumentDto`（class → record）を allowlist で承認 → `--update` で baseline へ消化（未消化 0）。orval 再生成の差分は `bff.schemas.ts` の JSDoc 4 行のみ。`@platform/frontend` typecheck OK |
| 文書・scripts | trace-blocks / cross-repo-refs（初稿の `planning#626 / #1451` を列挙形として検出 → 「実装は #1451」へ書き換え）/ plan-id-qualification / adr-numbering / doc-links / doc-type-vocabulary / doc-status-vocabulary / knowledge-graph --check / reading-budget すべて OK。`scripts.test.js` 771 件緑 |

受け入れ基準: 1〜6 は `BffSharedDocumentReadTests` の新規 5 テスト（Theory 含め 6 ケース）と既存テストで緑、7・8 は上表のとおり。

## 監査記録

2026-09-13・別エージェント（sonnet）が diff（`origin/develop...741e7e9`・10 ファイル）と §受け入れ基準 だけを渡されて監査。**合格**（🔴🟡🟢 いずれも指摘なし）。証跡: `git rev-parse --is-shallow-repository`＝`true` を確認したうえで diff / grep / テスト実行のみで判定。`dotnet test Platform.Bff.Tests --filter BffSharedDocumentReadTests` 24 件緑、両 slnx の build / test / format 緑、`check-contract-schema` / `check-openapi-dto-drift` / `check-trace-blocks` / `check-cross-repo-refs` OK。

| 観点 | 判定 |
| --- | --- |
| 漏れ経路の全数（`grep -rn "SharedWith" src --include=*.cs`） | 利用者へ `DocumentDto` が返る口は詳細・一覧の 2 つで、両方が `ForViewer` を通る。`/content`・`/versions` の DTO は `SharedWith` を持たない。`RelayAsync` は後段の応答を透過するだけで、`FetchAuthorizedAsync` の戻りは null 判定にしか使わない |
| 判定の不変 | `IsReadable` / `IsManageable` の本体に diff 行なし |
| 陰性対照 | 「共有された相手には返さない」「空集合も落とす」「SC-05 一覧」の 3 件 |
| `class → record` | 等価性・キーとしての利用 0 件。契約検査 2 種とも緑 |
| 文書の整合・trace ブロック規約 | 実装と一致。`docs/*.md` の表示テキストに ID 無し |
