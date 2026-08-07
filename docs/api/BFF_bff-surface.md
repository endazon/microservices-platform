---
title: BFF 境界（/bff/*）通信仕様書
type: api-spec
status: in-progress
related_ids: [SC-01, SC-02, SC-03, SC-05, SC-06, SC-07, SC-08, SC-09, SC-10, SC-11, UC-01, UC-02, UC-03, UC-04, UC-05, UC-06, FR-01, FR-03, FR-04, FR-06, FR-08, FR-09, FR-10, FR-12, FR-15, ADR-0031, IADR-0009, IADR-0121, IADR-0131, IADR-0132, IADR-0135, IADR-0136]
author: Claude
created: 2026-08-05
updated: 2026-08-07
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
related_specs:
  - ../adr/IADR-0135_generated-client-adoption-and-cache-keys.md
  - ../adr/IADR-0131_openapi-as-bff-contract-source.md
  - ../adr/IADR-0132_openapi-required-from-csharp-nullability.md
  - ../adr/IADR-0121_spa-stack-migration-staging.md
  - ../specs/20260805_issue-506_openapi-bff-groups.md
  - ../specs/20260805_issue-520_openapi-response-required.md
  - ../specs/20260805_issue-519_orval-hook-migration.md
  - ../specs/20260806_issue-538_next-sync-at.md
  - ../adr/IADR-0136_next-sync-at-from-worker-cadence.md
---

# 通信仕様書: BFF 境界（`/bff/*`）

> 個々のエンドポイントの要求・応答・ステータスは **[`openapi.yaml`](openapi.yaml) を正**とする。
> 本書はその上位にある**境界の規約**（誰が何をどう呼ぶか／何が生成対象で何が対象外か）を定める。
> 決定の根拠は [[IADR-0131]] を参照。

> **`status: in-progress` の理由と、いま残っているもの**（`docs/README.md` の語彙: `draft` =
> 着手前・記述途中／`in-progress` = 実装中）。BFF 境界は #506（PR #518）で全 27 パスが契約に載り、
> #520 で応答スキーマの `required` が確定し、**#519 で SPA 側の載せ替えが完了した**。
> **着手前でも記述途中でもないので `draft` は外す。** 一方、次の 1 点が未了なので `completed` でもない。
>
> 1. **`/bff/feedback`・`/bff/feedback/stats` の端点認可が未裁定である**（**#521**。§未決事項 3）。
>    **［2026-08-07 追記 / #586］裁定は下りた。未了なのは実装である。** 計画 FR-08 が
>    **無認証 401 / 権限外 403（統計は運用者・管理者のみ）**を確定した（planning `3e58b97` = PR planning#244〔裁定依頼 planning#236〕）。
>    **是正は #521 が持つ**（挙動の変更を伴う）。
>
> **［2026-08-05 追記］#519 で載せ替えが済み、`apiFetch` を使う画面は 0 になった**
> （残る `foundation/api` 直接利用は **SSE の `apiStream` だけ**である。[[IADR-0135]]）。

## 起点となる計画書（トレーサビリティ）

- 関連機能要求（FR）: FR-01 / FR-03 / FR-04 / FR-06 / FR-08 / FR-09 / FR-10 / FR-12 / FR-15
- 関連ユースケース（UC）: UC-01〜UC-06
- 技術検討 / ADR:
  [13_frontend-stack](../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md)（基本方針）／
  [ADR-0031](../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md)（Accepted）

## 概要

- **プロトコル**: REST / JSON（＋ SSE が 1 本）。すべて `/bff/` 接頭辞の下に置く。
- **SPA からの到達経路は 2 つだけである**（ADR-0031 / [[IADR-0121]] 決定 3）。
  1. **orval 生成フック**（`foundation/api/generated/*`）——既定。
  2. **`foundation/api` の `apiStream`**——**生成できない面（SSE）だけ**。
     **［2026-08-05 追記］#519 の載せ替え後、画面が `apiFetch` を使う箇所は無い。**
  - **手書きの HTTP クライアントは禁止**。`foundation/api` 以外での `fetch` / `XMLHttpRequest` /
    `EventSource` と `axios` 等の import は ESLint が error にする。
- **接続先はビルドに焼き込まない**。実行時 config（`platform/frontend/public/config.js` の `bffBaseUrl`。
  既定 `/bff`）を `apiClient` が前置する。**生成コードも `orvalMutator` 経由で必ずここを通る。**
- **フロントから各サービスを直接叩かない。** `openapi.yaml` は BFF とサービス直接 API を 1 ファイルに
  束ねているが、生成の入力からは `orval-bff-only.cjs` が `/bff/` 以外を落とす。

## 生成対象と対象外（**「載せ忘れ」と「意図的な除外」の区別**）

**この節が本書の主眼である。** OpenAPI に載っていないパスには生成フックが無く、載っていない理由が
「まだ書いていない」なのか「書けない」なのかは、`openapi.yaml` を見ただけでは分からない。

| 面 | OpenAPI に載る | orval が生成する | SPA の呼び方 |
| --- | --- | --- | --- |
| `/bff/` 配下の JSON API | **はい** | **はい** | 生成フック |
| **`POST /bff/analysis/ask/stream`（SSE）** | **はい** | **いいえ（意図的）** | **`apiStream`** |
| `POST /bff/internal/config/drift-run` | **いいえ（意図的）** | いいえ | **呼ばない**（メッシュ内部限定） |
| `/bff/` 以外（`/documents`・`/authz/*`・`/dashboard/*` ほか） | はい（**参照用**） | **いいえ** | **呼ばない**（BFF 境界） |

### SSE を生成対象外にする理由と、その機械的な担保

`POST /bff/analysis/ask/stream` は `text/event-stream` を逐次中継する（`AnalysisBffEndpoints.cs`。
`WithName("BffAnalysisAskStream")`）。orval / TanStack Query のフックは**1 回で完結する応答**を前提と
するため、SSE を扱えない。

**問題は「扱えない」ことではなく、orval が黙って諦めずに“動きそうに見える”フックを作ってしまうこと**
である（実測: 宣言をそのまま渡すと `useBffAnalysisAskStream` が生成され、mutator が本文を全部読んでから
`JSON.parse` するためストリーミングにならず SSE 本文で例外になる）。**罠を置かない**ため、
**`orval-bff-only.cjs` が「SSE の応答を持ち JSON の応答を持たない操作」を生成の入力から落とす**。

この規則は宣言ではなく**成果物で固定されている**——生成物はコミットされ、CI が
`pnpm run codegen && git diff --exit-code` を検査する。除外が壊れれば `useBffAnalysisAskStream` が
生成物へ現れ、差分検査が落ちる。

### `POST /bff/internal/config/drift-run` を載せない理由

`ExcludeFromDescription()` が付いており、**メッシュ内部限定**（ingress へ公開しない。ClusterIP ＋
NetworkPolicy / mTLS が防御）で ArgoCD の PostSync フックが叩く。応答は 202 のみで構成情報を返さない
（存在秘匿・[[IADR-0009]]）。**SPA から呼ぶ面ではない**ため契約に載せない。

## エンドポイント一覧

**`operationId` は例外なく C# の `WithName` のケバブケースである**（#519 で既存 2 本
〔`analysis-ask` / `analysis-analyze`〕を規約へ揃えた。[[IADR-0131]] 決定 3 の改定＝[[IADR-0135]] 決定 5）。

**認可の列は BFF 実装の実測である。** 「404（秘匿）」は「不在」と「権限外」を区別しないことを指す
（[[IADR-0009]]）。詳細（要求・応答スキーマ・全ステータス）は [`openapi.yaml`](openapi.yaml) を正とする。

| メソッド | パス | 認可 | 関連 FR/UC/SC | 生成される関数 |
| --- | --- | --- | --- | --- |
| POST | `/bff/search` | **端点認可なし**（結果は ABAC で絞る） | FR-03 / UC-01 / SC-02 | `useBffSearch`（**mutation**。SC-02 は操作関数 `bffSearch` を `useQuery` に据える。[[IADR-0135]] 決定 2） |
| POST | `/bff/analysis/ask` | 同上 | FR-04 / UC-01 / SC-01 | `useBffAnalysisAsk`（**画面は呼んでいない**） |
| POST | `/bff/analysis/ask/stream` | 同上 | FR-04 / UC-01 / SC-01 | **無し（SSE。`apiStream`）** |
| POST | `/bff/analysis/analyze` | 同上 | FR-07 / UC-02 / SC-08 | `useBffAnalysisAnalyze` |
| POST | `/bff/feedback` | **端点認可なし**（**計画は認証必須＝無認証 401。是正は #521**。下記注） | FR-08 / UC-01 / SC-01 | `useBffSubmitFeedback` |
| GET | `/bff/feedback/stats` | **端点認可なし**（**計画は運用者・管理者のみ＝権限外 403。是正は #521**。下記注） | FR-08 / SC-10 | `useBffFeedbackStats` |
| GET | `/bff/dashboard/summary` | **admin** | FR-10 / UC-05 / SC-10 | `useBffDashboardSummary` |
| GET | `/bff/documents` | **端点認可なし**（ABAC で絞る） | FR-06 / SC-05 | `useBffDocumentList` |
| POST | `/bff/documents` | **admin / operator** | FR-06 / UC-03 / SC-05 | `useBffDocumentCreate` |
| GET | `/bff/documents/{id}` | **端点認可なし**（ABAC ＋ 404 秘匿） | FR-06 / SC-03 | `useBffDocumentDetail` |
| PUT | `/bff/documents/{id}` | **admin / operator** | FR-06 / UC-03 / SC-05 | `useBffDocumentUpdate` |
| DELETE | `/bff/documents/{id}` | **admin / operator** | FR-06 / UC-03 / SC-05 | `useBffDocumentDelete` |
| GET | `/bff/documents/{id}/content` | **端点認可なし**（ABAC ＋ 404 秘匿） | FR-06 / SC-03 | `useBffDocumentContent` |
| GET | `/bff/documents/{id}/versions` | **端点認可なし**（ABAC ＋ 404 秘匿） | FR-06 / UC-03 / SC-03 | `useBffDocumentVersions` |
| POST | `/bff/documents/{id}/publish` | **admin / operator** | FR-06 / UC-03 / SC-05 | `useBffDocumentPublish` |
| POST | `/bff/documents/{id}/archive` | **admin / operator** | FR-06 / UC-03 / SC-05 | `useBffDocumentArchive` |
| GET | `/bff/datasources` | **admin / operator** | FR-01 / SC-06 | `useBffDataSourceList` |
| POST | `/bff/datasources` | **admin / operator** | FR-01 / UC-04 / SC-06 | `useBffDataSourceCreate` |
| GET | `/bff/datasources/{id}` | **admin / operator** | FR-01 / SC-06 | `useBffDataSourceGet` |
| DELETE | `/bff/datasources/{id}` | **admin / operator** | FR-01 / SC-06 | `useBffDataSourceDelete` |
| POST | `/bff/datasources/{id}/sync` | **admin / operator** | FR-01 / UC-04 / SC-06 | `useBffDataSourceSync` |
| GET | `/bff/conversion/jobs` | **admin / operator** | FR-12 / UC-06 / SC-07 | `useBffConversionJobList` |
| GET | `/bff/conversion/jobs/{id}` | **admin / operator** | FR-12 / SC-07 | `useBffConversionJobGet` |
| POST | `/bff/conversion/jobs/{id}/retry` | **admin のみ** | FR-12 / UC-06 / SC-07 | `useBffConversionJobRetry` |
| GET | `/bff/admin/authz/policies` | **admin のみ** | FR-09 / UC-05 / SC-09 | `useBffAuthzListPolicies` |
| POST | `/bff/admin/authz/policies` | **admin のみ** | FR-09 / UC-05 / SC-09 | `useBffAuthzCreatePolicy` |
| GET | `/bff/admin/authz/policies/{id}` | **admin のみ** | FR-09 / SC-09 | `useBffAuthzGetPolicy` |
| PUT | `/bff/admin/authz/policies/{id}` | **admin のみ** | FR-09 / UC-05 / SC-09 | `useBffAuthzUpdatePolicy` |
| DELETE | `/bff/admin/authz/policies/{id}` | **admin のみ** | FR-09 / SC-09 | `useBffAuthzDeletePolicy` |
| PATCH | `/bff/admin/authz/policies/{id}/active` | **admin のみ** | FR-09 / SC-09 | `useBffAuthzSetPolicyActive` |
| GET | `/bff/admin/authz/attributes` | **admin のみ** | FR-09 / UC-05 / SC-09 | `useBffAuthzListAttributes` |
| POST | `/bff/admin/authz/attributes` | **admin のみ** | FR-09 / UC-05 / SC-09 | `useBffAuthzCreateAttribute` |
| GET | `/bff/admin/authz/attributes/{id}` | **admin のみ** | FR-09 / SC-09 | `useBffAuthzGetAttribute` |
| PUT | `/bff/admin/authz/attributes/{id}` | **admin のみ** | FR-09 / UC-05 / SC-09 | `useBffAuthzUpdateAttribute` |
| DELETE | `/bff/admin/authz/attributes/{id}` | **admin のみ** | FR-09 / SC-09 | `useBffAuthzDeleteAttribute` |
| GET | `/bff/admin/config` | **ConfigViewer**（非権限は 404） | FR-15 / SC-11 | `useBffConfigEffective` |
| GET | `/bff/admin/config/drift` | 同上 | FR-15 / SC-11 | `useBffConfigDrift` |
| GET | `/bff/admin/config/history` | 同上 | FR-15 / SC-11 | `useBffConfigHistory` |

> **［2026-08-07 追記 / #586］`/bff/feedback` 系の 2 行は計画と食い違う。**
> planning `3e58b97`（PR planning#244。裁定依頼 planning#236 の反映）で計画 FR-08 に
> **「投稿には認証を要する（匿名投稿は許さない）／統計は運用者・管理者に限って参照できる」**が確定し、
> 受け入れ基準として **無認証で 401 / 認証済みでも権限外は 403** が置かれた。
> **上表の「端点認可なし」は BFF 実装の実測であり、その意味では正しい**ため書き換えていない。
> **是正（`RequireAuthorization` の追加とテスト）は #521 が持つ**——認可の変更は挙動の変更であり、
> 独立した PR とテストが要る（#586 は planning pin の更新と事実の追随に限る）。
> 同型の記述: [機能仕様書 FR-08](../functional/FR-08_answer-feedback.md)・[[IADR-0010]]・§未決事項 3。

## 横断の規約

### 1. 認可の失敗をどう見せるか（**面ごとに違う。揃えない**）

| 面 | 未認証 | 権限不足 | 理由 |
| --- | --- | --- | --- |
| `/bff/admin/config` 群 | **404** | **404** | **存在秘匿**（[[IADR-0009]]）。`RequireAuthorization` を**使わず**ハンドラ内で `ConfigViewer` を評価する——付けると無認証が 404 到達前に 401 で短絡して存在が漏れる |
| `/bff/documents/{id}`（読み） | **404** | **404** | 同上。スコープ外と不在を区別しない。無認証は `userId="anonymous"` として ABAC スコープ解決へ渡り、許可が無ければ null＝404 になる |
| その他の管理系 | 401 | 403 | 管理 API の存在自体は秘匿しない（画面側が `RequireRole` で導線を隠す） |

> **「端点認可なし」の意味**（一覧表）: `RequireAuthorization` を**付けていない**——`/bff/search`・
> `/bff/analysis/*`・`/bff/feedback*`・`/bff/documents` の読みがこれに当たる（実測: BFF に
> fallback policy も無い）。無認証でもハンドラに到達する。
>
> - `/bff/search`・`/bff/analysis/*`・`/bff/documents` の読み: 資格情報は **ABAC スコープ解決の入力**に
>   なる（`BffScopeResolver`。無認証は `userId="anonymous"`）。許可ポリシーが無ければ deny-by-default で
>   null＝空応答または 404 になる。**「認証不要」ではなく「認証の有無を認可の入力として扱う」設計である。**
> - `/bff/feedback`・`/bff/feedback/stats`: ABAC を通らない。送信者は後段が JWT から特定し、
>   **開発・テスト環境では `anonymous`** として記録される（IADR-0010 の二重計上防止はこの ID で行う）。
>   **BFF 自身は無認証の投稿を拒まない**——この面をどこで塞ぐか（エッジか BFF か）は本書では未確認である
>   （§未決事項 3）。

**SPA 側はこの差を吸収する**——403 と 404 をどちらも同じ中立表示へ寄せ、5xx とは区別して
`role="alert"` を出す（[[IADR-0129]] 決定 3）。「権限が無い」と「存在しない」を利用者に見分けさせない。

### 2. エラー本文の形が 3 種類ある（**統一されていない。実装がそうなっている**）

| 形 | 使う面 | 例 |
| --- | --- | --- |
| **RFC7807 `ValidationProblemDetails`** | 保存前検証の 400 | `/bff/admin/authz/*` の登録・更新／`/bff/documents` の作成・更新 |
| **RFC7807 `ProblemDetails`** | 参照中削除の 409 | `DELETE /bff/admin/authz/attributes/{id}`（`detail` に参照元ポリシー名） |
| **素の JSON** | 楽観ロック・状態遷移の 409 | `PUT /bff/documents/{id}`（`version_conflict`）／`POST …/publish`（`invalid_transition`） |
| **本文なし** | 再変換の 409 | `POST /bff/conversion/jobs/{id}/retry`（**後段の `not_retryable` 本文は BFF が落とす**） |

`apiClient.parseProblemDetails` は 400 / 409 のとき本文から人間可読なメッセージ群を抽出し、
`ApiError.details` に載せる（`errors` の配列 → `detail` → `title` の順で拾う）。
**本文なしの 409 では `details` は空配列になる**——画面は本文ではなく**ステータスだけ**を根拠にする。

### 3. 縮退の方針は面ごとに違う（**揃えない理由がある**）

| 面 | 後段不達・不調のとき | 理由 |
| --- | --- | --- |
| `/bff/search`・`/bff/documents`（一覧） | **空応答へ縮退**（200） | 権限外の存在を示さない（deny-by-default） |
| `/bff/datasources`・`/bff/conversion/jobs`（一覧） | **502** | 運用画面。「未登録」「ジョブ無し」と誤認させると重複登録・見落としを誘発する |
| `/bff/analysis/ask/stream` | `event: error` の SSE イベント | ヘッダ送出済みで HTTP ステータスを変えられない |

### 4. 状態・種別の文字列を **enum にしない**

`status` / `action` / `scope` / `sourceType` などの値は C# 側で `string` であり、値集合は `const` 群
（`DocumentStatus` / `PolicyAction` / `AttributeScope` / `ConversionJobStatus`）にすぎない。
OpenAPI で閉じた `enum` にすると、**後段が値を増やした瞬間に SPA が既存の値を解釈できなくなる**。
契約では `type: string` ＋ `description` に値集合を書き、**未知の値も受け取れる**ようにする。
画面が値集合を持つ必要があるときは、feature 側の純関数（`jobStatus.ts` / `abacVocabulary.ts` /
`driftView.ts` 等）に置き、そこを単体テストで固定する。

### 5. 応答スキーマの `required` は **C# の非 null 性**から起こす（#520 / [[IADR-0132]]）

**orval は `required` の無いスキーマの全プロパティを省略可（`?`）で生成する。**
`required` を書き忘れた面は、契約に載っていても**型検査の網にならない**
（#506 の変異試験 M2 が実測。誤った型へ戻しても `typecheck exit=0` で素通りした）。

- **入れる**: C# DTO で `?` の付かないメンバー（値型・コレクション・**既定値つきメンバーを含む**）。
  C# の既定値は「引数を省いたときの値」であってシリアライズ時の省略ではない
  （`System.Text.Json` はプロパティを省略しない）。
- **入れない**: `?` の付くメンバー。および次の 5 スキーマ全体——
  `AbacConditionMap`（`properties` を持たない写像）／`ProblemDetails`・`ValidationProblemDetails`（RFC7807）／
  `ConfigVersionDto`・`ConfigVersionEntryDto`（C# の全メンバーが nullable）。
  **「入れない判断」は `description` に理由を書く**（「書き忘れ」と区別が付くようにする）。
- **`required` に入れたプロパティに `default` を書かない**（[[IADR-0132]] 決定 2 の系）。
  応答側の `default` は「**欠けていたらこの値と読め**」の意味であり、「必ず出る」と言う `required` と
  同居させると契約が自己矛盾する。C# の引数既定（`bool Sent = true` 等）は契約の情報ではない。
  **要求スキーマの `default` は別である**——「送らなければこの値になる」という本来の意味で機能する。
- **`required` を入れても `?? 既定値` は消さない**（[[IADR-0132]] 決定 3）。
  **「契約上は必須」と「実行時に必ず来る」は別**であり、応答本文を実行時に検証する層は無い。
- **`/bff/` 外のスキーマにも `required` を入れる。** 生成の前処理 `src/orval-bff-only.cjs` が
  入力から落とすのは **`paths` だけ**で、**`components.schemas` は素通りする**
  ——`/bff/` から到達しないスキーマも含め、**53 個すべてが `bff.schemas.ts` に出力される**
  （実測: `grep -c '^export interface ' src/platform/frontend/src/foundation/api/generated/bff.schemas.ts`）。
  **「`/bff/` 外は生成されないから書かなくてよい」は誤りである**——#520 の着手時にこの誤った想定を置き、
  変異試験 M8（`EmbedApiResponse.model` の削除で生成物に差分が出た）で覆った。
  ただし**生成されることと型検査の網になることは別**で、網の有無を決めるのは
  「その型を画面が読んでいるか」である（現に読まれているのは `AiAnswerDto` / `CitationDto` だけ）。
- **要求スキーマの `required` は別問題である。** 応答と要求の両方で使われるスキーマは
  `AbacConditionMap` の 1 個だけで、それは上記のとおり `required` を適用できない形をしている
  ——したがって「応答を厳しくしたら要求も必須になった」という事故は現時点では起こり得ない。
  **両用スキーマを新設するときはこの前提が崩れるので、要求側への影響を必ず確認すること。**

### 6. 応答項目の一部は**エンティティの列ではなく導出値**である（#538 / [[IADR-0136]]）

`DataSourceDto.nextSyncAt`（SC-06「次回同期」）は DB の列ではない。定期同期は**全ソース共通の間隔**で
回る hosted service であり、次回実行時刻は**ワーカーの位相から導出される**。契約上の性質は次のとおりで、
これを知らずに読むと「ソースごとに時刻を設定できる」と誤読する。

- **全ソースで同じ値**を返す（ソース別スケジュールは持たない。planning#200 の裁定 Q15）。
- **定期同期が無効なら `null`**（compose / dev の既定。`nullable: true`・`required` に入れない）。
- 保存されないため、**同じソースでも時間が経てば別の値**になる（キャッシュの鮮度と混同しない）。

## 非機能・運用

- **冪等性**: `POST /bff/feedback` は `(answerId, userId)` の upsert（新規 201 / 更新 200。IADR-0010）。
  `POST /bff/conversion/jobs/{id}/retry` は状態で直列化される（`failed` 以外は 409）。
- **認証**: 現在は Keycloak の JWT を `Authorization: Bearer` で付与する。
  **ADR-0032 の BFF セッション方式へ移行予定**（移行第 3 段 / #439）。移行時に直すのは
  `foundation/api/apiClient` の 1 箇所で、生成コードは `orvalMutator` 経由なので影響を受けない。
- **バージョニング**: 契約の破壊的変更は `scripts/check-contract-schema.js`（[[IADR-0122]]）が
  C# ソース側で検出する。**OpenAPI 側には同等のゲートが無い**（§未決事項 1）。

## 関連仕様

- 契約本体: [`openapi.yaml`](openapi.yaml)
- 実装 ADR: [[IADR-0131]]（本書の決定の根拠）・**[[IADR-0135]]（SPA 側の載せ替えとキャッシュキー）**・
  [[IADR-0121]]（BFF 境界）・[[IADR-0122]]（契約スキーマ）
- 画面仕様書: `docs/screens/SC-*.md`

## 未決事項

1. **OpenAPI は手書きであり、C# の DTO からは生成されていない**（`scripts/generate-openapi.sh` は無い）。
   したがって本書と `openapi.yaml` が与える保証は「**OpenAPI を変えると SPA の型検査が落ちる**」であって、
   「**C# の DTO を変えると型検査が落ちる**」ではない。C# → OpenAPI の追随は人手である。
2. **［2026-08-05 解消］SPA の載せ替えは #519 で完了した**（[作業仕様書 #519](../specs/20260805_issue-519_orval-hook-migration.md)）。
   残るのは **SSE の 1 本だけ**で、これは恒久的に `apiStream` である。
3. **`/bff/feedback`・`/bff/feedback/stats` は BFF に端点認可が無い**（実測）。ABAC も通らないため、
   BFF 単体では無認証の投稿・集計取得を拒まない。これを意図とみなすか（エッジで塞ぐ）、
   BFF へ `RequireAuthorization` を足すかは**本作業では判断していない**——#506 は契約の記述を
   揃える作業であり、認可の変更は挙動の変更だからである。**セキュリティ仕様書側での裁定が要る**——
   **#521** として起票済みである。
   - > **［2026-08-07 追記・裁定は下りた / #586］計画側が FR-08 の認可を確定した**
     > （planning `3e58b97` = PR planning#244〔裁定依頼 planning#236〕。**投稿は認証必須で無認証は 401／統計は運用者・管理者のみで
     > 権限外は 403／同一利用者の 2 回投稿でも集計は 1 件**）。**したがって本項はもう「未決」ではない。
     > 残っているのは実装であり、#521 が持つ。** 本書の認可列は実測（＝現状）のままである。
