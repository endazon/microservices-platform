---
title: 管理者の書き込みの口（更新・公開・保管・削除）とタグ反映の管理者の分岐から、他人の個人資料を外す（#1629）
type: spec
status: done
related_ids: [FR-05, FR-06, FR-18, FR-19, FR-21, UC-03, SC-05, NFR-09, ADR-0036, ADR-0056, ADR-0058, ADR-0063, ADR-0119, IADR-0044, IADR-0277, IADR-0364, IADR-0455, IADR-0476]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md D-08
  - planning:projects/microservices-platform/07_adr/ADR-0119_document-machine-client-own-docs-and-read-abac.md 決定 3
  - planning:projects/microservices-platform/07_adr/ADR-0056_existence-hiding-boundary-404-403.md 決定 1
issue: "#1629"
---

# 仕様書: 管理者の書き込みの口から他人の個人資料を外す（#1629）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-19**（個人資料）、**FR-06** / **UC-03**（文書の管理）、NFR-09（全 API で文書・データ単位の認可）、FR-18（タグ提案の反映）
- 画面: SC-05（文書管理。管理の口の利用者）
- 関連 ADR:
  - **ADR-0036 D-08**（管理者・運用者は平時、非公開の個人資料を一切閲覧できない）
  - **ADR-0119 決定 3**（個人資料は所有者と共有先にだけ返す。管理者を含め、他の主体には返さない）
  - ADR-0056 決定 1（読めない文書への拒否は 404。存在を明かさない）、ADR-0058（`doc_scope` の不変）、ADR-0063 決定 3（タグ反映の選言）
- 起点 issue: #1629（PR #1626〔#1614〕の監査 F2）

## 目的・背景

DocumentService の管理の書き込み口 —— `PUT /documents/{id}`・`PATCH /documents/{id}/metadata`・`POST /documents/{id}/publish`・
`POST /documents/{id}/archive`・`DELETE /documents/{id}` —— は `AdminOnly` のロールだけで守られ、**どの文書に作用してよいか**を
確かめていない。`AdminOnly` を持つ主体（人の管理者、機械の `abac-seeder`〔`service-account-abac-seeder` ＋ `platform-admin`〕）は
他人の個人資料を書き換え・公開・保管・削除でき、応答に完全な DTO（表題・owner・共有先）が返る。`PUT` は属性を全置換するので
`owner` を自分へ書き換え、そのあと #1614 の読み取りの口から所有者として読める。BFF は `IsManageable` で同じ経路を塞いでいるが、
後段は最終防衛線である（IADR-0044）。

## 対象範囲

- 対象:
  - 上の 5 口（DocumentService）。
  - **タグの反映口 `POST /documents/{id}/tags` と east-west gRPC `DocumentTagWrite/AddTag` の「②管理者ロール」の分岐**
    （issue の列挙には無いが、母集合を引いた結果、同じ型の欠陥として見つかった。下記「母集合」）。
  - 記録: IADR-0044 決定 1・IADR-0364 決定 3・IADR-0455 §結果 への日付つき追記。OpenAPI の 404 の説明、機能・テスト・セキュリティの仕様書。
- 対象外:
  - **読み取りの口**（`GrpcService`・`DocumentReadAccess`）—— #1628 / #1615 が並行して扱う。本件は触らない。
  - **組織文書での `owner`・`doc_scope` の書き換えの禁止、機械クライアントの自分の文書の更新・削除** —— #1616（ADR-0119 決定 2）。
    issue の「交差する」点は、**本件は個人資料を管理の口から外すだけ**、#1616 は組織文書の側で `owner`/`doc_scope` を不変にする、と分けた。
    本件の後に #1616 を入れる（#1616 の機械の分岐も個人資料には及ばない —— 本件の門の後ろに入る）。
  - BFF（`IsManageable` は既に個人資料を一律に落としている。変更なし。契約も変わらないのでフロントの検査は不要）。

## 設計

### 1. 判定点（`DocumentManageScope.FindManageableAsync`）

- 5 口の文書の取得を `DocumentManageScope.FindManageableAsync(db, id, ct)` 1 つへ寄せる。**不在と個人資料を同じ `null` に畳み**、
  口は従前どおり `Results.NotFound()` を返す。判定は `DocumentScopes.IsPrivateNote(doc.Attributes)`（集合帰属。値の大小を問わない。
  キー欠落は組織文書）—— #1614 の `DocumentReadAccess` と同じ関数。
- **判定の順序**: 入力検証（400。`Update`・`UpdateMetadata` の `validator.Validate`）は従前どおり取得より前（不在の ID への空題名は 400）。
  個人資料の判定は取得の直後で、`doc_scope` の不変性（400）・制限 project の保持（400）・並行制御（409）・状態遷移（409）より前。
  ⇒ 個人資料の実在は応答から推せない（不在の ID と同じ 404・同じ本文）。
- 新しいファイルに置いた（`DocumentEndpoints.cs` は #1628 / #1615 の読み取り側の変更と交差し得るため）。

