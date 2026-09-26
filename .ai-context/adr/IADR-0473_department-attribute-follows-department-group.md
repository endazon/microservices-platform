---
title: IADR-0473 利用者属性 department は AuthorizationService の opt-in の定期処理が部門グループ所属へ合わせて直す（ちょうど 1 つのときだけ・グループは変えない・既定 Off）
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-09, UC-05, SC-17, ADR-0115, ADR-0026, ADR-0088, IADR-0301, IADR-0329, IADR-0369, IADR-0385, IADR-0413, IADR-0428, IADR-0468, IADR-0472]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0115_department-domain-is-realm-group-and-default-from-registrant.md 決定 3
  - planning:projects/microservices-platform/07_adr/ADR-0088_authz-resolves-user-attributes-itself.md 決定 1
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md §利用者属性
related_specs:
  - ../specs/20260926_issue-1573_department-attribute-follows-group.md
---

# IADR-0473: 利用者属性 department を部門グループ所属へ合わせる（#1573）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-26
- 決定者: claude（#1573。計画 ADR-0115 決定 3 の実装）

## 起点・関連

- 関連する計画書 ID: FR-05（ABAC）・FR-09 / UC-05 / SC-17（利用者への属性の割当）
- 関連する計画 ADR: ADR-0115 決定 3（正本は部門グループ。属性はグループに合わせて直し、逆向きには直さない。形は実装判断）／ADR-0088 決定 1（判定に使う利用者属性は IdP から引き直す）／ADR-0026（人事連携はグループ所属を更新する）
- 関連する実装 ADR: IADR-0472（`FindGroupByPathAsync`。本決定はその上に積む）・IADR-0468（入れ子は上位のコードに畳む規則）・IADR-0369 決定 2（realm reconcile Job の境界）・IADR-0329（`identity-admin`）・IADR-0413 決定 5（列挙の打ち切りで利用者が黙って落ちる欠陥）・IADR-0428（属性の書き込みは他を巻き添えにしない）・IADR-0385（`department` は 1 値）
- 作業仕様書: `../specs/20260926_issue-1573_department-attribute-follows-group.md`

## コンテキストと課題

ABAC が判定に使う利用者の部門は、AuthorizationService が IdP から引き直す利用者属性 `department` である（ADR-0088 決定 1）。
一方、データソースの既定部門（IADR-0468）と明示値の値域（IADR-0472）は部門**グループ**を読む。両者が食い違うと、文書は部門へ寄っても
その部門の利用者に開かない（ADR-0115 §理由）。決定 3 は一致を実装に求めたが、形（自動で導く／検知して直す）は実装判断とした。

制約（コーディネータの指示）: 稼働 realm は AST の PoC と共有している。**稼働 realm に対して動くものは opt-in**にし、client の secret を触らず、
AST のクライアントが依存する挙動を変えない。

## 検討した選択肢

1. **realm の reconcile Job（`deploy/local/keycloak-setup/reconcile-realm.js`）に属性の是正を足す** —— 却下。
   Job は「人間の利用者の資格情報・属性・ロール・グループは実行時所有（触らない）」という境界を持つ（IADR-0369 決定 2）。
   client の secret を宣言へ戻す Job でもあり、利用者属性の書き込みを同居させると「属性を直すために Job を回す」と secret が宣言値へ戻る。
   `deploy/local` 専用で、組織の本番 realm では動かない。
2. **Keycloak のマッパーでクレームをグループのパスから導く** —— 却下。AuthorizationService は判定のたびに属性を IdP から引き直すので
   （ADR-0088 決定 1）、トークンのクレームを変えても判定に使う属性は変わらない。加えて `department` クレームは AST のクライアントも受け取っており、
   導き方を変えると依存先の挙動が変わる。
3. **AuthorizationService の定期処理が検知して直す**（**採用**）。
   - 判定に使う属性の持ち主（IdP）へ、既に realm を読み書きする唯一の主体（`identity-admin`）で書く。主体・ロール・資格情報は増えない。
   - どの realm（開発・組織の本番）でも同じ形で動く。**構成で明示するまで動かない**（既定 Off）。
   - `Report`（検知だけ）を挟めるので、稼働 realm で書き込む前に食い違いを見られる。

   なお「判定の時点でグループから導く」（ScopeUserAttributeSource で属性の代わりにグループを読む）は、判定の意味を変える変更であり、
   トークンのクレームを読む経路（BFF の `BffScopeResolver`）との食い違いを新たに作るため採らない。

## 決定

1. **AuthorizationService に opt-in の定期処理 `DepartmentAttributeSync` を置く。** `DepartmentAttributeSync:Mode` = `Off`（既定・IdP へ問い合わせない）／
   `Report`（検知して記録するだけ）／`Fix`（直す）。周期は `DepartmentAttributeSync:Interval`（既定 1 時間）。値域外の宣言は起動時に落とす
   （打ち間違いを黙って Off へ倒さない。`IdentityAdmin:Provider` / `RetentionAnchor:Source` と同じ）。helm / compose には既定値を置かない。
