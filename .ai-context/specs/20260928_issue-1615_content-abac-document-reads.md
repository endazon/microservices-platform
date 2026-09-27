---
title: DocumentService の読み取りに、門が開いたときだけ効く内容の ABAC を入れ、所有者・利用者共有のコード判定を認可サービスへの問い合わせにまとめる（#1615）
type: spec
status: in-progress
related_ids: [FR-05, FR-06, FR-19, NFR-09, UC-03, SC-03, SC-05, ADR-0121, ADR-0119, ADR-0122, ADR-0036, ADR-0034, ADR-0056, ADR-0058, ADR-0086, ADR-0088, ADR-0109, IADR-0253, IADR-0416, IADR-0447, IADR-0476, IADR-0480, IADR-0481]
author: claude
created: 2026-09-28
updated: 2026-09-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0121_owner-read-policy-mandatory-and-content-abac-gate.md 決定 2・4（4 番目）・5・6・フォローアップ 4
  - planning:projects/microservices-platform/07_adr/ADR-0119_document-machine-client-own-docs-and-read-abac.md 決定 3・4・フォローアップ 2
  - planning:projects/microservices-platform/07_adr/ADR-0122_ast-stale-copies-deletion-scope-and-switchover-order.md 決定 3・4（段 2 は本件の射程外）
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md D-01・D-02・D-08・D-14
issue: "#1615"
---

# 仕様書: DocumentService の読み取りの内容の ABAC（#1615）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`、`origin/main` = `17518cc` を読み取り専用で参照）を一次情報とする。
> MSP は `origin/develop` = `653878c0`（#1673 マージ後）から分岐した。

## 起点となる計画書（トレーサビリティ）

- 要求: **NFR-09**（全 API で文書・データ単位の認可）、**FR-05**（ABAC）、FR-06 / UC-03 / SC-03 / SC-05（文書の閲覧・管理）、FR-19（個人資料）
- 計画 ADR:
  - **ADR-0121 決定 4 の 4 番目**（所有者の read ポリシーの存在を確かめ、内容の ABAC を有効にする）・**決定 5**（`IADR-0476` の所有者・利用者共有の
    コード判定を認可サービスへの問い合わせにまとめる。DocumentService に判定器を残さない。認可サービスが落ちたら自分の資料も読めない＝fail-closed を受け入れる）・
    決定 2（門）・決定 6（3 点セット）・フォローアップ 4
  - **ADR-0119 決定 3**（読み取りの全ての口で `read` 規則を自ら判定する。判定は認可サービスへ問う。主体は中継された利用者／本文の利用者文脈／機械自身。
    読めない文書は一覧から除き個別は 404。件数にも含めない。機械は属性が無ければ自分が owner の文書だけ）・フォローアップ 2（AST の入れ直しが写しを見つけられることを確かめる）
  - ADR-0122 決定 3・4（配備の段 2 は本件の射程外。列挙の口〔#1667〕ができるまで段 4〔有効化〕へ進まない ＝ 門の構成を `On` にしない運用で守る）
  - ADR-0036 D-01（判定器は ABAC ひとつ）・D-02・D-08（管理者も平時は他人の個人資料を見ない）・D-14（キャッシュのキーに主体を含める）、
    ADR-0034 決定 9（サービスアカウントは個人資料を一律に対象外）、ADR-0056（存在秘匿は 404）、ADR-0058、ADR-0086 決定 1、ADR-0088（属性は認可サービスが引き直す）、ADR-0109 決定 3
- 関連 IADR: IADR-0476（読み取りの判定点 `DocumentReadAccess`・決定 7 の差し込み口）、IADR-0480（所有者の read ポリシー・seed の期待値のファイル）、
  IADR-0481（門 `IContentAbacGate`。決定 6「#1615 が `DocumentReadAccess` から `IsOpen` を読む」）、IADR-0253（1 ポリシー = 1 分岐・束縛は評価器の中だけ）、
  IADR-0447（共有先の分岐）、IADR-0416（認可サービスは本文の属性を信じない）
