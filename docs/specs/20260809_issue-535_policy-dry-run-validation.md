---
title: 作業仕様書 — ポリシーの dry-run 検証 API を追加する（#535）
type: work-spec
status: fixed
related_ids:
  - FR-05
  - FR-09
  - SC-09
  - UC-05
  - IADR-0006
  - IADR-0040
author: claude
created: 2026-08-09
updated: 2026-08-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
related_specs:
  - "../functional/FR-09_abac-attribute-policy-management.md"
  - "../screens/SC-09_admin-abac-settings.md"
---

# 作業仕様書 — ポリシーの dry-run 検証 API を追加する（#535）

## 起点となる計画書（トレーサビリティ）

| 種別 | ID | 何を求めているか |
| --- | --- | --- |
| 画面 | **SC-09** | hi-fi が**保存とは別に**描く「検証」ボタン。**保存せず検証だけ行う口を定める**（確定 2026-08-05・裁定 Q23） |
| 要求 | **FR-09** | 属性・タグ・ポリシー管理。矛盾するポリシーは保存前に検証しエラー |
| 要求 | **FR-05** | ABAC。ポリシーの誤りは認可判定へ即時反映される |

**計画の言葉**（`05_screens/01_screens.md`）:
> **保存せず検証だけ行う口を定める。** 従前は検証が `POST /policies` の応答（400）としてのみ得られ、
> hi-fi が保存とは別に描く「検証」ボタンを満たせなかった。**検証ロジックは既にあるため、
> 保存せず同じ検証を走らせる口を足すだけで足りる。**
>
> ローカルでの代用は採らない——「検証は通ったのに保存で矛盾が出る」形になり、検証ボタンへの信頼が失われる。
> **信頼できない検証ボタンは無いより悪い**（押して安心してから壊す）。

**この「無いより悪い」が本 issue の設計を決める。** 下記「判断 1」を参照。

## 母集合（[[IADR-0141]] 決定 1）

**着手時に実装側が引き直した。走査基準: develop `8cc0280`（#539 マージ直後）。**

**［#635 / #539 の教訓を適用］型検査に掛からない経路を別途引く。**
本 issue は**エンドポイントの追加**なので、型検査は既存の呼び出し側を 1 つも壊さない。
**「無いこと」を固定しているテストと文書**をパスから引くこと（#539 で SC-08 の注記が実際にそれだった）。

### ★ 着手前の実測で分かったこと

**1. 検証の 3 行が `POST` と `PUT` に重複している。**

```csharp
// AuthzEndpoints.cs L43-47（POST /policies）と L62-66（PUT /policies/{id}）に同じ 3 行
var definitions = await db.AttributeDefinitions.ToListAsync();
var errors = AbacValidation.ValidatePolicy(
    req.Name, req.Action, req.UserConditions, req.DocumentConditions, definitions);
if (errors.Count > 0) return ValidationProblem(errors);
```

**dry-run を 3 つ目の複製として足すと、計画が禁じた事態（検証は通ったが保存で矛盾）が構造的に可能になる。**

**2. 副作用なしの検証口には先例がある。** `POST /authz/attributes/validate`（L183）が
`ValidateDocumentAttributesRequest` / `Response(bool Valid, List<string> Errors)` の形で既に在る。
**応答の形はこれに揃える**（画面が 2 種類の検証結果の読み方を覚えなくて済む）。

**3. 「検証ボタンが無いこと」を 2 か所が固定している。**

| 場所 | 何と書いてあるか |
| --- | --- |
| `sc09-admin-abac/PolicyEditorPanel.tsx` L42-44 | 「「検証」ボタン（hi-fi 430 左）は**実装しない**——保存せずに矛盾検証だけを行う API が無い。」 |
| `sc09-admin-abac/AdminAbacSettingsPage.test.tsx` L420-421 | `it('does not render the tag dictionary or a dry-run validate button (no contract behind them)')` |

**どちらも本 issue の完了とともに書き換える**（残すと「未実装」と読める／テストが実装を否定する）。

