---
title: IADR-0462 実効構成の収集（introspection）は扇形のまま gRPC へ移す —— 面は共通基盤が REST と対で全サービスに張り、呼び出し側は宛先ごとに opt-in する
type: impl-adr
status: Accepted
related_ids:
  - FR-15
  - FR-16
  - NFR-09
  - NFR-16
  - ADR-0018
  - ADR-0024
  - ADR-0029
  - ADR-0075
  - ADR-0089
  - IADR-0017
  - IADR-0026
  - IADR-0029
  - IADR-0117
  - IADR-0379
  - IADR-0397
  - IADR-0419
  - IADR-0458
  - IADR-0465
author: claude
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md §決定・2026-08-04 追記（該当する REST の east-west 同期呼び出しはすべて gRPC へ移行する）
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md 決定 2〜6
  - planning:projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md（自己申告と宣言の突合・ドリフト検出）
  - planning:projects/microservices-platform/07_adr/ADR-0089_east-west-completion-rule-and-authz-service-face.md 決定 1
---

# IADR-0462: 実効構成の収集を扇形のまま gRPC へ移す（#1514 / #1255 経路 ⑤）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-26
- 決定者: endazon（方針のオーナー裁定 2026-09-25・#1255）／ Claude Code（実装の形の起案）

## 起点・関連

- 起点 issue: #1514（スライス 1）／親 #1255（経路 ④⑤）。後続スライス #1515（④ ツール申告の収集）・#1516（④ ツール実行。判断待ち）・#1517（④⑤ の REST 退役）
- オーナー裁定（2026-09-25・#1255）: **④⑤（MCP のツール・introspection の、全サービスへ配る経路）は、配り先のすべてのサービスに gRPC の口を実装して移す。** BFF → サービスの 15 本は対象外（[[IADR-0458]]）
- 前提: [[IADR-0379]]（置き場・versioning・h2c・s2s・並走の正）／[[IADR-0419]] 決定 1（扇形は宛先の側が面を実装しないと 1 経路も移らない）／[[IADR-0029]]（到達不能と適用漏れを混ぜない）
- 作業仕様書: `.ai-context/specs/20260926_1514_introspection-grpc-fanout.md`

## コンテキストと課題

構成情報 API（BFF 同居）は、`Introspection:Services:<名前>` で開く扇形の宛先（13）へ `GET /internal/introspection` を REST で送り、
得た自己申告を宣言（pipeline.json）と突合してドリフトを検出する。自己申告の受け口は 14 サービスが持つ（共通基盤の
`AddPlatformIntrospection` / `MapPlatformIntrospection`）。これまでの 11 経路と違い、**呼び出し元は 1 つ・宛先は構成で開く**。

扇形を移すときに決めることは 4 つある。

1. **面をどこに置くか** —— 14 サービスの Program.cs へ個別に足すか、共通基盤に 1 つ置くか
2. **切替の単位** —— 経路全体を 1 つのスイッチで切り替えるか、宛先ごとか
3. **失敗の畳み方** —— gRPC の status をどう収集器の 2 値（申告を得た / 得られなかった）へ落とすか
4. **受け口の前提が欠けるサービス** —— h2c リスナを持たない 6 サービスと、認証を持たない 2 サービス（変換・取り込み）

## 検討した選択肢

### 1. 面の置き場

- **1-A: 共通基盤に 1 つ。`MapPlatformIntrospection` が REST と gRPC を対で張る**（採用）
- 1-B: 各サービスの Program.cs に `MapGrpcService` を 1 行ずつ足す —— 張り忘れたサービスだけが REST のまま残り、
  **呼び出し側からは到達不能としか見えない**（収集器は失敗を到達不能へ隔離し、ドリフト検出は Info に留める）。
  14 か所に同じ 1 行を置く利点が無い

### 2. 切替の単位

- **2-A: 宛先ごとの opt-in（`Introspection:GrpcServices:<名前>` = h2c アドレス）**（採用）
- 2-B: 経路全体を 1 キーで切り替える（先行 11 経路の `Services:<Name>Grpc` と同じ形）—— 1 対 1 の経路では正しいが、
  扇形では **gRPC 面をまだ持たない宛先が 1 つでもある間は切り替えられず、切り替えた瞬間にその宛先だけが恒久的に到達不能になる**。
  AST 等の外部ユニットが同じ収集先へ加わるときも、面の実装を全宛先で揃えるまで待つことになる

