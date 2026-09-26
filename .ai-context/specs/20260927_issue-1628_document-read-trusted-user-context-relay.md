---
title: gRPC の DocumentRead で本文の利用者文脈を信じる呼び出し元を構成の許可集合（既定 bff）に絞る（#1628）
type: spec
status: done
related_ids: [FR-05, FR-06, FR-19, NFR-09, ADR-0119, ADR-0086, ADR-0034, ADR-0109, IADR-0476, IADR-0379, IADR-0402, IADR-0420]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0086_user-context-in-body-not-token-exchange.md 決定 1・§結果（中継サービスが正直であることへの依存）
  - planning:projects/microservices-platform/07_adr/ADR-0119_document-machine-client-own-docs-and-read-abac.md 決定 3
issue: "#1628"
---

# 仕様書: gRPC の DocumentRead で本文の利用者文脈を信じる呼び出し元を絞る（#1628）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **NFR-09**（全 API で文書・データ単位の認可）、FR-19（個人資料）、FR-06（文書の閲覧）、FR-05（ABAC）
- 関連 ADR:
  - **ADR-0086 決定 1**（利用者の権限で動く east-west は利用者文脈を本文で運ぶ）と §結果「中継サービスが正直であることへの依存が 1 段増える（受け入れたトレードオフ）」
  - **ADR-0119 決定 3**（判定の主体: エッジが中継した利用者／east-west で本文が運んだ利用者／機械クライアント自身）
  - ADR-0034 決定 9（サービスアカウントは個人資料を一律に対象外）
- 関連 IADR: IADR-0476（本件はその追記で記録する。新しい IADR は起こさない）、IADR-0379 決定 4（利用者のトークンは面を通らない）、IADR-0402（文書読み取りの gRPC 面）、IADR-0420（`MachinePrincipal`）
- 起点 issue: #1628（PR #1626 の監査の F1）

## 目的・背景

`DocumentRead` の 4 rpc は、本文の `UserContext.user_id` を「その利用者」として信じる（`GrpcService.PrincipalOf`）。
面の門は `ServiceCaller`（realm ロール `platform-service`）だけで、クライアントごとの区別が無い。realm では 11 のサービスアカウントが
`platform-service` を持ち、うち 1 つは別プロジェクト（AST）の `ai-stock-trading-llm-caller` である。どれかが `user_id` を任意の利用者にして
呼べば、その利用者の個人資料の表題・owner・共有先が返る。実際に `DocumentRead` を呼ぶのは BFF だけである（下記の母集合）。

## 対象範囲

- 対象: DocumentService の gRPC `DocumentRead` 4 rpc の主体の決定（`PrincipalOf`）と、その構成（信頼する呼び出し元の集合）。
- 対象外:
  - REST の読み取り（主体は呼び出し元の資格情報そのもので、本文の主張を信じない）。
  - 他の面で本文の利用者文脈を受け取るもの（`DocumentTagWrite/AddTag`・`GraphQuery`・検索の `HybridSearch` / `ListValues`・
    `AuthzScope/Resolve`）。同じ信頼の形を持つが、issue の範囲外であり報告に留める（下記「残るもの」）。
  - realm のロール付与の変更（`ai-stock-trading-llm-caller` は下記のとおり `platform-service` を要する）。
  - 内容の ABAC（#1615）。

## 設計

### 1. 判断: 信頼しない呼び出し元が `user` を付けてきたら **拒否する（`PERMISSION_DENIED`）**

選択肢は 2 つあった。

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A | 機械として扱う（`user` を捨て、呼び出し元サービス自身として読む） | 個人資料は返らない。ただし **ADR-0119 決定 3 の「本文で利用者が運ばれたなら、その利用者」を、呼び出し先が黙って別の主体へ読み替える**ことになる。呼び出し元は利用者の視野のつもりで機械の視野を受け取り、誤りに気付けない（#1614 の「利用者が分からないを機械の主体へ畳まない」と同じ理由で退ける） |
| **B** | **拒否する**（`PERMISSION_DENIED`） | **採用。** 受け付けた呼び出しでは決定 3 の主体の規則がそのまま成り立つ。新しい中継者を足すときは構成を 1 行足す判断が要り、足し忘れは静かな縮退ではなく拒否として見える |

