---
title: 作業仕様書 — AiSuggestionGenerator を Features/ の外へ出すか決める（#1093）
type: spec
status: done
related_ids:
  - NFR
  - FR-18
  - UC-10
  - ADR-0051
  - ADR-0063
  - ADR-0065
  - ADR-0068
  - IADR-0261
  - IADR-0266
  - IADR-0282
  - IADR-0319
  - IADR-0334
  - IADR-0350
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md (Accepted 2026-08-30) 決定 1・2・5
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md (Accepted 2026-08-30) 決定 1・2・7
  - planning:projects/microservices-platform/07_adr/ADR-0051_ai-suggestion-abac-boundary.md (Accepted) 決定 1・3・4
  - planning:projects/microservices-platform/07_adr/ADR-0063_tag-suggestion-reflection-and-approval-authz.md (Accepted)
related_specs:
  - ./20260830_issue-1062_adr-0068-decision2-mcptool-contracts.md
  - ./20260830_issue-1062_three-level-slices-document-graph.md
  - ./20260823_issue-915_ai-suggestion-generation.md
---

# 作業仕様書: AiSuggestionGenerator の置き場（#1093）

起点: 実装 issue #1093（#1062 の作業仕様書が別 issue として予告していたものの切り出し）。

基点 `origin/develop` = **`3d0a7048`**。`git rev-parse --is-shallow-repository` = **`false`**
（履歴は完全であり、`git log` を出典に引ける）。

## 1. 母集合（着手時に自分で引き直した）

🔴 **#1093 本文の実測（基点 `d3403107`）は転記しない。** 基点が進んでいるため、同じ走査を
`3d0a7048` で自分で回した。**結論は本文と一致したが、一致したこと自体も走査で確かめた。**

### 軸 1 — `AiSuggestionGenerator` を使う「操作」の数（`ADR-0068` 決定 2 の分母）

分母の取り方は `IADR-0319` 決定 1 に従う —— **数えるのは 3 段目の操作フォルダからの実依存だけ**
であり、`Program.cs` の DI 登録・`Tests/`・散文コメント中の言及は数えない。

```console
$ grep -rn "AiSuggestionGenerator" src --include=*.cs
.../GraphService/Domain/EdgeTypeResolver.cs:36                                   … 散文コメント
.../GraphService/Features/AiSuggestions/AiSuggestionGenerator.cs:30              … 定義
.../GraphService/Features/AiSuggestions/Generate/Endpoint.cs:21                  … 散文コメント
.../GraphService/Features/AiSuggestions/Generate/Endpoint.cs:28                  … 実依存（引数注入）
.../GraphService/Program.cs:81                                                   … DI 登録
.../GraphService/Tests/Domain/EdgeTypeResolverTests.cs:121                       … assert の説明文字列
.../GraphService/Tests/Features/AiSuggestions/AiSuggestionGenerationTests.cs:94,98 … テスト
```

| 参照元 | 種別 | 操作として数えるか |
| --- | --- | --- |
| `Features/AiSuggestions/Generate/Endpoint.cs:28` | 実依存（ハンドラの引数注入） | **数える（1 操作）** |
| `Features/AiSuggestions/Generate/Endpoint.cs:21` | 同一ファイルの散文コメント | 数えない（重複） |
| `Program.cs:81`（`AddScoped<AiSuggestionGenerator>()`） | DI 登録 | 数えない |
| `Tests/Features/AiSuggestions/AiSuggestionGenerationTests.cs` | テスト | 数えない |
| `Domain/EdgeTypeResolver.cs:36` / `Tests/Domain/EdgeTypeResolverTests.cs:121` | 散文・説明文字列（依存していない） | 数えない |

**`Approve` / `Reject` / `List` の 3 操作はいずれも使っていない → 使う操作は 1 つ。**

#### 🔴 陽性対照（走査が効いていることの証明）

同じ集約の `AiSuggestionEndpoints`（登録表）へ**同じ走査**を当てると **4 操作**が出る。

