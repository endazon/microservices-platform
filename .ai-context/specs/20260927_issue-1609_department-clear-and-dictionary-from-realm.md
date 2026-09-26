---
title: 作業仕様書 — 部門グループから外れた利用者の部門属性を同期で消し、属性辞書の部門の値を realm の部門グループから導く（#1609・計画 ADR-0116 決定 2・3）
type: spec
status: in-progress
related_ids:
  - FR-05
  - FR-09
  - UC-05
  - SC-09
  - SC-17
  - ADR-0116
  - ADR-0115
  - ADR-0088
  - IADR-0413
  - IADR-0428
  - IADR-0472
  - IADR-0473
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0116_sc17-department-edits-group-membership.md 決定 2（部門グループに 1 つも属さない利用者の属性 department は消す。全利用者の列挙は最後まで読み、読み切れなければ知らせる。2 個以上は扱わない）
  - planning:projects/microservices-platform/07_adr/ADR-0116_sc17-department-edits-group-membership.md 決定 3（SC-09 の属性辞書は department の値の集合を手で持たない。realm の部門グループのコードを値の集合とする）
  - planning:projects/microservices-platform/07_adr/ADR-0115_department-domain-is-realm-group-and-default-from-registrant.md 決定 1・3
  - planning:projects/microservices-platform/10_feedback/20260926_sc17-department-follows-group.md §残るもの（実装側の残作業 2・3）
related_specs:
  - 20260926_issue-1573_department-attribute-follows-group.md
  - 20260926_issue-1557_department-domain-validation.md
issue: "#1609"
---

# 作業仕様書 — 部門グループから外れた利用者の部門属性を消し、属性辞書の部門の値を realm から導く

## 目的と射程

計画 ADR-0116 は ADR-0115 決定 3（部門の正本は部門グループ）を 3 つの場面へ当てはめた。本作業はそのうち認可の側で完結する 2 つを入れる。

1. **決定 2**: 部門グループに 1 つも属さない利用者の属性 `department` を、部門の同期（IADR-0473。opt-in）が消す。
2. **決定 3**: SC-09 の属性辞書の `department` の値の集合を、seed の固定値ではなく realm の部門グループ（`/department/<code>`）から導く。

**射程外**: SC-17 の部門欄を部門グループの選択へ改める画面の変更（決定 1）は #1610 で行う。本作業は SC-17 の画面を変えないが、
SC-17 の選択肢（属性辞書の利用者スコープの許可値）が realm のコードになることと、保存時の値域検証が同じ集合を使うことは本作業の結果として起きる。
2 個以上の部門グループに属する利用者は今回も触らない（ADR-0116 決定 2・フォローアップ 4）。

## 受け入れ基準（#1609 ＋ コーディネータの指示）

| # | 基準 | 写像先 |
| --- | --- | --- |
| AC-1 | 部門グループが 0 個になった利用者の属性 `department` が、同期（`Fix`）の後に消える | T-55 |
| AC-2 | 全利用者の列挙が途中で失敗した（ページの失敗・打ち切り）周期では、誰の属性も消えない。失敗は計器とログで知らせる（否定の試験） | T-56 |
| AC-3 | 1 つ属する利用者はこれまでどおり直る。2 個以上に属する利用者は触らない。サービスアカウントは消さない | T-57 |
| AC-4 | 属性辞書の `department`（利用者・文書の両スコープ）の許可値が realm の部門グループのコードと一致する | T-58 |
| AC-5 | realm を読めないときは「不明」と示し、既存の値を消さない | T-59 |
| AC-6 | 属性辞書の `department` の値を手で足す・消すことはできない（部門の追加・削除は realm の部門グループで行う） | T-60 |

## 設計

### 1. 部門グループが 0 個の利用者の属性を消す（同期）

- **ポートに 2 つの口を足す**（`IIdentityAdminClient`）。
  - `ListAllUsersAsync` → `UserEnumeration(Users, Complete)`。全利用者を属性つき・ロールなしで**最後のページまで**読む。
    ページの失敗は例外（部分的な結果を返さない）。Keycloak 実装は上限ページ数（`MaxEnumeratedPages`）に達したら `Complete = false`（打ち切り）を返す。
    サービスアカウント（表現の `serviceAccountClientId`）は返さない。
  - `ClearDepartmentAttributeAsync(userId, observed)` → `DepartmentWriteResult`。`department` 1 キーだけを消す。
    `SetDepartmentAttributeAsync` と同じく書く直前に読み直し、有効状態・部門以外の属性が変わっていれば見送る（`Changed`）。読み直して残っていれば例外（fail-closed）。
- **判定は純関数 `DepartmentAttributeReconciliation.Plan` に新しい判定 `Orphaned` を足す**（部門グループ 0 個・属性あり → 消す対象）。
  0 個・属性なしは `InSync`。2 個以上は従来どおり `Unresolved`。
- **🔴 列挙が完了したときだけ 0 個の利用者を計画に入れる**（原則 A: 不明は「無い」ではない）。
  `ListAllUsersAsync` が例外（`page_failed`）または `Complete = false`（`truncated`）なら、その周期は 0 個の利用者を 1 人も計画に入れず、
  計器 `department_sync.enumeration_incomplete.total{department_sync.reason}` と Error ログで知らせる。1 つ属する人の是正はそのまま行う。
- **消す直前に、その人の所属を個別に読み直す**（`GetUserGroupsAsync`）。部門グループが見つかれば消さずに見送る（`skipped_changed`）。
  所属者の一覧はページ送り（offset）であり、並行した所属の変更でページの境目の人が 1 人飛ぶことがある。飛んだ人を「0 個」と読んで消さないための歯止め。
