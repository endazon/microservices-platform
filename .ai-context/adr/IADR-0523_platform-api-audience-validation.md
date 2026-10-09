---
title: IADR-0523 全サービスの JWT 検証は共有の audience（platform-api）を求め、受け付ける値はサービスごとの構成（Auth:Audiences）に持つ。audience は 1 つのクライアントスコープで正当な呼び出し元だけに載せ、mcp-server とは分ける
type: impl-adr
status: Accepted
related_ids: [NFR-09, ADR-0036, ADR-0032, ADR-0086, ADR-0134, ADR-0119, IADR-0516, IADR-0086, IADR-0379, IADR-0429, IADR-0465, IADR-0369]
author: claude
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0036_ownership-based-discretionary-access.md（audience の定めは無い。planning#770 で環流）
  - planning:projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md（同上）
  - planning:projects/microservices-platform/07_adr/ADR-0086_user-context-in-body-not-token-exchange.md（token exchange を採らない）
  - planning:projects/microservices-platform/07_adr/ADR-0134_mcp-client-keycloak-template-and-secret-one-time-display.md 決定 1（MCP クライアントの audience を MCP サーバーに限る）
related_specs:
  - ../specs/20261009_1846_service-audience-validation.md
---

# IADR-0523: 全サービスの JWT 検証は共有の audience（platform-api）を求める（#1846）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-09
- 決定者: 利用者（方式 C の裁定 2026-10-09）・claude（スコープの割り当ての範囲・構成の形・検査器）

## 起点・関連

- 起点 issue: **#1846**（planning#751 の裁定コメントの実測 6「全サービスが audience を検証していない」）。計画への環流: **planning#770**（`feedback`・`decision-needed`）。
- 計画: ADR-0036・ADR-0032 に audience の方針は無い（計画を `audience` で全文検索して確認。作業仕様書）。ADR-0086（BFF は利用者のトークンを token exchange せずに中継する）。ADR-0134 決定 1（MCP クライアントのトークンの audience は `mcp-server`）。
- 前提: [IADR-0516](./IADR-0516_sc12-keycloak-service-account-provisioning.md)（#1844 の追記で `/mcp` の `McpAudience` スキームを置き、残余 3 に本件を残した）・[IADR-0086](./IADR-0086_oidc-issuer-metadata-split.md)（発行元の検証の分離）・[IADR-0379](./IADR-0379_east-west-grpc-preconditions.md) 決定 4（`ServiceCaller`）・[IADR-0429](./IADR-0429_platform-spa-removal-and-bearer-caller-narrowing.md)（BFF の Bearer 腕）・[IADR-0369](./IADR-0369_persist-by-default-and-realm-reconcile-job.md)（realm の差分の適用）。
- 基点コミット: MSP `origin/develop` `842b970f`。

## コンテキストと課題

`AuthExtensions.PlatformJwtBearer`（全サービスの既定の JWT スキーム）は `ValidateAudience = false` だった。署名・発行元・期限が合えば、**同じ realm のどのクライアントに発行されたトークンでも**受け付ける —— 運用ツールの OIDC クライアント（wiki-js・headlamp・grafana・argocd・vault）、realm 管理用（identity-admin・mcp-client-admin・reset-gate）、SC-12 が作る MCP クライアント（`aud=mcp-server`）のトークンを含む。realm の 25 クライアントのうち audience の写像を持つものは 0 件で、`ValidateAudience` を true にするだけでは全経路が 401 になる。

issue の題は「サービスごとの audience」だが、ADR-0086 のため BFF は利用者のトークンを全サービスへそのまま中継する。`bff` のトークンは中継先の全 audience を持たねばならず、**どの方式でも利用者の経路はサービスごとに絞れない**。

## 検討した選択肢

| | A. 共有の platform audience | B. サービスごとの audience | C. A ＋ 受理する値をサービスごとの構成に持つ |
| --- | --- | --- | --- |
| realm | スコープ 1 個（写像 1）を呼び出し元 17 件の既定スコープへ | 呼び出し先ごとの写像を呼び出しの行列どおり（bff だけで 14） | A と同じ |
| サービス | 全サービス `platform-api` | 各サービス自分の名前 | `Auth:Audiences`（既定 `platform-api`） |
| 拒否できるもの | 運用ツール・realm 管理用・MCP クライアント | 左＋行列に無い east-west | A と同じ（構成の変更で B へ寄せられる） |
| 保守 | 配線を足しても realm は不変 | 配線のたびに realm の写像も要る。漏れは稼働で初めて 401 | A と同じ |

**利用者裁定（2026-10-09）: C。** B の追加の効果は east-west の絞り込みだけで、そこは `ServiceCaller`（`platform-service` ロール）・NetworkPolicy・STRICT mTLS（NFR-09）が守っている。

## 決定

### 決定 1: 共有の audience は `platform-api`。`mcp-server` と分ける

