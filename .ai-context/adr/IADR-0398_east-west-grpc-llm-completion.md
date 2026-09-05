---
title: IADR-0398 east-west gRPC の第 2 スライス（LlmGateway のテキスト生成）— 逐次生成はサーバストリーミングで初回トークンの境界を保ち、縮退は輸送の失敗にせず呼び出し元ごとの現行の枝へ落とす
type: impl-adr
status: Proposed
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
  - IADR-0394
  - IADR-0397
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md §決定・2026-08-04 追記
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md 決定 3・4・5・6
  - planning:projects/microservices-platform/07_adr/ADR-0076_slo-evaluation-target-and-metric-units.md 決定 4・5
  - planning:projects/microservices-platform/07_adr/ADR-0044_llm-usage-metrics-and-pricing-table.md 決定 1・3
  - planning:projects/microservices-platform/07_adr/ADR-0038_analysis-purpose-drop-fable-5.md 決定 3・4
  - planning:projects/microservices-platform/07_adr/ADR-0010_llm-gateway.md
  - planning:projects/microservices-platform/07_adr/ADR-0012_conversion-pipeline.md
---

# IADR-0398: east-west gRPC の第 2 スライス — LlmGateway のテキスト生成（#1255）

- 状態: Proposed
- 日付: 2026-09-05
- 決定者: claude（実装）

## 起点・関連

- 計画: `ADR-0029` §決定（east-west 同期は gRPC。例外は対象経路を明記した新 ADR に限る）／
  `ADR-0075` 決定 3（一括移行の義務は緩めない）・決定 4（AST → MSP の `POST /complete` ×2 は MSP が proto を
  公開した時点で移る）・決定 5（IADR で REST 継続を自認しない）・決定 6（基盤先行は MSP 自身の移行を含む）／
  `ADR-0076` 決定 4（合成トラフィックの標識と除外）・**決定 5（初回トークンまでの時間を測る。応答完了 p95 へ
  改める案 i を明示的に却下した）**／`ADR-0044` 決定 1・3（用途別・モデル別の費用）／
  `ADR-0038` 決定 3・4（フォールバック鎖の射程）／`ADR-0010`・`ADR-0012`・`ADR-0025`
- 実装 ADR: `IADR-0379`（先行条件の 4 決定。**本 IADR はこれを変えない**）／
  `IADR-0397`（第 1 スライス＝埋め込み。**本 IADR はその鎖を延ばす**）／`IADR-0037`（SSE の 1 イベント契約）／
  `IADR-0101`（`MaxTokens` の既定 4096）／`IADR-0104`（`StopReason`）／`IADR-0110`（計器の値域）／
  `IADR-0111`（モデル名を偽らない）／`IADR-0212` 決定 3（`sawDone` のときだけ計上）／
  `IADR-0225`（ストリーム経路はフォールバックを持たない）／`IADR-0256` 決定 3（故障を「該当なし」に化けさせない）／
  `IADR-0266` 決定 6（縮退した応答を根拠に使わない）／`IADR-0354`（初回トークン計器の両端）／
  `IADR-0374`（上流ステータスの軸）／`IADR-0378`（合成標識の 2 段）／`IADR-0394`（Meter の probe はインスタンスで絞る）
- 実装ガイド（人が読む正）: `docs/api/east-west-grpc.md`（本 IADR で 3 つ目の面を追記した）
- 作業仕様書: `.ai-context/specs/20260905_issue-1255_east-west-grpc-llm-completion.md`
- issue: #1255（本 PR は第 2 スライス。第 1 スライスは #1290）

## コンテキストと課題

第 1 スライス（`IADR-0397`）は `/embed` で gRPC 面の形を確立した。本スライスは同じ LlmGateway の残り半分、
すなわち `POST /complete`・`POST /complete/stream` と、それを呼ぶ 3 サービス 4 箇所を移す。

埋め込みと違って本スライスに固有の論点は 2 つある。

1. **`/complete/stream` は SSE である。** gRPC には unary と server-streaming があり、
   `NFR-02` の SLI は**初回トークン**で測る。輸送の選び方が SLI の意味を変え得る。
2. **縮退の向きが埋め込みと逆である。** 埋め込みの呼び出し元は輸送の失敗を例外のまま上げる
   （`IADR-0397` 決定 4）。生成の REST 実装は上げない —— 500 を伝播させず `Sent=false` /
   `done(Sent=false)` を返している。移行の不変条件は「挙動を変えない」であり、**ここを取り違えると
   現在は縮退表示になる場面が north-south の 500 になる。**

