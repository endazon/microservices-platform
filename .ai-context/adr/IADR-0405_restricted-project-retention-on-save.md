---
title: IADR-0405 制限 project は保存で落とせないことだけを実装で閉じ、必須化と ABAC 判定軸への追加は計画へ環流する
type: impl-adr
status: Accepted
related_ids:
  - FR-05
  - FR-06
  - FR-16
  - UC-03
  - SC-05
  - SC-12
  - ADR-0036
  - ADR-0054
  - ADR-0058
  - ADR-0062
  - ADR-0080
  - IADR-0253
  - IADR-0278
  - IADR-0373
  - IADR-0385
  - IADR-0398
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

# IADR-0405: 制限 project は保存で落とせないことだけを実装で閉じ、必須化と ABAC 判定軸への追加は計画へ環流する

- 状態: Accepted
- 日付: 2026-09-06
- 決定者: claude（実装セッション。#1233）

## 起点・関連

- 起点 issue: #1233（#1190 / PR #1231 / IADR-0373 の残射程 1・2）
- 計画 ADR: `AST/ADR-0032` 決定 2・決定 3・フォローアップ 2 ／ `ADR-0036` ／ `ADR-0054` ／
  `ADR-0058`（`doc_scope` 不変性の前例）／ `ADR-0080`（集合値の利用者属性・2026-09-05 Accepted）
- 関連 IADR: `IADR-0373`（一律除外と語彙）／ `IADR-0278`（`doc_scope` 不変性の実装）／
  `IADR-0253`（選言・束縛変数を増やさない）／ `IADR-0385`（集合値の符号化を契約側に 1 つ持つ）／
  `IADR-0398` 決定 8（既存文書が要る検証は端点入口ではない）
- 実装仕様書: `.ai-context/specs/20260906_issue-1233_restricted-project-controls.md`
- 環流先: **planning#557**（`decision-needed`）
- 基点コミット: `origin/develop` `c1dfb1eb`。`git rev-parse --is-shallow-repository` = `false`。

## コンテキストと課題

`IADR-0373` は `AST/ADR-0032` 決定 2 の (2)(3)（割当禁止・CI 検証）と、決定 3 の但し書きが指す
**サービスアカウント実行経路での一律除外**を実装した。残ったのは 2 つである。

1. **文書保存時の `project` 必須化** —— `DocumentAttributes` が静的に検証しているのは
   `confidentiality` 1 つだけで、付与漏れは検出されない。
2. **ABAC の判定軸への `project` 追加** —— 具体判定規則に `project` が無く、主体側に属性が
   付いていても到達可否は変わらない。

#1233 自身が「どちらも既存の未決（選言・`/authz/scope` の値域）に触れる」と警告している。
**その警告が当たっているかを、文言ではなく制御フローで測った。**

### 🔴 実測 1 —— `project` は計画上 **任意**である。`ADR-0036` は必須化を命じていない

必須属性の正本は `07_abac-attribute-model` §文書の基本属性 の表であり、必須は
`confidentiality` / `department` / `owner` / `lifecycle` / `doc_scope` の **5 つ**、
**`project` は「任意」**である。

`ADR-0036` を「必須と定めているか」の側から引き直した（記憶で挙げない）。

| 走査 | 結果 |
| --- | --- |
| `grep -n '`project' ADR-0036_…md` | **1 行**（84 行目）。「利用者属性（`department` / `roles` / `clearance` / `projects` 等）をすべて束縛可能にする」＝**主体側**の属性を挙げた**却下された選択肢** |
| 陽性対照: `grep -c '`doc_scope' ADR-0058_…md` | **14 行**（走査器は生きている） |

🔴 **`ADR-0036` は `project` を必須と定めていない。** #1233 が「`ADR-0036` の必須属性の定義を
確かめる」と書いた先に、必須化の根拠は無かった。

### 🔴 実測 2 —— 計画は必須化の射程を **AST ユニットに限って**いる

`AST/ADR-0032` 決定 2 (1) の原文は「**属性そのものは任意だが、本ユニットが保存する文書に
ついては必須とする**」であり、同 §結果 は付与漏れの受け皿として **CI 検証**を名指ししている
（基盤 DocumentService の保存時 400 ではない）。

