---
title: 作業仕様書 — AccessScopeResponse の granted を契約へ載せ、deny-by-default と全件許可を区別可能にする（#525）
type: work-spec
status: draft
related_ids:
  - FR-05
  - NFR-09
  - UC-01
  - UC-05
  - ADR-0004
  - IADR-0004
  - IADR-0122
  - IADR-0132
  - IADR-0116
  - IADR-0139
  - IADR-0159
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/07_abac-attribute-model.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - "./20260805_issue-520_openapi-response-required.md"
---

# 作業仕様書: `AccessScopeResponse.granted` を契約へ載せる（#525）

## 起点

- **FR-05**（ABAC アクセス制御）／**UC-01**・**UC-05**／**ADR-0004**
- 起点 issue: **#525**（出所は #520 の監査 §未決事項 1）

## 母集合（自分でファイルから引いた）

規則に従い **2 軸**（`.claude/rules/traceability.md` 規則 5「軸を 1 本で終わらせない」）で引いた。

### 軸 1: issue 番号で引く（引き継ぎ表を機械的に出す）

**#521 で「語ではなく issue 番号で引く」を学んだので、今回は最初からこれを引いた。**

```console
$ grep -rn '#525' --include=*.cs --include=*.ts --include=*.tsx --include=*.yaml \
    --include=*.yml --include=*.md --include=*.json \
    --exclude-dir={node_modules,.git,ai-stock-trading,planning} .
```

| 箇所 | 内容 | 対応 |
| --- | --- | --- |
| `docs/specs/20260805_issue-520_openapi-response-required.md:460` | §未決事項 1。**「#525（起票済み）」と名指しした予告** | **本 PR が回収する**。ただし**この作業仕様書は書き換えない**（確定した過去 PR の記録。改竄にあたる） |
| `docs/adr/IADR-0132_...md:188` | 「独立の issue #525 として起票済み」 | **live 文書なので追随させる**（回収済みへ） |
| `docs/how-to/session-handoff.md:98` | 引き継ぎ表の並び D | 状態を持たない一覧なので変更不要 |

> **★ `#520` の予告は 1 箇所だけだった。** #521 では 8 箇所あったので同じ規模を疑ったが、
> **引いた結果は 3 ヒット・要追随は 1 件**である。**規模を前回から推測せず毎回引く**。

### 軸 2: 計画書の現状を引く（issue 本文は転記しない）

`06_technical/07_abac-attribute-model.md`（`fixed`）§ポリシー評価モデル の**具体判定規則**:

| 計画の記述 | 契約上の状態 |
| --- | --- |
| 「利用者にマッチするポリシーが **1 件も無い場合は全件遮断**する」 | `granted=false` ＋ `allowedFilters=[]` |
| 「マッチしたポリシーに**文書条件が無い場合は（そのポリシーの範囲で）全件許可**する」 | `granted=true` ＋ `allowedFilters=[]` |

**計画は 2 つの状態を明確に分けており、しかも `allowedFilters` は両方とも空である。**
`granted` を落とすと、**契約の上でこの 2 つが同一の応答になる** —— 全件遮断と全件許可が区別できない。
これが #525 の本質であり、**「フィールドが 1 つ足りない」という以上の意味を持つ**。

`ADR-0036` D-04・`ADR-0004` も「deny-by-default は変更しない」と繰り返し確定している。

### 軸 3: C# 契約と OpenAPI の**全数突合**（同型が何件あるかを実測する）

「`granted` だけが漏れているのか、同じ漏れが他にもあるのか」を記憶で答えないため、
使い捨てスクリプトで `components.schemas`（69 件）と `*/Contracts/Dtos/*.cs` の `record`（43 件）を
同名で突き合わせ、プロパティ集合の差分を取った（照合できたのは 40 件）。

> **この 43 / 40 という数は、下表 3 行目のパーサの誤りを含んだ値である。**
> 本体つき record（`record X(...) { ... }`）を切れていなかったため、そのぶん record を取りこぼしていた。
> 検査器として作り直したあとの実測は **record 54 件・同名で照合 49 件**である。
> **数を直さず残しているのではなく、どちらの数も何を測ったものかを書き分けている。**