実測（`origin/develop` `42d12ec4`。`git rev-parse --is-shallow-repository` = `false`）:
呼び出し元は **3 クラス / 4 箇所**（AiAnalysis の `/complete/stream` と `/complete`、Graph、Conversion）。
4 つの独立した軸（端点の文字列・要求 DTO の構築・HTTP クライアント登録・ストリーム契約の消費）が
同じ集合を指した。母集合の導出は作業仕様書 §母集合の再導出にある。

## 決定

### 決定 1: `/complete/stream` は **server-streaming rpc**（`LlmCompletion/CompleteStream`）へ写す。unary へ潰さない

初回トークンの境界は「最初の `delta` メッセージの**到着**」であり、SSE の「最初の `data:` 行」と同じ位置にある。

`rag.answer.first_token.duration`（`IADR-0354` 決定 2）の**両端は AiAnalysis の north-south 応答にあり**、
AiAnalysis ↔ LlmGateway の輸送とは独立に定義されている。したがって計器は変えず、
**LlmGateway 側に TTFT 計器は足さない**（SLI の担い手を 2 つにしない）。

🔴 unary に潰すと、最初の `delta` が生成完了後にしか届かず、AiAnalysis が最初の `token` を書く時刻が
≒ 生成完了時刻になる。`RagFirstTokenP95High` は**応答完了 p95**を測ることになり、`ADR-0076` 決定 5 が
明示的に却下した「長い回答ほど SLO 違反になり、回答品質を上げると SLO が悪化する」形へ戻る。

bidi streaming は過剰である（要求は単一メッセージであり、「要求は最初の応答の前に確定している」という
現行の性質が型から消える）。「`/complete/stream` だけ REST に残す」は `ADR-0075` 決定 5 により採れない。

🔴 **サーバが早く書くことは gRPC の保証ではなくコードの性質である。** gRPC が保証するのは順序だけで、
「サーバが溜めずに書く」ことは保証しない。したがって
`GrpcCompleteStreamTests.First_delta_arrives_before_done` が唯一の防護であり、判定は**到着時刻の差**で行う
（絶対時刻だと遅い機械で偽陽性が出るが、溜めていれば差がほぼ 0 になるので差なら機械の速さに依らない）。

### 決定 2: 判定器は 1 つ（`CompletionUseCase`）。REST と gRPC が同じ本体を呼ぶ

`Complete/Endpoint.cs` と `CompleteStream/Endpoint.cs` の本体を `Features/Completions/CompletionUseCase.cs` へ
括り出した（#1290 が `EmbedUseCase` を括り出したのと同型。`IADR-0397` 決定 1 の適用）。
同じ `ILlmRouter.Route`・同じ `LlmCompletionMetrics` / `LlmUsageMetrics`・同じフォールバック鎖・
同じ `LogStopReason` を通る。**第 2 のルータ経路を作らない** —— 分けると
「どちらか一方だけが越境判定を通る」という最悪の食い違いが起こり得る。

`isSynthetic` は**引数で渡す**。判定そのものは `SyntheticTraffic.IsSyntheticInternalRequest(HttpRequest)` が
単一情報源であり、REST は `http.Request`、gRPC は `context.GetHttpContext().Request` から呼ぶ。

### 決定 3: メタデータ ＝ 運搬の出所、本文 ＝ 要求の意味（`IADR-0397` 決定 4 の踏襲）

s2s トークン（`authorization`）・トレース文脈（`traceparent` / `tracestate`。既存の
`AddHttpClientInstrumentation` / `AddAspNetCoreInstrumentation` が扱う）・合成標識
（`x-synthetic-traffic`。`IADR-0378` 内周）は**メタデータ**。
`purpose` / `model` / `confidentiality`（`ADR-0044` の帰属軸＝ルーティング入力）は**本文**。

🔴 **合成標識を本文に置かない。** 標識は要求の意味ではなく出所であり、本文に `bool synthetic` を置くと
**すべての rpc の不変契約に番号つきで残り**、呼び出し元が「試験のため」に立てる典型的な誤用の口になる。
送る側は `SyntheticTraffic.PropagateTo(Metadata, bool)` の**多重定義**（同じクラス）を使う。

🔴 **`purpose` を proto の enum にしない。** 値域を閉じるのは設定（`PurposeModels`）と計器
（`IADR-0110`。未定義値を `other` へ丸める）であって契約ではない —— enum にすると値域の正が
設定から不変契約へ移り、モデル・用途の追加が契約変更になる。

### 決定 4: proto3 に null は無い。REST の既定値はサーバ側で明示的に写し、同値試験で固定する