```console
$ grep -rn "AiSuggestionEndpoints\." .../GraphService/Features --include=*.cs
Approve/Endpoint.cs   （NotFound / ResolveEndpointsAsync / IsSourceWritableAsync / ToDto）
Generate/Endpoint.cs  （NotFound / ResolveEndpointsAsync / ToDto）
List/Endpoint.cs      （AnyState / ResolveEndpointsAsync / ToDto）
Reject/Endpoint.cs    （NotFound / ResolveEndpointsAsync / IsSourceWritableAsync）
```

**同じ集約・同じ走査で 1 と 4 に割れる。**「参照が見つからないのは走査が壊れているから」ではない。
`AiSuggestionEndpoints` は `ADR-0068` 決定 1（登録表）と決定 2（2 操作以上）の**両方**で 2 段目に
残る —— **判定が一方向に倒れる基準ではない。**

### 軸 2 — 「`Features/` の外（`Domain/` ／ `Infrastructure/ExternalServices/`）へ出せるか」

#1093 の争点はこちらである。**issue 本文が置いた前提 2 つを、どちらも走査で確かめた。**

#### 🔴 前提 1「LLM 境界（外部サービス呼び出し）を持つ」は**実測すると成り立たない**

`AiSuggestionGenerator` は **HTTP を 1 行も持たない。** LLM への送信は `Domain/Ports/` の
`ISuggestionLlmClient` 越しの `llm.ProposeAsync(prompt, ct)` 1 行であり、**実際の外部境界は
`Infrastructure/ExternalServices/LlmGatewaySuggestionClient.cs`（`HttpClient` /
`PostAsJsonAsync("/complete", …)`）に既に居る。**

```console
$ grep -rln "HttpClient\|HttpRequestMessage" Domain/ Features/ Infrastructure/
Infrastructure/ExternalServices/GraphAccessResolver.cs
Infrastructure/ExternalServices/HttpKnowledgeHealthReporter.cs
Infrastructure/ExternalServices/LlmGatewaySuggestionClient.cs
Infrastructure/ExternalServices/StorageContentReader.cs
```

**`Domain/` と `Features/` は 0 件**（`AiSuggestionGenerator` を含む）。
`Infrastructure/ExternalServices/` の 4 件は**すべて Domain ポートの実装（`: I<Port>`）＋ `HttpClient`**
であり、`AiSuggestionGenerator` は**どちらの性質も持たない**（実装するインタフェースが無い）。
**構造が違うものを同じフォルダへ入れる根拠が無い。**

#### 🔴 前提 2「純粋な判断は `Domain/` へ出せる」も成り立たない —— **機械検査が止める**

`AiSuggestionGenerator` は `GraphDbContext`（`GraphService.Infrastructure.Persistence`）を
コンストラクタで受け取り、`db.EdgeTypes` を読み `db.AiSuggestions` へ書いて `SaveChangesAsync` する。
`Domain/` へ置くと **`Domain` → `Infrastructure` の using** が生まれ、
`node scripts/check-unit-dependencies.js` 規則 3③（`IADR-0282` 決定 2）が**違反として止める。**

```console
$ grep -rn "^using GraphService\.\(Infrastructure\|Features\|Common\)" Domain/
（0 件）
```

陽性対照: 同じ走査を `Infrastructure/` へ当てると `Infrastructure/Persistence/EfGraphStore.cs` ほかが
`using GraphService.Domain;` で出る（逆向きは許される）。**`Domain/` の 0 件は走査の壊れではない。**

**純粋な判断はすでに `Domain/` へ切り出されている** —— `SuggestionPrompt.Seal`（封）・
`AuthorizedNode.Authorize`（可視性）・`AiSuggestion.CreateLink` / `CreateTag`（不変条件）。
`AiSuggestionGenerator` に残っているのは **`ADR-0051` 決定 3 が定めた 6 段の順序そのもの**、
すなわち **`Generate` 操作の処理**である。

### 軸 3 — #1014（`ADR-0063`）の宣言ファイル領域との衝突

