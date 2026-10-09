---
title: IADR-0473 利用者属性 department は AuthorizationService の opt-in の定期処理が部門グループ所属へ合わせて直す（ちょうど 1 つのときだけ・グループは変えない・既定 Off）
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-09, UC-05, SC-17, ADR-0115, ADR-0116, ADR-0026, ADR-0088, IADR-0301, IADR-0329, IADR-0369, IADR-0385, IADR-0413, IADR-0428, IADR-0468, IADR-0472, IADR-0477]
author: claude
created: 2026-09-26
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0115_department-domain-is-realm-group-and-default-from-registrant.md 決定 3
  - planning:projects/microservices-platform/07_adr/ADR-0088_authz-resolves-user-attributes-itself.md 決定 1
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md §利用者属性
  - planning:projects/microservices-platform/07_adr/ADR-0116_sc17-department-edits-group-membership.md 決定 2（2026-09-27 追記 / #1609）
  - planning:projects/microservices-platform/07_adr/ADR-0116_sc17-department-edits-group-membership.md 決定 1（2026-09-27 追記 / #1610。SC-17 の部門欄は部門グループの所属を変える）
  - planning:projects/microservices-platform/07_adr/ADR-0116_sc17-department-edits-group-membership.md 決定 1 の 2026-10-09 補完・決定 4 の 2026-10-09 追記（2026-10-09 追記 / #1783。稼働 PoC の配備値は Fix・暫定手段の訂正）
related_specs:
  - ../specs/20260926_issue-1573_department-attribute-follows-group.md
  - ../specs/20260927_issue-1609_department-clear-and-dictionary-from-realm.md
  - ../specs/20260927_issue-1610_sc17-department-edits-group-membership.md
  - ../specs/20261009_1783_dept-sync-poc-fix.md
  - ../specs/20261009_1850_dept-sync-carry-over.md
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

   > **［2026-09-27 追記 / #1609］計画 ADR-0116 決定 2 により、0 個の扱いを改める。** 部門グループに 1 つも属さない利用者の属性 `department` は
   > **消す**（判定 `Orphaned`・`Fix` だけが書く。`Report` は記録するだけ）。2 個以上は従来どおり上書きせず消しもしない。
   > - 🔴 **0 個の人は全利用者の列挙からしか見つからない**（決定 3 の集め方では所属者として現れない）。ポートに全利用者の列挙
   >   `ListAllUsersAsync`（`UserEnumeration(Users, Complete)`。属性つき・ロールなし・サービスアカウントを返さない）を足した。
   >   **ページを最後まで読み、ページの失敗は例外、上限（`MaxEnumeratedPages` = 1000 ページ ＝ 10 万人）に達したら `Complete = false`** を返す。
   > - 🔴 **列挙が例外・未完了の周期は、0 個の人を 1 人も計画に入れない**（原則 A: 列挙に現れなかった人は「居ない」ではなく「読めなかった」。
   >   部分的な列挙から 0 個を推定しない）。未完了は計器 `department_sync.enumeration_incomplete.total{department_sync.reason=page_failed|truncated}` と
   >   Error ログで知らせ、1 つ属する人の是正は続ける。アラート `DepartmentSyncNotCorrecting` の式に 3 つ目の項として足した（4 か所）。
   >   [[IADR-0413]] 決定 5 の「黙った打ち切り」をここへ持ち込まない。
   > - **消す直前にその人の所属を個別に読み直す**（`GetUserGroupsAsync`）。部門グループが見つかれば消さず `Changed`（見送り）として数える ——
   >   所属者の一覧はページ送り（offset）であり、並行した所属の変更でページの境目の人が 1 人飛ぶことがある。飛んだ人を 0 個と読んで消さない。
   > - 消す書き込みは新しい口 `ClearDepartmentAttributeAsync`（`department` 1 キーだけを消し、他の属性は多値のまま持ち越す）。決定 6 の競合の規則
   >   （書く直前の読み直しで有効状態・部門以外の属性が変わっていれば PUT しない）を同じく当て、読み直して残っていれば例外（fail-closed）。
   > - 🔴 **サービスアカウントは消さない**（Keycloak の表現の `serviceAccountClientId`、利用者名の `service-account-` 接頭辞）。開発用 realm の
   >   `service-account-abac-seeder` は部門グループなしで `department` を持つ（実測）。「部門グループに属さないサービスアカウントは対象に現れない」を保つ。
   > - 計器の利用者の結末に `cleared` を足した。全員が見送られた周期（決定 10）の判定には消す試みも含める。
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
8. ［同・差分監査で改めた］**周期は `TimeSpan.TryParseExact(..., "hh\:mm\:ss")` で読み、`00:01:00`〜`23:59:59`。**
   `TryParse` は `60` を 60 日、`24:00:00` を 24 日と読み、50 日超は `PeriodicTimer` が起動後に落ちる。`hh` は 0〜23 しか受けないので、誤読も上限超過も起動時に落ちる。
