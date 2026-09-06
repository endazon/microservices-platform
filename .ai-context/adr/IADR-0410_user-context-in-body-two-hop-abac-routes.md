---
title: IADR-0410 east-west gRPC の第 6 スライス（利用者の権限で動く 2 経路）— 利用者文脈を本文で運び、判定の位置は動かさない。ロールは ABAC 属性へ混ぜない
type: impl-adr
status: Proposed
related_ids:
  - FR-04
  - FR-05
  - FR-17
  - FR-18
  - NFR-09
  - NFR-16
  - UC-10
  - SC-03
  - SC-05
  - SC-09
  - SC-18
  - ADR-0004
  - ADR-0029
  - ADR-0034
  - ADR-0035
  - ADR-0036
  - ADR-0049
  - ADR-0063
  - ADR-0065
  - ADR-0075
  - ADR-0080
  - ADR-0086
  - IADR-0044
  - IADR-0139
  - IADR-0242
  - IADR-0272
  - IADR-0335
  - IADR-0364
  - IADR-0371
  - IADR-0379
  - IADR-0395
  - IADR-0397
  - IADR-0400
  - IADR-0401
  - IADR-0402
  - IADR-0408
author: claude
created: 2026-09-07
updated: 2026-09-07
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0086_user-context-in-body-not-token-exchange.md 決定 1〜5
  - planning:projects/microservices-platform/07_adr/ADR-0034_graph-traversal-abac-enforcement.md 決定 1・2
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md §決定・2026-08-04 追記
  - planning:projects/microservices-platform/07_adr/ADR-0075_east-west-grpc-migration-order.md 決定 3・5・6
  - planning:projects/microservices-platform/07_adr/ADR-0063_tag-suggestion-reflection-and-approval-authz.md 決定 1〜3
  - planning:projects/microservices-platform/06_technical/07_abac-attribute-model.md §利用者属性・§ポリシー評価モデル
---

# IADR-0410: east-west gRPC の第 6 スライス — 利用者の権限で動く 2 経路（#1255）

- 状態: Proposed
- 日付: 2026-09-07
- 決定者: claude（実装）

## 起点・関連

- 計画: `ADR-0086`（**本 IADR はその適用である**）—— 決定 1（利用者文脈を本文で運ぶ）・決定 2
  （token exchange は今は採らない）・決定 3（対象は 2 経路。`AiAnalysis → Retrieval` は中継）・
  決定 4（呼び出し元の主張をそのまま評価する構造は改めない）・決定 5（例外 ADR を起こさず
  `ADR-0075` の一括移行の射程内で移す）／`ADR-0034` 決定 1・2（ホップごと ABAC・存在秘匿）／
  `ADR-0063` 決定 1〜3（タグ提案の承認の認可）／`ADR-0029`・`ADR-0075`（east-west は gRPC・一括移行）
- 実装 ADR: [[IADR-0379]]（先行条件の 4 決定。**本 IADR はこれを変えない**）／
  [[IADR-0397]]・[[IADR-0400]]・[[IADR-0401]]・[[IADR-0402]]・[[IADR-0408]]（先行 5 スライス）／
  [[IADR-0044]]（最終防衛線）／[[IADR-0242]]（ホップごと ABAC の実装）／[[IADR-0272]] 決定 4（`action` の明示）／
  [[IADR-0335]] 決定 4（未認証の短絡）／[[IADR-0364]]（タグ反映の経路）／[[IADR-0395]]（検証と認可の順序）
- 作業仕様書: `20260907_issue-1255_user-context-in-body`

## 文脈

`ADR-0086` は 2026-09-07 に、**利用者の権限で動く east-west を「利用者文脈を本文で運ぶ」形へ揃える**
と裁定した。対象は 2 経路である。

| 経路 | 呼び出し先が転送トークンから何を取るか（**自分で実測した**） |
| --- | --- |
| `RetrievalService → GraphService`（近傍展開） | `/graph/{id}/neighbors` は `RequireAuthorization()`（`Neighbors/Endpoint.cs:114`）を持ち、`GraphAccessResolver` が `ctx.User.Identity.Name` と claim `clearance` / `department` を取って `AuthzScope/Resolve` へ**本文で**渡す。**主体を読む** |
| 同上（辺の型の重み） | `/graph/edge-types/catalog` は `RequireAuthorization()`（`Catalog/Endpoint.cs:22`）を持つが、ハンドラの引数は `GraphDbContext` と `CancellationToken` だけである。🔴 **主体を 1 バイトも読まない** —— 転送トークンは**認証の門にしか使われていない** |
| `GraphService → DocumentService`（タグ反映） | `POST /documents/{id}/tags` は `http.User.Identity?.Name`（`AddTag/Endpoint.cs:58`）で所有者束縛、`http.User.IsInRole(platform-admin)`（`:60`）で管理者経路を判定する。**主体と realm ロールの両方を読む** |