#1014（AI のタグ提案がタグ辞書の値域に収まらない）は **裁定待ちで着手されていない**
（本文の案 A〜D はいずれも計画側の前提に触れる）。宣言ファイル領域は本文に無いが、
**案 A（生成の段で辞書外を落とす）が採られれば触るのは `AiSuggestionGenerator.PersistAsync`**、
**案 B（承認の段）なら `Features/AiSuggestions/Approve/`** である。

- `Generate/` へ降ろす → 案 A の作業は `Features/AiSuggestions/Generate/` の **1 フォルダに閉じる。**
  案 B とはフォルダが分かれる（`Approve/`）。**どちらでも衝突しない。**
- `Domain/` ＋ `Infrastructure/` へ割る → 案 A は **3 フォルダ**（`Domain/` の照合規則・
  `Infrastructure/` の辞書照会・`Features/` の配線）へ散り、**#1014 の宣言領域が広がる。**

**#1014 が次に触る前提では、`Generate/` へ降ろすほうが領域が狭い。**

## 2. 決定

**`Features/AiSuggestions/AiSuggestionGenerator.cs` → `Features/AiSuggestions/Generate/AiSuggestionGenerator.cs`。**
`ADR-0068` 決定 2 の機械適用（案 1）を採り、**中身は 1 行も割らない**（同 決定 5「純粋な移送に留める」）。

論拠は `IADR-0350` に残す（`ADR-0068` 決定 2 の適用範囲が `Features/` の中に閉じるか、の一般論を含む）。

## 3. 作業（純粋な移送）

| # | 変更 | 内容 |
| --- | --- | --- |
| 1 | `git mv Features/AiSuggestions/AiSuggestionGenerator.cs Features/AiSuggestions/Generate/` | rename として残す |
| 2 | 同ファイルの `namespace` | `GraphService.Features.AiSuggestions` → `…AiSuggestions.Generate`（`IADR-0261` の `<Svc>Service.*` 規約は維持） |
| 3 | `Program.cs` | `using GraphService.Features.AiSuggestions.Generate;` を追加。**DI 登録行（ライフタイム・登録型）は変えない** |
| 4 | `Features/AiSuggestions/Generate/Endpoint.cs` | 冒頭コメントの「生成器は集約直下に居る」を現状へ追随（**コードは変えない**。同一名前空間になるため `using` は不要） |
| 5 | `git mv Tests/Features/AiSuggestions/AiSuggestionGenerationTests.cs Tests/Features/AiSuggestions/Generate/` | `IADR-0334` 決定 3（型を直接呼ぶテストは、その型が定義された本体ファイルのディレクトリへ） |
| 6 | 同テストの `namespace` / `using` | `namespace …Tests.Features.AiSuggestions.Generate;`・`using GraphService.Features.AiSuggestions;` → `….Generate;` |

**触らないもの**: `Domain/EdgeTypeResolver.cs` と `Tests/Domain/EdgeTypeResolverTests.cs` の散文
（パスを書いていない。`IADR-0319` 決定 1 と #1094 本文の扱いに揃える）、`AiSuggestionEndpoints.cs`
（4 操作が使う登録表。軸 1 の陽性対照）、GraphService の他クラスの段（射程外）。

## 4. 受け入れ基準

1. `Features/AiSuggestions/` 直下に残る `.cs` が `AiSuggestionEndpoints.cs`（登録表）**1 件だけ**になる
2. `dotnet build src/knowledge/backend/backend.slnx` が**新規警告なく**通る
   （基点の警告 3 件は Testcontainers の CS0618 であり本件と無関係）
3. **テスト件数が移送前後でプロジェクト単位（skip 込み）で完全に一致する** —— 基点 `3d0a7048` の
   実測は `GraphService.Tests` = **失敗 0 / 合格 279 / スキップ 0 / 合計 279**
   （#1093 本文の 275 は基点 `d3403107` の値であり、develop が進んでいる）
4. `dotnet format src/knowledge/backend/backend.slnx --verify-no-changes` が通る
5. `node scripts/check-unit-dependencies.js` / `check-event-topology.js` / `check-test-traceability.js` /
   `check-doc-links.js` / `check-trace-blocks.js` が通る
6. 判断が `IADR-0350` に残る
