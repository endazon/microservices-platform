---
title: 作業仕様書 — BFF 認可修正 2 件（#1010 action 必須化・#989 段 3 の BFF 分岐評価）
type: spec
status: done
related_ids: [FR-05, FR-06, FR-19, UC-01, UC-03, SC-03, SC-05, ADR-0004, ADR-0036, IADR-0009, IADR-0044, IADR-0253, IADR-0272]
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
---

# 作業仕様書: BFF 認可修正 2 件（#1010 / #989 段 3 の BFF 分）

> 本書は 2 件の同居を統括の割り当て（宣言ファイル領域が同一）により 1 仕様書へまとめる。
> コミットは分ける（1 issue = 1 論理変更を保つ。fix と feat の 2 コミット＋本書の確定 1 コミット）。

## 走査基準（実測の再現条件）

| 対象 | ref | 備考 |
| --- | --- | --- |
| 実装 `microservices-platform` | 波 0 HEAD `d451ada`（worktree ブランチ `worktree-agent-a735823b5981006c3`） | shallow clone のため `git log` / `git blame` は出典に用いない（`git rev-parse --is-shallow-repository` = `true` を確認済み） |
| 計画 `project-planning` | GitHub `refs/heads/main`（`07_abac-attribute-model.md` blob SHA `cad6300c`） | 隣接クローンは worktree 隔離により `git -C` が使えず、GitHub API で直接読んだ |

## 1. これは「実装に閉じた判断」か

**委任済み・裁定済みである。**

- #1010 は #993（`IADR-0272`）と同型の欠陥の platform 共通版であり、対応の形（action を
  既定値の無い必須引数にする・`Granted` だけを見ず文書条件まで適用する・否定形と陽性対照を
  対で置く）は `IADR-0272` 決定 2・4 と issue #1010 対応案が確定している。
- #989 段 3 は `IADR-0253` 決定 1・6 が確定した方針の消費側適用であり、評価規則
  （分岐内 AND・分岐間 OR・空/旧形式は従来評価）は段 1・2 で実装済みの意味論に揃える。
  写像元は `AbacPageFilter`（WikiService。段 3 の先行実装）。
- 割り当てる action の根拠: 計画 `07_abac-attribute-model` §動的束縛「`write` 許可:
  `doc.owner ∈ { ${current_user} }`」と `ADR-0036` D-07（`manage` は計画に判定規則が無い）。

## 2. 母集合（規則 6・9。引いた結果と除外理由）

走査語: `BffScopeResolver` / `ResolveAsync` / `AccessScopeRequest(`。
走査範囲: `src/*/backend` 全域（パス除外のみ: `obj/` `bin/`、`src/ai-stock-trading`〔別プロジェクトの
submodule。`BffScopeResolver` への参照が無いことは同走査で確認済み〕）。
第 2 軸（規則 5）: `AccessScope\b` / `Matches(`（BFF スコープ型の消費面）。
第 3 軸: `docs/api/openapi.yaml` の `/bff/documents` 系記述（変更禁止領域のため読み取りのみ）。

### 2-a. `BffScopeResolver.ResolveAsync` の呼び出し面（コミット 1 の変更対象）

| # | ファイル:行（走査時点） | 端点の意味 | 指定する action |
| --- | --- | --- | --- |
| 1 | `Knowledge.Bff.Endpoints/SearchBffEndpoints.cs:46`（`POST /bff/search`） | 横断検索（読み取り） | `read` |
| 2 | `Knowledge.Bff.Endpoints/SearchBffEndpoints.cs:105`（`POST /bff/attribute-values`） | 権限内属性値の照会（読み取り） | `read` |
| 3 | `Knowledge.Bff.Endpoints/DocumentBffEndpoints.cs:42`（`GET /bff/documents`） | 一覧（読み取り） | `read` |
| 4 | `Knowledge.Bff.Endpoints/DocumentBffEndpoints.cs:108`（`POST /bff/documents`） | 新規作成（書き込み） | `write` |
| 5 | `Knowledge.Bff.Endpoints/DocumentBffEndpoints.cs:225`（`FetchAuthorizedAsync`） | 共用ヘルパ（読み取り GET 3 口と書き込みプリフライトの両方が呼ぶ） | **呼び出し元で分岐**: 詳細・版履歴・本文 GET は `read`、`ForwardIfInScope`（PUT / publish / archive / DELETE の write グループ全部）は `write` |

