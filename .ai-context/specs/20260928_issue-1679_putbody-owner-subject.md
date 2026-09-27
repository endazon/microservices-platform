---
title: 本文を入れる口（PutBody）ほか所有者で許す文書の口の主体を、作成の口と同じ規則（MachineSubject）へそろえる（#1679）
type: spec
status: done
related_ids: [FR-06, FR-08, FR-18, FR-20, FR-21, UC-03, NFR-09, ADR-0122, ADR-0119, ADR-0036, ADR-0056, ADR-0034, IADR-0044, IADR-0031, IADR-0476]
author: claude
created: 2026-09-28
updated: 2026-09-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0122_ast-stale-copies-deletion-scope-and-switchover-order.md 実測 8・フォローアップ 3
  - planning:projects/microservices-platform/07_adr/ADR-0119_document-machine-client-own-docs-and-read-abac.md 決定 2
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md D-07
issue: "#1679"
---

# 仕様書: 所有者で許す文書の口の主体を、作成の口と同じ規則へそろえる（#1679）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`、隣接クローン `origin/main` = `17518cc`。読み取り専用）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-06**（文書の管理。機械クライアントの自分の文書）、FR-21（本文の直接受け入れ）、FR-20（共有）、FR-18（タグ反映）、FR-08（AST の KB 保存）
- 関連 ADR:
  - **ADR-0122 実測 8・フォローアップ 3**（planning#696 の裁定 2026-09-28）: 「本文を入れる口（`PutBody`）の所有者の判定を、作成の口と同じ主体の規則にそろえる。AST の入れ直しが自分の写しに本文を入れられることを確かめる」
  - ADR-0119 決定 2（機械クライアントの主体名 ＝ サービスアカウント）、ADR-0036 D-07（`doc.owner ∈ { ${current_user} }`）、ADR-0056（拒否は 404）、ADR-0034 決定 9（機械は個人資料を扱わない）
- 起点 issue: #1679。前提: #1616（`DocumentManageScope.MachineSubject` の導入。記録は IADR-0044 決定 1 の 2026-09-27 追記）

## 着手前の確認（推論を現物で確かめた）

| # | 推論 | 現物（`origin/develop` = `f42e4e36`） | 判定 |
| --- | --- | --- | --- |
| 1 | `PutBody` が `Identity.Name` で判定している | `Features/Documents/PutBody/Endpoint.cs` L48: `DocumentBodyIntake.CanWrite(doc.Attributes, http.User.Identity?.Name)` | **正しい** |
| 2 | 利用者名の無いサービスアカウントが、自分の作った文書に本文を入れられない | 作成（`Create/Endpoint.cs` L78）は `MachineSubject(http.User) ?? Identity.Name` で `owner = service-account-<azp>` を入れる。`PutBody` では `Identity.Name` が null → `IsOwnedBy` は空白の主体を偽にする（`DocumentBodyIntake.cs` L129）→ **404**。新設の試験（下記 T-73 の 1 行目）を**変更前のコードで走らせて 404 で赤**になることを実測した | **正しい** |

## 母集合（規則 9・10。着手前に自分で引いた）

### 走査語とヒット

`git grep -n -E "Identity\??\.Name" -- 'src/**/*.cs'`（`src/ai-stock-trading` を除く）→ 試験を除き 33 行。うち `owner` とも同じファイルで現れるもの
（`xargs grep -l -i owner`）と、`CanWrite` / `IsOwnedBy` の呼び出し（`git grep -n -E "CanWrite\(|IsOwnedBy\("`）で絞った。

