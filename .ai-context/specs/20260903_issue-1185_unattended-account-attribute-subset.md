---
title: 無人アカウントの ABAC 属性を登録者の集合の部分集合に限る判定を後段へ入れる
type: spec
status: draft
related_ids: [FR-16, FR-05, FR-09, UC-09, SC-12, ADR-0062, ADR-0036, ADR-0034, ADR-0024, ADR-0004]
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0062_unattended-account-attribute-subset.md
  - planning:projects/microservices-platform/05_screens/01_screens.md#sc-12
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
---

# 仕様書: 無人アカウントの ABAC 属性を登録者の集合の部分集合に限る（issue #1185）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-16（MCP サーバー連携）・FR-05 / FR-09（ABAC）
- ユースケース（UC）: UC-09 基本フロー 1
- 画面（SC）: SC-12（MCP クライアント登録管理）
- 関連 ADR: ADR-0062（本件の裁定）・ADR-0036 D-04・ADR-0034 決定 9・ADR-0024・ADR-0004
- 実装 ADR: IADR-0297（SC-12 の 3 層）・IADR-0269（公開構成とサービスアカウント除外）・
  IADR-0253（認可スコープ契約の選言）・IADR-0301（`IIdentityAdminClient`）・IADR-0329
- 裁定: planning#493（2026-08-29 CLOSED / COMPLETED）。受け皿は本リポジトリ issue #1185

## 目的・背景

ADR-0062 決定 2 は次を定める。

> 無人アカウントへ割り当てられる機密区分（`clearance`）とタグの集合は、登録操作を行った
> 利用者自身が持つ集合の部分集合でなければならない。

決定 3 は判定を**後段（McpServer）**へ置き、決定 4 は**身元の口（`/bff/auth/me`）へ
`clearance` / `department` を足さない**ことを併せて決めている。決定 5 は判定が入るまでの暫定として
`confidential` / `restricted` を配らないことを定め、**判定の実装をもって解除する**としている。

本作業はこの判定を実装し、拒否応答へ**どの値が外れたか**を載せ、SC-12 がそれを読める形で描く。

## 対象範囲

- 対象:
  - McpServer の登録（`POST /mcp-clients`）と属性差し替え（`PUT /mcp-clients/{clientId}/attributes`）
  - McpServer → AuthorizationService の解決経路（登録者の属性・登録者が読める機密区分の集合）
  - 拒否応答（400 ValidationProblem）への外れた値の列挙
  - `/bff/auth/me` が `clearance` / `department` を持たないことの退行防止
  - SC-12 の画面が拒否理由を読める形で描くことの退行防止
- 対象外:
  - **入力中の即時フィードバック**（ADR-0062 §結果 が明示的に諦めている）
  - **画面・BFF での部分集合判定**（決定 3。値域の絞り込みまでが画面・BFF の担当）
  - **`department` への同種の絞り** —— ADR-0062 が名指しするのは `clearance` と**タグ**の 2 つだけである。
    部門をまたぐ割当も同じ形の昇格になり得るが、**計画が決めていないものを実装側で足さない**
    （[[IADR-0179]] 決定 2）。§計画書との差異 へ環流候補として記録する。
  - Keycloak クライアントの実作成（IADR-0297 のフォローアップ 3。本件と別）

## 母集合の取り方（着手前に自分で引いた。陽性対照つき）

`.claude/rules/traceability.repo.md` §是正・追随の母集合の取り方 に従い、issue 本文の実測を
転記せず自分で引いた（`origin/develop` を取り込んだ `d06cf387` 時点。
`git rev-parse --is-shallow-repository` → `false`）。

| # | 引いた対象 | コマンド | 結果 | 判定 |
| --- | --- | --- | --- | --- |
| A | 部分集合判定の有無（陰性） | `grep -rni "subset\|部分集合" --include=*.cs McpServer AuthorizationService` | 1 件（`AbacEvaluatorTests.cs:346` の決定 2 反例コメント。別事象） | **無い** |
| B | A の陽性対照 | `grep -rni "private-note" --include=*.cs McpServer` | 14 件 | 走査は当たる。A の 0 は「無い」 |
| C | McpServer → 認可サービスの経路（陰性） | `grep -rn "AuthorizationService\|authz/" --include=*.cs --include=*.json McpServer` | **0 件** | **経路は新設** |
| D | C の陽性対照（同型の既存経路） | `grep -rn "authz/users" --include=*.cs DataSourceService` | 7 件 | 同型の経路は実在する（`AuthorizationServiceUserDirectory`）。C の 0 は「無い」 |
| E | 「上限を超えない」を書いた追随先 | `grep -rn "上限を超えない" .`（`.git` 除く） | 8 件 | 下表 |

E の内訳と扱い（**誤りの側の文字列で引いた**。規則 9）:

