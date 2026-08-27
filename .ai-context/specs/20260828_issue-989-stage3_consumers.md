---
title: 作業仕様書 — #989 段 3 の残り消費側 3 サービス（RetrievalService / AiAnalysisService / GraphService）
type: spec
status: done
related_ids: [FR-03, FR-04, FR-05, FR-17, FR-19, UC-01, UC-02, UC-10, ADR-0004, ADR-0034, ADR-0036, ADR-0043, ADR-0046, IADR-0014, IADR-0151, IADR-0242, IADR-0253, IADR-0259, IADR-0272]
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
  - planning:projects/microservices-platform/07_adr/ADR-0034_knowledge-graph-traversal.md
---

# 作業仕様書: #989 段 3 —— 消費側の残り 3 サービス

> 方針の正本は `IADR-0253`（決定 1・2・3・6）。段の定義は同決定 6。
> 先行実装（写像元）: `AbacPageFilter`（WikiService。段 3 の先行）と
> [`20260828_issue-1010-989-bff_write-action-and-branch-matching.md`](20260828_issue-1010-989-bff_write-action-and-branch-matching.md)
> の `BffScopeResolver.Matches`（波 1）。

## 走査基準（実測の再現条件）

| 対象 | ref | 備考 |
| --- | --- | --- |
| 実装 `microservices-platform` | worktree `/home/user/wt-w2c`（ブランチ `wt-w2c`、起点 `e43e0a9`、clean） | **shallow clone**（`git rev-parse --is-shallow-repository` = `true`）のため `git log` / `git blame` は出典に用いない |
| 計画 `project-planning` | 隣接クローン不使用。契約・評価規則は本リポの実装（段 1・2・5 の着地物）を一次情報とした | |

段 1・2・5 と波 1（BFF）が着地済みであることを、`AccessScopeDto.cs` の `Branches` / `Action`、
`AbacEvaluator` の分岐組み立て、`BffScopeResolver.Matches` の分岐評価で確認した。

## 1. これは「実装に閉じた判断」か

**委任済み・裁定済みである。** 評価規則（分岐内 AND・分岐間 OR・空/旧形式は従来評価・
`${current_user}` は述語で解釈しない）は `IADR-0253` 決定 1・3 が確定し、段 1・2・波 1 で
実装済みの意味論に揃える。本作業は**消費側への適用**であって新しい規則を作らない。

**ただし本段には、着手時点で決まっていなかった論点が 1 つある**（§3）。統括の裁定を得た。

## 2. 母集合（規則 6・9・10。引いた結果と除外理由）

### 2-a. 第 1 軸 —— スコープ消費点

走査語: `AllowedFilters` / `GrantsAccess` / `Branches`。
走査範囲: `src` 全域（パス除外: `obj/` `bin/`、`src/ai-stock-trading`〔別プロジェクトの submodule。
本契約への参照が無いことは同走査で確認済み〕）。**24 ファイルが一致**した。