| 箇所 | 比較 | 扱い |
| --- | --- | --- |
| `DocumentService/.../PutBody/Endpoint.cs` L48 | `CanWrite(attrs, Identity.Name)` | **変える**（本件の主対象） |
| `DocumentService/.../GrantShare/Endpoint.cs` L38・`GrantedBy` L46 | `CanWrite(attrs, Identity.Name)`・`Identity!.Name!` | **変える**。`GrantedBy` も同じ主体にする（腕 B で `null!` を台帳へ入れない） |
| `DocumentService/.../RevokeShare/Endpoint.cs` L24 | 同上 | **変える** |
| `DocumentService/.../ListShares/Endpoint.cs` L17 | 同上（読み取りだが共有の管理＝所有者限定の口） | **変える**（付与できて一覧できない食い違いを作らない） |
| `DocumentService/.../AddTag/Endpoint.cs` L54（REST 面） | `AddDocumentTagUseCase` の `CanWrite(attrs, subject)` へ `Identity.Name` を渡す | **変える** |
| `DocumentService/.../Create/Endpoint.cs` L78 | `MachineSubject ?? Identity.Name`（規則の元） | 同じ式を共有の関数へ移す（挙動は変えない） |
| `DocumentService/.../AddTag/GrpcService.cs`（gRPC 面） | 要求本文の `UserId`（中継された利用者） | 除外: 主体は呼び出し元の資格情報ではなく中継された承認者（ADR-0086 決定 1）。機械自身の主体を引く口ではない |
| `DocumentService/.../DocumentManageScope.cs` L78 | `IsOwnedBy(attrs, MachineSubject)` | 既に規則どおり（#1616） |
| `DocumentService/.../DocumentReadPrincipal.cs` L52-55 | 機械は `MachineSubject`、人は `Identity.Name` | 既に規則どおり（#1614 / #1615） |
| `DocumentService/Features/PrivateNotes/PrivateNoteEndpoints.cs` L92 | `SubjectOf` = `Identity.Name` | 除外: 個人資料は人だけのもの（ADR-0034 決定 9）。機械の主体を足すと個人資料の口を腕 B の機械へ開く向きになる |
| `DocumentService/.../List`・`ListPage`（列挙の口） | — | 除外: #1667（列挙の口）の領域。別の作業者が並行で進めている |
| `GraphService/.../IGraphAccessResolver.cs`・`GrpcDocumentTagWriter.cs`、`RetrievalService/.../SearchAccessResolver.cs`、`WikiService/.../WikiAccessResolver.cs`、`Platform.Shared/.../BffScopeResolver.cs`、`McpServer/.../AuthorizationServiceRegistrarAttributes.cs` | 認可サービスへ `userId` を渡すだけで、`owner` を自分で比べない | 除外: 所有者の比較は認可サービスの動的束縛（本件の射程外） |
| `FeedbackService`・`DashboardService`・`AiAnalysisService`・`Platform.Bff` の `Identity.Name` | 監査・送信者の記録 | 除外: 所有者の比較ではない |
| `NotificationService/Domain/NotificationSubject.cs`・`McpServer/.../McpSubjectResolver.cs` | 自サービスの主体解決（`sub` 優先） | 除外: 文書の `owner` と比べない |

### 追随する記述（誤りの側の文字列で走査した）

`git grep -n -E "Identity\??\.Name|利用者名と一致" -- docs .ai-context/adr`（凍結記録の本文は追随対象外）→ 追随が要るのは次だけ:

- `docs/functional/FR-06_document-crud-versioning.md` §機械クライアントの自分の文書 —— 本文の投入・共有・タグ反映の主体も同じ名前であることを 1 項足す。
- `.ai-context/adr/IADR-0044_…` 決定 1 —— #1616 の追記の直後へ日付つき追記（新しい IADR は作らない）。
- `IADR-0410` L88（`AddTag/Endpoint.cs:58` の `Identity?.Name`）は当時の実測の表であり書き換えない（凍結記録）。

### 呼び出し側（壊さないこと）

- **人の利用者**: `MachineSubject` は人に null を返すので、主体は従前どおり `Identity.Name`。挙動は変わらない（既存の試験は無変更で緑）。
- **腕 A の機械**（`preferred_username = service-account-…`）: `MachineSubject` は `Identity.Name.Trim()`。作成時の `owner` も同じ関数なので一致する。
- **腕 B の機械**: 本件で初めて、自分が作った文書に本文・共有・タグを入れられる（ADR-0122 フォローアップ 3 の目的）。
- 名前もクライアント識別も無い主体（`MachinePrincipal.IsMachine` が偽）: 従前どおり主体なし → 誰の文書にも書けない。

## 設計

- `DocumentManageScope.OwnerSubject(ClaimsPrincipal)` を足す: `MachineSubject(user) ?? user.Identity?.Name`。**`owner` と比べる・`owner` へ入れる主体は、この関数ただ 1 つから引く**
  （作成・本文の投入・共有 3 口・タグ反映の REST 面）。2 本目の式を口の中へ書くと片方だけが直る（本件がその実例）。
- 比較は従前どおり `DocumentBodyIntake.CanWrite` / `IsOwnedBy`（序数一致・空白は偽）。拒否の形（404）も変えない。
- 判断（共有・タグの口まで広げるか）: **広げる。** ADR-0119 決定 2 は機械の主体名をサービスアカウントと定め、#1616 は「同じクライアントが `profile` スコープの有無で
  別の所有者にならない」ことを `MachineSubject` の要件にした。腕 A の機械は今日すでに共有・タグの口を自分の文書に使える。腕 B だけが使えないのは新しい権限の
  有無ではなく主体の引き方の食い違いである。口ごとの許可の範囲（所有者だけ）は変えない。