### 🔴 実測 3 —— 既存データが通らない。既定値も計画に無い

**DB は照会できない。** 確定できることだけを書く。

- §必須指定と実データの乖離 の実測（`document_svc` **2,368 件**）で、**必須 5 属性のうち
  実データにあるのは `confidentiality` だけ**である。任意属性の `project` がそれを上回ることはない。
- 属性は**全置換**である（`Document.Update` / `Document.UpdateMetadata` はともに
  `Attributes = attributes;`）。SC-05 の属性編集は既存属性をスプレッドして送るため、必須化すると
  `project` を持たない文書の**通常の保存が一斉に 400 になる**。
- 🔴 **「意図した既定」も無い。** `owner` / `department` / `lifecycle` の予約値（`system` /
  `unassigned`）と `doc_scope` の既定（`organization`）は `09_datasource-connectors`
  §システム投入経路 にあるが、**`project` には既定も予約値も定義が無い**。実装が発明すると
  **全文書が `project` キーを持つ**ことになり、「スコープ対象の属性キーを持たない文書は
  除外する」という具体判定規則の入力が静かに変わる。

### 🔴 実測 4 —— 実装で塞げるのは 2 形のうち 1 形だけである

一律除外は `attributes["project"]` の**集合帰属**で効く（`RestrictedProject.IsRestricted`）。
統制を無効化する保存は 2 形ある。

| 形 | 観測できるか |
| --- | --- |
| (a) 新規作成で `project` を付けない | 🔴 **できない。** 属性が無い文書を「制限 project の文書」と判定する材料が文書側に無い。主体側にも無い —— `IADR-0373` 決定 6 の実測で realm に `project` / `projects` を持つ主体は **0 件** |
| (b) 既存の制限文書の更新で `project` を落とす／別値へ変える | **できる。** 現在値が DB にあり、`doc_scope` 不変性が同じ材料で既に動いている |

### 🔴 実測 5 —— 判定軸への追加は #1233 の警告どおり実装裁量を超える。ただし理由は 1 つずれる

`AbacEvaluator.ResolveScope` は文書条件のキーを**列挙していない**
（`foreach (var (key, values) in policy.DocumentConditions ?? [])`）。
**したがって「`project` というキーを扱えない」という制約は無い。** 決められないのは別の 3 点である。

1. 🔴 **`project` を allow ポリシーの文書条件へ載せた瞬間、`project` を持たない文書が全滅する**
   （「スコープ対象の属性キーを持たない文書は除外する」）。これは `ADR-0054` が `doc_scope` に
   ついて書いた「片側だけでは組織ポリシーがこのキーを名指した瞬間に組織文書まで落ちる」と**同型**で
   ある。**つまり残射程 1（必須化）が残射程 2 の先行条件である** —— 2 つは独立していない。
2. 🔴 **主体ごとの突合（`doc.project ∩ ${current_projects}`）には束縛変数の新設が要る。**
   §動的束縛 は「**束縛できる変数は次の 2 つのみとする**」と定め、`AbacEvaluator` も
   「**1 つだけである。増やさない**」（`IADR-0253` 決定 3）と固定している。
   実測: `grep -rn 'current_projects' src --include=*.cs`（submodule 除外）→ **0 件**。
   陽性対照 `current_groups` → **1 件**（`DocumentBodyIntake.cs`）。**走査器は生きている。**
3. 静的ポリシー対（利用者条件 `projects` × 文書条件 `project`）は `ADR-0080`（2026-09-05）が
   意味論（交差が空でない）を与えたが、`MatchesUserConditions` はいまも
   `allowedValues.Contains(userValue)` の **1 キー 1 値照合**であり交差判定を実装していない
   （同 ADR フォローアップ 2。未着手）。

**#1233 の警告は当たっている。ただし「選言（`IADR-0253`）」は主たる障害ではなかった** ——
`IADR-0253` の分岐は既に契約・評価器へ入っており、本件を止めているのは
**必須化の未決**と**束縛変数の語彙**である。**測ってみて理由が 1 つずれた。**

### 🔴 実測 6 —— 前例（`doc_scope`）の形は借りるが、規則は 1 本違う

