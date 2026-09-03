---
title: IADR-0359 owner の写像表は既定属性と別の器に持ち、写像先の実在を SC-17 側の名簿で検証してから取り込みへ効かせる
type: impl-adr
status: Accepted
related_ids: [FR-01, FR-05, UC-04, SC-06, SC-17, ADR-0036, ADR-0064, ADR-0074]
author: implementation-agent
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0074_owner-mapping-table-container-in-sc06.md
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
  - planning:projects/microservices-platform/06_technical/09_datasource-connectors.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
---

# IADR-0359: owner の写像表の器と解決器

- 状態: Accepted
- 日付: 2026-09-03
- 決定者: implementation-agent（実装担当）

## 起点・関連

- 関連する計画書 ID: FR-01 / FR-05 / UC-04 / SC-06 / SC-17 / ADR-0036 / ADR-0064 / ADR-0074
- 関連する実装仕様書: `.ai-context/specs/20260903_issue-1194_sc06-owner-mapping-table.md`
- 先行: IADR-0019（データソースが原本へ既定 ABAC 属性を付与する）/ IADR-0199（必須属性フェイルセーフ）/
  IADR-0122（契約の非破壊な足し方）/ IADR-0301・IADR-0329（SC-17 の身元管理と実 Keycloak）/
  IADR-0148・IADR-0295（応答のマスクと書き戻しの守り）
- issue: #1194（関連 #752 / #754。環流 planning#518）

## コンテキストと課題

計画 ADR-0074（Accepted・2026-09-03）が「`owner` の解決順②に当たる**データソース単位の写像表**は
SC-06 の登録・更新フォームが持つ」（決定 1）と器の場所を確定させた。決定 4 は「**写像先の利用者識別子は
登録時に実在を検証し、通らない対は保存しない**」、決定 5 は「`db` コネクタへの値搭載は解決器の配備より後」
と定める。

着手前に器の不在を実測した（詳細は作業仕様書 §母集合）。

```console
$ git grep -rniE "ownerMapping|identityMapping|principalMapping|userMapping" -- .
docs/operations/local-sso-recovery-runbook.md:83   ← Discord 通知の UserMapping。無関係
$ git grep -rln "DefaultAttributes|defaultAttributes" -- . | wc -l    # 陽性対照
46
```

**器は無い。** 一方 `DataSourceSyncService.PerItemAttributes` は `SourceItem.UpdatedBy` を
**そのまま `owner` へ写して**おり、コメント自身が「🔴 ここには解決段が無い」と述べていた。
今日はどのコネクタも値を載せないため無害だが、**#752 が値を載せた瞬間に別名前空間の識別子が
`owner` になる**。ADR-0074 決定 5 の先後は、この穴を先に塞げという指示である。

## 決定

### 決定 1: 写像表は `Config` / `DefaultAttributes` と**別の器**にする

`DataSource.OwnerMappings`（`Dictionary<string,string>`・jsonb 列）を足す。

**既定属性へ混ぜない理由は保存の意味論にある。** `DefaultAttributes` は PATCH でも
「**指定したときは全置換**」であり、更新フォームは既存の地図を土台に自分の 3 キーだけを重ねた
**完全な地図**を送っている。同じ辞書に写像表を入れると、**片方の更新がもう片方を消す** ——
しかも 200 が返るので気づけない。器を分けたことで、片方だけの PATCH がもう片方を巻き込まないことを
テストで直接固定できる（`Patch_OwnerMappingsOnly_KeepsDefaultAttributes_AndViceVersa`）。

**上限は設けない。** ADR-0074 §残るもの が「写像表の規模の上限を定めていない」と明記しており、
`Config` / `DefaultAttributes` も上限を持たない。**計画に無い統制を実装が足さない。**

### 決定 2: 写像先は **`username`（`preferred_username`）**であり、IdP の内部 ID ではない

```console
$ git grep -n "NameClaimType" -- src | grep -v Tests
Foundation/Extensions/AuthExtensions.cs:72        = "preferred_username"
Foundation/Session/BffSessionExtensions.cs:160    = "preferred_username"
```

`AbacEvaluator` は `${current_user}` を `AccessScopeRequest.UserId`（＝`Identity.Name`＝
`preferred_username`）へ束縛し、`DocumentBodyIntake.CanWrite` は `owner` をその値と
`StringComparison.Ordinal` で突き合わせる。

