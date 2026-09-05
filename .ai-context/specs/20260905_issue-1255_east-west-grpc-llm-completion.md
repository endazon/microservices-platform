---
title: east-west gRPC の展開（第 2 スライス）— LlmGateway のテキスト生成面をサーバストリーミングで開き、3 呼び出し元 4 箇所を兄弟実装で移す
type: spec
status: done
related_ids:
  - FR-04
  - FR-11
  - FR-12
  - FR-18
  - NFR-02
  - NFR-09
  - NFR-16
  - UC-01
  - UC-02
  - ADR-0010
  - ADR-0012
  - ADR-0025
  - ADR-0029
  - ADR-0030
  - ADR-0038
  - ADR-0044
  - ADR-0075
  - ADR-0076
  - IADR-0037
  - IADR-0101
  - IADR-0104
  - IADR-0110
  - IADR-0111
  - IADR-0212
  - IADR-0225
  - IADR-0256
  - IADR-0266
  - IADR-0316
  - IADR-0354
  - IADR-0374
  - IADR-0378
  - IADR-0379
  - IADR-0397
  - IADR-0398
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md §決定・2026-08-04 追記
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md 決定 3・4・5・6
  - planning:projects/microservices-platform/07_adr/ADR-0076_slo-evaluation-target-and-metric-units.md 決定 4・5
  - planning:projects/microservices-platform/07_adr/ADR-0044_llm-usage-metrics-and-pricing-table.md 決定 1・3
  - planning:projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md
  - planning:projects/microservices-platform/07_adr/ADR-0012_conversion-pipeline.md
  - planning:projects/microservices-platform/07_adr/ADR-0038_analysis-purpose-drop-fable-5.md 決定 3・4
  - planning:projects/microservices-platform/02_requirements/01_requirements.md FR-04 / FR-11 / NFR-02 / NFR-09 / NFR-16
---

# 仕様書: LlmGateway テキスト生成面の east-west gRPC 化（#1255 第 2 スライス）

> 本書は #1255（east-west gRPC の展開）の**第 2 スライス**の作業仕様である。
> 第 1 スライス（#1290 / IADR-0397。埋め込み面）が着地させた形を**そのまま写す**のが基本方針であり、
> IADR-0379 の 4 決定と `docs/api/east-west-grpc.md` §1〜§4 は**変えない**。
> 認可サービスの名簿（`user_directory.proto`）と `/authz/scope` の 5 呼び出し元は**次のスライス**である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-04（RAG 回答の生成）／FR-11（用途・機密区分による呼び出し先の切替）／
  FR-12（図のコード化）／FR-18（グラフの関連提案）
- 非機能要求（NFR）: NFR-02（RAG 回答の初回応答 p95。**本スライスの中心論点**）／
  NFR-09（全 API で OIDC/JWT 認証。gRPC 面は ServiceCaller）／NFR-16（サービス間 mTLS。h2c ＋ サイドカー終端）
- ユースケース（UC）: UC-01（横断検索・質問回答）／UC-02（分析・比較・抽出）
- 画面（SC）: 直接の画面は無い（サービス間経路）。SC-01 の体験は NFR-02 経由で間接的に掛かる
- 関連 ADR: ADR-0029（east-west 同期は gRPC・所有者は呼ばれる側・キャッシュ等は呼び出し元）／
  ADR-0075（移行順序＝基盤先行。決定 4 で AST は MSP の proto 公開に追随）／ADR-0010（LLM ゲートウェイ）／
  ADR-0044 決定 1・3（用途別・モデル別の費用計測）／ADR-0076 決定 4（合成標識と除外）・決定 5（初回トークン計器）／
  ADR-0038 決定 3・4（フォールバック鎖の射程）／ADR-0012（変換パイプライン）／
  ADR-0025（モデルの終了理由の語彙）／ADR-0030（Grpc.* は採用済みライブラリ）