### 2. 所有者の扱い（判断）

issue は「404 にする」か「所有者にだけ許す（FR-19 の経路と揃える）」かを選ばせていた。**主体を問わず一律に 404** を採った。

| 案 | 評価 |
| --- | --- |
| **A. 一律に 404（所有者が管理者でも）**（採用） | BFF の `IsManageable`（「除外は所有者を問わず一律」）と同じ形。判定軸が 1 本。所有者は FR-19 の経路を持つ |
| B. 所有者だけ許す（`DocumentBodyIntake.IsOwnedBy`） | 管理の口の `PUT` は属性を全置換するので、所有者自身が `owner` を他人へ書き換え `PrivateNote.OwnerId` と食い違わせられる。`DELETE` はごみ箱（ソフト削除・完全削除の猶予）を経ずに消す。所有権の 2 か所の整合を管理の口が壊し得る |

- **所有者の経路**（変わらない）: `/private-notes/*`（作成・ごみ箱・復元・完全削除・露出）、Obsidian 同期（push・move・delete。端末の所有者で束縛）、
  `PUT /documents/{id}/body`（`DocumentBodyIntake.CanWrite` → `IsOwnedBy`）、共有台帳（`GrantShare` / `RevokeShare` / `ListShares`。同じく `CanWrite`）、
  タグの反映口の①（`CanWrite`）。どれも管理の口を経由しない（実測。下記「母集合」）。

### 3. タグの反映口の管理者の分岐

`AddDocumentTagUseCase`（REST と gRPC の共通の本体）で、②を `isAdmin && !DocumentScopes.IsPrivateNote(doc.Attributes)` に狭める。
①（所有者 = `DocumentBodyIntake.CanWrite` = `IsOwnedBy`）はそのまま。拒否は従前どおり `NotWritable`（REST 404 / gRPC `TagWriteResult.NotWritable`）。
GraphService の `CanDecide` は可視性の判定が先に立つ（IADR-0364 決定 3）ため、管理者は他人の個人資料の提案をそもそも見ない ——
本件は最終防衛線を同じ形にする。

### 4. 既存の試験で直したもの（欠陥を固定していた試験）

`PrivateNoteExposurePublishTests` の 2 Theory（#1471。「管理者の属性更新で露出が外れると撤収のイベントが発行される」「全てOFFの個人資料を管理者が更新しても発行されない」）は、
**既定の主体（`test-user` ＋ `platform-admin`。所有者ではない）が他人の個人資料を PUT / PATCH できること**を前提にしていた —— 本件の欠陥そのもの。
「管理者の属性更新は他人の個人資料に届かず_404で撤収も書き換えも起きない」の 1 Theory（PUT / PATCH）へ置き換え、陽性対照として
同じ資料で所有者の SetExposure が撤収まで届くことを示す。撤収の形の門（IADR-0455 決定 1）は 2 口に残す（組織文書では単純な門と同値。IADR-0455 への追記）。

## 母集合（着手前に自分で引いた。規則 9）

### 引き方

1. 書き込みの REST 口: `grep -rn "\.Map\(Put\|Post\|Patch\|Delete\)(" src/knowledge/backend/Services/DocumentService/Features`（`obj/` 除外）→ **28 行**。
2. 書き込みの gRPC 面: `grep -rn "MapGrpcService" .../DocumentService/Program.cs` → 3 面のうち書き込みは `DocumentTagWriteGrpcService`（`AddTag`）の 1 rpc。
3. 認可の軸: 28 口それぞれについて「文書への作用を**ロール**で許す分岐があるか」を読んだ（`RequireAuthorization` / `IsInRole` / `CanWrite` / `OwnerId ==` の grep と本文の確認）。

### 結果

| 口 | 作用を許す根拠 | 他人の個人資料に作用し得たか | 本件 |
| --- | --- | --- | --- |
| `PUT /documents/{id}` | `AdminOnly` | **はい**（DTO 返却・`owner` 書き換え） | **塞ぐ** |
| `PATCH /documents/{id}/metadata` | `AdminOnly` | **はい**（同上） | **塞ぐ** |
| `POST /documents/{id}/publish` | `AdminOnly` | **はい**（DTO 返却） | **塞ぐ** |
| `POST /documents/{id}/archive` | `AdminOnly` | **はい**（DTO 返却） | **塞ぐ** |
| `DELETE /documents/{id}` | `AdminOnly` | **はい**（ごみ箱を経ない削除） | **塞ぐ** |
| `POST /documents/{id}/tags` ＋ gRPC `AddTag` | ①所有者 **または** ②`platform-admin` | **はい**（②。DTO 返却） | **②を塞ぐ** |
| `POST /documents` | admin / operator | いいえ（作成。検証器が `doc_scope=private-note` を拒否） | 対象外 |
| `PUT /documents/{id}/body` | 所有者（`CanWrite`） | いいえ | 対象外（陽性対照に使う） |
| `POST /documents/{id}/shares`・`DELETE …/shares/{type}/{id}` | 所有者（`CanWrite`） | いいえ | 対象外 |
| `/private-notes/*` の 5 口（Create / Purge / Restore / SetExposure / SoftDelete） | 本人（`OwnerId == owner`） | いいえ | 対象外 |
| `/private-notes/sync/*` の 3 口（Push / Move / Delete） | 同期トークンの端末の所有者 | いいえ | 対象外 |
| `/private-notes/conflicts/{id}/resolve`・`sync-settings`・端末の 4 口 | 本人 | いいえ | 対象外 |
| `PUT /private-notes/quotas/{ownerId}` | `AdminOnly` | いいえ（上限値と使用量の数値だけを返す。資料の中身・表題に作用しない） | 対象外 |
| `/tags` の 3 口（Create / Rename / Delete） | admin / operator ・ `AdminOnly` | 辞書の操作。使用件数に個人資料が含まれる（件数だけで、表題・owner は返さない）。改名は付いている文書を再発行するが門を通る | 対象外（理由: 辞書の管理であり特定の文書に作用しない） |

