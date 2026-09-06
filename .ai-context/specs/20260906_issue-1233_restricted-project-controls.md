---
title: 作業仕様書 — 制限 project の統制を保存時へ広げ、ABAC 判定軸への追加は計画へ環流する（#1233）
type: spec
status: done
related_ids:
  - FR-05
  - FR-06
  - FR-16
  - UC-03
  - SC-05
  - ADR-0036
  - ADR-0054
  - ADR-0058
  - IADR-0278
  - IADR-0373
  - IADR-0385
  - IADR-0398
  - IADR-0405
author: claude
created: 2026-09-06
updated: 2026-09-06
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0032_mcp-non-exposure-is-enforced-by-attributes-not-the-allowlist.md
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
  - planning:projects/microservices-platform/07_adr/ADR-0058_doc-scope-immutability.md
  - planning:projects/microservices-platform/07_adr/ADR-0080_set-valued-user-attributes-and-match-semantics.md
---

# 作業仕様書: 制限 project の統制を保存時へ広げ、ABAC 判定軸への追加は計画へ環流する

## 1. 起点

- 起点 issue: #1233（#1190 / PR #1231 / `IADR-0373` の残射程 1・2）
- 基点コミット: `origin/develop` `c1dfb1eb`（2026-09-06 取得）。
  `git rev-parse --is-shallow-repository` = **`false`**（`git log` を出典に引ける）。
- submodule: `git submodule update --init src/ai-stock-trading` を実行済み（`75075404`）。
  **走査・件数からは除外する**（他プロジェクトのコードを自リポの規約で数えない）。

## 2. 実測（先に測る。文言から推し量らない）

### 実測 1 — `project` は計画上 **任意属性**である。`ADR-0036` は必須化を命じていない

必須属性の正本は planning `06_technical/07_abac-attribute-model.md` §文書の基本属性 の表である。

| 属性 | 表の「必須」欄 |
| --- | --- |
| `confidentiality` | 必須 |
| `department` | 必須 |
| `owner` | 必須 |
| `lifecycle` | 必須 |
| `doc_scope` | 必須 |
| `project` | 🔴 **任意** |
| `shared_with` / `data_class` / `region` | 任意 |

`ADR-0036` は所有者ベースの裁量制御（`owner` / `shared_with`）の ADR であり、
**`project` を必須と定める記述を 1 件も持たない。**

- 走査（syntax で数える。語で数えない）:
  `grep -n '`project' ADR-0036_ownership-based-discretionary-access.md` → **1 行**（84 行目）。
- その 1 行は「利用者属性（`department` / `roles` / `clearance` / `projects` 等）をすべて
  束縛可能にする」——**主体側**の属性を挙げた**却下された選択肢**である。文書側の必須属性の話ではない。
- 陽性対照: 同じ走査を `ADR-0058`（`doc_scope` 不変性）へ当てると `doc_scope` が複数行で出る。
  **走査器は生きている。**

### 実測 2 — 計画は必須化の射程を **AST ユニットに限って**いる

`AST/ADR-0032` 決定 2 (1) の原文:

> AST が基盤へ保存する文書は、`project` 属性に `ai-stock-trading` を必須で持つ。
> **属性そのものは任意だが、本ユニットが保存する文書については必須とする。**

同 §結果 のトレードオフ:

> `project` 属性を本ユニットの文書について必須とするのは、基盤の属性モデル（任意）への
> 上乗せである。基盤側で「任意」のまま運用されると、付与漏れが検出されない。
> **決定 2 の 3 点目（CI 検証）がその受け皿になる。**

🔴 **付与漏れの受け皿として計画が名指ししているのは CI 検証であって、基盤 DocumentService の
保存時 400 ではない。** 基盤側で全文書に必須化すると、計画が「任意」と定めた属性を実装が
無断で必須へ格上げすることになる。

### 実測 3 — 既存文書は `project` を持たない。必須化は SC-05 の保存を一斉に 400 にする

**DB は照会できない。** 代わりに確定できることだけを書く。

- planning §必須指定と実データの乖離 の実測（`document_svc` **2,368 件**）では、必須 5 属性のうち
  実データにあるのは `confidentiality` だけで、`department` / `owner` / `lifecycle` / `doc_scope` は
  **0 件**である。**必須属性ですら 0 件なのだから、任意属性の `project` がそれを上回ることはない。**
- 属性は**全置換**である（`Document.Update` / `Document.UpdateMetadata` はともに `Attributes = attributes;`）。
  SC-05 の属性編集フォームは既存属性をスプレッドして送るため、必須化すると `project` を持たない文書の
  **通常の保存がすべて 400 になる**。