- 起点 issue: #1615（着手条件 #1664 = PR #1670、#1665 = PR #1673 はマージ済み）

## 目的・背景

- #1614（IADR-0476）で DocumentService の読み取りは認証を要し、個人資料を所有者と共有先にだけ返すようになった。ただし
  **組織文書の内容の ABAC（機密・部門・ライフサイクル）は DocumentService に無く**、機械の呼び出し元（AST の KB の書き手・east-west）は
  機密・制限の組織文書を ABAC なしで読める（ADR-0119 実測 10・11）。
- 所有者と利用者共有はコードで判定しており、認可サービスに問うのはグループ共有だけである（ADR-0121 実測 5）。
- #1664 で所有者の read ポリシーが seed と配備の手順に入り、#1665 で門（`ContentAbac:Mode` 既定 Off・ポリシーを確かめるまで開かない・開いたらラッチ）が入った。
  本件は**門が開いたときだけ**、読み取りの判定を認可サービスの分岐 1 つにまとめる。

## 設計

### 1. 門で 2 つの判定を切り替える（`DocumentReadAccess`）

- `DocumentReadAccess` は `IContentAbacGate` を受け取り、**要求の中で初めて判定するときに 1 度だけ `IsOpen` を読んで固定する**（スナップショット）。
  同じ要求の一覧の途中で門が開いても、1 つの応答の中で判定が混ざらない（§窓 を参照）。
- **門が閉じている（既定 Off・未確認・数えられない）間は、今日の判定を 1 ビットも変えない**（IADR-0476 のコード判定のまま。認可サービスへの問い合わせの
  回数・条件も同じ）。`/documents/page` も今日どおり `DocumentReadAccess` を通さない。
- **門が開いているときは、次の 1 本だけで判定する**（ADR-0121 決定 5・ADR-0119 決定 3）:
  1. 機械の主体は個人資料を読まない（ADR-0034 決定 9。ABAC の外の規則なので判定器の二重化ではない）。
  2. 判定の主体名（下の §2）が決まらなければ何も読めない（fail-closed）。
  3. 認可サービスの `read` の分岐（`IDocumentReadScopeSource`。主体名ごとに要求の中で高々 1 回）を引く。引けない・許可なし・時間切れ・未構成は何も読めない。
  4. 共有先を `shared_with` として重ねた像（`DocumentAttributeEncoding.WithSharedWith`）に対して、
     `分岐のどれか ⋀ AttributeFilterMatch.MatchesAll ⋀ PrivateNoteVisibility.BranchMayGrant`。BFF・検索・グラフと同じ述語である（新設しない）。
  - 所有者・利用者共有のコード判定（`IsOwnedBy`・`SubjectId` の照合）は**この枝では使わない**（所有者は所有者の read ポリシー、利用者共有は共有先の
    ポリシーの `${current_user}`、グループ共有は `${current_groups}` の束縛として認可サービスが答える）。
  - 管理者ロールは特別扱いしない（D-08。今日と同じ）。

### 2. 判定の主体名（`DocumentReadPrincipal.AbacSubject`）

| 経路 | 門が閉じている間の主体（不変） | 門が開いたときに認可サービスへ名指す名前 |
| --- | --- | --- |
| REST・人 | 利用者（`Identity.Name`） | 同じ利用者名（無ければ null → 何も読めない） |
| REST・機械 | 機械（名前なし） | `DocumentManageScope.MachineSubject`（`service-account-<clientId>`。作成時に `owner` へ入れる名前と同じ関数） |
| gRPC・`user` あり（信頼する中継者） | その利用者／`service-account-` なら機械 | `user.user_id`（機械でも同じ名前） |
| gRPC・`user` なし | 呼び出し元サービス（機械） | 呼び出し元の `MachineSubject` |