| # | ファイル | 扱い |
| --- | --- | --- |
| 1 | `Platform.Shared.Contracts/Dtos/AccessScopeDto.cs` | **改定**（`AccessScope` へ `Branches`。末尾・既定 null） |
| 2 | `Platform.Shared.Infrastructure/Foundation/Authz/BffScopeResolver.cs` | **改定**（`ToContractScope()` が Branches を運ぶ。§3） |
| 3 | `Knowledge.Bff.Endpoints/SearchBffEndpoints.cs` | **コメントのみ**（2 箇所。`ToContractScope()` 経由で自動追随） |
| 4 | `RetrievalService/.../Services/HybridSearchService.cs` | **改定**（`BuildFilters` の分岐対応・`HybridSearchOutcome` が分岐を運ぶ） |
| 5 | `RetrievalService/.../Ports/IVectorStore.cs` | **改定**（`ScopeFilter` キャリア化） |
| 6 | `RetrievalService/.../Adapters/QdrantVectorStore.cs` | **改定**（`Should` ＋入れ子 `Must` へ写像） |
| 7 | `RetrievalService/.../Adapters/InMemoryVectorStore.cs` | **改定**（Qdrant と同一意味論） |
| 8 | `RetrievalService/.../Services/GraphExpandingSearchService.cs` | **追随**（段③ へ同じフィルタを渡す。`IADR-0259` 決定 3） |
| 9 | `RetrievalService/.../Endpoints/SearchEndpoints.cs` | **改定**（`/attribute-values` も分岐で絞る） |
| 10 | `AiAnalysisService/.../Services/DataRangeScopeResolver.cs` | **改定**（分岐ごとの交差） |
| 11 | `AiAnalysisService/.../Services/RagOrchestrator.cs` | **無改修**（`resolved` をそのまま渡すのみ。Authorization 伝播〔波 2 / #970〕を壊さない） |
| 12 | `GraphService/.../Services/AbacNodeFilter.cs` | **改定**（`AbacPageFilter` の写像） |
| 13 | `GraphService/.../Services/GraphAccessResolver.cs` | **コメントのみ**（§2-c） |
| 14 | テスト 4 プロジェクト | **追補**（Retrieval / AiAnalysis / Graph / Platform.Bff） |
| 15 | `scripts/contract-schema-baseline.json` | **`--update`**（差分が `AccessScope.Branches` の追加のみであることを確認） |

### 2-b. 除外した一致（理由つき）

| 一致 | 除外理由 |
| --- | --- |
| `WikiService`（`AbacPageFilter` / 同テスト / `WikiEndpointsAbacTests`） | 段 3 実施済み（**写像元**）。無改修 |
| `AuthorizationService`（`AbacEvaluator` / `AccessScopeContractTests` / `AbacEvaluatorTests`） | 段 1・2・5 で着地済みの**発行側**。本作業は消費側であり発行の算出は変えない（決定 2 の据え置きを守る） |
| `Knowledge.IntegrationTests/AbacScopeTests` | 契約を直接叩く統合テスト。既定 `read`・従来挙動が正しい（段 5 仕様書 §2-a #6 で「無改修」確定済み） |
| `Knowledge.Contracts/Dtos/SearchDto.cs` / `AttributeValueDto.cs` | `AccessScope?` を参照するだけ。**型の拡張で自動追随**（無改修） |
| `Platform.Bff.Tests`（`BffSearchEndpointTests` / `BffDocumentEndpointTests` / `BffScopeResolverTests`） | 波 1 の成果物。**`ToContractScope()` の反転に伴う追補のみ**行い、既存の判定は変えない |
| `GraphService/Composable/Steps/`・`GraphDocumentSyncConsumer.cs`・リンク抽出まわり | **並行エージェントの領域**（統括の割り当て）。触らない |
| `docs/api/openapi.yaml`・orval 生成物 | **変更禁止**（統括）。かつ **`AccessScope` はスキーマとして存在しない**（`AccessScopeRequest` / `AccessScopeResponse` のみ = `/authz/scope` 端点用）ため本作業では影響しない（実測） |
| `DriftServiceCoverageTests.cs` の「3 分岐」 | 無関係の同語（ドリフト検査の 3 ケース）。認可ではない |
| `NotificationService` / `McpServer` の `ResolveAsync` | 無関係の同名メソッド（メール宛先解決 / MCP 主体解決） |

### 2-c. 第 2 軸（規則 10）—— 本改定で新たに誤りになる自分の記述

走査語: `3 分岐` / `表現構造が無い` / `Branches (は|を)(運ばれない|落|持たない)` / `未移行` / `#516`。
**是正前の語では捕まらない**ため、**改定後に偽になる主張の側の文字列**で引いた。

