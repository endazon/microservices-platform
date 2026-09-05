---
title: IADR-0366 無人アカウントの clearance とタグは登録者が持つ集合の部分集合かを後段で判定し、外れた値を拒否応答へ載せる
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-09, FR-16, UC-09, SC-12, SC-17, ADR-0004, ADR-0024, ADR-0034, ADR-0036, ADR-0062, IADR-0385, IADR-0386]
author: implementation-agent
created: 2026-09-03
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0062_unattended-account-attribute-subset.md
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md
  - planning:projects/microservices-platform/07_adr/ADR-0034_graph-traversal-abac-enforcement.md
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
---

# IADR-0366: 無人アカウントの属性を登録者の集合の部分集合に限る判定の置き場所と形

- 状態: Accepted
- 日付: 2026-09-03
- 決定者: implementation-agent（実装担当）

## 起点・関連

- 関連する計画書 ID: FR-05 / FR-09 / FR-16 / UC-09 / SC-12 / ADR-0062 / ADR-0036 D-04 /
  ADR-0034 決定 9 / ADR-0024 / ADR-0004
- 関連する実装仕様書:
  `.ai-context/specs/20260903_issue-1185_unattended-account-attribute-subset.md`
- 先行: IADR-0297（SC-12 の 3 層）/ IADR-0269（公開構成とサービスアカウント除外）/
  IADR-0253（認可スコープ契約の選言）/ IADR-0301・IADR-0329（`IIdentityAdminClient` と実 Keycloak）
- issue: #1185（裁定 planning#493。トラッカー #438 / #452）

## コンテキストと課題

計画 ADR-0062（Accepted・2026-08-29）が次を確定させた。

> **決定 2**: 無人アカウントへ割り当てられる機密区分（`clearance`）とタグの集合は、登録操作を行った
> 利用者自身が持つ集合の部分集合でなければならない。
>
> **決定 3**: 判定は後段（MCP クライアント登録を受けるサービス）が行う。
>
> **決定 4**: 身元の口（`/bff/auth/me`）へ `clearance` / `department` を足さない。
>
> **決定 5**: 決定 3 の判定が実装されるまで、無人アカウントへ `confidential` と `restricted` を
> 割り当てない（**判定の実装をもって解除する**）。

着手前に実測した（母集合と陽性対照は作業仕様書 §母集合の取り方）。**部分集合の判定はどこにも無く**
（陽性対照: 同じ走査が ADR-0034 決定 9 の絞りを 14 件当てる）、**McpServer から
AuthorizationService への経路も 1 本も無かった**（陽性対照: DataSourceService の同型経路は 7 件当たる）。

**難所は「登録者が持つ集合」をどう得るかである。** 利用者の `clearance` は Keycloak の単一値であり、
ADR-0062 の受け入れ像（`{public, internal, confidential}` を持つ登録者は `internal` を配れる）は
**単一値の一致では表せない**。かといって階段（`confidential` は `internal` を含む）を実装側の表にすると、
計画 07_abac-attribute-model が**意図的に排除した序数**をコードへ再導入することになる。

## 決定

**決定 1: 判定は McpServer（後段）に置き、1 つの関数へ寄せる。**
`McpClientEndpoints.RejectUnassignableAsync` を登録（`POST /mcp-clients`）と属性差し替え
（`PUT /mcp-clients/{clientId}/attributes`）の**両方**が呼ぶ。同関数は ADR-0034 決定 9 の絞り
（`doc_scope=private-note`）と ADR-0062 決定 2 の部分集合判定を続けて掛ける。
**2 か所に書かない** —— 片方だけ直したときに黙ってズレ、「登録だけ塞いで差し替えが緩い」形になる。

**決定 2: 🔴 「登録者が配れる `clearance` の集合」はポリシー評価器から引く。実装側に階段表を持たない。**
`POST /authz/scope`（action=read）の応答 `AllowedFilters` のうち**キー `confidentiality` の許可値集合**が、
それである。`clearance`（主体側）と `confidentiality`（文書側）は属性辞書上**同一の値域**を共有するため
（07_abac-attribute-model の文書基本属性／利用者属性）、「読める機密区分の集合」がそのまま
「渡してよい `clearance` の集合」になる。**階段の列挙はポリシーにしか無く、それが正である。**

