---
title: 作業仕様書 — 無認証で到達できる BFF 9 端点を塞ぎ、検査器が無認証を見分けられるようにする（#656）
type: spec
status: draft
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
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
---

# 作業仕様書: BFF 端点の認証欠落（#656）

## 起点

- **NFR-09**（認証・認可。Must）／**FR-03**・**FR-04**・**FR-06**・**FR-07**／**SC-01**・**SC-03**・**SC-05**・**SC-08**
- 起点 issue: **#656**（出所は #525 / PR #657 の母集合走査。`granted` の消費側を全数確認していて発見した）

## 母集合（自分でファイルから引いた）

### 軸 1: issue 番号で引く

```console
$ grep -rn '#656' --include=*.cs --include=*.ts --include=*.tsx --include=*.yaml \
    --include=*.yml --include=*.md --include=*.json \
    --exclude-dir={node_modules,.git,ai-stock-trading,planning} .
```

**0 件。** 先行作業からの引き継ぎは無い（自分で起票したばかりなので当然だが、**引かずに「無い」と決めない**）。

### 軸 2: 計画書の現状（issue 本文は転記しない）

| 出所 | 確定内容 |
| --- | --- |
| `02_requirements:104`（**NFR-09**・Must） | 恒久: 全 API で OIDC/JWT 認証。**暫定: エッジ（BFF）で OIDC/JWT を担保** |
| 同 §暫定運用の注記（セキュリティ） | 「サービスメッシュ導入までの暫定期は、**エッジ（BFF）で OIDC/JWT 認証を担保し**、内部サービスはネットワーク分離を第一防御とする」 |
| 同 ［2026-08-04 更新］の追記 | 「**恒久像の残課題は『全 API の OIDC/JWT 認証』である**」 ＝ **エッジ側は達成済みという前提で計画が進んでいる** |
| `05_screens:124` | **SC-05/06/07 = 閲覧は管理者・運用者／破壊的操作は管理者限定** |
| `05_screens:126` | 利用者グループ（**SC-01〜04・SC-08** ほか）は **ABAC の権限内で全利用者が利用できる** |

**計画は「エッジで担保されている」ことを前提に恒久側の議論をしている。** 本件はその前提が崩れていることを示す。

### 軸 3: 端点単位の全数走査（**ファイル単位で数えると落ちる**）

```console
$ node scripts/check-bff-authz-docs.js   # → OK（49 端点が x-roles と一致）
```

検査器は通る。**通るからこそ**、群・端点・ハンドラ内 `AuthorizeAsync`・private ヘルパの **4 経路**を
自分でたどり、端点ごとに「認証を要求するか」を出した。

> **★ issue 起票時は「5 端点」と書いていた。実測は 9 である。**
> 落としたのは `DocumentBffEndpoints.cs` の**読み取り 4 本**。**同ファイルは `RequireAuthorization` を
> 6 個持つ**（すべて書き込み群 `var write = app.MapGroup(...).RequireAuthorization(...)` の側）ため、
> **ファイル単位の件数でスクリーニングした起票時の走査をすり抜けた**。
> 読み取り群は `var g = app.MapGroup("/bff/documents")` で認可を持たない。
> **#657 で踏んだ「対象を絞りすぎて落とす」と同じ型の 2 回目である**——今回は測り方の側
> （ファイル単位 → 端点単位）を直した。**issue 本文も訂正した。**

**認証を要求しない 9 端点**:

| 端点 | 画面 | ファイル |
| --- | --- | --- |
| `POST /bff/search` | SC-01 / SC-02 | `SearchBffEndpoints.cs` |
| `POST /bff/attribute-values` | SC-01 / SC-08 | 同上 |
| `POST /bff/analysis/ask` | SC-01 | `AnalysisBffEndpoints.cs` |
| `POST /bff/analysis/analyze` | SC-08 | 同上 |
| `POST /bff/analysis/ask/stream` | SC-01 | 同上 |
| **`GET /bff/documents`** | **SC-05** | `DocumentBffEndpoints.cs` |
| `GET /bff/documents/{id}` | SC-03 | 同上 |
| `GET /bff/documents/{id}/versions` | SC-03 | 同上 |
| `GET /bff/documents/{id}/content` | SC-03 | 同上 |