| 箇所 | 扱い |
| --- | --- |
| `docs/screens/SC-12_mcp-client-management.md`（3 箇所） | **是正する**（live な権威文書） |
| `docs/tests/SC-12_mcp-client-management.md`（2 箇所） | **是正する**（live な権威文書） |
| `src/knowledge/frontend/.../McpClientManagementPage.tsx:44` | **是正する**（コード） |
| `.ai-context/adr/IADR-0297_sc12-mcp-client-management.md`（2 箇所） | **日付つき追記で後継を併記する**（本文の決定は書き換えない） |
| `.ai-context/specs/20260828_issue-452_...md` | **書き換えない**（確定済み記録。当時の計画文言の引用） |
| `.ai-context/adr/IADR-0215_...:202` | 別事象（通知の送信数上限）。対象外 |

規則 10（この変更で新たに誤りになる自分の記述）で引き直したもの:
**IADR-0297 フォローアップ 2「身元の契約へ登録者の属性上限を載せ…」は、ADR-0062 決定 4 が
明示的に退けた形である。** 本作業で誤りになるため追記で打ち消す。

## 設計

### 1. 「登録者が持つ集合」をどう得るか

🔴 **序数の語を持ち込まない。** 07_abac-attribute-model は序数比較を意図的に排除しており、
`clearance` の階段は**各段の許可集合を明示列挙する形（＝ポリシー）**でしか表現されていない。
したがって「登録者が持つ `clearance` の集合」を実装側で階段表として持つことはできない
（持てば計画が退けた序数をコードへ再導入することになる）。

**登録者が読める機密区分の集合をポリシー評価器から引く。**

- `POST /authz/scope`（`AccessScopeRequest(userId, userAttributes, "read")`）の応答
  `AllowedFilters` のうち **キー `confidentiality` の許可値集合**が、それである。
- `clearance` と `confidentiality` は**同一の値域**を共有する（属性辞書
  `deploy/local/abac-seed/attributes.json`。計画 07_abac-attribute-model の文書基本属性／利用者属性）。
  したがって「読める機密区分の集合」がそのまま「渡してよい `clearance` の集合」になる。
- 🔴 **`Branches` ではなく `AllowedFilters`（キー単位の union）を使う。** 所有者ベースの分岐は
  `confidentiality` の条件を持たない —— これを「全値許可」と読むと、**登録者が自分の文書を読める
  ことを根拠に `restricted` を配れてしまう。** サービスアカウントは登録者の所有権を継がないため、
  その読みは緩む向きに誤っている。キー単位 union は当該キーの**属性ベースの到達範囲**を表し、
  条件を持たない分岐はこのキーの union を広げない（＝安全側）。
  IADR-0253 決定 2 が警告する「キー単位 union の混成」は**複数キーの連言を 1 本へ潰す**話であり、
  ここで行う**単一キーの許可値の読み出し**には当たらない。
- `Granted == false`（＝どのポリシーにもマッチしない）／応答が引けない → **何も配れない**
  （deny-by-default）。
- `Granted == true` かつ `confidentiality` のフィルタが 1 つも無い → 契約上「条件無しで許可」であり、
  登録者は全機密区分を読める。この場合だけ `clearance` の制限を掛けない。

**タグ**は登録者自身の `tags` 属性値をそのまま集合として扱う（ポリシーの介在は無い）。
契約は 1 キー 1 値（`Dictionary<string,string>`。Keycloak の多値属性も先頭 1 値へ畳まれる。
`KeycloakIdentityAdminClient.ToIdentityUser`）なので、**値をカンマ / 空白区切りのトークン集合として
読む**。単一値はその 1 要素の集合になる。

登録者の属性そのものは `GET /authz/users`（AdminOnly）から採り、`preferred_username`
（`http.User.Identity.Name`）で突き合わせる。**DataSourceService の
`AuthorizationServiceUserDirectory` と同型の経路**であり、呼び出し元の `Authorization` を
そのまま転送する（サービス専用の資格情報を新設しない —— 新設すると SC-12 を触れない主体が
名簿を引ける経路ができる）。SC-12 は AdminOnly なので呼び出し元の資格情報がそのまま通る。

### 2. 判定を置く場所（1 つの関数へ寄せる）

登録と差し替えが**同じ 1 つの関数**を呼ぶ（2 か所へ書くと片方だけ緩む。既存の作法どおり）。

```
McpClientEndpoints.RejectUnassignableAsync(clientId, kind, attributes, registrar, ct)
  ├─ ToolPublicationConfigValidator.ValidateServiceAccountAttributes  … ADR-0034 決定 9（doc_scope）
  └─ ServiceAccountAttributeSubset.Validate                            … ADR-0062 決定 2（部分集合）
```

- `ToolPublicationConfigValidator.ValidateServiceAccountAttributes` は**純関数のまま据え置く**
  （起動時の公開構成検証が同じ関数を呼ぶ。登録者が居ない文脈では部分集合判定を行えない）。
- `ServiceAccountAttributeSubset` も**純関数**である（登録者の集合を引数で受ける）。
  解決の I/O は `IRegistrarAttributeResolver` の後ろに置く。
- 🔴 **要求属性が `clearance` / `tags` のどちらも含まないときは解決を呼ばない。** 呼ぶと
  「属性を持たない無人アカウントの登録」が認可サービスの可用性に従属する（今日は通っている経路）。

### 3. 拒否応答

