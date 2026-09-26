---
title: gRPC の DocumentSearch/Search で本文の利用者文脈を信じる呼び出し元を構成の許可集合（既定 aianalysis-service）に絞る（#1635）
type: spec
status: done
related_ids: [FR-03, FR-04, FR-05, FR-07, FR-19, NFR-09, ADR-0086, ADR-0119, ADR-0034, IADR-0426, IADR-0416, IADR-0379, IADR-0420]
author: claude
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0086_user-context-in-body-not-token-exchange.md 決定 1・§結果（中継サービスが正直であることへの依存）
  - planning:projects/microservices-platform/07_adr/ADR-0119_document-machine-client-own-docs-and-read-abac.md 決定 3
issue: "#1635"
---

# 仕様書: gRPC の DocumentSearch/Search で本文の利用者文脈を信じる呼び出し元を絞る（#1635）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/microservices-platform/`）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **NFR-09**（全 API で文書・データ単位の認可）、FR-19（個人資料）、FR-03 / FR-04 / FR-07（検索・RAG の文脈収集・データ範囲）、FR-05（ABAC）
- 関連 ADR:
  - **ADR-0086 決定 1**（利用者の権限で動く east-west は利用者文脈を本文で運ぶ）と §結果「中継サービスが正直であることへの依存」
  - **ADR-0119 決定 3**（判定の主体: エッジが中継した利用者／east-west で本文が運んだ利用者／機械クライアント自身）
  - ADR-0034 決定 1（判定の位置は受け口）
- 関連 IADR: **IADR-0426**（gRPC `DocumentSearch/Search` を導入した記録。本件はその追記で残す。新しい IADR は起こさない）、
  IADR-0416（受け口が自分でスコープを解決する）、IADR-0379 決定 4（利用者のトークンは面を通らない）、IADR-0420（`MachinePrincipal`）
- 先行の同型: #1628（PR #1631、`DocumentRead`）。**未マージなので、その型・キーには依存しない**（本件は RetrievalService に自前の構成型を置く）。
- 起点 issue: #1635（PR #1631 の監査で見つかった同型でより重い穴）

## 目的・背景

`DocumentSearch/Search`（`RetrievalService/Features/Search/Hybrid/GrpcService.cs`）は本文の `request.User.UserId` を信じ、
`ISearchAccessResolver.ResolveForUserAsync` で**名乗った利用者のスコープ**を解決して検索する。スコープには個人資料の分岐が含まれ、
結果にはチャンクの**本文**（`text`）が入る。面の門は `ServiceCaller`（realm ロール `platform-service`）だけで、そのロールを
持つ 11 のサービスアカウント（別プロジェクト AST の `ai-stock-trading-llm-caller` を含む）のどれもが、他人の個人資料の中身を読めた。
また利用者属性（`user_attributes`）も本文なので、管理者の属性を名乗ることもできた。

## 対象範囲

- 対象: RetrievalService の gRPC `DocumentSearch/Search` の主体の決定（本文の `user` を信じるか）と、その構成（信頼する呼び出し元の集合）。
- 対象外:
  - REST `POST /search`（主体は呼び出し元の資格情報そのもの。本文の主張を権限の根拠にしない。IADR-0416）。
  - 同じ RetrievalService の `AttributeValues/ListValues`（呼び出し元は BFF。返すのは属性値で本文ではない）。同じ信頼の形を持つので「残るもの」に報告する。
  - 他サービスの面（`DocumentTagWrite/AddTag`・`GraphQuery`・`AuthzScope/Resolve`）。
  - realm のロール付与の変更（`ai-stock-trading-llm-caller` は LlmGateway のために `platform-service` を要する。#1628 の仕様書と同じ）。

## 設計

### 1. 信頼しない呼び出し元が `user` を付けてきたら **拒否する（`PERMISSION_DENIED`）**

#1628 と同じ判断（案 A「機械として扱う」を退け、案 B「拒否」を採る）。加えて本面には固有の事情がある。

- **`Search` は `user` 無しを既に `INVALID_ARGUMENT` で拒んでいる**（IADR-0426 決定 3「利用者が分からない」）。機械の主体として
  検索する口は元々無い。したがって「読み替え」案は面の既存の規則（利用者の無い検索は受けない）とも衝突する。