2. **直すのは「部門グループにちょうど 1 つ属する人」の属性 `department` だけ**であり、値はグループのパスの第 1 セグメント（入れ子は上位に畳む。IADR-0468 と同じ規則）。
   **0 個・2 個以上は未解決として上書きせず、消しもしない。** 照合は序数。
3. **グループは変えない**（逆向きに直す口をポートに持たない）。集め方は `/department` から子グループを辿り、各グループの直接の所属者を属性つきで読む ——
   所属者として現れない人（サービスアカウント・部門なし）は対象に入らない。
4. **ポートに 3 つの口を足す**: `ListSubGroupsAsync`（直下の子）・`ListGroupMembersAsync`（直接の所属者・属性つき・ロールなし）・
   `SetDepartmentAttributeAsync`（`department` 1 キーだけを差し替え、他の属性は多値のまま持ち越し、読み直して確かめる）。
   読み取りの 2 つは**ページを最後まで読む**（IADR-0413 決定 5 の打ち切りと同型の欠陥を作らない）。全置換の `ReplaceAttributesAsync` は使わない
   （単一値キーを先頭 1 値へ畳んだ像を書き戻すと 2 値目以降が消える）。
5. **判定は純関数 `DepartmentAttributeReconciliation.Plan`** に置き、冪等性（直した後の再計画は食い違い 0）を試験で固定する。
   ログは件数と、食い違い・未解決の利用者の IdP 内部 ID・値（制御文字を落とす）を出す。利用者名は出さない。

6. ［2026-09-26 追記 / #1573 監査］**SC-17 の操作との競合を狭める。** Keycloak の `PUT /users/{id}` は表現全体の置き換えで If-Match が無く、
   計画の読み取り〜PUT の間に入った SC-17 の無効化（`enabled=false` ＋ 保持起点）を古い表現で上書きし得る。`SetDepartmentAttributeAsync` は
   計画の読み取りの像（`observed`）を受け取り、**書く直前の GET で有効状態・部門以外の属性が変わっていれば PUT しない**（`Changed`。次の周期で読み直す）。
   残る窓は「その GET から PUT まで」の 1 往復で、ゼロにはできない。運用仕様書にそのまま書いた（従前の「有効状態に触れない」は競合下では正しくなかった）。
7. ［同］**1 人の失敗で周期を止めない。** 書き込みの例外は利用者ごとに捕まえ、IdP 内部 ID つきで記録して続ける。失敗数は周期のまとめ行（失敗があれば Warning）と
   計器 `department_sync.users.total{department_sync.outcome=failed}`・`department_sync.cycles.total{...=completed_with_failures}` に出す。
8. ［同］**周期は `hh:mm:ss` だけ・下限 1 分。** `TimeSpan.TryParse("60")` は 60 日を返すので、数字だけの値は起動時に落とす。

## 結果

- `Fix` を有効にすると、人事連携や管理者がグループを変えれば、次の周期で属性が追随する（遅れの上限は周期）。
- 🔴 **SC-17 の部門欄との関係**: SC-17 は属性 `department` を直接編集できる（計画 SC-17「属性辞書に定義済みの値のみ」）。`Fix` の下では、
  部門グループにちょうど 1 つ属する人の部門欄を SC-17 で別の値へ変えても、次の周期でグループの値へ戻る（決定 3 の「属性はグループに従う」どおり）。
  画面の挙動をどうするか（部門欄を読み取り専用にする・グループの編集へ誘導する等）は計画の問いとして環流する。
- 複数レプリカでは各レプリカが同じ処理を回すが、書く値はグループから決まるので結果は同じである（書き込みが重複するだけ）。
- **稼働環境への適用（利用者・コーディネータの作業）**: 既定は Off のため、本変更をデプロイしても稼働 realm には何も起きない。
  有効化は authorization-service に `DepartmentAttributeSync__Mode=Report` を与えてログで食い違いを確かめ、問題が無ければ `Fix` へ切り替える。
  `identity-admin` の既存権限（`view-users` / `manage-users`）で足りる。realm の構成・マッパー・client・secret は変えない。

## 残るもの

1. SC-17 の部門欄と決定 3 の整合（計画への環流。planning#672 で起票済み）。
2. 部門グループに属さないのに属性 `department` を持つ人は、所属から何も言えないので触らない（検知もしない。全利用者の列挙が要り、打ち切りの問題を持ち込むため）。
   **部門グループからすべて外された人は古い部門の属性を持ち続け、ABAC はその部門として扱う**（決定 3 に対する残差）。計画側へは planning#672 のコメントで伝えた。
