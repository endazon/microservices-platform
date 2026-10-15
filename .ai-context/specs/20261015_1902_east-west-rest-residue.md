---
title: 作業仕様書 — east-west REST 退役の残余を掃除する（呼び出し元が 0 の REST 受け口・読まれない REST 宛先の env・未使用の引数。#1901 / IADR-0533 の残余）
type: spec
status: done
related_ids: [NFR-09, NFR-16, ADR-0029, ADR-0089, IADR-0379, IADR-0426, IADR-0533]
author: claude
created: 2026-10-15
updated: 2026-10-15
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0089_east-west-completion-rule-and-authz-service-face.md（決定 1。撤去が移行の終わりである）
  - planning:projects/microservices-platform/07_adr/ADR-0029_grpc-rest-usage-criteria.md（east-west 同期は gRPC・north-south は REST）
issue: "#1902"
---

# 作業仕様書 — east-west REST 退役の残余の掃除（#1902）

> 本仕様書は実装着手前に作成した（着手 2026-10-15）。基点は MSP `origin/develop` `28342843`。
> 判断の記録は [IADR-0533](../adr/IADR-0533_east-west-rest-retirement.md) に日付つきの追記で残す
> （新しい判断は「受け口を撤去する」だけで、決定 5 が「別 issue で受け口ごとに行う」と予告済みのため、IADR は新設しない）。

## 起点（トレーサビリティ）

- 起点 issue: **#1902**（親 #1255。#1901 / IADR-0533 のフォローアップ 1・3・4・5）。
- 計画: **ADR-0089 決定 1**・**ADR-0029**（north-south は REST のまま）・NFR-09・NFR-16。計画の変更は要らない。
- 射程外: `LlmGatewayClusterSummaryClient` の gRPC 化（IADR-0533 フォローアップ 2。#1255 で扱う）。

## 母集合の取り方（規則 9・10）

issue の列挙を鵜呑みにせず、**誤りの側の文字列**（受け口のルート文字列・`Map*` 呼び出し・env 名）で全追跡ファイルを引いた。

1. 受け口: `git grep -nE 'Map(Get|Post|Put|Delete|Group)\('` を `src/` の本番コードで引き、issue が名指した 9 群の
   ルートを特定した。
2. 呼び出し元: 各ルート文字列（`authz/scope`・`/embed`・`complete/stream`・`attribute-values`・`/neighbors`・
   `edge-types`・`internal/notifications`・`knowledge-health/observations`・`internal/tags/names`・`}/tags`）を
   **MSP の全追跡ファイル**（`src/` の本番・試験・frontend・obsidian-plugin、`deploy/`、`scripts/`、`perf/`、`.github/`、`docs/`）と
   **AST**（submodule の pin `bc2ab98` と隣接クローンの最新 `3f80163c` の両方）で引いた。
3. env: `Services__(AuthorizationService|LlmGateway)\b`（`Grpc` 接尾辞なし）を `deploy/` で引き、所属サービスへ割り付けた。
   読み手は `Services:(AuthorizationService|LlmGateway)` を各サービスの本番コード・appsettings で引いた。

## 受け口の母集合と処置

| # | 受け口 | サービス | 本番の呼び出し元（撤去後） | AST | 処置 | 理由 |
|---|---|---|---|---|---|---|
| 1 | `POST /authz/scope` | AuthorizationService | 0（B-1・A-1〜A-6 は gRPC。BFF の管理面の代理は `/authz/attributes`・`/authz/users` で、`/authz/scope` を叩かない） | 0（AST は自前の認可） | **撤去** | east-west 専用。ABAC の評価は gRPC 面 `AuthzScope/Resolve` が持つ |
| 2 | `POST /embed` | LlmGateway | 0（L-1・L-2 は gRPC） | 0（AST は `/complete` だけ） | **撤去** | 同上。越境判定は gRPC 面 `LlmEmbedding/Embed` が同じ `EmbedUseCase` を呼ぶ |
| 3 | `POST /complete/stream` | LlmGateway | 0（L-3 は gRPC） | 0（AST の proto 注記「本リポジトリはこの rpc を呼んでいない」） | **撤去** | 同上 |
| 4 | `POST /search/attribute-values` | RetrievalService | 0（R-2 は gRPC） | 0（AST は `/search` だけ） | **撤去** | 同上。問い合わせ本体は gRPC 面へ移す |
| 5 | `GET /graph/{id}/neighbors` | GraphService | **BFF の中継 `/bff/graph/{id}/neighbors`**（north-south） | 0 | **残す** | G-1 の呼び出しは消えたが、SC-03 のグラフ表示が BFF 経由で叩く north-south の口である（ADR-0029） |
| 6 | `GET /graph/edge-types/catalog` | GraphService | **BFF の中継 `/bff/graph/edge-types`**（north-south） | 0 | **残す** | 同上（辺の型名の解決） |
| 7 | `POST /internal/notifications` | NotificationService | 0（N-1 は gRPC） | 0 | **撤去** | 同上。`NotificationIngress.AcceptAsync` は gRPC 面が呼ぶ |
| 8 | `POST /internal/knowledge-health/observations` | DashboardService | 0（H-1 は gRPC） | 0 | **撤去** | 同上。判定器は gRPC 面へ移す |
| 9 | `GET /internal/tags/names` | DocumentService | 0（D-3 は gRPC） | 0 | **撤去** | 同上。問い合わせ本体は gRPC 面へ移す |
| 10 | `POST /documents/{id}/tags` | DocumentService | 0（D-2 は gRPC。BFF・MCP・obsidian-plugin・scripts も叩かない） | 0（AST は `/documents`・`/documents/{id}/body`・`DELETE /documents/{id}` だけ） | **撤去** | 同上。`AddDocumentTagUseCase` は gRPC 面が呼ぶ |