🔴 **`AiAnalysis → Retrieval` は本 PR で触らない**（`ADR-0086` 決定 3 が中継と裁定し、
フォローアップ 1 が「移行後に落とす」と定めた）。本 PR で落とすと、`Services:GraphServiceGrpc` が
未設定の構成（並走中の既定）では REST 経路が資格情報を失い、**全ホップ 404** になる。

## 決定

### 決定 1: 利用者文脈は `user_id` / `user_attributes` / `action` を本文で運ぶ。判定の位置は動かさない

- 2 つの proto（`knowledge.graph.v1.GraphNeighbors` / `knowledge.document.v1.DocumentTagWrite`）は
  利用者文脈を要求本文で受け取る。**メタデータに載るのは呼び出し元サービス自身の s2s トークンだけ**である。
- 🔴 **呼び出し先は受け取った文脈で自分の判定を行う。** GraphService は `AuthzScope/Resolve` を
  自分で呼んでスコープを解決し、DocumentService は所有者束縛とロールを自分で再判定する
  （[[IADR-0044]] の最終防衛線）。**`ADR-0034` 決定 1 のホップごと ABAC は満たされている。**
- 🔴 **判定結果（スコープ）を運ぶ形は採らない**（`ADR-0086` の 2026-09-07 追記が同じ理由で退けた）——
  受け取った scope をそのまま信じる口を開くと、そこへ到達できる誰もが任意の scope を主張できる。
- **新しい形ではない。** `platform.authz.v1.AuthzScope/Resolve` が既にこの形であり、本スライスは
  その射程を「ホップごと ABAC の呼び出し先」へ及ぼしただけである。

### 決定 2: 🔴 realm ロールは `user_attributes` へ混ぜず、専用の `user_roles` で運ぶ

タグ反映の認可は「①所有者の動的束縛 **または** ②`platform-admin`」の選言であり（`ADR-0063` 決定 3）、
②は ABAC ではなくロール判定である。**②のためにロールを運ぶ必要があるが、`user_attributes` へ入れない。**

- 計画 `07_abac-attribute-model` §利用者属性 の 2026-09-05 追記が
  「**`roles` は現時点で ABAC の文書単位判定には用いていない。ロール判定は ABAC とは別の経路である**」
  （`ADR-0080` 決定 4）と明記している。
- 実装側も `UserAttributeEncoding.SetValuedKeys` を **`tags` / `projects` の 2 つだけ**とし、
  `roles` を**含めないこと**を試験（`UserAttributeEncodingTests.集合値キーはタグと参加プロジェクトだけである`）で
  固定している。`roles` をカンマ連結で属性マップへ載せると、この線上表現の規則と食い違う。
- 🔴 **混ぜると 2 つの判定経路が 1 つの地図の上で溶ける。** 分けておけば、将来 ABAC 側に
  `roles` 条件が入るかどうかを計画が決めるまで、実装が先に既成事実を作らずに済む。

### 決定 3: 面は「呼び出し元が実際に要る問い」まで狭める。近傍展開と辺の重みを 2 rpc に割る

- `ExpandNeighbors` は**辺だけ**を返す（`id` / 両端点 / 型 ID）。ノード・総数・打ち切りは出さない ——
  近傍展開は辺しか読まず、**辺だけを入口にすれば未承認の AI 提案が構造的に根拠へ混ざり得ない**。
- `ListEdgeTypeWeights` は `edge_type_id` と `weight` だけを返し、**利用者文脈を持たない**
  （呼び出し先が主体を読まないため）。`ServiceCaller` が現行の「認証済みであること」の門を
  **より狭く**置き換える（[[IADR-0401]] 決定 1 / [[IADR-0402]] 決定 3 と同じ向き）。
- 🔴 **書き込み（辺の作成）・AI 提案・辺の型の管理はこの面に出さない**（[[IADR-0401]] 決定 2 の作法）。
- 🔴 タグ反映は **`document_read.proto` へ足さず別ファイル**にする。同 proto は
  「書き込み・本文・共有の口はこの面に存在しない」と宣言しており、混ぜると宣言が嘘になる。

### 決定 4: 本体を REST と共有する（判定器を 2 つにしない）

`ExpandNeighborsUseCase` と `AddDocumentTagUseCase` を新設し、REST の端点と gRPC の rpc が
**同じ関数**を通るようにした（先行スライスの `EmbedUseCase` / `DocumentReadUseCase` と同じ形）。

- **検証（`IValidator<T>`）だけは輸送の縁に残す** —— 器（ProblemDetails か gRPC status か）が
  輸送ごとに違うためであり、**規則そのものは 1 つ**である。順序（検証が認可より前）は本体が持つ。