合計: 28 口 ＝ 5（塞ぐ）＋ 1（タグ反映）＋ 22（対象外）。gRPC の書き込み 1 rpc はタグ反映と同じ本体。
正規化の取り込み（`DocumentNormalizedConsumer`）は口ではない（ID は取り込み元から決まる UUIDv5 で、個人資料の ID と衝突しない）ので除外した。

### 呼び出し側（壊さないこと）

- **BFF**: 5 口を `ForwardIfInScope` で呼ぶが、事前に `IsManageable` が個人資料を落とす（404）。BFF の応答は変わらない。
- **AST の KB 書き込み**（`src/ai-stock-trading`。読み取り専用で確かめた）: pin 7a7a8a14 は `POST /documents` だけ、AST の origin/develop 892376e8 は
  `GET /documents`・`POST /documents`・`PUT /documents/{id}/body` だけを呼ぶ（`HttpKnowledgeBaseWriter.cs`・`HttpKnowledgeDocumentCatalog.cs`）。
  管理の 5 口もタグの反映口も呼ばず、扱うのは組織文書だけ（作成の口は個人資料を拒む）。**影響なし。**
- **`abac-seeder`**: ABAC ポリシーの投入（認可サービス）であり、DocumentService の管理の口は呼ばない。本件はその資格情報でも個人資料に作用できないことを試験で固定する。
- **GraphService**: タグ提案の承認で gRPC `AddTag` を承認者の身元で呼ぶ。可視性の判定が先に立つため、管理者が他人の個人資料の提案を承認する経路は元から無い。

## 受け入れ基準

- [x] 管理者（人・機械〔`service-account-abac-seeder`〕）と、管理者ロールを持つ所有者本人が、個人資料に 5 口を呼ぶと 404。応答に表題・owner・文書 ID が出ない。
  表題・状態・版・`owner` は変わらず、`DocumentUpdated` / `DocumentDeleted` も出ない。同じ主体・同じ口で組織文書には作用できる（陽性対照）。
- [x] `PUT` / `PATCH` で `owner` を呼び出し元へ書き換えようとしても、個人資料の `owner` は変わらない。
- [x] 個人資料と不在の ID は、状態コードも本文も同じ（公開・削除で確認）。
- [x] タグの反映口の管理者の分岐（REST・gRPC）は他人の個人資料に及ばない（404 / `NOT_WRITABLE`、タグは付かない）。所有者（ロールなし）は同じ資料に足せ、管理者は組織文書に足せる（陽性対照）。
- [x] 所有者の経路は変わらない: 本文の投入は所有者に通り管理者に 404、露出の変更（SetExposure）は撤収まで届く。既存の個人資料の試験（ライフサイクル・同期・共有・露出）はすべて緑。
- [x] 変異試験: 5 口のうち 1 つ（`Publish`）で門を `db.Documents.FindAsync` へ戻すと、その口の 3 行と存在秘匿の 1 行が落ちる。タグ反映の門を外すと REST 2 行と gRPC 1 行が落ちる。

## 実装 ADR

新しい IADR は起こさない（欠番の心配もない）。**追記で足りる** —— 判断は IADR-0044 決定 1（管理の口のロールの門）の射程の補いであり、
タグ反映は IADR-0364 決定 3 の②の射程の補いである。IADR-0455 は前提（管理者が個人資料を書き換えられる）が崩れたことを追記する。

## 検証

`ci.yml` と同じコマンドをローカルで実行する（結果は PR 本文に証跡として貼る）。

- `dotnet test src/knowledge/backend/backend.slnx` / `dotnet test src/platform/backend/backend.slnx`
- `dotnet format <slnx> --verify-no-changes`（両ユニット）
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- BFF の契約は変えていないのでフロントの検査は対象外（OpenAPI は DocumentService の内部口の 404 の説明だけ。orval の入力は `/bff/` 配下のみ）。
