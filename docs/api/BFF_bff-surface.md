---
title: BFF 境界（/bff/*）通信仕様書
type: api-spec
status: in-progress
created: 2026-08-05
updated: 2026-09-02
author: Claude
---
<!-- trace:
ids: [FR-01, FR-03, FR-04, FR-05, FR-06, FR-07, FR-08, FR-09, FR-10, FR-12, FR-15, FR-16, FR-22, SC-01, SC-02, SC-03, SC-05, SC-06, SC-07, SC-08, SC-09, SC-10, SC-11, SC-12, SC-17, UC-01, UC-02, UC-03, UC-04, UC-05, UC-06, UC-09, UC-11]
adrs: [ADR-0024, ADR-0026, ADR-0031, ADR-0032, ADR-0037, ADR-0043]
iadrs: [IADR-0009, IADR-0010, IADR-0044, IADR-0121, IADR-0122, IADR-0129, IADR-0131, IADR-0132, IADR-0135, IADR-0136, IADR-0151, IADR-0152, IADR-0153, IADR-0158, IADR-0215, IADR-0285, IADR-0297, IADR-0301, IADR-0347]
specs: [20260805_issue-506_openapi-bff-groups, 20260805_issue-519_orval-hook-migration, 20260805_issue-520_openapi-response-required, 20260806_issue-538_next-sync-at]
issues: [#439, #452, #506, #519, #520, #521, #538, #544, #586, #600, #629, #634, #640, planning#200, planning#236, planning#244, planning#299]
-->

# 通信仕様書: BFF 境界（`/bff/*`）

## 目次

- [起点となる計画書（トレーサビリティ）](#起点となる計画書トレーサビリティ)
- [概要](#概要)
- [生成対象と対象外（**「載せ忘れ」と「意図的な除外」の区別**）](#生成対象と対象外載せ忘れと意図的な除外の区別)
- [エンドポイント一覧](#エンドポイント一覧)
- [横断の規約](#横断の規約)
- [非機能・運用](#非機能運用)
- [関連仕様](#関連仕様)
- [未決事項](#未決事項)

> 個々のエンドポイントの要求・応答・ステータスは **[`openapi.yaml`](openapi.yaml) を正**とする。
> 本書はその上位にある**境界の規約**（誰が何をどう呼ぶか／何が生成対象で何が対象外か）を定める。
> 決定の根拠は、OpenAPI を BFF 契約の単一情報源とする実装 ADR を参照。

> **`status: in-progress` の理由と、いま残っているもの**（`docs/README.md` の語彙: `draft` =
> 着手前・記述途中／`in-progress` = 実装中）。BFF 境界は #506（PR #518）で**当時の全 27 パス**が契約に載り
> （**その後も端点は増える。現在数はここへ書かない**——下の一覧と `openapi.yaml` が正である）、
> #520 で応答スキーマの `required` が確定し、**#519 で SPA 側の載せ替えが完了した**。
> **着手前でも記述途中でもないので `draft` は外す。** 一方、次の 1 点が未了なので `completed` でもない。
>
> 1. ~~**`/bff/feedback`・`/bff/feedback/stats` の端点認可が未裁定である**~~
>    **［2026-08-10 解消 / #521］裁定（2026-08-07）と実装の双方が揃った。**
>    投稿は認証必須（無認証 401）、統計は運用者・管理者のみ（権限外 403）を
>    **BFF と後段の両層**で実装し、テストで固定した。
>
> **［2026-08-05 追記］#519 で載せ替えが済み、`apiFetch` を使う画面は 0 になった**
> （残る `foundation/api` 直接利用は **SSE の `apiStream` だけ**である）。

## 起点となる計画書（トレーサビリティ）

- 関連機能要求: データソースのカタログ化／ハイブリッド横断検索／根拠付き AI 回答と出典／文書 CRUD・版管理／
  回答フィードバック／属性・ポリシー管理／利用状況ダッシュボード／正規化変換／構成情報 API
- 関連ユースケース: 検索・質問する／AI 分析を依頼する／文書を管理する／データソースを登録・同期する／
  ABAC 権限を管理する／文書を正規化変換する
- 技術検討 / ADR:
  13_frontend-stack（計画リポ）（基本方針）／
  フロントエンドスタックの計画 ADR（Accepted）

## 概要

- **プロトコル**: REST / JSON（＋ SSE が 1 本）。すべて `/bff/` 接頭辞の下に置く。
- **SPA からの到達経路は 2 つだけである**（フロントエンドスタックの計画 ADR と、SPA 新スタック移行の決定 3）。
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
（存在秘匿）。**SPA から呼ぶ面ではない**ため契約に載せない。

## エンドポイント一覧

**`operationId` は例外なく C# の `WithName` のケバブケースである**（#519 で既存 2 本
〔`analysis-ask` / `analysis-analyze`〕を規約へ揃えた。契約の単一情報源の決定 3 を、生成クライアント採用の決定 5 が改定した）。

**認可の列は BFF 実装の実測である。** 「404（秘匿）」は「不在」と「権限外」を区別しないことを指す
（存在秘匿の方針）。詳細（要求・応答スキーマ・全ステータス）は [`openapi.yaml`](openapi.yaml) を正とする。

| メソッド | パス | 認可 | 関連する要求・画面 | 生成される関数 |
| --- | --- | --- | --- | --- |
| POST | `/bff/attribute-values` | **端点認可なし**（**候補は ABAC で絞る**。スコープ付き属性値ルックアップの決定）。**［#634］辞書（`dictionary`）が付くのは管理者・運用者かつ `key=tags` のときだけ**——実効境界は DocumentService の `/tags` 側である（書き込み・管理 API への認可強制＝多層防御） | —| `useBffAttributeValues` |
| GET | `/bff/tags` | **admin ＋ operator**（辞書の読み取り。裁定 Q18・タグ辞書の実装判断・決定 5） | —| `useBffTagList`（**#640**） |
| POST | `/bff/tags` | **AdminOnly**（運用者も不可。管理者設定画面はシステム管理者ロール限定） | —| `useBffTagCreate`（**#640**。名前の重複は 409） |
| PUT | `/bff/tags/{id}` | **AdminOnly** | —| `useBffTagRename`（**#640**。**文書は 1 件も書き換わらない**——正本が識別子を参照する。タグの正本を識別子とする実装判断・決定 1。応答の `republishedDocuments` は射影の再発行件数） |
| DELETE | `/bff/tags/{id}` | **AdminOnly** | —| `useBffTagDelete`（**#640**。**参照 1 件以上は 409 ＋ `usageCount`**。管理者設定画面「削除前に使用件数を示す」のため**本文ごと透過する**） |
| POST | `/bff/search` | **端点認可なし**（結果は ABAC で絞る） | —| `useBffSearch`（**mutation**。検索結果一覧は操作関数 `bffSearch` を `useQuery` に据える。生成クライアント採用の決定 2） |
| POST | `/bff/analysis/ask` | 同上 | —| `useBffAnalysisAsk`（**画面は呼んでいない**） |
| POST | `/bff/analysis/ask/stream` | 同上 | —| **無し（SSE。`apiStream`）** |
| POST | `/bff/analysis/analyze` | 同上 | —| `useBffAnalysisAnalyze` |
| POST | `/bff/feedback` | **認証必須**（ロールは問わない。無認証は 401。#521） | —| `useBffSubmitFeedback` |
| GET | `/bff/feedback/stats` | **`platform-admin` ＋ `platform-operator`**（無認証 401 / 権限外 403。#521） | —| `useBffFeedbackStats` |
| GET | `/bff/dashboard/summary` | **admin ＋ operator**（**#544**。計画側の運用ダッシュボード「運用者・管理者ロール限定」。従前は admin のみ） | —| `useBffDashboardSummary` |
| GET | `/bff/documents` | **端点認可なし**（ABAC で絞る） | —| `useBffDocumentList` |
| POST | `/bff/documents` | **admin のみ** | —| `useBffDocumentCreate` |
| GET | `/bff/documents/{id}` | **端点認可なし**（ABAC ＋ 404 秘匿） | —| `useBffDocumentDetail` |
| PUT | `/bff/documents/{id}` | **admin のみ** | —| `useBffDocumentUpdate` |
| DELETE | `/bff/documents/{id}` | **admin のみ** | —| `useBffDocumentDelete` |
| GET | `/bff/documents/{id}/content` | **端点認可なし**（ABAC ＋ 404 秘匿） | —| `useBffDocumentContent` |
| GET | `/bff/documents/{id}/versions` | **端点認可なし**（ABAC ＋ 404 秘匿） | —| `useBffDocumentVersions` |
| POST | `/bff/documents/{id}/publish` | **admin のみ** | —| `useBffDocumentPublish` |
| POST | `/bff/documents/{id}/archive` | **admin のみ** | —| `useBffDocumentArchive` |
| GET | `/bff/datasources` | **admin / operator** | —| `useBffDataSourceList` |
| POST | `/bff/datasources` | **admin のみ** | —| `useBffDataSourceCreate` |
| GET | `/bff/datasources/{id}` | **admin / operator** | —| `useBffDataSourceGet` |
| PUT | `/bff/datasources/{id}` | **admin のみ** | —| `useBffDataSourceUpdate` |
| PATCH | `/bff/datasources/{id}` | **admin のみ** | —| `useBffDataSourcePatch` |
| DELETE | `/bff/datasources/{id}` | **admin のみ** | —| `useBffDataSourceDelete` |
| POST | `/bff/datasources/{id}/sync` | **admin / operator**（破壊的操作に含めない。計画側の裁定による） | —| `useBffDataSourceSync` |
| GET | `/bff/conversion/jobs` | **admin / operator** | —| `useBffConversionJobList` |
| GET | `/bff/conversion/jobs/{id}` | **admin / operator** | —| `useBffConversionJobGet` |
| POST | `/bff/conversion/jobs/{id}/retry` | **admin のみ** | —| `useBffConversionJobRetry` |
| GET | `/bff/conversion/jobs/{id}/figures` | **admin のみ** | —| `useBffConversionJobFigures` |
| GET | `/bff/conversion/jobs/{id}/figures/{figureId}/image` | **admin のみ** | —| （画像。生成フックを使わない） |
| POST | `/bff/conversion/jobs/{id}/figures/{figureId}/correction` | **admin のみ** | —| `useBffConversionJobFigureCorrection` |
| GET | `/bff/notifications` | **認証必須・ロールは問わない**（`x-roles: []`）。**絞るのは役割ではなく主体**（JWT の `sub`）——本人宛だけを返し、管理者にも他人の通知は返らない（利用者通知の要求／Obsidian 同期方式の計画 ADR 決定 6。通知サービスの実装 ADR 決定 2） | —| `useBffNotificationList` |
| POST | `/bff/notifications/{id}/read` | 同上。**本人の通知でなければ 404**（「存在しない」と「本人のものでない」を区別しない。存在秘匿の方針） | —| `useBffNotificationMarkRead`（**冪等**。既読へもう一度呼んでも 200） |
| GET | `/bff/admin/authz/policies` | **admin のみ** | —| `useBffAuthzListPolicies` |
| POST | `/bff/admin/authz/policies` | **admin のみ** | —| `useBffAuthzCreatePolicy` |
| POST | `/bff/admin/authz/policies/validate` | **AdminOnly** | —| `useBffAuthzValidatePolicy`（**#535**。保存せず検証だけ行う。**矛盾は 200 ＋ `{ valid, errors }`** で、保存の 400 とは別。後段は登録・更新と**同じ検証関数**を通る） |
| GET | `/bff/admin/authz/policies/{id}` | **admin のみ** | —| `useBffAuthzGetPolicy` |
| PUT | `/bff/admin/authz/policies/{id}` | **admin のみ** | —| `useBffAuthzUpdatePolicy` |
| DELETE | `/bff/admin/authz/policies/{id}` | **admin のみ** | —| `useBffAuthzDeletePolicy` |
| PATCH | `/bff/admin/authz/policies/{id}/active` | **admin のみ** | —| `useBffAuthzSetPolicyActive` |
| GET | `/bff/admin/authz/attributes` | **admin のみ** | —| `useBffAuthzListAttributes` |
| POST | `/bff/admin/authz/attributes` | **admin のみ** | —| `useBffAuthzCreateAttribute` |
| GET | `/bff/admin/authz/attributes/{id}` | **admin のみ** | —| `useBffAuthzGetAttribute` |
| PUT | `/bff/admin/authz/attributes/{id}` | **admin のみ** | —| `useBffAuthzUpdateAttribute` |
| DELETE | `/bff/admin/authz/attributes/{id}` | **admin のみ** | —| `useBffAuthzDeleteAttribute` |
| GET | `/bff/admin/mcp-clients` | **admin のみ** | —| `useBffMcpListClients` |
| POST | `/bff/admin/mcp-clients` | **admin のみ** | —| `useBffMcpRegisterClient`（後段の 400〔禁止された属性割当〕・409〔重複〕を透過する） |
| GET | `/bff/admin/mcp-clients/tools` | **admin のみ** | —| `useBffMcpListTools`（**読み取りのみ**。公開範囲の変更は Git 経由の公開構成変更で行う。書き込みの口を作らない） |
| POST | `/bff/admin/mcp-clients/{clientId}/disable` | **admin のみ** | —| `useBffMcpDisableClient`（後段の 404 を**そのまま**返す） |
| POST | `/bff/admin/mcp-clients/{clientId}/enable` | **admin のみ** | —| `useBffMcpEnableClient` |
| PUT | `/bff/admin/mcp-clients/{clientId}/attributes` | **admin のみ** | —| `useBffMcpReplaceClientAttributes` |
| GET | `/bff/admin/users` | **admin のみ** | —| `useBffUserAdminListUsers`（**作成の口は無い**。アカウントは人事システム連携で自動的に作られる） |
| GET | `/bff/admin/users/assignable-roles` | **admin のみ** | —| `useBffUserAdminListAssignableRoles`（入力規則「定義済みロールのみ」の値域。画面へ焼き込まない） |
| PUT | `/bff/admin/users/{userId}/attributes` | **admin のみ** | —| `useBffUserAdminReplaceUserAttributes`（差し替え。後段の 400〔必須欠落・辞書外の値／キー〕を透過する） |
| PUT | `/bff/admin/users/{userId}/roles` | **admin のみ** | —| `useBffUserAdminReplaceUserRoles`（差し替え。空集合は 400 —— 権限剥奪は無効化で行う） |
| POST | `/bff/admin/users/{userId}/disable` | **admin のみ** | —| `useBffUserAdminDisableUser`（**無効化と全セッション失効は 1 つの操作である**。後段の 404 を**そのまま**返す） |
| POST | `/bff/admin/users/{userId}/enable` | **admin のみ** | —| `useBffUserAdminEnableUser`（セッションは復活しない） |
| GET | `/bff/admin/config` | **ConfigViewer**（非権限は 404） | —| `useBffConfigEffective` |
| GET | `/bff/admin/config/drift` | 同上 | —| `useBffConfigDrift` |
| GET | `/bff/admin/config/history` | 同上 | —| `useBffConfigHistory` |

> **［2026-08-07 追記 / #586］`/bff/feedback` 系の 2 行は計画と食い違う。**
> 計画リポジトリのコミット `3e58b97`（裁定依頼への回答を反映したもの）で、フィードバック収集の要求に
> **「投稿には認証を要する（匿名投稿は許さない）／統計は運用者・管理者に限って参照できる」**が確定し、
> 受け入れ基準として **無認証で 401 / 認証済みでも権限外は 403** が置かれた。
> **［2026-08-10 是正 / #521］上表を実装へ揃えた。** 従前の「端点認可なし」は
> 当時の実測としては正しかったが、**BFF と後段の両層へ認可を足した**ので現在は誤りである。
> 投稿は `RequireAuthorization()`（ロールは問わない——計画が定めていない制限を足さない）、
> 統計は群の認証と `RequireRole(Admin, Operator)` の **AND 合成**で実効 admin ＋ operator。
> 同型の記述も同じ PR で揃えた: [機能仕様書](../functional/FR-08_answer-feedback.md)・
> [テスト仕様書](../tests/FR-08_answer-feedback.md)・関連する 2 件の実装 ADR・§未決事項 3。

## 横断の規約

### 1. 認可の失敗をどう見せるか（**面ごとに違う。揃えない**）

| 面 | 未認証 | 権限不足 | 理由 |
| --- | --- | --- | --- |
| `/bff/admin/config` 群 | **404** | **404** | **存在秘匿**の方針による。`RequireAuthorization` を**使わず**ハンドラ内で `ConfigViewer` を評価する——付けると無認証が 404 到達前に 401 で短絡して存在が漏れる |
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
>   **開発・テスト環境では `anonymous`** として記録される（upsert による二重計上防止はこの ID で行う）。
>   **BFF 自身は無認証の投稿を拒まない**——この面をどこで塞ぐか（エッジか BFF か）は本書では未確認である
>   （§未決事項 3）。

**SPA 側はこの差を吸収する**——403 と 404 をどちらも同じ中立表示へ寄せ、5xx とは区別して
`role="alert"` を出す（管理画面 3 種の再実装・決定 3）。「権限が無い」と「存在しない」を利用者に見分けさせない。

### 2. エラー本文の形が 3 種類ある（**統一されていない。実装がそうなっている**）

| 形 | 使う面 | 例 |
| --- | --- | --- |
| **RFC7807 `ValidationProblemDetails`** | 保存前検証の 400 | `/bff/admin/authz/*` の登録・更新／`/bff/documents` の作成・更新 |
| **RFC7807 `ProblemDetails`** | 参照中削除の 409 | `DELETE /bff/admin/authz/attributes/{id}`（`detail` に参照元ポリシー名） |
| **素の JSON** | 楽観ロック・状態遷移の 409 | `PUT /bff/documents/{id}`（`version_conflict`）／`POST …/publish`（`invalid_transition`） |
| **本文あり（透過）** | 再変換の 409 | `POST /bff/conversion/jobs/{id}/retry`（`not_retryable` / **`corrections_would_be_lost` ＋ `correctedFigures`**） |
| **本文あり（透過）** | 人手補正の 409 | `POST /bff/conversion/jobs/{id}/figures/{figureId}/correction`（`figure_not_correctable` / `job_busy` / `body_unavailable`） |

`apiClient.parseProblemDetails` は 400 / 409 のとき本文から人間可読なメッセージ群を抽出し、
`ApiError.details` に載せる（`errors` の配列 → `detail` → `title` の順で拾う）。
**本文なしの 409 では `details` は空配列になる**——画面は本文ではなく**ステータスだけ**を根拠にする。

### 3. 縮退の方針は面ごとに違う（**揃えない理由がある**）

| 面 | 後段不達・不調のとき | 理由 |
| --- | --- | --- |
| `/bff/search`・`/bff/documents`（一覧） | **空応答へ縮退**（200） | 権限外の存在を示さない（deny-by-default） |
| `/bff/attribute-values` | **空配列へ縮退**（200） | **404 にも 403 にもしない** —— 候補が無いことと権限が無いことを区別させない（スコープ付き属性値ルックアップの決定 3 の趣旨と、facet 実装の決定 5） |
| `/bff/datasources`・`/bff/conversion/jobs`（一覧） | **502** | 運用画面。「未登録」「ジョブ無し」と誤認させると重複登録・見落としを誘発する |
| `/bff/analysis/ask/stream` | `event: error` の SSE イベント | ヘッダ送出済みで HTTP ステータスを変えられない |

> **［2026-08-09 注記 / #540］「縮退」は後段への到達失敗を指す。後段の非 2xx はそのまま透過する。**
> `/bff/search` と `/bff/attribute-values` はどちらも、`HttpRequestException` /
> `TaskCanceledException`（**接続断・タイムアウト**）だけを空応答へ畳み、
> 後段が返した非 2xx は `Results.StatusCode` で**そのまま返す**（実測）。
> **「後段が失敗したら必ず 200 空応答になる」と読まないこと。** 後段が 500 を返せば 500 が出る。
> deny-by-default が守るのは「**権限外の存在を示さない**」ことであって、
> 後段の障害を利用者から隠すことではない。

### 4. 状態・種別の文字列を **enum にしない**

`status` / `action` / `scope` / `sourceType` などの値は C# 側で `string` であり、値集合は `const` 群
（`DocumentStatus` / `PolicyAction` / `AttributeScope` / `ConversionJobStatus`）にすぎない。
OpenAPI で閉じた `enum` にすると、**後段が値を増やした瞬間に SPA が既存の値を解釈できなくなる**。
契約では `type: string` ＋ `description` に値集合を書き、**未知の値も受け取れる**ようにする。
画面が値集合を持つ必要があるときは、feature 側の純関数（`jobStatus.ts` / `abacVocabulary.ts` /
`driftView.ts` 等）に置き、そこを単体テストで固定する。

### 5. 応答スキーマの `required` は **C# の非 null 性**から起こす（#520）

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
- **`required` に入れたプロパティに `default` を書かない**（`required` を非 null 性から起こす決定 2 の系）。
  応答側の `default` は「**欠けていたらこの値と読め**」の意味であり、「必ず出る」と言う `required` と
  同居させると契約が自己矛盾する。C# の引数既定（`bool Sent = true` 等）は契約の情報ではない。
  **要求スキーマの `default` は別である**——「送らなければこの値になる」という本来の意味で機能する。
- **`required` を入れても `?? 既定値` は消さない**（同決定 3）。
  **「契約上は必須」と「実行時に必ず来る」は別**であり、応答本文を実行時に検証する層は無い。
- **`/bff/` 外のスキーマにも `required` を入れる。** 生成の前処理 `src/orval-bff-only.cjs` が
  入力から落とすのは **`paths` だけ**で、**`components.schemas` は素通りする**
  ——`/bff/` から到達しないスキーマも含め、**`components.schemas` の宣言がそのまま `bff.schemas.ts` へ出力される**
  （数え方: `grep -c '^export interface ' src/platform/frontend/src/lib/api/generated/bff.schemas.ts`。
  **件数はここに書かない**——契約が増えれば動く。**［2026-08-10 / #558］従前ここには「53 個すべて」と
  書いてあり、実測すると 69 で古くなっていた**。数を書くと次に読む人が古い数を信じる）。
  **「`/bff/` 外は生成されないから書かなくてよい」は誤りである**——#520 の着手時にこの誤った想定を置き、
  変異試験 M8（`EmbedApiResponse.model` の削除で生成物に差分が出た）で覆った。
  ただし**生成されることと型検査の網になることは別**で、網の有無を決めるのは
  「その型を画面が読んでいるか」である（**［2026-08-08 / #591］#519 の載せ替え後、生成型は
  `sc01`〜`sc11` の各画面が読んでいる**。従前ここには「読まれているのは `AiAnswerDto` /
  `CitationDto` だけ」と書いてあり、載せ替え前の事実のまま残っていた）。
- **要求スキーマの `required` は別問題である。** 応答と要求の両方で使われるスキーマは
  `AbacConditionMap` の 1 個だけで、それは上記のとおり `required` を適用できない形をしている
  ——したがって「応答を厳しくしたら要求も必須になった」という事故は現時点では起こり得ない。
  **両用スキーマを新設するときはこの前提が崩れるので、要求側への影響を必ず確認すること。**

### 6. 応答項目の一部は**エンティティの列ではなく導出値**である（#538）

`DataSourceDto.nextSyncAt`（データソース管理画面の「次回同期」）は DB の列ではない。定期同期は**全ソース共通の間隔**で
回る hosted service であり、次回実行時刻は**ワーカーの位相から導出される**。契約上の性質は次のとおりで、
これを知らずに読むと「ソースごとに時刻を設定できる」と誤読する。

- **全ソースで同じ値**を返す（ソース別スケジュールは持たない。計画側の裁定 Q15）。
- **定期同期が無効なら `null`**（compose / dev の既定。`nullable: true`・`required` に入れない）。
- 保存されないため、**同じソースでも時間が経てば別の値**になる（キャッシュの鮮度と混同しない）。

## 非機能・運用

- **冪等性**: `POST /bff/feedback` は `(answerId, userId)` の upsert（新規 201 / 更新 200。upsert による冪等化）。
  `POST /bff/conversion/jobs/{id}/retry` は状態で直列化される（`failed` 以外は 409）。
- **認証**: 現在は Keycloak の JWT を `Authorization: Bearer` で付与する。
  **計画側が定める BFF セッション方式へ移行予定**（移行第 3 段 / #439）。移行時に直すのは
  `foundation/api/apiClient` の 1 箇所で、生成コードは `orvalMutator` 経由なので影響を受けない。
- **バージョニング**: 契約の破壊的変更は `scripts/check-contract-schema.js`（契約スキーマの抽出方式と後方互換ゲート）が
  C# ソース側で検出する。**OpenAPI 側には同等のゲートが無い**（§未決事項 1）。

## 関連仕様

- 契約本体: [`openapi.yaml`](openapi.yaml)
- 実装 ADR: OpenAPI を BFF 契約の単一情報源とする（本書の決定の根拠）・**生成クライアントの採用とキャッシュキー**・
  SPA 新スタック移行（BFF 境界）・契約スキーマの後方互換ゲート
- 画面仕様書: `docs/screens/SC-*.md`

## 未決事項

1. **OpenAPI は手書きであり、C# の DTO からは生成されていない**（`scripts/generate-openapi.sh` は無い）。
   したがって本書と `openapi.yaml` が与える保証は「**OpenAPI を変えると SPA の型検査が落ちる**」であって、
   「**C# の DTO を変えると型検査が落ちる**」ではない。C# → OpenAPI の追随は人手である。
2. **［2026-08-05 解消］SPA の載せ替えは #519 で完了した**（仕様書: 画面の通信を orval 生成物へ載せ替える）。
   残るのは **SSE の 1 本だけ**で、これは恒久的に `apiStream` である。
3. **`/bff/feedback`・`/bff/feedback/stats` は BFF に端点認可が無い**（実測）。ABAC も通らないため、
   BFF 単体では無認証の投稿・集計取得を拒まない。これを意図とみなすか（エッジで塞ぐ）、
   BFF へ `RequireAuthorization` を足すかは**本作業では判断していない**——#506 は契約の記述を
   揃える作業であり、認可の変更は挙動の変更だからである。**セキュリティ仕様書側での裁定が要る**——
   **#521** として起票済みである。
   - > **［2026-08-07 追記・裁定は下りた / #586］計画側がフィードバック収集の認可を確定した**
     > （計画リポジトリのコミット `3e58b97`。裁定依頼への回答を反映したもの。**投稿は認証必須で無認証は 401／統計は運用者・管理者のみで
     > 権限外は 403／同一利用者の 2 回投稿でも集計は 1 件**）。**したがって本項はもう「未決」ではない。**
     > **［2026-08-10 解消 / #521］実装も揃った。** BFF と後段の両層へ認可を足し、
     > 上表の認可列も実装へ揃えた（従前は実測＝現状のままだった）。

<!-- trace-table:
row1: FR-04, FR-05, FR-09, SC-01, SC-05, SC-08, SC-09
row2: FR-09, UC-05, SC-09
row3: FR-09, UC-05, SC-09
row4: FR-09, UC-05, SC-09
row5: FR-09, UC-05, SC-09
row6: FR-03, UC-01, SC-02
row7: FR-04, UC-01, SC-01
row8: FR-04, UC-01, SC-01
row9: FR-07, UC-02, SC-08
row10: FR-08, UC-01, SC-01
row11: FR-08, SC-10
row12: FR-10, UC-05, SC-10
row13: FR-06, SC-05
row14: FR-06, UC-03, SC-05
row15: FR-06, SC-03
row16: FR-06, UC-03, SC-05
row17: FR-06, UC-03, SC-05
row18: FR-06, SC-03
row19: FR-06, UC-03, SC-03
row20: FR-06, UC-03, SC-05
row21: FR-06, UC-03, SC-05
row22: FR-01, SC-06
row23: FR-01, UC-04, SC-06
row24: FR-01, SC-06
row25: FR-01, UC-04, SC-06
row26: FR-01, UC-04, SC-06
row27: FR-01, SC-06
row28: FR-01, UC-04, SC-06
row29: FR-12, UC-06, SC-07
row30: FR-12, SC-07
row31: FR-12, UC-06, SC-07
row32: FR-12, UC-06, SC-07
row33: FR-12, UC-06, SC-07
row34: FR-12, UC-06, SC-07
row35: FR-22, UC-11
row36: FR-22, UC-11
row37: FR-09, UC-05, SC-09
row38: FR-09, UC-05, SC-09
row39: FR-05, FR-09, SC-09
row40: FR-09, SC-09
row41: FR-09, UC-05, SC-09
row42: FR-09, SC-09
row43: FR-09, SC-09
row44: FR-09, UC-05, SC-09
row45: FR-09, UC-05, SC-09
row46: FR-09, SC-09
row47: FR-09, UC-05, SC-09
row48: FR-09, SC-09
row49: FR-15, SC-11
row50: FR-15, SC-11
row51: FR-15, SC-11
-->