| 契約 | REST の既定 | proto3 の「未指定」 | サーバの写し |
| --- | --- | --- | --- |
| `max_tokens` | 4096（`IADR-0101`） | `0` | **`0 → 4096`**。負数は `INVALID_ARGUMENT` |
| `model` | `null` → ルータが用途で選ぶ | `""` | 写し不要（`LlmRouter` が `IsNullOrWhiteSpace` で判定。実測） |
| `confidentiality` | `null` → restricted | `""` | 写し不要（`SensitivityClasses.Parse`。実測） |
| `purpose` | `null`/空白 → `"default"` | `""` | 写し不要（`CompletionUseCase`。実測） |
| `sent` | DTO 既定 **`true`** | **`false`** | 🔴 **向きが逆**。delta メッセージにも明示的に `sent=true` を書く |

🔴 **`sent` の写し漏れは例外にならない。** 全 delta が「縮退」に見える形で静かに壊れ、
AiAnalysis は縮退表示、Graph は提案 0 件、Conversion は全図が画像保持へ倒れる。
負数を `INVALID_ARGUMENT` にするのは REST に無い検証だが、0 が「未指定」を担う以上 0 未満は意味を持たず、
黙って 4096 へ倒すと「送った値と違う上限で課金された」ことに呼び出し元が気付けないからである。

### 決定 5: 縮退は**呼び出し元ごとに**現行の枝へ落とす。生成の縮退は `RpcException` にしない

🔴 **埋め込み（`IADR-0397` 決定 4）とは向きが逆である。**

| 呼び出し元 | REST の現行の枝 | gRPC の写し |
| --- | --- | --- |
| AiAnalysis `StreamCompletionAsync` | 送信失敗・非 2xx → `done(Sent=false, "LLM が現在利用できません。")` ／ 読み取り中断 → `done(Sent=false, "LLM 応答の受信に失敗しました。")` | 呼び出し**確立**の `RpcException`・s2s トークン取得失敗 → 前者。**受信途中**の `RpcException` → 後者 |
| AiAnalysis `GenerateAsync` | 非 2xx → 出典のみ。**接続失敗は例外が伝播する** | `RpcException`（全 status）・トークン取得失敗 → 非 2xx と同じ枝（出典のみ）。§理由の 🔴 を参照 |
| Graph `ProposeAsync` | 非 2xx・`HttpRequestException` / `TaskCanceledException` → `[]` | `RpcException`・トークン取得失敗 → `[]` |
| Conversion `CodeAsync` | `EnsureSuccessStatusCode` の例外・接続失敗 → `Retain("llm-call-failed")` | `RpcException`・トークン取得失敗 → `Retain("llm-call-failed")`（**理由文字列も同じ**） |

ゲートウェイ側の縮退（越境拒否・プロバイダ未登録・上流不調）は**すべて応答**である
（`sent=false` の `CompleteResponse` ／ `done=true, sent=false` の `CompletionStreamEvent`）。
`RpcException` になるのは s2s の面（`UNAUTHENTICATED` / `PERMISSION_DENIED`）・輸送不達（`UNAVAILABLE`）・
`max_tokens` 負数（`INVALID_ARGUMENT`）だけである。

🔴 **1 点だけ REST と厳密には一致しない（意図的・記録済み）。**
`GenerateAsync` の REST 実装は「非 2xx → 出典のみ」と「接続失敗 → 例外が伝播」を**別の枝**として持つが、
gRPC には「非 2xx」に相当する概念が無く、到達失敗も応答の失敗も等しく `RpcException` になる。
したがって gRPC 実装は両方を「出典のみ」の枝へ落とす。**観測できる縮退は一致し、gRPC の側が緩い方向**
（利用者に見える失敗が減る向き）であり、越境・費用・認可のいずれの保証も弱めない。
計画は輸送ごとの例外伝播を定めておらず `ADR-0029` の射程外であるため、計画への環流はしない。

### 決定 6: 切替は `Services:LlmGatewayGrpc` の有無。並走中の正は REST

各呼び出し元の `Program.cs` が構成の有無で実装を選ぶ。**戻すのは構成を外すだけ**（コードは変えない）。

AiAnalysis だけは兄弟クラスではなく**ポート**（`ILlmCompletionTransport`）を置いた ——
`RagOrchestrator` は検索・ABAC・出典・機密区分の算出という業務判断を大量に持ち、
兄弟クラスにするとそれらが 2 か所へ複製されるからである。ポートが返すのは
「ゲートウェイが何と答えたか」だけであり、合成監視の抑止・回答文の選択は `RagOrchestrator` に残る。
🔴 ポートの `CompleteAsync` は **3 値**（`LlmCompletionOutcome`）を返す —— REST 実装が持つ
「到達できなかった」「答えたが本文を復元できなかった」「答えた」の 3 枝を潰さないためである
（真ん中は gRPC では起こり得ないが、**REST 実装が現に持っている**ので型から消さない）。

## 理由

- 決定 1 は `NFR-02` の測り方から導かれる。計器の両端が輸送の外にあることを確かめたうえで、
  境界を保つ形（server-streaming）を採った。