- 実装 ADR: IADR-0379（先行条件の 4 決定。**本作業はその適用であり改定しない**）／
  IADR-0397（第 1 スライス。**本作業はこの鎖を延ばす**）／IADR-0037（SSE の 1 イベント契約）／
  IADR-0101（MaxTokens 既定 4096）／IADR-0104（StopReason）／IADR-0110（計器の値域）／
  IADR-0111（モデル名を偽らない）／IADR-0212 決定 3（sawDone のときだけ計上）／
  IADR-0225（ストリーム経路はフォールバックを持たない）／IADR-0256 決定 3（故障を「該当なし」に化けさせない）／
  IADR-0266 決定 6（縮退した応答を根拠に使わない）／IADR-0316（Secret 注入の宣言と配備の突合）／
  IADR-0354（初回トークン計器の両端）／IADR-0374（上流ステータスの軸）／IADR-0378（合成標識の 2 段）／
  本作業で新設する IADR-0398
- 計画書リンク: 隣接クローン `../project-planning/projects/microservices-platform/`（読み取り専用）

## 目的・背景

第 1 スライスが `/embed`（利用者の文脈を持たない 1 端点）で gRPC 面の形を確立した。本スライスは
**同じ LlmGateway の残り半分**、すなわち `POST /complete` と `POST /complete/stream`、およびそれを呼ぶ
3 サービス 4 箇所を担当する。

埋め込みと違って本スライスに固有の論点は 1 つだけである ——
**`/complete/stream` は SSE であり、gRPC には unary と server-streaming がある。**
NFR-02 の SLI は**初回トークン**で測るため、輸送の選び方が SLI の意味を変え得る。

## 対象範囲

### 対象

| # | 対象 | 内容 |
| --- | --- | --- |
| 1 | `Protos/platform/llmgateway/v1/completion.proto` | `LlmCompletion/{Complete, CompleteStream}`。**CompleteStream はサーバストリーミング** |
| 2 | LlmGateway サーバ側 | `Features/Completions/CompletionUseCase.cs`（REST と gRPC が呼ぶ**唯一の**本体）＋ `Features/Completions/GrpcService.cs`（ServiceCaller）＋ `Program.cs` の登録 |
| 3 | 共通部品 | `Foundation/Llm/LlmGrpcMapping.cs` と `LlmGatewayGrpcClientExtensions.cs` を**拡張する**（並行ファイルを作らない）。`SyntheticTraffic.PropagateTo(Metadata, bool)` の多重定義 |
| 4 | AiAnalysis（2 箇所） | `ILlmCompletionTransport` ポート ＋ REST 実装 ＋ gRPC 実装。Program.cs が `Services:LlmGatewayGrpc` の有無で選ぶ |
| 5 | Graph（1 箇所） | `LlmGatewayGrpcSuggestionClient : ISuggestionLlmClient`（兄弟クラス。Parse は共通 static） |
| 6 | Conversion（1 箇所） | `LlmGatewayGrpcDiagramCoder : IDiagramCoder`（兄弟クラス。フェンス抽出は共通 static） |
| 7 | 配備・realm | confidential client ×3（`aianalysis-service` / `graph-service` / `conversion-service`）＋ `users[]` の platform-service ＋ ExternalSecret / Vault seed / helm / compose |
| 8 | 記録 | IADR-0398・本仕様書・`docs/api/east-west-grpc.md` の追補・`scripts/proto-contract-baseline.json` |

### 対象外（理由つき）

| 対象 | 理由 |
| --- | --- |
| `user_directory.proto` と `/authz/scope` の 5 呼び出し元 | 次のスライス。利用者トークン転送の論点を別 PR で切る |
| REST の `/complete` `/complete/stream` の撤去 | **並走中の正は REST**（IADR-0379 決定 5）。加えて ADR-0075 決定 4 により AST が移るまで消せない |
| BFF（参照実装）の配備上の未配線 | 第 1 スライスが**意図して**残した（`docs/api/east-west-grpc.md` §未決事項）。本 PR で反転させない |
| AiAnalysis → Retrieval `/search` の利用者トークン転送 | 呼び出し先が**利用者の権限で動く**（ホップごと ABAC。ADR-0034 方式 A）。読み口を狭める形では解けない。次の壁として名指しするだけ |
| ストリーム経路のフォールバック鎖 | ADR-0038 決定 3 / IADR-0225 の射程外。REST が実装していないものを gRPC で足さない |
| `OpenTelemetry.Instrumentation.GrpcNetClient` / gRPC ヘルスプロトコル | CPM 追加を伴う判断（#1255 やること 6）と `docs/api/east-west-grpc.md` §未決事項 |
| AST（`src/ai-stock-trading`）の `POST /complete` ×2 | 別リポジトリ（submodule）。ADR-0075 決定 4 により MSP が proto を公開した時点で AST 側が移る。**本リポジトリからは起票しない** |
| 照合規則・purpose の値域の見直し | 移行の不変条件は「挙動を変えない」 |