9. ［同・差分監査］**取り消し（ホストの停止）は利用者ごとの失敗として数えず、周期ごと中断する。** 周期ごとの例外は計器 `cycles.total{aborted}` に数える。
10. ［同・差分監査］**直そうとした全員が `Changed` で見送られた周期は `all_skipped_changed` として Warning と計器に出す。** 所属者の一覧（`/groups/{id}/members`）と
    利用者の個別取得（`/users/{id}`）で属性のキー集合が違う realm では、毎回 `Changed` になり `Fix` が黙って誰も直さないため。運用仕様書に試験利用者 1 人での確認手順を書いた。
11. ［同・差分監査］**アラート `DepartmentSyncNotCorrecting`（warning）を 4 か所（compose / k8s の Prometheus と Grafana）に置いた。**
    式は `failed` の利用者と `aborted` / `all_skipped_changed` の周期の直近 1 時間の前進量を `or vector(0)` で足して `> 0`、Grafana は `noDataState: OK` と
    評価器 `gt 0`（`> 0` の後に残る値は正の前進量なので正しい。`== 0` と `gt 0` の組み合わせ〔#1577〕ではない）。
12. ［同・差分監査］計器は 1 サービス 1 Meter の慣行に揃え、`microservices-platform.authorization-service` に載せる。

13. ［2026-09-27 追記 / #1610］**計画 ADR-0116 決定 1 により、SC-17 の部門欄は部門グループの所属を変える。** 本 IADR の「属性はグループに従い、
    グループは変えない」（決定 3）の境界は保ったまま、グループを変える口を **SC-17 の部門欄だけ**に置く。
    - **ポートに 3 つの口を足す**: `FindByIdAsync`（内部 ID で 1 人。属性つき・ロールなし）・`JoinGroupAsync` / `LeaveGroupAsync`
      （Keycloak `PUT` / `DELETE /users/{id}/groups/{groupId}`。冪等。404 は false、それ以外の失敗は例外）。名前に作成の禁止語を使わない。
      🔴 **同期はこの 2 つの書き込みを使わない**（同期の試験の偽物は呼ばれたら落とす）。権限は既存の `manage-users` の範囲で、主体・ロールは増やさない。
    - **SC-17 の経路から属性 `department` を書かない。** 端点 `PUT /authz/users/{id}/attributes` は要求に `department`（大小文字無視）があれば 400、
      必須は `clearance` だけにした（ADR-0116 決定 1 の「部門なし」）。ポートの `ReplaceAttributesAsync` は保持起点（[[IADR-0428]]）と同じく
      現在の `department` を多値のまま持ち越す（差し替えは全置換であり、持ち越さないと機密区分上限を直しただけで部門が消える）。
    - **部門の変更の口** `GET` / `PUT /authz/users/{id}/department`（BFF は透過）。選択肢は [[IADR-0477]] と同じ realm の読み取り
      （`AttributeDictionary.ReadDepartmentDomainAsync`）。計画は純関数 `DepartmentMembershipPlan`（コードは `CodeOf` で入れ子を上位に畳む。
      部門の木の外・根は触らない。目的と同じなら何もしない）。
    - 🔴 **2 個以上の部門グループに属する人は SC-17 からも変えない**（409・理由つき・何も書かない）。ADR-0116 はこの人を扱わない
      （決定 2・フォローアップ 4）。1 つを選ばせて残りを外すと、管理者が組んだ複数所属を黙って崩す。
    - 🔴 **先に入れてから外す**（途中で止まっても 0 個にならない順）。外す途中の失敗は補償する: 外したグループと失敗したグループへ入れ直し（冪等）、
      **入れ直しがすべて成功したときだけ**目的のグループから外す。入れ直しが 1 つでも失敗したら目的のグループを残す（**黙って 0 個にしない**。原則 A）。
      入れる段の失敗は目的のグループから外すだけ（まだ何も外していない）。いずれも読み直した所属を 502 の理由に載せ、元に戻せなかったときは Error ログ。
      書き込みの途中は要求の取り消しで止めない（入れた後・外す前に止めると 2 つに属したまま残る）。書いた後に所属を読み直し、期待と違えば 409。
    - 同期（`Fix`）は次の周期で属性を新しい部門グループへ追随させる（戻さない）。`Off` / `Report` の環境では属性は追随しない（画面が「未反映」を示す）。
    - 監査: 所属の変更は Keycloak の管理イベント（`GROUP_MEMBERSHIP`）に残る（SC-17 の他の操作と同じ記録先）。サービスのログに IdP 内部 ID と変更前後のコード。