ADR-0086 決定 1 との関係: 決定 1 は「利用者文脈を本文で運ぶ」という**運び方**を定め、§結果は「中継サービスが正直であること」への依存を受け入れた。
**受け入れたのは、利用者の権限で動く east-west の中継者への依存であって、`platform-service` を持つ全主体への依存ではない。** `DocumentRead` で
利用者の権限で動く中継者は BFF だけであり（母集合）、依存の範囲を実在する中継者へ狭めても決定 1 の形（本文で運ぶ）は変わらない。
`user` を付けない呼び出し（呼び出し元サービス自身＝機械の主体）は従来どおり全ての `platform-service` の主体に開いている（ADR-0119 決定 3 の 3 種目）。

### 2. 信頼する呼び出し元の判定

- 呼び出し元が **機械の主体**（`MachinePrincipal.IsMachine`）であり、かつ **クライアント識別**（`MachinePrincipal.ClientIdOf`。`azp` を第一に、
  無ければ `service-account-<clientId>` から復元）が許可集合に**序数一致**で含まれるときだけ、本文の `user` を信じる。
- 機械であることを併せて求めるのは、`azp` が人のトークンにも付く（BFF のセッションの利用者トークンは `azp=bff`）ためである。
  `ServiceCaller` を通った人のトークンは想定しないが、`azp` だけで信じる形にはしない。
- 判定は新しい述語を作らず、既存の `MachinePrincipal` 2 関数だけで書く（IADR-0420 の「判定を基盤に 1 つ」）。

### 3. 構成

- キー: `DocumentRead:TrustedUserContextClients`（配列。環境変数なら `DocumentRead__TrustedUserContextClients__0=bff`）。
- **未構成なら既定 `["bff"]`**（helm の BFF の `serviceToken.clientId` と同じ値。compose も同じ）。
- **構成したら既定を置き換える**（足し合わせない）。.NET の配列の束縛は初期値に構成値を**追記する**ため、プロパティの既定は null にし、
  「null なら既定」を読み出し側で解決する（初期値 `["bff"]` を置くと `bff` を外せなくなる）。
- 空白だけの要素は捨てる。要素が 1 つも残らなければ誰も信じない（fail-closed）。
- 🔴 `MachinePrincipal` の「サービスアカウントの一覧を構成に持たない」とは向きが逆である。あちらは一覧から外すと統制を免れる（抜け道）。
  こちらは**許可**の集合で、外すと狭くなり、空なら誰も信じない。`SyntheticTraffic` の許可集合と同じ fail-closed の形である。

### 4. 変更点

- `DocumentService/Features/Documents/DocumentReadRelayOptions.cs`（新規）: 構成と判定 `TrustsUserContextFrom(ClaimsPrincipal)`。
- `GrpcService.cs`: `PrincipalOf(user, caller, relay)`。`user` が在り呼び出し元が信頼されなければ `PERMISSION_DENIED`（拒否はクライアント識別だけを警告ログへ残す。基数は realm の機密クライアント数で閉じる）。
- `Program.cs`: 構成の束縛。
- `DocumentReadPrincipal.cs`: 注記の更新。
- IADR-0476: 追記ブロック（決定 2 の「本文の利用者文脈を信じてよい理由」を改める）。
- docs: `docs/api/east-west-grpc.md` の 5 つ目の面、`docs/security/security.md` の読み取りの節。

## 受け入れ基準

- AC-1: BFF 以外の `platform-service` の主体（例: `ai-stock-trading-llm-caller`）が `user` に他人（所有者）を載せて 4 rpc を呼ぶと、4 つとも `PERMISSION_DENIED`。個人資料は返らない。
- AC-2: 同じ主体が `user` を載せなければ、組織文書は読め、個人資料は一覧に出ず個別は `found=false`（機械の主体。拒否が一律ではないことの対照）。
- AC-3: BFF（`azp=bff`）が所有者の `user` を載せると個人資料が返る（陽性対照）。他人の `user` なら返らない（従来どおり）。
- AC-4: `azp=bff` を持つ**人**のトークン（`platform-service` を持っていても）が `user` を載せると `PERMISSION_DENIED`。
- AC-5: 構成が無ければ許可集合は `bff` だけ。構成すると既定を置き換える（`bff` は残らない）。空白だけの構成は誰も信じない。
- AC-6: 変異試験: 判定から呼び出し元の確認を落とすと AC-1 が落ちる。
- AC-7: 既存の `DocumentRead` の試験（`GrpcDocumentReadTests`・`DocumentReadAuthenticationTests`）と BFF の試験が通る。

