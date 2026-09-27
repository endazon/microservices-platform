---
title: 機械クライアントに自分が owner の組織文書のメタデータ更新・削除を許し、owner と doc_scope を書き換えさせない（#1616）
type: spec
status: done
related_ids: [FR-06, FR-08, FR-19, UC-03, SC-05, NFR-09, ADR-0119, ADR-0036, ADR-0034, ADR-0056, ADR-0057, ADR-0058, ADR-0060, ADR-0091, IADR-0039, IADR-0044, IADR-0075, IADR-0475, IADR-0476]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0119_document-machine-client-own-docs-and-read-abac.md 決定 1・2
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md D-01・D-07・§未確定事項 3
  - planning:projects/microservices-platform/07_adr/ADR-0056_existence-hiding-boundary-404-403.md
issue: "#1616"
---

# 仕様書: 機械クライアントの自分の文書の更新・削除と、owner / doc_scope の不変性（#1616）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-06**（文書の管理）、FR-08（AST の KB 保存。機械クライアントの呼び出し元）、FR-19（個人資料は対象外）、NFR-09
- 画面: SC-05（「管理者限定」の射程 ＝ 人の利用者）
- 関連 ADR:
  - **ADR-0119 決定 1**（外部 ID は当面持たない）・**決定 2**（機械クライアントは自分が owner の組織文書に限りメタデータ更新・削除。
    動的束縛・ロールは足さない・`owner`/`doc_scope` は書き換えさせない・機械の `POST` の `owner` はサービスアカウント・`POST` の追認・
    人には SC-05 の管理者限定・個人資料には及ばない・拒否は ADR-0056）
  - ADR-0036 D-01・D-07（動的束縛）・§未確定事項 3（所有者の移管は未確定）、ADR-0034 決定 9、ADR-0056、ADR-0057、ADR-0058
- 起点 issue: #1616（planning#680 の裁定 A・B）。**本 issue のマージで #1575 も閉じる**（項目 1 は決定 1 により持たない。項目 4 を本件で実装）。

## 対象範囲

- 対象（DocumentService の書き込み）:
  - `POST /documents`: 機械クライアントの `owner` をサービスアカウントにする（腕 B も同じ名前）。
  - `PATCH /documents/{id}/metadata`・`DELETE /documents/{id}`: 機械クライアントの自分の組織文書の分岐。
  - `PUT /documents/{id}`・`PATCH /documents/{id}/metadata`: `owner` の不変性（主体を問わない。下記「判断」）。
  - 記録: IADR-0044 決定 1・IADR-0075・IADR-0475・IADR-0039 への日付つき追記。OpenAPI、FR-06 の機能/テスト仕様書、SC-05 画面仕様書、セキュリティ仕様書。
  - `GrpcDocumentTagWriteTests.cs` の #1629 の試験ラベル T-12 を T-62 へ（FR-06 の T-12 との衝突。#1632 の監査）。
- 対象外:
  - 読み取りの口（`GrpcService`・`DocumentReadAccess`）—— #1628 / #1615。本件は `DocumentReadAccess.CanReadAsync` を**呼ぶだけ**で変えない。
  - 外部 ID・upsert（ADR-0119 決定 1 により持たない）。
  - BFF（人の経路。挙動は変わらない）・realm（ロールは足さない）。

## 設計

### 1. 主体名（`DocumentManageScope.MachineSubject`）

人か機械かは `MachinePrincipal.IsMachine` ただ 1 つ。機械の主体名は、腕 A（`preferred_username = service-account-<clientId>`）ならその名前、
腕 B（利用者名が無くクライアント識別だけ）なら `service-account-` ＋ クライアント識別（Keycloak の同じ規約）。同じクライアントが `profile` スコープの
有無で別の所有者にならない。`POST` は `MachineSubject ?? Identity.Name` を `WithOwner` へ渡す（要求の `owner` は従前どおり捨てる）。

### 2. メタデータ更新・削除の判定（3 段）