- `IGraphAccessResolver` に `ResolveForUserAsync(GraphUserContext, action, ct)` を足し、
  既存の `ResolveAsync(HttpContext, …)` はそれへ委譲する。
  🔴 **`ResolveAsync` の多重定義にしない** —— `GraphTypeGateArchitectureTests` が
  `GetMethod(nameof(ResolveAsync))` で引いており、多重定義は `AmbiguousMatchException` で
  「既定値の不在」を固定する試験を落とす。

### 決定 5: 縮退の枝を 1 つも増やさず・1 つも減らさない

| 事象 | REST（現行） | gRPC（本スライス） |
| --- | --- | --- |
| 近傍展開: 資格情報が無い | 呼ばずに警告 → 空 | **同じ**（`IsAuthenticated` を見て呼ばない） |
| 近傍展開: 起点が見えない・無い | 404 → `[]`（警告しない） | `found=false` → `[]`（警告しない） |
| 近傍展開: 非 2xx・不達・トークン取得失敗 | 警告 → `[]` | `RpcException` ほか → 警告 → `[]` |
| 辺の重み: 引けない | 警告 → 全辺フォールバック重み | **同じ** |
| タグ反映: 2xx / 400 / 404 | `Applied` / `UnknownTag` / `NotWritable` | `APPLIED` / `UNKNOWN_TAG` / `NOT_WRITABLE` |
| タグ反映: その他の非 2xx・不達・トークン取得失敗 | LogError → `Unavailable` | `RpcException` ほか → LogError → `Unavailable` |
| タグ反映: 承認者が分からない | 呼んで後段が匿名 404 → `NotWritable` | 🔴 **呼ばずに `NotWritable`**（下記） |

🔴 **タグ反映の「承認者が分からない」だけは副作用が 1 つ減る**（doomed な往復をしない）。
**値は同じ `NotWritable` である。** gRPC で空の `user_id` を送ると後段は `INVALID_ARGUMENT` を返し、
それは `Unavailable` へ落ちる —— **値が変わってしまう**ので手前で同じ値へ倒した。
承認の口は `RequireAuthorization()` の後ろにあり、要求の外から呼ばれる経路は無いので、
この枝は多層防御である。

🔴 **故障を「該当なし」に化けさせない。** 利用者文脈の欠落は**要求の誤り**（`INVALID_ARGUMENT`）で
あって deny ではない —— deny へ畳むと、呼び出し元の配線誤りが「グラフには何も無い」
「承認できない文書だった」に化けて気付けなくなる。

### 決定 6: `user_attributes` に載るのは現行と同じ 2 属性のままとする（広げない）

`clearance` / `department` の 2 つだけを運ぶ。**現行と同じ**である（下記「実測」）。

- 🔴 **広げるのは本スライスの射程ではない。** `ADR-0086` §残るもの が
  「`user_attributes` に何を載せるかを定めていない…**運ばれない属性は判定に効かない**」と記録し、
  フォローアップ 2 で「実装 → 計画へ報告する」と定めた。**報告は PR で行う。**
- **本スライスは経路の形を変えるだけであり、属性の搬送範囲を同時に変えると
  「どちらの変更で挙動が変わったか」が切り分けられなくなる。**

## 実測（`user_attributes` に実際に載る属性）

`grep -rn 'FindFirst("clearance")' --include=*.cs src/`（非テスト・非 bin）で引いた **4 箇所**。

| 抽出箇所 | 載せる属性 | 数 |
| --- | --- | --- |
| `Platform.Shared.Infrastructure/Foundation/Authz/BffScopeResolver.cs:67-69` | clearance / department | 2 |
| `GraphService/.../GraphAccessResolver.cs`（本 PR で `GraphUserContext` へ移した） | clearance / department | 2 |
| `WikiService/.../WikiAccessResolver.cs:76-78` | clearance / department | 2 |
| `AiAnalysisService/Features/Analysis/AnalysisEndpoints.cs:53-55` | clearance / department | 2 |

**4 箇所すべてが 2 属性である。プラットフォーム全体で 2 であり、GraphService が絞っているのではない。**

計画が定める利用者属性は **5 つ**（`roles` / `department` / `clearance` / `projects` / `tags`）。
**運ばれていないのは `projects` / `tags` の 2 つ**である（`roles` は ABAC 判定に用いない旨が明記されており欠落ではない）。

🔴 **`AbacEvaluator.MatchesUserConditions`（`AbacEvaluator.cs:72-78`）はキーを固定していない** ——
`userAttrs.TryGetValue(key, …)` が失敗した条件は**マッチしない**。すなわち
**`tags` / `projects` を利用者条件に持つポリシーは、配備されても 1 度もマッチしない。**
倒れる向きは deny（fail-closed）なので情報は漏れないが、
**SC-17 で割り当てたタグは文書単位の判定に一切効かない。**
これは `ADR-0086` フォローアップ 2 の報告事項であり、**本 PR では直さない。**

