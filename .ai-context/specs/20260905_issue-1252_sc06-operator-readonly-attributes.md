---
title: SC-06 で運用者が既定属性・owner 写像表を閲覧できない（ADR-0074 決定 1 の閲覧側が画面で未達）（#1252）
type: spec
status: done
related_ids: [FR-05, UC-04, SC-06, ADR-0036, ADR-0074, IADR-0127, IADR-0382]
author: Claude
created: 2026-09-05
updated: 2026-09-05
plan_refs: []
---

# #1252: SC-06 の既定属性・owner 写像表を運用者にも読ませる

## 起点となる計画書（トレーサビリティ）

- 機能要求: `FR-05`（ABAC アクセス制御）
- ユースケース: `UC-04`（データソース登録・同期）
- 画面: `SC-06`（データソース管理）
- 計画 ADR: `ADR-0074` 決定 1（写像表は SC-06 の登録・更新フォームが持ち、**既定属性 3 つと同じ面・
  同じ権限**＝**閲覧は管理者・運用者**、登録・更新は管理者限定）／`ADR-0036`（所有者ベースの裁量制御）
- 起点 issue: #1252（#1194 受け入れ基準 3 の画面側が未達）

## 1. 事象の再確認（issue の主張を自分で走査して確かめた）

issue 本文の件数・主張は**検証対象であって根拠ではない**ため、母集合を自分で引き直した。

### 1-1. 既定属性・写像表を描く箇所（生成物・テスト除く）

```console
$ git rev-parse --is-shallow-repository
false

$ grep -rln "defaultAttributes\|ownerMappings" src/knowledge/frontend/src src/platform/frontend/src | grep -v generated
src/knowledge/frontend/src/features/adminFlow.test.tsx                                  # テスト
src/knowledge/frontend/src/features/sc06-datasources/components/DataSourceAttributesForm.tsx
src/knowledge/frontend/src/features/sc06-datasources/components/DataSourceForm.tsx
src/knowledge/frontend/src/features/sc06-datasources/components/DataSourceManagementPage.test.tsx  # テスト
src/knowledge/frontend/src/lib/abac/owner.ts                                            # 語彙（描画しない）
```

**描画する実体は 2 つのフォームだけ**である。`DataSourceForm` は登録フォーム（管理者のみ）、
`DataSourceAttributesForm` は「既定属性」ボタン（管理者のみ）から開く更新フォーム。
**一覧表（`DataSourceManagementPage`）には属性列も写像表列も無い。** issue の主張は成り立つ。

### 1-2. 「既定属性」ボタンは運用者に出ない

`DataSourceManagementPage.tsx:57` の `const canWrite = useHasAnyRole(PlatformRole.Admin);` が
ボタンの出し分けを持ち、既存テスト `hides the edit action from non-admins` がそれを固定している。

### 1-3. 陽性対照 —— 走査が壊れていないこと・API 側は閲覧を開いていること

- 陽性対照 A: 同じ走査語で 2 つのフォームは確かに挙がる（1-1。ヒット 0 件ではない）。
- 陽性対照 B: 契約は運用者へ値を届けている。`DataSourceDto` は `DefaultAttributes` と
  `OwnerMappings` を持ち、BFF の一覧・個別取得は
  `RequireRole(AdminRole, OperatorRole)`（`DataSourceBffEndpoints.cs:25-27`）である。
  **画面が描いていないだけで、運用者のブラウザには値が届いている。**
- 陽性対照 C: ルートのゲートは `RequireRole anyOf=[Admin, Operator]`（`sc06DataSourcesRoute.tsx:32`）。
  運用者はこの画面へ入れる。

### 1-4. 追随が要る文書（誤りの側の文字列で走査してから挙げた。規則 9）

`docs/` を「既定属性」「写像」で走査し、SC-06 の閲覧権限を述べている箇所を集めた。

| 文書 | 追随の要否 |
| --- | --- |
| `docs/screens/SC-06_datasource-management.md` | **要**（hi-fi 対応表・アクセスの記述） |
| `docs/tests/SC-06_datasource-management.md` | **要**（テスト観点に閲覧側を足す） |
| `docs/screens/SC-05_document-management.md` 等の他画面 | 不要（SC-06 の権限を述べていない） |
| `docs/authz/**` | 不要（API の権限は変えない） |

除外理由: **API の権限は 1 行も変えない**（既に正しい）。したがって権限表を持つ文書
（`docs/authz/`・`docs/api/openapi.yaml`）は母集合に入らない。

## 2. 決めたこと

[IADR-0382](../adr/IADR-0382_sc06-readonly-attributes-for-operators.md) に記録する。要点:

1. **一覧の行に、既定属性 3 つと owner 写像表を読み取り専用で描く**（新しい画面 ID も新しい権限も
   作らない。ADR-0074 決定 1・案 B の否決を尊重する）。管理者にも同じものが見える
   （**同じ面** —— 権限で内容を出し分けない。出し分けるのは編集の口だけである）。
2. **「既定属性」ボタン（編集の口）は従来どおり管理者のみ。** 既存テストを残す。
3. 読み取り専用の描画は `DataSourceAttributesView` に切り出す。**フォームと同じ語彙定数**
   （`lib/abac`）を使い、値の読み替え規則を 2 箇所へ割らない。
4. **色だけで意味を持たせない**（ラベル文字列 ＋ 値）。**表示文言は `@platform/ui` に入れない**
   （`features/` 側の `<Trans>` で持つ）。

## 3. 受け入れ基準（issue から写す）

- [ ] Given 運用者 / When SC-06 を開く / Then 各データソースの既定属性 3 つと owner 写像表が読める
- [ ] Given 運用者 / When 同画面 / Then 写像表・既定属性を更新する口は無い（既存テストを維持）
- [ ] Given 管理者 / When 同画面 / Then 従来どおり「既定属性」から編集できる（陽性対照）
- [ ] Given `src/` / When `pnpm run lint` / `typecheck` / `test` / Then 成功する

## 4. 変異試験

読み取り専用の描画から owner 写像の行を落とすと、運用者側の陰性が落ちること（＝テストが赤くなる）を
実測して PR 本文へ貼る。

## 5. やらないこと

- 新しい画面 ID・新しい権限を作らない（ADR-0074 決定 1）。
- API の権限を変えない（既に `ADR-0074` 決定 1 のとおり）。
- 写像表の**編集**を運用者へ開かない（登録・更新は管理者限定）。
