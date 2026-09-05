---
title: IADR-0397 east-west gRPC の展開（第 1 スライス: LlmGateway の埋め込み）— 判定器を 1 つに保ったまま gRPC 面を足し、proto3 に無い既定値をサーバ側で写し、輸送の失敗は縮退させず例外のまま上げる
type: impl-adr
status: Proposed
related_ids:
  - FR-02
  - FR-03
  - FR-05
  - NFR-09
  - NFR-16
  - ADR-0010
  - ADR-0013
  - ADR-0016
  - ADR-0017
  - ADR-0029
  - ADR-0030
  - ADR-0075
  - IADR-0117
  - IADR-0256
  - IADR-0313
  - IADR-0316
  - IADR-0379
author: claude
created: 2026-09-05
updated: 2026-09-05
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md §決定・2026-08-04 追記
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md 決定 1・3・4・5・6
  - planning:projects/microservices-platform/07_adr/ADR-0016_embedding-provider-voyage.md §決定（高機密はティアA・fail-closed）
  - planning:projects/microservices-platform/07_adr/ADR-0013_embedding-model.md（Embed ポート）
  - planning:projects/microservices-platform/07_adr/ADR-0017_selfhosted-embedding-ruri.md
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md（Grpc.* は採用済み）
  - planning:projects/microservices-platform/02_requirements/01_requirements.md FR-02 / FR-03 / FR-05 / NFR-09 / NFR-16
---

# IADR-0397: east-west gRPC の展開 — 第 1 スライス（LlmGateway の埋め込みと 2 呼び出し元）（#1255）

- 状態: Proposed
- 日付: 2026-09-05
- 決定者: claude（実装）

## 起点・関連

- 計画: `ADR-0029` §決定（east-west 同期は gRPC。proto の所有者は呼び出される側。例外は対象経路を明記した
  新 ADR に限る）と 2026-08-04 追記（`*.Client` を作らない・キャッシュ等は呼び出し元の Infrastructure）／
  `ADR-0075` 決定 1（基盤が現物を作る）・決定 3（一括移行の義務を緩めない）・決定 4（AST → MSP の
  `POST /complete` ×2 は MSP が proto を公開した時点で移る）・決定 5（IADR で REST 継続を自認しない）・
  決定 6（基盤先行は MSP 自身の移行を含む）／`ADR-0016`（埋め込みは高機密でティアA 固定・fail-closed）／
  `ADR-0013`・`ADR-0017`（埋め込みモデルとセルフホスト）／`ADR-0030`（`Grpc.*` は採用済みライブラリ）
- 実装 ADR: `IADR-0379`（先行条件の 4 決定。**本 IADR はその適用であり 1 つも変えない**）／
  `IADR-0256` 決定 3（設計上の縮退は続行・本当の故障は上げる）／`IADR-0316`（Secret 注入の宣言と配備の突合）／
  `IADR-0117`（ユニット外参照は `Shared/` の 3 プロジェクト）／`IADR-0313`（決定的ローカル埋め込み）
- 実装仕様書: `.ai-context/specs/20260905_issue-1255_east-west-grpc-llm-embedding.md`
- 実装ガイド（人が読む正）: `docs/api/east-west-grpc.md`
- issue: #1255（全 31 呼び出しの展開。本 IADR はその**第 1 スライス**）

## コンテキストと課題

参照実装（#1201 / `IADR-0379`）は 1 経路（BFF → 認可の権限スコープ解決）であり、残る 31 呼び出しへ写すときに
経路ごとの判断が要る。#1255 はそれを呼び出し先ごとのスライスへ切る。本 IADR が扱うのは**最初のスライス**、
LlmGateway の `POST /embed` と、それを呼ぶ RetrievalService / IngestionService の 2 呼び出し元である。

埋め込みを最初に切った理由は、**この経路が利用者の文脈を持たない**ことである（要求本文は
`text` / `confidentiality` / `purpose` のみ）。したがって `IADR-0379` 決定 4 の 🔴「利用者トークンを
メタデータへ載せない」と衝突する論点が無く、**s2s トークンを実際に配備へ配線する最初の PR**として最小である。

実測（`origin/develop` `6138a7ad`。`git rev-parse --is-shallow-repository` = `false`）:

- `/embed` の製品コード呼び出し元は **2**（Ingestion / Retrieval）。端点文字列・構成キー・ポート実装・
  契約 DTO の **4 軸すべてが同じ 2 つへ収束した**（走査と陽性対照は作業仕様書）。
- `git ls-files "*.proto"` = **1**（`authz_scope.proto` のみ）。
- 🔴 `ServiceToken__*` と `Services__AuthorizationServiceGrpc` は helm・compose に **1 件も無い**。
  **参照実装は配備上 1 度も走っていない。**
- 🔴 realm の service account で realm ロール `platform-service` を持つ主体は **0**。
  `platform-service` は `roles.realm` に**定義**されているが、`users[]` の service account 3 件のどれにも
  付いておらず、`bff` の service account は `users[]` に**存在しない**。`IADR-0379` 決定 4 の散文
  「realm の `bff` に service account と `platform-service` を付けてある」は **realm export の実体と
  一致していない**（`serviceAccountsEnabled: true` はあるが、ロール割当が無い）。
- `ServiceTokenOptions.ClientSecret` は `check-secret-injected-options.js` の宣言マーカを持たず、
  同検査器の母集合の**外**にあった。

決めるのは 5 点である。(1) REST と gRPC が同じ判定を通ることをどう構造で保証するか。
(2) proto3 に無い「未指定」をどう写すか。(3) 呼び出し元で輸送の失敗をどう扱うか。
(4) 切替と戻しをどこで行うか。(5) s2s の資格情報を配備へどう固定するか。

## 決定

### 決定 1: 判定器を 1 つに保つ —— `/embed` の本体を `EmbedUseCase` へ括り出し、REST と gRPC の両方がそれを呼ぶ

参照実装が REST と gRPC で `AbacEvaluator.ResolveScope` を共有したのと**同じ向き**である（`IADR-0379` 決定 5）。
越境判定（機密区分 × ティア）・プロバイダ解決・次元照合・上流不調の 4 つの縮退はすべて use-case の中に閉じ、
輸送（HTTP / gRPC）は写像だけを持つ。

🔴 **判定を輸送ごとに置くと、どちらか一方だけが `ADR-0016` の fail-closed を通るという最悪の食い違いが
起こり得る。** 埋め込みは**文書本文の全量**を送るため、その食い違いは越境ポリシーの破れとして現れる。
T-S-04（REST と gRPC が同じ入力に同じ答え）はこの構造の外形的な証明である。

縮退は gRPC でも `RpcException` にせず `embedded=false` の**応答**で返す（REST の 200 ＋ `Embedded=false` と
同値）。エラーにすると呼び出し側は「ポリシーが働いた」と「後段が壊れた」を区別できなくなる（T-S-11）。

### 決定 2: gRPC 面は `ServiceCaller` を要求する。REST `/embed` は現行のまま触らない

REST の `/embed` はサービス間呼び出し専用として認可を掛けていない（メッシュの mTLS が第一防御）。
gRPC の面では**呼び出し側サービス自身の資格情報**（client credentials の JWT・realm ロール
`platform-service`）を要求する。すなわち gRPC 面は**現状より強い**向きの変更であり、緩めていない。

🔴 **利用者のトークンでは通さない。** 管理者のトークンを転送しても `PERMISSION_DENIED` である（T-S-03）。
本経路は利用者の文脈を持たないので転送する動機自体が無いが、**面の性質は経路の都合で決めない** ——
1 つでも緩い面があると、後続スライスがそこを先例として引く。

### 決定 3: proto3 に null は無い。REST の DTO 既定はサーバ側で明示的に写し、同値試験で固定する

| 契約 | REST の既定 | proto3 の「未指定」 | 写し |
| --- | --- | --- | --- |
| `EmbedPurpose` | `Index`（`EmbedApiRequest.Purpose` の既定引数） | `EMBED_PURPOSE_UNSPECIFIED`（0） | 🔴 **`UNSPECIFIED → Index`**（`LlmGrpcMapping.ToDtoPurpose`） |
| `confidentiality` | `null` → restricted | `""` | 写し不要（`SensitivityClasses.Parse` が null / 空文字 / 未知をすべて restricted へ倒す。実測） |