### 3. 失敗の畳み方

- **3-A: REST と同じ 2 値へ畳み、ログだけを status で分ける**（採用）
- 3-B: [[IADR-0419]] の送出のように結末を 3 値へ分ける —— 収集器の出力はドリフト検出の入力であり、その値域は
  「申告を得た / 得られなかった」の 2 値で閉じている（[[IADR-0029]]）。値域を広げると検出側の改修が要り、
  **移行の不変条件（挙動を変えない）を破る**

## 決定

1. **proto** `platform.introspection.v1.ServiceIntrospection/Get` を `Platform.Shared.Contracts/Protos/platform/introspection/v1/` に置く。
   所有者は**特定のサービスではなく基盤**（全サービスへ配る面）であり、knowledge のサービスも同じ proto から受け口を得る
   （knowledge → platform の Shared/ 参照は許可。[[IADR-0117]]）。原則 A: DTO で null を取り得るのは `PortSelectionDto.Target` だけで、
   `optional string` で運ぶ。enum は持たない。**空の `service` は申告として無効**とし、呼び出し側で到達不能へ落とす（REST の空応答と同じ枝）
2. **受け口**（決定 1-A）: `IntrospectionGrpcService` は DI の同じ `ServiceIntrospectionDto` を写すだけで、REST と同じ 1 つの申告を返す。
   `[Authorize(Policy = ServiceCaller)]`。REST の受け口は認証を持たない（メッシュ内部限定）ので**この面は現状より狭い**。REST 側は変えない。
   `AddPlatformIntrospection` が `AddGrpc` を、`MapPlatformIntrospection` が `MapGrpcService` を呼ぶ
3. **宛先の前提**: 収集先のうち h2c リスナを持たない 5 サービス（aianalysis / datasource / feedback / ingestion / wiki）に
   `AddPlatformGrpcListener`、helm `grpcPort: 8081`、compose `Grpc__Port` と `expose`。認証を持たない ingestion に
   `AddPlatformAuth` を足す（無いと面への要求は毎回「AddAuthorization が無い」例外で落ちる。ミドルウェアは登録があれば WebApplication が
   自動で挟むので `Use*` は足さない）。既存の REST 端点は認可を要求しないので挙動は変わらない。
   🔴 **conversion は本決定では配線しない（REST のまま）。** conversion も認証を持たないが、その認証は planning#651 の裁定
   （BFF が中継する利用者の資格情報を後段が自ら検証する。conversion だけが満たしていない）による**別作業が実装する**。
   本決定で `AddPlatformAuth` を先に足すとその作業と重なるので触らない。共通基盤が張る gRPC 面は conversion にも在るが、
   認可の登録が無いので **fail-closed**（どの要求も成功しない。匿名で申告が読めることは無い）であり、BFF の gRPC 宛先にも入れない。
   認証が着地した段で、h2c リスナ・`grpcPort`・gRPC 宛先を足す（配線の試験は保留一覧から外すだけで赤から緑へ移る形にしてある）。
   ［2026-09-26 追記 / #1520］**conversion の認証は着地した**（[IADR-0465](./IADR-0465_conversion-service-validates-relayed-user-credential.md)）。gRPC 面は `ServiceCaller` を判定する
   （s2s 無し → `UNAUTHENTICATED`、利用者のトークン → `PERMISSION_DENIED`、`platform-service` → 申告）。conversion の
   `IntrospectionEndpointTests` の fail-closed の試験は他サービスと同じ形へ書き換えた。**配線（フォローアップ 3）は未着手のまま**であり、
   保留一覧の理由だけを改めた。本文は書き換えていない。
   **mcp-server は収集先に無い**ので h2c リスナも `grpcPort` も足さない（面は共通基盤が張る）。収集先へ加えるのは FR-15 の挙動変更であり本決定の外
