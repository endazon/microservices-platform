---
title: IADR-0160 BFF の全端点に認証を要求し、検査器が「無認証」を見分けられるようにする
type: impl-adr
status: Accepted
related_ids:
  - FR-03
  - FR-04
  - FR-06
  - FR-07
  - NFR-09
  - UC-01
  - UC-02
  - UC-03
  - SC-01
  - SC-03
  - SC-05
  - SC-08
  - ADR-0004
  - ADR-0005
  - IADR-0009
  - IADR-0039
  - IADR-0044
  - IADR-0128
  - IADR-0156
  - IADR-0158
author: claude
created: 2026-08-10
updated: 2026-08-10
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/05_screens/01_screens.md
---

# IADR-0160: BFF の全端点に認証を要求する（#656）

- 状態: Accepted
- 日付: 2026-08-10
- 決定者: claude（実装）

## 起点・関連

- **NFR-09**（認証・認可。Must）／FR-03・FR-04・FR-06・FR-07／SC-01・SC-03・SC-05・SC-08
- 実装 issue: **#656**（出所は #525 / PR #657 の母集合走査）
- 作業仕様書: [20260810_issue-656](../specs/20260810_issue-656_bff-endpoint-authentication.md)

## コンテキストと課題

**`/bff/*` の 49 端点のうち 9 端点に認可が無く、無認証で到達できた。**

計画 `02_requirements` の **NFR-09（Must）** は「暫定: **エッジ（BFF）で OIDC/JWT を担保**」と定め、
§暫定運用の注記も同じことを重ねて書いている。さらに ［2026-08-04 更新］の追記が
「**恒久像の残課題は『全 API の OIDC/JWT 認証』である**」としており、
**計画はエッジ側が達成済みという前提で恒久側の議論をしている**。本件はその前提が崩れていた。

**救済経路も無かった**（実測）——`AddAuthorization` に `FallbackPolicy` は無く、
`deploy/` 配下に Istio の `RequestAuthentication` / `jwtRules` は 0 件である。

### 実害は 0 だった。しかし防御が 1 枚しかなかった

無認証呼び出しは `BffScopeResolver` が `userId="anonymous"` / 属性なしで解決し、
`AbacEvaluator` にマッチするポリシーが 0 件 → `Granted=false` → 空応答（文書詳細は 404）へ縮退する。
`RagOrchestrator` も `!Granted` の時点で `EmptyAnswer()` を返すので**検索も LLM 呼び出しも走らない**。

**しかしその安全は「利用者条件が空のポリシーが 1 件も無いこと」だけに支えられていた。**
`AbacEvaluator.MatchesUserConditions` は**条件が空なら全利用者にマッチする**。
SC-09 から管理者が利用者条件を持たないポリシーを 1 件作れば、**コード変更なしで無認証の呼び出し元へ開く**。
[IADR-0044](./IADR-0044_backend-service-authorization-defense-in-depth.md) の多層防御に反する。

## 決定 1: 端点ごとに認可を分ける（**一律に足さない**）

| 端点 | 認可 | 根拠 |
| --- | --- | --- |
| `POST /bff/search`・`POST /bff/attribute-values` | **認証のみ** | 計画 `05_screens`「利用者グループ（SC-01〜04・SC-08）は **ABAC の権限内で全利用者が利用できる**」 |
| `POST /bff/analysis/{ask,analyze,ask/stream}` | **認証のみ** | 同上 |
| `GET /bff/documents/{id}`・`/content`・`/versions` | **認証のみ** | **SC-03**（文書詳細）。SC-01 の出典クリックから一般利用者が遷移する |
| **`GET /bff/documents`** | **admin ＋ operator** | **SC-05**。計画 `05_screens`（2026-08-05 の裁定）「SC-05/06/07 = **閲覧は管理者・運用者**」 |

**一律に足すとどちらかが壊れる。** 一律「認証のみ」なら SC-05 の閲覧ロールを実装が満たさないまま残り、
一律「ロール」なら SC-03 の出典が開けなくなる。

### `GET /bff/documents` だけ違う理由は**呼び出し元を引いて決めた**

`grep -rn 'BffDocumentList' --include=*.ts --include=*.tsx src/ | grep -v /generated/` の結果、
**呼び出し元は `sc05-documents/useDocumentAdmin.ts` ただ 1 つ**である。
しかも**画面側は既に `RequireRole anyOf={[Admin, Operator]}` で絞られていた**——
**#628 / #629 で 2 度直したのと同じ型**（画面は絞れているが API は誰でも通る）だった。