## 母集合の再導出（自分で引いた。issue と設計書の数字を転記していない）

基点 `origin/develop` = `42d12ec4`。`git rev-parse --is-shallow-repository` = **`false`**
（履歴の打ち切り無し。`git log` を出典に引ける）。

**軸を 1 本で終わらせない**（traceability.repo.md 規則 5）。4 軸で引き、結果が一致することを確かめた。

| 軸 | 走査 | 生産コードの結果 |
| --- | --- | --- |
| 1（端点の文字列） | `grep -rn --include=*.cs -E '"/complete"｜"/complete/stream"' src` | RagOrchestrator:271・:403／DiagramCoder:33／SuggestionClient:29 ＋ ゲートウェイの端点 2 |
| 2（要求 DTO の構築） | `grep -rn --include=*.cs "new CompletionApiRequest" src` | **同じ 4 箇所**（軸 1 と完全一致） |
| 3（HTTP クライアント登録） | `grep -rn -A2 "AddHttpClient" src/*/backend/Services/*/Program.cs` を LlmGateway で絞る | AiAnalysis:51（名前つき）／Conversion:75／Graph:91 ＋ 埋め込みの 2（移行済み） |
| 4（ストリーム契約の消費） | `grep -rln --include=*.cs "CompletionStreamEvent" src` | RagOrchestrator ／ ゲートウェイの端点 ／ DTO 定義のみ |

- **陽性対照（軸が生きていること）**: 軸 1 は `/Tests/` を含めると **57 行**掛かる（生産 4 ＋ 端点 2 ＋ 試験 51）。
  0 件ではないことを確かめたうえで `/Tests/` を除外している。
- **陽性対照（軸 3 が名前つき登録も拾うこと）**: 埋め込みの 2 呼び出し元（Retrieval / Ingestion）が
  **移行済みの形**（`if (…Grpc) AddSingleton else AddHttpClient`）で掛かる。掛からなければ軸が壊れている。
- **陰性対照**: 軸 4 は生産コードで 3 ファイルしか返さない（消費者は AiAnalysis ただ 1 つ）。
  4 呼び出し箇所のうちストリームは 1 つだけ、という読みと整合する。
- **除外**: `src/ai-stock-trading/`（submodule。別リポジトリの `AddHttpClient("llm", …)`。対象外の表を参照）、
  `/obj/`（生成物）、`/Tests/`（呼び出し元ではなく試験の偽サーバ）。

**結論: 3 呼び出し元クラス / 4 呼び出し箇所。** 4 軸すべてが同じ集合を指した。

### 是正の母集合（規則 9・10）

- 規則 9（**誤りの側の文字列で走査してから挙げる**）: 「LlmGateway の gRPC 面は埋め込みだけ」と述べている
  記述を、誤りの側の語（`LlmEmbedding）だけ` / `テキスト生成（/complete 系）は後続 PR`）で走査し、
  `deploy/helm/microservices-platform/values.yaml`（llmgateway.grpcPort の注記）と
  `docs/api/east-west-grpc.md`（§概要「gRPC 面を持つのは 2 経路」）の 2 箇所を得た。**記憶では挙げていない。**
- 規則 10（**是正で新たに誤りになる自分の記述**）: 本 PR で「2 経路」→「3 経路」になるため、
  `docs/api/east-west-grpc.md` §概要の**導出値**は走査ではなく数え直す（参照実装 1 ＋ 埋め込み 1 ＋ 生成 1 = 3）。
  同様に ESO の「MSP ns は常時 11 本」は **11 → 14** へ数え直す（本 PR で 3 本増える）。

## 設計

### 決定 1: `/complete/stream` は **server-streaming rpc** へ写す。unary へ潰さない

初回トークンの境界は「最初の delta メッセージの**到着**」であり、SSE の「最初の `data:` 行」と同じ位置にある。