### 2-b. 除外した `ResolveAsync` / `AccessScopeRequest(` の一致（理由つき）

| 一致 | 除外理由 |
| --- | --- |
| `WikiService` `WikiAccessResolver` / `IWikiAccessResolver` / `WikiEndpoints` / `TestWebApplicationFactory` | 別サービスの自前リゾルバ。段 3 Wiki は実施済み（仕様書 20260823 §2-c）。閲覧経路のみで action は既定 `read` のままが確定済み |
| `GraphService` `GraphAccessResolver` / `IGraphAccessResolver` / 各 Endpoints / tests | #993（`IADR-0272`）で対応済み（本作業の写像元） |
| `AiAnalysisService` `RagOrchestrator.cs:279` | #448 が同時編集中の領域（RetrievalService / AiAnalysisService の段 3 は同 issue の射程） |
| `NotificationService` `IEmailAddressResolver.ResolveAsync` / `EmailOutboxDispatcher` / `TestDoubles` | 無関係の同名メソッド（メール宛先解決）。認可スコープではない |
| `McpServer` `McpSubjectResolver.ResolveAsync` / `ToolInvocationService` | 無関係の同名メソッド（MCP 主体解決） |
| `AuthorizationService.Api.Tests`（`AccessScopeContractTests` / `AbacEvaluatorTests`） | 契約・評価器側のテスト。発行側は #989 段 5 で対応済み |
| `Knowledge.IntegrationTests/AbacScopeTests` | 契約を直接叩く統合テスト（既定 `read` の従来挙動で正しい。段 5 仕様書 §2-a #6 で「無改修」と確定済み） |
| コメントのみの一致（`AttributeValueDto.cs:15` / `NotificationSubject.cs:9` / `BffPrivateNoteExclusionTests.cs:11` / `BffDocumentWriteRoundtripBenchmark.cs:14`） | 表示テキスト・コード注記であり呼び出しではない（ベンチマークは HTTP 経由で端点を叩くため署名変更の影響を受けない） |

### 2-c. BFF スコープ型（`AccessScope`）の消費面（コミット 2 の変更対象）

走査語: `AccessScope\b` / `Matches(`（`src` 全域・パス除外は 2 と同じ）。

| # | ファイル | 扱い |
| --- | --- | --- |
| 1 | `Platform.Shared.Infrastructure/Foundation/Authz/BffScopeResolver.cs` | **改定**（戻り値を Branches を運べる `BffAccessScope` へ・`Matches` に分岐評価） |
| 2 | `Knowledge.Bff.Endpoints/DocumentBffEndpoints.cs`（`IsManageable`） | **改定**（引数型の追随のみ。判定は `Matches` に委譲済み） |
| 3 | `Knowledge.Bff.Endpoints/SearchBffEndpoints.cs`（`SearchRequest` / `AttributeValuesRequest` への埋め込み 2 箇所） | **改定**（後段へは契約型 `AccessScope` へ写して渡す。**Branches は落ちる** —— 後段 RetrievalService の段 3 は #448 の射程であり、契約 `AccessScope` は Branches を持たない。未移行側が従来の連言で判定する形は段 1・2 の後方互換の扱いと同一）<br>🔴 **［2026-08-28 追記 / #989 段 3］この留保は解除した**（§8） |
| 4 | `Platform.Bff.Tests`（`BffScopeResolverTests` / `BffSearchEndpointTests:82` / `BffDocumentWriteEndpointTests` / `BffTestFactory`） | **追補・追随**（否定形＋陽性対照、スタブの action 別応答） |
| 5 | `Knowledge.Contracts/Dtos/SearchDto.cs` / `AttributeValueDto.cs`（契約の `AccessScope?` メンバ） | **無改修**（契約変更は本作業の宣言領域外。#3 の写しで従来どおりの値が入る） |
| 6 | `Platform.Shared.Contracts/Dtos/AccessScopeDto.cs`（`AccessScope` 定義） | **無改修**（宣言領域外。検索契約が参照し続ける） |

### 2-d. 契約の表現形の軸（段 1 の引き漏らしの再発防止）

`docs/api/openapi.yaml`・orval 生成物・baseline JSON はいずれも**変更禁止**（統括が波末に一括処理）。
本作業は契約（`Platform.Shared.Contracts`）を変えないため再生成は発生しない。散文の乖離は §6 に記録し
報告へ載せる。

## 3. 設計（コミット単位）

### コミット 1（#1010）: `ResolveAsync` の action を既定値の無い必須引数にする

