---
title: IADR-0465 ConversionService は BFF が中継した利用者の資格情報を自ら検証し、変換ジョブの 5 口すべてに BFF と同じロールの端点の門を張る（サービス間トークンは通さない）
type: impl-adr
status: Accepted
related_ids: [NFR-09, NFR-16, FR-12, UC-06, SC-07, ADR-0004, ADR-0029, ADR-0084, ADR-0109, IADR-0029, IADR-0042, IADR-0044, IADR-0128, IADR-0154, IADR-0379, IADR-0403, IADR-0424, IADR-0458]
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0109_bff-user-credential-relay-is-edge.md 決定 1・3・4
  - planning:projects/microservices-platform/07_adr/ADR-0084_nfr09-judgment-unit-and-interim-clause-release.md 決定 1（端点単位）・決定 4（暫定条項での追認）
  - planning:projects/microservices-platform/02_requirements/01_requirements.md NFR-09
  - planning:projects/microservices-platform/05_screens/01_screens.md §SC-07（閲覧は管理者・運用者／再変換と人手補正は管理者限定）
related_specs:
  - ../specs/20260926_1520_conversion-service-auth.md
---

# IADR-0465: ConversionService は中継された利用者の資格情報を自ら検証する（#1520）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-26
- 決定者: claude（#1520。計画 ADR-0109 決定 3 の実装。利用者裁定 2026-09-26・planning#651）

## 起点・関連

- 関連する計画書 ID: NFR-09（恒久条項「全 API で OIDC/JWT 認証」）／FR-12・UC-06・SC-07（変換ジョブの照会・再変換・人手補正）
- 関連する計画 ADR: **ADR-0109** 決定 1（BFF の中継はエッジ）・**決定 3（エッジの後段は中継された利用者の資格情報を自ら検証する。ConversionService も検証する）**／**ADR-0084** 決定 1（NFR-09 は端点単位で判定。`FallbackPolicy` は門ではない）・決定 4（暫定条項での追認。本 IADR の着地まで有効）／ADR-0029（east-west は gRPC）／ADR-0004（Keycloak OIDC/JWT）
- 関連する実装 ADR: **IADR-0403** 決定 3〜5・フォローアップ 3（ConversionService を「N・要さない」とし `AddPlatformAuth` を足さないとした記録）／**IADR-0458** 決定 1・残るもの 4（BFF の中継をエッジとし、ConversionService の残差を計画へ出した記録）／IADR-0042 決定 3・IADR-0128 決定 3・IADR-0154 決定 6（ワーカーに認可を課さないとした記録）／IADR-0379 決定 4（`ServiceCaller` は利用者のトークンを通さない）／IADR-0424（本物の JwtBearer を通す試験の作法）
- 関連する実装仕様書: `.ai-context/specs/20260926_1520_conversion-service-auth.md`（母集合・除外理由の全表）

## コンテキストと課題

ConversionService は認証を持たなかった（`AddPlatformAuth`・`RequireAuthorization`・`AddAuthentication` がいずれも 0 件）。BFF の
`/bff/conversion/jobs` は利用者の資格情報を付けて後段の `/jobs` へ中継していたが、後段はそれを読まず、**門は BFF の 1 枚だけ**だった。
`IADR-0403` はこの 5 口を NFR-09 の残差とし、「east-west gRPC 移行が及べば `ServiceCaller` の門（S）へ動く」と見込んでいた。

`IADR-0458`（オーナー裁定 2026-09-25）と計画 **ADR-0109 決定 1** が BFF の中継をエッジと分類したため、その移行は起きない。
**ADR-0109 決定 3** は代わりの経路を「ConversionService は中継された利用者の資格情報を自ら検証する（他の 14 サービスと同じ形）」と定めた。

判断が要ったのは 4 点である。**(A) 門をどの単位に掛け、どのロールを要求するか**、**(B) サービス間トークンを通すか**、
**(C) 門を持たない口をどれにするか**、**(D) `IADR-0403` 決定 5（`AddPlatformAuth` を足さない）との関係**。

## 検討した選択肢

### (A) 門の単位とロール

