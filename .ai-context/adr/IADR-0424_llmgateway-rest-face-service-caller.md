---
title: IADR-0424 LlmGateway の REST 3 口は gRPC 面と同じ 1 つの ServiceCaller で判定し、呼び出し側は共通の 1 か所で s2s を載せる
type: impl-adr
status: Accepted
related_ids: [NFR-09, FR-02, FR-03, FR-04, FR-11, ADR-0004, ADR-0010, ADR-0016, ADR-0029, ADR-0075, ADR-0084, IADR-0379, IADR-0397, IADR-0400, IADR-0413, IADR-0418]
author: endazon (with Claude Code)
created: 2026-09-09
updated: 2026-09-10
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0084_nfr09-judgment-unit-and-interim-clause-release.md
  - planning:projects/microservices-platform/07_adr/ADR-0004_authz-abac.md
---

# IADR-0424: LlmGateway の REST 3 口は gRPC 面と同じ 1 つの `ServiceCaller` で判定し、呼び出し側は共通の 1 か所で s2s を載せる

- 状態: Accepted
- 日付: 2026-09-09
- 決定者: 実装（issue #1364 の受け入れ基準に従う）

## 起点・関連

- 関連する計画書 ID: `NFR-09`（全 API で OIDC/JWT 認証）／`ADR-0084` 決定 1・決定 4／`ADR-0004`／
  `FR-02`・`FR-03`（埋め込み）／`FR-04`・`FR-11`（生成と越境ルーティング）
- 関連する実装仕様書: `.ai-context/specs/20260909_issue-1364_llmgateway-rest-service-caller.md`
- 先例: `IADR-0413`（認可サービスの REST 面へ同じ門を掛け、呼び出し側の登録を 1 か所へ畳んだ）／
  `IADR-0418`（REST 面へ門を掛けたときの試験の作法）／`IADR-0379` 決定 4（`ServiceCaller` の定義）

## コンテキストと課題

LlmGateway の REST 3 口（`POST /complete`・`POST /complete/stream`・`POST /embed`）は
**認可を 1 つも持たなかった**。`AddPlatformAuth` は認証の器を登録するだけであり、`FallbackPolicy` も
無いため（`ADR-0084` 実測 1）、**メッシュ内から到達できる主体は誰でも叩けた**。

3 つの事情が重なる。

1. **前段に BFF の門が無い。** LlmGateway は BFF の名前つき `AddHttpClient` に居ない
   （両ユニットの BFF での `LlmGateway` の言及 4 件は**すべてコメント**である）。
   したがって `NFR-09` の暫定条項「エッジで担保」は**この 3 口には掛かっていない**。
2. **mTLS は身元を保証するが、身元を見るのは端点である。** 稼働構成は `PERMISSIVE` であり、
   `STRICT` にしてもサイドカーを持つ Pod からは素通しである。
3. **LLM 呼び出しは課金を伴う。** 無認可の口は費用の観点でも開けておけない。

`ADR-0084` 決定 4 は「`IngestionService` / `LlmGateway` についても、同じ 3 点
（BFF での担保・ネットワーク分離・機械検査の範囲）を実装側が示すこと。**示せない場合は決定 1 の判定で未達**」
と定めている。**1 が示せない以上、暫定条項では担保できない。**

## 検討した選択肢

| # | 案 | 評価 |
| --- | --- | --- |
| 1 | **端点ごとに `RequireAuthorization(ServiceCaller)`（採用）** | gRPC 面と**同じ 1 つのポリシー**。呼び出し元 5 つは realm ロール・資格情報を既に持つ |
| 2 | `FallbackPolicy` でサービス既定を締める | 🔴 `ADR-0084` 決定 1 補完節が「**`FallbackPolicy` は門ではない**」と明記。加えて `/health/*` まで閉じて Pod が起動しなくなる |
| 3 | 群（`MapGroup("")`）へまとめて掛ける | 判定の単位が端点から群へ移る。**新しい端点が黙って門を継承する**形になり、`ADR-0084` 決定 1 の「端点単位」から離れる |
| 4 | 認証だけ要求する（`RequireAuthorization()`。`IADR-0418` の形） | 🔴 **採らない。** `IADR-0418` がポリシーを付けなかったのは**呼び出し元 3 つが利用者トークンを運んでいた**からである。本件の呼び出し元は 5 つとも**サービスとして呼ぶ**ので、利用者トークンを通す理由が無い |
| 5 | 新しい認可軸（例: `LlmCaller`）を作る | 🔴 **採らない。** 軸が増えると realm のロール設計と 2 か所で同期が要る。issue の受け入れ基準も「既存の `ServiceCaller` と揃える」である |

## 決定

### 決定 1: 3 口はいずれも `RequireAuthorization(PlatformAuthPolicies.ServiceCaller)` を端点に持つ

- **群ではなく端点に付ける**（`ADR-0084` 決定 1）。群はタグ付けのためにあり、門の単位ではない。
- **gRPC 面（`[Authorize(Policy = ServiceCaller)]`）と同じ 1 つのポリシー**である。
  従前 2 つの面のコメントは「REST は無認可なので gRPC のほうが強い」と書いていた ——
  **その非対称は解消した**（コメント 3 か所と `docs/api/east-west-grpc.md` 2 か所を是正済み）。
- **`/health/*`・introspection・OpenAPI は触らない。** readiness を閉じると Pod が起動しない
  （T-A-10 が射程を陰性側から固定する）。

### 決定 2: 呼び出し側は `LlmGatewayHttpClient.AddLlmGatewayServiceToken` の 1 か所で s2s を載せる