| # | 箇所 | 現記述 | 本作業後 |
| --- | --- | --- | --- |
| 1 | `BffScopeResolver.cs` L23-24 / L111-112 / L119 | 「契約型 `AccessScope` は Branches を持たない」「Branches はそこで落ちる」 | **偽になる**（§3 の反転）→ 改定 |
| 2 | `SearchBffEndpoints.cs` L69 / L126 | 「後段は未移行のため契約型（Branches なし）へ写して渡す」 | **偽になる** → 改定 |
| 3 | `GraphAccessResolver.cs` L51 | 「`AccessScopeResponse` に 3 分岐 OR の表現構造が無いため機能しない（#516）」 | **偽になる**（構造は在る）→ 残る制約は「実データの `owner` が 0% 充足」だけ。書き換え |
| 4 | `AbacUnenforcedAxisTests.cs` L30 / L69 | 理由 2「3 分岐 OR を表現する構造が `AccessScopeResponse` に無い」 | **解消**（§6-3）→ 日付つき追記で記録し理由 1 が残ることを明示 |
| 5 | `AccessScopeDto.cs` L40-43 | 「未移行のサービスは挙動が 1 ビットも変わらない」 | **文としては真のまま**だが、本作業で**未移行の消費側が 0 になる**。事実を追記 |

**凍結記録は書き換えない**（`traceability.repo.md`「Superseded / Deprecated な ADR を引用するときの書式」）。
`.ai-context/specs/20260822_*` / `20260823_*`・`.ai-context/adr/IADR-0242` / `IADR-0253` は
確定済み記録であり本文へ後付け注記をしない。**例外は波 1 仕様書
`20260828_issue-1010-989-bff_write-action-and-branch-matching.md` への
`［2026-08-28 追記 / #989］` 形式の経過追記**で、これは統括の裁定による（§3）。

## 3. 🔴 決めたこと —— 契約 `AccessScope` へ `Branches` を足し、`ToContractScope()` を反転する

**着手時の実測で、3 サービスが 2 群に割れることが判明した。**

| サービス | スコープを受け取る型 | `Branches` が届くか |
| --- | --- | --- |
| GraphService | `AccessScopeResponse` | ✅ 届く（`AbacPageFilter` と同型で即実装可） |
| AiAnalysisService | `AccessScopeResponse` → `DataRangeScopeResolver` が契約 `AccessScope` へ畳む | △ 自サービス内までは届くが**後段へ渡す時点で落ちる** |
| RetrievalService | `SearchRequest.Scope` / `AttributeValuesRequest.Scope` = 契約 **`AccessScope`** | ❌ **届かない** |

契約 `AccessScope(Filters, GrantsAccess)` は `Branches` を持たず、波 1 は
`ToContractScope()` で**意図的に落として**いた（波 1 仕様書 §2-c #3 / §6-2）。

**したがって Retrieval / AiAnalysis の段 3 は、契約 `AccessScope` への `Branches` 追加なしには成立しない。**

### 決定（統括の裁定を得た）

1. **`AccessScope` へ `List<AccessScopeBranch>? Branches = null` を足す**（末尾・既定値付き＝非破壊。
   `AccessScopeResponse.Branches` が pos 3 / `required:false` の同型の前例。`IADR-0122` 決定 2）。
2. **`ToContractScope()` を反転し Branches を運ばせる。** 波 1 の留保は「**消費側が未移行だから**」を
   理由にした意図的な先送りであり、**本作業がまさにその消費側移行である**ため反転が正しい。
   反転の記録は波 1 仕様書へ `［2026-08-28 追記 / #989］` で残す。
3. **`docs/api/openapi.yaml` は触らない** —— `AccessScope` はスキーマに無く、影響しない（実測）。
   `contract-schema-baseline.json` のみ `--update` する。

**反転しない案は採らない。** 採ると「RetrievalService は分岐を評価できるが、本番の
`/bff/search` 経路には分岐が来ないので従来評価のまま」となり、**「テストは緑・本番は従来評価」**
（`IADR-0014` が記録した型）を新たに作る。

