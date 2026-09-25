---
title: IADR-0458 BFF はエッジであり、BFF が利用者の資格情報を後段へ付けて中継する 15 本は east-west に数えない（オーナー裁定の記録。計画 ADR への反映は環流待ち）
type: impl-adr
status: Accepted
related_ids:
  - NFR-09
  - NFR-16
  - ADR-0029
  - ADR-0032
  - ADR-0075
  - ADR-0086
  - ADR-0089
  - IADR-0042
  - IADR-0154
  - IADR-0379
  - IADR-0401
  - IADR-0402
  - IADR-0403
  - IADR-0426
  - IADR-0462
author: claude
created: 2026-09-25
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md §決定（現行の該当経路に「BFF → 各サービス」を挙げる）・例外規定
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md 決定 3・5・6
  - planning:projects/microservices-platform/07_adr/ADR-0086_user-context-in-body-not-token-exchange.md 決定 1・3
  - planning:projects/microservices-platform/07_adr/ADR-0089_east-west-completion-rule-and-authz-service-face.md 決定 1
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-09（暫定条項「エッジ（BFF）で担保」）
---

# IADR-0458: BFF の利用者資格情報の中継は east-west ではない（#1397）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: endazon（オーナー裁定 2026-09-25・#1397）／ Claude Code（記録・分類基準の起案）

## 起点・関連

- issue: #1397（#1255 の 2026-09-10 コメントと 2026-09-11 監査が「扱いが未定」とした項目）
- 作業仕様書: `.ai-context/specs/20260925_1397_bff-user-credential-relay-is-edge.md`（母集合・除外理由の全表）
- 関連 IADR: [IADR-0379](IADR-0379_east-west-grpc-preconditions.md) 決定 4（north-south = 利用者トークン／east-west = s2s）／
  [IADR-0401](IADR-0401_east-west-grpc-authz-user-directory.md)・[IADR-0402](IADR-0402_east-west-grpc-bff-document-read.md)（BFF の 15 本を先に数えた記録）／
  [IADR-0426](IADR-0426_east-west-grpc-rag-search-and-user-context-by-argument.md)（「扱いは未定のまま」と書いた記録）
- 計画: `ADR-0029` §決定／`ADR-0075` 決定 3・5・6／`ADR-0086` 決定 1・3／`ADR-0089` 決定 1／`NFR-09`・`NFR-16`
- 実装ガイド（人が読む正）: `docs/api/east-west-grpc.md`

## コンテキストと課題

#1255 の受け入れ基準は「移行後に east-west の `AddHttpClient` を数えて 0 本」である。①②③ が着地した段で、
**0 にならない主因は扇形の ④⑤ ではなく BFF の 15 本**になった（#1255 の 2026-09-11 監査）。
この 15 本は `ADR-0086` 決定 3 の対象 2 経路に含まれず、`IADR-0426` §残るもの が「扱いは未定のまま」と
書いたまま拾い手が居なかった。

**オーナー裁定（2026-09-25・#1397）**:

> BFF はエッジであり、BFF → 各サービスの利用者資格情報を運ぶ 15 本は east-west（ADR-0029 の gRPC 化の対象）に含めない。
> IADR に記録し、ADR-0029 / ADR-0086 の扱いとして計画へ環流する。

同日の #1255 裁定は ④⑤（MCP ツール申告・introspection の扇形）を「配り先のすべてのサービスに gRPC の口を
実装して移す」とし、「BFF → サービスの 15 本は #1397 の裁定により対象外」と確認した。

### 🔴 計画 ADR の文面と食い違う

- **`ADR-0029` §決定は、east-west の「現行の該当経路」として「BFF → 各サービス」を名指ししている。**
  `ADR-0075` 決定 6 はそれを引いて「基盤先行は MSP 自身の east-west 移行を含む」とした。