- 機械の名前を `MachineSubject` に揃えるのは、**AST の KB の書き手が自分で作った写しの `owner` と一致させる**ためである（作成は同じ関数で `owner` を入れる。#1616）。
- Keycloak 24 の `GET /admin/realms/{realm}/users?username=…&exact=true` はサービスアカウントの利用者も返す（`UsersResource.searchForUser` の
  `includeServiceAccounts` が、`username` などの個別の条件を与えた枝では `true`。keycloak 24.0.0 のソースで確認）。したがって認可サービスは
  `service-account-ai-stock-trading-kb-writer` の属性（無し）を引き、所有者の分岐 `owner ∈ {service-account-ai-stock-trading-kb-writer}` を返す。

### 3. 一覧の問い合わせは件数に比例しない

- 一覧（REST `GET /documents`・`/documents/page`・gRPC `ListDocuments`）は、1 要求の主体が 1 人なので、**主体名で memo した 1 回の問い合わせ**で全件を判定する
  （IADR-0476 決定 4 の memo をそのまま使う。D-14 のキーの要件は主体名をキーにして満たす）。scope を DB の条件へ訳す形は採らない
  （属性は jsonb の値変換で SQL へ訳せない。既存の一覧も台帳を読んでからメモリで絞っている）。

### 4. `/documents/page` は門が開いたときだけ `DocumentReadAccess` を通す

- 門が開いたら、台帳を**切り出す前に**読める文書へ絞る（切り出した後に落とすと、ページが短くなり続きのカーソルがずれる）。共有先は全件分を 1 クエリで引く。
- 門が閉じている間は今日どおり（通さない。個人資料は主体に依らず返さない、の性質は開いても保たれる —— `DocumentPageQuery.Slice` がそのまま外す）。

### 5. 変えないもの

- 認証の門（REST の `read` 群・gRPC の `ServiceCaller`）、信頼する中継者（`DocumentReadRelayOptions`）、BFF、認可サービス、契約（proto のフィールド）、配備の値。
- 門そのもの（IADR-0481。構成の既定 Off・ラッチ・評価の周期）。
- 書き込みの判定（`DocumentManageScope`。ただし「読めるが書けない 403 ／ 読めない 404」の「読めるか」は同じ `DocumentReadAccess` が答えるので、門が開けば ABAC で答える。ADR-0056 のとおり）。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| AC-1 | **門が閉じている間は判定が変わらない**: 機密の組織文書は機械・属性の無い利用者にも返り、個人資料は所有者・利用者共有に認可サービスを問わずに返る（問い合わせ回数 0）。`/documents/page` も同じ | 単体・結合 |
| AC-2 | 門が開くと、属性の合わない利用者・機械には、機密・制限の組織文書が**一覧に出ず、個別・版の一覧・特定版は 404／gRPC `found=false`**（件数にも含めない）。陽性対照として、合う利用者には返る | 結合（REST 4 口・gRPC 4 rpc・`/page`） |
| AC-3 | 門が開くと、所有者の判定も認可サービスが答える: 所有者の read の分岐が無い主体は、自分の個人資料・自分の組織文書も読めない。分岐があれば読める | 単体 |
| AC-4 | 門が開くと、利用者共有・グループ共有の判定も認可サービスの分岐（`shared_with`）で答える。静的属性の分岐（`confidentiality` だけ）は他人の個人資料を開かない（D-08） | 単体 |
| AC-5 | 門が開くと、機械の主体は個人資料を読まない（分岐が一致しても） | 単体 |
| AC-6 | 門が開くと、認可サービスが答えない（null・例外に畳んだ縮退）ときは**何も読めない**（fail-closed）。名前の分からない主体も何も読めない | 単体 |
| AC-7 | **本文の scope を信じない**: 要求の `user.user_attributes` にどんな属性を載せても判定は変わらない（認可サービスへ送らない） | 結合（gRPC） |
| AC-8 | 一覧は、文書の件数によらず主体ごとに問い合わせ 1 回 | 結合 |
| AC-9 | AST の KB の書き手（`service-account-ai-stock-trading-kb-writer`・属性なし）は、門が開いても**自分が owner の写しを一覧で見つけられる**。他人の・`owner=system` の・`owner` の欠落した組織文書は見えない（ADR-0122 が段 2 で消す対象） | 結合（seed の期待値のファイルの応答を入力にする） |
| AC-10 | seed を入れた認可サービスが、サービスアカウントの主体に所有者と共有先の分岐だけを返す（期待値のファイルに主体を足し、実物の応答と突き合わせる） | 認可サービスの結合 |
| AC-11 | 門のスナップショット: 1 要求の中で門が開いても、その要求の判定は最初に読んだ状態のまま | 単体 |
| AC-12 | `/documents/page` は門が開いたら切り出す前に絞る（読めない文書でページが短くならず、カーソルで全件を辿れる） | 結合 |

