---
title: 作業仕様書 — GraphService の書き込み経路を write アクションで認可する（#993）
type: spec
status: done
related_ids:
  - FR-05
  - FR-17
  - FR-18
  - FR-21
  - UC-10
  - SC-03
  - SC-21
  - ADR-0004
  - ADR-0033
  - ADR-0034
  - ADR-0036
  - ADR-0051
  - IADR-0242
  - IADR-0253
  - IADR-0266
  - IADR-0272
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - "ADR-0036 D-07（書き込み可否は doc.owner ∈ { ${current_user} } で判定する）"
  - "ADR-0036 D-01（単一の評価モデルへ統合する・別レイヤを作らない）"
  - "06_technical/07_abac-attribute-model.md §ポリシー評価モデル（アクションの値域 read/analyze/manage/write）"
  - "ADR-0034 決定 8 の具体化（リンク作成時にリンク先文書への作成者の閲覧権限を検証する）"
  - "ADR-0051 決定 1〜4（AI 提案生成の ABAC 境界）"
issue: "#993"
---

# 作業仕様書: GraphService の書き込み経路を `write` アクションで認可する（#993）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05（ABAC 文書アクセス制御）/ FR-17（ナレッジグラフ）/ FR-18（AI リンク・タグ提案）/ FR-21（認可スコープ契約）
- ユースケース（UC）: UC-10（関連をたどる）
- 画面（SC）: SC-03（文書詳細）/ SC-21（AI 提案一覧）
- 関連 ADR: ADR-0004（ABAC 採用）/ ADR-0033 決定 7（承認済み提案だけが辺になる）/ ADR-0034 決定 2・8 /
  ADR-0036 D-01・D-07 / ADR-0051 決定 1〜4
- 関連 IADR: IADR-0242（ホップごと ABAC の型ゲート）/ IADR-0253 決定 5（`AccessScopeRequest.Action`）/
  IADR-0266（AI 提案の LLM 境界）/ 本作業の IADR-0272
- 計画書リンク: `../../../project-planning/projects/microservices-platform/`（隣接クローン。読み取り専用）

## 目的・背景

GraphService の**書き込み経路が、読み取りの認可スコープ（`action = read`）で判定している**。
「読めるなら書ける」形であり、行為別ポリシーが運用され始めた瞬間に権限の逆転が起きる。

### 着手前の実測（待ち条件が解けていることの確認）

#993 は `blocked:decision` だが、名指しの待ち先は解けている。**自分で確かめた結果**を残す。

| 確かめたこと | 実測（2026-08-23。作業ツリー `claude/implementation-repo-all-issues-hilvbs`） |
| --- | --- |
| `AccessScopeRequest` に `Action` があるか | **ある。** `Platform.Shared.Contracts/Dtos/AccessScopeDto.cs` に `string Action = "read"`（末尾・既定値つき＝非破壊） |
| `/authz/scope` のハードコードが消えたか | **消えている。** `AuthzEndpoints.cs` は `PolicyAction.IsValid(req.Action)` で検証し、不正値は 400。評価は `AbacEvaluator.ResolveScope(req, policies, req.Action)` |
| `PolicyAction` の値域 | **4 値**（`read` / `analyze` / `manage` / `write`）。`Write` は #989 が追加 |
| write ポリシー 0 件のときの write スコープ | **`Granted=false`（全件遮断）。** `AccessScopeContractTests` が否定形＋陽性対照の対で固定している |
| `IADR-0253` の状態 | **`Proposed` のまま。** ただし決定 5 の**実装は着地済み**であり、`Proposed` は決定の効力を停止しない（`.claude/rules/traceability.repo.md`「`Proposed` でも ID としては実在する」） |
| GraphService 側 | **`GraphAccessResolver.cs:30` が `new AccessScopeRequest(userId, userAttrs)`（action 省略＝read）のまま。** 既定値による後方互換は「無改修を許す」だけで、**無改修なら挙動も変わらない** |

**結論: 待ち条件は解けている。** 残っているのは呼び出し側の是正であり、それが本 issue の射程である。