- `AuthExtensions.DefaultAudience = "platform-api"`。**`mcp-server` は流用しない** —— ADR-0134 決定 1 で「MCP クライアントに発行したトークン」の意味を持ち、`/mcp` だけが受け付ける。同じ値にすると BFF・サービスのトークンが `/mcp` を通り、MCP クライアントのトークンが全サービスを通る（#1844 の統制が崩れる）。
- 既定のスキームの構成に `mcp-server` が入れば**起動を止める**（`AuthExtensions.ReservedMcpAudience`）。
- `McpAudienceAuthentication` は共有の設定を当てた後で `ValidAudience = "mcp-server"` に加えて **`ValidAudiences = ["mcp-server"]` へ置き換える**。置き換えないと、ハンドラは `ValidAudience` と `ValidAudiences` の和集合で照合する（`platform-api` のトークンが `/mcp` を通る）。

### 決定 2: サービスは `Auth:Audiences` を検証する（既定 `["platform-api"]`、空は起動失敗）

- `PlatformJwtBearer` は `ValidateAudience = true`・`ValidAudiences = ResolveAudiences(config)`。キーが無ければ `[platform-api]`。配列（`Auth:Audiences:0`）とカンマ／空白区切りの文字列（`Auth__Audiences`）の両方を受ける。
- **キーが在るのに空**（`Auth__Audiences=""`）は例外 —— 「検証しない」へ黙って縮退させない。構成は登録の時点で読む（従前と同じ）ので、例外はサービスの起動の時点で出る。
- Helm・compose には値を足さない（既定値で足りる）。サービスごとの audience へ移るときは構成と realm の写像を同じ配備で変える。

### 決定 3: realm はクライアントスコープ `platform-api-audience` 1 個を、正当な呼び出し元にだけ既定で割り当てる

- スコープ: `oidc-audience-mapper`・`included.custom.audience=platform-api`・`access.token.claim=true`（ID トークンには載せない）。
- 割り当て（17 件）: `bff`（利用者のトークンの発行元を兼ねる）・サービスアカウント 9 件（document / datasource / conversion / ingestion / retrieval / aianalysis / wiki / graph / mcp-server）・`abac-seeder`・`synthetic-monitor`・AST が platform realm に持つ 5 件（`ai-stock-trading-kb-writer` / `-kb-reader` / `-llm-caller` / `-svc` / `-owner`）。`kb-reader` は既定スコープが `profile` だけだったが、同じく足した（AST のコードは変えない）。
- 割り当てない: 運用ツール 5 件・realm 管理用 3 件。**realm の既定スコープ（`defaultDefaultClientScopes`）・任意スコープにも置かない** —— SC-12 が作る MCP クライアント（`defaultClientScopes=["profile"]` を明示）や後から足す運用ツールへ黙って継がれない。
- 範囲は**列挙せず形で検査する**（`check-realm-constraints.js` 検査 9）: サービスアカウントを持ち `realm-management-roles` を持たないクライアントは全員持つ／それ以外は `bff` を除き持たない／既定・任意スコープに置かない／写像の値。
- 稼働の realm へは `reconcile-realm.js` が差分（スコープ・写像・既定スコープの割り当て）を当てる（IADR-0369。新しい仕組みは要らない）。

### 決定 4: 稼働の実測は integration-stack の既存の門に足す（M11）

`check-mcp-client-provisioning.js` は管理者のトークン・evaluate-scopes・MCP クライアントの実トークンをすでに持つので、そこへ M11 を足した: `bff`（利用者）・document-service・kb-reader・使い捨ての登録者のトークンの `aud` に `platform-api` が在り `mcp-server` は無い／grafana（利用者）・有人と無人の MCP クライアントのトークンに `platform-api` は無い／無人の MCP クライアントの実トークンで McpServer の管理 API は **401**（ロール不足の 403 ではない）。使い捨ての登録者は McpServer の管理 API を呼ぶので、既定スコープへ `platform-api-audience` を足した（足さないと門ごと 401 で赤）。利用者の経路の陽性対照は ABAC の正常系の門（`verify-oidc-edge-flow.sh`。BFF が利用者のトークンを後段へ中継する）が兼ねる。

## 結果

- 運用ツール・realm 管理用・MCP クライアントのトークンは、全サービス（BFF の Bearer 腕を含む）で 401 になる。
- 単体の試験の器（gRPC の Kestrel・WebApplicationFactory）11 か所のトークンに `aud=platform-api` を載せた（実 Keycloak の呼び出し元のトークンと同じ形）。否定形: ConversionService の REST（aud 無し・mcp-server・grafana・account は管理者を名乗っても 401。aud が配列で `platform-api` を含めば通る）、AuthorizationService の gRPC（`platform-service` ロールを持っていても aud 無し・mcp-server・grafana・document-service は Unauthenticated）、McpServer の `/mcp`（`platform-api` は 401）。
- 構成の器の単体: 既定・文字列・配列・空は例外・`mcp-server` は例外。

## 残余

