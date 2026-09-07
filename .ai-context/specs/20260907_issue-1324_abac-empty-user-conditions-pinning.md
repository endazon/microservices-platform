---
title: 「利用者条件が空＝全利用者にマッチ」という認可規則を両方向の変異で固定する（#1324）
type: spec
status: done
related_ids: [FR-05, FR-09, ADR-0004, ADR-0036, IADR-0253, IADR-0335, IADR-0384]
author: Claude（実装）
created: 2026-09-07
updated: 2026-09-07
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0004_authorization-abac.md
  - planning:projects/microservices-platform/07_adr/ADR-0036_abac-read-write-rules.md
---

# 仕様書: 「利用者条件が空＝全利用者にマッチ」を両方向で固定する（#1324）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05（認可・ABAC）／FR-09（属性辞書・ポリシー管理）
- 計画 ADR: ADR-0004（ABAC）／ADR-0036（read / write 規則。D-01 が所有者ベース read を定める）
- 実装 ADR: [[IADR-0253]]（名前つき分岐・束縛）／[[IADR-0335]]（Wiki の同型欠陥）／[[IADR-0384]]（不在フィルタ）
- issue: #1324。文脈として PR #1320 と #1318（**#1318 は閉じない**）

## 射程

🔴 **試験の追加だけを行う。`AbacEvaluator` の挙動は 1 行も変えない。**
寛容則（利用者条件が空なら全利用者にマッチ）は FR-05 / ADR-0036 D-01 上**意図的**であり、
所有者ベース read を成り立たせている当の規則である。**是正ではなく固定が目的である。**

**#1318 欠陥 B（Retrieval が呼び出し側の自称 Scope を検証しない）は射程外**（裁定が要る）。
**#1323（`user_attributes` が `clearance`/`department` しか運ばない）も射程外**（運搬側の話であり、
本件は判定側の意味論の固定である）。

## 🔴 依頼文の前提のうち 1 つが崩れている —— 等価変異

依頼は「`if (conditions is null) return false;` を入れても全 1628 本が緑＝**無試験**」を前提にしていた。
**緑になることは実測で再現した**（下記 M-A）が、**その理由は「無試験」ではない。**

`conditions` は**到達可能な経路で null になり得ない**:

| 根拠 | 位置 |
| --- | --- |
| `public Dictionary<string, List<string>> UserConditions { get; private set; } = [];`（**非 null 型**） | `AbacEntities.cs:47` |
| `Create` が `UserConditions = userCond ?? []`（**null を保存しない**） | `AbacEntities.cs:64` |
| `Update` も同じく `userCond ?? []` | `AbacEntities.cs:75` |
| 唯一の呼び出し元が渡すのは `policy.UserConditions` | `AbacEvaluator.cs:26` |

⇒ M-A は**等価変異（equivalent mutant）**であり、**どんな試験を足しても殺せない。**
null → 空辞書の正規化そのものは既に固定済み ——
`AbacValidationTests.AbacPolicy_Create_NullConditions_StoredAsEmpty`（`AbacValidationTests.cs:169-175`）。

**到達可能な入力は空辞書のほう**である。本仕様書はそちらを固定する。

## 母集合（自分で引き直した走査。規則 1〜10）

`git rev-parse --is-shallow-repository` = **`false`**（履歴を出典に使える）。

**軸を 4 本引いた**（規則 5）。走査はパスから引き、拡張子で絞らず、行フィルタで潰していない（規則 3・4・7）。
除外は `src/ai-stock-trading`（未 populate の submodule）と `obj/` `bin/`（生成物）のみ。

| 軸 | 検索語 | 全体 | コード・配備側 |
| --- | --- | --- | --- |
| 1 | `MatchesUserConditions` | 22 | 6 |
| 2 | `userConditions`（camelCase・JSON） | 55 | 42 |
| 3 | `UserConditions`（PascalCase） | 60 | 34 |
| 4 | `条件が空`（規則の言い換え） | 15 | 6 |

**軸 1 のコード側 6 件**（規則の実装と、それを引用する注記）:

| # | 位置 | 種別 | 本 PR の扱い |
| --- | --- | --- | --- |
| 1 | `AbacEvaluator.cs:26` | 唯一の呼び出し元 | **変えない**（変異を当てて必ず戻す） |
| 2 | `AbacEvaluator.cs:72` | 規則の実装本体 | **変えない** |
| 3 | `AbacEvaluatorTests.cs:346` | 既存注記（#1242 / IADR-0384） | **触らない**。新試験から参照する |
| 4 | `AccessScopeContractTests.cs:19` | 既存注記（共有 InMemory DB の理由づけ） | **触らない** |
| 5 | `BffEndpointAuthenticationTests.cs:13` | 既存注記（BFF 端点認証） | **触らない** |
| 6 | `IGraphAccessResolver.cs:70` | 既存注記（運ばれない属性は効かない） | **触らない**。#1323 の射程 |

