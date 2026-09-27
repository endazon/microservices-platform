---
title: 内容の ABAC の前に消す AST の古い写しを、管理者だけが使える読み取り専用の口で列挙する（#1667）
type: spec
status: done
related_ids: [FR-06, FR-05, FR-08, FR-19, UC-03, SC-05, NFR-09, ADR-0122, ADR-0121, ADR-0119, ADR-0057, ADR-0036, ADR-0060, IADR-0044, IADR-0075, IADR-0459, IADR-0480, IADR-0483, IADR-0484]
author: claude
created: 2026-09-28
updated: 2026-09-28
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0122_ast-stale-copies-deletion-scope-and-switchover-order.md 決定 1・2・3・4・フォローアップ 1・2
  - planning:projects/microservices-platform/07_adr/ADR-0121_owner-read-policy-mandatory-and-content-abac-gate.md 決定 3・4（段 2）
  - planning:projects/microservices-platform/07_adr/ADR-0057_deletion-propagation-scope.md
issue: "#1667"
---

# 仕様書: AST の古い写しを列挙する口（#1667）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-06** / **UC-03**（文書の管理）、FR-05（ABAC）、NFR-09、FR-19（個人資料は対象外）、AST/FR-08（AST の KB の書き手）
- 画面: SC-05（管理者の削除の経路）
- 関連 ADR:
  - **ADR-0122** 決定 1（対象は AST が書いた写しのうち現在のサービスアカウントが所有していないもの。`owner=system` と欠落の両方。見分けは作成の経路と属性。
    個人資料は含めない。遡及しない。消す前に列挙して件数を出し、除いた件数を理由ごとに示す）・決定 2（収集記事も対象。件数と期間を示す）・
    決定 3（切替の後は 0 件の確認。列挙の口は両方の場合で作る）・決定 4（列挙の口ができるまで段 2 を済んだものとしない）・フォローアップ 1・2
  - ADR-0121 決定 3・4（段 2。基盤の管理者が消す）、ADR-0057（削除の伝播範囲）、ADR-0119（機械の作成の `owner`）
- 裁定: planning#696（利用者裁定 2026-09-28）
- 起点 issue: #1667（planning#688 の実装。着手前に止めて planning#696 へ環流した）

## 目的・背景

内容の ABAC を有効にする前に、AST の古い写し（AST の KB の書き手が作ったが、AST の現在のサービスアカウント
`service-account-ai-stock-trading-kb-writer` が所有していない文書）を基盤の管理者が消す。件数は分かっていないので、
消す前に列挙する口が要る。切替（#457）が先に済んだ場合も、同じ口で 0 件を確かめる。

## 対象範囲

- 対象:
  - DocumentService に **`GET /documents/ast-stale-copies`**（`AdminOnly`・読み取り専用）を置く。
  - 見分けの条件を新しい IADR（IADR-0484）に残す。
  - 試験（陽性の対照と対）、`docs/operations/` の Runbook、運用仕様書からの導線、テスト仕様書（FR-06）の行。
- 対象外:
  - **削除そのもの**（稼働クラスタで管理者が SC-05 の管理者の経路で行う。コードは消さない）。
  - **`PutBody` の所有者の判定**（#1679。ADR-0122 フォローアップ 3）。
  - **稼働中のトークン・DB の `owner` の分布の実測**（ADR-0122 フォローアップ 4。Runbook の列挙の結果がその実測を兼ねる）。
  - BFF の中継（管理者はクラスタの内側から叩く。認可サービスの管理 API と同じ形。画面は作らない）。OpenAPI（`docs/api/openapi.yaml` の
    DocumentService 内部口は `POST` / `DELETE` も載せていない部分集合であり、本口も載せない）。

## 着手前に実コードで確かめたこと（MSP `origin/develop` f42e4e36・AST 隣接クローン 40d992e。どちらも shallow なので `git log` は出典にしない）

