---
title: 機能仕様書 — FR-10 利用状況・検索傾向・回答品質ダッシュボード
type: functional-spec
status: in-progress
created: 2026-07-03
updated: 2026-08-23
author: claude
---
<!-- trace:
ids: [FR-08, FR-10, FR-17, FR-18, FR-19, UC-05, SC-10]
adrs: [ADR-0002, ADR-0006, ADR-0034, ADR-0044, ADR-0054]
iadrs: [IADR-0011, IADR-0119, IADR-0265]
specs: [20260703_FR-10_usage-dashboard, 20260823_issue-443_llm-usage-metrics-and-pricing]
issues: [#443, #452, #504]
-->

# 機能仕様書: 利用状況・検索傾向・回答品質ダッシュボード

## 概要

運用・分析の担当者が、**利用状況**（検索・回答の件数推移）、**検索傾向**（よく検索される語）、
**回答品質**（👍/👎 の満足率）を 1 つのダッシュボードで把握できる。業務指標のドメイン集計は
`DashboardService`（専用マイクロサービス。DB per Service の方針による）が担い、回答品質は
フィードバック収集機能の `FeedbackService`（`/feedback/stats`）を単一の出所として BFF が集約する
（実装判断: 業務指標は専用サービスで集計し、回答品質は `FeedbackService` を単一の出所とする）。
可観測性基盤の Grafana（インフラ指標）とは責務が異なる（業務指標はアプリ側 API で提供）。

## データモデル（`UsageEvent`）

| 項目 | 型 | 説明 |
| --- | --- | --- |
| Id | Guid | 主キー |
| EventType | string(16) | `search`（検索実行）/ `answer`（AI 回答生成）。小文字正規化 |
| Query | string(512)? | 検索語（種別が `search` のときのみ保持。前後空白除去・小文字化。超過は切り詰め） |
| UserId | string(256) | 記録者（JWT の名前。テスト・開発は `anonymous`） |
| OccurredAt | DateTimeOffset | 発生時刻（UTC）。期間フィルタ・集計の基準 |

索引: `(OccurredAt, EventType)`（期間フィルタ・種別集計の効率化）。

## API（DashboardService）

| メソッド | パス | 認可 | 説明 |
| --- | --- | --- | --- |
| POST | `/dashboard/events` | 認証済み（`RequireAuthorization`。管理者限定にはしない） | 利用イベント記録。`EventType` 必須（`search`/`answer`）。201 |
| GET | `/dashboard/usage?days=N` | admin ＋ operator（**#544**） | 日次利用状況（日付 × 種別の件数） |
| GET | `/dashboard/trends?days=N&top=M` | admin ＋ operator（**#544**） | 検索傾向（検索語 × 件数の上位） |
| GET | `/dashboard/summary?days=N&top=M` | admin ＋ operator（**#544**） | 利用側サマリ（総件数・利用状況・検索傾向） |
| POST | `/dashboard/knowledge-health/observations` | 認証済み | ナレッジ健全性の観測値の報告（指標 1 つ分のスナップショット置換）。202 |
| GET | `/dashboard/knowledge-health` | **operator ＋ admin のみ** | ナレッジ健全性の指標（**件数のみ**） |

- `days`：既定 7・上限 90 にクランプ。`top`：既定 10・上限 50 にクランプ（無制限集計を防ぐ）。
- 集計は UTC 当日 00:00 を含む起点から現在まで。日付は UTC で丸める。

## ナレッジ健全性の指標

計画（可観測性・運用設計の当該節）が定める **7 指標**を集計する。**集計と表示の 4 つの規則を同時に満たす**
ことが条件であり、**1 つでも欠けると存在秘匿が崩れるため個別に緩めない**。

| 規則 | 実装 |
| --- | --- |
| 集計範囲は**全体**（閲覧者の権限で絞らない） | 閲覧者の属性を集計の述語に使わない |
| 閲覧は**運用者・システム管理者のみ** | `RequireRole(platform-admin, platform-operator)`。他は 403・無認証は 401 |
| **個人資料は集計から除外**（一律） | `doc_scope` が個人資料の観測値を集計前に落とす。**集合帰属で判定する**（「組織文書でない」で書くと、スコープ属性を持たない大多数が除外され指標が一斉に 0 になる） |
| **文書名を出さず件数のみ** | 応答は指標名と件数だけ。観測値の識別子は保持するが返さない |

- 指標の語彙は**閉じる**（未知の指標名は 400）。綴り違いが「0 件の指標」として静かに現れると、改善したと読めるためである。
- 観測値は**指標ごとのスナップショット置換**で受け取る（差分ではない）。解消の取り消し漏れが件数を恒久的に膨らませないため。
- 観測値の**生産者側の配線**（知識グラフ・文書カタログからの報告）と**画面表示**は本作業の対象外である。
- 閲覧は監査ログに記録する（件数のみ。対象の識別子は残さない）。

## API（BFF 集約）

| メソッド | パス | 認可 | 説明 |
| --- | --- | --- | --- |
| GET | `/bff/dashboard/summary?days=N&top=M` | admin ＋ operator（**#544**） | DashboardService の利用側サマリと FeedbackService の回答品質を集約し `DashboardSummaryDto` を返す |

- BFF は DashboardService（**admin ＋ operator**。#544）へ `Authorization` ヘッダを伝播する。
- 利用側サマリと回答品質は並行取得する（互いに独立）。後段が非 2xx ならそのステータスを透過する。
  いずれかの応答本文が null（欠損）なら 502（BadGateway）を返す。
- **期間の整合**: BFF は有効な `days`（既定 7・上限 90 にクランプ）を確定し、DashboardService（利用状況・検索傾向）と
  FeedbackService（満足率）の**双方に同じ `days`** を渡す。これにより「直近 N 日間の利用状況」と「同 N 日間の満足率」が
  同一期間で揃う。FeedbackService `GET /feedback/stats` は `days` 未指定なら従来どおり全期間（後方互換）。

## DTO（`Shared.Contracts`）

- `UsageEventRequest(EventType, Query?)`
- `UsagePointDto(Date, EventType, Count)`
- `SearchTrendDto(Term, Count)`
- `DashboardUsageDto(TotalSearches, TotalAnswers, UsageTrend, TopSearchTerms)` — DashboardService の利用側サマリ
- `DashboardSummaryDto(TotalSearches, TotalAnswers, UsageTrend, TopSearchTerms, Quality)` — BFF が回答品質を付加
- `UsageEventType`（`search` / `answer` の定数・検証・正規化）

ナレッジ健全性の DTO は**サービス間契約に置かず** `DashboardService.Api` 内に持つ。画面へ載せる段
（別作業）で契約プロジェクトへ昇格させる —— 契約の形はスナップショット検査の対象であり、
**使う側が居ない契約を先に固定しない**。

## バリデーション・例外

- `EventType` が `search`/`answer` 以外 → 400。
- 検索語は種別が `search` のときのみ集計対象（`answer` では保持しない）。空・空白のみは集計対象外。
- 集計 API を**管理系ロール以外**で呼ぶ → 403（**#544**。運用者は 200）。
- ナレッジ健全性の閲覧を**運用者・管理者以外**で呼ぶ → 403。**403 の本文に件数を一切載せない**。
- `days`/`top` は範囲外でもクランプして常に有効値で集計する（エラーにしない）。

## 非機能・セキュリティ

- 集計値（件数・満足率・検索語）のみを扱い、文書本文・回答本文は保持・返却しない。
- 満足率は FeedbackService を単一の出所とし、DashboardService へ複製しない（乖離防止）。集計期間は BFF が渡す `days` に追随する。
- 検索傾向の集計は、期間内の検索イベントから **`Query` 列のみを射影**して取得し、グルーピング・上位 N 件の絞り込みはアプリ側で行う（GroupBy＋集計はプロバイダ非依存とし、全エンティティのロードは避ける）。データ増加時は DB 側集計（`GROUP BY`＋`ORDER BY`＋`LIMIT`）への切替を検討する。
- DashboardService は専用 DB・専用 Dockerfile・compose 定義を持ち、独立してデプロイ・ロールバックできる（受け入れ基準④）。

## 対象外（別 PR）

- 画面（SC）実装本体・グラフ描画（UI）。
- 検索・回答経路からの利用イベント自動送信の配線。
- LLM API コストの可視化。**可観測性基盤（Grafana）側で実装済みである**——
  金額換算はゲートウェイが単価表を読んで行い、画面契約（BFF）には載せない。
- ナレッジ健全性指標の**画面表示**と、観測値の**生産者側の配線**（別作業）。
