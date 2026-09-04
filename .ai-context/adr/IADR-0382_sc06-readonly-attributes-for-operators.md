---
title: IADR-0382 SC-06 の既定属性・owner 写像表は一覧の行に読み取り専用で描き、権限で出し分けるのは編集の口だけにする
type: impl-adr
status: Accepted
related_ids:
  - FR-05
  - UC-04
  - SC-06
  - ADR-0036
  - ADR-0074
  - IADR-0127
  - IADR-0359
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs: []
---

# IADR-0382: SC-06 の既定属性・owner 写像表は一覧の行に読み取り専用で描き、権限で出し分けるのは編集の口だけにする

- 状態: Accepted
- 日付: 2026-09-05
- 決定者: claude（実装セッション）

## 起点・関連

- 関連する計画書 ID: `FR-05`（ABAC アクセス制御）／`UC-04`（データソース登録・同期）／
  `SC-06`（データソース管理）／`ADR-0074` 決定 1／`ADR-0036`
- 関連する実装 ADR: `IADR-0127`（管理画面の実装方針。決定 1「押しても 403 になるボタンを置かない」）／
  `IADR-0359`（owner 写像表の器と解決器）
- 関連する実装仕様書: `.ai-context/specs/20260905_issue-1252_sc06-operator-readonly-attributes.md`
- 起点 issue: #1252（#1194 受け入れ基準 3 の画面側が未達）

## コンテキストと課題

`ADR-0074` 決定 1 は `owner` の写像表を **「既定属性 3 つと同じ面・同じ権限（閲覧は管理者・運用者、
登録・更新は管理者限定）」** に置くと定める。#1194（PR #1211）の受け入れ基準 3 も
「運用者は写像表を**閲覧できるが更新できない**」と書いている。

**API 側はこれを満たしている。** BFF の一覧・個別取得は `RequireRole(AdminRole, OperatorRole)` で、
`DataSourceDto` は `DefaultAttributes` と `OwnerMappings` を両方運ぶ。
`OwnerMappingEndpointTests.Operator_CanReadMappings_ButCannotWriteThem` が GET 200 / PATCH 403 を固定している。

**満たしていなかったのは画面である。** 走査（テストと生成物を除く）:

```console
$ grep -rln "defaultAttributes\|ownerMappings" src/knowledge/frontend/src src/platform/frontend/src | grep -v generated
src/knowledge/frontend/src/features/sc06-datasources/components/DataSourceAttributesForm.tsx
src/knowledge/frontend/src/features/sc06-datasources/components/DataSourceForm.tsx
src/knowledge/frontend/src/lib/abac/owner.ts   # 語彙のみ。描画しない
```

**描画点は 2 つのフォームだけ**であり、どちらも管理者にしか開かない（登録フォームは
「＋ ソース登録」、更新フォームは「既定属性」ボタン。いずれも `canWrite = useHasAnyRole(Admin)`）。
一覧表には属性列も写像表列も無い。**つまり「既定属性 3 つと同じ権限」は、
「運用者にはどちらも見えない」という形でしか成立していなかった。**

既定属性の側は #754 以前からの欠落であり、#1194 はそれを継承した。

## 決定

### 決定 1 — 読み取り専用の表示を**一覧の行**へ置く。新しい画面 ID も新しい権限も作らない

`DataSourceAttributesView` を新設し、一覧の**ソース**列に既定属性 3 つと `owner` 写像表を
読み取り専用で描く。

**理由**: `ADR-0074` は案 B（写像表だけを扱う別画面 / API）を「新しい画面 ID と権限の裁定が要り、
SC-06 との二重管理になる」として否決している。**閲覧の欠落を別画面で埋めるのはその否決を回避する形になる。**
一覧は運用者が既に入れる面であり（ルートのゲートは `anyOf=[Admin, Operator]`）、
**面を増やさずに閲覧側を満たせる唯一の置き場所**である。

行の詳細（開閉する詳細ペイン）にしなかったのは、**開かないと読めない値は「読める」と言い切れない**
からである。値は数個であり、行に収まる。

### 決定 2 — 権限で出し分けるのは**編集の口だけ**にする。内容は出し分けない

管理者にも運用者にも**同じ読み取り専用表示**を出す。管理者にはそれに**加えて**「既定属性」ボタンが出る。

**理由**: 内容を出し分けると「管理者が見ている値」と「運用者が見ている値」が別物になり得る面が 1 つ増える。
`ADR-0074` 決定 1 が言う「**同じ面**」は、権限ごとに別の面を作らないという意味である。
`IADR-0127` 決定 1（押しても 403 になるボタンを置かない）は**操作**の規律であって、
**表示の規律ではない** —— 読めるものを読ませないことをその規律は要求していない。

### 決定 3 — 予約値は読み取り専用の面では**隠さずそのまま出す**

`department` が `unassigned` なら `unassigned` と出す。空なら
「未設定（予約値 `unassigned` が入ります）」、写像表が空なら
「未登録（写像に無い利用者は予約値 `system` になります）」と書く。**空欄で終わらせない。**

**理由**: 編集フォームは予約値を入力欄へ出さない（`DataSourceAttributesForm` の注記のとおり、
管理者がそれを実在の部門名と読み、**明示指定として送り返す**ため）。
**しかし読み取り専用の面ではこの配慮が裏目に出る** —— 隠すと「未設定」と区別できず、
`ADR-0074` 決定 3 が「予約値の件数を環流債務の測定値として読む」と定めた読み方が画面からできなくなる。
**送り返す口が無い面では、隠す理由も無い。**

空欄にしないのは、空欄が「値が無い」とも「取得できていない」とも読めるからである。

### 決定 4 — 表示文言は features 側に置き、色だけで意味を持たせない

`DataSourceAttributesView` は `@platform/ui` のプリミティブを使わず素の要素とラベル文字列で構成する。
状態を色で表さない（INDEX 決定 21）。**表示文言は `@platform/ui` に入れない**（共有 UI パッケージの規約）。

## 影響

- `src/knowledge/frontend/src/features/sc06-datasources/components/DataSourceAttributesView.tsx`（新規）
- `DataSourceManagementPage.tsx`（行へ差し込む・冒頭の未実装注記を更新）
- `DataSourceManagementPage.test.tsx`（`#1252` の describe を追加。既存の
  `hides the edit action from non-admins` は**残す**）
- `docs/screens/SC-06_datasource-management.md` / `docs/tests/SC-06_datasource-management.md`
- **API・契約・権限は 1 行も変えない**（既に `ADR-0074` 決定 1 のとおりである）。

## 残るもの

- 写像表の**規模の上限**は `ADR-0074` §残るもの のとおり未定である。行に列挙する形は
  数百対の運用を想定していない。規模が問題になったら計画へ改めて諮る（案 B の再検討）。
- hi-fi モックには既定属性も写像表も無い。本表示は「モックに無いが実装する要素」であり、
  計画側の hi-fi が更新されたら対応表を引き直す。