## 母集合（規則 1〜10 に従って自分で引いた）

**「`AccessScopeRequest` を組む箇所」だけでは足りない。** 引くべきは「**認可スコープを取得して書き込みを
許している箇所**」全部である。あり得る形を先に列挙してから引いた。

### 引いた語（`src/` 配下・`obj/` と `ai-stock-trading` を除く）

`new AccessScopeRequest` / `/authz/scope` / `AccessResolver` / `ResolveAsync` / `AccessScopeResponse` /
`GrantsAccess` / `AccessScope` / `MapPost` / `MapPut` / `MapDelete` / `MapPatch` /
`SaveChangesAsync` / `.Add(` / `.AddRange(` / `.Remove(` / `ExecuteUpdate` / `ExecuteDelete`

### 引けたもの（プラットフォーム全体・スコープ解決の発行点）

| # | 発行点 | 現況 | 本作業での扱い |
| --- | --- | --- | --- |
| 1 | `GraphService/.../GraphAccessResolver.cs:30` | action 省略（read） | 🔴 **対象。** action を必須引数化する |
| 2 | `WikiService/.../WikiAccessResolver.cs:22` | action 省略（read） | **対象外（read が正しい）。** WikiService の `ResolveAsync` 呼び出しは `WikiEndpoints.cs:28,68` の 2 か所だけで、**同サービスに `MapPost` / `MapPut` / `MapDelete` は 0 件**（閲覧専用）。実測で確認した |
| 3 | `AiAnalysisService/.../RagOrchestrator.cs:279` | action 省略（read） | **対象外（`analyze` の可能性はあるが裁定が要る）。** 後述「未決事項」 |
| 4 | `Platform.Shared.Infrastructure/.../BffScopeResolver.cs:25` | action 省略（read） | **触れない範囲**（`src/platform/backend/Shared/**`）。**BFF の文書書き込み経路が同じ形を持つ疑い**があり、報告書の台帳へ載せる |
| 5 | `Knowledge.IntegrationTests/.../AbacScopeTests.cs:39,68` | action 省略（read） | 対象外（read の試験。Docker 前提で本セッションでは走らない） |
| 6 | `AuthorizationService.Api.Tests/*` | 発行側の試験 | 対象外（#989 の担当範囲・確定済み） |

### 引けたもの（GraphService 内の**書き込み**経路。永続化を伴う口の全数）

| # | 口 | 永続化 | 現在の認可 | 本作業での扱い |
| --- | --- | --- | --- | --- |
| A | `POST /graph/edges` | `db.Edges.Add` | read スコープの `Granted` ＋両端の read 判定 | 🔴 **write ゲートを足す** |
| B | `POST /graph/suggestions/{id}/approve` | `db.Edges.Add` ＋状態遷移 | 同上（`IsVisibleAsync`） | 🔴 **write ゲートを足す** |
| C | `POST /graph/suggestions/{id}/reject` | 状態遷移・却下回数・指紋 | 同上 | 🔴 **write ゲートを足す** |
| D | `POST /graph/suggestions/generate/{documentId}` | `db.AiSuggestions.AddRange` | read スコープ | **除外**（理由は下） |
| E | `POST/PUT/DELETE /graph/edge-types(/{id})` | `db.EdgeTypes.*` | **ロール（`AdminOnly`）**。`/authz/scope` を呼ばない | **除外**（理由は下） |
| F | `EdgeTypeSeed`（起動時） | `db.EdgeTypes.AddRange` | 要求主体が無い | **除外**（理由は下） |

### 除外したものと、その理由

- **D（提案の生成）**: #993 本文が挙げる 3 経路に入っていない（#915 が本 issue の起票後に足した口である）。
  正しいアクションは **`analyze` である可能性が高い**が、**計画は `analyze` の判定規則を定めていない**
  （`07_abac-attribute-model` は値域に列挙するだけで、規則を書いているのは `read` と `write` の 2 つだけ。
  実測: 計画配下を `analyze` で走査してヒット 4 件、いずれも値域の列挙か画面ルート `/analyze`）。
  **推測で `write` を当てると、生成が全件遮断されて #915 が壊れる。** ADR-0051 は本経路の制約を
  **要求元の read スコープ**だけで書いており（決定 4「各実行が 1 利用者のスコープに閉じていること」）、
  現状が計画に反しているとは読めない。**裁定が要る事項として報告書へ残す。**
