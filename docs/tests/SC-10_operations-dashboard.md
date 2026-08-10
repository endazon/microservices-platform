---
title: SC-10 運用ダッシュボード テスト仕様書
type: test-spec
status: completed
related_ids:
  - SC-10
  - UC-05
  - FR-10
  - NFR
  - IADR-0009
  - IADR-0035
  - IADR-0129
author: claude
created: 2026-07-08
updated: 2026-08-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/06_technical/05_observability-ops.md"
related_specs:
  - "../screens/SC-10_operations-dashboard.md"
  - "../adr/IADR-0129_sc09-11-admin-ops-screen-composition.md"
  - "../adr/IADR-0035_frontend-role-based-nav-and-existence-hiding.md"
  - "../adr/IADR-0011_dashboard-service-usage-aggregation.md"
  - "../specs/20260805_issue-504_sc09-11-admin-ops-screens.md"
---

# テスト仕様書: SC-10 運用ダッシュボード

> **［2026-08-05 / #504］新スタックでの再実装に合わせて画面側を全面改訂した。**
> **改訂にあたり §バックエンド（BFF・xUnit）の節を新設した**——改訂前の本書は「対象外: BFF
> `/bff/dashboard/summary` のサーバ側テスト（既存）」と 1 行で片付けており、**実在する
> `DashboardBffEndpointTests` の 6 ケースがどこにも写像されていなかった**。
> **画面の権限は片側だけを固定しても実効境界にならない**（#503 が SC-05〜07 でバックエンドの節を
> 落とし、#510 として起票された先例がある）。とくに本画面は**計画と実装で閲覧ロールが食い違って
> おり**、その根拠が API 側にあるため、両側を並べて読めることに意味がある。

対象（画面）: `src/knowledge/frontend/src/features/sc10-operations/`
テスト: `opsTools.test.ts`（純関数）／ `OperationsDashboardPage.test.tsx`（Vitest + Testing Library。
画面 ＋ **アクセス制御**）／ 導線は `src/knowledge/frontend/src/features/opsFlow.test.tsx`／
E2E は `src/platform/frontend/e2e/sc10-operations.smoke.spec.ts`

対象（API）: `src/platform/backend/Bff/Platform.Bff.Tests/DashboardBffEndpointTests.cs` ／
`src/knowledge/backend/Services/DashboardService/tests/DashboardService.Api.Tests/DashboardEndpointTests.cs`

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: SC-10 ／ ユースケース（UC）: **UC-05** ／
  機能要求（FR）: **FR-10**（利用状況・検索傾向・回答品質の可視化）＋ **非機能要件（運用・可観測性）**
- 受け入れ基準の所在: issue #504 §受け入れ基準 ／ 作業仕様書
  [20260805_issue-504](../specs/20260805_issue-504_sc09-11-admin-ops-screens.md) §受け入れ基準

## 計画の要素 → 実装／テストの対応

| 計画 §SC-10 の要素 | テスト |
| --- | --- |
| KPI カード（**SLO 達成率**） | **実装しない**（契約の不在）。`renders only the three KPI cards the contract can fill` |
| KPI カード（利用状況〔人/日〕） | **部分**（件数を出す）。同上（KPI カードの集合を固定する） |
| KPI カード（**LLM コスト**） | **実装しない**（契約の不在）。同上 |
| 外部ツールリンク（Grafana / Kiali / Jaeger・Tempo） | `renders only the observability tools that runtime config injects` ／ 純関数 P1〜P4 |
| 構成ビューア（SC-11）への導線 | `always offers the link to SC-11 for anyone who can open this screen` ／ 導線テスト A |
| **ナレッジ健全性**（4 KPI ＋ 辺の型の使用件数 ＋ フォールバック警告 ＋ 注記） | **実装しない**（着手保留・[[IADR-0119]]）。`does not render the knowledge-health section` |
| アクセス制御（計画は運用者・管理者） | **管理者 ＋ 運用者**（**#544** で計画と一致）。`grants access to platform-admin` / `grants access to platform-operator` / `hides existence (NotFound) for a plain user` |

## FR-10 → テストの写像

| FR-10 の要素 | テスト |
| --- | --- |
| 利用状況の可視化 | `shows the usage, trend and answer-quality summary`（総数 ＋ 日次一覧） |
| 検索傾向の可視化 | 同上（上位語の一覧） |
| 回答品質の可視化 | 同上（満足率 ＋ 👍/👎 の内訳） |
| 期間の切替 | `starts at seven days and sends the selected period to the API` |

## テストケース（画面）