これを独立の決定にしたのは、**REST の既定値が DTO の既定引数という目に見えにくい場所にあり、
gRPC で静かに変わり得る**からである。`EmbedPurpose` の写し漏れは例外にならない —— 未指定が `Query` として
扱われると越境判定が「public 相当」へ落ち、**機密文書の本文が外部経路（voyage）へ送られる**。
つまり **egress の違反**として現れる。

T-S-07 はこれを confidential ＋ 未指定で観測し、Query（陽性対照）と明示 Index（同値対照）を対に置く。
変異試験で写しを逆向きに壊すと T-S-07 だけが赤くなることを実測した（PR に出力を載せた）。

### 決定 4: 呼び出し元は**輸送の失敗を縮退させない**。`RpcException`（全 status）と s2s トークン取得失敗は例外のまま上げる

現行 REST 実装の `EnsureSuccessStatusCode` と**同じ判断**である（`IADR-0256` 決定 3
「故障を『該当なし』に化けさせない」）。

- Retrieval で空ベクトルへ倒すと、`HybridSearchService` は「意味検索の系統が使えない」と読んで
  **0 件を返す** —— ゲートウェイの故障が静かに「該当なし」になる。
- Ingestion で `Retryable=true` へ倒すと、**ゲートウェイの故障と機密区分による送信拒否が同じ形**になり
  区別できなくなる。

続行してよいのは、**後段が「使えない」と応答で明示したとき**（`embedded=false`）だけである。これは
`ADR-0016` の fail-closed が正常に働いた状態であって故障ではない。

🔴 REST 実装が持つ「応答が null（本文欠落）→ `Retryable=true`」の枝は gRPC 側に**持たない**。
proto の応答メッセージは欠落し得ず（不達は `RpcException` になる）、起こり得ないケースへの防御的実装をしない。

### 決定 5: 切替は `Services:LlmGatewayGrpc` の有無。並走中の正は REST。呼び出し元は兄弟クラスで足す

`IADR-0379` 決定 5 を踏襲する。`AddLlmGatewayGrpcClient(config)` は当該構成が在るときだけ生成クライアントを
登録し、無ければ**何も登録しない**。呼び出し元の `Program.cs` は登録の有無で gRPC 実装と HTTP 実装を選ぶ。
**既存の REST クラスは 1 文字も変えない** —— 戻すのは構成を外すだけで済ませるためである。

写像（`LlmGrpcMapping`）は `Platform.Shared.Infrastructure/Foundation/Llm/` に置き、**写像だけ**を置く
（`ADR-0029` 2026-08-04 追記。キャッシュ・タイムアウト・リトライ・fail-safe は呼び出し元の Infrastructure）。
呼び出し元ごとに縮退の落とし先が違う（Retrieval は空ベクトル、Ingestion は `Retryable` 経由で再試行）ので、
ここへ寄せると 1 つの縮退規則をすべての呼び出し元へ押しつけることになる。

🔴 チャネルは `GrpcChannel` 型で DI へ**登録しない**。`AddAuthzScopeGrpcClient` が同じ型で別アドレスの
チャネルを登録するため、両方を構成したサービス（後続スライスの AiAnalysis 等）で片方のクライアントが
もう片方の宛先へ繋がる。宛先ごとのチャネルはクライアントの登録に閉じる。

### 決定 6: `ServiceTokenOptions.ClientSecret` へ Secret 注入の宣言を足し、本スライスの 2 呼び出し元で配線を完成させる

`check-secret-injected-options.js`（`IADR-0316` / #1107）の宣言マーカを doc コメントへ足す。
これにより `ServiceToken__ClientSecret` が helm に `secretKeyRef` 由来の env として、compose に変数展開の
env として存在することが**機械で要求される**。

足す判断の根拠:

1. 同検査器の突合は**リポジトリ全体で 1 度**である（`computeViolations` は集合を見る。サービス単位ではない）。
   本 PR は 2 サービスにそれを配線するので、**その場で満たせる**。BFF を含む他のバインド先を本 PR で
   配線する義務は生じない。
2. 宣言を足さないまま配線すると、`BffSessionOptions` が踏んだ穴（#1107）が `ServiceToken` について再発しうる。
   **実際、参照実装はすでにその穴の中に居る**（上の実測）。宣言はその穴を機械で塞ぐ唯一の手段である。