## 呼び出し元の母集合（着手前に自分で引いた）

### 引き方

- `grep -rln "DocumentRead.DocumentReadClient\|DocumentReadClient\b" --include=*.cs src` —— gRPC クライアントの生成箇所。
- `grep -n -B3 'value: "http://document-service:8081"' deploy/helm/microservices-platform/values.yaml` —— document-service の h2c へ向く構成。
- realm（`deploy/keycloak/microservices-platform-realm.json`）の `users[].serviceAccountClientId` と `realmRoles`。

### 結果

- `DocumentRead` のクライアント: **BFF の `DocumentReadGrpcClient` だけ**（他は DocumentService・BFF の試験）。
- h2c 8081 へ向く構成は 4 つ: graph（`DocumentTagWrite`）、mcp-server（`McpToolDeclarations`）、BFF の introspection（`Introspection`）、BFF（`DocumentRead`）。`DocumentRead` を呼ぶのは BFF だけ。
- BFF の s2s の client は helm `services.bff.serviceToken.clientId: bff`。realm の `service-account-bff` が `platform-service` を持つ。
- `platform-service` を持つサービスアカウント 11: bff・aianalysis-service・graph-service・conversion-service・retrieval-service・ingestion-service・wiki-service・
  datasource-service・mcp-server・document-service・ai-stock-trading-llm-caller。

### `ai-stock-trading-llm-caller` の `platform-service`

- AST（隣接クローン、読み取りのみ）の TradeDecisionService・ReportService が LlmGateway の `/complete`（REST）と `CompletionService`（gRPC）を
  この client の s2s で呼ぶ（AST/IADR-0323・AST/IADR-0332）。LlmGateway の両面の門は `ServiceCaller`＝`platform-service` である。
- **したがって外せない**（外すと AST の LLM 呼び出しが 403 / `PERMISSION_DENIED`）。LlmGateway 用の狭いロールを作って置き換える案はあるが、
  realm と LlmGateway の門の両方を変える別の作業であり、本件の修正（許可集合）で `DocumentRead` の穴は閉じるため、realm は変えない。

### 除外したもの

- REST の読み取り: 主体は資格情報そのもの。本文の主張を読まない。
- 他の面の `UserContext`: 対象外（上記）。

## 残るもの

- 本文の `user_id` を `platform-service` の全主体から信じる面は他にもある（`DocumentTagWrite/AddTag`・`GraphQuery`・`HybridSearch`・`ListValues`・
  `AuthzScope/Resolve`）。同じ形の許可集合を当てるかは面ごとに呼び出し元が違うため、別の issue で扱う。

## 検証

- `dotnet test src/knowledge/backend/backend.slnx` / `dotnet test src/platform/backend/backend.slnx`
- `dotnet format <slnx> --verify-no-changes`（両ユニット）
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- 変異: `TrustsUserContextFrom` の呼び出しを落として AC-1 の試験が落ちることを確かめ、戻す。

### 結果（2026-09-27・ローカル）

- `dotnet test src/knowledge/backend/backend.slnx`: exit 0（DocumentService.Tests 705 合格。全プロジェクト失敗 0）
- `dotnet test src/platform/backend/backend.slnx`: exit 0（Platform.Bff.Tests 768 合格・1 スキップ。全プロジェクト失敗 0）
- `dotnet format <slnx> --verify-no-changes`: 両ユニット exit 0
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`: 841 tests passed（初回は test-spec-coverage の床の上げ忘れで落ち、`--update` で 3 件を床へ足した）
- 変異 1（`PrincipalOf` の `TrustsUserContextFrom` を `if (false && …)` で落とす）: `DocumentReadTrustedRelayTests` 8 件中 6 件が落ちた
  （BFF 以外 4 主体の Theory・`azpがbffでも人のトークンは利用者文脈を運べない`・`信頼しない呼び出し元の空の利用者文脈はPERMISSION_DENIED`〔InvalidArgument が返った〕）。
- 変異 2（`TrustsUserContextFrom` の `IsMachine` を落とす）: 2 件が落ちた（人のトークンの統合試験と単体試験）。
- どちらも戻し、`grep "false &&"` で残りが無いことを確かめた。