1. `BffScopeResolver.ResolveAsync(IHttpClientFactory, HttpContext, string action, CancellationToken)`。
   **既定値を付けない**（`IADR-0272` 決定 4 と同じ理由 —— 既定値が黙って read を意味していたことが
   本欠陥そのもの）。`AccessScopeRequest(userId, userAttrs, action)` で発行する。
2. 値域の定数は `BffScopeAction`（`Read` / `Write`。`BffScopeResolver.cs` 内）に置く。
   正本は AuthorizationService の `PolicyAction`（`AbacEntities.cs`）だが、共有基盤から
   サービスプロジェクトは参照できないため写しを置く（`GraphAccessAction` と同じ形・同じ理由）。
   綴りがずれても `/authz/scope` が 400 を返し `null`（deny）へ縮退する —— 緩む向きには壊れない。
3. 各呼び出し面へ §2-a の action を明示する。`FetchAuthorizedAsync` は `action` を引数に取り、
   読み取り GET は `read`、`ForwardIfInScope`（write グループ）は `write` で解決する。
   **write 経路は解決を write の 1 回に置き換える**（読み替えの根拠: issue #1010 対応案 2 は
   「`POST /` は write を渡す」であり、`IADR-0272` 決定 2 の read+write 二重解決は
   ADR-0034 決定 8〔リンク先の閲覧権限検証〕というグラフ固有の計画要求に由来する。文書更新に
   相当する計画要求は無く、計画の write 規則（owner ベース）を満たす主体は read の分岐 2
   （所有者ベース）も満たすため、「閲覧できない文書を書き換えられる」逆転は計画上生じない）。
   ステータスは現状の形を保つ: 作成は 403（Forbid）、既存文書への write グループは 404（存在秘匿）。
4. `Granted` だけを見ない: 既存の `Matches`（文書条件の適用）を write スコープに対して
   そのまま使う（`FetchAuthorizedAsync` → `IsManageable` の経路は不変）。

### コミット 2（#989 段 3 BFF）: `Matches` へ名前つき分岐の評価を実装する

1. `BffAccessScope`（`Filters` / `GrantsAccess` / `Branches`。`BffScopeResolver.cs` 内）を新設し、
   `ResolveAsync` の戻り値にする。契約 `AccessScope` は変えない（宣言領域外。検索契約が参照）。
   後段へ渡す箇所は `ToContractScope()` で写す（Branches は運ばれない = 未移行後段は従来評価。
   段 1・2 と同じ後方互換の扱い）。
2. `Matches` の評価規則（`AbacPageFilter.Matches` の写像）:
   - `GrantsAccess == false` → 不一致（deny-by-default。従来どおり）
   - `Branches` が 1 件以上 → **いずれかの分岐のフィルタをすべて満たす文書が一致**
     （分岐内 AND・分岐間 OR）。分岐のフィルタが空 = そのポリシーの範囲で全件許可
   - `Branches` が空/null → 従来どおり `Filters`（連言）で評価（後方互換）
   - `${current_user}` はここで解釈しない（認可サービスが分岐内で束縛済み。`IADR-0253` 決定 3）
3. 🔴 **キー単位 union の再導入をしない。** `IADR-0253` 決定 2 の追記（2026-08-23）が実証した
   反例 —— A `{confidentiality:[internal], department:[hr]}` と B `{confidentiality:[public],
   department:[sales]}` の union が**どちらのポリシー単独も許可しない混成 `(internal, sales)` を
   許す** —— をそのままテスト名・テストデータへ写し、分岐評価が混成を拒否することを固定する。

### コミット 3: 本仕様書の受け入れ基準を実測で埋め `status: done` へ

## 4. 受け入れ基準（実測で埋めた。詳細は §7）

- [x] （#1010）`ResolveAsync` の `action` に既定値が無いことがリフレクションのテストで固定されている
      （`BffScopeResolverTests.ResolveAsync_ActionParameter_HasNoDefaultValue`）
- [x] （#1010）**否定形**: read ポリシーだけを持つ主体（read=許可・write=不許可）で
      `POST /bff/documents` が **403** になる（`Create_WhenSubjectHasOnlyReadPolicy_IsForbidden`）。
      **陽性対照**: write スコープが許可なら 201（`Create_WhenWriteGranted_Succeeds_AndResolvesWriteAction`）