- **`ADR-0029` は例外を「対象経路を明記した新 ADR」に限り、`ADR-0075` 決定 5 は「実装側の IADR で REST 継続を
  自認する余地は無い」とする。**

⇒ **本 IADR は実装側の判断で REST 継続を自認するものではない。** 記録するのは、`ADR-0029` / `ADR-0075` /
`ADR-0086` の**決定者（利用者＝オーナー）自身の裁定**である。**計画 ADR の文面への反映の形（新 ADR か、
`ADR-0029` §決定 の該当経路の部分改定か）は計画側が決める。** 反映されるまでの間、計画 ADR の文面と
本 IADR は食い違ったままであり、**それを隠さないために本節を置く。**

［2026-09-26 追記 / #1520］**計画は反映した。** 計画 ADR-0109（利用者裁定 2026-09-26・planning#651）が、例外ではなく分類の是正として
ADR-0029 §決定 の該当経路の列挙から「BFF → 各サービス」を外し、ADR-0075 決定 6 の根拠の記述を追随させた（いずれも部分改定）。
ADR-0086 決定 1 の射程が east-west に限られることも同 決定 2 で確認された。本節の食い違いは解消している。

## 実測（基点 `origin/develop` `d3e8b2a4`・shallow でない）

### 15 本の中身（登録単位 = BFF の名前付きクライアント）

| # | 名前付きクライアント | 宛先 | 資格情報を付ける呼び出し箇所 |
| --- | --- | --- | --- |
| 1 | `AiAnalysisService` | AI 分析 | 3 |
| 2 | `FeedbackService` | フィードバック | 3 |
| 3 | `DashboardService` | ダッシュボード | 2（うち 1 は利用状況イベントの送出。列に載せた `signal.Authorization` から付ける） |
| 4 | `AuthorizationService` | 認可（**管理面の代理**。属性辞書・利用者管理・利用者検索・グループ検索） | 2 |
| 5 | `RetrievalService` | 検索 | 2 |
| 6 | `GraphService` | グラフ | 3 |
| 7 | `McpServer` | MCP | 1 |
| 8 | `NotificationService` | 通知 | 1 |
| 9 | `WikiService` | Wiki | 1 |
| 10 | `DocumentService` | 文書（書き込み・個人資料・タグ辞書・検索の文書引き当て） | 4 |
| 11 | `ConversionService` | 変換 | 1 |
| 12 | `DataSourceService` | データソース | 1 |
| 13 | `ConfigurationService` | AST 設定 | 1（AST submodule `471cbf31`） |
| 14 | `RiskManagementService` | AST リスク統制 | 1（同上） |
| 15 | `MarketMonitorService` | AST 監視銘柄 | 1（同上） |

**登録単位 15 本・資格情報を付ける呼び出し箇所 27（MSP のコード 24・AST submodule 3）。** 数え方は 3 軸
（`AddHttpClient` の登録・BFF が読む `Services:<Name>` の構成キー・配備側の `Services__*`）で引き、
3 軸とも上表の外に**サービス宛の** REST の宛先を持たない。🔴 **サービス以外の宛先は 1 つある** —— `OpendAuth__BaseUrl`
（`deploy/local/values-local.yaml` の BFF の `extraEnv`。構成キーは `OpendAuth:BaseUrl` で `Services:` の外）が指す OpenD 認証の
サイドカーである。決定 2 の表の最終行のとおり 15 本に入れない。呼び出し箇所の内訳と除外の全表は作業仕様書にある。

### 🔴 #1255 本文の内訳は 1 本違っていた（数は同じ 15）

| #1255 本文 | 実測 |
| --- | --- |
| 名前付き **14**（`AuthorizationService` を含まない） | 名前付き **15**。管理面の代理 `AuthorizationService`（利用者の資格情報を転送し、後段が `AdminOnly` を判定）を落としていた。`IADR-0401` §残るもの・`IADR-0402` §母集合の「15 本（`UserAdminBffEndpoints` を含む）」とは一致する |
| ＋ `HttpEffectiveConfigCollector` **1** | **利用者の資格情報を運ばない**（`Authorization` を 1 度も付けない）。これは #1255 の ⑤（introspection の扇形）そのものであり、**同日の #1255 裁定で gRPC へ移す側に入った** |

