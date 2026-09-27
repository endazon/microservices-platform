---
title: 作業仕様書 — SC-17 の部門欄を部門グループの所属の変更に改め、利用者属性 department を直接書かない（#1610・計画 ADR-0116 決定 1）
type: spec
status: done
related_ids:
  - FR-05
  - FR-09
  - UC-05
  - SC-17
  - SC-09
  - ADR-0116
  - ADR-0115
  - ADR-0026
  - ADR-0064
  - ADR-0082
  - IADR-0301
  - IADR-0428
  - IADR-0473
  - IADR-0477
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0116_sc17-department-edits-group-membership.md 決定 1（SC-17 の部門欄は部門グループの所属を変える。選択肢は realm の部門グループのコード。選んだグループへ入れ、ほかの部門グループから外す。「部門なし」はすべてから外す。属性 department は同期が追いつき、SC-17 は直接書かない。操作は監査ログに記録する）
  - planning:projects/microservices-platform/07_adr/ADR-0116_sc17-department-edits-group-membership.md 決定 2（2 個以上に属する利用者は本 ADR では扱わない）・フォローアップ 4
  - planning:projects/microservices-platform/05_screens/01_screens.md §SC-17（入力規則の部門の行・［2026-09-26 追加］部門の変更は部門グループの所属の変更である）
  - planning:projects/microservices-platform/10_feedback/20260926_sc17-department-follows-group.md §裁定 1・§残るもの（実装側の残作業 1）
related_specs:
  - 20260927_issue-1609_department-clear-and-dictionary-from-realm.md
  - 20260926_issue-1573_department-attribute-follows-group.md
  - 20260829_issue-452_sc17-user-account-management.md
issue: "#1610"
---

# 作業仕様書 — SC-17 の部門欄を部門グループの所属の変更に改める

## 目的と射程

計画 ADR-0116 決定 1 を実装する。SC-17（利用者アカウント管理）の部門欄は、これまで利用者属性 `department` を
`PUT /authz/users/{id}/attributes` で直接書いていた。部門の正本は部門グループ（`/department/<code>`）への所属であり（ADR-0115 決定 3）、
部門の同期（IADR-0473。opt-in）を `Fix` で有効にすると、SC-17 で変えた部門は次の周期でグループの値へ戻っていた。

本作業で、SC-17 の部門欄は **部門グループの所属を変える操作**になる。属性 `department` は同期が追いつく。SC-17 の経路からは属性 `department` を書かない。

**射程外**:
- 部門の同期そのもの（IADR-0473・#1609 で完了）。本作業は同期を変えない（同期は今後もグループを変えない）。
- 2 個以上の部門グループに属する利用者の扱いの決定（ADR-0116 フォローアップ 4。計画でも対象外）。本作業は「SC-17 からは変えない（拒む）」という安全側の振る舞いだけを定める。
- 属性辞書の部門の値の導出（IADR-0477。#1609 で完了）。本作業は同じ読み取り（`AttributeDictionary.ReadDepartmentDomainAsync`）を再利用する。

## 受け入れ基準（#1610 ＋ コーディネータの指示）

| # | 基準 | 写像先 |
| --- | --- | --- |
| AC-1 | 部門を変えて保存すると、部門グループの所属が変わる（いまの部門グループから外れ、選んだグループへ入る）。「部門なし」はすべての部門グループから外す | T-62 |
| AC-2 | 保存の後に同期（`Fix`）を 1 周させても、部門が戻らない（属性が新しい部門へ追随し、所属はそのまま） | T-63 |
| AC-3 | SC-17 の経路から、属性 `department` を書く要求が出ない（否定の試験。部門の変更でも、属性の差し替えでも） | T-64 |
| AC-4 | 2 個以上の部門グループに属する利用者は、SC-17 からは変えない（理由つきの 409。何も書かない）。画面は変えられない理由を示す | T-65 |
| AC-5 | 途中で失敗しても（入れたが外せない等）、黙って部門グループ 0 個にしない。補償（元に戻す）を試み、結果（戻せた／戻せなかった・いまの所属）を理由つきで返す | T-66 |
| AC-6 | 画面: 部門の選択肢は realm の部門グループのコード（＋「部門なし」）。いまの所属と属性の追随の状態を色 ＋ アイコン ＋ 文言で示す。ja / en の文言 | T-67 |