## 4. 設計

### 4-1. RetrievalService

🔴 **fail-open の footgun を避ける形にする。** 分岐を**任意の追加引数**にすると、渡し忘れた経路が
黙って `Filters`（キー単位 union ＝ **混成を許す側**）へ落ちる。**漏れる向きの縮退**である。
したがって**運搬型 `ScopeFilter`（連言 ＋ 分岐）を 1 つ置き、`IVectorStore` の引数をそれに置き換える**。

```
ScopeFilter(IReadOnlyList<AttributeFilter> Conjunction,
            IReadOnlyList<IReadOnlyList<AttributeFilter>>? Branches = null)
```

- `List<AttributeFilter>?` からの**暗黙変換**を持たせ、既存テストの呼び出し（72 箇所）を壊さない
  —— それらは**連言のみ＝分岐なし**を意味しており、暗黙変換の意味と一致する。
- 本番の呼び出し面（`HybridSearchService` / `SearchEndpoints` / `GraphExpandingSearchService`）は
  **明示的に書き換える**。

**`BuildFilters` の規則**:

| 入力 | 出力 |
| --- | --- |
| `Scope` null / `GrantsAccess=false` | 検索に入らない（従来どおり deny-by-default・fail-closed。`IADR-0012`） |
| `Scope.Branches` が 1 件以上 | 分岐ごとに連言を作り**分岐間 OR**。利用者指定 `AttributeFilters`（FR-03 後方互換）は**その全体と AND** |
| `Branches` が空/null | **従来どおり**（`AttributeFilters` と `Scope.Filters` のキー単位結合）。**1 バイトも変えない** |

**Qdrant への写像**（`Qdrant.Client` 1.18.1。`Filter` は `Should`/`Must`/`MustNot`、`Condition` は入れ子 `Filter` を取れる）:

```
分岐あり: Filter { Must = [ ...利用者条件...,
                            Condition{ Filter = Filter{ Should = [
                                Condition{Filter = Filter{Must = [分岐1の各キー条件]}},
                                Condition{Filter = Filter{Must = [分岐2の各キー条件]}}, ... ] } } ] }
分岐なし: Filter { Must = [...] }（現状のまま）
```

**空の分岐**（フィルタ 0 件 = そのポリシーの範囲で全件許可）は、その分岐だけで全件が通る。
Qdrant では**空の `Must` を持つ入れ子は使えない**ため、**分岐に 1 つでも空があれば選言全体を省く**
（＝ ABAC 由来の制約なし。利用者指定の条件だけを残す）。**これは緩む向きだが正しい** ——
「そのポリシーの範囲で全件許可」の意味そのものである。

### 4-2. AiAnalysisService

`DataRangeScopeResolver.Resolve` の交差を**分岐へ拡張**する（narrowing-only の不変条件を保つ）。

| 入力 | 規則 |
| --- | --- |
| `abac.Granted == false` | 従来どおり `AccessScope([], false)` |
| 分岐なし | **従来の算出そのまま**（`AllowedFilters` とレンジの交差）。既存テストが担保 |
| 分岐あり | **各分岐へ独立にレンジを交差**させる |

分岐ありの詳細:

- 分岐内の同キー → 値集合の積。**積が空ならその分岐を捨てる**（**全体 deny ではない**）
- 分岐が制約しないキーをレンジが指定 → **その分岐へ追加**（安全な narrowing）
- **全分岐が消えたら全体 deny**（`GrantsAccess=false`）
- 🔴 **キー単位 union へ畳まない**（`IADR-0253` 決定 2 の反例。絶対にしない）
- `Filters`（従来面）は**据え置きの算出のまま**運ぶ —— 分岐を持たない後段の互換のため

**Authorization 伝播（波 2 / #970）には触らない。** `RagOrchestrator` の `httpContextAccessor`
まわりは無改修で、`AskAsync` / `AnalyzeAsync` / `AskStreamAsync` の 3 経路とも同じ縮退
（空回答・中立文言。`IADR-0009` 存在秘匿）を保つ。