**`/bff/admin/config` の 3 本は対象外である。** `RequireAuthorization` を**意図的に付けず**、
ハンドラ内 `AuthorizeAsync(user, ConfigViewer)` ＋ **404 存在秘匿**を採る形である（[[IADR-0009]]。
`RequireAuthorization` だと無認証が 404 到達前に 401 で短絡して存在が漏れる）。
**ミドルウェアを使っていないだけで認可は在る。** 走査の 1 巡目は private ヘルパの解決に空のポリシーを
渡していたため 12 件と出た —— **実データで解決し直して 9 件に落ちた。**

### 軸 4: 救済経路が無いことの確認（「無いこと」も実測する）

| 救済の可能性 | 実測 |
| --- | --- |
| ASP.NET の `FallbackPolicy` | **無い**。`AuthExtensions.cs:80` の `AddAuthorization` は `AdminOnly` / `ConfigViewer` を**登録するだけ** |
| Istio の `RequestAuthentication` / `jwtRules` | **`deploy/` 配下に 0 件** |

### 軸 5: 呼び出し元（ロールを足すか否かはここで決まる）

```console
$ grep -rn 'BffDocumentList' --include=*.ts --include=*.tsx src/ | grep -v /generated/
```

**`GET /bff/documents` の呼び出し元は `sc05-documents/useDocumentAdmin.ts` ただ 1 つ**である。
一方 `{id}` / `/content` / `/versions` は契約の `summary` が **SC-03**（文書詳細）を指し、
**SC-01 の出典クリックから一般利用者が遷移する**経路である（`05_screens` の SC-01 §アクション）。

**画面側のガードも実測した**（`features/<画面>/index.tsx` の `RequireRole anyOf`）:

| 画面 | ガード |
| --- | --- |
| SC-05 / SC-06 / SC-07 / SC-10 | `[Admin, Operator]` |

**SC-05 は画面が admin ＋ operator に絞られているのに、その画面が呼ぶ `GET /bff/documents` は
誰でも通る。** これは #628 / #629 で 2 度直したのと**同じ型**（画面は絞れているが API が絞れていない）である。

## ★ いま情報漏洩は起きていない。ただし防御が 1 枚しかない

無認証呼び出しは `BffScopeResolver` が `userId="anonymous"` / `userAttributes={}` で解決し、
`AbacEvaluator` にマッチするポリシーが 0 件なら `Granted=false` → BFF は空応答（文書詳細は 404）へ縮退する。
`RagOrchestrator` も `!resolved.Granted` の時点で `EmptyAnswer()` を返すので**検索も LLM 呼び出しも走らない**。
dev シード 5 件はすべて `clearance` を要求するため、無認証はどれにもマッチしない。**実害は 0 である。**

**しかしその安全は「利用者条件が空のポリシーが 1 件も無いこと」だけに支えられている。**
`AbacEvaluator.MatchesUserConditions` は**条件が空なら全利用者にマッチする**（同ファイルのコメントが明記）。
SC-09 から管理者が利用者条件を持たないポリシーを 1 件作れば、**コード変更なしで無認証の呼び出し元へ開く**。
[[IADR-0044]] の多層防御に反する状態である。

## 判断

### 判断 1: **端点ごとに認可を分ける**（一律に足さない）

| 端点 | 与える認可 | 根拠 |
| --- | --- | --- |
| `search` / `attribute-values` / `analysis` 3 本 | **`RequireAuthorization()`（認証のみ）** | `05_screens:126`「利用者グループは ABAC の権限内で全利用者が利用できる」。**書かれていない制限を足さない** |
| `GET /bff/documents/{id}` / `/versions` / `/content` | **`RequireAuthorization()`（認証のみ）** | SC-03。SC-01 の出典から一般利用者が遷移する |
| **`GET /bff/documents`** | **`RequireRole(Admin, Operator)`** | `05_screens:124`「SC-05 = 閲覧は管理者・運用者」＋**呼び出し元が SC-05 だけ**（軸 5） |