| スキーマ | 差分 | 判定 |
| --- | --- | --- |
| `SearchRequest` | C# に `scope`・OpenAPI に無し | **意図どおり**。BFF はクライアント指定 Scope を信頼しない（権限昇格の防止）。OpenAPI の説明文が明記している |
| `AttributeValuesRequest` | 同上 | **意図どおり**（同上・#540） |
| `AiAnswerDto` | `}` / `outputTokens` / `answerId` | **スクリプトの誤り**。本体つき record（`record X(...) { ... }`）を正規表現が正しく切れていなかった。手で照合したところ **6 プロパティすべて一致**（`required` にも 6 つ載っている） |
| **`AccessScopeResponse`** | **C# に `granted`・OpenAPI に無し** | **本件。真の乖離** |

**逆向き**（OpenAPI にあり C# 契約に同名が無い 29 件／C# 契約にあり OpenAPI に無い 3 件）も引いた。
3 件はいずれも意図どおりである —— `AttributeFilter` は `allowedFilters.items` へ**インライン展開**されており、
`AccessScope` と `CompletionStreamEvent` は**どの documented path の要求／応答にも現れない**
（前者は BFF がサーバ側で解決して後段へ渡す内部型、後者は SSE で `/complete` は `CompletionApiResponse` を返す）。

> **★ 結論: いま残っている乖離は 1 件である。**

> **［着手中の追記］この節は当初「したがって検査器は足さない（同型 1 回目だから）」で終わっていた。**
> **その判断は 2 つの実測で覆った。**
>
> 1. **「同型 1 回目」が誤りだった。** 数えたのは*いま残っている*乖離であって、*起きた事故*ではない。
>    リポジトリ自身の記録を引き直すと、**契約が実装と食い違ったまま残る事故は 4 回**起きている ——
>    `openapi.yaml` の `Issue #118 監査` 注記 3 か所（パス誤り）・`Issue #506 監査`（`citations` の型誤り）・
>    #520（`required` 欠落）・本件。**いずれも人手の監査で偶然見つかっている。**
>    `CLAUDE.md` の条件は**満たされている**。
> 2. **変異試験で、この PR の OpenAPI 変更が無防備だと分かった**（後述 §変異試験）。
>    C# のシリアライズ試験を書いても、`openapi.yaml` から `granted` を消して 1 件も落ちなかった。
>
> **したがって検査器を足す**（`scripts/check-openapi-dto-drift.js`。[[IADR-0159]] 決定 3）。
> **「測ったうえで足さない」と書いた直後に、測り直して足すことにした**——数え方を間違えていたためである。

### 軸 4: `granted` の**消費側**を全数確認する（契約が直っても実装が見ていなければ意味が無い）

| 消費側 | `Granted` を見ているか |
| --- | --- |
| `BffScopeResolver.ResolveAsync:31` | ✅ `resolved is not { Granted: true }` → `null`（＝閲覧可能なし） |
| `BffScopeResolver.Matches:58` | ✅ `!scope.GrantsAccess` → `false` |
| `AbacPageFilter.Matches:23` / `.Filter:39` | ✅ 両方 |
| `RagOrchestrator`（3 経路） | ✅ `!resolved.Granted` で `EmptyAnswer()` |
| `SearchEndpoints`（RetrievalService） | ✅ `req.Scope is not { GrantsAccess: true }` |

**実装は全数が `Granted` を見ている。** すなわち**本件は契約側だけの欠落**であり、**挙動は変わらない**。

> **★ この軸を引いた副産物として、別の欠陥を 1 件見つけた。** `BffScopeResolver` を呼ぶ
> `/bff/search`・`/bff/attribute-values`・`/bff/analysis/*` の **5 端点に `RequireAuthorization` が無い**
> （fallback policy も Istio の `RequestAuthentication` も無いことを実測）。
> **#656 として起票した。本 PR には含めない** —— 端点の認可は DTO スキーマとは**別の資源**であり、
> 挙動を変えるので独立した PR とテストが要る（[[IADR-0139]] 決定 1）。