- **E（辺の型辞書の管理）**: 認可が `/authz/scope` ではなく**ロール**（`PlatformAuthPolicies.AdminOnly`）である。
  #993 が「対象外: `/authz/scope` 以外の認可（BFF のロール判定など）」と明記している。
- **F（起動時シード）**: 要求主体が存在しない起動処理であり、ABAC の判定対象になり得ない。
- **2（WikiService）**: 上表のとおり書き込み経路を持たない。**`read` で正しい。**
- **3（RagOrchestrator）**: 検索・RAG 回答であり書き込みではない。`analyze` の可能性は D と同じ理由で裁定待ち。

### 規則 10（この変更で新たに誤りになる自分の記述）を引き直した結果

- `GraphService.Api.Tests/AbacUnenforcedAxisTests.cs` の tripwire 冒頭コメント **理由 3**
  （「`/authz/scope` は `PolicyAction.Read` をサーバ側でハードコードしており、`AccessScopeRequest` に
  Action フィールドが無い」）が**事実として誤りになる** → **消さずに書き換える**（テスト本体は理由 1・2 に
  依拠しており有効なので触らない）。
- `docs/functional/FR-05_abac-access-control.md` §閲覧規則の選言（名前つき分岐）と解決アクション の
  「解決アクション」項に、**消費側でどの経路が `write` を渡すか**の記述が無い → 1 行足す。
- `.ai-context/adr/IADR-0242` 冒頭の実測（`Action` フィールドが無い）は**凍結記録**であり、
  後付けで書き換えない（`.claude/rules/traceability.repo.md` §凍結の射程）。
- `.ai-context/specs/20260822_issue-913_user-authored-edges.md` は「両端の到達可能性を検証する」と
  書いており、**本変更でも真のまま**（read の検証は維持する）→ 追記不要。

## 対象範囲

- 対象: `src/knowledge/backend/Services/GraphService/**`（実装・テスト）、
  `docs/functional/FR-05_abac-access-control.md`（1 行追記）、`.ai-context/adr/IADR-0272` ＋索引
- 対象外: 契約（`Platform.Shared.Contracts`）・`AuthorizationService`・BFF・フロントエンド・`scripts/`

## 設計

### どのアクションを割り当てるか —— 計画書の原文から決める

**`write` を採る。`manage` は採らない。**

- 計画 `06_technical/07_abac-attribute-model.md` §ポリシー評価モデル §動的束縛 の判定規則は
  **`write` 許可: `doc.owner ∈ { ${current_user} }`** と定める。ADR-0036 **D-07** が同じ規則を
  「書き込み権限も同じ動的束縛で表現する」として決定している。
- **`manage` には計画側の判定規則が無い。** 計画配下を走査すると `manage` の出現は
  §選言 の表と変更履歴の 2 か所だけで、いずれも「実装の `PolicyAction` が 3 値である」ことの
  **実測の引用**であって、計画が `manage` に意味を与えた記述ではない。
- #993 本文は「`manage`（または相当）」と括弧つきで書いているが、**原文で確かめると `write` である。**
  値域へ `write` を足した裁定（planning#466・2026-08-22）が、まさにこの読み替えを求めている。

### 「1 回の解決を使い回す」ことをやめる —— read と write は別の問い

書き込み経路は **2 つの異なる問い**を持つ。**同じスコープで両方に答えてはならない。**

| 問い | アクション | 根拠 |
| --- | --- | --- |
| 「この辺の両端は、この主体に**見えて**いるか」 | **`read`** | ADR-0034 決定 8 の具体化は「リンク作成時に、リンク先文書に対する作成者の**閲覧権限**を検証する」と書く。**`read` のままが正しい** |
| 「この主体は**書いて**よいか」 | **`write`** | ADR-0036 D-07 |