- 既存 2,368 件へ**遡及付与しない**方針は `ADR-0054` §結果 が確定させている（`doc_scope` について）。
  同じ母集合であり、`project` だけ遡及付与できる理由が無い。

### 実測 4 — 「意図した既定」も計画に無い

計画は既定値を定めた属性については明示している —— `owner` / `department` / `lifecycle` は
予約値 `system` / `unassigned`、`doc_scope` は `organization`（いずれも
`06_technical/09_datasource-connectors.md` §システム投入経路）。
🔴 **`project` の既定値・予約値は計画のどこにも無い。** 実装が `unassigned` 等を発明すると、
**全文書が `project` キーを持つ**ことになり「スコープ対象の属性キーを持たない文書は除外する」
という具体判定規則の入力が変わる（ABAC の挙動が静かに動く）。

### 実測 5 — 実際に塞げる穴は「保存で制限値が落ちること」である

`ServiceAccountDocumentFilter` の除外は `attributes["project"]` の**集合帰属**で効く
（`RestrictedProject.IsRestricted`）。したがって統制を無効化する保存は次の 2 形である。

| 形 | 観測できるか | 本作業の扱い |
| --- | --- | --- |
| (a) 新規作成で `project` を付けない | 🔴 **できない。** 属性が無い文書を「制限 project の文書」と判定する材料がゼロである（主体側にも材料が無い —— `IADR-0373` 決定 6 の実測で realm に `project` / `projects` を持つ主体は **0 件**） | 計画へ環流（必須化の裁定） |
| (b) 既存の制限文書の更新で `project` を落とす／別値へ変える | **できる。** 現在値が DB にあり、`doc_scope` 不変性が同じ材料で既に動いている | 🔴 **本作業で塞ぐ** |

### 実測 6 — 前例（`doc_scope`）の形。ただし規則は 1 本違う

`ADR-0058` は `doc_scope` を **不変（immutable）**にしたのであって**必須**にしていない
（`DocumentAttributes.ValidateDocScope` は 🔴 欠落を拒否しない）。
`ValidateDocScopeUnchanged` は **1 本の等値規則**で ①変更 ②削除 ③後からの付与 を閉じる。

🔴 **`project` に等値規則をそのまま持ってきてはならない。**

- ③「後からの付与」は `doc_scope` では `ADR-0058`「作成時に確定」に反するので拒否だが、
  `project` では**統制を強める向き**であり、拒否する根拠が計画に無い。
- 等値規則は**制限外の `project` 値の変更・削除まで拒否する**。`project` の再割当を禁じる決定は
  計画に無く、**制限の射程外の文書の挙動を変えてしまう**（issue の陰性対照に反する）。

したがって採る規則は **「文書が現に持つ制限値は、保存後も残っていなければならない」**（単調非減少）。
集合帰属で書き、否定形（「制限値でない」）では書かない。

### 実測 7 — 残射程 2（ABAC 判定軸）は実装裁量で決められない

`AbacEvaluator.ResolveScope` は文書条件のキーを**列挙していない**
（`foreach (var (key, values) in policy.DocumentConditions ?? [])`）。したがって
「`project` というキーを扱えない」という制約は**無い**。決められないのは別の 3 点である。

1. 🔴 **`project` を allow ポリシーの文書条件へ載せた瞬間、`project` を持たない文書が全滅する。**
   計画の具体判定規則「スコープ対象の属性キーを持たない文書は除外する」による。
   これは `ADR-0054` が `doc_scope` について「個人資料の側にだけ値を置く設計は採らない ——
   **片側だけでは組織ポリシーがこのキーを名指した瞬間に組織文書まで落ちる**」と書いたのと同型で、
   **`project` を必須化して両側に値を置かない限り成立しない**（＝残射程 1 の裁定が先行条件）。
2. 🔴 **主体ごとの突合（`doc.project ∩ ${current_projects}`）には束縛変数の新設が要る。**
   計画 §動的束縛 は「**束縛できる変数は次の 2 つのみとする**」（`${current_user}` / `${current_groups}`）
   と定める。実装も `AbacEvaluator` で「**1 つだけである。増やさない —— 計画が束縛変数の語彙を
   定めていないため、実装が先取りしない**」（`IADR-0253` 決定 3）と固定している。
   実測: `grep -rn 'current_projects' src --include=*.cs`（submodule 除外）→ **0 件**。
   陽性対照として `current_groups` を同じ走査に掛けると 1 件出る（`DocumentBodyIntake.cs`）。
   **走査器は生きている。**