400 ValidationProblem（`errors.request[]`）。**外れた値そのものを書く。**

- `clearance`: 「`clearance` の値 '<値>' は割り当てられません（登録者が読める機密区分は
  <集合> です）。」
- `tags`: 「タグ '<値>, <値>' は割り当てられません（登録者が持つタグは <集合> です）。」
  🔴 **外れていない値を混ぜない**（差集合だけを名指しする）。
- 解決できないとき: 「登録者の属性を解決できませんでした。…」（**「値が悪い」と混ぜない**）。

**存在秘匿との整合**: 返すのは**登録者本人の集合と、本人が入力した値の差**だけである。
他者の属性も、他者が持つ値域も、文書の存在も返さない。SC-12 は AdminOnly であり、
登録者は自分自身の属性を知る資格を当然に持つ。

### 4. 暫定手段（ADR-0062 決定 5）

**判定が入るので暫定は解除する**（同決定が「判定の実装をもって解除する」と定めている）。
暫定を別途コードとして入れない —— 入れると `confidential` を読める登録者が
`confidential` のサービスアカウントを作れず、**判定より狭い統制が二重に残る**。

暫定が塞いでいた事象（`clearance: internal` の管理者が `restricted` を配る）は、判定の
一般規則が覆う。**解除の環流**（同 §フォローアップ 3）は本 PR の報告に残す。

### 5. 画面（SC-12）

**後段の 400 は BFF が透過し、画面は `toMessages(err)` で `ApiError.details` を描いている。**
したがって新規実装は要らず、必要なのは**退行防止テスト**と、
「上限を超えないは実装できない」と書いた注記の是正である。

## 受け入れ基準

- [ ] 1. `clearance` が `{public, internal}` の登録者が `clearance: confidential` の無人アカウントを
      登録しようとすると 400 になり、本文に `confidential` が名指しで含まれる
- [ ] 2. `clearance` が `{public, internal, confidential}` の登録者は `clearance: internal` の
      無人アカウントを登録できる（**登録者より狭い無人アカウントは作れる**）
- [ ] 3. タグ `{sales, hr}` を持つ登録者がタグ `{sales, finance}` を割り当てると 400 になり、
      **`finance` だけ**が外れた値として列挙される
- [ ] 4. `PUT /attributes` でも登録と同じ判定・同じ文言で拒否される
- [ ] 5. ロールが `platform-admin` で `clearance` が `internal` の利用者は `clearance: restricted` の
      無人アカウントを作れない（**ロールと機密区分は別の軸**）
- [ ] 6. 登録者の属性を解決できないときは `clearance` / `tags` の割当を拒否する（deny-by-default）
- [ ] 7. SC-12 の登録フォームは、後段が返した外れた値を**画面上のテキストとして**表示する
- [ ] 8. `/bff/auth/me` の応答契約は `clearance` / `department` を含まない
- [ ] 9. ADR-0034 決定 9（`doc_scope=private-note` の割当禁止）は従来どおり拒否される
- [ ] 10. 順序違い（`"hr,sales"` と `"sales,hr"`）は同値として扱われる

## テスト方針

| # | 層 | テスト |
| --- | --- | --- |
| 1・2・5 | McpServer xUnit（端点） | 登録者の集合をヘッダで注入し、範囲外／範囲内を各 1 本 |
| 3・10 | McpServer xUnit（ドメイン純関数） | 差集合だけが出ること・順序違いが同値であること |
| 4 | McpServer xUnit（端点） | `PUT /attributes` が同じ文言で拒否する |
| 6 | McpServer xUnit（端点） | 解決不能を注入 → 400（陽性対照: 属性なしの登録は通る） |
| 7 | Vitest | 400（details に `confidential`）を返す MSW → 画面に `confidential` が出る |
| 8 | BFF xUnit | `BffIdentityDto` に `clearance` / `department` が無い（反射。陽性対照つき） |
| 9 | McpServer xUnit | 既存テストを維持（回帰） |

**陽性対照**: 「無い」を主張するテスト（8）には、在る項目（`roles`）を同じ手段で確かめる対を置く。

## 計画書との差異

- 差異: **あり（環流候補・本 PR では実装しない）**
  1. **`department` は本規則の対象外である。** ADR-0062 決定 2 が名指しするのは `clearance` と
     タグだけだが、`department` を跨いだ割当も同型の昇格になり得る（`sales` の管理者が
     `hr` のサービスアカウントを作る）。**計画が決めていないため実装しない。** 環流候補。
  2. **ADR-0062 決定 5 の暫定は本 PR で解除される。** 同 §フォローアップ 3 が「解除する契機を
     実装側が環流すること」を求めている。**環流は未送付**（報告に残す）。
  3. タグは契約上 1 キー 1 値であり、集合はカンマ区切りの値として表現する。計画の「タグの集合」を
     契約の形へ落とすのは実装判断であり、IADR に記録する。

## 未決事項

- 登録者の属性照会は `GET /authz/users`（全件）であり、単一利用者を引く口が無い。
  規模が問題になったら AuthorizationService へ単票の口を足す（本件では既存口を使う）。