したがって書き込み経路は **read スコープと write スコープの両方を解決**し、
**到達可能性の検証は read、変更の可否は write** に割り当てる。片方に寄せると、決定 8（read で検証）か
#993（write で判定）のどちらかを壊す。

### 解決したスコープを**使う**（`Granted` だけを見ない）

`writeScope.Granted` だけを見ると、write ポリシーの**文書条件を無視**することになる。
たとえば `confidentiality ∈ {public}` に限った write ポリシーを持つ主体が、`restricted` の文書へ
辺を張れてしまう。**#993 と同じ family の欠陥**（スコープを取得しておいて使わない）である。

そこで **`AuthorizedNode.Authorize(<文書>, writeScope)`** を通す。
述語を直接呼ばないのは IADR-0242 決定 2 の型ゲートの作法に従うためである。

### どの端点に write を要求するか

**起点（source）に要求する。終点（target）には要求しない。**

- ADR-0034 決定 8 の具体化は、**リンク先（target）に課す条件を「閲覧権限」と明示**している。
  target へ write を課すと、「自分の個人資料から会社文書へリンクを張る」という同決定が明らかに
  想定している操作が成立しなくなる。**計画に無い制約を足すことになる。**
- 起点は「リンクが付く側の文書」であり、変更の対象である。ここに write を課すのが最小かつ自然である。
- **対称型（`related`）の扱いは緩い側に残る**（`Edge.Create` が `(min, max)` へ正規化するため、
  A→B と B→A は同じ行になる ⇒ 実質「どちらかの端点に write があれば張れる」）。
  **計画は対称型の書き込み側を定めていない。** 残件として報告する。

### 実装の形

1. **`IGraphAccessResolver.ResolveAsync(HttpContext, string action, CancellationToken)`** —— **`action` は
   既定値を持たない必須引数**にする。🔴 **これが本 issue の構造的な再発防止である** ——
   #993 が起きたのは「既定値がある呼び出し」が**黙って read を意味していた**ためであり、
   既定値を残すと同じ事故が同じ形で再発する。全 8 か所の呼び出しが**アクションを明示**する。
   既定値が無いことを `GraphTypeGateArchitectureTests` にリフレクションで固定する。
2. **`GraphAccessAction`**（`Foundation/Ports/`）に `Read` / `Write` の定数を置く。
   値域の正本は `AuthorizationService` の `PolicyAction` だが、**knowledge → platform のサービス参照は
   禁止**であり、契約 DTO（`Platform.Shared.Contracts`）は本作業の担当範囲外である。
   **綴りがずれた場合は `/authz/scope` が 400 を返し、`GraphAccessResolver` が `Granted=false` へ縮退する**
   （deny 側に倒れる＝緩む向きではない）。
3. 読み取り経路（`GET /graph/{id}` / `/neighbors` / 提案一覧 / 提案生成）は **`Read` を明示**する。
   挙動は変わらない。
4. 書き込み経路 A・B・C は、**既存の read 判定をすべて残したうえで**、変更の直前に write ゲートを置く。
5. 応答は **404 に揃える**（403 にしない）。理由は §採らなかった案。

## 採らなかった案

| 案 | 却下理由 |
| --- | --- |
| `ResolveAsync` の action に既定値 `read` を残す | **#993 を再生産する形である。** 「既定で read」が黙って書き込み経路へ効いたのが本欠陥そのもの |
| 書き込み経路の解決を `read` → `write` へ**置き換える**（1 回だけ解決する） | ADR-0034 決定 8 が求める**閲覧権限の検証**が失われる。write スコープで到達可能性を測ることになり、決定 8 と矛盾する |
| `manage` を使う | 計画に `manage` の判定規則が無い（§どのアクションを割り当てるか） |
| write 拒否を **403** で返す | 主体側の性質なので情報は漏れないが、`GraphEndpoints` / `AiSuggestionEndpoints` は「不可視・不存在はすべて 404」を明文の不変条件として持ち、`GraphEndpointsSecrecyTests` が固定している。**新しい状態コードを足すと、この 1 本道が 2 本になる。** 診断性は落ちるが、秘匿の一本道を優先する |
| 両端点に write を要求する | ADR-0034 決定 8 が target に課す条件は閲覧権限であり、計画に無い制約を足すことになる |
| `writeScope.Granted` だけを見る | 解決したスコープの文書条件を捨てることになる（#993 と同型の欠陥） |

