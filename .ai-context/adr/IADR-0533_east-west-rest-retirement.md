---
title: IADR-0533 east-west の REST 同期呼び出しを退役させる —— 移行済みの全経路で REST 実装を撤去して「並走中の正は REST」（IADR-0379 決定 5）を反転し、未構成の gRPC は UNAVAILABLE として縮退させ、扇形の構成キーを一本化する
type: impl-adr
status: Accepted
related_ids: [NFR-09, NFR-16, ADR-0029, ADR-0075, ADR-0086, ADR-0087, ADR-0089, IADR-0379, IADR-0413, IADR-0426, IADR-0462]
author: claude
created: 2026-10-10
updated: 2026-10-10
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0089_east-west-completion-rule-and-authz-service-face.md（決定 1）
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md（決定 2）
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md
related_specs:
  - ../specs/20261010_issue-1255-1517_east-west-rest-retirement.md
---

# IADR-0533: east-west の REST 同期呼び出しを退役させる

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: Accepted
- 日付: 2026-10-10
- 決定者: Claude（実装）／起点は #1255 の残射程 2 と #1517

## 起点・関連

- 関連する計画書 ID: ADR-0089 決定 1（REST 実装が残り構成で選ばれる経路は「解けた」と数えない。段の on は条件にしない。
  撤去が移行の終わりである）・ADR-0075 決定 2・ADR-0029・NFR-09・NFR-16。
- 反転する実装判断: [IADR-0379](./IADR-0379_east-west-grpc-preconditions.md) **決定 5**（「`Services:<Name>Grpc` が構成されたときだけ
  gRPC を使い、無ければ REST。並走中の正は REST」）。
- 関連: [IADR-0426](./IADR-0426_east-west-grpc-rag-search-and-user-context-by-argument.md)（RAG の検索輸送。利用者トークンの転送は REST 輸送だけ）・
  [IADR-0462](./IADR-0462_introspection-grpc-fanout-per-target-opt-in.md)（扇形 2 経路の gRPC 面）・
  [IADR-0413](./IADR-0413_authz-resolves-user-attributes-and-rest-face-authorization.md)（`/authz/scope` の `ServiceCaller`）。
- 作業仕様書: [20261010_issue-1255-1517_east-west-rest-retirement](../specs/20261010_issue-1255-1517_east-west-rest-retirement.md)（母集合 19 経路と除外の理由）。

## コンテキストと課題

east-west の 19 経路は gRPC 実装を持ち、配備（compose・helm）はすべて gRPC の宛先を構成していた。それでもコードには
REST 実装が残り、宛先の構成の有無で REST と gRPC が選ばれていた。ADR-0089 決定 1 の数え方では、この形の経路は
「解けていない」。IADR-0379 決定 5 が REST を正にしたのは「切替の事故を構成を外すだけで戻せる」ためだったが、
戻し先の REST 実装を残し続けるかぎり移行は終わらない。

撤去にあたって 3 つを決める必要があった。(1) gRPC の宛先が構成されていない配備をどう扱うか、
(2) 扇形 2 経路（BFF の構成の自己申告・McpServer のツール申告）の構成キーが REST と gRPC で 2 つに割れていること、
(3) 既定 off の段（近傍展開 G-1）と、呼び出し元が 0 になる REST の受け口を射程に含めるか。

## 検討した選択肢

### 宛先が未構成のとき

1. **起動を止める** —— 単体試験・`dotnet run` の各ホストが宛先を与えないと起動できなくなる。
   `WebApplicationFactory.ConfigureAppConfiguration` は組み立て時の読み取りに間に合わない（IADR-0379 §結果の既知の罠）ので、
   試験の器を全部書き換えることになる。
2. **コードに既定のアドレスを持つ** —— 配備の Service 名が compose と helm で違う宛先がある（`llm-gateway` / `llmgateway-service`）。
   既定が外れると名前解決は通るのにポートが無い形で沈黙する（#342 の REST と同型）。
3. **宛先へ届かないのと同じ `UNAVAILABLE` を返す呼び出し器の上に組む** —— gRPC だけの既存の経路（文書 → 認可、データソース →
   部門照会、MCP のツール実行）が既に採っている「構成が無ければ縮退」と同じ向き。各実装の縮退の枝を 1 行も変えずに使える。

### 扇形の構成キー

1. 2 つのキー（REST の `Introspection:Services` / `Mcp:Services` と gRPC の `…:GrpcServices`）を残す。
2. **`…:Services` へ一本化し、値を gRPC の宛先にする。旧キーが残っていれば起動を止める。**
3. `…:GrpcServices` へ一本化する。

## 決定

1. **IADR-0379 決定 5 を反転する。** 移行済みの 19 経路（作業仕様書の母集合表）で REST 実装を撤去し、east-west の輸送を
   gRPC だけにする。「並走中の正は REST」は今後どこにも書かない。IADR-0379 本文には日付つきの追記で本決定を指す。