`ADR-0058` は `doc_scope` を **不変**にしたのであって**必須**にしていない
（`ValidateDocScope` は欠落を拒否しない）。`ValidateDocScopeUnchanged` は **1 本の等値規則**で
①変更 ②削除 ③後からの付与 を閉じる。

🔴 **等値規則をそのまま持ってきてはならない。**

- ③「後からの付与」は `doc_scope` では `ADR-0058`「作成時に確定」に反するので拒否だが、
  `project` では**統制を強める向き**であり、拒否する根拠が計画に無い。
- 等値規則は**制限外の `project` 値の変更・削除まで拒否する**。`project` の再割当を禁じる決定は
  計画に無く、**制限の射程外の文書の挙動を変える**（#1233 の第 3 受け入れ基準に反する）。

## 検討した選択肢

| # | 案 | 評価 |
| --- | --- | --- |
| 1 | **`project` を全文書で必須にする**（#1233 の「拒否される」の素直な読み） | **採らない。** 計画が任意と定め（実測 1）、必須化の射程を AST ユニットに限り（実測 2）、既存データが通らない（実測 3）。**実装が計画を無断で上書きすることになる** |
| 2 | **欠落へ既定値を入れる**（#1233 の「意図した既定が入る」） | **採らない。** 既定値・予約値の定義が計画に無く（実測 3）、発明すると ABAC の判定入力が静かに変わる |
| 3 | **`doc_scope` と同じ等値規則で `project` を不変にする** | **採らない。** 後からの付与と制限外の値の付け替えまで落ち、**制限の射程外の文書の挙動が変わる**（実測 6） |
| 4 | **文書が現に持つ制限値の単調非減少だけを課す**（採用） | 実装で**観測できる唯一の形**（実測 4 (b)）。集合帰属で書け、射程外は 1 件も変えない |
| 5 | ABAC の判定軸へ `project` を加える | **採らない。** 必須化の裁定が先行条件であり、主体ごとの突合には束縛変数の新設が要る（実測 5）。**計画へ環流する** |

## 決定

### 決定 1 — `project` を必須にしない・既定値も入れない。裁定は計画へ環流する

**#1233 の「どちらかを決めて実装する」に対して、「どちらも実装しない」を選ぶ理由を残す。**
選択肢 1・2 はいずれも計画が定めた「任意」を実装が覆す形になる（実測 1〜3）。
**環流先は planning#557**（必須化と判定軸を **1 件**で問う —— 実測 5 (1) のとおり同じ決定の表裏である）。

🔴 **新規作成時の付与漏れは、本 IADR では 1 件も止まっていない**（実測 4 (a)）。
「統制を定めた」と「統制が働いている」を取り違えないため、ここに明記する。

### 決定 2 — 文書が現に持つ制限 `project` の値を落とす保存を 400 で拒否する

`DocumentAttributes.ValidateRestrictedProjectRetained(incoming, current)`。
**単調非減少**（現に持つ制限値が減らないこと）**1 本**の規則で、削除と別値への差し替えの
2 形を閉じる。**集合帰属で書き、否定形（「制限値でない」）では書かない。**

等価性の宣言:

| 観点 | 値 |
| --- | --- |
| status | **400**（`Results.ValidationProblem`。RFC7807）。`doc_scope` 不変性と同じ器 |
| body | `errors` に **1 鍵 1 件**（形 α）。鍵は `RestrictedProject.DocumentKey`（= `project`）。**外れた値を本文へ載せる**（理由を丸めない。`ADR-0062` §結果） |
| position | `FindAsync` の**後**・`DocScopeChangedProblemOrNull` の**直後**・楽観的並行制御（409）の**前**。両方に違反する要求では従来どおり `doc_scope` が出る（宣言順が応答の契約。`IADR-0371` 決定 2） |
| 述語の粒度 | 文書属性キー `project` **のみ**。値は `RestrictedProject.Values` への集合帰属。**制限外の値・欠落・後からの付与は 1 件も新しく拒否しない** |