| 段 | 判定 | 応答 |
| --- | --- | --- |
| 群 | 書き込みの群の下限（admin / operator）—— 変えない | 403 |
| 1. 入口 `ForbidUnlessAdminOrMachine` | 管理者か機械でなければ拒否。**入力検証・取得より前**（`AdminOnly` を積んでいた頃と同じく、運用者だけの人は文書の有無・本文に依らず 403） | 403 |
| 2. 取得 `FindManageableAsync` | 不在・個人資料（#1629 の門） | 404 |
| 3. `DenyUnlessAdminOrMachineOwnerAsync` | 管理者なら通す。機械は `DocumentBodyIntake.IsOwnedBy(doc, MachineSubject)` なら通す。それ以外は ADR-0056 のとおり `DocumentReadAccess.CanReadAsync` が真なら 403・偽なら 404 | 403 / 404 |

- `AdminOnly` は 2 口から外した（ロールで塞ぐと機械の分岐が死ぬ）。`PUT`・`publish`・`archive` は `AdminOnly` のまま。
- 管理者ロールを持つ機械（`abac-seeder`）は管理者として扱う（従前の挙動を変えない）。
- `DocumentReadAccess` は現状、組織文書を認証済みの全主体に返すので、他の主体の組織文書への機械の拒否は現状 403。#1615 が組織文書の内容の ABAC を
  入れると、機械が読めない文書は自動的に 404 になる（試験は 403 / 404 のどちらでも「作用しない」を主張する）。

### 3. `owner` の不変性（判断）

**`owner` は属性を書き換える口（`PUT`・`PATCH metadata`）で、主体を問わず変えられない。** 監査の論点（組織文書の owner を管理者が書き換えられる）への回答。

| 案 | 評価 |
| --- | --- |
| **A. 主体を問わず不変**（採用） | ADR-0119 §理由「所有者で許す判定の前提を、判定の対象の側から崩させない」は人の管理者の経路にも同じく当てはまる。管理者が `owner` を機械へ書き換えればその機械へ書き込み権限を渡す移管になるが、移管は ADR-0036 §未確定事項 3 のまま |
| B. 機械の経路だけ不変 | 未確定の移管を管理者の一存で成立させる口が残る |

既存の仕様の確認: SC-05 の編集画面（`DocumentForm.tsx`）は `{ ...editing.attributes, confidentiality }` を送る —— 応答の属性（`owner` を含む）を
そのまま送り返すので A で退行しない。

- 規則 `DocumentBodyIntake.ValidateOwnerUnchanged`: 要求に `owner` キーが**無ければ通す**、**有れば**現在の値と序数一致を要する（現在 `owner` が無い文書へ付けるのも、空文字へ変えるのも拒否）。
  `doc_scope` の厳密な等値（キー欠落も変更扱い）と違い、欠落を許すのは、`owner` を持たない送り方（属性を一から組む呼び出し元）を「書き換え」と区別するため。
- 保存 `WithCurrentOwner`: 属性は全置換なので、送らなかった `owner` を現在の値で入れ直す（所有者が落ちない）。
- 拒否は 400（キー `owner`）。`doc_scope` の不変性の直後。**黙って捨てない**（移管できたと誤解させない）。

## 母集合（着手前に自分で引いた。規則 9）

### 変える口

`DocumentService/Features/Documents/` の書き込みのうち、ADR-0119 決定 2 が名指すもの（作成・メタデータ更新・削除）と、`owner` を書き換え得るもの
（属性を全置換する `Update`・`UpdateMetadata`）。`owner` を書き換え得る他の経路: 本文の投入（属性に触れない）、タグ反映（タグだけ）、共有台帳（別表）、
個人資料・同期の口（個人資料だけ。`PrivateNote.OwnerId` と揃えて作る）、正規化の取り込み（取り込み元が `owner` を決める。口ではない）—— いずれも対象外。

### 追随する記述（誤りの側の文字列で走査した）

`git grep -n -l -E "document-write-machine-client|POST しか|POST\` しか|POST /documents\` だけは|UntilArbitration|自分が所有する文書でも|自分が作った文書でも"`
（`.ai-context/specs`・`.ai-context/superpowers`・`CHANGELOG.md`・`src/ai-stock-trading` を除く）→ 10 ファイル。