**15 という数が偶然一致したため、内訳の食い違いは数だけ見ていると見えない。** 本 IADR の射程は上表の 15 本であり、
`HttpEffectiveConfigCollector` は含まない。

## 決定

### 決定 1: BFF が利用者の資格情報を後段へ付けて中継する呼び出しは、エッジ（north-south の続き）として扱い、east-west に数えない

- **15 本のうち 14 本の後段**は、受け取った利用者の資格情報で**自分の門**（`AdminOnly`・ABAC・主体の絞り込み）を判定する。
  BFF は利用者の要求をそのまま運ぶ入口であり、`IADR-0379` 決定 4 の「north-south = 利用者トークン」と同じ線上にある。
- 🔴 **例外が 1 本ある: `ConversionService`。** 認証を一切持たない（`AddPlatformAuth` も `RequireAuthorization` も 0 件。
  `IADR-0042` 決定 3・`IADR-0154` 決定 6・`IADR-0403` 決定 4 が据え置いたワーカーの最小 HTTP サーフェス）。BFF は資格情報を
  付けて送るが、**後段はそれを読まない。門は BFF の 1 枚だけ**（`/bff/conversion/jobs` の admin / operator と、retry・figure 系の
  `AdminOnly`）であり、代償統制はメッシュの STRICT mTLS とネットワーク分離である。**本裁定はこの経路もエッジとして east-west から
  外すので、`IADR-0403` が当てにしていた解消の道（下記「結果」）が消える。**
  ［2026-09-26 追記 / #1520］**この例外は解消した。** 計画 ADR-0109 決定 3 が「エッジの後段は中継された利用者の資格情報を自ら検証する。
  ConversionService も検証する」と定め、[IADR-0462](./IADR-0462_conversion-service-validates-relayed-user-credential.md) が `AddPlatformAuth` と 5 口の端点の門（BFF と同じロール）を掛けた。
  **本決定の「14 本」は 15 本になった**（後段がみな自分の門を利用者の資格情報で判定する）。
- **`ADR-0029` の gRPC 化の対象から外す。** したがって #1255 の受け入れ基準「east-west の `AddHttpClient` が 0 本」の
  母集合にも入らない。
- **`ADR-0086` 決定 1（利用者トークンを面に通さない）の射程にも入らない** —— 同決定の対象は east-west だからである。
  本 IADR はこの読みを記録し、計画側へ確認を求める（決定 4）。

### 決定 2: 分類は「呼び出し箇所が誰の資格情報を付けるか」で引く。BFF から出る呼び出しをすべてエッジとは読まない

| BFF から出る呼び出し | 分類 | 理由 |
| --- | --- | --- |
| 上表 15 本の、利用者の資格情報を付ける呼び出し箇所 | **エッジ**（本裁定） | 利用者の要求の中継 |
| BFF 自身の s2s で呼ぶもの（`AuthzScope` / `DocumentRead` / `AttributeValues` の gRPC 3 面と、`/authz/scope` の REST 並走側） | **east-west のまま** | BFF がサービスとして自分の資格で呼ぶ。移行済み・並走中 |
| 資格情報を付けない REST 呼び出し（`DocumentService` の読み取り 4 箇所） | **east-west のまま** | gRPC `DocumentRead` の REST 並走側。退役は `ADR-0089` 決定 1 の規則で数える |
| `HttpEffectiveConfigCollector`（introspection の収集） | **east-west のまま** | 扇形 ⑤。#1255 が移す |
| `OpendAuthGateway`（`OpendAuth:BaseUrl` が指す OpenD 認証のサイドカー） | **対象外**（エッジでも east-west でもない） | 宛先がメッシュ内の**サービスではない**（OpenD の Pod に同居するサイドカーで、認証を持たない）。BFF は**利用者の資格情報を付けない**（BFF が唯一の認可点。`trading-owner` 以外は 404） |