1. **BFF の門と同じロールを、同じ単位（群 ＋ 操作）で後段にも掛ける**（採用） — 群に「admin または operator」、再変換・図の一覧・人手補正の 3 口に `AdminOnly` を重ねる（AND 合成で admin のみ）。
2. 群に `AdminOnly` を 1 つ掛ける — 運用者の照会（SC-07 の閲覧は管理者・運用者）が BFF では通り後段で 403 になり、画面が壊れる。
3. 認証済みであることだけを要求する — 端点は「閉じる」が、BFF を迂回した直呼びでは一般利用者が再変換・人手補正を実行できる。**BFF の門と後段の門が食い違い、緩い側が効く。**
4. `FallbackPolicy` で全体を閉じる — ADR-0084 決定 1 の補完が「`FallbackPolicy` は門ではない（端点または端点群に付くものだけを門とする）」と定めている。

### (B) サービス間トークン

1. **通さない**（採用） — `/jobs` を呼ぶのは BFF の中継だけである（呼び出し元の走査は作業仕様書）。
2. `ServiceCaller` を OR で通す — 呼び出し元が無いのに口を開くことになる。利用者のロールと別軸の主体を混ぜると「利用者が操作した」と区別できない（IADR-0379 決定 4 の confused deputy）。

## 決定

### 決定 1: `AddPlatformAuth` ＋ `UsePlatformMiddleware` を張り、`/jobs` の 5 口すべてに端点の門を掛ける。ロールは BFF の門と同じ

| 口 | 実効ロール | 門の所在 |
| --- | --- | --- |
| `GET /jobs` | admin ＋ operator | 群（`ConversionJobEndpoints`。`RequireRole(platform-admin, platform-operator)`） |
| `GET /jobs/{id}` | admin ＋ operator | 群 |
| `POST /jobs/{id}/retry` | **admin のみ** | 群 ∧ `AdminOnly`（`Retry/Endpoint.cs`） |
| `GET /jobs/{id}/figures` | **admin のみ** | 群 ∧ `AdminOnly`（`ListFigures/Endpoint.cs`） |
| `POST /jobs/{id}/figures/{figureId}/correction` | **admin のみ** | 群 ∧ `AdminOnly`（`CorrectFigure/Endpoint.cs`） |

- **検証は他の後段サービスと同じ `AddPlatformAuth`** である（Keycloak の JWT を `Auth:Authority` の metadata で検証。署名・発行元・有効期限。
  `realm_access.roles` は `KeycloakRolesClaimsTransformation` が展開する）。新しい仕組みは足さない（ADR-0109 §理由「新しい仕組みは要らない」）。
- 🔴 **audience は検証しない。** `AddPlatformAuth` が全サービス共通で `ValidateAudience = false` であり、ConversionService だけを変えると
  BFF が中継するトークン（audience は BFF のクライアント）で門が閉じる。**「ロール違いで 403」は試験で固定し、audience は共通設定の射程とする。**
- 配備の構成は増えない。`Auth__Authority`（と任意の `Auth__MetadataAddress` / `Auth__ValidIssuers`）は Helm の `deployment.yaml` が
  全サービスへ `global.auth` から描き、compose は `x-common-env` が ConversionService にも注入している（オフライン描画で確認。作業仕様書）。
- **ABAC（内容の絞り込み）は掛けない。** 変換ジョブは運用資産であり、SC-07 は照会を管理者・運用者のロールで絞る画面である。
  文書単位の可視範囲は取り込み後の文書側で判定される。

### 決定 2: サービス間トークン（`platform-service`）は通さない

- `/jobs` をサービスとして呼ぶ呼び出し元は無い。`platform-service` だけを持つトークンは群のロールを満たさないので 403 になる。
- **サービス間で変換ジョブを扱う必要が出たら、それは east-west であり gRPC ＋ `ServiceCaller` の面として作る**（ADR-0029・IADR-0379 決定 4）。
  REST の `/jobs` に s2s を相乗りさせない。

### 決定 3: ヘルスチェック（`/health/live`・`/health/ready`）と自己申告（`/internal/introspection`）は門を持たない

- 他サービスと同じである（DataSourceService などは同じ 3 口に門を掛けていない）。プローブと構成情報の収集（`HttpEffectiveConfigCollector`）は
  利用者の資格情報を持たない。自己申告の保護は従来どおりネットワーク分離と mTLS である（IADR-0029）。