- **`user` を付けない呼び出しは、従来どおり全ての `ServiceCaller` に `INVALID_ARGUMENT`**（広げない。誰にも機械の視野を新設しない）。
- 判定の順: ① `user` が無い（または `user_id` が空白）→ `INVALID_ARGUMENT`（従来どおり・呼び出し元を問わない）、② 呼び出し元が
  信頼されない → `PERMISSION_DENIED`、③ スコープ解決・検索。
  - ①を②より先に置くのは、①が要求の形の誤りであって権限の判断を要しないからで、どの呼び出し元にも同じ答えを返す（何も漏れない）。
    #1631 は「user 在り・user_id 空」を信頼しない呼び出し元に `PERMISSION_DENIED` で返すが、`DocumentRead` には `user` 無しの正当な
    機械の口があり、本面には無い、という違いによる。本面では空の `user_id` は常に `INVALID_ARGUMENT` のままにする（既存の試験 T-04 と同じ）。
  - 空の query の早期 return（空の並び）は②の後に置く（信頼しない呼び出し元に「空の query なら通る」形を残さない）。

### 2. 信頼する呼び出し元の判定

- 呼び出し元が **機械の主体**（`MachinePrincipal.IsMachine`）であり、かつ **クライアント識別**（`MachinePrincipal.ClientIdOf`。`azp` を第一に、
  無ければ `service-account-<clientId>` から復元）が許可集合に**序数一致**で含まれるときだけ、本文の `user` を信じる。
- `MachinePrincipal` は `Platform.Shared.Infrastructure`（共有 3 プロジェクトの 1 つ）に在るので**再利用する**（写しを作らない）。
- 🔴 realm の `aianalysis-service` の既定クライアントスコープは `roles` だけで `profile` を持たない —— **実トークンは `preferred_username` を持たず
  `azp=aianalysis-service` だけを持つ**。`MachinePrincipal` の腕 B（利用者名が無く、クライアント識別がある）で機械と判定され、`ClientIdOf` は `azp` を返す。
  試験はこの実形と、`preferred_username = service-account-aianalysis-service` の形の両方で陽性対照を取る。
- 🔴 `azp` を第一に見るので、利用者名が `service-account-aianalysis-service` でも `azp` が別なら**別のクライアント**として扱う（信じない）。

### 3. 構成

- キー: `DocumentSearch:TrustedUserContextClients`（配列。環境変数なら `DocumentSearch__TrustedUserContextClients__0=aianalysis-service`）。
  gRPC のサービス名を節名にする（#1631 の `DocumentRead:` と同じ命名。RetrievalService 固有のキーで、#1631 の型・キーに依存しない）。
- **未構成なら既定 `["aianalysis-service"]`**（helm `services.aianalysis.serviceToken.clientId` と compose `ServiceToken__ClientId` と同じ値）。
- **構成したら既定を置き換える**（.NET の配列の束縛は初期値に追記するので、既定は null にし `EffectiveClients` で解決する）。
- 空白だけの要素は捨てる。1 つも残らなければ誰も信じない（fail-closed）。
- 変更点:
  - `RetrievalService/Features/Search/Hybrid/DocumentSearchRelayOptions.cs`（新規）: 構成と判定 `TrustsUserContextFrom(ClaimsPrincipal?)`。
  - `GrpcService.cs`: 判定の差し込みと拒否の警告ログ（クライアント識別だけ。基数は realm の機密クライアント数で閉じる）。
  - `Program.cs`: 構成の束縛。
  - 試験器 `Tests/Grpc/GrpcKestrelFactory.cs`: `IssueToken` に `azp` と「利用者名を載せない」を足す（既定は従来の形）。
  - IADR-0426: 日付つき追記（決定 3 の表の「認可」行の注記と、末尾の追記 1）。
  - docs: `docs/api/east-west-grpc.md` §11 つ目の面、`docs/security/security.md`、`docs/tests/FR-03_hybrid-search.md`（T-89）。

## 受け入れ基準

- AC-1: aianalysis-service 以外の `platform-service` の主体（`ai-stock-trading-llm-caller`・`bff`・`mcp-server`・`document-service`・`retrieval-service`）が
  `user` を載せて `Search` を呼ぶと `PERMISSION_DENIED`。スコープ解決（`ResolveForUserAsync`）は呼ばれない。