## 結果

- `Fix` を有効にすると、人事連携や管理者がグループを変えれば、次の周期で属性が追随する（遅れの上限は周期）。
- 🔴 **SC-17 の部門欄との関係**: SC-17 は属性 `department` を直接編集できる（計画 SC-17「属性辞書に定義済みの値のみ」）。`Fix` の下では、
  部門グループにちょうど 1 つ属する人の部門欄を SC-17 で別の値へ変えても、次の周期でグループの値へ戻る（決定 3 の「属性はグループに従う」どおり）。
  画面の挙動をどうするか（部門欄を読み取り専用にする・グループの編集へ誘導する等）は計画の問いとして環流する。
  - ［2026-09-27 追記 / #1610］**解消した。** SC-17 の部門欄は部門グループの所属を変え、属性を書かない（決定 13）。同期は属性を新しいグループへ追随させる。
- 複数レプリカでは各レプリカが同じ処理を回すが、書く値はグループから決まるので結果は同じである（書き込みが重複するだけ）。
- **稼働環境への適用（利用者・コーディネータの作業）**: 既定は Off のため、本変更をデプロイしても稼働 realm には何も起きない。
  有効化は authorization-service に `DepartmentAttributeSync__Mode=Report` を与えてログで食い違いを確かめ、問題が無ければ `Fix` へ切り替える。
  `identity-admin` の既存権限（`view-users` / `manage-users`）で足りる。realm の構成・マッパー・client・secret は変えない。

## 残るもの

1. SC-17 の部門欄と決定 3 の整合（計画への環流。planning#672 で起票済み）。
2. 部門グループに属さないのに属性 `department` を持つ人は、所属から何も言えないので触らない（検知もしない。全利用者の列挙が要り、打ち切りの問題を持ち込むため）。
   **部門グループからすべて外された人は古い部門の属性を持ち続け、ABAC はその部門として扱う**（決定 3 に対する残差）。計画側へは planning#672 のコメントで伝えた。
   - ［2026-09-27 追記 / #1609］**解消した。** 計画 ADR-0116 決定 2 の裁定で、0 個の人の属性は消す（決定 2 の追記）。全利用者の列挙は読み切れた周期だけ使い、
     読み切れなければ消さずに計器で知らせる。**2 個以上の人の扱いは計画でも対象外のまま**（ADR-0116 フォローアップ 4）。
