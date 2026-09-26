---
title: IADR-0476 属性辞書の department の許可値は、読むたびに realm の部門グループから導いて保存し直し、realm を読めなければ最後に確かめた値を「不明」として示して消さない
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-09, UC-05, SC-09, SC-17, ADR-0116, ADR-0115, IADR-0006, IADR-0040, IADR-0301, IADR-0329, IADR-0472, IADR-0473]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0116_sc17-department-edits-group-membership.md 決定 3（SC-09 の属性辞書は department の値の集合を手で持たない。realm の部門グループのコードを値の集合とする）
  - planning:projects/microservices-platform/07_adr/ADR-0115_department-domain-is-realm-group-and-default-from-registrant.md 決定 1（値域は /department/<code> の <code>）
related_specs:
  - ../specs/20260927_issue-1609_department-clear-and-dictionary-from-realm.md
---

# IADR-0476: 属性辞書の department の許可値を realm の部門グループから導く（#1609）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-27
- 決定者: claude（#1609。計画 ADR-0116 決定 3 の実装）

## 起点・関連

- 関連する計画書 ID: FR-05 / FR-09 / UC-05（ABAC の属性辞書・ポリシー）・SC-09（属性辞書の画面）・SC-17（部門欄の選択肢と保存の値域）
- 関連する計画 ADR: ADR-0116 決定 3（辞書の部門の値は realm の部門グループから導く。ポリシーの許容値と SC-17 の選択肢はこの集合から選ぶ）／ADR-0115 決定 1（値域）
- 関連する実装 ADR: IADR-0472（部門コードの照会。キャッシュを持たない判断を同じく採る）・IADR-0473（部門の同期。同じ木 `/department` を読む）・
  IADR-0301 / IADR-0329（realm を読む主体は `identity-admin` の 1 つ）・IADR-0006 / IADR-0040（辞書の削除と参照中の 409）
- 作業仕様書: `../specs/20260927_issue-1609_department-clear-and-dictionary-from-realm.md`

## コンテキストと課題

属性辞書（AuthorizationService の `AttributeDefinitions`）は `department` の許可値を手で持っていた。seed（`deploy/local/abac-seed/attributes.json`）は
`engineering` / `sales` / `hr` / `finance` / `legal` を入れ、realm の部門グループは `engineering` / `sales` / `hr` だった（ADR-0116 実測 2）。
辞書はポリシーの許容値の検証（保存・dry-run）、SC-17 の選択肢（画面が一覧から作る）と保存の値域検証、文書属性の検証が読む。決めるべき点は 4 つあった。
**(a) いつ・どこで realm から導くか**、**(b) realm を読めないときにどうするか**、**(c) 手で持つ値を足す・消す要求をどうするか**、**(d) 画面へ何を示すか**。

## 検討した選択肢

### (a) 導く場所

1. **読む経路ごとに realm を引く**（検証 5 か所それぞれで） —— 却下。1 経路でも保存済みの値を直に読むと、画面に出る値と検証が使う値が食い違う。
2. **定期処理で辞書を realm へ合わせる** —— 却下。部門の同期（IADR-0473）は既定 Off であり、辞書の正しさを opt-in の処理に預けられない。
   周期の間は画面と realm がずれる。
3. **辞書を読む唯一の入口（`AttributeDictionary`）を置き、読むたびに realm から導く**（**採用**）。一覧・個別取得・ポリシー検証（保存・dry-run）・
   SC-17 の属性差し替え・文書属性の検証がすべてこの 1 つを通る。**キャッシュは持たない**（IADR-0472 と同じ。管理者の低頻度な操作であり、
   部門グループの変更が次の要求で辞書に現れることを優先する）。

### (b) realm を読めないとき

1. **空にする**（部門なし） —— 却下。読めないことは「部門が無い」ではない（原則 A）。ポリシーの保存がすべて通らなくなり、SC-17 の選択肢も消える。
2. **seed の値に戻す** —— 却下。旧い固定値（`finance` / `legal`）が再び通る。
3. **読めたときに導いた値を保存し直しておき、読めないときはその値（最後に確かめた値）を使い、出所を「不明」と示す**（**採用**）。
   根（`/department`）が無い realm も不明に倒す。根はあるが子が 0 個なら「部門が無い」という確定した答えとして空にする。

### (c) 手で持つ値の要求

1. **黙って realm の値に置き換える** —— 却下。送った値と保存された値が黙って違う。
2. **空（＝ realm から導く）か、現在の値と同じ集合（並び・大小は序数）のときだけ受け付け、それ以外は 400**（**採用**）。ラベル・必須は変えられる。
   seed は空で投入する（`seed-abac-policies.js` の冪等性は変わらない）。realm を読めないときは保存済みの値のまま送る更新だけが通る。

## 決定

1. **`AttributeDictionary`（`Features/Authz`）を属性辞書を読む唯一の入口とする。** `department`（キーは大小文字無視・両スコープ）の許可値を
   `/department` の直下の子のコード（`CodeOf`。序数順・重複なし）へ置き換え、**変わっていれば保存し直す**。判定は純関数 `DepartmentDictionaryValues`（Domain）に置く。
2. **realm を読めない（例外・根が無い）ときは保存済みの値を使い、消さない。** 取り消し（要求の中断）だけは上げる。
3. **応答に許可値の出所 `allowedValuesSource` を足す**（契約 `AttributeDefinitionDto`。null 可）。手で持つキーは null、`department` は
   `realm`（今回導いた）／`realm-unavailable`（不明・最後に確かめた値）。`enum` にしない（値域の正は `DepartmentDictionaryValues`）。
4. **`department` の登録・更新は (c) の 2 のとおり。** 新規登録で realm を読めなければ空で登録し、出所を不明とする（次に読めた要求で埋まる）。
5. **SC-09 の属性辞書の行に出所を文言つきのバッジで示す**（不明は注意の色。`StatusBadge` がアイコンと文言を強制する）。SC-17 の画面は変えない（#1610）。
6. **開発用の偽物 IdP（`InMemoryIdentityAdminClient`）の部門グループを realm export と同じ `engineering` / `sales` / `hr` にする。**

## 結果

- 属性辞書・ポリシーの許容値の検証・SC-17 の選択肢と保存の値域が、realm の部門グループの 1 つの集合にそろう。
- 🔴 **消える値**: 辞書の `department` から `finance` / `legal` が消える。seed のポリシーは `department` を条件に持たず、realm export の利用者の属性も
  `engineering` だけである（実測）。稼働 DB に `finance` / `legal` を条件に持つポリシーがあれば、評価は変わらない（ポリシーは書き換えない）が、
  **保存し直すと 400 になる**。該当の有無は稼働 DB を見ないと分からない（本作業は稼働クラスタに触れない）。
- 辞書を読む要求ごとに realm へ 2 往復（根の引き当て・子の一覧）が増える。
- 稼働環境では、次に辞書を読んだ要求で保存済みの値が realm のコードへ置き換わる（配備後の作業は要らない）。

## 残るもの

1. SC-17 の部門欄を部門グループの選択と所属の変更へ改める（計画 ADR-0116 決定 1。#1610）。
2. 予約値 `unassigned`（IADR-0199）は文書の `department` に入り得るが、辞書の許可値には入れない（どのポリシーにも許さない値であり、従前の seed も持たない）。