- 🔴 **名前付きクライアント `DocumentService` は両方の側に呼び出し箇所を持つ**（書き込みは資格情報を付け、読み取り 4 箇所は付けない）。
  **登録を 15 本に数えることは、読み取り 4 箇所を east-west から外すことを意味しない。**
- 🔴 **属性値照会の REST 側（`RetrievalService`）は資格情報を付けるが、gRPC `AttributeValues` の REST 並走側でもある。**
  この呼び出し箇所が消えるのは当該経路の REST 退役による（本裁定によってではない）。
- **裁定の文言（「利用者資格情報を運ぶ 15 本」）を呼び出し箇所へ降ろしただけであり、広げていない。**

### 決定 3: 本 IADR はコードを変えない

- 15 本はすでに REST（`IHttpClientFactory` の名前付きクライアント）で動いている。**本 IADR は数え方を記録し、
  数を書く文書（`docs/api/east-west-grpc.md` / `docs/tech/tech-requirements.md`）を追随させるだけである。**
- `Refit.HttpClientFactory` の PackageReference（BFF。インタフェース 0 件）の扱いは本裁定で変わらない ——
  15 本は Refit を使っておらず、撤去の判断は #1255 の REST 退役の段に残る。

### 決定 4: 計画側へ環流する（起票は利用者が行う）

- 反映を求める先: `ADR-0029` §決定（現行の該当経路から「BFF → 各サービス」の利用者資格情報の中継を外す）／
  `ADR-0075` 決定 6（「基盤先行は MSP 自身の移行を含む」の根拠に BFF → 各サービスを引いている）／
  `ADR-0086`（決定 1 の射程が east-west に限られ、エッジの中継に及ばないことの確認）。
- 🔴 **`ADR-0029` の例外規定（対象経路を明記した新 ADR）との関係は計画側の判断に委ねる** ——
  本裁定は「例外」ではなく「分類の是正（BFF はエッジ）」だと読めるが、その読みを実装側で確定させない
  （`ADR-0075` 決定 5）。

## 理由

- **決定 1**: BFF は SPA の唯一の入口であり（`ADR-0032` の Token Handler）、`NFR-09` の暫定条項も BFF を「エッジ」と呼んでいる。
  **`ADR-0029` 自身も一部はこの読みを支える** —— 表題が「内部サービス間 gRPC・**BFF/外部 REST**」であり、採用した選択肢 1 は
  「内部同期サービス間 = gRPC、**BFF/外部向け = REST** に固定」である（ただし §決定の該当経路の列挙は「BFF → 各サービス」を east-west に
  入れており、同じ ADR の中で揺れている。決定 4 の環流で計画側に解いてもらう）。
  利用者の資格情報を付けて中継する呼び出しを east-west に数えると、`ADR-0086` 決定 1 に従って「本文で運ぶ」形へ
  作り直すことになる —— **後段の門を利用者の資格情報で判定する二重ゲート（`AdminOnly` など）を s2s の 1 枚へ
  落とす**変更であり（BFF の `Program.cs` が文書の書き込み経路について「s2s へ替えると門が 1 枚になる」と注記し、
  `IADR-0402` 決定 1 が「付ける 23 箇所は移さない —— 後段が利用者の権限で判定している」とした形）、得るものより失うものが大きい。
  🔴 **この理由は 15 本のうち 14 本にしか当たらない。** `ConversionService` の後段は門を持たないので、s2s へ替えても落ちる門が無い ——
  むしろ s2s ＋ `ServiceCaller` へ移せば後段に門が 1 枚**増える**経路だった（`IADR-0403` 決定 4 の残差）。本裁定はこれも含めて外すので、
  その残差は別の手段で閉じる必要がある（「残るもの」4）。