| # | 観点 | 起点 | 検証内容 |
| --- | --- | --- | --- |
| 1 | サマリ表示 | FR-10 | 検索総数・回答総数・満足率と、日次・上位語の 2 表。`?days=7` を送る |
| 2 | 集計期間 | 契約（`?days=`） | 既定 7。選択で `?days=30` を送る（キャッシュキーに期間を含む） |
| 3 | **未知のイベント種別** | 契約の 2 値 | 生値をそのまま出す（`—`・「不明」へ丸めない） |
| 4 | 0 件 | — | 「期間内の利用はありません。」「検索傾向はまだありません。」 |
| 5 | **403 の中立化** | [[IADR-0009]] / [[IADR-0129]] 決定 3 | 「運用ダッシュボードは利用できません。」（`role="alert"` を出さない） |
| 6 | **404 の中立化** | 同上 | **5 と同じ文言**（文言から権限の有無を読ませない） |
| 7 | **5xx は中立化しない** | 同上 | `role="alert"` で障害として出す（運用者に見逃させない） |
| 8 | 外部ツール | [[IADR-0121]] 決定 3 | 実行時 config が注入したものだけ描く（Kiali 未設定なら出さない） |
| 9 | 未設定 | — | 「外部ツールの導線は未設定です。」 |
| 10 | SC-11 導線 | 遷移図 `SC10 --> SC11` | `/admin/config-viewer` へのリンク。**権限で出し分けない**（[[IADR-0129]] 決定 4） |
| 11 | **着手保留**（実装しない要素） | [[IADR-0119]] | ナレッジ健全性の語（節見出し・4 KPI・辺の型・フォールバック・個人資料の注記）が無い。**先にサマリが在ることを確かめてから**無いことを見る |
| 12 | **契約の不在**（実装しない要素） | 画面仕様書 §hi-fi 対応 #3・#5 | **KPI カードの見出しの集合**が 3 枚に固定される（「SLO」の語は副題にも出るため、テキスト検索ではなく**カードが在るか**で見る） |
| 13 | ロケール `en` | ADR-0031 | 見出しが英語で描画される |

## アクセス制御・存在秘匿（画面）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| A1 | 許可 | `platform-admin` は開ける |
| A2 | **許可**（**#544**） | **`platform-operator` も開ける**（計画 §SC-10。裁定 Q19 / Q28。従前は `NotFound` だった） |
| A2-b | **存在秘匿** | **一般利用者**は **`NotFound`**。**BFF を呼ばない**（**広げすぎない**の側） |
| A3 | **markup 一致** | 権限による秘匿の描画が `foundation/ui/NotFound`（＝不在）と**同じ markup**（#490 の作法） |
| A4 | ナビ | `requiresAnyRole: [platform-admin, platform-operator]`・`group: 'ops'`（**#544**。ルートゲートと揃える） |

> **［2026-08-09 / #544］A2 は「差異の固定」から「一致の固定」へ反転した。**
> 従前は計画との差異（計画=運用者・管理者／実装=管理者のみ）を固定していたが、
> **裁定 Q19 / Q28 で計画が正となり、3 層を同時に広げて一致させた**（[[IADR-0129]] 決定 4 の追記）。
> 予告どおり `opsFlow.test.tsx` の 2 本目も同時に反転してある
> （`lets an operator reach both SC-10 and SC-11 directly`）。
>
> **A2-b を対で置いたのは、「広げる」作業が検査にならないためである** ——
> 権限を全開にしても A2 は緑のまま通る（変異試験で確認した）。

## 純関数（`opsTools.test.ts`）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| P1 | 値集合 | 計画が挙げる **3 件**（`grafana` / `kiali` / `tracing`）と完全一致する |
| P2 | 表示 | 表示名（固有名詞・翻訳しない）と説明（翻訳する）が計画の記述どおり。**並び順も固定** |
| P3 | 絞り込み | URL が未注入のツールを落とす |
| P4 | 空 | 何も設定されていなければ空配列 |

## 導線（`opsFlow.test.tsx`）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| A | SC-10 → SC-11 | 「構成ビューア →」で構成ビューアへ遷移し、構成バージョンが出る（**2 ルートを 1 本のルータへ載せる**） |
| B | 運用者の到達 | 運用者は **SC-10 にも SC-11 にも**直接到達できる（**#544** で反転。`lets an operator reach both SC-10 and SC-11 directly`） |

## バックエンド（BFF・xUnit）

対象: `src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/DashboardBffEndpoints.cs`
テスト: `src/platform/backend/Bff/Platform.Bff.Tests/DashboardBffEndpointTests.cs`