**決定 3: 🔴 `Branches` ではなく `AllowedFilters`（キー単位 union）から読む。**
所有者ベースの分岐は `confidentiality` の条件を持たない。これを「全値許可」と読むと、
**登録者が自分の文書を読めることを根拠に `restricted` を配れてしまう** —— サービスアカウントは
登録者の所有権を継がないため、その読みは**緩む向きに誤っている**。
キー単位 union は当該キーの**属性ベースの到達範囲**を表し、条件を持たない分岐はこのキーの union を
広げない（＝安全側）。IADR-0253 決定 2 の 2026-08-23 追記が警告する混成は**複数キーの連言を 1 本へ
潰す**話であり、**単一キーの許可値の読み出しには当たらない。**

> **［2026-09-05 追記 / #1242］本決定は [IADR-0385](./IADR-0385_registrar-clearance-scope-absent-filter.md) が差し替えた（Superseded by IADR-0385）。**
> 🔴 **「`AllowedFilters` から読み、`confidentiality` が無ければ無制限」は fail-open だった。**
> 契約 `AccessScopeResponse` が「条件無しで許可（全件可）」と定めるのは **`AllowedFilters` が空**の
> ときだけであり、**`owner` だけを持つ**（空ではないが `confidentiality` を持たない）場合は含まれない。
> `ADR-0036` D-01 の所有者ベース `read` ポリシーだけにマッチする登録者は、本決定の読み方では
> `ClearanceUnrestricted = true` へ倒れ、**`restricted` の無人アカウントを作れてしまう**。
> **本決定が避けようとした昇格経路（「自分の文書を読めることを根拠に `restricted` を配る」）は
> 正しい懸念であり、退けた手段（`Branches` を読む）の側が誤っていた** —— 分岐ごとに見て
> **単一キー `confidentiality` の分岐だけを数えれば**、所有者分岐は値を 1 つも足さない。
> **`IADR-0385` 決定 1〜3 が正である。決定 1・2・4〜6 は有効である。**

**決定 4: 対象は `clearance` とタグの 2 キーだけとする。**
ADR-0062 決定 2 が名指しするのがこの 2 つだからである。**`department` は対象外**（同型の昇格になり得るが
計画が決めていない。§結果 に環流候補として残す）。`doc_scope` は ADR-0034 決定 9 の別の規則が見る。
🔴 **対象キーを 1 つも含まない要求では登録者の解決を呼ばない** —— 呼ぶと、属性を持たない無人アカウントの
登録まで認可サービスの可用性に従属する。

**決定 5: タグの「集合」は 1 つの属性値の中のトークン列として読む。**
契約は 1 キー 1 値（`Dictionary<string,string>`。Keycloak の多値属性も先頭 1 値へ畳まれる）であり、
集合を運ぶ器が他に無い。カンマ / 空白で区切って集合として扱い、**順序と余白と大小文字は同値**とする
（集合であって列ではない）。単一値はその 1 要素の集合になるので `clearance` の読み方は変わらない。

> **［2026-09-05 追記 / #1243］本決定の前提の一部は [IADR-0386](./IADR-0386_set-valued-user-attribute-encoding.md) が
> 差し替えた（Superseded by IADR-0386）。** 🔴 **「Keycloak の多値属性も先頭 1 値へ畳まれる」は
> 欠陥であって前提ではなかった** —— `tags: ["sales","hr"]` の `hr` が静かに消え、稼働再測で
> 拒否理由が実際より狭い集合（「登録者が持つタグは 'sales' です」）を告げていた。
> **集合値キー（`tags` / `projects`）は Keycloak の多値属性を正の器とし**、1 キー 1 値の契約へは
> カンマ区切りで載せる。**分割の規則そのもの（カンマ / 空白区切り・順序と余白と大小文字は同値）は
> 変わっていない**が、置き場所は共有契約（`UserAttributeEncoding`）へ移した。
> **本決定の「集合として読む」という結論は有効である。**

**決定 6: 🔴 拒否応答（400 ValidationProblem）に「どの値が外れたか」を値そのもので載せる。**
「権限がありません」で丸めない。**差集合だけを名指しし、外れていない値を混ぜない。**