## 設計

### 1. ポート（`IIdentityAdminClient`）に 3 つの口を足す

| 口 | 意味 | Keycloak |
| --- | --- | --- |
| `FindByIdAsync(userId)` | IdP の内部 ID で 1 人を引く（属性つき・ロールなし）。居なければ null | `GET users/{id}`（404 → null） |
| `JoinGroupAsync(userId, groupId)` | グループへ入れる（冪等）。利用者かグループが居なければ false | `PUT users/{id}/groups/{groupId}` |
| `LeaveGroupAsync(userId, groupId)` | グループから外す（冪等）。利用者かグループが居なければ false | `DELETE users/{id}/groups/{groupId}` |

- 🔴 名前に作成の禁止語（`Add` 等。`IdentityAdminContractTests`）を使わない。利用者を作る口ではない。
- 🔴 **部門の同期はこの 2 つの書き込みを使わない**（IADR-0473 決定 3「グループは変えない」を保つ）。同期の試験の偽物は `JoinGroupAsync` / `LeaveGroupAsync` を呼ばれたら例外にする。
- 権限: 既存の機密クライアント `identity-admin` の `manage-users` で足りる（所属の変更は `manage-users` の範囲）。主体・ロールは増やさない。
- 監査: 所属の変更は Keycloak の管理イベント（`GROUP_MEMBERSHIP`）に残る（realm の `adminEventsEnabled: true`。SC-17 の他の操作と同じ記録先）。サービスのログにも IdP 内部 ID と変更前後のコードを 1 行出す（利用者名は出さない）。

### 2. 属性の差し替え（`PUT /authz/users/{id}/attributes`）から部門を外す

- 🔴 要求に `department`（大小文字無視）が含まれていれば **400**（「部門は部門グループの所属で変える」）。受け付けて無視しない。
- `department` は必須から外す（必須は `clearance` だけ）。ADR-0116 決定 1 は「部門なし」を許す。
- 🔴 **差し替えでも現在の `department` は消えない**。ポートの `ReplaceAttributesAsync` は、保持起点（IADR-0428）と同じく、現在の表現から `department` を**多値のまま持ち越す**
  （要求側の値は採らない）。持ち越さないと、機密区分上限を 1 つ直しただけで部門が消える（＝属性を書くことになる）。

### 3. 部門の読み取りと変更（新しい口）

- `GET /authz/users/{id}/department` → `UserDepartmentDto { departmentGroups: string[], departmentAttribute: string?, choices: string[] }`
  - `departmentGroups`: 利用者が直接属する部門グループのコード（`CodeOf`。入れ子は上位に畳む。序数順・重複なし）。
  - `departmentAttribute`: 利用者属性 `department`（ABAC が読む値。同期が追いつくまでグループと違い得る）。
  - `choices`: realm の `/department` の直下の子のコード（`AttributeDictionary.ReadDepartmentDomainAsync`。IADR-0477 と同じ読み取り）。
  - realm を読めない（根が無い・例外）→ **503**（選択肢を推測で出さない）。利用者が居ない → 404。