### 4-3. GraphService

`AbacNodeFilter.Matches(GraphDocument, AccessScopeResponse)` は既に分岐を持つ型を受けている。
**`AbacPageFilter` の写像をそのまま当てる**:

```
Granted == false                          → false（deny-by-default）
Branches が 1 件以上                      → いずれかの分岐の全フィルタを満たせば true（分岐内 AND・分岐間 OR）
Branches が空/null かつ AllowedFilters 空 → true（条件無しの許可）
それ以外                                  → AllowedFilters の連言（従来どおり）
```

**属性キー欠落は不一致（deny 側）を分岐内でも保つ。** `${current_user}` は解釈しない（`IADR-0253` 決定 3）。

ホップごと ABAC（`AuthorizedNode` / `AuthorizedGraphView` / `GraphTraversal`）は
`AbacNodeFilter.Matches` を通すため、**述語を直すと自動的に分岐対応になる**
（`IADR-0242` 決定 2 の型ゲートを迂回しない）。

`GraphAccessResolver` は**スコープ照合をしない**（応答を運ぶだけ）ため**コード変更は不要**。
コメントのみ §2-c #3 の是正を行う。

## 5. 受け入れ基準

- [x] （契約）`AccessScope.Branches` が末尾・既定値付きで追加され、`check-contract-schema.js` が
      **非破壊**と判定し、baseline 差分が `AccessScope.Branches` の追加のみである
- [x] （Retrieval）分岐 OR の正例（A のみ満たす文書と B のみ満たす文書が**両方**返る）と
      **混成の負例**（`(internal, sales)` が返らない）が対で固定されている
- [x] （Retrieval）各分岐単独の**陽性対照**がある（「常に空」の実装を落とす）
- [x] （Retrieval）`Branches` が空/null なら従来どおり（後方互換）
- [x] （Retrieval）`/attribute-values` にも同じ規則が効く（候補と検索の一致。`IADR-0151` 決定 1）
- [x] （AiAnalysis）分岐ごとの交差が固定され、**全分岐 drop のときだけ全体 deny** になる
- [x] （AiAnalysis）混成の負例がある／キー単位 union へ畳んでいない
- [x] （Graph）分岐 OR の正例・混成の負例・後方互換・属性欠落 deny が固定されている
- [x] （Graph）tripwire `AbacUnenforcedAxisTests` を**消さずに**検出対象を更新し、
      **owner で見え方が変わる**ことの陽性対照が新設されている
- [x] 変異試験を各サービス最低 1 種で実測し、**予想と実測の対比**を記録している
- [x] `dotnet build`（platform / knowledge 両 slnx）緑・`dotnet format --verify-no-changes` 緑
- [x] 3 サービスのテスト全緑（件数を記録。**skip は「通った」と数えない**）
- [x] `check-commit-messages.js --range e43e0a9..HEAD` / `check-backend-libraries.js` /
      `check-unit-dependencies.js` 緑
- [x] `docs/api/openapi.yaml`・orval 生成物に差分が無い（`git status` で確認）

## 6. 実測記録（2026-08-28）

### 6-1. 変異試験（いずれも変異が当たったことを `git diff` で確認してから実行し、復元後に全緑を再確認）