- 決定 4 を独立の決定にしたのは、REST の既定値が **DTO の既定引数という目に見えにくい場所**にあり、
  gRPC で静かに変わり得るからである（`sent` は向きまで逆）。
- 決定 5 が最も事故りやすい。**同じ #1255 の中で、埋め込みは例外を上げ、生成は上げない。**
  分ける基準は「REST の現行がどうしているか」ただ 1 つであり、
  埋め込みは `EnsureSuccessStatusCode` で上げ、生成は 500 を伝播させない。
  移行の不変条件は「挙動を変えない」であって「輸送ごとに一貫させる」ではない。

## 結果

- 良い影響:
  - proto 1 本（`completion.proto`）で 3 呼び出し元 4 箇所が gRPC で呼べる。
    LlmGateway の gRPC 面は埋め込みと生成の **2 面**になった。
  - `ADR-0075` 決定 4 の条件（MSP が `POST /complete` の proto を公開する）が満たされた。
    AST（`AST#584`）はこれに追随できる —— **本リポジトリからは起票しない**。
  - REST と gRPC の同値が、既存の `RagOrchestratorStopReasonTests` /
    `RagOrchestratorDegradedModelTests` を**両輸送で回す**ことで測れるようになった
    （元データは 1 つで、`TestLlmTransports` が両輸送へ載せ替える）。
- 悪い影響・トレードオフ:
  - Keycloak の confidential client が 3 つ増え、Secret 注入が 3 サービスに要る（helm / compose /
    ExternalSecret / Vault seed の 4 経路）。
  - LlmGateway の gRPC テストは **`SharedMeterCollection` へ入れざるを得ない**。
    h2c ポートがプロセスで 1 つであること（器を共有する必要）と、補完テストが共有 Meter へ発行すること
    （直列化の必要）が同じコレクションを要求する。**実 Kestrel の試験が直列化される分だけ遅くなる。**
  - 🔴 `GenerateAsync` の「接続失敗」の扱いが REST と厳密には一致しない（決定 5 の 🔴）。
  - 🔴 稼働クラスタでの h2c 往復は依然として**未実測**（#1255 やること 7。`IADR-0397` と同じ）。
- 変異検査（本 PR で実施。詳細は PR 本文）:
  - `CompletionStreamEvent` の `Sent` 写しを落とす → LlmGateway の 3 試験が赤。
  - `CompleteResponse` の `Sent` 写しを落とす → LlmGateway 5 ＋ Conversion 6 ＋ Graph 2 が赤。
  - `max_tokens` の `0 → 4096` を落とす → LlmGateway の 1 試験が赤。
  - 🔴 **変異検査が試験の穴を 2 つ見つけた。** Graph の偽クライアントが proto を手で組んでいたため
    写像の欠陥を検出できず、共通写像を通す形へ改めた。AiAnalysis の偽クライアントは
    **送る側と受ける側の両方に同じ写像を使う**ため対称な写像欠陥を原理的に検出できない ——
    それを検出できるのは REST（独立経路）と突き合わせる LlmGateway 側の試験だけである。
- 移していないもの（作業仕様書 §対象範囲・対象外が正）:
  `user_directory.proto` と `/authz/scope` の 5 呼び出し元（次のスライス）／REST の並走
  （`IADR-0379` 決定 5・`ADR-0075` 決定 4）／BFF（参照実装）の配備上の未配線（#1290 が意図して残した）／
  AiAnalysis → Retrieval `/search` の利用者トークン転送（呼び出し先が利用者の権限で動くため
  読み口を狭める形では解けない。次の壁）／ストリーム経路のフォールバック鎖（`IADR-0225` の射程外）／
  `OpenTelemetry.Instrumentation.GrpcNetClient` と gRPC ヘルスプロトコル。
- フォローアップ:
  1. 認可サービスの名簿（`UserDirectory`）と `/authz/scope` の 5 呼び出し元。
  2. AiAnalysis → Retrieval / Graph → Document / Retrieval → Graph の利用者トークン転送 3 箇所は
     ホップごと ABAC（`ADR-0034` 方式 A）であり、token exchange の要否を計画へ諮る必要がある。
  3. `AST#584` は MSP の proto 公開に追随できる（本リポジトリからは起票しない）。
  4. compose の conversion-service は `Services__LlmGateway` を持たず、appsettings 既定
     （`:5007`）が compose の llm-gateway（8080）と合っていない。**REST 側は本 PR で触っていない**（別 issue）。

## 関連

- Supersedes: なし（`IADR-0379` の 4 決定・`IADR-0397` の 6 決定はいずれも不変。本 IADR はその適用と拡張）
- Superseded by: なし