呼び出し点は **2 つ**（`Documents/Update` と `Documents/UpdateMetadata`）。
母集合は `Document.Update` / `Document.UpdateMetadata` の呼び出し元を走査して引いた ——
3 件のうち `ObsidianSync/Push` は `doc.Attributes` をそのまま渡す（属性を変えない）ため対象外。
**残る 2 件が `doc_scope` 不変性の呼び出し点と一致する。**

### 決定 3 — 判定は Domain に置く。端点の FluentValidation へは出さない

`#1278` / `IADR-0398` は DocumentService の**入力検証**を FluentValidation へ移したが、
**本判定は既存文書の属性が要る**ため `FindAsync` の後ろでしか実行できず、端点入口の入力検証では
ない。`IADR-0398` 決定 8 が `ValidateDocScopeUnchanged` について既に引いた線と**同じ**である。
**sink（`ValidationProblems.FirstViolation`）も規則集合（`DocumentAttributeRules`）も増やしていない。**

### 決定 4 — 語彙 `RestrictedProject` を `Platform.Shared.Contracts` へ移す

到達の除外（`McpServer`・platform）と保存時の統制（`DocumentService`・knowledge）が**別ユニット**に
居り、`src/README.md` の依存規則ではユニット外参照は `Platform.Shared.{Contracts,Infrastructure,Kernel}`
の 3 つしか許されない。**語彙を各側へ写すと、どちらの綴り・どちらの向きが正かを誰も言えなくなる。**

**先例と同じ理由・同じ置き場である** —— `UserAttributeEncoding`（`IADR-0385` / #1243）が
「AuthorizationService と McpServer は互いを直接参照できないため、契約の側に 1 つだけ持つ」と
書いて既にここに居る。`RestrictedProject` の唯一の外部依存（`ServiceAccountAttributeSubset.Tokens`）の
実体は `UserAttributeEncoding.Split` であり、**移設で分割規則が複製されることはない。**

🔴 **`IADR-0373` 決定 1「語彙は 1 箇所」は覆っていない。1 箇所のまま置き場が動いただけ**である。

### 決定 5 — 文書側の判定に主体側の綴り（`projects`）を混ぜない

保存時の統制は `RestrictedProject.DocumentValues`（単数 `project` のみ）を見る。
割当の検査（`AssignedValues`）が両綴りを見るのは `IADR-0373` 決定 2 のとおりだが、**あちらは
主体への割当の話**である。文書の属性辞書に紛れ込んだ複数形を文書の帰属として読むと、
**除外（`IsRestricted`。単数のみ）と保存時の統制の母集合がずれる。**

## 理由

- **選択肢 1・2 を退けたのは実装コストではなく、権限の所在である。** 計画が「任意」と定めた属性を
  実装が必須へ格上げすると、`AST/ADR-0032` §結果 が「基盤側で任意のまま運用されると付与漏れが
  検出されない」と**トレードオフとして受け入れた**判断を、実装が一方的に覆すことになる。
- **選択肢 4 を採るのは、それが「観測できる形」だからである。** 実装が判定できるのは
  「いま制限値を持っている文書から、その値が消えようとしている」ことだけである。
  新規作成の付与漏れは**材料が無い**（実測 4 (a)）——「決められない」のではなく「見えない」。
- **選択肢 3 を退けたのは、陽性対照でしか見分けがつかないからである。** 等値規則と単調非減少は
  「制限文書から値を落とせない」点では**動作で見分けがつかない**。分けられるのは
  「後からの付与が通る」「制限外の値を落とせる」という陽性対照だけである（変異試験 M2 が実測）。
- **決定 4 を移設としたのは、複製が同型の事故を再生産するからである。** `IADR-0385` が
  `UserAttributeEncoding` について書いた理由がそのまま当てはまる。

## 結果

- **良い影響**
  - **1 回の保存で統制が消える形が閉じた。** 属性は全置換であり、従前は SC-05 の保存で
    `project` を落とすだけで一律除外が 1 件も効かなくなった。
  - **制限の射程外は 1 ビットも変わらない。** `project` を持たない文書・制限外の値を持つ文書の
    保存も、サービスアカウント実行での到達可否も従来どおり（陽性対照で固定）。
  - **語彙が 1 箇所のままユニットを跨げるようになった。**