| # | 事実 | 確かめた箇所 |
| --- | --- | --- |
| 1 | **作成の経路は最初の版の `ChangeNote` で分かれる。** `Document.Create` → `created`、`CreateWithBody` → `created-with-body`、取り込み（`CreateNormalized`）→ `normalized`。版 1 は作成時に必ず積まれる（`Snapshot`） | `Domain/Document.cs` |
| 2 | `created` / `created-with-body` を作る口は `POST /documents` のほかに **個人資料の作成・Obsidian の push・同期の衝突の解決**がある。**3 つとも `PrivateNoteDefaults`（`doc_scope=private-note`）で作る** ⇒ 個人資料の除外で落ちる | `Features/PrivateNotes/Create`・`ObsidianSync/Push`・`SyncConflicts/Resolve` |
| 3 | `POST /documents` を呼ぶのは BFF（人の SC-05）と AST の KB の書き手だけ（MSP の他サービスは呼ばない） | `git grep "PostAsJsonAsync(\"/documents\""` |
| 4 | **`owner=system` は DataSourceService の予約値**（`DataSource.UnresolvedOwner`）。取り込みの経路（`normalized`）で入る | `DataSourceService/Domain/DataSource.cs` L72 |
| 5 | 個人資料の判定は `DocumentScopes.IsPrivateNote`（集合帰属。大小を問わない。キー欠落は組織文書） | `Knowledge.Contracts/Dtos/AiInputExposure.cs` |
| 6 | `project` 属性のキーは `RestrictedProject.DocumentKey`（`project`）。AST は #665（2026-09-03）以降 `project=ai-stock-trading` を必ず付け、別の値は例外で拒む（空白は補う） | `HttpKnowledgeBaseWriter.BuildAttributes`（AST） |
| 7 | **AST の確定報告書**: 属性 `periodKey`・`kind`（`Daily` / `Weekly` / `Monthly`）・`assumptionsVersion`・`confirmedAt`、表題 `確定報告書 {kind} {periodKey}`（#169 から不変）。AST の入れ直し（AST/IADR-0436 決定 2）は「`periodKey`・`kind` が一致し、`project=ai-stock-trading` を持つか、`project` が無く表題が完全一致」を写しとみなす | `ReportKnowledgeMapper`（AST）・AST/IADR-0436 |
| 8 | **AST の収集記事**: 属性 `kind`（`Quote` / `News` / `Disclosure` / `MacroIndicator` / `SupplyDemand` / `SourceStatus`）・`source`・`publishedAt`（ISO 8601 の往復形）・`symbol`（任意）。表題は記事の表題（固定の形は無い） | `KnowledgeBaseWriterSink`・`InformationKind`（AST） |
| 9 | 作成の口の `owner` は主体から入れ直し、主体が無ければ載せない（`WithOwner`）。機械の主体名は `DocumentManageScope.MachineSubject`（`service-account-<clientId>`） | `Features/Documents/Create/Endpoint.cs`・`DocumentManageScope.cs` |
| 10 | 試験の器は InMemory（クラスごとに DB を分ける）。`AdminOnly` は `platform-admin` ロールを要る | `Tests/TestWebApplicationFactory.cs`・`AuthExtensions.cs` |

## 設計

### 1. 見分けの規則（`AstStaleCopyRules.Classify`。純粋関数）

文書ごとに、次の順で見て、最初に外れた理由で**除く**（1 件は 1 つの理由にだけ数える）。全部を通ったものが**対象**。

| 順 | 条件（通る側） | 外れたときの理由 |
| --- | --- | --- |
| 1 | 個人資料でない（`DocumentScopes.IsPrivateNote` が偽） | `private-note` |
| 2 | 最初の版の `ChangeNote` が `created` / `created-with-body`（大小を区別） | `not-created-via-post`（取り込み・版なし） |
| 3 | `project` が無い・空白、または `ai-stock-trading`（大小を区別） | `other-project` |
| 4 | 報告書の形か記事の形（下） | `not-ast-shape` |
| 5 | `owner` が無い・空白、または `system` | `owned-by-current-account`（`service-account-ai-stock-trading-kb-writer`）／`other-owner`（それ以外） |

- **報告書の形**: `kind` ∈ {`Daily`, `Weekly`, `Monthly`} ∧ `periodKey` が空白でない ∧（`project=ai-stock-trading` ∨ 表題 = `確定報告書 {kind} {periodKey}`）。
  AST の入れ直しの写しの判定（事実 7）と同じ。
- **記事の形**: `kind` ∈ {記事の 6 種} ∧ `source` が空白でない ∧ `publishedAt` が日時として読める。
- 🔴 **`owner` は最後にだけ見る**（ADR-0122 決定 1: `owner` では見分けない）。`owner=system` でも取り込みの経路（2）・別の project（3）・形の違う文書（4）は拾わない。

### 2. 口（`GET /documents/ast-stale-copies`）

- 群は `read`（認証）＋ `RequireAuthorization(PlatformAuthPolicies.AdminOnly)`（#629 の「個々の口へ積む」形）。運用者・一般利用者・ロールなしの機械は 403。
- 台帳を `AsNoTracking` で 1 回引く（文書の題・属性・状態・時刻と、最初の版の `ChangeNote` を投影）。書き込まない。
- 応答:
  - `scanned`（見た件数）＝ `targets.total` ＋ `excluded` の合計。
  - `targets`: `total`、`reports`（`count`・`createdFrom` / `createdTo`）、`articles`（`count`・`createdFrom` / `createdTo`・`publishedFrom` / `publishedTo`）。**期間は件数 0 なら null**。
  - `excluded`: 理由 6 つをすべて並べる（0 件も出す。名前は上の表）。
  - `currentAccountReports`: 現在のサービスアカウントが所有する報告書の写しの件数と、同じ `kind`・`periodKey` が 2 件以上ある組（入れ直しの後に重複が出ていないかの確認用）。
  - `items`: 対象の各件（`id`・`title`・`category`〔`report` / `article`〕・`owner`〔`missing` / `system`〕・`hasProject`・`status`・`createdAt`・`updatedAt`・`kind`・`periodKey`・`publishedAt`）。作成の古い順。

### 3. 規則 11（窓）の適用