## 受け入れ基準

- [x] 利用者名の無い機械クライアント（腕 B）が、自分の作った文書に本文を入れられる（200・本文と指紋が入る）。同じクライアントの腕 A でも入れられる。
- [x] 腕 B の機械は、別の機械（腕 A・腕 B）の文書と人の文書に本文を入れられない（404・本文は入らない）。
- [x] 人の利用者: 所有者は入れられ、別の利用者・名前もクライアント識別も無い主体は 404（従前どおり）。
- [x] 腕 B の機械が自分の文書の共有を付与・一覧・取り消しでき、付与者はサービスアカウント名。別の機械の文書は 404。
- [x] 腕 B の機械が自分の文書へタグを足せる。別の機械の文書は 404。
- [x] 変異 3 件以上が赤になる（PR 本文に表）。

## 検証

- `dotnet test` を `DocumentService.Tests` で全件、`dotnet format --verify-no-changes`（knowledge の slnx）、`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`、
  文書系の検査器（`check-trace-blocks`・`check-test-spec-coverage`・`check-test-traceability`・`check-cross-repo-refs`・`check-plan-id-qualification`・
  `gen-knowledge-graph --check`・`check-commit-messages --range=origin/develop..HEAD`）。

## ［2026-09-28 追記 / #1679］監査（PR #1680・head `dd6ede2a`）の指摘への対応

### 走査語の取りこぼし（規則 9・10）

走査語 `Identity\??\.Name` は `Identity!.Name` の形を拾わない。`git grep -n -E "Identity!\.Name" f42e4e36 -- 'src/**/*.cs'`（試験を除く）→ 7 行。

| 箇所 | 中身 | 扱い |
| --- | --- | --- |
| `Knowledge.Bff.Endpoints/Documents/DocumentReadGrpcClient.cs` L97 | 読み取りの gRPC へ運ぶ利用者文脈（`UserContext.UserId`） | 除外: 所有者との比較ではない（受け口が判定する） |
| `AiAnalysisService/.../Analyze/Endpoint.cs` L41・`Ask/Endpoint.cs` L21 | ABAC へ渡す userId | 除外: 所有者との比較ではない |
| `AiAnalysisService/.../AskStream/Endpoint.cs` L53 | 同上 | 除外: 同上 |
| `RetrievalService/Domain/SearchUserContext.cs` L53 | 検索の利用者文脈 | 除外: 同上 |
| `DocumentService/Features/PrivateNotes/PrivateNoteEndpoints.cs` L92 | `SubjectOf`（`Identity?.Name` と同じ行。上の母集合で既出） | 除外: 個人資料は人だけ（ADR-0034 決定 9） |
| `DocumentService/.../GrantShare/Endpoint.cs` L47 | 共有の付与者（`GrantedBy`） | 変えた（同じファイルの L38 を読んだときに拾っていた。上の母集合の表の GrantShare 行） |

所有者との比較の取りこぼしは無かった。

### 監査で生き残った変異と、足した試験

「`OwnerSubject` が人でも `azp` を優先して `service-account-<azp>` を返す」変異は、885 件がすべて緑のまま残っていた。試験の人が `azp` を持たなかったためである（本番の人のトークンは `azp=bff` を持つ）。
`利用者名とazpを持つ人は自分の文書を扱え_別の人の文書には404になる` を足した（T-73 に 1 行を追記）。

### 残余リスク

- **利用者名を持たない人のトークン**（`profile` スコープが無く `azp=bff`）は、`MachinePrincipal.IsMachine` で腕 B と読まれ、主体が `service-account-bff` の 1 つにまとまる。これは #1616（作成の口の `owner`・メタデータ更新・削除）以来の前提である。
  本件で、この主体どうしが本文・共有・タグでも互いの文書に書ける範囲まで広がる。
- **緩和（実在と CI 配線を確かめた）**: `scripts/check-realm-constraints.js` の検査 7（#1589 / #1587）が、realm の宣言について次を拒否する。
  - 標準フロー（人がログインする経路）のクライアントが `profile` を既定のスコープに持たないこと
  - `profile` スコープが `preferred_username` を access token へ載せないこと
  - 軽量アクセストークンで利用者名が落ちること
  - `service-account-` 接頭辞の人の利用者
  CI では `.github/workflows/ci.yml` の `static-checks` ジョブが、`--self-test`（L473）と本走（L476）の 2 段で走らせている。本作業の時点で本走は OK だった。
- 稼働中のトークンに利用者名が載るかの実測は、計画 ADR-0122 のフォローアップ 4 で行う（稼働クラスタ。本件の外）。