- **悪い影響 / トレードオフ**
  - 🔴 **新規作成時の付与漏れは止まらない**（決定 1）。planning#557 の裁定が下りるまで、
    統制は ①一律除外 ②割当禁止 ③本決定 2 の 3 点であり、**①が唯一の実効的な歯止めである。**
  - 🔴 **`project` の再割当が「制限値から」だけできなくなる。** 制限プロジェクトの文書を
    別プロジェクトへ移す運用が必要になったら、本決定を改める IADR が要る（現時点でその運用は無い）。
  - **契約プロジェクトに可変ユニットの名前（`ai-stock-trading`）が入った。** `IADR-0373` §結果 が
    platform のドメインについて開示したのと同じ性質であり、**露出範囲は platform サービス 1 つから
    全ユニットへ広がった**。構成から読む案は `IADR-0373` 決定 4 のとおり退けたままである（抜け道になる）。
- **フォローアップ**
  1. **planning#557 の裁定**（`project` の必須化と予約値／判定軸へ載せるか）。下りたら
     新規作成側の検証と、必要なら評価器を追随させる。
  2. **`ADR-0080` フォローアップ 2**（`MatchesUserConditions` の交差判定）は本 issue の射程外。
     静的ポリシー対で判定軸を実現する道を採るなら、その実装が先行条件になる。

### 変異試験（実測）

| # | 変異 | 赤くなった試験 |
| --- | --- | --- |
| M1 | 判定を常に `(true, null)` にする | **7 件** —— `RestrictedProjectRetained_{DroppingRestrictedValue,ReplacingWithAnotherValue,NullIncoming}_Fails` / `制限プロジェクトの値を落とす更新は拒否される` / `制限プロジェクトを別の値へ差し替える更新は拒否される` / `メタデータ経路でも制限プロジェクトの値は落とせない` / `拒否は400のRFC7807でありprojectの鍵で返る` |
| M2 | **単調非減少を `doc_scope` と同じ等値規則へ置き換える** | **7 件** —— `RestrictedProjectRetained_{DroppingUnrestrictedValue,AddingRestrictedValueLater,NullCurrent}_Ok` / `SameValueDifferentCasing_Ok`（2 ケース）/ `制限外のプロジェクトの値は従来どおり落とせる` / `制限プロジェクトの後からの付与は通る`。**射程外を巻き込む実装がここで落ちる** |
| M3 | `DocumentValues` を `AssignedValues`（両綴り）へ替える | **1 件** —— `RestrictedProjectRetained_SubjectSpellingOnDocument_IsNotGuarded` |
| M4 | `UpdateMetadata` 側の配線を外す | **1 件** —— `メタデータ経路でも制限プロジェクトの値は落とせない` |
| M5 | `doc_scope` 不変性検査との順序を入れ替える | **1 件** —— `文書スコープ違反と同時なら従来どおり文書スコープが返る` |
| M6 | 比較を `OrdinalIgnoreCase` → `Ordinal` にする | **2 件** —— `RestrictedProjectRetained_SameValueDifferentCasing_Ok`（大文字混じりの 2 ケース） |

🔴 **決定 1（必須化しない）には変異試験の対象が無い。** 「実装しないこと」を落とす変異は
書けない —— 代わりに**陽性対照 2 件**（`projectを持たない文書の更新は従来どおり通る` /
`RestrictedProjectRetained_NoProjectOnEitherSide_Ok`）が、**必須化する実装が入ったら赤くなる**
向きで置いてある。**これは変異試験の代替ではない。開示しておく。**

🔴 **決定 4（語彙の移設）にも固有の変異試験は無い。** 移設は挙動を変えない変更であり、
既存の `ServiceAccountDocumentFilterTests`（`project_を持たない文書はサービスアカウントでも返る` /
`制限外のプロジェクトの文書はサービスアカウントでも返る` を含む）が**移設後も緑であること**が
その主張のすべてである。**「移設し損ねたら落ちる」試験は無い**（落ちるのはコンパイルである）。

## 関連

- Supersedes: なし
- Superseded by: なし（`IADR-0373` の決定は 1 つも改めない。決定 1 の置き場だけを動かした）