1. **配備の順序**: サービスだけが先に入ると全経路が 401。realm を先に当てる（運用仕様書「トークンの audience の検証の導入」）。経路B の `k8s-local-up.sh` は helm の適用の後に realm を追随させるので、永続化した既存のクラスタでは reconcile を先に単独で流す。
2. **既存の BFF セッション**: 導入前に発行されたアクセストークンは `aud` を持たない。リフレッシュで既定スコープが再評価される見込みだが稼働では未実測（再ログインで解消）。
3. **AST の配備順序**: AST の 3 経路（KB の保存・検索・LLM）は platform realm のクライアントのトークンを使う。platform realm の変更が先に当たっていないと 401。AST のコードは変えない（配備の告知だけ）。
4. **サービスごとの audience**（方式 B）へ移るなら、呼び出しの行列と realm の写像を突き合わせる検査器が先に要る（漏れは稼働で初めて 401）。
5. 稼働の `aud` の実値（M11）は本 PR の integration-stack の実行で初めて測られる（本 PR を書いた環境に k8s は無い）。

## フォローアップ

- planning#770 の裁定（計画への方針の記録）を受けて、必要なら本 IADR に追記する。

## ［2026-10-09 追記 / #1864 監査］監査の指摘の是正

PR #1864 の独立監査（判定 GO）の指摘を同じ PR で是正した。

1. **realm の追随を helm の前へ移し、失敗で止める（残余 1 を解消）**: `k8s-local-up.sh` は realm の追随（`reconcile-realm.sh`）を
   [7/7] の後（best-effort の WARN）で走らせていた。永続化した既存のクラスタでは、[6/7] の helm が入れる新しい Pod が `aud=platform-api` を
   求めるのに稼働の realm はまだ載せない —— Job の完了＋トークン寿命（最大 300 秒）のあいだ全 API が 401、追随が落ちれば WARN だけで
   401 が恒久化する。追随を **[4/7] の Keycloak の rollout の直後（[5/7] の前）** へ移し、**失敗したら非 0 で止める（fail-closed）**。
   - 依存の確認: Job は platform-infra で `http://keycloak:8080` を叩き、[3/7] の ConfigMap `keycloak-realms` と Secret `keycloak-admin` を
     読むだけである。[5/7] の Secret・[7/7] の ExternalName（`platform-infra-externalnames.yaml` の逆向きの別名は Keycloak が
     バックチャネルログアウトで叩く口で、Admin REST の書き込みには要らない）には依存しない。
   - 新規クラスタを塞がない判断: 空の PVC の Keycloak は [4/7] で同じ宣言を取り込んだ直後なので差分は無いか後追いで収束する。
     収束しなければ `check-stack-ready.js` の G9（`--check`＝差分 0）がもともと落とすので、CI の新規の経路で新たに赤になる場面は増えない。
     dev 以外の kube context で管理用クライアントを dev の値で作れない（IADR-0517）ときに止まるのは意図どおり（以前も Job は非 0 だった）。
   - 本移行に限らず恒常的に fail-closed とした（audience の検証は今後も realm の先行を前提にする。逃げ道の env は足さない）。
     `SYNTHETIC=1` の「realm 追随 → 標識の env」の順序は保たれる（追随がさらに前へ移っただけ）。
2. **docker-compose の既存環境の注記**: 運用仕様書の導入節が経路B と新規だけを扱っていた。compose は Keycloak を共有 Postgres へ
   永続化しており、`--import-realm` が既存 realm を黙って飛ばして全経路 401 になる。同仕様書の既存の「realm を更新したときの反映手順」
   （DB の作り直し／Partial import）を先に当てる手順を足した。
3. **検査 9 の別経路**: 範囲の検査は `platform-api-audience` の割り当てしか見ておらず、`platform-api` を出す写像をクライアントへ直付けする・
   `profile` 等の共有スコープや別名のスコープへ置く経路を素通りした。`clientScopes[].protocolMappers` と `clients[].protocolMappers` を
   全件走査し、`oidc-audience-mapper` の `included.custom.audience` / `included.client.audience` が `platform-api` の写像は
   `platform-api-audience` の中にだけ許す。自己試験: grafana への直付け（custom / client）・`profile` への追加・別名 `pa2` を grafana へ
   付けた形の各々を検出し、他の audience（`mcp-server`）の写像は数えない。
4. **人のログインの口＋サービスアカウント**: 両方を持ち `bff` でないクライアントを「呼び出し元だから足せ」と誘導していた。
   人のログインの口は検査 7 と同じ `humanLoginGrants`（未設定の `standardFlowEnabled` は Keycloak の既定で true）で読み、
   スコープの有無によらず「要確認」の文言で名指す（呼び出し元専用なら口を閉じる／中継の口なら判断を記録して例外へ加える）。
   実データの realm で該当するのは `bff`（例外）だけで、新たな赤は無い。自己試験の器の呼び出し元は口を明示的に閉じた。
5. 残余 2（既存の BFF セッションのリフレッシュで `aud` が載るか）は「見込み・未実測」のまま据え置く。