### 触るもの（**着手後に確定させる。現時点の想定**）

| # | 対象 | 何をするか |
| --- | --- | --- |
| 1 | `AuthorizationService/.../Endpoints/AuthzEndpoints.cs` | **検証の 3 行を 1 つのヘルパへ括り出し**、`POST` / `PUT` / **dry-run** の 3 経路が同じものを呼ぶ。dry-run 口（`POST /authz/policies/validate`）を追加 |
| 2 | `Platform.Shared.Contracts/Dtos/AbacManagementDto.cs` | 応答 DTO（`ValidatePolicyResponse`）。要求は既存の `CreatePolicyRequest` を再利用する |
| 3 | `Platform.Bff/.../Endpoints/AuthzBffEndpoints.cs` | `/bff/admin/authz/policies/validate` のパススルー（[[IADR-0040]] 決定 2。AdminOnly） |
| 4 | `docs/api/openapi.yaml` ＋ orval 生成物 | 契約の追随（生成物はコミットし CI が再生成差分を検査） |
| 5 | `sc09-admin-abac/PolicyEditorPanel.tsx` ＋ `useAbacAdmin.ts` | 「検証」ボタンと検証結果の表示。**注記を消す** |
| 6 | テスト（サービス・BFF・画面） | 下記「テスト」 |
| 7 | `docs/screens/SC-09*` / `docs/tests/SC-09*` / `docs/functional/FR-09*` / `docs/api/BFF_bff-surface.md` / `docs/adr/IADR-0040` | 「契約の不在」の解消を追記 |

### 触らないもの

| 対象 | 理由 |
| --- | --- |
| `AbacValidation.ValidatePolicy` の**判定ロジック** | **issue が「新しい判定ロジックは要らない」と明記**。ここを変えると dry-run と保存の一致という本 issue の目的が崩れる |
| `AbacEvaluator`（認可の評価） | dry-run は**保存前の検証**であり、評価そのものには触れない |
| タグ辞書（#640） | 同じ SC-09 の画面だが**別資源**。ファイル領域が交差するため直列化する（下記） |

### 並行可否の判定（`planning/docs/ai-implementation-workflow-guide.md`）

**#640（タグ辞書の BFF 書き込み口）とは同一フェーズに入れない。**
どちらも `AuthzBffEndpoints.cs` 近傍・SC-09 の画面・`openapi.yaml` の同じ節・orval 生成物を触る。
**規約は「宣言済みファイル領域の非重複で機械的に判定し、交差する issue は直列化する」**と定めており、
感覚で「別機能だから大丈夫」と判定しない。

## 決めたこと（着手時の判断）

### 判断 1: **検証の重複を先に潰す**（3 経路が 1 つのヘルパを呼ぶ）

計画は「**信頼できない検証ボタンは無いより悪い**」と書いている。
その信頼を担保する最も確実な方法は、**dry-run と保存が同じコードを通ること**である。

3 つ目の複製として足すと、**将来どれか 1 つだけを直したときに黙ってズレる**——
#539 で `tags` の写像が「候補は出るが絞れない」と割れていたのと同じ型の事故である。
**括り出しは過剰な抽象化ではなく、計画が求めた性質を構造で守るための最小の手当てである。**

### 判断 2: 要求は `CreatePolicyRequest` を**再利用**する

dry-run は「保存しようとしているもの」をそのまま検証する口である。
**別の要求型を作ると、画面が保存用と検証用で 2 つの組み立てを持つことになり、そこがズレる余地になる。**

### 判断 3: 応答は `{ valid, errors }`（`attributes/validate` と同形）

**200 で返す。** 検証の結果として矛盾が見つかったことは、要求の失敗ではない。
保存（`POST /policies`）は従来どおり **400 ＋ RFC7807** を返す——**そちらは変えない**（既存の契約）。

## テスト（受け入れ基準の写像）