- `PUT /authz/users/{id}/department` 本文 `ReplaceUserDepartmentRequest { department: string? }`（null・空 ＝ 部門なし）
  1. 利用者が居なければ 404。realm を読めなければ 503（何も変えない）。
  2. `department` が realm の部門グループのコードに無ければ 400（ValidationProblem）。
  3. いまの所属を読む（`GetUserGroupsAsync`）。計画は純関数 `DepartmentMembershipPlan.Plan` が立てる:
     - 部門のコードが 2 個以上 → **409**（「複数の部門グループに属しているため、この画面では変えない。Keycloak で所属を 1 つにしてから変える」）。何も書かない。
     - 目的と同じ（コードが目的の 1 つ／部門なしで 0 個）→ 何も書かずに 200（冪等）。
     - それ以外 → 入れるグループ（目的のグループ。部門なしなら無し）と、外すグループ（部門の木の直接の所属すべて）。
  4. 🔴 **先に入れてから外す**（途中で失敗しても 0 個にならない向き）。
  5. 外す途中で失敗したら **補償**: 外したグループ（と失敗したグループ）へ入れ直す（冪等）。入れ直しがすべて成功したときだけ、入れた目的のグループから外す。
     入れ直しが 1 つでも失敗したら目的のグループを残す（0 個にしない）。いまの所属を読み直して、**502** で「戻せた／戻せなかった」といまの所属を返す（Error ログ）。
     入れる段で失敗したら、目的のグループから外す（冪等。入ったか分からないため）→ 502。
  6. 書いた後に所属を読み直し、期待どおり（コードが目的の 1 つ／0 個）でなければ **409**（他の変更と競合した可能性）といまの所属を返す。
  7. 成功は 200 と `UserDepartmentDto`（読み直した所属・属性・選択肢）。
- BFF: `GET` / `PUT /bff/admin/users/{userId}/department` を AdminOnly で透過中継する（既存の `Proxy`）。

### 4. 画面（`src/knowledge/frontend/src/features/sc17-users`）

- 権限編集を開くと `GET /bff/admin/users/{id}/department` を引き、部門欄の選択肢を `choices` ＋「部門なし」で作る（属性辞書の部門の定義は部門欄に使わない）。
- 部門の状態を `StatusBadge` で示す（色 ＋ アイコン ＋ 文言）:
  - 属性がグループに追随済み（成功）／属性はまだ追随していない（注意。「属性は部門の同期で追随します」）／複数の部門グループ（注意。部門欄は無効化し、理由を出す）。
- 保存: ロール → 属性（**`department` を除く**）→ 部門（**いまの所属から変えたときだけ**）の要求を送る。部門の要求の拒否理由（400 / 409 / 502 / 503）はそのまま出す。
- 必須の検査は機密区分上限だけにする（部門は「部門なし」を選べる）。
- orval の生成物は `pnpm run codegen` で作り直す（手で編集しない）。文言は Lingui のカタログ（ja / en）を `pnpm run i18n` で作り直す。

### 5. 開発用の偽物 IdP

- `InMemoryIdentityAdminClient` の所属表を可変にし、`JoinGroupAsync` / `LeaveGroupAsync` / `FindByIdAsync` を本物と同じ意味論で持つ。
  `ReplaceAttributesAsync` は `department` を持ち越す（本物と同じ）。

## 母集合（追随する記述。誤りの側の文字列で走査した）

走査語: `部門欄` / `両方変える` / `画面で部門を付け` / `直接編集` / `直接書` / `#1610` / `画面の変更は別` / `SC-17 の画面は変え` / `本画面の挙動` / `admin/users` / `authz/users` / `利用者アカウント管理` × `部門`（`git grep`。`src/ai-stock-trading`・`.ai-context/specs/`・`.ai-context/superpowers/`・`CHANGELOG.md` を除く）。