## 理由

- **決定 1 を「新設」と書かないのは、既に動いている参照実装（`AuthzScope/Resolve`）との関係が
  読めなくなるからである。** `ADR-0086` §理由 も同じことを述べている。
- **決定 2 を決定として書くのは、`user_attributes` へ入れるほうが一見素直だからである。**
  1 つのマップに入れると呼び出し先の実装は短くなるが、**線上表現の規則（集合値キーは 2 つだけ）と
  計画の「ロール判定は ABAC とは別の経路」の両方に反する。** 短さのために境界を溶かさない。
- **決定 3 は [[IADR-0401]] 決定 2 の作法の再適用である。** 呼び出し元が要らないものを面へ出すと、
  出した瞬間にそれが誰かの入口になる。
- **決定 5 の表を書くのは、移行の不変条件が「挙動を変えない」だからである**（[[IADR-0400]] と同じ）。
  🔴 **1 つだけ副作用が減る枝があることを、表で明示的に認めた** —— 黙って揃えたことにすると、
  次に読む者が「全部同じ」と信じて別の枝も削る。

## 結果

- **良い影響**: 🔴 **利用者の JWT が east-west の面から 2 経路ぶん消えた**（confused deputy の
  成立余地が 2 つ減った）。当該経路が `ADR-0075` の一括移行へ乗り、**例外 ADR を起こさずに済んだ**
  （`ADR-0086` 決定 5）。REST と gRPC が同じ本体を通るので、判定の写し違いが構造的に起こらない。
- **悪い影響 / トレードオフ**: 🔴 **中継サービスが正直であることへの依存が 1 段増える。**
  現行はホップの直前まで検証済みトークンに支えられていたが、決定 1 では呼び出し元の主張になる。
  **`ADR-0086` §結果 が受け入れたトレードオフである** —— その先はいずれにせよ主張であり
  （`AuthzScope/Resolve` は従前から主張を評価している）、**エスカレーション経路の到達点は変わらない。
  変わるのは「どこから主張になるか」が 1 段早まることだけである。**

### 残るもの

- 🔴 **`ADR-0086` 決定 4 の構造は残る。** 呼び出し元が任意の `user_id` / `user_attributes` を
  主張でき、認可サービスはそれを評価する。**本スライスはこれを閉じていない**（計画側の別裁定）。
- 🔴 **`/search` が本文の `scope` をそのまま信じる構造も残る**（#1318 欠陥 B / planning#564）。
  **別の裁定待ちであり、本 PR は触っていない。**
- 🔴 **`AiAnalysis → Retrieval` の利用者トークン転送は残っている**（`ADR-0086` フォローアップ 1）。
  経路 2 が gRPC へ切り替わった段で落とす。
- **並走中の正は REST である。** 切替は `Services:GraphServiceGrpc` / `Services:DocumentServiceGrpc` の
  有無だけであり、戻すのは構成を外すだけでよい。**REST の端点は 1 つも消していない。**
- **稼働 k3s での h2c 往復は本 PR では実測していない**（#1255 やること 7 の残件と同じ扱い）。

## フォローアップ

1. **実装**: 経路 2 が gRPC で安定した段で、`RagOrchestrator` の Retrieval へのトークン転送を落とす
   （`ADR-0086` フォローアップ 1 / #1255 残作業 2）。
2. **実装 → 計画**: `user_attributes` の搬送範囲（現行 2 / 計画 5）を報告する
   （`ADR-0086` フォローアップ 2）。**本 PR の本文で行う。**
3. **計画**: `ADR-0086` 決定 4 の信頼モデル（呼び出し元の主張）は計画側の `decision-needed` である。
   **実装側で先に閉じない。**

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連 ADR: `ADR-0086`（**本 IADR はその適用である**）・`ADR-0034` 決定 1・2・`ADR-0063` 決定 1〜3・
  `ADR-0029`・`ADR-0075` 決定 3・5・6・`ADR-0080` 決定 4
- 関連 IADR: [[IADR-0379]]・[[IADR-0397]]・[[IADR-0400]]・[[IADR-0401]]・[[IADR-0402]]・[[IADR-0408]]・
  [[IADR-0044]]・[[IADR-0242]]・[[IADR-0272]]・[[IADR-0335]]・[[IADR-0364]]・[[IADR-0395]]

## 変更履歴

| 日付 | 変更 | 根拠 |
| --- | --- | --- |
| 2026-09-07 | 起案した | 計画 `ADR-0086` 決定 1・3・5（#1255 残作業 1） |