- **決定 2**: 名前付きクライアント単位で機械的に外すと、`DocumentService` の読み取り 4 箇所と `/authz/scope` の
  REST 並走側（east-west の退役対象）まで一緒に外れ、#1255 の REST 退役の母集合から**黙って落ちる**。
  呼び出し箇所で引くのは `IADR-0402` 決定 1 が採った軸と同じである（判定は送る側の行で引く）。
- **決定 4**: 計画 ADR の文面が「BFF → 各サービス」を east-west と名指しし続ける限り、次に計画を読む者は
  15 本を移行対象と読む。**食い違いを実装側の記録だけに置くと、計画を一次情報とする読み手には見えない。**

## 結果

- 良い影響: #1255 の受け入れ基準の母集合が確定する —— 残るのは扇形 ④⑤ の gRPC 化と、移行済み経路の REST 退役
  （`IADR-0379` 決定 5 の反転）である。
- 良い影響: BFF の後段の二重ゲート（14 本）を s2s の 1 枚へ落とす作業が発生しない。
- 悪い影響 / トレードオフ: **計画 ADR の文面と本 IADR が、計画側の反映まで食い違う。**
- 悪い影響 / トレードオフ: 15 本とも BFF は利用者トークンを後段へ付けて送るので、BFF と後段の間では利用者トークンが
  メッシュ内を流れ続ける（mTLS の内側。`NFR-16`）。**これは現状の継続であり、本裁定で新たに生じるものではない。**
  後段がそのトークンを検証するのは **14 本**であり、`ConversionService` は検証しない（受け取って捨てる）。
- 悪い影響 / トレードオフ: 🔴 **`IADR-0403` が `NFR-09`（全 API で OIDC/JWT）の残差として挙げた「`ConversionService` 5 口」は、
  「east-west gRPC 移行が `ConversionService` に及んだ時点で N から S へ動く」と見込まれていた（同 決定 4・結果・フォローアップ 3）。
  本裁定で BFF → `ConversionService` は east-west ではなくなり、その移行は起きない。** 残差は、それを閉じる予定の道を失ったまま残る
  （`IADR-0403` フォローアップ 3 へ日付つきの指し先を置いた）。

### 残るもの

1. **計画**: 決定 4 の環流（起票は利用者）。
2. **実装**: #1255 の残作業（④⑤・REST 退役）。本 IADR はその射程から 15 本を外しただけで、残作業を進めない。
3. 🔴 **本 IADR の分類基準（決定 2）は機械で検査していない。** 新しい BFF → サービス経路を足す人が
   「資格情報を付けるか」で east-west か否かを判断する。同型の取り違えが 2 回起きたら検査器を考える。
4. 🔴 **実装 → 計画**: `ConversionService` 5 口の `NFR-09` 残差を、east-west gRPC 以外のどの手段で閉じるか（後段に
   `AddPlatformAuth` ＋ BFF が既に付けている利用者トークンでの門を積むか、`NFR-09` の判定を BFF の門で満たすと読むか）。
   `IADR-0403` は `AddPlatformAuth` を足すだけでは口が塞がらない（`FallbackPolicy` が無い）とし、判定単位の裁定を計画へ求めていた。
   **決定 4 の環流に含めて計画へ出す。**
   ［2026-09-26 追記 / #1520］**裁定され、着地した。** 計画 ADR-0109 決定 3（planning#651）は前者（後段に `AddPlatformAuth` ＋ BFF が既に
   付けている利用者トークンでの門を積む）を採った。[IADR-0462](./IADR-0462_conversion-service-validates-relayed-user-credential.md) が実装した —— `FallbackPolicy` は置かず、5 口すべてに端点の門を掛けている
   （`IADR-0403` 決定 5 の指摘どおり、`AddPlatformAuth` だけでは口は塞がらないため）。

## 関連

- Supersedes: なし
- Superseded by: なし