🔴 **ここを ID（UUID）にすると、保存も実在検証も通るのに `owner` として 1 度も一致しない。**
実在検証も `IdentityUser.Username` に対して **`Ordinal`** で行い、大小文字を畳まない
（畳むと「保存できたのに一致しない写像」を作れてしまう）。

**無効化された利用者も名簿に含める。** 決定 4 が課すのは「実在すること」であって
「有効であること」ではない —— 退職者が所有者だった文書は所有者を失わない。

### 決定 3: 実在検証は DataSourceService → AuthorizationService の `GET /authz/users` で行い、**呼び出し元の `Authorization` を転送する**

ADR-0074 決定 4 は「ADR-0064 決定 4 が分けた**取り込み経路のクライアント**ではなく
**SC-17 側のクライアント（`view-users`）**で行う」と定める。SC-17 の後段は
`IIdentityAdminClient` → 実 Keycloak（IADR-0329 決定 1 の機密クライアント `identity-admin`）であり、
**既にある口をそのまま使えば決定 4 を満たせる。**

- **ユニット跨ぎ HTTP は既存の作法である。** `GraphAccessResolver` / `WikiAccessResolver` /
  `RagOrchestrator` が同じ名前付きクライアントで `/authz/scope` を叩いている（14 箇所）。
  **本 issue が `/authz/users` の最初のサービス間呼び出しになる**（従来の呼び出し元は BFF の透過中継だけ）。
- 🔴 **サービス専用の資格情報を新設しない。** `/authz/users` は AdminOnly、SC-06 の登録・更新も
  管理者限定なので、**呼び出し元の資格情報がそのまま通る**。専用主体を作ると
  「SC-06 を触れない主体が名簿を引ける経路」が生まれる。
- **Keycloak を直接叩かない。** 叩くと `view-users` を持つ主体が 2 つになり、ADR-0064 決定 4 が
  分けた線が消える。
- **コード既定は `http://authorization-service:8080` にする。** 先行 3 サービスの既定は `:5005` だが、
  **compose も k8s も 8080 で上書きしており、既定値のほうが古い**。古い既定を写すと、配備の上書き漏れが
  「名前解決は通るがポートが無い」形で沈黙する（values.yaml の `bff` に同型の実測がある）。
  helm / compose にも明示の env を足す。

### 決定 4: 「**実在しない**」（400）と「**確かめられなかった**」（502）を分け、400 は RFC7807 で返す

| 事象 | 応答 | 理由 |
| --- | --- | --- |
| 書式違反（空キー・空値） | 400 | 後段へ問い合わせるまでもない（名簿を引かない） |
| 実在しない写像先がある | 400（**どの値かを返す**） | SC-06 は管理者限定の面であり、その管理者は SC-17 で利用者一覧を丸ごと見られる。**伏せても隠せる情報が無い**（ADR-0074 決定 4 は「保存しない」だけを課し、存在秘匿は課していない） |
| 名簿を引けなかった | **502** | 🔴 **「確かめられなかった」を「存在しない」と報告するのは嘘である。** 実在する利用者を「居ない」と言われた運用者は誤った是正をする。**どちらも保存しないので安全側は同じ** |

🔴 **400 の本文は `Results.ValidationProblem`（RFC7807）で返す。`{ error = ... }` では画面に理由が出ない。**
SPA 側の問題本文パーサ（`apiClient.ts` の `parseProblemDetails`）が読むのは
`errors` / `detail` / `title` / `message` の 4 キーだけで、**`error` は読まない**。本サービスの既存の 400
（`ConnectionUriPolicy`）は `{ error }` 形だが、それは**画面に理由が出ていない**ということであって、
真似する理由にはならない。#1194 の受け入れ基準は「保存されず、**理由が表示される**」である。
形は `UserAdminEndpoints.ValidationProblem` と揃える（画面が 2 種類の読み方を覚えなくて済む）。

**写像表が要求に無い・空のときは名簿を引かない。** 写像表を触らない PATCH を、
認可サービスの障害へ道連れにしない。

### 決定 5: PUT（全置換）で `ownerMappings` を省略したら**現状維持**にする（意図的な非対称）