| # | サービス | 変異 | 予想 | **実測** |
| --- | --- | --- | --- | --- |
| ① | Retrieval | `InMemoryVectorStore.MatchesFilters` の分岐評価を**キー単位 union へ潰す** | 混成負例が赤 | **4 件が赤**（126 中）: `Search_DeniesCrossPolicyMixture_BranchesAreNotKeywiseUnion` / `KeywordSearch_AppliesTheSameBranchRules` / `ListAttributeValues_AppliesTheSameBranchRules` / `PostSearch_DeniesCrossPolicyMixture_...`。**予想より広い**（混成データを使う 4 経路すべてが検出した） |
| ② | Retrieval | `BuildFilters` の分岐分岐を消し**従来評価へ戻す** | 分岐 OR 正例＋混成負例が赤 | 🔴 **1 件だけが赤**: `PostSearch_DeniesCrossPolicyMixture_EvenThoughKeywiseUnionWouldAllow`。**予想が外れた**（§6-2） |
| ③ | AiAnalysis | 分岐ごとの交差を消し**キー単位 union（従来経路）へ潰す** | 混成負例が赤 | **6 件が赤**（89 中）: 新設した分岐試験 6 件すべて |
| ④ | AiAnalysis | 生き残った分岐の**名前を添字で引く**（実際に書いてしまった欠陥） | 名前の対応試験が赤 | ✅ **ちょうど 1 件**: `Branches_RangeMatchingOnlyOneBranch_KeepsThatBranchWithItsOwnName`。**予想が当たった** |
| ⑤ | Graph | `AbacNodeFilter` の分岐評価を**キー単位 union へ潰す** | 混成負例＋tripwire が赤 | **3 件が赤**（205 中）: `Matches_DeniesCrossPolicyMixture_...` / `AuthorizedNode_Authorize_AppliesBranches` / tripwire の `Branches_are_not_folded_into_a_keywise_union` |
| ⑥ | Graph | 分岐を無視して**従来評価へ戻す** | 分岐試験群が赤 | **7 件が赤**: ⑤ の 3 件 ＋ `Matches_NodeMissingBranchAttribute_IsNotVisible` / `Matches_DoesNotInterpretPlaceholders_InsideBranches` / `Matches_WithSingleBranch_OnlyThatPolicyGrants` / **tripwire の陽性対照 `Owner_attribute_IS_enforced_when_scope_carries_an_owner_branch`** |

### 6-2. 🔴 変異 ② が明かしたこと —— 純ロジック試験は配線を測っていない

**変異 ②（`BuildFilters` が分岐を渡さない）を捕まえたのは端点試験 1 件だけだった。**

- ストア直呼びの試験（`ScopeBranchFilteringTests` の大半）は `BuildFilters` を**通らない**ため、
  配線が切れても緑のままである。
- **分岐 OR の正例（`PostSearch_EvaluatesBranchesAsDisjunction`）も緑のままだった** ——
  分岐を無視すると `AllowedFilters` が空＝全件許可になり、**期待していた 2 件がそのまま返る**からである。
  **正例は「配線が切れた」ことを検出できない。検出したのは負例だけである。**

**教訓**: 消費側の段では「純ロジックの試験」と「配線の試験」を分けて数える。
純ロジックだけを厚くしても、**スコープを渡し忘れた実装は素通りする**。
段 2 の仕様書 §7 が「間接的な指標が全部使えない」と書いたのと同型の注意である。

### 6-3. tripwire `AbacUnenforcedAxisTests` の扱い

| テスト | 分岐対応後 | 扱い |
| --- | --- | --- |
| `Owner_attribute_is_NOT_yet_enforced_...` | **緑のまま**（分岐なし応答 → 後方互換で従来評価） | **残した**。理由の文言を「分岐を持たない応答では働かない」へ改め、赤くなる条件を「後方互換が壊れたとき」に付け替えた |
| `Dynamic_binding_placeholders_are_NOT_interpreted` | **緑のまま**（述語は素の比較のみ） | **残した**（今も正しい不変条件）。分岐形の変種を `AbacNodeFilterTests` へ 1 件追加 |
| （新設）`Owner_attribute_IS_enforced_when_scope_carries_an_owner_branch` | — | **陽性対照。** 分岐を運ぶ応答なら owner で見え方が変わることの証拠。変異 ⑥ が検出 |
| （新設）`Branches_are_not_folded_into_a_keywise_union` | — | **陰性対照。** 変異 ⑤・⑥ の両方が検出 |