3. ［2026-09-27 追記 / #1609］SC-17 の部門欄（残るもの 1）は、計画 ADR-0116 決定 1 で「部門グループの所属を変える」と裁定された（#1610）。
   属性辞書の部門の値は realm の部門グループから導く形になった（[[IADR-0477]]）。
   - ［2026-09-27 追記 / #1610］実装した（決定 13）。**2 個以上の部門グループに属する人の扱いは計画でも実装でも対象外のまま**（SC-17 は変えずに理由を示す）。

## ［2026-10-09 追記 / #1783］稼働 PoC の配備値は `Fix`・コードの既定は `Off` のまま・暫定手段の訂正（計画 ADR-0116 決定 1 の 2026-10-09 補完。planning#741 項目 4）

> 上の本文・追記は書き換えない。本節は利用者裁定（2026-10-09。planning#741 項目 4・第 4 回全体監査 B-12）を受けて決めたことだけを足す。
> 作業仕様書 `20261009_1783_dept-sync-poc-fix` と対になる。計画は ADR-0116 決定 1 の 2026-10-09 補完・決定 4 の表の直後の 2026-10-09 追記・フォローアップ 5
> （完了記録 `projects/microservices-platform/10_feedback/20261009_audit-b12-b13-dept-sync-ingestion.md`）。

1. **稼働 PoC の配備値は `Fix` とする。決定 1 の既定（`Off`）は変えない。** 計画は「コードの既定を `Fix` にしない理由」を、段階的な適用（`Report` を経る）を飛ばさないこと、
   配備するだけで稼働 realm（AST の PoC と共有）への書き込みが始まる形にしないこととした。本 IADR の §結果「稼働環境への適用」（`Report` で確かめてから `Fix`）が、
   そのまま稼働 PoC の手順になる。
2. **値はリポジトリの values に置かない。運用者が稼働 PoC の helm リリースへ `helm upgrade --reuse-values` で入れる。** 稼働 PoC は `scripts/k8s-local-up.sh` が
   `deploy/local/values-local.yaml` で立てるが、同じ上書きを CI の使い捨てスタック（`ci.yml`・`integration-stack.yml`・`cutover-rehearsal.yml`）も使う。
   `values-local.yaml` に `Fix` を書くと、計画が「変えない」とした helm の既定を CI の側でも変えることになる。env は `services.authorization.extraEnvAppend` へ足す
   （`extraEnv` へ `--set` すると既存の `IdentityAdmin__*` が消える）。`kubectl set env` / `kubectl patch` は使わない（Helm 4 のサーバサイド apply で field manager が奪われる。IADR-0377）。
   手順（`Report` → ログ → `Fix` → 試験利用者 1 人）は運用仕様書 `docs/operations/operations.md` §利用者の部門属性を部門グループへ合わせる同期の有効化 の「稼働 PoC を `Fix` にする手順」に置いた。
3. **暫定手段の訂正を写す。** 計画の旧い暫定手段「SC-17 で属性を同じ値にそろえる」は決定 13（SC-17 は属性を書かない）の後は成り立たない（計画は例外 3 で訂正した）。
   `Off` / `Report` の間は、Keycloak の管理コンソールで属性 `department` を手で直し、`Report` の後はログで食い違いが残っていないことを確かめる。運用仕様書の
   「属性を別の手段でそろえる」をこの手順へ具体化した。決定 2 の追記の「グループから外したときは属性も手で消す」は `Off` / `Report` の間そのまま成り立つ。
4. **実装（コード・helm values・compose）は変えない。** 本節の時点（MSP `origin/develop` `c66c5641`）で稼働 PoC の同期は `Off`（未適用）であり、適用は運用者の作業である。

- 🔴 **残るリスク**: `scripts/k8s-local-up.sh` の再実行（`--reuse-values` なし）は上書きを外し、同期を `Off` へ黙って戻す（`Off` では計器の系列が無く
  `DepartmentSyncNotCorrecting` も鳴らない）。運用仕様書に「再実行の後は当て直す」と書いた。起動器が現行の値を引き継ぐ形（`ISTIO` と同じ「明示 ＞ 現行 ＞ 初回の既定」。
  IADR-0488）にすれば塞がるが、本 issue の範囲（裁定の反映）を超えるため作っていない。