- AC-2: クライアント識別の接頭辞・大小文字の変種（`aianalysis-service-x`・`aianalysis-servic`・`AIANALYSIS-SERVICE`・`xaianalysis-service`）は信じない。
- AC-3: 利用者名が `service-account-aianalysis-service` で `azp` が別（`ai-stock-trading-llm-caller`）のトークンは信じない。
- AC-4: 人のトークン（`azp=aianalysis-service` を持ち `platform-service` を持っていても）は信じない（`PERMISSION_DENIED`）。
- AC-5（陽性対照）: aianalysis-service（実トークンの形 = 利用者名なし・`azp` あり、および `service-account-` の利用者名の形）は従来どおり
  利用者として検索でき、スコープ解決はその利用者で呼ばれ、結果が返る。
- AC-6: `user` の無い要求は呼び出し元を問わず `INVALID_ARGUMENT`（広げない）。
- AC-7: 構成が無ければ許可集合は `aianalysis-service` だけ。構成は既定を置き換える。空白だけの構成は誰も信じない。
- AC-8: 配備の固定: compose・helm の aianalysis の s2s の client は既定の集合に入り、realm にその機密クライアントのサービスアカウントが
  `platform-service` 付きで在り、aianalysis の gRPC 検索の宛先が配線されており、配備ファイルは集合を上書きしない。
- AC-9: 変異試験: (a) 判定から呼び出し元の確認を落とすと AC-1 が落ちる。(b) 序数一致を接頭辞一致に変えると AC-2 が落ちる。
- AC-10: 既存の試験（`GrpcDocumentSearchTests`・AI 分析の `GrpcRagSearchTransportTests`）が通る。

## 呼び出し元の母集合（着手前に自分で引いた）

### 引き方

- `grep -rln "DocumentSearch\b\|DocumentSearch\.\|retrieval\.v1\|DocumentSearch/Search" --include=*.cs --include=*.proto --include=*.ts --include=*.py --include=*.go src`
  —— 両ユニットと submodule（`src/ai-stock-trading`、`git submodule update --init` 後）を含む。
- `grep -rn "AddRetrievalSearchGrpcClient\|DocumentSearchClient" --include=*.cs src`（試験を除く）—— gRPC クライアントの生成箇所。
- `grep -n "RetrievalServiceGrpc\|retrieval-service:8081" deploy/docker-compose.yml deploy/helm/microservices-platform/values.yaml` —— retrieval の h2c へ向く構成。
- 隣接クローン `../ai-stock-trading`（読み取りのみ。`.claude/worktrees` を除く）で `DocumentSearch|retrieval.v1|retrieval-service`。
- realm（`deploy/keycloak/microservices-platform-realm.json`）の `users[].serviceAccountClientId`・`realmRoles` と `clients[].defaultClientScopes`。

### 結果

- `DocumentSearch` のクライアント: **AI 分析の `GrpcRagSearchTransport` だけ**（`AddRetrievalSearchGrpcClient`。`Services:RetrievalServiceGrpc` が在るときだけ登録）。
  他のヒットは RetrievalService 自身・試験・proto・注記（`RagOrchestrator` のコメント、`SearchUserContext`）・BFF の `AttributeValuesGrpcClient`（別サービス `AttributeValues`）。
- retrieval の h2c 8081 へ向く構成は 4 つ: aianalysis（`Services__RetrievalServiceGrpc` → `DocumentSearch`）、BFF（`Services__RetrievalServiceGrpc` → `AttributeValues`）、
  BFF の構成情報 API（`Introspection__GrpcServices__retrieval-service` → `ServiceIntrospection`）、mcp-server（`Mcp__GrpcServices__retrieval-service` → `McpToolDeclarations`）。
  **`DocumentSearch` を呼ぶのは aianalysis だけ。**