- **サービスアカウントは消さない**（利用者名 `service-account-` 接頭辞を純関数側でも除く）。開発用 realm では `service-account-abac-seeder` が
  部門グループなしで `department=engineering` を持つ（実測）。IADR-0473 の「部門グループに属さないサービスアカウントは対象に現れない」を保つ。
- 計器: 利用者の結末に `cleared` を足す。アラート `DepartmentSyncNotCorrecting` の式に列挙の未完了を足す（4 か所）。

### 2. 属性辞書の `department` を realm の部門グループから導く

- **realm の読み取り**: `/department` を `FindGroupByPathAsync` で引き、直下の子（`ListSubGroupsAsync`）のコード（`CodeOf`）を序数順に並べる。
  根が無い・例外は**不明**（`Known = false`）。子が 0 個は「部門が無い」という確定した答え（`Known = true`・空）。
- **適用（`AttributeDictionary.LoadAsync`）**: 属性辞書を読む 5 経路（一覧・個別取得・ポリシー検証（保存・dry-run）・利用者属性の差し替え（SC-17）・文書属性の検証）
  が同じ 1 つを通る。キー `department`（大小文字無視）の定義について ——
  - realm を読めた: 許可値を realm のコードへ置き換え、**変わっていれば保存する**（最後に確かめた値として残す）。応答の `allowedValuesSource = "realm"`。
  - realm を読めない: 保存済みの値（最後に確かめた値）をそのまま使い、**消さない**。応答の `allowedValuesSource = "realm-unavailable"`（不明）。
  - 他のキー: 従来どおり手で持つ値（`allowedValuesSource = null`）。
- **登録・更新**: `department` の許可値は、空（＝realm から導く）か、実効の値（realm のコード。読めなければ保存済みの値）と同じ集合のときだけ受け付ける。
  それ以外は 400（「部門の追加・削除は realm の部門グループで行う」）。保存する値は realm のコード（読めなければ保存済み。新規で読めなければ空）。
- **seed**（`deploy/local/abac-seed/attributes.json`）: `department` の許可値を空にする（固定値 `finance` / `legal` 等をやめる）。
- **契約**: `AttributeDefinitionDto` に `allowedValuesSource`（null 可）を足す（`docs/api/openapi.yaml`・生成クライアントの再生成）。
- **SC-09 の画面**: 属性辞書の行に値の出所を文字で示す（「realm の部門グループから導出」／「不明（realm を読めない。最後に確かめた値）」）。色だけに頼らない。
- **開発用の偽物 IdP**（`InMemoryIdentityAdminClient`）の部門グループを realm export と同じ `engineering` / `sales` / `hr` にする（辞書が realm から導かれるため）。

## 消える値（ABAC ポリシーへの影響）

- 属性辞書の `department`（両スコープ）から **`finance` / `legal`** が消える（realm の部門グループは `engineering` / `sales` / `hr`）。
- seed のポリシー（`deploy/local/abac-seed/policies.json`）は `department` を条件に持たない（実測）。realm export の利用者の属性も `engineering` だけである（実測）。
- 稼働 DB に `finance` / `legal` を条件に持つポリシーがあれば、評価は変わらない（ポリシーは書き換えない）が、**そのポリシーを保存し直すと値域の検証で 400 になる**。
  該当の有無は稼働 DB を見ないと分からない（本作業は稼働クラスタに触れない）。
- `tags` の許可値（`finance` / `legal` を含む）は別のキーであり変わらない。

## 母集合（追随する記述。誤りの側の文字列で走査した）

走査語: `消しもしない` / `手で消す` / `属性も手で` / `0 個・2 個以上` / `0 個・複数` / `finance` / `legal`（`git grep`。`.ai-context/specs/`・`superpowers/` と `src/ai-stock-trading` を除く）。

| 対象 | 扱い |
| --- | --- |
| `.ai-context/adr/IADR-0473_*.md` 決定 2 | 日付つき追記 `［2026-09-27 追記 / #1609］` で改める。§残るもの 2 も追記 |
| `docs/operations/operations.md` §部門の同期 | 「外したときは属性も手で消す」を改める。列挙の未完了の見え方・属性辞書の出所を書く |
| `docs/security/security.md` 書かない相手 | 0 個の扱いを改める |
| `docs/tests/SC-17_user-account-management.md` T-45 | 0 個の扱いを改め、T-55〜T-57 を足す |
| `docs/tests/SC-09_admin-abac-settings.md` | T-58〜T-60 相当を足す |
| `docs/screens/SC-09`・`SC-17` | 辞書の部門の値の出所を追記する（SC-17 の画面は変えない） |
| `docs/data/abac-policy.md` | `department` の許可値の出所を追記する |
| `DepartmentAttributeReconciliation.cs`・`DepartmentAttributeSync.cs` の注記 | 0 個の扱いを改める |
| 試験 `DepartmentAttributeReconciliationTests`・`DepartmentAttributeSyncTests` の 0 個の期待 | 新しい判定へ改める |
| `IADR-0468`・`RegistrantDepartment.cs`・`data-source.md`・`SC-06` の「0 個・2 個以上」 | **除外**: データソースの既定部門を導く規則であり、本件と別（ADR-0115 決定 2） |
| knowledge の試験データの `finance` / `legal` | **除外**: 文書の属性値の試験であり、属性辞書を読まない |

## 手順

1. 純関数（`Orphaned`）と試験。2. ポート・Keycloak／偽物の実装と試験。3. 同期の組み込み・計器・アラートと試験（変異: 列挙の未完了で消去を止める条件を外す）。
4. 属性辞書の導出（`AttributeDictionary`）と 5 経路への適用・試験。5. 契約・生成クライアント・SC-09 画面・i18n。6. 文書・IADR 追記・新 IADR。7. 検証。