**軸 1 の残り 16 件はすべて `.ai-context/` の凍結記録**（IADR 6 件・作業仕様書 10 件）であり、
**本 PR では書き換えない**（`.ai-context/` は凍結記録。日付つき追記の要件も生じない ——
本 PR は既存の記述を誤りにしないため。規則 10 の引き直しを実施した結果である）。

**軸 2・3 の大半（`policies.json` の seed・管理端点の DTO・フロントの語彙表）は
「条件を書く側」であり、判定の意味論には触れない。** 本 PR の変更対象に入らない。

**軸 4 のコード側 6 件のうち 2 件（`AbacEvaluatorTests.cs:71` /
`AbacValidationTests.cs:293`）は「文書条件が空」の話であり、利用者条件ではない。**
語が似ているため走査には出るが、**別の規則**である（規則 2 の帰結として明示的に除外する）。

## 変異試験（実装前・実走した。実出力を記録する）

適用は `.cjs` をスクラッチパッドへ書いて行い、**毎回 `grep` で着地を確認してから**走らせた
（🔴 Bash ツールで `node -e '複数行'` は黙って何も適用しない。過去 2 回この事故を踏んでいる）。

```
cd src/platform/backend && dotnet build backend.slnx && dotnet test backend.slnx --no-build
```

**基準（変異なし）**: 7 アセンブリ・失敗 0 ／ 合格 1628 ／ スキップ 1 ／ 合計 1629。

| 変異 | 実装前の結果 |
| --- | --- |
| **M-A** `if (conditions is null) return false;` | **全 7 アセンブリ緑**（失敗 0 / 合格 1628）。**等価変異** |
| **M-B** `if (conditions is null \|\| conditions.Count == 0) return false;` | `失敗: 1、合格: 200、合計: 201` — `AbacEvaluatorTests.ResolveScope_OwnerOnlyReadPolicy_GrantedWithoutConfidentialityFilter` |
| **M-C** `return true;` をメソッド先頭へ | `失敗: 5、合格: 196、合計: 201` |

**M-C の 5 本**:

```
AbacEvaluatorTests.ResolveScope_OnlyOnePolicyMatches_ProducesOneBranch
AbacEvaluatorTests.ResolveScope_OwnerOnlyReadPolicy_GrantedWithoutConfidentialityFilter
AbacEvaluatorTests.ResolveScope_NoMatchingPolicy_NotGranted
AbacEvaluatorTests.ResolveScope_NoMatchingPolicy_ProducesNoBranches
GrpcResolveScopeTests.Resolve_for_user_without_matching_policy_is_not_granted
```

### 🔴 実測が示した非対称 —— 穴は依頼文が言うより狭く、かつ性質が違う

- **制限側（M-C）は 5 本が殺す。既に十分固定されている**
- **寛容側（M-B）を殺すのは 1 本だけ**であり、その 1 本の**主題は #1242 の
  「`confidentiality` フィルタが不在のスコープ」**（`IADR-0384`）であって、この規則ではない。
  規則を**直接**主張する試験は **0 本**である

⇒ #1242 側の都合でその試験が書き換わると、**寛容側の反転が無言で通る。**
これが埋めるべき穴である。**「無試験」ではなく「巻き添えでしか止まらない」が正確な記述である。**

## 決定（実装方針）

1. **追加先は `AbacEvaluatorTests.cs` の末尾**。新ファイルを作らない ——
   同じ規則を固定する試験が 2 ファイルへ散ると、次の走査が両方を引けない
2. **両方向を対で置く**。片方向だけだと 2 つの変異のうち一方しか殺せない
3. **寛容側は「属性を 1 つも持たない利用者」で書く**。既存 `:346` は `department` を持つ利用者なので、
   **同じ主張の弱い写しにならないようにする**
4. **制限側は `[Theory]` でキー欠落（`AbacEvaluator.cs:78`）と値違い（`:80`）の 2 経路を通す**
5. **陽性対照を対で置く**（「ポリシーが 1 本しか無い作り物」ではないことの担保。既存 `:346` の作法）
6. **M-A が等価変異であることをコード注記に残す** —— 次に同じ変異を当てる人が
   「緑＝無試験」と読み違えるのを止めるため

## テスト（受け入れ基準）

- [x] 利用者条件が空のポリシーは、**属性を 1 つも持たない**利用者にも許可する（`Granted == true`・分岐名を固定）
- [x] 同じ入力で、利用者条件を持つ同居ポリシーは分岐に**出ない**（陽性対照）
- [x] 利用者条件を持つポリシーだけのとき、**キーが無い**利用者には許可しない（`Granted == false`・分岐 0 本）
- [x] 同じく、**値が合わない**利用者にも許可しない
- [x] M-B を当てると**新試験が赤になる**（既存 1 本の巻き添えに依存しない）
- [x] M-C を当てると**新試験が赤になる**
- [x] M-A は追加後も**生存する**（等価変異であることの確認。記録に残す）
- [x] `AbacEvaluator.cs` / `AbacEntities.cs` の差分が **0 行**
- [x] platform backend の試験件数が **1628 → 増える**（減らさない）