### 決定 4: 既存の記録との関係（部分的に置き換える）

- **`IADR-0403` 決定 3 の表 14 行目（`ConversionService` = N・要さない）・決定 4（acceptable）・決定 5（`AddPlatformAuth` を足さない）は、
  ConversionService について本 IADR が置き換える。** 決定 5 の理由（「`FallbackPolicy` が無いので 1 つの口も塞がらない」）は今も正しく、
  だから本 IADR は `AddPlatformAuth` と**同時に 5 口の端点の門**を掛ける。形は N から **R（ロール門）** へ動く（S ではない）。
- **`IADR-0042` 決定 3・`IADR-0128` 決定 3・`IADR-0154` 決定 6 の「ワーカー自身に認可を課さない」は、ConversionService の `/jobs` について置き換える。**
  代償統制（ネットワーク分離・mTLS・BFF の門）は外さない —— 多層防御の 1 枚が増えるだけである。
- `IADR-0458` 決定 1 の「例外が 1 本ある: `ConversionService`」と残るもの 4 は本 IADR で解消する。
- 各記録へは日付つきの追記で指し先を置いた（本文は書き換えていない）。

## 理由

- **決定 1**: ADR-0109 決定 3 は「他の 14 サービスと同じ形」を求める。DataSourceService が同じ形（群に admin/operator・破壊的操作に `AdminOnly`・
  利用者トークンは BFF が伝播）で BFF の門を後段に二重化しており、それに合わせた。**ロールを BFF と同じにする**のは、片側だけ緩いと
  BFF を迂回した直呼びで緩い側が効き、片側だけ厳しいと画面が壊れるからである。ADR-0084 決定 1 が端点単位で数えるので、
  5 口を 1 つずつ試験で固定した（群の門を 1 口で確かめても、別の群へ移された口は見えない）。
- **決定 2**: 呼び出し元の無い主体に口を開かない。開けば、利用者の操作と区別できない呼び出しが BFF の門と無関係に通る。
- **決定 4**: `IADR-0403` の判断は「利用者文脈を持たないワーカーにロール門を積むには east-west に利用者トークンを流すことになり、IADR-0379 決定 4 に反する」
  （決定 4 の 3）を理由の 1 つにしていた。ADR-0109 決定 1・2 により BFF の中継は east-west ではなく、利用者トークンを運ぶのは正しい形になったので、この理由は消えた。

## 結果

- 良い影響: ConversionService の 5 口が NFR-09 の恒久条項（端点単位）を満たす。ADR-0084 決定 4（暫定条項での追認）は役目を終える
  （計画側の記録は ADR-0109 フォローアップ 3。本リポジトリは記録しない）。
- 良い影響: BFF が 6 口すべてで資格情報を中継していることが初めて試験に現れた（`EveryRoute_RelaysTheUsersCredential_ToConversionService`）。
  従前は後段が資格情報を読まなかったため、中継を落としても緑のままだった。
- 悪い影響 / トレードオフ: BFF の中継が 1 口でも切れると、その口は後段で 401 になり画面が失敗する（従前は黙って通っていた）。これは意図した fail-closed である。
- 悪い影響 / トレードオフ: ConversionService が Keycloak の metadata（JWKS）に依存する。取得は最初の Bearer 付き要求で遅延して起きるため、
  起動・ヘルスチェック・購読（Wolverine）は影響を受けない。

### 残るもの

1. **実配備での疎通は本 PR では確かめていない**（クラスタへは触れない）。BFF → ConversionService の実トークンでの 200 は、次の配備で SC-07 を開いて確かめる。
2. 後段側の端点ごとの判定を機械で測る手段は無い（ADR-0084 §残るもの）。本 IADR は ConversionService の 5 口を試験で固定するだけである。

## 関連

- Supersedes: なし（`IADR-0403` 決定 3〜5・`IADR-0042` 決定 3・`IADR-0128` 決定 3・`IADR-0154` 決定 6 を ConversionService の `/jobs` について部分的に置き換える。各記録の状態は変えない）
- Superseded by: なし