- `rag.answer.first_token.duration`（IADR-0354 決定 2）の**両端は AiAnalysis の north-south 応答にあり**、
  AiAnalysis ↔ LlmGateway の輸送とは独立に定義されている。したがって計器は変えない。
  LlmGateway 側に TTFT 計器は**足さない**（SLI の担い手を 2 つにしない）。
- 🔴 **unary へ潰すと**、最初の delta が「生成完了後」にしか届かず、AiAnalysis が最初の token を書く時刻が
  ≒ 生成完了時刻になる。`RagFirstTokenP95High` は**応答完了 p95**を測ることになり、
  ADR-0076 決定 5 が明示的に却下した「長い回答ほど SLO 違反・品質を上げると SLO が悪化する」形へ戻る。
- bidi streaming は過剰（要求は単一メッセージであり、「要求は最初の応答の前に確定している」性質が型から消える）。
- 「`/complete/stream` だけ REST に残す」は ADR-0075 決定 5（実装側 IADR で REST 継続を自認しない）により採れない。

🔴 **書く側がバッファリングしていないこと**（`WriteAsync` を chunk ごとに呼ぶこと）は gRPC の保証ではなく
**コードの性質**であり、試験でしか守れない → **T-P1-03**。

### 決定 2: 判定器は 1 つ（CompletionUseCase）

`Complete/Endpoint.cs` と `CompleteStream/Endpoint.cs` の**本体**を `Features/Completions/CompletionUseCase.cs` へ
括り出し、REST と gRPC の両方が呼ぶ（#1290 が EmbedUseCase を括り出したのと同型）。

同じ `ILlmRouter.Route`・同じ `LlmCompletionMetrics` / `LlmUsageMetrics`・同じフォールバック鎖・
同じ `LogStopReason` を通る。**第 2 のルータ経路を作らない** —— 分けると「どちらか一方だけが越境判定を通る」
という最悪の食い違いが起こり得る。

`isSynthetic` は**引数で渡す**（bool）。判定そのものは既存の
`SyntheticTraffic.IsSyntheticInternalRequest(HttpRequest)` を、REST は `http.Request`、
gRPC は `context.GetHttpContext().Request` から呼ぶ（**定義を 2 つにしない**）。

### 決定 3: メタデータ ＝ 運搬の出所、本文 ＝ 要求の意味（IADR-0397 決定 4 の踏襲）

| 軸 | 載せ場所 |
| --- | --- |
| s2s トークン | メタデータ `authorization` |
| トレース文脈 | メタデータ `traceparent` / `tracestate`（既存の `AddHttpClientInstrumentation` / `AddAspNetCoreInstrumentation` が扱う。proto に書かない） |
| 合成トラフィックの標識 | メタデータ `x-synthetic-traffic`（IADR-0378 内周）。**本文に bool synthetic を置かない** —— 標識は要求の意味ではなく出所であり、本文に置くと全 rpc の不変契約に番号つきで残る |
| purpose / model / confidentiality（ADR-0044 の帰属軸＝ルーティング入力） | **本文**。**enum にしない** —— 値域を閉じるのは設定（PurposeModels）と計器（IADR-0110）であって契約ではない |

### 決定 4: proto3 に null は無い。REST の既定値をサーバ側で明示的に写す

| 契約 | REST の既定 | proto3 の「未指定」 | サーバの写し |
| --- | --- | --- | --- |
| max_tokens | 4096（CompletionApiRequest。IADR-0101） | `0` | **`0 → 4096`**。負数は INVALID_ARGUMENT |
| model | null → ルータが用途で選ぶ | `""` | 写し不要（LlmRouter は IsNullOrWhiteSpace で判定） |
| confidentiality | null → restricted | `""` | 写し不要（SensitivityClasses.Parse が空文字・未知を restricted へ） |
| purpose | null/空白 → `"default"` | `""` | 写し不要（CompletionUseCase が IsNullOrWhiteSpace ? "default"） |
| sent | DTO 既定 **true** | **false** | 🔴 **向きが逆**。サーバは delta メッセージにも `sent=true` を**明示的に**書く |

🔴 sent の写し漏れは例外にならない。**全 delta が「縮退」に見える**形で静かに壊れ、
Graph は提案 0 件・Conversion は画像保持・AiAnalysis は「LLM が利用できません」へ倒れる。

