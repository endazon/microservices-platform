---
title: IADR-0603 BFF が後段の不達・時間切れを畳むときは WARN と計器 bff.downstream.failure.total を 1 件ずつ出し、検索の不達は 200・0 件ではなく 502 で返す
type: impl-adr
status: Accepted
related_ids: [FR-03, NFR-06, NFR-19, NFR-28, IADR-0537, IADR-0417, IADR-0343]
author: claude
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md（FR-03・NFR-06・NFR-19・NFR-28）
related_specs:
  - ../specs/20261023_2104_bff-downstream-failure-observability.md
---

# IADR-0603: BFF の後段不達を黙って畳まない（#2104）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-23
- 決定者: claude（#2104。親 #2047 の指摘 M-7）

## 起点・関連

- 関連する計画書 ID: FR-03（横断検索）、NFR-19（可観測性「メトリクス・ログ・分散トレースを全サービスで収集」）、
  NFR-06（障害時の縮退運転「検索は継続、AI 回答のみ一時停止を許容」）、NFR-28（外部由来の文字列をログへ載せない）。
- 前提とする実装 ADR: [IADR-0537](./IADR-0537_document-list-pagination-offset-window-after-abac.md)（文書一覧は空へ畳まず 502）、
  [IADR-0417](./IADR-0417_retrieval-grpc-face-and-attribute-values.md) 決定 9（属性値候補の不達は空配列）、
  [IADR-0343](./IADR-0343_usage-event-producer-at-bff.md)（BFF の計器は Meter `microservices-platform.bff`・1 本のカウンタの属性で分ける）。
- 作業仕様書: [20261023_2104_bff-downstream-failure-observability](../specs/20261023_2104_bff-downstream-failure-observability.md)。

## コンテキストと課題

BFF の端点の多く（作業仕様書 §母集合の 20 件）が後段の不達・時間切れを `catch (… HttpRequestException or TaskCanceledException …)` や
gRPC の `RpcException(Unavailable)` で拾い、502・「200・0 件」・SSE の `error` へ畳んでいた。文書の読み取り（`DocumentReadFailureLog`。#1897）を除き
**ログも計器も出さない**。運用者には後段の不調が見えず、検索（`POST /bff/search`）では利用者にも「該当する文書はありません」と見えた。

## 決定

### 決定 1: 畳む箇所はすべて共有の報告口 `BffDownstreamFailure` を通し、WARN 1 行と計器 1 件を出す

- 置き場所は `Platform.Shared.Infrastructure/Foundation/Observability`（platform・knowledge の両 BFF モジュールが参照できる唯一の共有層）。
- 計器は **1 本** `bff.downstream.failure.total`（Counter・`{request}`・Meter `microservices-platform.bff`）。属性は
  `bff.downstream`（後段名＝名前付きクライアント名）・`bff.endpoint`（ルートの型）・`bff.downstream.failure.kind`
  （`unreachable` / `timeout` / `credential` / `error`）・`bff.fallback`（`502` / `404` / `empty` / `sse_error` / `omitted` / `unknown`）。
  **すべて有界**であり、生のパス・検索語・利用者 ID は載せない。
- WARN のカテゴリは `Platform.Bff.DownstreamFailure`。載せるのは上の属性と例外の**型名**だけで、例外本体・メッセージは載せない（NFR-28）。
- 既に WARN を出している `DocumentReadFailureLog` はカテゴリと文言を保ち（運用者の grep を壊さない）、計器だけ同じ 1 本へ足す。

### 決定 2: 検索の不達は 502 を返す（200・0 件へ畳まない）

文書一覧（IADR-0537）・グラフと同じ形に揃える。画面（SC-02）は非 2xx を既存の失敗表示（「検索に失敗しました。」と再試行）で扱うため、
フロントの変更は要らない。0 件の正常応答（空クエリ・許可スコープ無し）は早期 return で別に返しており、502 と混ざらない。

### 決定 3: 意図された縮退は応答を変えず、ログと計器だけ足す

- 属性値候補（gRPC 不達 → 空候補）: IADR-0417 決定 9 の縮退を維持する（候補の欠落で検索そのものは止まらない）。
- タグ辞書（不達 → 辞書を添えない）: 候補一覧は辞書に依存しない。
- AI 分析（SSE。既に 200 で開始済み → `error` イベント）: NFR-06 の「AI 回答のみ停止」。
- 文書の詳細（不達 → 404 秘匿）・索引の状態（不達 → `unknown`）: 既存の判断どおり。

### 決定 4: 利用者の取り消しは数えない

`HttpContext.RequestAborted` が取り消されていれば、報告口はログも計器も出さずに返る。各 catch の `!ct.IsCancellationRequested` と二重に守る
（取り消しを数えると、利用者が戻る操作をしただけで計器が伸び、後段の不調と区別できなくなる）。

## 母集合

作業仕様書 §母集合（走査コマンド・20 件の表・除外の理由）を正とする。issue 本文の「15 件」は走査し直した結果と一致しない（変種の述語
`IsTransient` / `IsTransportFailure` / `IsRetrievalUnreachable` と gRPC の索引状態を含めると 20 件）。

## 結果

- 良い影響: 後段の不調が `bff_downstream_failure_total` と WARN で見える（後段名・端点で分かれる）。検索の不達が「該当なし」と区別できる。
- 悪い影響 / トレードオフ: 検索は後段の停止中に 502 を返す（従前は 0 件として「動いて見えた」）。NFR-06 の「検索は継続」は
  検索サービス以外（LLM 等）の停止で検索を止めないことであり、検索サービス自身の停止を 0 件で隠すことではない。
- アラートの閾値は本決定に含めない（系列は出るようになった。閾値は運用の段で決める）。
- 認可スコープ解決（`BffScopeResolver` / `AuthzScopeGrpcClient`）の不達は deny-by-default（存在秘匿）であり本決定の対象外。

## 検証

| # | 内容 | 結果 | 根拠 |
| --- | --- | --- | --- |
| 1 | 検索（不達・時間切れ）・Wiki・通知・文書一覧（gRPC 不達）で WARN と計器が 1 件ずつ | 通った | `BffDownstreamFailureObservabilityTests` |
| 2 | 検索の不達は 502、正常応答は 200 で計器・WARN なし | 通った | 同上 |
| 3 | `RequestAborted` 済みの文脈で報告しても 0 件（陽性対照つき） | 通った | 同上 |
| 4 | 変異: 検索の報告呼び出しを外す → 2 件赤／計器の記録を外す → 5 件赤／WARN を外す → 5 件赤／取り消しの判定を外す → 1 件赤／`DocumentReadFailureLog` の計器を外す → 1 件赤 | 赤を確認 | 手元実行（2026-10-23） |
| 5 | `Platform.Bff.Tests` 全件 | 1071 通過・1 skip | 手元実行（2026-10-23） |