`config` / `defaultAttributes` の「省略は 400」は**契約の初期からある規約**である。
`ownerMappings` は**後から足す項目**であり、ここで必須にすると**既存の PUT クライアントが一斉に 400 になる**
（契約の破壊。#1194 は「契約は非破壊」を要求している）。

**400 の目的（送り忘れで消えるのを防ぐ）は、現状維持でも同じだけ果たせる。**
明示的に空にしたい場合は `{}` を送る。契約 record では**末尾に既定値つきで足す**（IADR-0122 決定 2。
位置引数の並べ替えも既定値なしの追加も破壊的である）。

### 決定 6: 取り込み経路は**写像表を引いた結果だけ**を `owner` にする

`PerItemAttributes(source, item)` は `source.ResolveOwner(item.UpdatedBy)` の結果だけを返し、
**当たらなければ null**（＝上書きなし＝予約値 `system`）を返す。

🔴 **生の識別子が `owner` へ入る経路は消える。** 計画は「別名前空間の識別子をそのまま `owner` へ
入れてはならない」「誤った写像は**偽の所有者**を作り、**裁量制御が意図しない相手に開く**」
「安全側は『解決しない』」と定める（09_datasource-connectors / ADR-0036）。
**これが ADR-0074 決定 5 の先後の前半であり、`db` への値搭載（#752）が乗ってよくなる前提である。**

**①（Keycloak のユーザー検索）は未配備のままでよい。** 解決順は保ったまま②だけを埋める。

### 決定 7: 画面の導線とラベルは**変えない**

写像表は既定属性フォームと登録フォームの中に置く。**新しい画面 ID も新しい権限も作らない**
（ADR-0074 決定 1・案 B の却下理由）。行の導線ボタンは既存の「既定属性」のままにする ——
写像表は自分のラベル付き区画（「所有者の写像（ソース側の利用者 → 基盤の利用者）」）を持っており、
ラベルの改名は画面仕様書・テスト仕様書・14 箇所のテストを動かすだけで、
**計画が求めていない**。

**入力欄は登録側と更新側で同じ部品（`OwnerMappingRows`）を使う。** 2 つ書くと片方が古くなる ——
`department` の登録側（#767）と更新側（#1021）が実際に 2 段階へ分かれ、その間ずっと
「登録時にしか指定できない」状態が残った。

**利用者の候補一覧は出さない。** 値域は IdP が持ち、実装が列挙すると退職者・新入社員のたびに
画面が古くなる。**入力の正しさは後段が名簿で検証する。**

## 結果

- SC-06 の登録・更新フォームから、データソース単位の `owner` 写像表を保守できるようになった。
- 取り込み経路に解決段②が入り、**生の更新者が `owner` へ素通りする経路が消えた**。
- `db` コネクタへの更新者列の搭載（#752）は、ADR-0074 決定 5 の前提を満たしたので着手可能になった。

### 🔴 これで予約値は減らない（ADR-0074 決定 3）

**器と解決器を入れても、現在登録されているデータソースの `owner=system` は 1 件も減らない。**
`filesystem` は構造上更新者を運べず、計画はこれを**意図的な縮退**と裁定している。
②が効くのは `db` コネクタと、`wiki` / `saas` の契約が拡張されたときである。
**件数を完了判定に使わない。** 使うと、構造上減らないものを待って永久に閉じられなくなる。

### 検出しないこと・残るもの

- **同定規則（突合キー・同姓同名・退職者・共有アカウント）は組織側で未確定である**（ADR-0074 §残るもの）。
  **器ができても、何を入れるかは決まっていない。**
- **`wiki` / `saas` の REST 契約へ更新者フィールドを足すかは未決である。** 足さない限り②は空振りする。
- **写像先の実在は「保存の時点」でしか見ない。** 保存後にその利用者が削除されても写像は残り、
  同期は存在しない利用者を `owner` にする。**再検証の仕組みは持たない**（計画が求めていない）。
- **502 の理由文は画面に出ない。** `ApiError.fromStatus` は 5xx に `details` を渡さないため、
  画面は既定文言（「サーバでエラーが発生しました。」）を出す。**保存されないことは変わらない。**
- **`department` の①（フォルダ → 部門の写像）は本 IADR の射程外である**（#754。
  planning#372 が「部門コードの値域が定まるまで写像を行わない」と禁じている）。