| 対象 | 扱い |
| --- | --- |
| `docs/screens/SC-17_user-account-management.md` 部門の行・バリデーション・アクション・計画との対応・状態の示し方 | 部門グループの所属の変更へ改める |
| `docs/tests/SC-17_user-account-management.md` T-04・T-06・T-27・T-28・T-30・T-59・関連仕様・未決事項 3 | 改め、T-62〜T-67 を足す |
| `docs/operations/operations.md` §部門の同期（注意の 2 行） | 「画面の変更は別の作業」「部門グループに 0 個の人に画面で部門を付けても消える」を改める |
| `docs/security/security.md` §部門の同期 | 管理画面が所属を変える書き込みを持つことを足す |
| `docs/api/BFF_bff-surface.md` 管理面の表 | 2 行を足し、属性の差し替えの注記を改める |
| `docs/api/openapi.yaml` | 2 経路・2 スキーマを足し、属性の差し替えの説明を改める（生成クライアントの入力） |
| `.ai-context/adr/IADR-0473_*.md` §結果の SC-17 の行・§残るもの 1・3 | 日付つき追記で改め、決定の追記（所属を変える口・同期は使わない）を足す |
| `.ai-context/adr/IADR-0477_*.md` 決定 5・§残るもの 1 の「SC-17 の画面は変えない（#1610）」 | 日付つき追記（本作業で画面を変えた・選択肢は同じ読み取りから引く） |
| `src/.../InMemoryIdentityAdminClient.cs` 田中（`finance`）の注記 | 所属の可変化に合わせて改める |
| `docs/how-to/plan-id-range-history-annex.md` の ADR-0116 の行 | **除外**: 計画 ID の表であり、実装の状態を書いていない |
| `docs/tests/FR-01`・`SC-06` の「部門欄」 | **除外**: データソース管理画面の部門欄であり、SC-17 と別 |

## 実装判断の記録先

- IADR-0473 への追記 `［2026-09-27 追記 / #1610］`（所属を変える口・SC-17 だけが使う・同期は使わない・補償の順序・2 個以上は拒む）。
  新しい IADR は起こさない（決定の対象が IADR-0473 の「属性はグループに従い、グループは変えない」の境界そのものであるため）。

## 結果（2026-09-27）

- 受け入れ基準 AC-1〜AC-6 は T-62〜T-67（テスト仕様書: 利用者アカウント管理）に写像し、いずれも緑。
- 変異（いずれも赤になることを確かめ、`git show HEAD:<path> > <path>` で戻した）:
  - M1 部門の変更の後に属性を直接書き戻す（`SetDepartmentAttributeAsync` / `ClearDepartmentAttributeAsync`）→ T-64
    `Changing_the_department_never_writes_the_department_attribute` と T-63 `One_sync_cycle_after_the_change_follows_the_new_group_instead_of_reverting_it` が赤。
  - M2 外す段を飛ばす → T-62 `Saving_moves_the_user_to_the_chosen_department_group_and_none_leaves_them_all`・T-63 の 2 件・T-66 の 2 件（計 5 件）が赤。
  - M3 実プロバイダ実装の差し替えで `department` の持ち越しを外す → T-64 `Replacing_attributes_never_writes_the_department_and_carries_the_current_one_over` が赤。
  - M5 画面の属性の下書きに部門を残す → Vitest 4 件（T-64 の画面の否定の試験を含む）が赤。
- 検証: 両 slnx の `dotnet test`（全緑）・`dotnet format --verify-no-changes`（両方 exit 0）。`src/` で typecheck / lint（エラー 0）/ format:check /
  codegen・i18n（再生成で差分なし）/ Vitest（sc17-users は全緑）。`check-i18n-catalogs`・`check-chunk-budget --require`（初期ロード +1.44 kB を床へ反映。
  増分は ja / en のカタログの文言）・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（841 件緑）。
- 手元だけで赤い既存の試験（本変更と無関係。CI の Node 22 では緑の見込み）: `orvalMutator.test.ts` の Blob の 1 件は手元の Node 24 の jsdom で
  `arrayBuffer` が無いため。echarts の遅延読み込みの 2 件は全量実行の負荷で時間切れになり、単独では緑。
- `check-knip.js --require` は Windows で knip の起動（`.CMD`）に失敗したため、`pnpm exec knip` の区分別件数を床（4 / 1 / 16 / 15）と突き合わせて一致を確かめた。

## 手順

1. 純関数 `DepartmentMembershipPlan` と試験。2. ポート・Keycloak／偽物の実装と試験。3. 端点（読み取り・変更・属性の差し替えの改め）と試験（T-62〜T-66）。
4. BFF の中継と試験。5. 契約（openapi・Shared.Contracts・生成クライアント）。6. 画面・i18n・Vitest。7. 文書・IADR 追記。8. 検証・変異。