## 判断

### 判断 1: `granted` を `properties` と **`required` の両方**へ入れる

[[IADR-0132]] 論点 B の **B1**（既定値つきメンバーも `required` に入れる）に従う。
C# の `bool Granted = false` の既定値は**呼び出し側の引数の既定**であって、シリアライズの省略とは無関係である。
`System.Text.Json` は既定でプロパティを省略しないので、**JSON には必ず出る**。

**`required` に入れないと目的を半分しか達しない。** orval は `required` の無いプロパティを `?` で生成するため、
`granted?: boolean` になり、**読み手は `undefined` を「false と同じ」と扱ってよいか判断できない** ——
まさに #525 が消そうとしている曖昧さがそのまま残る。

### 判断 2: 説明文は**書き換える**（監査メモを消して、意味論を書く）

現在の説明文は「**フィールドの追加は本 issue の範囲外**」という #520 時点の**申し送り**である。
回収した以上、これは**残すと嘘になる**。代わりに**計画の 2 つの判定規則**（全件遮断／全件許可）を
`granted` × `allowedFilters` の組で書き下ろす —— 契約を読むだけで区別が付くようにするのが本件の目的だからである。

### 判断 3: 生成物（orval）を**再生成してコミットする**

`src/orval-bff-only.cjs` が落とすのは `paths` だけで **`components.schemas` は素通りする**
（#520 §未決事項 8 の知見）。したがって `AccessScopeResponse` は `/bff/` 外のスキーマでありながら
**`bff.schemas.ts` に生成されている**（実測: `:247`）。**`granted` を足すと生成差分が出る**ので
`pnpm run codegen` を回してコミットする（CI が再生成差分を検査する）。

> **#655 で同じ手順を 1 度落とした**（`check-bff-authz-docs` は回したが `codegen` を回さず、
> `responses` の追加が生成物へ波及していた）。**同じ轍を踏まない。**

### 判断 4: 挙動は**変えない**。テストは「**線の上に載っていること**」を固定する

実装は全数が `Granted` を見ており（軸 4）、`AbacEvaluatorTests` が**2 つの状態を既に固定している**
（`T-01` = 未マッチ→`Granted=false`＋空、`T-04` = マッチ＋文書条件無し→`Granted=true`＋空）。

**足りないのは「その区別が HTTP の本文に載っている」ことの固定である。**
**単体テストの `result.Granted` を見るだけでは足りない** —— それは C# のオブジェクトを見ているだけで、
**シリアライズを通っていない**。#525 が言っているのは「**契約から**区別できない」ことである。

#### ★ 端点越しに「値」を固定しない（書く前に共有 DB の中身を引いた）

当初は「`POST /authz/scope` を叩いて 2 状態の値を見る」つもりだった。**それは壊れる。**

`TestWebApplicationFactory` は InMemory DB を**固定名 `AuthzTest`** で張っており、プロセス内の全テストで
共有される（同ファイルのコメントが「InMemory DB はプロセス内で名前共有」と明記している）。そして
**既存テストは利用者条件が空のポリシーを複数作っている**（`AuthzManagementEndpointTests` に 4 か所・
`PolicyDryRunValidationTests` の既定）。`AbacEvaluator.MatchesUserConditions` は
**条件が空なら全利用者にマッチする**ので、`granted=false` を端点越しに固定すると**実行順に依存して壊れる**。

したがって分ける:

| 何を | どこで |
| --- | --- |
| 値の対応（`false`/`true` の 2 状態が本文で区別できる） | **決定的なシリアライズ**（DB に触らない） |
| `granted` が実際に本文へ載ること | **端点**（値は主張しない） |

**落ちるのを待たずに、書く前に共有 DB の中身を引いた。**