- 実体は**既存の `ServiceTokenHandler`**（`IADR-0413` が導入したもの）である。**新しい部品を作らない。**
- **アドレスはこの拡張が決めない。** 既定アドレスは呼び出し元ごとに揃っておらず
  （`GraphService` だけ `5010`・他は `5007`）、**本件でその不揃いを直さない** ——
  「認可を掛ける」変更に「宛先を変える」変更を混ぜると、壊れたときにどちらが原因か分からなくなる。
  ここが引き受けるのは**資格情報の付け方だけ**である。
- **配備の変更は要らない。** 5 つの呼び出し元は `ServiceToken__ClientId` / `__ClientSecret` を
  compose・helm の双方に既に持ち、realm の service account も `platform-service` を持つ
  （gRPC 経路のために配線済みだったものを REST 経路が共有する）。

### 決定 3: 試験は「既定を有資格にし、陰性対照を別の口で作る」

- 器（`TestWebApplicationFactory`）の**既定クライアントを `ServiceCaller` にする** ——
  既存の端点試験 30 箇所超は「サービスが呼ぶ」ことを書いており、資格情報の有無は主題ではない
  （`IADR-0418` が `RetrievalService` で採った作法と同型）。
- **陰性対照は 2 種類**を 3 口それぞれに置く: ①資格情報なし → **401**、
  ②利用者トークン（**管理者でも**）→ **403**。②が無いと confused deputy へ緩めたときに気付けない。
- 🔴 **偽の認証スキームを置かない。** `TestServiceTokens` が JwtBearer の検証鍵と issuer を
  静的構成へ差し替え、**本物の JwtBearer ＋ `KeycloakRolesClaimsTransformation`** を通す
  （`realm_access.roles` が `ClaimTypes.Role` へ展開されるところまで含めて `ServiceCaller` である）。
  発行器は gRPC の器（`GrpcKestrelFactory`）と**共有する** —— 器ごとに別の鍵を持つと片方が古くなる。

### 決定 4: AST（別リポジトリ）の 2 呼び出し元は壊れる。**門は緩めない**

`src/ai-stock-trading` の `ReportService` / `TradeDecisionService` は `/complete` を叩くが、
**s2s トークンを付けていない**（`HttpReportNarrativeDrafter` に「`/complete` は匿名エンドポイントゆえ
s2s トークンは付けない」と明記されている）。realm にも該当 client が無い。

- **発火するのは `LlmGateway__BaseUrl` を設定した配備だけ**である（AST の `values-local.yaml`。
  既定 `values.yaml` は空文字＝呼ばない）。
- **壊れ方は安全側だが無音ではない。** 報告書はプレースホルダ散文へ、取引判断は Hold へ倒れるが、
  後者は `LlmFailureClassification.Classify(401)` が **`ModelUnavailable`** を返すため
  「割当モデルが使えない」という**誤った運用シグナル**が積み上がる。
- 🔴 **それでも緩めない。** 「外部の呼び出し元が資格情報を持たない」ことは、
  課金を伴う端点を無認可で開けておく理由にならない。**追随は AST 側の別 issue とする**
  （AST は `TestSupport.PlatformShim` に `ServiceTokenHandler` 相当を既に持つため、
  必要なのは realm への confidential client 追加と登録の 1 行である）。

## 理由

- **端点単位にするのは計画の決定だからである**（`ADR-0084` 決定 1）。サービス既定で締める案は、
  同じ決定の補完節が「門ではない」と明示的に退けている。
- **同じポリシーを使うのは、面が 2 つあっても主体は 1 つだからである。** 輸送の並走
  （`IADR-0379` 決定 5）は**認可の並走ではない** —— 従前のコメントはこの 2 つを混同していた。
- **登録を 1 か所へ畳むのは、5 か所に散ると 1 つだけが古くなるからである**（`IADR-0413` と同じ理由）。
- **陰性対照を要求するのは、陽性だけの試験が門を守らないからである。** 実際 #1364 の時点で
  この 3 口には陽性試験が 30 件超あり、**1 つも欠陥を検出しなかった。**

## 結果

- 良い影響:
  - `LlmGateway` は `ADR-0084` 決定 1 の判定で**達成**になる（決定 4 の「示せない場合は未達」から抜ける）。
  - REST と gRPC の門が揃い、「輸送を替えれば迂回できる」経路が消えた。
  - 変異試験で確認済み: 3 口の `RequireAuthorization` を外すと**陰性対照 6 件が落ちる**。
- 悪い影響・トレードオフ:
  - 🔴 **AST の 2 呼び出し元が壊れる**（決定 4）。緩めずに別 issue で追随する。
  - 呼び出し側 5 サービスがトークン発行（Keycloak への client credentials）に**依存するようになった**。
    取得失敗は `HttpRequestException` へ畳まれ、既存の縮退の枝へ合流する（新しい枝は作っていない）。
    🔴 **この依存は単体テストでは踏めない**（`ServiceTokenOptions` の 🔴 と同型）——
    構成の欠落は統合／配備でしか露見しない。5 つとも配線済みであることは実測で確かめた。
- フォローアップ:
  1. **AST 側の追随**（決定 4）。realm の confidential client ＋ 呼び出し側の登録。
  2. `/complete/stream` は `docs/api/openapi.yaml` に**そもそも記載が無い**（本件の前から）。
     記載の追加は別件とし、ここでは記録に留める（1 回目）。
  3. 既定アドレスの不揃い（`5007` / `5010`）は本件で直していない（決定 2）。記録に留める（1 回目）。

## 関連

- Supersedes: なし
- Superseded by: なし