## ［2026-10-09 追記 / #1850］起動器の再実行は部門属性の同期の値を引き継ぐ（上の「残るリスク」を塞ぐ）

> 上の本文・追記は書き換えない。本節は PR #1848 の独立監査 🟡-1 から分離した #1850（利用者裁定 2026-10-09: すぐ実装する）で決めたことだけを足す。
> 作業仕様書 `20261009_1850_dept-sync-carry-over` と対になる。形は [[IADR-0488]]（`ISTIO` の「明示 ＞ 現行 ＞ 初回の既定」と fail-closed）の直接の当てはめであり、新しい IADR は起こさない。

1. **`scripts/k8s-local-up.sh` に明示の env `DEPT_SYNC_MODE`（`Off` / `Report` / `Fix` / 未指定）を置く。** それ以外の綴りは副作用より前に拒否する。
2. **選び方は 明示 ＞ 現行（`helm get values msp` の `services.authorization.extraEnvAppend` の `DepartmentAttributeSync__Mode`）＞ 何も足さない。**
   現行の値はサービスと同じく大小文字・前後の空白を許して正規化する。`DEPT_SYNC_MODE=Off` は helm を読まず何も足さない（従来の起動器と同じ）。
   リリースが無い（初回・CI）・要素が無いときも何も足さない（CI の描画は従来とバイト等価）。
3. **宣言するときは authorization の `extraEnvAppend` を丸ごと与える values ファイルを [6/7] の `-f` に足す。** 中身は「現行の他の要素（`helm get values -o yaml` の字面のまま）＋
   `DepartmentAttributeSync__Mode`」。添字 0 の `--set` は他の要素を消すか古い値と同名で並び、他の要素を `--set` で写し直すと `"false"` が bool に化けて env の値が消える
   （`helm-dept-sync-values.test.js` の対照で実測）。リポジトリの values（チャート既定・`values-local.yaml`）は authorization の `extraEnvAppend` を持たない
   （持ち始めたら `k8s-local-up.test.js` の前提の試験が赤になる。そのときは足し合わせ方を決め直す）。
4. **読めないときは止める（fail-closed）。** helm に届かない・`get values` が失敗する × 未指定・`Report`・`Fix` は [2/7] の前に止まる（未指定で進むと `Off` へ黙って戻し、
   `Report` / `Fix` で進むと他の要素を黙って消す）。要素が壊れている（値域外・値でない形・同名が 2 つ以上・流れ形式）× 未指定も止まり、明示なら置き換えて進む。
5. **読みはメッシュの判定と共有する（1 回）。** 読む口は `scripts/lib/mesh-mtls-mode.sh` の `current_release_values`（`current_mesh_mtls_mode` から切り出した。振る舞いは同じ）、
   判定は `scripts/lib/dept-sync-mode.sh` の純関数。選んだ値と出どころ（明示・現行・既定）を起動器のログに出す。

- 負: 既定経路（`DEPT_SYNC_MODE` 未指定）は、`ISTIO=0` でも `helm list` を 1 回読むようになる（リリースが在れば `get values` も）。全く読まないのは `DEPT_SYNC_MODE=Off` を併せたとき（[[IADR-0488]] への同日の追記）。
- 残余: 未指定 × 現行に `DepartmentAttributeSync__Mode` が無いときは何も足さないので、authorization の `extraEnvAppend` の他の要素（運用者が helm で足したもの）は従来どおり再実行で外れる。
  `DEPT_SYNC_MODE=Off` の明示も同じ。本件の射程は同期の値であり、他の要素は値を宣言し直すときに消さないことだけを保証する。
- 残余: 読みと [6/7] の間に別の操作がリリースを書き換える窓（TOCTOU）は扱わない（[[IADR-0487]] と同じ）。
- 試験: `k8s-local-up.test.js` の #1850 節（窓の表 P1〜P8・値域・壊れた要素・読み 1 回・判定表・リポジトリの values の前提）、`helm-dept-sync-values.test.js`（実物のチャートで描画・対照）。変異の結果は作業仕様書。