| ファイル | 扱い |
| --- | --- |
| `.ai-context/adr/IADR-0039_…` | 「SC-05 は `POST` 1 口が裁定待ち」→ 日付つき追記で解消を記す |
| `.ai-context/adr/IADR-0044_…` | 決定 1 へ #1616 の追記（本件の主たる記録） |
| `.ai-context/adr/IADR-0475_…` | 決定 5 へ追記（裁定・ADR-0091 の読み方） |
| `docs/functional/FR-06_…` | 「自分が所有する文書でもできない・判断を待つ」を差し替え、節を新設 |
| `docs/screens/SC-05_document-management.md` | 「裁定を依頼中」→ 裁定済み（人の挙動は不変） |
| `docs/security/security.md` | 「計画へ裁定を依頼中」「admin 必須のまま。判断を待つ」を差し替え |
| `…/Create/Endpoint.cs` | 「裁定待ち・裁定が出たら追随」→ 追認の注記 |
| `…/Tests/…/DocumentAuthorizationTests.cs` | 「裁定を待つ」「裁定まで据え置く」の注記を差し替え（試験名は確定済み仕様書が引くので変えない） |
| `.ai-context/adr/IADR-0135_…` | 除外（BFF の検索が POST しか持たない話。無関係） |
| `.ai-context/adr/IADR-0185_…` | 除外（環流記録の状態語彙の記録。当時の事実） |

IADR-0075 は上の語で引っかからなかった（本文は「構造上 `POST /documents`（カタログ登録＝作成）しか発行しない」で、括弧が挟まる）。issue が名指すので追記した
（規則 2 の取りこぼしの実例 —— 語の形を 1 つに固定すると落ちる）。

### 呼び出し側（壊さないこと）

- **AST の KB 書き込み**（`src/ai-stock-trading`。読み取り専用）: pin `7a7a8a14` は `POST /documents` だけ、AST `origin/develop` `892376e8` は
  `GET /documents`・`POST /documents`・`PUT /documents/{id}/body` を呼ぶ。`PATCH`・`DELETE` はまだ呼ばない。
  - 作成: AST のトークンは `preferred_username = service-account-ai-stock-trading-kb-writer`（腕 A）なので **`owner` は従前と同じ値**。
  - 本文の投入: 所有者の束縛で通る（変更なし）。
  - ⇒ **影響なし**。AST は今後、自分の古い写しを `DELETE` で消せる（ADR-0119 の目的）。
- **BFF**: 人の経路。`PUT` / `DELETE` を利用者の資格情報で中継する（管理者だけが BFF の門を通る）。SC-05 の編集は `owner` を同値で運ぶ ⇒ 挙動は変わらない。
- **既存の試験**: `PUT` / `PATCH` で属性を一から組む試験は `owner` を送らない ⇒ `WithCurrentOwner` で所有者が残るだけで、応答は変わらない（DocumentService.Tests 710 件は変更前後とも緑）。

## 受け入れ基準

- [x] 機械クライアント（腕 A・腕 B）が作る文書の `owner` はそのサービスアカウント（要求の `owner` は捨てる）。
- [x] 機械クライアントは自分が `owner` の組織文書のメタデータを更新でき（200・版が進む・`owner`/`doc_scope` は残る）、削除できる（204・削除のイベント）。
- [x] 他の主体（利用者・別の機械・所有者なし）の組織文書は 403 または 404 で、何も変わらない。同じ文書を人の管理者は更新できる（陽性対照）。
- [x] 個人資料（機械自身が `owner` でも）と不在の ID は 404。
- [x] `owner`（別の値・空文字）と `doc_scope` の書き換えは 400 で保存されない。`owner` を送らない更新は 200 で所有者が残る。
- [x] 運用者だけの人は、自分が `owner` の文書でも、不在の ID でも 403。ロールを持たない機械は 403。運用者の機械の `PUT`・`publish`・`archive` は 403。
- [x] 人の管理者も `PUT` / `PATCH` で `owner` を書き換え・付与できない（400）。送らない保存・同値の保存は 200。
- [x] 変異試験: 所有者の比較を外すと「他の主体の文書」の 3 行が落ちる。`UpdateMetadata` の `owner` 不変性の呼び出しを外すと 3 行が落ちる。

## 検証

- `dotnet test` を両 slnx で、`dotnet format --verify-no-changes` を両 slnx で、`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`。
- BFF の契約（`/bff/*`）は変えていない ⇒ フロントの検査は対象外（OpenAPI の変更は DocumentService の内部口の説明だけ）。