| # | 確かめること |
| --- | --- |
| 1 | 妥当なポリシーを渡すと `valid: true`・`errors` 空 |
| 2 | 矛盾するポリシーを渡すと `valid: false`・`errors` に理由 |
| 3 | ★ **何も保存されない**（呼び出し前後でポリシー件数が変わらない） |
| 4 | ★ **dry-run と保存が同じ結果を出す**（同じ入力に対し、dry-run の `errors` と `POST /policies` の 400 の `errors` が一致） |
| 5 | **システム管理者限定**（運用者・一般利用者は 403） |
| 6 | BFF がパススルーし、AdminOnly を実効化する |
| 7 | 画面に「検証」ボタンが出て、押すと結果が表示される |
| 8 | 画面が**保存せずに**検証できる（検証だけでは一覧が変わらない） |

## 実装中に決めたこと（仕様書からの差分）

### 1. 母集合に **BFF のスタブ**が入っていなかった

`Platform.Bff.Tests/BffTestFactory.cs` の `/authz/policies` 分岐は
**`method == HttpMethod.Post` を先に見る**ため、`/authz/policies/validate` も
`StartsWith("/authz/policies")` に一致して **201 Created に化ける**。
**POST の分岐より前に validate の分岐を置いた。**

**着手時の表は「BFF 側の口」までしか挙げておらず、スタブの分岐順という実装の細部を織り込んでいなかった。**

### 2. 画面のテストが **2 つの不在を 1 つの `it` で主張していた**

```
it('does not render the tag dictionary or a dry-run validate button (no contract behind them)')
```

**タグ辞書（#640 待ち）と検証ボタン（本 issue）を同時に否定している。**
片方の契約が着地しただけで、もう片方まで巻き込んで書き換えることになる。**2 つに分けた**——
分けておけば「なぜ落ちたのか」がテスト名から読み取れる。

### 3. `validate` は**キャッシュを無効化しない**

`create` / `setActive` / `remove` は一覧を書き換えるので `invalidateQueries` する。
**dry-run は何も変えていないので無効化しない。** 無効化すると「検証しただけで一覧が再取得される」
という、利用者から見て説明のつかない挙動になる。

### 4. 矛盾は `isError` では拾えない

`valid: false` も **HTTP 200** なので、`mutation.isError`（通信・認可の失敗）には現れない。
画面は `validate.data.status === 200` の本文を読む。**「検証の結果」と「検証できなかった」を混ぜない。**

## 検証記録（実測）

**走査基準: develop `8cc0280`（#539 マージ直後）＋ 本ブランチ。**

| コマンド | 結果 |
| --- | --- |
| `dotnet build platform/backend/backend.slnx` | `Build succeeded.` |
| `dotnet test platform/backend/backend.slnx` | Passed（`AuthorizationService.Api.Tests` **63 件**・着手時 58／`Platform.Bff.Tests` **175 件**・着手時 173） |
| `dotnet format platform/backend/backend.slnx --verify-no-changes` | 差分なし |
| `pnpm run typecheck` / `lint` / `format:check` | 通過（lint の warning 9 件は既存の `react-refresh` のみ） |
| `pnpm run test` | **609 件 Passed**（着手時 606） |
| `pnpm run test:coverage` | 床（90/90/88/86）を満たす |
| `pnpm run codegen` / `pnpm run i18n` | 再生成差分をコミット済み |
| `node scripts/check-chunk-budget.js` | 床を 582.78 → **582.96 kB** へ更新（+0.18 kB。検証ボタンと文言の分） |
| `node scripts/check-contract-schema.js` | `ValidatePolicyResponse` の追加（**非破壊 1 件・破壊的 0**）。baseline 更新済み |
| 検査器 8 本 ＋ `scripts.test.js` | すべて OK（293 件 Passed） |

### つまずいた点（記録）

**`openapi.yaml` の YAML が壊れた。** `description` の平文に `` `valid: true` `` を書いたところ、
YAML が `: ` をマッピングの区切りとして解釈し、orval が
`bad indentation of a mapping entry` で落ちた。**引用符で囲んで解消した。**
**バッククォートは YAML にとって何の意味も持たない**——コードの引用のつもりでも平文である。