> **起票時の自分の判断は誤っていた。** #656 の受け入れ基準 1 は「ロールは要求しない」と書いていたが、
> それは 9 端点すべてを利用者機能と見た推測である。**呼び出し元を引いたら 1 件だけ違った。**

## 決定 2: 404 の存在秘匿は壊さない（[IADR-0039](./IADR-0039_datasource-management-bff-and-role-gating.md) 決定 3）

`GET /bff/documents/{id}` は権限外・不在ともに **404** を返す（[IADR-0009](./IADR-0009_wiki-browsing-404-hides-existence.md)）。
`RequireAuthorization` を付けると**無認証は 401** になるが、**これは存在秘匿を壊さない** ——
401 が示すのは経路の存在であり、**その経路は公開契約に載っている**。秘匿の対象は**文書の存在**で、
それは認証済みの権限外で 404 のままである。

**`/bff/admin/config` とは事情が違う。** あちらは `ConfigViewer` を持たない**認証済み利用者**にも
404 を返して**画面の存在ごと**隠す設計であり、`RequireAuthorization` を付けると
無認証が 404 到達前に 401 で短絡して隠す対象が漏れる。**だから 3 本は対象外である。**

## 決定 3: 検査器に `requiresAuth` を足す（[IADR-0156](./IADR-0156_bff-authz-contract-checker.md) の穴）

`check-bff-authz-docs.js` の `rolesFromStatement` は
**「認可属性なし」と「`RequireAuthorization()`（認証のみ・ロール不問）」をどちらも `null` へ畳む**。

| 実装 | 実効ロール | `x-roles` | 従来の判定 |
| --- | --- | --- | --- |
| `RequireAuthorization()`（#521 の `/bff/feedback`） | `null` | `[]` | OK |
| **認可属性なし**（`/bff/search`） | `null` | `[]` | **OK（素通り）** |

**ロールとは別の軸**として `requiresAuth` を持たせ、**無認証は契約と一致していても違反**とする
（`kind: 'anonymous'`）。`CLAUDE.md`「検査器・規約の追加は**同型の事故が 2 回起きたら**」——
**#521 が 1 例目、本件が 2 例目**である。

### ★ 判定は「ミドルウェアの有無」ではなく「認可の有無」で行う

**素朴に `.RequireAuthorization(` の有無で判定すると、`/bff/admin/config` の 3 本を誤検出する**
（決定 2）。**群・端点・ハンドラ内 `AuthorizeAsync`・private ヘルパの 4 経路**のいずれかに認可が在れば真とする
——`collectImplementation` が既に持っている経路をそのまま使う。
**実データで 3 本が `requiresAuth = true` になることをテストで固定した。**

### ★ 検査器自身の盲点も 1 つ塞いだ（**PR を出したあとに自分で見つけた**）

本検査器は**群を辿って認可を合成する**設計なので、`app.MapVerb("/bff/...")` のように
**群に属さない形で書かれると `requiresAuth` を判定できず、黙って読み飛ばす**
（`collectImplementation` の `if (!group) continue`）。
**決定 3 が立てた不変条件（「無認証の `/bff/` 端点は存在しない」）が、そこだけすり抜ける。**

`[Authorize]` 属性は BFF に **0 件**（実測）なので認可の経路はこれで尽きているが、
群外の `app.Map*` は**実在する** —— `ConfigBffEndpoints` の `/internal/config/drift-run` である。
これは `/bff/` ではなく**メッシュ内部限定**（ClusterIP ＋ NetworkPolicy / mTLS が防御・
`ExcludeFromDescription`）で、ArgoCD の PostSync フックが叩く。**対象外で正しい。**

したがって **`app.MapVerb` かつパスが `/bff/` で始まるものだけ**を
`kind: 'ungrouped'` として報告する（対処は `MapGroup` 配下へ移すこと）。
現状の実データでは 0 件であり、**この検査は是正ではなく予防である**。