## テスト（受け入れ基準の写像）

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 全件遮断（マッチ 0 件）が**本文の `granted:false`** で表せる | **T-17**（新規・生の JSON を読む） |
| 2 | 全件許可（マッチ＋文書条件無し）が**本文の `granted:true` ＋ 空 `allowedFilters`** で表せる | **T-18**（新規・同上） |
| 3 | 2 状態が**契約の上で区別できる**（`allowedFilters` は両方とも空） | T-17 / T-18 の**対**で固定する |
| 4 | 生成型が `granted: boolean`（`?` ではない）になる | `pnpm run codegen` の再生成差分検査 ＋ `typecheck` |

## 変異試験（実測。**ここで判断が 1 つ覆った**）

| 変異 | 当初（検査器を入れる前） | 現在 |
| --- | --- | --- |
| `granted` を `properties` から消す | **何も落ちない** | `check-openapi-dto-drift`（`missing-in-openapi`） |
| `granted` を `required` から外す | **何も落ちない** | 同（`missing-in-required`） |
| `AccessScopeResponse` の 2 状態を同じ JSON にする | T-18 | T-18 |
| 検査器を消す・壊す | — | `scripts.repo.test.js` |

**★ 最初に書いたテストは、この PR の変更を 1 つも守っていなかった。**
C# のシリアライズ試験は `AccessScopeDto.cs`（もともと `Granted` を持つ）を見ているだけで、
**`openapi.yaml` を書き戻しても緑のままだった**。生成物の再生成差分検査も止まらない ——
**openapi を変えて再生成すれば両者は一致する**からである。

**「テストを書いた」と「変更が守られている」は別である。** 変異試験を回して初めて気づいた。

### `required` 検査を全数へ当てたら 10 件が赤くなった（測ってから線を引いた）

#525 は `AccessScopeResponse` の資源に閉じており（[[IADR-0139]] 決定 1）、10 件の是正は別の作業なので
**ラチェットで据え置き、#658 へ送った**（リポジトリ既定の方式）。据え置きは応答側 3 件・要求側 7 件で
**是正の安全性が違う**ため、baseline の `_comment` に分けて書いた。

> **★ この数は当初 20 だった。うち 10 件（`DataSourceDto` の全プロパティ）は自分の検査器の偽陽性である。**
> `collectSchemas` が **1 行形式の `required: [a, b]` しか読めておらず**、prettier が折り返した
> 複数行形式を「`required` が無い」と誤報していた。**実データは最初から一致していた。**
>
> **気づいた経緯が重要である** —— 「応答側は安全なのだから直せたのでは、と指摘されうる」と考えて
> **費用を測りに実データを開いた**ところ、`DataSourceDto` の `required` に 10 件とも既に載っていた。
> **レビューを待たずに自分で反論を用意しようとしたことが、自分の誤りを見つけた。**
>
> **教訓: baseline へ入れる前に 1 件ずつ実データを開く。** 検査器が出した一覧を、検査器の正しさを
> 確かめずに債務として記録すると、**存在しない債務が恒久的に残る**。

**応答側 3 件（`ConversionJobDto`）は安いと分かっていて、なお本 PR で直していない。**
試しに直すと**生成物 5 行の差分・`typecheck` 緑**である（実測）。それでも `ConversionJobDto` は
SC-07 の資源であり、`1 issue = 1 PR`（[[IADR-0116]] 規約 1）に反する。**測った費用は #658 へ渡した。**

## 射程外

- **`/bff/*` 5 端点の認可欠落**（#656）。**別資源**（[[IADR-0139]]）。
- **`required` 不一致 20 件の是正**。ラチェットに据え置き、別 issue へ送る。
- **型の不一致の検査**。C# の `List<X>` と OpenAPI の `array`/`$ref` は 1 対 1 でなく、
  判定器を書くと誤検出の保守コストが上回る（[[IADR-0159]] 決定 3）。**#506 の型誤りは捕まらない。**
- **`AccessScope`（`Filters` / `GrantsAccess`）を OpenAPI へ載せること**。どの documented path にも現れない
  内部型であり、載せると「クライアントが送ってよい」と読める —— **権限昇格の入口を契約が示唆する**。
- **`SearchRequest.scope` / `AttributeValuesRequest.scope` を OpenAPI へ載せること**。同じ理由で**意図的に載せない**。