3. 静的ポリシー対（利用者条件 `projects` × 文書条件 `project`）で代替する道は
   `ADR-0080`（2026-09-05 Accepted）が意味論（交差が空でない）を与えたが、
   `AbacEvaluator.MatchesUserConditions` はいまも `allowedValues.Contains(userValue)` の
   **1 キー 1 値照合**であり、交差判定を実装していない（同 ADR フォローアップ 2。未着手で、
   本 issue の宣言領域にも受け皿の open issue にも入っていない）。

**したがって残射程 2 は「効かせない」ことを記録し、必須化の裁定と一緒に計画へ環流する。**

### 実測 8 — 語彙の置き場（ユニット跨ぎ）

`RestrictedProject` は `McpServer.Domain`（platform サービス）にある。DocumentService は
**knowledge ユニット**であり、`src/README.md` の依存規則では platform サービスを参照できない
（許可されるのは `Platform.Shared.{Contracts,Infrastructure,Kernel}` の 3 つだけ。
`check-unit-dependencies.js` 規則 1）。

**先例がある。** `UserAttributeEncoding`（`Platform.Shared.Contracts/Dtos/`）は
まったく同じ理由で契約プロジェクトへ置かれている —— 同ファイルの原文:

> AuthorizationService と McpServer は互いを直接参照できない（`src/README.md` の依存規則）ため、
> 規則を各側へ写すと**その食い違いをそのまま再生産する**。**契約の側に 1 つだけ持つ。**

`RestrictedProject` の唯一の外部依存は `ServiceAccountAttributeSubset.Tokens` で、その実体は
`UserAttributeEncoding.Split`（既に契約側）である。よって**移設に伴う分割規則の複製は起きない**。

## 3. 決定（本作業で採るもの）

| # | 決定 |
| --- | --- |
| D1 | 🔴 **`project` を必須にしない・既定値も入れない。** 計画が任意と定め（実測 1）、必須化の射程を AST ユニットに限り（実測 2）、既定値を定めていない（実測 4）ため。実装が単独で決めてよい範囲を超える → 環流する |
| D2 | 🔴 **文書が現に持つ制限 project 値を落とす保存を 400 で拒否する**（単調非減少。実測 5 (b)）。制限外の値・欠落は**従来どおり**（集合帰属。否定形で書かない） |
| D3 | 語彙 `RestrictedProject` を `Platform.Shared.Contracts` へ移す（実測 8）。`IADR-0373` 決定 1「語彙は 1 箇所」を**維持する**ための移設であり、決定を覆さない |
| D4 | 判定は `Domain/DocumentAttributes` に置く（端点の FluentValidation ではない）。**既存文書の属性が要る**ため `FindAsync` の後ろでしか実行できず、端点入口の入力検証ではない（`IADR-0398` 決定 8 が `ValidateDocScopeUnchanged` について既に引いた線と同じ） |
| D5 | 残射程 2 は「効かせない」を記録し、D1 の裁定と**同じ 1 件**で計画へ環流する（同じ決定の表裏であるため 2 件に割らない）。**起票済み: planning#557**（`feedback` / `decision-needed`） |

### 等価性の宣言（status / body / position / 述語の粒度）

| 観点 | 本作業が入れるもの |
| --- | --- |
| **status** | **400**（`Results.ValidationProblem`。RFC7807）。`doc_scope` 不変性と同じ器 |
| **body** | `errors` に **1 鍵 1 件**。鍵は `RestrictedProject.DocumentKey`（= `"project"`）。既存の形をそのまま使い、**新しい sink を作らない** |
| **position** | `FindAsync` の**後**・`DocScopeChangedProblemOrNull` の**直後**・楽観的並行制御（409）の**前**。宣言順が応答の契約であり、`doc_scope` 違反と同時なら従来どおり `doc_scope` が出る |
| **述語の粒度** | 文書属性キー `project` **のみ**。値は `RestrictedProject.Values` への**集合帰属**。制限外の値の変更・削除、`project` を持たない文書の保存は**1 件も新しく拒否しない** |

## 4. 実装（変更するファイル）