**存在秘匿の向きと矛盾しない。** 返すのは**登録者本人の集合と、本人が入力した値の差**だけである。
他者の属性も、他者が持つ値域も、文書の存在も返さない。SC-12 はシステム管理者限定であり、
**登録者が自分自身の属性を知ることは秘匿の対象ではない**（むしろ ADR-0062 §結果 は、画面が事前に
示せないことの**唯一の緩和策**としてこの列挙を要求している）。

**決定 7: 「配れない」と「引けなかった」を型と文言で分ける。**
`RegistrarAssignableAttributes.Available` が false のときは「解決できませんでした」と書き、
「あなたはその区分を持っていません」とは書かない。**どちらも配らない点では安全側だが、報告は嘘になる**
（DataSourceService の `IPlatformUserDirectory` と同じ理由・同じ形）。

**決定 8: ADR-0062 決定 5 の暫定（`confidential` / `restricted` を配らない）は本 PR で解除する。**
同決定が「判定の実装をもって解除する」と定めている。**暫定を別途コードとして入れない** ——
入れると `confidential` を読める登録者が `confidential` のサービスアカウントを作れず、
**判定より狭い統制が二重に残る**。暫定が塞いでいた事象（`clearance: internal` の管理者が
`restricted` を配る）は判定の一般規則が覆う。

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | **登録者の属性値そのものと一致するかで見る** | 却下。ADR-0062 が「同一の値しか割り当てられない」を明示的に退けている（登録者より狭いサービスアカウントを作れなくなる） |
| B | 実装側に `clearance` の階段表を持ち、序数で比較する | 却下。07_abac-attribute-model が排除した序数の再導入。**辞書を編集しても表が追随しない** |
| C | **`/authz/scope` の `confidentiality` 許可値集合を配れる集合とする** | **採用**（決定 2） |
| D | AuthorizationService へ「配れる集合」を返す新しい口を足す | 却下（今回は）。既にある 2 つの口で足り、口を増やすと値域の正が 2 か所になる |
| E | 身元の口（`/bff/auth/me`）へ属性を載せ、画面で判定する | **計画が禁じている**（ADR-0062 決定 4）。不在を反射と実応答の 2 面で固定した |

## 結果

- 良い: **実装できないと記録していた規則が実装された。** SC-12 の「一部する」が閉じ、
  ADR-0062 が挙げた昇格経路（`clearance: internal` の管理者が `restricted` のサービスアカウントを作る）が
  塞がった。
- 良い: **階段の正がポリシーひとつに保たれる。** SC-09 で階段を編集すれば判定も追随する。
- 良い: 拒否理由が値つきで画面へ届く（BFF は透過中継、画面は `ApiError.details` をそのまま描く）。
- 悪い（受容）: **後段が認可サービスへ依存する。** 登録・差し替えの要求 1 本につき最大 2 回
  問い合わせる（名簿と認可スコープ）。**対象キーを含まない要求では呼ばない**ので既存経路は無影響である。
  認可サービスが落ちている間は `clearance` / タグの割当ができない（**deny 側**）。
- 悪い（受容）: **登録者の照会は `GET /authz/users`（全件）である。** 単一利用者を引く口が無い。
  規模が問題になったら AuthorizationService へ単票の口を足す。
- 悪い（受容・環流候補）: 🔴 **`department` は本規則の対象外である。** `sales` の管理者が `hr` の
  サービスアカウントを作れる形は残る。ADR-0062 が名指ししていないため実装側で足さない
  （[[IADR-0179]] 決定 2）。**計画へ環流する。**
- 🔴 **ADR-0062 §フォローアップ 3「決定 5 の暫定を解除する契機を実装側が環流すること」は未送付である。**
  本 PR の報告に残し、計画リポジトリの issue で送る。

## 追随（本決定によって誤りになった自分の記述）

- **IADR-0297 §結果・§フォローアップ 2**（「有人アカウントの上限を超えない」は実装していない／
  身元の契約へ登録者の属性上限を載せる）。**後者は ADR-0062 決定 4 が明示的に退けた形である。**
  同 IADR へ日付つき追記で本 IADR を併記した（本文の当時の決定は書き換えない）。
- `docs/screens/SC-12_mcp-client-management.md` / `docs/tests/SC-12_mcp-client-management.md` /
  `McpClientManagementPage.tsx` の「上限を超えない」の記述を是正した。
- `.ai-context/specs/20260828_issue-452_...` は**確定済み記録として書き換えない**
  （当時の計画文言の引用である）。