**本文コメントの理由 2 は日付つき追記で「解消」と記録し、残る未強制の理由が 1（実データの
`owner` が 0% 充足・owner ポリシー未配備）だけであることを明示した。**
tripwire 自身の指示「**赤くなったら消すのではなく、強制されるようになったことを確かめる形へ
書き換えること**」に従った形である（赤くはならなかったが、理由の 1 つが消えたので書き換えた）。

### 6-4. テスト・検証の実行結果

| 実行 | 結果 |
| --- | --- |
| `dotnet build src/knowledge/backend/backend.slnx` | **EXIT=0**（警告 2 件は既存の `MinioBuilder` CS0618） |
| `dotnet build src/platform/backend/backend.slnx` | **EXIT=0**（警告 0） |
| `dotnet format <両 slnx> --verify-no-changes` | いずれも **EXIT=0** |
| `RetrievalService.Api.Tests` | **126 passed / 0 skipped**（着手時 111 → 分岐試験 13 ＋ 端点試験 2 を追加） |
| `AiAnalysisService.Api.Tests` | **89 passed / 0 skipped**（分岐試験 7 を追加） |
| `GraphService.Api.Tests` | **205 passed / 0 skipped**（分岐試験 10 ＋ tripwire 陽性/陰性対照 2 を追加） |
| `WikiService.Api.Tests`（写像元・退行確認） | **59 passed / 0 skipped** |
| `Platform.Bff.Tests`（契約・`ToContractScope`） | **365 passed / 1 skipped**（skip は既存のベンチマーク系） |
| `AuthorizationService.Api.Tests`（発行側の据え置き確認） | **95 passed / 0 skipped** |
| `check-commit-messages.js --range e43e0a9..HEAD` | OK（4 件すべて規約適合） |
| `check-backend-libraries.js` | OK（新規混入 0。既知残件 9 は baseline 済み） |
| `check-unit-dependencies.js` | OK（csproj 196 / .cs 1841 を走査し違反なし） |
| `check-contract-schema.js` | 更新前: **非破壊 1 件**（`AccessScope.Branches` の追加）→ `--update` 後 OK |
| `check-xunit1051-ratchet.js` | OK |
| `git diff e43e0a9..HEAD -- docs/api/openapi.yaml src/*/frontend src/packages` | **空**（禁止領域に差分なし） |

**Docker が無いため Testcontainers 系の統合テストは実行できない。上表に skip を「通った」と
数えた箇所は無い**（Platform.Bff の 1 skip は既存分であり本作業の対象外）。

### 6-5. 契約 baseline の差分（全文）

```
"Platform.Shared.Contracts.Dtos.AccessScope": {
   "members": {
+    "Branches": { "source": "positional", "type": "List<AccessScopeBranch>?",
+                  "required": false, "position": 2 },
     "Filters":  ...
```

**追加は 1 メンバーのみ・`required:false`・末尾（position 2）＝非破壊判定**である。

## 7. 射程外・統括へ返すもの

1. **SC-09 語彙の乖離**: `POLICY_ACTIONS = ['read','analyze','manage']`（3 値。
   `src/knowledge/frontend/src/features/sc09-admin-abac/types/abacVocabulary.ts` L12）に対し
   契約 `PolicyAction` は **4 値**（`write` あり）。**フロント波 3 の射程**。
   機能欠落であり権限は緩まない（write ポリシーを画面から作れないだけ）。
2. **分岐 ③（shared）の生成・評価**: `shared_with` は属性辞書に載せない（`IADR-0253` 決定 4）ため、
   消費側は自 DB に共有情報を持たない。越境参照の方式は未決であり本作業でも実装しない。
3. **`owner` 属性の実データ充足**（#516 / #451）——分岐評価は入ったが、
   **owner を持つ文書と owner ベースのポリシーが無い間は挙動が変わらない**。