- [x] （#1010）write グループ（PUT / publish / archive / DELETE）は write スコープの文書条件で
      判定され、条件外は 404（`Write_WhenSubjectHasOnlyReadPolicy_Returns404` 4 口・
      `Update_WhenOutOfScope_Returns404` / `Delete_WhenOutOfScope_Returns404` は
      `WriteScopeFilters` での文書条件判定へ書き換え）・条件内は透過（既存の 200/204/409 テスト）
- [x] （#1010）作成経路が `action="write"`、読み取り経路が `"read"` を発行したことを
      スタブの捕捉（`ScopeActionsRequested`）で assert している（`Detail_ResolvesReadAction` ほか）
- [x] （#989 段 3）分岐 OR の正例と混成の負例が対で固定されている
      （`Matches_EvaluatesBranchesAsDisjunction` /
      `Matches_DeniesCrossPolicyMixture_BranchesAreNotKeywiseUnion`。各分岐単独の陽性対照つき）
- [x] （#989 段 3）`Branches` が空/null の応答は従来どおり `Filters` の連言で評価される
      （`Matches_FallsBackToFiltersWhenBranchesAbsent`）
- [x] （#989 段 3）端点経由でも分岐が効く（`GetDetail_WhenBranchAllowsButLegacyFiltersDeny_ReturnsDocument` /
      `GetDetail_WhenNoBranchMatches_Returns404_EvenIfLegacyFiltersWouldAllow`）
- [x] 変異試験 3 種を実測（§7。いずれも変異が当たったことを確認してから復元し全緑へ復帰）
- [x] `dotnet build`（platform / knowledge 両 slnx）緑・`dotnet format --verify-no-changes` 緑・
      変更領域の `dotnet test` 緑（Platform.Bff.Tests 361 passed / 1 skipped〔既存・
      `BffDocumentWriteRoundtripBenchmark` 系の恒常 skip〕。フィルタ実行の内訳は §7）
- [x] `check-bff-authz-docs.js`（BFF 12 ファイル / 56 端点一致）/ `check-backend-libraries.js`
      （新規混入 0）/ `check-commit-messages.js --range d451ada..HEAD` 緑
- [x] `docs/api/openapi.yaml`・orval 生成物・baseline JSON に差分が無い（`git status` で確認。
      散文の乖離の報告は §6）

## 5. テスト配置

BFF スコープ解決の純ロジック・端点テストは従前どおり `Platform.Bff.Tests`
（`BffScopeResolverTests` / `BffDocumentWriteEndpointTests` / `BffTestFactory`）に置く
（既存の慣行。`Platform.Shared.Infrastructure.Tests` には Authz 名前空間の既存テストが無く、
他名前空間は並行トラックが触るため近づかない）。xUnit1051: `Platform.Bff.Tests` は
`migrated:false`（baseline 実測）だが、新設テストは `TestContext.Current.CancellationToken` を渡す。

## 6. 射程外・統括へ返すもの

1. **`docs/api/openapi.yaml` の散文の乖離**（コードは変えない・記述も本作業では触らない）:
   - `POST /bff/documents` の 403 説明「許可ポリシーが無い」→ 実装後は「**write の**許可ポリシーが
     無い」が正確（ステータスコード自体は不変）
   - write グループの 404 説明「スコープ外」→ 実装後は「write スコープ外」が正確（同上）
   - `/authz/scope` 応答の `branches` を BFF が消費し始めたことは openapi の形へ影響しない
2. **検索経路の分岐対応**: `SearchRequest.Scope` / `AttributeValuesRequest.Scope`（契約
   `AccessScope`）は Branches を運べないため、後段（RetrievalService）は従来評価のまま
   （#448 の射程）。BFF 内で `Matches` を使う文書系だけが本作業で分岐対応になる。
   🔴 **［2026-08-28 追記 / #989 段 3］解消した**（§8）。
3. **write ポリシー未配備環境では、対応後に文書の作成・更新系が全件拒否される**
   （deny-by-default の正しい帰結。`IADR-0272` §結果と同じ運用上の注意）。

## 7. 実測記録（2026-08-28）

### 変異試験（いずれも変異が当たったことを diff で確認してから実行し、復元後に全緑を再確認）