3. 🔴 **単体テストは構成を自分で与えて走るので、この欠落では絶対に落ちない。** #1107 と同型である。

配線の射程は helm（`services.<name>.serviceToken` ブロック）・compose・realm（confidential client と
service account の realm ロール）に加え、**ローカル配備の供給元一式**である ——
`k8s-local-up.sh` の手動 apply（`ESO != 1`）、ESO の ExternalSecret マニフェスト、Vault の seed、
同期待ちと rollout。helm が**非 optional** な `secretKeyRef` で読む以上、供給元が 1 つでも欠けると
Pod は `CreateContainerConfigError` で起動しない（#1012 / #1022 が踏んだ形）。

## 理由

- 決定 1 は「同じ判定を通ること」を試験の約束ではなく**構造**で得る。試験（T-S-04）は構造が保たれている
  ことの外形的な確認であって、構造そのものの代わりではない。
- 決定 3 を独立させたのは、写し漏れが**例外にならない**からである。落ちるものは直る。静かに向きが変わる
  ものは、それを名指しする試験が無い限り誰も気づかない。
- 決定 4 は `IADR-0256` 決定 3 の再確認である。同 IADR は REST の経路について「200 ＋ 空へ一律に潰さない」を
  決めており、**輸送が変わっても同じ線が要る**。輸送の変更は縮退の意味論を変える機会ではない。
- 決定 6 で宣言を足すのは、`CLAUDE.md`「検査器の追加は同型の事故が 2 回起きたら」の例外ではない ——
  検査器は既に在り、**母集合へ入れるだけ**である。

## 結果

- 良い影響:
  - proto 1 本（`platform/llmgateway/v1/embedding.proto`）で 2 呼び出し元が gRPC で呼べる。
    `check-proto-contracts.js` は R1〜R4 合格・**非破壊の file 追加 1 件**。
  - **s2s の資格情報が初めて配備に載った**（helm / compose / realm / ローカル供給元の 4 経路）。
    参照実装が持っていなかったものである。
  - `ServiceToken__ClientSecret` が `check-secret-injected-options.js` の母集合に入り、以後の
    呼び出し元追加で注入漏れが機械で止まる。
- 悪い影響・トレードオフ:
  - Keycloak の confidential client が 2 つ増え、ローカル配備の Secret 供給元が 2 本増えた
    （手動 apply・ExternalSecret・Vault seed・同期待ち・rollout の 5 箇所）。
  - `EmbeddingEndpoints` のラムダ本体が `EmbedUseCase` へ移った（REST の挙動は不変。既存試験が緑のまま）。
  - 🔴 **稼働クラスタでの h2c 往復は依然として未実測**（Pod の再起動を要する。`IADR-0379` §結果 と同じ制約）。
    実 Kestrel の往復（T-S-01 / T-S-12）で代替した。
  - 🔴 **参照実装（BFF → AuthorizationService）の未配線は本 PR では直していない**（下記フォローアップ 1）。
- フォローアップ:
  1. 🔴 参照実装の配備上の未配線: `bff` の service account が `users[]` に無く `platform-service` を持たない、
     `Services__AuthorizationServiceGrpc` と `ServiceToken__*` が BFF に配線されていない。
     **認可スライスの PR でまとめて埋める**（本スライスで触ると、埋め込みの受け入れ証跡と認可の配線が
     同じ PR に混ざる）。
  2. `completion.proto`（`Complete` / `CompleteStream`）と 4 呼び出し箇所（AiAnalysis ×2・Graph・Conversion）。
     server-streaming と TTFT 計器（`IADR-0354`）の論点を含む。
  3. `user_directory.proto` と認可サービスの 2 呼び出し元（DataSource・McpServer）—— 利用者トークン転送の
     置き換え。決定 3 相当の判断が別途要る。
  4. AST への通知（`ADR-0075` 決定 4）は **`POST /complete` の proto が着地する次の PR**で行う
     （AST が追随する対象は補完であり、本スライスには含まれない。本リポジトリからは起票しない）。
  5. `OpenTelemetry.Instrumentation.GrpcNetClient`（CPM 追加）と gRPC ヘルスの要否（#1255 やること 6）。

## 関連

- Supersedes: なし（`IADR-0379` の 4 決定は不変。本 IADR はその適用）
- Superseded by: なし