- AST（submodule・隣接クローン）: gRPC `DocumentSearch` の呼び出しは無い（`retrieval.search_documents` は MCP ツール名の宣言だけ）。
- aianalysis の s2s の client: helm `services.aianalysis.serviceToken.clientId: aianalysis-service`、compose `ServiceToken__ClientId: aianalysis-service`。
  realm の `clients[].clientId = aianalysis-service`（機密・サービスアカウント有効・既定スコープ `roles` のみ）と `service-account-aianalysis-service`（`platform-service`）。
- `platform-service` を持つサービスアカウント 11: bff・ai-stock-trading-llm-caller・aianalysis-service・graph-service・conversion-service・retrieval-service・
  ingestion-service・wiki-service・datasource-service・mcp-server・document-service。

### 除外したもの

- REST `POST /search`: 主体は資格情報そのもの（IADR-0416・IADR-0418）。
- `AttributeValues/ListValues`: 対象外（上記）。

## 配備の順番

- 既定の許可集合は配備の aianalysis の client（`aianalysis-service`）と一致するので、**retrieval-service だけを先に配備してよい**（aianalysis の変更は無い）。
- aianalysis の s2s の client 名を変える配備は、**先に retrieval-service の許可集合（`DocumentSearch__TrustedUserContextClients__N`）へ足すこと**。
  逆順だと aianalysis の gRPC 検索が `PERMISSION_DENIED` になり、`GrpcRagSearchTransport` は警告を出して**引用なしの回答へ静かに縮退する**（例外もヘルスの赤も出ない）。
- REST 輸送（`Services:RetrievalServiceGrpc` 無し）へ戻した配備には影響しない（REST の口は変えていない）。

## 残るもの

- `AttributeValues/ListValues` も本文の `user_id` を全 `ServiceCaller` から信じる（呼び出し元は BFF。返すのは属性値）。同じ形の許可集合を当てるかは別の issue。
- #1631 がマージされたら、`DocumentReadRelayOptions` と本件の `DocumentSearchRelayOptions` の判定部を共有へ寄せるかを検討できる（本件では寄せない —— 未マージの型に依存しない）。

## 検証

- `dotnet test src/knowledge/backend/backend.slnx` / `dotnet test src/platform/backend/backend.slnx`
- `dotnet format <slnx> --verify-no-changes`（両ユニット）
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`
- 変異 (a)(b) を当てて落ちることを確かめ、戻す。

### 結果（2026-09-27・ローカル）

- `dotnet test src/knowledge/backend/backend.slnx`: exit 0（RetrievalService.Tests 345 合格〔新規 3 クラス 29 件を含む〕・AiAnalysisService.Tests 161 合格。全プロジェクト失敗 0）
- `dotnet test src/platform/backend/backend.slnx`: 下記 PR 本文に記録（exit 0 を確かめてから PR を出した）
- `dotnet format <slnx> --verify-no-changes`: 両ユニット exit 0
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`: 841 tests passed（test-spec-coverage の床は `--update` で 3 件を足した）
- 変異（新規 2 クラスの 29 件で実測。どれも戻し、`grep "//MUT"` で残りが無いことを確かめた）:
  - M1 `EnsureTrustedRelay(context)` の呼び出しを落とす: 14 件が落ちた（AI 分析以外 6 主体の Theory・変種 5・`azp` の食い違い・人のトークン・空の query）。
  - M2 許可集合の照合を落とす（`Contains` → `true`）: 21 件が落ちた（上記の統合試験 ＋ 構成・判定の単体試験）。
  - M3 接頭辞一致（`clientId.StartsWith(c) || c.StartsWith(clientId)`）: 4 件が落ちた（`aianalysis-service-x`・`aianalysis-servic` の統合・単体）。
  - M4 大小文字を畳む（`OrdinalIgnoreCase`）: 3 件が落ちた（`AIANALYSIS-SERVICE`・`Aianalysis-Service`）。
  - M5 機械であることの確認を落とす: 2 件が落ちた（人のトークンの統合試験と単体試験）。
- 実装中の気付き: 初版の試験器の点に `doc_scope=private-note` を付けたところ、陽性対照も空の並びになった（索引の模型が個人資料の分岐を
  所有者の分岐だけに許すため）。本件の主張は「名乗った利用者のスコープで本文が返るか」であり個人資料の述語ではないので、点から外して
  `dept` のスコープで模型化した（個人資料の述語は `PrivateNoteSearchExposureTests` の範囲）。