| # | 変異 | 落ちたテスト（実測） |
| --- | --- | --- |
| ① | 作成経路の `BffScopeAction.Write` を `Read` へ戻す | **3 件が赤**: `Create_WhenSubjectHasOnlyReadPolicy_IsForbidden` / `Create_WhenWriteGranted_Succeeds_AndResolvesWriteAction`（発行 action の assert）/ `Create_WhenScopeNotGranted_IsForbidden_DenyByDefault` |
| ②a | `Matches` の分岐評価をキー単位 union へ潰す | **3 件が赤**: `Matches_DeniesCrossPolicyMixture_BranchesAreNotKeywiseUnion`（混成 internal×sales が通ってしまう）/ `Matches_EvaluatesBranchesAsDisjunction` / `GetDetail_WhenBranchAllowsButLegacyFiltersDeny_ReturnsDocument` |
| ②b | 分岐を無視して従来評価へ戻す | **5 件が赤**: 上記 2 系に加え `Matches_BranchWithNoFilters_GrantsAll` / `GetDetail_WhenNoBranchMatches_Returns404_EvenIfLegacyFiltersWouldAllow` |

### テスト・検証の実行結果

| 実行 | 結果 |
| --- | --- |
| `dotnet build src/platform/backend/backend.slnx` / `src/knowledge/backend/backend.slnx` | いずれも EXIT=0（knowledge の CS0618〔MinioBuilder〕は既存警告） |
| `dotnet test src/platform/backend/backend.slnx --filter "FullyQualifiedName~Authz\|FullyQualifiedName~Bff"` | AuthorizationService.Api.Tests **15 passed** / Platform.Bff.Tests **361 passed・1 skipped**（skip は既存） |
| `dotnet test src/knowledge/backend/backend.slnx --filter "FullyQualifiedName~Bff\|FullyQualifiedName~Authz"` | AiAnalysisService.Api.Tests **2 passed** / Knowledge.IntegrationTests **3 passed**（Docker 不要分のみが該当。Testcontainers 系は本フィルタに合致せず未実行） |
| `dotnet format <両 slnx> --verify-no-changes` | いずれも EXIT=0 |
| `node scripts/check-bff-authz-docs.js` | OK（BFF 12 ファイル / 56 端点の実効ロールが `x-roles` と一致） |
| `node scripts/check-backend-libraries.js` | OK（新規混入 0。既知残件 11 は baseline 済み） |
| `node scripts/check-commit-messages.js --range d451ada..HEAD` | OK（コミット 3 の前に 2 件で実行し、本コミットの件名も同規約で検査される） |

### 環境の注記

- Docker が無いため Testcontainers 系の統合テストはローカル実行不可。上記フィルタに合致した
  Knowledge.IntegrationTests の 3 件（`AbacScopeTests` 系）は in-process で走り緑。
- AST submodule（`src/ai-stock-trading`）は Platform.Bff のビルドに必要なため
  `git submodule update --init` で pin どおり `9b9c6763` を取得した（変更していない）。

## 8. ［2026-08-28 追記 / #989 段 3］`ToContractScope()` の反転

**本書 §2-c #3 と §6-2 が記録した「後段へは Branches を落とす」という留保を解除した。**

| 項目 | 波 1（本書の当初） | 段 3（#989 消費側 3 サービス） |
| --- | --- | --- |
| 契約 `AccessScope` | `Branches` を持たない | **`List<AccessScopeBranch>? Branches = null` を末尾へ追加**（既定値付き＝非破壊。`AccessScopeResponse.Branches` と同型。`IADR-0122` 決定 2） |
| `BffScopeResolver.ToContractScope()` | `new(Filters, GrantsAccess)`（Branches を落とす） | **`new(Filters, GrantsAccess, Branches)`（運ぶ）** |
| 後段 RetrievalService | 従来評価（連言） | **分岐間 OR で評価** |

**留保の理由が消えたため反転した。** 本書が Branches を落としたのは「**後段が未移行だから**」で
あり（§2-c #3 の理由欄）、**段 3 がまさにその消費側移行である**。落としたままにすると
「BFF は分岐で判定するが後段は従来評価」という食い違いが残り、**検索経路だけが混成
（`IADR-0253` 決定 2 の反例）を許す**。

- 反転は `docs/api/openapi.yaml` に影響しない —— **`AccessScope` はスキーマとして存在しない**
  （`AccessScopeRequest` / `AccessScopeResponse` のみ = `/authz/scope` 端点用）。実測で確認した。
- `scripts/contract-schema-baseline.json` は `--update` した（差分は `AccessScope.Branches` の追加のみ・
  **非破壊判定**）。
- 段 3 の作業仕様書: [`20260828_issue-989-stage3_consumers.md`](20260828_issue-989-stage3_consumers.md)。