> **★ その予防を固定したつもりのテストが、最初は機能していなかった（レビュー 🟡 で判明）。**
>
> ```js
> const src = 'app.MapPost("/bff/rogue", async () => Results.Ok());';
> const eps = authz.collectImplementation.length >= 0 ? [{ /* 手組み */ }] : [];
> ```
>
> `collectImplementation.length` は**関数の仮引数の個数**であり常に真。`src` はどこにも渡っていない。
> すなわち**検証していたのは `findViolations` が `ungrouped: true` を報告することだけ**で、
> **検出そのもの（正規表現で群外の `/bff/` を拾う経路）は 1 度も走っていなかった**
> ——実データに群外の `/bff/` 端点が無いため、実データ経由でも到達しない。
>
> 一時ディレクトリへ `.cs` を書いて `collectImplementation` に**実際に読ませる**形へ直した。
> **変異試験で差を確認した**——検出を `if (false)` へ潰すと、直したテストは落ちる（元のテストは緑のまま）。
>
> **手で変異試験は回していた**（`/internal/config/drift-run` を `/bff/rogue` へ書き換えて検出を確認した）
> ので**機能自体は動いていた**。**動くことと、動き続けることが固定されていることは別である。**
> 同型をリポジトリ全体で引いたところ**本件 1 件のみ**だった（`scripts.repo.test.js:2204` の `src` は
> `statementFrom` へ実際に渡っている）。

### 契約側に新しいフィールドは足さない

「無認証」を契約で宣言する形（`x-anonymous: true` 等）も検討したが、**採らない** ——
本 PR 後は**無認証の端点が 0 件**になるので（実測）、
**「`/bff/*` に無認証の端点は存在しない」を不変条件にすれば足りる**。
宣言できる形にすると「宣言すれば通る」ことになり、**塞ぐべきものに逃げ道を与える**。

## 決定 4: 後段サービスは触らない（**片側だけであることを明記する**）

BFF を塞いでもクラスタ内から後段（RetrievalService / AiAnalysisService / DocumentService）へ
直接到達する経路は残る。**多層防御としては片側だけである。**
恒久側（全 API OIDC/JWT）は **#458** が持つ。本 ADR は**暫定側（エッジ＝BFF）の未達**に閉じる。

**「塞いだので安全になった」と読ませない。** 塞いだのは計画が暫定として定めた 1 枚だけである。

## 結果

### 変異試験（4 通り。いずれも復旧後に緑を確認）

| 変異 | 落ちるもの |
| --- | --- |
| `/bff/search` の `RequireAuthorization` を外す | 401 の Theory 1 件 |
| 文書一覧の `RequireRole` を外す | `DocumentList_AsNonPrivilegedRole_IsForbidden` |
| 文書読み取り群の `RequireAuthorization` を外す | 401 の Theory **3 件**（一覧は端点側の `RequireRole` が残るので落ちない——**期待どおり**） |
| 分析群の `RequireAuthorization` を外す | **検査器が 3 件を報告して exit 1** ＋ 401 の Theory 3 件 |

### ★ 検査器が自分の追随漏れを 1 件捕まえた

文書一覧を admin ＋ operator へ絞った直後、`openapi.yaml` の `x-roles` は `[]` のままだった。
**検査器が `get /bff/documents` の不一致を名指しで報告して落ちた。**
[IADR-0156](./IADR-0156_bff-authz-contract-checker.md) が意図したとおりに働いており、**今回は自分がその網に掛かった側**である。

### ★ 「通る側」の主張が弱いと、静かに壊れても緑のままになる

SC-03 の詳細は当初「**403 でも 401 でもない**」という主張にしていた。スコープ外・不在が 404 なので
環境依存を避けたつもりだったが、**404 が素通りする**。実測で差が出た:

| 変異 | 弱い主張（`NotBe(403/401)`） | 強い主張（`Be(200)`） |
| --- | --- | --- |
| E: 読み取り群へ誤ってロールを足す | 落ちる | 落ちる |
| **F: 詳細を常に 404 へ倒す** | **素通り** | **落ちる** |

テスト基盤は `/authz/scope` を `Granted=true`・フィルタ空（＝全件許可）で、`/documents/{id}` を
文書ありでスタブしている（実測）ので、**200 を直接主張できた**。
**「環境依存を避ける」を理由に弱い主張を選ぶ前に、基盤が何を返すか読むこと。**

### 拒否の側だけでは足りない

「全部拒否」でも赤くならないので、**通る側と対で固定した**——
非特権ロールで `search` / `attribute-values` / `analysis/ask` が 200、SC-03 の詳細が 403/401 にならないこと、
文書一覧が運用者・管理者で 200 であること。

## 申し送り

- **後段サービスの認可**（決定 4）。**#458** が持つ。本 PR は片側だけである。
- **`/bff/admin/config` の 3 本**は意図的にミドルウェアを使わない形のまま据え置く（決定 2）。
- **検査器は「認証を要求するか」までしか見ない。** 「その端点に**正しい**ロールが付いているか」は
  依然として人が計画を読んで決める——`x-roles` と実効ロールの一致は見るが、
  **どちらも同じように間違っていれば通る**（[IADR-0156](./IADR-0156_bff-authz-contract-checker.md) が既に開示している限界）。