2. **未構成の gRPC は `UNAVAILABLE`（案 3）。** 共有の `UnconfiguredGrpcDestination`（`CallInvoker`）が理由（構成キーの名前）を
   ステータスの詳細に載せて `UNAVAILABLE` を返す。サーバストリーミングは本物のチャネルと同じく読み出し（`MoveNext`）で失敗する。
   各 `Add…GrpcClient` は宛先が無くても生成クライアントを登録する（`TryAddUnconfiguredGrpcClient`）。
3. **扇形のキーは `Introspection:Services` / `Mcp:Services` へ一本化する（案 2）。** 値は gRPC の宛先（`:8081`）。旧キー
   `Introspection:GrpcServices` / `Mcp:GrpcServices` が 1 件でも残っていれば、両キーを名指す `InvalidOperationException` で
   起動を止める（上書き値の移し忘れを黙って通さない）。REST の受け口 `GET /internal/introspection`・`GET /internal/mcp-tools`
   と、その収集（`HttpEffectiveConfigCollector`・`HttpToolDeclarationSource`）は撤去する（#1517）。
4. **G-1（Retrieval → Graph の近傍展開）を含める。** 理由は 3 つ: (a) ADR-0089 決定 1 は「段が on になることは達成の条件に
   しない」と明記しており、既定 off は除外の理由にならない。(b) REST の展開器は利用者の `Authorization` を転送する方式で、
   east-west gRPC の入口（RAG の検索）からは転送できる資格情報が無く、既に呼ばれていなかった（IADR-0426 決定 2）。
   (c) 段は既定 off であり、撤去で変わる稼働中の挙動が無い。
5. **呼び出し元が 0 になる REST の受け口は、#1517 の 2 つを除いて残す（残余）。** ADR-0089 決定 1 の基準は
   「構成で選ばれる」ことであり、呼び出し元の撤去で満たされる。受け口の撤去は試験の移植を伴い、一部は north-south や
   AST も使う（`/complete`・`/search`・`/documents`）。撤去は別 issue で受け口ごとに行う。

## 理由

- **決定 1**: 戻し先の REST を残す限り、ADR-0089 の数え方で移行は終わらない。gRPC の宛先は配備でとうに構成済みであり、
  稼働中の経路は既に gRPC を通っている。残っていたのは「構成を外したときの行き先」だけである。
- **決定 2**: 案 1 は試験の器の全面改修を要し、案 2 は配備の名前の不一致で静かに壊れる。案 3 は、宛先の欠落を
  「届かない」と同じ観測（各実装の WARN と縮退）へ載せ、理由をステータスの詳細に残す。
- **決定 3**: 案 1 は同じ宛先を 2 か所に書かせ続ける。案 3 は既存の上書き（`Mcp__Services__*`）を黙って無効にする。
  案 2 は既存の名前を残しつつ値の意味だけを変えるので、旧キーの検出で移し忘れを機械的に止められる。
- **決定 4・5**: 計画が数えるのは「構成で選ばれる輸送が残っているか」であり、受け口の有無ではない。

## 結果

- 良い影響: east-west の 19 経路が ADR-0089 決定 1 の意味で「解けた」。構成キーの二重化が消えた。
- 悪い影響・トレードオフ:
  - Runbook の「REST への緊急切り戻し」は使えなくなった。gRPC の不調は gRPC の側で直す。
  - gRPC の宛先を書き忘れた配備は、起動はするが該当経路が `UNAVAILABLE` で縮退する（起動では気づけない）。
    縮退の WARN にはステータスの詳細（構成キーの名前）が載る。
  - BFF に `Services__AuthorizationServiceGrpc` が要るようになった（従前 B-1 は REST だった）。
  - 変換の受け口で呼び出し元が取り消したとき、図のコード化の失敗は `RpcException(Cancelled)` で外へ出る
    （REST では `OperationCanceledException` だった。gRPC の配備では従前から同じ）。
- フォローアップ（残余）:
  1. 呼び出し元が 0 になった REST の受け口の撤去（`/authz/scope`・`/embed`・`/complete/stream`・`/search/attribute-values`・
     グラフ近傍と辺の型の辞書・`/internal/notifications`・`/internal/knowledge-health/observations`・`/internal/tags/names`・
     `/documents/{id}/tags`）。
  2. Graph → LLM のクラスタ要約（`LlmGatewayClusterSummaryClient`。REST・既定 off）の gRPC 移行。
  3. 使われなくなった REST の宛先の env（`Services__AuthorizationService`・`Services__LlmGateway` の一部）の掃除。
  4. `BffScopeResolver.ResolveAsync` の未使用の引数 `IHttpClientFactory` の除去。
  5. `SearchUserContext.ForwardableCredential` の整理（REST 輸送の転送のためだけの値）。

## 関連

- Supersedes: なし（IADR-0379 決定 5 だけを反転する。IADR-0379 の他の決定は有効）
- Superseded by: なし