## 変異試験（実装後・再実走した。実出力を記録する）

**基準（変異なし・追加後）**: 7 アセンブリ・失敗 0 ／ 合格 **1631** ／ スキップ 1 ／ 合計 1632
（`AuthorizationService.Tests` が 201 → **204**。`[Fact]` 1 本 ＋ `[Theory]` 2 ケース）。

| 変異 | 実装前 | 実装後 |
| --- | --- | --- |
| **M-A** | 失敗 0（全緑） | **失敗 0（全緑のまま）** —— 等価変異なので殺せない。**予測どおり** |
| **M-B** | 失敗 1 / 合格 200 / 合計 201 | **失敗 2 / 合格 202 / 合計 204** |
| **M-C** | 失敗 5 / 合格 196 / 合計 201 | **失敗 8 / 合格 196 / 合計 204** |

**M-B で赤になった 2 本**（新規は 1 本目）:

```
AbacEvaluatorTests.ResolveScope_EmptyUserConditions_GrantsToUserWithNoAttributes   ← 新規
AbacEvaluatorTests.ResolveScope_OwnerOnlyReadPolicy_GrantedWithoutConfidentialityFilter
```

**M-C で赤になった 8 本**（新規は先頭 2 ケース）:

```
AbacEvaluatorTests.ResolveScope_UserConditionsPresent_DoesNotGrantWhenAttributesDiffer
    (attrKey: "department", attrValue: "engineering", why: "キーが無い（clearance を持たない）")   ← 新規
AbacEvaluatorTests.ResolveScope_UserConditionsPresent_DoesNotGrantWhenAttributesDiffer
    (attrKey: "clearance", attrValue: "public", why: "キーはあるが値が合わない")                   ← 新規
AbacEvaluatorTests.ResolveScope_EmptyUserConditions_GrantsToUserWithNoAttributes                  ← 新規
AbacEvaluatorTests.ResolveScope_OnlyOnePolicyMatches_ProducesOneBranch
AbacEvaluatorTests.ResolveScope_OwnerOnlyReadPolicy_GrantedWithoutConfidentialityFilter
AbacEvaluatorTests.ResolveScope_NoMatchingPolicy_NotGranted
AbacEvaluatorTests.ResolveScope_NoMatchingPolicy_ProducesNoBranches
GrpcResolveScopeTests.Resolve_for_user_without_matching_policy_is_not_granted
```

⇒ **新規 1 本が M-B を、新規 2 ケースが M-C を殺す。**寛容側は既存 1 本の巻き添えに依存しなくなった。
`ResolveScope_EmptyUserConditions_GrantsToUserWithNoAttributes` は**両方の変異で赤になる**
（空条件が「誰にもマッチしない」でも「常に true」でも、この試験の主張は崩れる）。

## 🔴 実装中に判明したこと（予定に無かった事実）

### 変異を戻したのに、走っていたのは変異後のバイナリだった

`revert` の後に全試験を回したところ **8 本が赤のまま**で、`git status` は clean、
`git diff` も 0 行、`grep MUTANT` も 0 件だった。**原因は mtime である。**

Node の `fs.copyFileSync` は Windows では `CopyFileW` を呼び、**元ファイルの最終更新時刻を保存する**。
バックアップは「変異を当てる前の（＝git チェックアウト時刻の）」mtime を持っていたため、
それを書き戻したソースは **M-C ビルド時の DLL より古い** ことになり、
MSBuild が「最新」と判断して**再コンパイルしなかった**。
`dotnet build` は `0 エラー` と `AuthorizationService -> ....dll` を出すので、**成功したように見える。**

**採った是正**: 適用・復帰の両方で `fs.utimesSync(path, now, now)` を明示的に呼ぶ。
**規律**: 🔴 **変異を戻したことは「ソースが元に戻った」ではなく「試験が緑に戻った」で確かめる。**
`git diff` が 0 行であることは**戻った証拠にならない**。

これは #1320 の「変異が当たっていないのに緑を『無試験』と読んだ」事故と**同型**であり、
**2 回目**である（[[IADR-0141]] の条件を満たす）。ただし本件の再発防止は
**検査器ではなく作法**で足りる（実行対象がローカルの一時スクリプトであり、CI に載る面が無い）ため、
**規約への追加は行わず本仕様書へ記録するに留める。**

## やらないこと

- 🔴 **`AbacEvaluator` の挙動を変えること**（寛容則は FR-05 上意図的）
- **`AbacEntities.cs` の正規化を変えること**
- **#1318 を閉じること**（欠陥 B の裁定が残る）
- **`.ai-context/` の既存記録の書き換え**（本 PR は既存の記述を誤りにしない）
- **M-A を殺そうとすること**（等価変異であり、殺すには到達不能な分岐を作ることになる）