## 母集合（着手前に自分で引いた。規則 9）

### 引き方

- `git grep -n "MapGet\|MapGrpcService\|Base$"`（DocumentService 本体・試験を除く）で読み取りの口を全件、`git grep -n "CanReadAsync\|DocumentReadAccess\|DocumentReadPrincipal"` で
  判定点とその呼び出し元を全件引いた。
- 呼び出し元（DocumentService の読み取りを呼ぶ側）は `"/documents`・`DocumentReadGrpcClient`・`DocumentRead.DocumentReadClient` を `src`・`scripts`・`deploy` で引き、
  #1614 の作業仕様書の棚卸し（`20260927_issue-1614_document-read-authn-private-note.md` §呼び出し元の棚卸し）と突き合わせた。AST は隣接クローン
  `origin/develop` = `b564cb8` の `HttpKnowledgeDocumentCatalog`（`GET /documents` だけを読む。`/page` は使っていない）。

### 結果（変えるもの）

| 口・判定点 | 経路 | 本件 |
| --- | --- | --- |
| `GET /documents` / `GET /documents/{id}` / `GET /{id}/versions` / `GET /{id}/versions/{v}` | REST → `DocumentReadUseCase` → `DocumentReadAccess` | 判定の本体を門で切り替える（§1） |
| gRPC `DocumentRead` の 4 rpc | gRPC → 同上 | 同上。`user` なしの主体に呼び出し元の名前を持たせる（§2） |
| `GET /documents/page` | REST（`DocumentReadAccess` を通していない） | 門が開いたときだけ通す（§4） |
| `DocumentManageScope.DenyUnlessAdminOrMachineOwnerAsync`（403 ／ 404 の「読めるか」） | 書き込み 2 口が同じ `DocumentReadAccess` を呼ぶ | 判定点を共有するので自動的に追随（コードの変更なし。試験で固定） |
| `DocumentReadPrincipal` | 全読み取り | `AbacSubject` を足す（閉じている間は読まない） |

### 除外したもの（理由）

- **`/private-notes/*`（一覧・容量）・Obsidian 同期（`/manifest`・`/notes/{id}`）・同期の設定・履歴・衝突・端末**: FR-19 の所有者の経路であり、所有者は
  `PrivateNote.OwnerId`（台帳）と同期トークンで決まる。ADR-0119 決定 3 の「読み取りの全ての口（一覧・個別・版の一覧・版の取得）」ではない。
  ここへ ABAC を入れると、所有者の read ポリシーを消したとき同期まで止まる（計画が求めていない広がり）。
- **`GET /documents/{id}/shares`**: 共有の管理（所有者の裁量。`DocumentBodyIntake.CanWrite`）。読み取り規則ではなく共有台帳の管理であり、ADR-0121 決定 5 の対象（`IADR-0476` の読み取りのコード判定）ではない。
- **`PUT …/body`・書き込み 5 口の所有者判定（`DocumentManageScope.MachineSubject`・`IsOwnedBy`）**: 書き込みの規則（ADR-0119 決定 2・ADR-0036 D-07）。
- **`/tags`・`/internal/tags/names`・`TagDictionary`・`DocumentTagWrite/AddTag`**: タグの辞書と書き込み。文書の内容を返さない。
- **`/internal/mcp/*`（MCP のツールの実行口）**: #1611 の段 2 が本件の判定点に乗せる（本件の後。issue の裁定コメント）。
- **BFF・検索・グラフ・Wiki・MCP の判定**: それぞれ既に認可サービスの分岐で判定している。本件は後段（DocumentService）に門を 1 枚足す作業で、BFF の応答は
  AND 合成で変わらない（BFF の判定は DocumentService の判定と同じ述語・同じ分岐）。