4. **呼び出し側**（決定 2-A・3-A）: `EffectiveConfigCollector` が新しい `IEffectiveConfigCollector` になる。宛先 = `Services` と `GrpcServices` の
   キーの和。`GrpcServices` に空でないアドレスが在れば gRPC、無ければ REST（両方に在れば gRPC）。集約は REST だけの収集と同じ 1 つ。
   - **期限**: REST のタイムアウトと同じ `Introspection:TimeoutSeconds` を引く（値を書き写さない）
   - **リトライ**: 持たない（REST も持たない）。定期検出の次の周期が再試行である
   - **失敗**: 全 status・s2s トークン取得失敗・期限切れ・空の申告を到達不能へ隔離する。`UNAUTHENTICATED` / `PERMISSION_DENIED` と
     **s2s トークンの取得失敗**（`ServiceToken:ClientId` の注入漏れ・IdP の拒否など）は**配線不備**（再起動で直らない）として Error、
     それ以外は Warning。取得失敗は CallCredentials の中で起き、gRPC クライアントが包み直すので型では見分けられない ——
     発行側を包んで取得失敗に印を付け、例外の連鎖（`InnerException` と `Status.DebugException`）から印を探す（#1524 の監査指摘）
   - **取り消し**: 呼び出し側の ct による取り消しだけを `OperationCanceledException` で外へ出す（REST と同じ。#1382）
   - **登録**: `AddPlatformConfigInspection` は `GrpcServices` が構成されたときだけ s2s トークンの発行側と gRPC の収集器を登録する
     （無い配備は資格情報を要求しない）。構成されているのに収集器が無ければ**起動時に落とす**（黙って REST へ倒すと、REST の退役の段で初めて露見する）
5. **配備**: helm・compose の BFF に収集先 13 のうち conversion を除く 12 の gRPC 宛先を入れる。資格情報は既存の `bff` client（`platform-service` 付き）を使い、
   realm・Secret は増やさない。**並走中の正は REST**（[[IADR-0379]] 決定 5）—— 戻すのは宛先ごとに gRPC の行を消すだけ

## 理由

- **決定 1・2** は「扇形は宛先の側が面を持たないと 1 経路も移らない」（[[IADR-0419]] 決定 1）への構造の答えである。
  面を共通基盤に 1 つ置けば**実装する宛先の数は 1**になり、宛先ごとの opt-in にすれば**移行の単位も宛先 1 つ**になる。
  オーナー裁定の「配り先のすべてのサービスに gRPC の口を実装する」を、14 か所の手作業ではなく 1 か所の実装で満たす
- **決定 3** の認証追加は、面の `ServiceCaller` が成り立つための前提であり、REST の挙動を変えない（試験で固定）
- **決定 4** の 2 値は [[IADR-0029]] の値域をそのまま保つ。ログを status で分けるのは、REST には無かった失敗の種類
  （s2s の配線不備）を一過性の到達不能に紛れさせないためである
- **決定 5** で先行経路と同じく配備で gRPC を有効にしておくのは、並走の期間に gRPC 側の配線不備を実配備で見つけるためである

## 結果

- 良い影響: 経路 ⑤ の宛先 13 がすべて gRPC 面を持ち、conversion を除く 12 が配備上 gRPC で収集される。④（MCP のツール申告）も同じ形（面を配る側が 1 つ、
  呼び出し側は宛先ごと opt-in）で写せる（#1515）
- 悪い影響・トレードオフ:
  - 5 サービスの Service が複数ポートになり、ポートに名前が付く（`grpcPort` の既知の帰結。[[IADR-0379]] 決定 3）
  - ingestion が JwtBearer を持つ（要求に Bearer が付いたときだけ IdP のメタデータを引く。付かない要求は従来どおり素通し）
  - conversion は認証の別作業が着地するまで REST のまま残る（経路 ⑤ はこの 1 宛先ぶん移り切らない）
  - 稼働 k3s での h2c 往復は**未実測**（Pod の再構築を要する）。ループバックの実 Kestrel 往復で代替した
- フォローアップ:
  1. #1515（④-a）・#1516（④-b。判断待ち）・#1517（REST 退役。#1255 残射程 2 と同じ段）
  2. mcp-server を収集先へ加えるか（FR-15 の挙動変更。本決定の外）
  3. conversion の gRPC 収集の配線（planning#651 の裁定による conversion の認証が着地した後）

## 関連

- [[IADR-0379]] / [[IADR-0419]] / [[IADR-0029]] / [[IADR-0458]]
- 通信仕様書: `docs/api/east-west-grpc.md`（12 つ目の面）
