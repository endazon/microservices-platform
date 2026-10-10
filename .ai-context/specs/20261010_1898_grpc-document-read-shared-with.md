---
title: 作業仕様書 — 文書の読み取りの gRPC 応答に shared_with を運ばせる（#1898）
type: spec
status: done
related_ids: [FR-06, FR-19, UC-03, SC-03, SC-05, NFR-09, ADR-0029, ADR-0036, ADR-0075, ADR-0098, ADR-0119, IADR-0379, IADR-0402, IADR-0447, IADR-0450]
author: claude
created: 2026-10-10
updated: 2026-10-10
issue: "#1898"
---

# 作業仕様書 — 文書の読み取りの gRPC 応答に shared_with を運ばせる（#1898）

> 本仕様書は実装着手前に作成した（着手 2026-10-10）。基点は MSP `origin/develop` `d486cd2f`。
> **IADR は作らない**（判断は既存の IADR-0379 決定 2〔フィールド番号は不変・追加は非破壊〕と IADR-0447 / IADR-0450〔`SharedWith` の契約〕の範囲に収まる）。

## 起点（トレーサビリティ）

- 起点 issue: **#1898**（`fix(FR-06)`）。関連: #1897（同じ経路の `ListDocuments` の受信上限。**本 PR では触らない**）。
- 計画: FR-06（文書の読み取り）・FR-19（共有先）・UC-03・SC-03・SC-05、ADR-0029 / ADR-0075（east-west gRPC）、
  ADR-0036 D-06・ADR-0098 決定 1・フォローアップ 5（共有先の写しと、所有者にだけ返すこと）、ADR-0119 決定 3（個人資料は所有者と共有先にだけ返す）。
- 前提 IADR: IADR-0379（proto の置き場・versioning）、IADR-0402（読み取り 4 口の gRPC 化）、IADR-0447（共有先の唯一の解決点）、IADR-0450（所有者にだけ返す）。

## 事象（コードで確かめた）

- `document_read.proto` の `DocumentSummary` に `shared_with` が**無い**（フィールドは 1〜11）。
- `DocumentReadGrpcMapping.ToProto(DocumentDto)` / `ToDto(DocumentSummary)` が `SharedWith` を写していない。
- 文書サービスは REST・gRPC とも同じ `DocumentReadUseCase` → `DocumentEndpoints.ToDto` で `SharedWith` を埋めている。**落ちるのは写像の 1 点だけ**。
- BFF は `DocumentReadGrpcClient` が `ToDto` で受け、`DocumentBffEndpoints.AuthzView`（`WithSharedWith`）と `ForViewer` が `SharedWith` を読む。
  gRPC 経路では常に null になり、(a) 共有先ベースの分岐に一致せず共有された相手が SC-03 で 404、(b) 所有者への応答から `sharedWith` が消える。

## フィールドの突き合わせ（REST DTO ↔ proto）

| DTO（`DocumentDto`） | proto（`DocumentSummary`） | `ToProto` | `ToDto` | 判定 |
| --- | --- | --- | --- | --- |
| `Id` | `id = 1` | 有 | 有 | 一致 |
| `Title` | `title = 2` | 有 | 有 | 一致 |
| `Status` | `status = 3` | 有 | 有 | 一致 |
| `MarkdownUri` | `optional markdown_uri = 4` | 有（presence） | 有 | 一致 |
| `Version` | `version = 5` | 有 | 有 | 一致 |
| `Attributes` | `map attributes = 6` | 有 | 有 | 一致 |
| `Tags` | `repeated tags = 7` | 有 | 有 | 一致 |
| `CreatedAt` | `created_at = 8` | 有 | 有 | 一致 |
| `UpdatedAt` | `updated_at = 9` | 有 | 有 | 一致 |
| `HasBody` | `has_body = 10` | 有（明示代入） | 有 | 一致 |
| **`SharedWith`** | **無し** | **無し** | **無し** | **落ちている（本件）→ `repeated string shared_with = 12` を足す** |
| `ContentFingerprint` | `optional content_fingerprint = 11` | 有（presence） | 有 | 一致 |

