---
title: 作業仕様書 — BFF が後段の不達・時間切れを黙って畳まず、ログと計器を出して区別できる応答にする（#2104）
type: spec
status: done
related_ids: [FR-03, NFR-06, NFR-19, NFR-28, IADR-0603, IADR-0537, IADR-0417]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-03・NFR-06・NFR-19・NFR-28）
---

# 仕様書: BFF が後段の不達・時間切れを黙って畳まず、ログと計器を出す（#2104。親 #2047 指摘 M-7）

## 起点となる計画書（トレーサビリティ）

- 機能要件: FR-03（横断検索）
- 非機能要件: NFR-19（可観測性「メトリクス・ログ・分散トレースを全サービスで収集」）、NFR-06（障害時の縮退運転「検索は継続、AI 回答のみ一時停止を許容」）、
  NFR-28（ログへ外部由来の文字列を載せない）
- 実装 ADR: IADR-0603（本件の決定）、IADR-0537（文書一覧は空へ畳まず 502）、IADR-0417 決定 9（属性値候補の不達は空配列）

## 目的・背景

BFF の端点の多くが後段（HTTP の名前付きクライアント・east-west gRPC）の不達・時間切れを `catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)`
で拾い、502 や「200・0 件」へ畳んでいた。**ログも計器も出さない**ため運用から見えず、検索（`POST /bff/search`）では利用者にも「該当なし」と区別できなかった。
是正済みの対照は文書の読み取り（`DocumentReadFailureLog`。#1897）で、WARN を 1 行出し、一覧は 502 へ倒す（IADR-0537）。

## 母集合（規則 9・10。記憶で挙げない）

走査（origin/develop `b93a854f`）:

```
git grep -n -E 'catch \((Exception|HttpRequestException|TaskCanceledException)[^)]*\)( when)?' -- 'src/*/backend/Bff/**/*.cs'
git grep -n -E 'catch \((HttpRequestException|TaskCanceledException|OperationCanceledException|RpcException)' -- 'src/*/backend/Bff/**/*.cs'
```

issue 本文の「15 件」は検証せず転記しない。上の走査で、**後段への要求の失敗を畳む catch は 20 件**（下表）。

| # | 箇所 | 後段 | 従前の応答 | 本件の応答 | ログ | 計器 |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | `SearchBffEndpoints` 横断検索 | RetrievalService | **200・0 件** | **502**（「該当なし」と区別。IADR-0537 と同じ形） | 追加 | 追加 |
| 2 | `SearchBffEndpoints` 属性値候補（gRPC 不達） | RetrievalService | 200・空候補 | 200・空候補（IADR-0417 決定 9 の縮退を維持） | 追加 | 追加 |
| 3 | `SearchBffEndpoints` タグ辞書 | DocumentService | 辞書を添えずに続行 | 同左（候補一覧は辞書に依存しない） | 追加 | 追加 |
| 4 | `AnalysisBffEndpoints` AI 分析（SSE） | AiAnalysisService | SSE `error` イベント | 同左（既に 200 で開始済み。NFR-06 の「AI のみ止める」） | 追加 | 追加 |
| 5 | `EdgeTypeDictionaryBffEndpoints` | GraphService | 502 | 502 | 追加 | 追加 |
| 6, 7 | `GraphBffEndpoints` 転送・照会 | GraphService | 502 | 502 | 追加 | 追加 |
| 8 | `PrivateNoteBffEndpoints` | DocumentService | 502 | 502 | 追加 | 追加 |
| 9 | `WikiBffEndpoints` | WikiService | 502 | 502 | 追加 | 追加 |
| 10, 11 | `ConversionBffEndpoints` 一覧・図版 | ConversionService | 502 | 502 | 追加 | 追加 |
| 12 | `DataSourceBffEndpoints` 一覧 | DataSourceService | 502 | 502 | 追加 | 追加 |
| 13 | `AuthzBffEndpoints`（Platform.Bff） | AuthorizationService | 502 | 502 | 追加 | 追加 |
| 14 | `McpClientBffEndpoints`（Platform.Bff） | McpServer | 502 | 502 | 追加 | 追加 |
| 15 | `NotificationBffEndpoints`（Platform.Bff） | NotificationService | 502 | 502 | 追加 | 追加 |
| 16 | `UserAdminBffEndpoints`（Platform.Bff） | AuthorizationService | 502 | 502 | 追加 | 追加 |
| 17〜19 | `DocumentBffEndpoints` 本文・詳細・一覧 | DocumentService | 502 / 404 秘匿 / 502 | 同左 | 既存（`DocumentReadFailureLog`） | 追加 |
| 20 | `DocumentIndexStatusGrpcClient` | IngestionService | 索引状態 `unknown` | 同左 | 既存（`DocumentReadFailureLog`） | 追加 |

除外（理由つき）:

- `UsageEventDispatcher`（常駐ドレイン。専用の計器 `usage.event.dispatch.total` を既に持つ）
- `Secrets/*`・`VaultKvClient`・`ExternalSecret*`・`SecretItemCatalog`・`SecretWriteRecordStore`（Vault・k8s・ファイルの扱いで、要求の後段ではない。各自がログ・監査を持つ）
- `SessionTokenRefresher`（IdP 更新。セッション側で扱う）、`ConfigBffEndpoints` drift-run（LogError 済み・内部専用）
- `BffScopeResolver` / `AuthzScopeGrpcClient`（認可の deny-by-default。存在秘匿のため応答を変えない。別系統として本件の母集合外）
- `src/ai-stock-trading`（submodule。本リポの母集合外）

## 方式

- 共有の報告口 `BffDownstreamFailure`（`Platform.Shared.Infrastructure/Foundation/Observability`）を新設し、全件から呼ぶ。
  - WARN 1 行。載せるのは**後段名・経路（ルートの型＝有界）・HTTP メソッド・失敗の種別・例外の型・縮退先**だけ。例外本文・メッセージ・検索語は載せない（NFR-28）。
  - 計器は 1 本 `bff.downstream.failure.total`（Meter `microservices-platform.bff`）。属性 `bff.downstream` / `bff.endpoint` / `bff.downstream.failure.kind` / `bff.fallback`。
  - **利用者の取り消し（`HttpContext.RequestAborted`）は数えない・ログしない。**
- `DocumentReadFailureLog.Folded` は WARN（既存カテゴリ）を保ち、計器だけ共有口から足す。
- 検索の不達は 502。画面（SC-02）は非 2xx を `ApiError` として `QueryState` の失敗表示（「検索に失敗しました。」＋再試行）へ出す既存の経路で扱える（フロントの変更不要。既存の試験で失敗表示を確認済み）。

## 受け入れ基準 → 試験

| 基準 | 試験 |
| --- | --- |
| 1. 不達・時間切れでログと計器が 1 件ずつ出る（代表複数） | `BffDownstreamFailureObservabilityTests`: 検索（不達）・検索（時間切れ）・Wiki・通知・属性値・文書一覧 |
| 2. 検索の不達が「該当なし」と区別できる | 同上: 不達で 502（200・0 件ではない）。0 件の正常応答は既存 `BffSearchEndpointTests` |
| 3. 利用者の取り消しは数えない | 同上: `RequestAborted` 済みの文脈で `Report` しても計器・ログが 0 件 |
| 4. 変異で赤 | ログ／計器の呼び出しを外すと 1 が赤（手元で確認。PR 本文に記録） |

## 検証

- `dotnet build` / `dotnet test` を `Platform.Bff.Tests` に絞って実行。`dotnet format --verify-no-changes`。