- 除外の確認（north-south の口を消していないこと）: `/bff/*`・`/complete`（AST・合成監視）・`/search`（AST）・`/documents`（AST）・
  `/tags`（`/bff/tags`・`seed-tag-dictionary.js`）・`/graph/*`（BFF の中継）・`/health/*` は触らない。
- 試験の器のスタブが受け口のパス文字列をキーに使うもの（`TestRagOrchestrator`・`BffTestFactory` の `/authz/scope` 等）は、
  **HTTP の偽物を gRPC の生成クライアントへ写す器の内部の名前**であり、受け口を呼んでいない。射程外として残す。

## env の母集合と処置

| サービス | キー | compose | helm | 読み手 | 処置 |
|---|---|---|---|---|---|
| datasource | `Services__AuthorizationService` | 在る | 在る | 無い（A-6 は gRPC） | **削除** |
| retrieval | `Services__AuthorizationService` | 在る | 在る | 無い（A-4 は gRPC） | **削除** |
| aianalysis | `Services__AuthorizationService` | 在る | 在る | 無い（A-1 は gRPC） | **削除** |
| wiki | `Services__AuthorizationService` | 在る | 在る | 無い（A-3 は gRPC） | **削除** |
| graph | `Services__AuthorizationService` | 在る | 在る | 無い（A-2 は gRPC） | **削除** |
| bff | `Services__AuthorizationService` | 在る | 在る | **在る**（管理面の代理 `"AuthorizationService"` HttpClient） | 残す |
| conversion | `Services__LlmGateway` | 無い | 在る | 無い（L-5 は gRPC） | **削除** |
| ingestion | `Services__LlmGateway` | 在る | 在る | 無い（L-1 は gRPC） | **削除** |
| retrieval | `Services__LlmGateway` | 在る | 在る | 無い（L-2 は gRPC） | **削除** |
| aianalysis | `Services__LlmGateway` | 在る | 在る | **在る**（readiness の `/health/live`） | 残す |
| graph | `Services__LlmGateway` | 在る | 在る | **在る**（クラスタ要約 REST） | 残す |

- 各サービスの `appsettings*.json` の同じキー（読み手が無いもの）も削除する。
- 再発の網: `EastWestGrpcDeploymentWiringTests` に「削除した (呼び出し元, キー) が helm・compose に戻っていない」試験を足す。

## その他

- `BffScopeResolver.ResolveAsync` の未使用の `IHttpClientFactory` 引数を除き、呼び出し側を追随させる。
- `SearchUserContext.ForwardableCredential` を整理する（REST 輸送の転送のためだけの値）。
- `IRagSearchTransport.cs` の「REST と gRPC の 2 実装」を現状（gRPC 1 実装）へ合わせる。
- `docs/api/openapi.yaml`（手書き。生成コマンドは無い）から撤去した受け口を除く。

## 試験の写し方

受け口だけを叩いていた試験は、**守っていた性質ごとに**次のいずれかへ写す。

- 同じ性質を gRPC 面の試験が既に持つ → 削除（例: REST 面の 401/403 の門。gRPC 面の T-S-02/03 が持つ）。
- 「REST と gRPC が同じ答えを返す」同値の対 → 対が作れないので削除（#1901 と同じ扱い）。
- 性質が REST 面にしか書かれていない（越境判定・ABAC の評価・検証・重複判定など） → **TestServer 上の gRPC 面へ写す**。

## 受け入れ基準

- [x] 上の表の「撤去」10 件中 8 件の受け口がルート表から消え、north-south の 2 件（#5・#6）は残る。
- [x] 上の表の「削除」8 組の env が helm・compose から消え、配線試験がそれを固定する。
- [x] `BffScopeResolver.ResolveAsync` に `IHttpClientFactory` が無い。
- [x] `SearchUserContext.ForwardableCredential`・`IRagSearchTransport` の記述が現状に合う。
- [x] 影響プロジェクトの build / test / `dotnet format --verify-no-changes`、`helm lint`・`helm template`、
      `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`、文書系検査が緑。

## 検証

（実施後に追記する）