| DTO（`DocumentVersionDto`） | proto（`DocumentVersionSnapshot`） | 判定 |
| --- | --- | --- |
| `DocumentId` / `Version` / `Title` / `Status` / `Attributes` / `Tags` / `ChangeNote`（presence） / `CreatedAt` | 1〜8 | 全 8 項目一致（落ちなし） |

落ちているのは `SharedWith` の 1 項目だけである。

## 設計

1. **proto**: `DocumentSummary` に `repeated string shared_with = 12;` を末尾追加する（既存番号は不変・非破壊）。
   `check-proto-contracts.js --update` で baseline を更新し、差分をレビュー対象にする。
2. **null の扱い**: proto3 の `repeated` は presence を持たない。DTO の契約は「null＝共有なし」で、
   サーバ（`DocumentEndpoints.NullIfEmpty`）は**空リストを作らない**。よって
   - `ToProto`: `SharedWith` が null なら何も書かない、在れば順序どおり `AddRange`。
   - `ToDto`: 0 件なら **null**、1 件以上なら順序どおりの `List<string>`。
   - 空リスト `[]` は往復で null に正規化される（サーバが作らない形であり、BFF の判定〔`WithSharedWith` は空集合を載せない〕・`ForViewer` の読みとも同値）。
   `optional` 相当のラッパーは使わない（null と空を区別する消費者が居ない）。
3. **全項目の固定**: 写像の往復試験の器（全項目を既定値以外にした `DocumentDto`）に `SharedWith` を入れ、
   **反射で「器の全公開プロパティが既定値でない」ことを確かめる**試験を足す。DTO に項目を足して写像を忘れると器の試験が落ちる（同型の再発防止）。

## 受け入れ基準と試験の写像

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| AC-1 | proto に `shared_with` を後方互換で足し、`check-proto-contracts` の基準を更新する | `node scripts/check-proto-contracts.js`（baseline 更新後に緑）・`--self-test` |
| AC-2 | `ToProto` / `ToDto` で `SharedWith` を写す。往復で保たれる（複数件・順序・空・null） | `Knowledge.Contracts.Tests/DocumentReadGrpcMappingTests`（複数件・null・空→null・バイト列経由の往復・全項目の器） |
| AC-3 | REST と gRPC の両経路で同じ文書の応答が一致する | `DocumentService.Tests/.../GrpcDocumentReadSharedWithTests`（共有つきの組織文書を REST と gRPC で読み `BeEquivalentTo`） |
| AC-4 | 共有された相手が gRPC 経路で SC-03 の個人資料を開ける（陽性）・共有されていない相手は開けない（陰性） | 文書サービス側: 同上（利用者文脈＝共有先で found・`SharedWith` が載る／他人で found=false）。BFF 側: `Platform.Bff.Tests/BffSharedDocumentGrpcReadTests`（gRPC 経路で共有先ベース分岐が 200／共有先外が 404／所有者に `sharedWith` が返る／共有された相手には落ちる） |

## 並行作業との重複回避

- REST east-west の退役（BFF の REST クライアント撤去）と #1897（`GrpcClientExtensions.cs`・`DocumentBffEndpoints.cs` の `FetchListAsync`）が並行している。
  本 PR は **`DocumentBffEndpoints.cs` / `GrpcClientExtensions.cs` / 既存 `BffDocumentGrpcTests.cs` を触らない**。BFF の試験は新規ファイルに置き、
  gRPC の CallInvoker スタブも新規ファイル内に閉じる（既存の private スタブを共有化しない）。

## 母集合の走査（規則 9・10）

`git grep -I -E "DocumentSummary|content_fingerprint|shared_with"`（submodule・`.ai-context/specs/`・`CHANGELOG.md` を除く）:
proto 本体・`DocumentReadGrpcMapping.cs`・baseline・IADR-0475（凍結記録。触らない）。`docs/api/east-west-grpc.md` は項目一覧を持たないので追随不要。
`docs/functional/FR-06_document-crud-versioning.md` の出力欄は DTO 側（`SharedWith` を含む）で正しい。

## 検証

- `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes`（`src/knowledge/backend/backend.slnx`、BFF 試験のため `src/platform/backend/backend.slnx` も）
- `node scripts/check-proto-contracts.js`（＋ `--self-test`）・`check-trace-blocks.js`・`check-commit-messages.js` ほか文書系検査