| # | 観点 | 起点 | 検証内容 | ケース |
| --- | --- | --- | --- | --- |
| 1 | 集約 | FR-10 | `DashboardService`（利用状況・検索傾向）と `FeedbackService`（回答品質）を 1 応答へ集約する | `GetSummary_AggregatesUsageAndQuality` |
| 2 | 資格情報の伝播 | [[IADR-0011]] | 後段の**管理系ロール要求**（admin ＋ operator。**#544**）を満たすため `Authorization` を引き継ぐ | `GetSummary_PropagatesAuthorizationHeader` |
| 3 | **ロール制限**（広げすぎない） | [[IADR-0011]] | **管理系ロール**（admin ＋ operator）が無ければ **403**（**#544** で名称と趣旨を実態へ） | `GetSummary_WithoutPrivilegedRole_Returns403` |
| 3-b | **ロール開放**（**#544**） | 計画 §SC-10・裁定 Q19 / Q28 | **運用者は 200**。**この対が無いと「広げる」作業は検査にならない**（権限を全開にしても 3 は緑のまま） | `GetSummary_AsOperator_IsAllowed` |
| 4 | 後段障害 | — | 後段の非成功ステータスをそのまま伝播し、空サマリへ縮退させない | `GetSummary_WhenDashboardFails_PropagatesStatus` ／ `GetSummary_WhenFeedbackStatsFails_PropagatesStatus` |
| 5 | 本文欠落 | — | 後段が本文を返さなければ 502 | `GetSummary_WhenDashboardBodyNull_Returns502` |

集計そのもの（期間の丸め・日次集計・上位語）は `DashboardService` 側で検証する
（`src/knowledge/backend/Services/DashboardService/tests/DashboardService.Api.Tests/DashboardEndpointTests.cs`）。

## E2E（Playwright）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| E1 | ルートの実在 ＋ 認証ガード | 未認証で `/admin/ops` を開くと `/login` へ誘導される |

## テストデータ

- ロール別のダミー `User`（`access_token` の `realm_access.roles`。`renderUnitRoute` が生成する）。
- `DashboardSummaryDto` のダミー（`totalSearches` / `totalAnswers` / `usageTrend` / `topSearchTerms` / `quality`）。
- 実行時 config（`window.__APP_CONFIG__.opsLinks`）。**各テストでキャッシュを破棄する**
  （`resetAppConfigCache()`。持ち越すと前のテストの config を次が読む）。

## 実行

- `pnpm run test -- knowledge/frontend/src/features/sc10-operations`

  **件数は母集合を明記する**。下表は上記コマンドの出力と突き合わせた実測である。

  | 括り | 本書の観点行 | `it` 宣言 | vitest の `Tests` |
  | --- | --- | --- | --- |
  | 純関数（`opsTools.test.ts`） | **4**（P1〜P4） | **4** | **4** |
  | 画面（`OperationsDashboardPage.test.tsx` の 1 つ目の `describe`） | **13** | **13** | **13** |
  | アクセス（同 2 つ目の `describe`） | **4**（A1〜A4） | **4** | **4** |
  | **合計** | **21** | **21** | **21**（2 ファイル） |

  本画面は 3 つの数え方が**たまたま一致する**（`it.each` を使っておらず、観点とテストが 1 対 1 である）。
  **一致するとは限らない**ため、SC-09 / SC-11 と同じ形で母集合を明記する。
- `pnpm run test -- knowledge/frontend/src/features/opsFlow.test.tsx`（導線）
- `pnpm run test:coverage`（カバレッジ・ラチェット維持）
- `pnpm --filter @platform/frontend run test:e2e`

## 未決事項

- 契約の不在 3 件（SLO・LLM コスト・一意利用者数）は
  `feedback/20260805_sc09-11-admin-ops-contract-gaps.md`。裁定までテストも書かない。
  **閲覧ロールの差異（提案 7）は [2026-08-09 / #544] で解決した**（計画が正。3 層を広げて一致）。
- ナレッジ健全性節は [[IADR-0119]] の保留解除待ち。
  **［2026-08-07 / #586］ADR-0033・0034・0035 は `Accepted` へ移り保留は解除された**
  （planning `3e58b97` = PR planning#244〔裁定依頼 planning#237〕）。**待っていた条件は成立している。**
  節を実装するか、したがって観点を書くかは **#504 / #452** が判断する（#586 は pin 更新と事実の追随に限る）。
  画面仕様書側の対の追記は [SC-10](../screens/SC-10_operations-dashboard.md) §実装しない要素の理由 (a)・§未決事項 5。