**`GET /bff/documents` だけ扱いが違うことに注意する。** 一律に「認証のみ」を足すと、
**計画が定めた SC-05 の閲覧ロールを実装が満たさないまま残る**。
逆に一律にロールを足すと SC-03 が壊れる（一般利用者が出典を開けなくなる）。

> **これは #656 の受け入れ基準 1（「ロールは要求しない」）を 1 件だけ上書きする判断である。**
> 起票時は 9 端点すべてを「一般利用者の機能」と見ていたが、**軸 5 で呼び出し元を引いたら SC-05 だった。**
> issue 側にも反映する。

### 判断 2: 404 の存在秘匿を壊さない（[[IADR-0039]] 決定 3）

`GET /bff/documents/{id}` は現在、権限外・不在ともに **404** を返す（[[IADR-0009]]）。
`RequireAuthorization` を付けると**無認証は 401** になる。

**これは存在秘匿を壊さない。** 401 は経路の存在を示すが、**その経路は公開契約（`openapi.yaml`）に
載っている**。秘匿の対象は**文書の存在**であり、それは認証済みの権限外で 404 のままである。
**`/bff/admin/config` とは事情が違う**——あちらは `ConfigViewer` を持たない**認証済み利用者**に対しても
404 を返して**画面の存在ごと**隠す設計であり、401 で短絡すると隠す対象そのものが漏れる。

### 判断 3: 検査器が「無認証」と「認証のみ」を見分ける（[[IADR-0156]] の拡張）

`check-bff-authz-docs.js` の `rolesFromStatement` は**両者をどちらも `null` へ畳む**（`:180`）。
`requiresAuth`（真偽）を実効ロールとは**別の軸**として持ち、契約側と突き合わせる。

**ミドルウェアの有無で判定してはならない**——`/bff/admin/config` の 3 本が誤検出される。
**群・端点・ハンドラ内 `AuthorizeAsync`・private ヘルパの 4 経路**を見て「認可が在るか」で判定する
（`collectImplementation` が既に持っている経路をそのまま使う）。

**契約側の表現は実測してから決める。** 候補は (a) `x-roles: []` の意味を「認証必須・ロール不問」に固定し
無認証は別フィールドで宣言する、(b) 本 PR 後は無認証の端点が 0 になるので**「全端点が認証を要求する」を
不変条件にする**——**(b) が採れるなら新しい契約フィールドは要らない**。49 端点を全数走査して確かめる。

### 判断 4: 後段サービスは**本 PR では触らない**（射程外）

BFF を塞いでもクラスタ内から後段へ直接到達する経路は残る。**多層防御としては片側だけである。**
恒久側（全 API OIDC/JWT）は **#458** が持つ。本 issue は**暫定側（エッジ＝BFF）の未達**に閉じる。
**「片側だけ」であることを PR と IADR に明記する**——塞いだことで全体が守られたと読ませない。

## テスト（受け入れ基準の写像）

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 9 端点が**無認証で 401** | 端点ごとに 1 件 |
| 2 | **認証済みの一般利用者は従来どおり使える**（狭めすぎていない） | `search` / `analysis` / SC-03 の 3 本を非特権ロールで**対**として固定 |
| 3 | `GET /bff/documents` は**非特権ロールで 403** | SC-05 の閲覧ロール |
| 4 | `GET /bff/documents` は**運用者で 200** | 狭めすぎていないことの対 |
| 5 | **404 存在秘匿が保たれる**（認証済み・権限外は 404 のまま） | 回帰 |
| 6 | 検査器が無認証を検出し、`/bff/admin/config` を誤検出しない | 検査器の自己試験＋実データ |

**変異試験**: 各 `RequireAuthorization` を外して対応するテストが落ちることを実測し、復旧後に緑を確認する。
**落ちないなら正直に書く**——#657 では「テストを書いた」が「変更が守られている」ではなかった。

## 射程外

- **後段サービスの認可**（判断 4・#458）。
- **`/bff/admin/config` の 3 本**（意図的な形。軸 3）。
- **`required` 不一致 10 件**（#658）。