## 受け入れ基準

- [x] `POST /graph/edges` が **write スコープで判定**され、read しか持たない主体は辺を作れない
- [x] `POST /graph/suggestions/{id}/approve` が同上（**状態も遷移しない**＝副作用が無い）
- [x] `POST /graph/suggestions/{id}/reject` が同上（**状態も遷移しない**）
- [x] **陽性対照**: 適切な write スコープを持つ主体は 3 経路すべてを実行できる
- [x] write スコープの**文書条件が適用される**（`Granted` だけを見ていない）
- [x] 読み取り経路は write スコープの有無に影響されない
- [x] ADR-0034 決定 8 の read 検証が残っている（**write を持っていても、見えない文書へは張れない**）
- [x] `ResolveAsync` の `action` に既定値が無いことがテストで固定されている
- [x] tripwire `AbacUnenforcedAxisTests` は**消さず**、理由 3 を現況へ書き換えてある。
      `Dynamic_binding_placeholders_are_NOT_interpreted` は**そのまま維持**
- [x] `dotnet build knowledge/backend/backend.slnx` と GraphService のテストが通る
- [x] 文書検査（trace ブロック / doc-links / adr-numbering / backend-libraries / contract-schema /
      test-traceability / unit-dependencies / doc-status-vocabulary）が通る

## テスト方針

**否定形と陽性対照を必ず対で置く**（対が無いと「常に拒む実装」でも緑になる）。

| 種別 | 何を測るか |
| --- | --- |
| 否定形 | read スコープだけを持つ主体が、辺の作成・承認・却下で**拒まれる**（404）。**承認・却下では状態が pending のまま**であることも測る |
| 陽性対照 | write スコープを持つ主体が、同じ 3 経路を**通せる**（201 / 200 ＋辺が 1 本できる） |
| スコープの適用 | write スコープの**文書条件に合わない起点**では拒まれる（`Granted` だけ見ていたら緑にならない） |
| 交差 | write を持っていても、**read で見えない終点**へは張れない（決定 8 が残っている） |
| 影響なし | 読み取り経路は write スコープが deny でも従来どおり |
| 発行側 | `GraphAccessResolver` が要求本文へ **`action` を実際に載せる**（read / write の 2 値） |
| 構造 | `ResolveAsync` の `action` 引数に既定値が無い（リフレクション） |

**変異試験で検出力を実測する。** 書き込み経路の action 指定を `Read` へ戻す変異を入れ、
何件落ちるかを記録し、**必ず元へ戻して残渣 0 を確認する。**

## 計画書との差異

- 差異: **あり。** ただし本作業が生む差異ではなく、**計画側が値域と規則を確定済みで実装が追随していない**
  部分の是正である。
- **副作用（重要）**: write ポリシーが 1 件も登録されていない環境では、**辺の作成・提案の承認／却下が
  全件 404 になる**（deny-by-default）。これは計画 FR-05「既定は拒否」の正しい帰結であり、
  **配備時に write ポリシーの登録が前提になる**。運用への申し送りとして報告書へ残す。

## 未決事項

1. **`analyze` の判定規則が計画に無い。** 値域には存在するが規則が書かれていないため、
   RAG（`RagOrchestrator`）と提案生成（`generate`）が `read` のままでよいのか判断できない。**裁定が要る。**
2. **対称型（`related`）の書き込み側の端点。** 正規化により「どちらかの端点に write があれば張れる」形に
   なる。計画は対称型の書き込み条件を定めていない。**裁定が要る。**
3. **`BffScopeResolver`（`Platform.Shared.Infrastructure`）** も action 省略のままで、BFF の文書書き込みが
   同じ形を持つ疑いがある。**担当範囲外**のため統括へ返す。