### 決定 5: 縮退は**呼び出し元ごとに**現行の枝へ落とす。生成の縮退は RpcException にしない

🔴 **埋め込みとは向きが逆である。** 埋め込み（IADR-0397）は輸送の失敗を**例外のまま上げる**が、
生成は**上げない** —— REST が 500 を伝播させず `Sent=false` / `done(Sent=false)` で返しているからである。

| # | 呼び出し元 | REST の現行の枝 | gRPC の写し |
| --- | --- | --- | --- |
| 1 | AiAnalysis StreamCompletionAsync | 送信失敗・非 2xx → `done(Sent=false, "LLM が現在利用できません。")` ／ 読み取り中断 → `done(Sent=false, "LLM 応答の受信に失敗しました。")` | 呼び出し確立の RpcException・トークン取得失敗 → 前者。ストリーム読み取り中の RpcException → 後者 |
| 2 | AiAnalysis GenerateAsync | 非 2xx → 出典のみ（fallback）。**接続失敗は例外が伝播する** | RpcException（全 status）・トークン取得失敗 → **非 2xx と同じ枝**（出典のみ）。§計画書との差異を参照 |
| 3 | Graph ProposeAsync | 非 2xx・HttpRequestException / TaskCanceledException → `[]` | RpcException・トークン取得失敗 → `[]` |
| 6 | Conversion CodeAsync | EnsureSuccessStatusCode の例外・接続失敗 → `Retain("llm-call-failed")` | RpcException・トークン取得失敗 → `Retain("llm-call-failed")` |

ゲートウェイ側の縮退（越境拒否・プロバイダ未登録・上流不調）は**すべて応答**である
（`sent=false` の CompleteResponse ／ `done=true, sent=false` の CompletionStreamEvent）。
RpcException になるのは s2s の面（UNAUTHENTICATED / PERMISSION_DENIED）と輸送不達（UNAVAILABLE）、
および max_tokens 負数（INVALID_ARGUMENT）だけである。

### 決定 6: 切替は `Services:LlmGatewayGrpc`。並走中の正は REST

各呼び出し元の Program.cs が、構成の有無で REST 実装か gRPC 実装かを登録する。
**戻すのは構成を外すだけでよい**（コードを変えない）。

## 受け入れ基準

- [x] `completion.proto` が `LlmCompletion/{Complete, CompleteStream}` を宣言し、CompleteStream が **server-streaming** である
- [x] `node scripts/check-proto-contracts.js` が緑（baseline は `--update` で更新し差分を PR に載せる）
- [x] REST（`/complete`・`/complete/stream`）と gRPC が**同一の** CompletionUseCase を通る（第 2 のルータ経路が無い）
- [x] gRPC サービス型が `[Authorize(Policy = ServiceCaller)]` を持つ
- [x] 資格情報無し → UNAUTHENTICATED／**管理者の利用者トークン** → PERMISSION_DENIED
- [x] **最初の delta が done より前に到着する**（時間差で 2 チャンクを出す偽プロバイダで観測）
- [x] REST と gRPC の CompletionStreamEvent 列（delta 列・done の model / tokens / stop_reason）が一致する
- [x] REST と gRPC の CompletionApiResponse が sent / refusal / 通常の 3 経路で一致する
- [x] max_tokens=0 がプロバイダへ **4096** として渡る／負数は INVALID_ARGUMENT
- [x] model="" は未指定・confidentiality="" は restricted・purpose="" は "default" として扱われる
- [x] 🔴 delta メッセージの sent が **true** である（proto3 の既定 false の反転を固定）
- [x] `x-synthetic-traffic` メタデータ付きの呼び出しが費用へ計上されず、除外の計器が増える
- [x] 4 呼び出し元それぞれで REST 実装と gRPC 実装の縮退が一致する（決定 5 の表）
- [x] 既存 RagOrchestratorStopReasonTests / RagOrchestratorDegradedModelTests / LlmGatewayDiagramCoderTests が**両実装**で緑
- [x] realm に 3 client と `users[]` の platform-service 割当があり、helm / compose / ExternalSecret / Vault seed の 4 経路が揃う
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が両ユニットで緑
- [x] check-secret-injected-options / check-realm-constraints / check-unit-dependencies / check-trace-blocks / check-adr-numbering / check-doc-links / check-backend-libraries / check-cpm-versions / check-proto-contracts が緑
- [x] **テストは 1 本も削除・skip しない**（件数の増減を PR に載せる）