- **AST の古い写しの列挙と削除（ADR-0121 決定 3・ADR-0122、#1667）**: planning#696 の裁定の後段。**門の構成を `On` にしない**ことで守る（ADR-0122 決定 4 の暫定手段）。

### 規則 10（この変更で新たに誤りになる自分の記述）

`git grep -n "内容の ABAC\|#1615"`（`docs`・`src`・`scripts`・`deploy`）で引いた。誤りになるもの:
`DocumentReadAccess.cs`・`DocumentReadUseCase.cs`・`GrpcService.cs`・`IDocumentReadScopeSource.cs`・`ListPage/Endpoint.cs`・`ListPage/DocumentPageQuery.cs`・
`ContentAbacGate.cs`（「門を読む判定は無い」）・`Program.cs`（同）・`document_read.proto`（注記）・BFF の `DocumentReadGrpcClient.cs`（注記）・
`docs/operations/operations.md` §内容の ABAC の有効化の門（「門が開いても判定は変わらない」）・`docs/security/security.md`（同）・
`docs/tests/FR-05_abac-access-control.md`（T-39〜T-53 の注記）。凍結記録の IADR-0476・IADR-0481 は日付つき追記で改める。
計画側の ADR 本文・issue 本文は数えに使わない（規則 10 の後半）。

### 規則 11（窓）

門は `closed → open` の 1 方向にしか動かない（ラッチ。IADR-0481 決定 5）。窓は「1 要求の処理の途中で門が開く」だけである。

| 形 | プローブ A（一覧の途中で開く＝制限が**増える**側） | プローブ B（開いた後の要求＝**減る**側は無い。ラッチ） |
| --- | --- | --- |
| 前の端（要求で最初に読んだ状態に固定） | 期待どおり（その要求は閉じたまま一貫。次の要求から開く） | 期待どおり（開いたまま） |
| 後の端（判定のたびに読む） | **期待外れ**（1 つの一覧に旧判定と新判定が混ざる） | 期待どおり |
| 両端（`min`＝どちらかが閉なら閉） | 前の端と同じ（開く向きにしか動かないので `min` ＝ 前の端） | 期待どおり |

→ **前の端（スナップショット）を採る**（両端と同値で、実装が単純）。AC-11 が固定する。

## 配備の順番

- コードの配備では何も変わらない（`ContentAbac:Mode` の既定は Off）。有効化は運用仕様書 §内容の ABAC の有効化の門 の順（所有者の read ポリシーの投入 →
  古い写しの削除 → 検知と通知 → `On`）で、ADR-0122 決定 4 のとおり **#1667 の列挙の口ができて段 2 を済ませるまで `On` にしない**。

## 検証

- DocumentService.Tests・AuthorizationService.Tests・Platform.Bff.Tests・RetrievalService.Tests・GraphService.Tests・McpServer.Tests、両ユニット build、`dotnet format --verify-no-changes`
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`、check-trace-blocks / check-test-spec-coverage / check-test-traceability / check-cross-repo-refs /
  check-plan-id-qualification / check-proto-contracts / check-contract-schema / gen-knowledge-graph --check / check-commit-messages
- 変異（コミット後に当てて赤を確かめ、`git show HEAD:<path> > <path>` で戻す）: ①門が閉じていても ABAC を効かせる ②門が開いていても ABAC を効かせない
  ③開いた枝で所有者の分岐を落とす（コード判定へ戻す／分岐を見ない）④機械の主体の個人資料の除外を落とす ⑤認可サービスの不達で許可へ倒す
  ⑥`/page` の絞り込みを切り出しの後へ回す ⑦門を判定のたびに読む
