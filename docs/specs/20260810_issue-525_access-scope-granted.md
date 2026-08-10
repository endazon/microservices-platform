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
  - IADR-0139
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

> **★ 結論: 真の乖離は 1 件である。** したがって **C# ↔ OpenAPI の突合検査器は足さない** ——
> `CLAUDE.md`「検査器・規約の追加は**同型の事故が 2 回起きたら**」。**2 回目が起きたら足す**（申し送り）。
> **測ったうえで足さないのであって、面倒だから足さないのではない。**

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
`POST /authz/scope` を実際に叩き、**生の JSON に `granted` が在り、2 状態で値が違う**ことを見る。

**単体テストの `result.Granted` を見るだけでは足りない** —— それは C# のオブジェクトを見ているだけで、
**シリアライズを通っていない**。#525 が言っているのは「**契約から**区別できない」ことである。

## テスト（受け入れ基準の写像）

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 全件遮断（マッチ 0 件）が**本文の `granted:false`** で表せる | **T-17**（新規・生の JSON を読む） |
| 2 | 全件許可（マッチ＋文書条件無し）が**本文の `granted:true` ＋ 空 `allowedFilters`** で表せる | **T-18**（新規・同上） |
| 3 | 2 状態が**契約の上で区別できる**（`allowedFilters` は両方とも空） | T-17 / T-18 の**対**で固定する |
| 4 | 生成型が `granted: boolean`（`?` ではない）になる | `pnpm run codegen` の再生成差分検査 ＋ `typecheck` |

**変異試験**: `openapi.yaml` から `granted` を（a）`required` だけ外す（b）`properties` ごと消す、の 2 通りを
実測し、それぞれ何が落ちるかを記録する。**落ちないなら、その変異はテストが守っていない**ので正直に書く。

## 射程外

- **`/bff/*` 5 端点の認可欠落**（#656）。**別資源**（[[IADR-0139]]）。
- **C# ↔ OpenAPI の突合検査器**。同型 1 回目なので足さない（軸 3）。**2 回目で足す**。
- **`AccessScope`（`Filters` / `GrantsAccess`）を OpenAPI へ載せること**。どの documented path にも現れない
  内部型であり、載せると「クライアントが送ってよい」と読める —— **権限昇格の入口を契約が示唆する**。
- **`SearchRequest.scope` / `AttributeValuesRequest.scope` を OpenAPI へ載せること**。同じ理由で**意図的に載せない**。