- `src/platform/backend/Shared/Platform.Shared.Contracts/Dtos/RestrictedProject.cs`（**新規**・移設先）
- `src/platform/backend/Services/McpServer/Domain/RestrictedProject.cs`（**削除**）
- `src/platform/backend/Services/McpServer/Domain/ServiceAccountDocumentFilter.cs`（using 追加）
- `src/platform/backend/Services/McpServer/Domain/ToolPublicationConfigValidator.cs`（using 追加）
- `src/platform/backend/Services/McpServer/Tests/Features/McpClients/McpValidationProblemContractTests.cs`（using 追加）
- `src/knowledge/backend/Services/DocumentService/DocumentService.csproj`（`Platform.Shared.Contracts` を明示参照。`AiAnalysisService` と同じ作法）
- `src/knowledge/backend/Services/DocumentService/Domain/DocumentAttributes.cs`（`ValidateRestrictedProjectRetained`）
- `src/knowledge/backend/Services/DocumentService/Features/Documents/DocumentEndpoints.cs`（`RestrictedProjectDroppedProblemOrNull`）
- `src/knowledge/backend/Services/DocumentService/Features/Documents/Update/Endpoint.cs`
- `src/knowledge/backend/Services/DocumentService/Features/Documents/UpdateMetadata/Endpoint.cs`
- テスト: `DocumentService/Tests/Domain/DocumentAttributesTests.cs`（追記）、
  `DocumentService/Tests/Features/Documents/RestrictedProjectRetentionTests.cs`（新規）
- 記録: `.ai-context/adr/IADR-0405_*.md` ＋索引行、`IADR-0373` へ移設の日付つき追記

## 5. 受け入れ基準

- [x] 【issue 基準 1】制限 project の文書を `project` 無し／別値で保存すると **400**（鍵 `project`）になる
      —— `制限プロジェクトの値を落とす更新は拒否される` / `制限プロジェクトを別の値へ差し替える更新は拒否される` /
      `メタデータ経路でも制限プロジェクトの値は落とせない` / `拒否は400のRFC7807でありprojectの鍵で返る`
- [x] 【issue 基準 1・記録】新規作成時の付与漏れは**観測できない**ことと、必須化の裁定を計画へ環流したことを
      IADR-0405 決定 1・実測 4 に記録した（環流先 **planning#557**）
- [x] 【issue 基準 2】ABAC の判定軸へ `project` を**足さない**ことと、その 3 つの理由（実測 7）を
      IADR-0405 実測 5・選択肢 5 に記録し、**同じ planning#557** で裁定を求めた
- [x] 【issue 基準 3・陰性対照】保存側は `制限外のプロジェクトの値は従来どおり落とせる` /
      `projectを持たない文書の更新は従来どおり通る` / `制限プロジェクトの後からの付与は通る` /
      `同じ制限プロジェクトを同送する機密区分の変更は通る` の 4 件。**到達側は既存の**
      `ServiceAccountDocumentFilterTests.制限外のプロジェクトの文書はサービスアカウントでも返る` /
      `…project_を持たない文書はサービスアカウントでも返る` が引き続き緑（語彙の移設後も 175 件全緑）
- [x] `dotnet build` / `dotnet test` が knowledge・platform 両ユニットで通る
      —— knowledge **2,023 合格 / 0 失敗**（12 アセンブリ。skip 50）、
      platform **1,628 合格 / 0 失敗**（7 アセンブリ。skip 1）。
      🔴 `Platform.Bff.Tests` は **520 合格**（submodule 未初期化なら丸ごと消える群である）
- [x] `dotnet format --verify-no-changes` が両ユニットで通る
- [x] `check-backend-libraries.js` / `check-unit-dependencies.js` / `check-trace-blocks.js` /
      `check-adr-numbering.js` / `check-test-traceability.js` / `REQUIRE_REPO_TESTS=1 scripts.test.js` が通る
- [x] 変異試験 6 種を実施し、赤くなる試験名を記録した（IADR-0405 §変異試験）。
      🔴 **決定 1（必須化しない）と決定 4（語彙の移設）には変異試験の対象が無い**ことを同節で開示した

## 6. 母集合の引き方（規則 9・10）

- **是正の側の文字列で引く**: `RestrictedProject` の型参照を移設前に列挙する。
  `grep -rn 'RestrictedProject\.' src --include=*.cs`（submodule 除外）→ **4 行 / 3 ファイル**
  （`ServiceAccountDocumentFilter.cs:53` / `ToolPublicationConfigValidator.cs:86,90` /
  `McpValidationProblemContractTests.cs:224`）＋ 宣言 1 件。
  🔴 **語で数えない** —— `grep -c RestrictedProject` は同テストの**メソッド名**
  （`..._ForbiddenScopeAndRestrictedProject_...`）を 1 件多く数える。
- **属性を全置換する経路**の母集合: `Document.Update` / `Document.UpdateMetadata` の呼び出し元を
  走査して引く（記憶で挙げない）。実測 3 件のうち `ObsidianSync/Push` は `doc.Attributes` を
  そのまま渡す（属性を変えない）ため対象外。**残る 2 件が `doc_scope` 不変性の呼び出し点と一致する。**
- **導出値は走査ではなく計算し直す**（IADR 番号・件数・テスト件数）。push 直前に測り直す。