## テスト方針

### 呼び出し先（LlmGateway。実 Kestrel の h2c。#1290 の GrpcEmbedTests と同型）

| ID | 内容 |
| --- | --- |
| T-S-01 | 陽性対照。s2s トークンで Complete が往復する |
| T-S-02 | 資格情報無し → UNAUTHENTICATED（Complete / CompleteStream の両方） |
| T-S-03 | 🔴 **管理者の利用者トークン** → PERMISSION_DENIED（両 rpc） |
| T-S-04 | REST と gRPC が同じ入力に同じ答えを返す |
| T-S-05 | `x-synthetic-traffic` メタデータで費用が増えず除外の計器が増える（IADR-0378） |
| T-S-06 | max_tokens=0 がプロバイダへ 4096 として渡る／負数は INVALID_ARGUMENT |
| T-S-10 | 構造の門。`[Authorize(Policy = ServiceCaller)]` をリフレクションで固定 |
| T-S-14 | model="" / confidentiality="" / purpose="" の写し（決定 4） |

### 呼び出し元

| ID | 内容 |
| --- | --- |
| T-P1-01 | AiAnalysis: REST / gRPC の CompletionStreamEvent 列が一致（delta 列・done の model / tokens / stop_reason） |
| T-P1-02 | AiAnalysis: REST / gRPC の CompletionApiResponse が sent / refusal / 通常で一致 |
| T-P1-03 | 🔴 **最初の delta が done より前に到着する**（2 チャンクを時間差で出す偽プロバイダ） |
| T-P1-04 | 🔴 delta メッセージの sent が true（proto3 の既定の反転） |
| T-P1-05 | Graph: 同じ JSON 本文で両実装の提案が一致。sent=false / refusal で両者 `[]` |
| T-P1-08 | Conversion: success / egress-denied / llm-refused / not-codeable の 4 経路で両実装一致 |
| 既存 | RagOrchestratorStopReasonTests / RagOrchestratorDegradedModelTests / LlmGatewayDiagramCoderTests を**両実装**で回す |

### 変異検査（sent と既定値の写しが本当に固定されているか）

1. LlmGrpcMapping の delta 写像から `Sent = ev.Sent` を落とす（proto3 既定 false になる）→ **どの試験が赤になるか**
2. `ToDto(Pb.CompleteRequest)` の `0 → 4096` を落とす（max_tokens=0 がそのまま渡る）→ 同上

結果は PR 本文に載せる。**赤にならなければ試験が足りていない。**

## 計画書との差異

- 差異: **あり（1 点。計画の誤りではなく輸送の表現力の差）。**
  AiAnalysis GenerateAsync の REST 実装は「非 2xx → 出典のみ返す」と「接続失敗 → 例外が伝播する」を
  **別の枝**として持つ。gRPC には「非 2xx」に相当する概念が無く、到達失敗も応答の失敗も等しく
  RpcException になるため、**gRPC 実装は両方を「出典のみ」の枝へ落とす**。
  観測できる縮退（出典のみ返す）は一致し、**gRPC の側が REST より緩い方向**である
  （利用者に見える失敗が減る向きであり、越境・費用・認可のいずれの保証も弱めない）。
  IADR-0398 決定 5 に記録する。**計画リポジトリへの環流は不要**と判断した ——
  計画は輸送ごとの例外伝播を定めておらず、ADR-0029 の射程外である。

## 未決事項

- 稼働クラスタでの h2c 往復は**未実測**（新イメージの配備＝Pod 再起動を要する。`docs/api/east-west-grpc.md` §未決事項と同じ）。
- `OpenTelemetry.Instrumentation.GrpcNetClient`（CPM 追加を伴う）と gRPC ヘルスプロトコルの要否は #1255 やること 6。
- 参照実装（BFF → 認可）の配備上の未配線は**本 PR では触らない**（#1290 が意図して残した）。
- AST（AST#584）への「proto を公開した」通知は**本リポジトリからは起票しない**（ADR-0075 決定 4）。