本件は**時間の窓を扱わない**（期間ごとの `owner` の違いは、`owner` の値の集合〔無し・`system`〕で閉じ、時刻の境界を条件にしない）。
作成時刻で窓を切る形（例: 2026-09-27 より前）は採らない —— 時刻の境界は稼働の着地の時刻に依存し、shallow な履歴では確かめられない。
よって規則 11 の 3 通りの形の表は不要である。

## 母集合（規則 9・10。着手前に自分で引いた）

### 引き方

1. `git grep -n "古い写し\|#1667\|[^I]ADR-0122" -- ':!CHANGELOG.md'`（誤りの側と追随先の両方の語）
2. `git grep -n "\"system\"\|UnresolvedOwner"`（予約値）、`git grep -n "PostAsJsonAsync(\"/documents\""`（作成の口の呼び出し元）
3. `grep` で `created` / `created-with-body` を作る箇所（上の事実 2）

### 結果と扱い

| 箇所 | 内容 | 本件で誤りになるか | 扱い |
| --- | --- | --- | --- |
| `docs/operations/operations.md`（所有者の読み取りのポリシー・門の節） | 「古い写しの削除（…列挙と削除、または切替後の 0 件の確認）を済ませてから `On`」 | ならない（手順の所在が無いだけ） | **Runbook への導線を足す** |
| `.ai-context/adr/IADR-0483`（決定の運用の 1） | 「門を `On` にするのは段 2（#1667 の列挙の口と…）の後」 | ならない | **日付つき追記**で口の所在（IADR-0484）を示す |
| `.ai-context/adr/IADR-0480` L64・L98 | 古い写しの削除は後段 | ならない | 変えない |
| `docs/security/security.md` L235 | 門は古い写しの削除を確かめない | ならない（列挙の口も門ではない） | 変えない |
| `scripts/scripts.repo.test.js` L590・`BffOwnerReadSeedTests`・`OwnerReadPolicySeedTests` の注記 | `system` ＝取り込みの印・AST の古い写し | ならない（`system` の古い写しは在り得る。大半が欠落であることは言っていないだけ） | 変えない |
| `Domain/DocumentBodyIntake.cs` の注記 | 取り込み経路の既定は `system` | ならない | 変えない |
| 確定済みの作業仕様書（#1616・#1664・#1665・#1615・#1676） | 古い写しの削除は射程外 | ならない（凍結記録） | 変えない |

- 規則 10（自分の記述）: 新しい Runbook・IADR・テスト仕様書の行が「削除はコードで行う」「`owner=system` だけ」と読める書き方をしていないかを、書いた後に
  `grep -n "owner=system\|system" ` で引き直した。

## 受け入れ基準

- [x] AC-1: 管理者は `GET /documents/ast-stale-copies` を引け、運用者だけの人・ロールなしの利用者・運用者ロールの機械（AST の KB の書き手）は 403。口は認証の群に居て `AdminOnly` を積む（試験の器は常に認証するので 401 は観測できない。エンドポイントのメタデータで確かめる）。
- [x] AC-2（陽性）: `POST /documents` の経路で作った報告書・記事の形の文書のうち、`owner` が無いもの・`system` のものは対象に入る（`project` あり・なしの両方。本文あり・なしの両方）。
- [x] AC-3（陰性・対照と対）: 同じ形でも、取り込みの経路（`normalized`）の `owner=system`、別の project、個人資料、報告書の表題が違う `project` なし、
  形の違う人の文書（`owner` あり・なし）、現在のサービスアカウントが所有する写し、他の主体が所有する写しは対象に入らず、それぞれの理由に 1 件ずつ数えられる。
- [x] AC-4: `scanned` は対象と除いた件数の合計に等しく、理由は 0 件も並ぶ。報告書・記事の件数と期間（作成の時刻・記事の `publishedAt`）が返る。
- [x] AC-5: 口は書き込まない（呼んだ後も文書の件数・版・属性が変わらず、イベントも出ない）。
- [x] AC-6: 現在のサービスアカウントが所有する報告書の写しに同じ `kind`・`periodKey` が 2 件あれば、その組が返る。
- [x] AC-7: 変異（`owner` の条件を `system` だけにする・作成の経路の条件を外す・`project` の条件を外す・個人資料の除外を外す）はいずれも試験で赤になる。

## 実装 ADR

**IADR-0484**（見分けの条件・理由の順・口の形）を新しく起こす。番号は push 時点の `origin/develop` の最大＋1 で確かめる。

## 検証

- `dotnet test src/knowledge/backend/Services/DocumentService/Tests/DocumentService.Tests.csproj`（全件）
- `dotnet format src/knowledge/backend/backend.slnx --verify-no-changes`（対象プロジェクトに限る）
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- `check-trace-blocks`・`check-test-spec-coverage`・`check-test-traceability`・`check-cross-repo-refs`・`check-plan-id-qualification`・`gen-knowledge-graph --check`・
  `check-commit-messages --range=origin/develop..HEAD`
- 変異 4 件以上をコミット後の状態に当てる（結果は PR 本文）。
