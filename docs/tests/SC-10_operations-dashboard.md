---
title: SC-10 運用ダッシュボード テスト仕様書
type: test-spec
status: completed
created: 2026-07-08
updated: 2026-08-29
author: claude
---
<!-- trace:
ids: [FR-10, SC-05, SC-06, SC-07, SC-09, SC-10, SC-11, UC-05]
adrs: [ADR-0031, ADR-0033, ADR-0034, ADR-0035]
iadrs: [IADR-0009, IADR-0011, IADR-0035, IADR-0119, IADR-0121, IADR-0129, IADR-0265, IADR-0299]
specs: [20260805_issue-504_sc09-11-admin-ops-screens, 20260829_issue-443_knowledge-health-producer]
issues: [#443, #452, #490, #503, #504, #510, #544, #586, planning#237, planning#244]
-->

# テスト仕様書: 運用ダッシュボード

> **［2026-08-05 / #504］新スタックでの再実装に合わせて画面側を全面改訂した。**
> **改訂にあたり §バックエンド（BFF・xUnit）の節を新設した**——改訂前の本書は「対象外: BFF
> `/bff/dashboard/summary` のサーバ側テスト（既存）」と 1 行で片付けており、**実在する
> `DashboardBffEndpointTests` の 6 ケースがどこにも写像されていなかった**。
> **画面の権限は片側だけを固定しても実効境界にならない**（#503 が文書管理・データソース管理・変換ジョブの各画面でバックエンドの節を
> 落とし、#510 として起票された先例がある）。とくに本画面は**計画と実装で閲覧ロールが食い違って
> おり**、その根拠が API 側にあるため、両側を並べて読めることに意味がある。

対象（画面）: `src/knowledge/frontend/src/features/sc10-operations/`
テスト: `opsTools.test.ts`（純関数）／ `OperationsDashboardPage.test.tsx`（Vitest + Testing Library。
画面 ＋ **アクセス制御**）／ 導線は `src/knowledge/frontend/src/features/opsFlow.test.tsx`／
E2E は `src/platform/frontend/e2e/sc10-operations.smoke.spec.ts`

対象（API）: `src/platform/backend/Bff/Platform.Bff.Tests/DashboardBffEndpointTests.cs` ／
`src/knowledge/backend/Services/DashboardService/Tests/DashboardEndpointTests.cs`

## 起点となる計画書（トレーサビリティ）

- 画面: 運用ダッシュボード ／ ユースケース: **ABAC 権限を管理する** ／
  機能要求: **利用状況・検索傾向・回答品質の可視化** ＋ **非機能要件（運用・可観測性）**
- 受け入れ基準の所在: issue #504 §受け入れ基準 ／ 作業仕様書
  仕様書: 管理者設定・運用ダッシュボード・構成ビューアの新スタックでの再実装 §受け入れ基準

## 計画の要素 → 実装／テストの対応

| 計画側の運用ダッシュボードの要素 | テスト |
| --- | --- |
| KPI カード（**SLO 達成率**） | **実装しない**（契約の不在）。`renders only the three KPI cards the contract can fill` |
| KPI カード（利用状況〔人/日〕） | **部分**（件数を出す）。同上（KPI カードの集合を固定する） |
| KPI カード（**LLM コスト**） | **実装しない**（契約の不在）。同上 |
| 外部ツールリンク（Grafana / Kiali / Jaeger・Tempo） | `renders only the observability tools that runtime config injects` ／ 純関数 P1〜P4 |
| 構成ビューアへの導線 | `always offers the link to SC-11 for anyone who can open this screen`（構成ビューアへのリンク）／ 導線テスト A |
| **ナレッジ健全性**（4 KPI ＋ 辺の型の使用件数 ＋ フォールバック警告 ＋ 注記） | **実装しない**。理由は 2 本ある——①関係探索・AI 提案の着手保留 ②**［2026-08-29 / #443］7 指標中 6 指標に観測値の生産者が無く、0 件が「問題なし」と読める**（未計測を健全と表示することになる）。`does not render the knowledge-health section` |
| アクセス制御（計画は運用者・管理者） | **管理者 ＋ 運用者**（**#544** で計画と一致）。`grants access to platform-admin` / `grants access to platform-operator` / `hides existence (NotFound) for a plain user` |

## 機能要求 → テストの写像

| 利用状況ダッシュボードの要素 | テスト |
| --- | --- |
| 利用状況の可視化 | `shows the usage, trend and answer-quality summary`（総数 ＋ 日次一覧） |
| 検索傾向の可視化 | 同上（上位語の一覧） |
| 回答品質の可視化 | 同上（満足率 ＋ 👍/👎 の内訳） |
| 期間の切替 | `starts at seven days and sends the selected period to the API` |

## テストケース（画面）

| # | 観点 | 起点 | 検証内容 |
| --- | --- | --- | --- |
| 1 | サマリ表示 | —| 検索総数・回答総数・満足率と、日次・上位語の 2 表。`?days=7` を送る |
| 2 | 集計期間 | 契約（`?days=`） | 既定 7。選択で `?days=30` を送る（キャッシュキーに期間を含む） |
| 3 | **未知のイベント種別** | 契約の 2 値 | 生値をそのまま出す（`—`・「不明」へ丸めない） |
| 4 | 0 件 | — | 「期間内の利用はありません。」「検索傾向はまだありません。」 |
| 5 | **403 の中立化** | 権限外は 404 とする存在秘匿 / 管理画面 3 種の再実装（決定 3） | 「運用ダッシュボードは利用できません。」（`role="alert"` を出さない） |
| 6 | **404 の中立化** | 同上 | **5 と同じ文言**（文言から権限の有無を読ませない） |
| 7 | **5xx は中立化しない** | 同上 | `role="alert"` で障害として出す（運用者に見逃させない） |
| 8 | 外部ツール | SPA からの到達経路の実装判断 | 実行時 config が注入したものだけ描く（Kiali 未設定なら出さない） |
| 9 | 未設定 | — | 「外部ツールの導線は未設定です。」 |
| 10 | 構成ビューアへの導線 | 遷移図 `SC10 --> SC11` | `/admin/config-viewer` へのリンク。**権限で出し分けない**（管理画面 3 種の再実装・決定 4） |
| 11 | **着手保留**（実装しない要素） | 関係探索・AI 提案の着手保留 | ナレッジ健全性の語（節見出し・4 KPI・辺の型・フォールバック・個人資料の注記）が無い。**先にサマリが在ることを確かめてから**無いことを見る |
| 12 | **契約の不在**（実装しない要素） | 画面仕様書 §hi-fi 対応 #3・#5 | **KPI カードの見出しの集合**が 3 枚に固定される（「SLO」の語は副題にも出るため、テキスト検索ではなく**カードが在るか**で見る） |
| 13 | ロケール `en` | —| 見出しが英語で描画される |

## アクセス制御・存在秘匿（画面）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| A1 | 許可 | `platform-admin` は開ける |
| A2 | **許可**（**#544**） | **`platform-operator` も開ける**（計画側の運用ダッシュボード。裁定 Q19 / Q28。従前は `NotFound` だった） |
| A2-b | **存在秘匿** | **一般利用者**は **`NotFound`**。**BFF を呼ばない**（**広げすぎない**の側） |
| A3 | **markup 一致** | 権限による秘匿の描画が `foundation/ui/NotFound`（＝不在）と**同じ markup**（#490 の作法） |
| A4 | ナビ | `requiresAnyRole: [platform-admin, platform-operator]`・`group: 'ops'`（**#544**。ルートゲートと揃える） |

> **［2026-08-09 / #544］A2 は「差異の固定」から「一致の固定」へ反転した。**
> 従前は計画との差異（計画=運用者・管理者／実装=管理者のみ）を固定していたが、
> **裁定 Q19 / Q28 で計画が正となり、3 層を同時に広げて一致させた**（管理画面 3 種の再実装・決定 4 の追記）。
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
| A | 運用ダッシュボード → 構成ビューア | 「構成ビューア →」で構成ビューアへ遷移し、構成バージョンが出る（**2 ルートを 1 本のルータへ載せる**） |
| B | 運用者の到達 | 運用者は **運用ダッシュボードにも構成ビューアにも**直接到達できる（**#544** で反転。`lets an operator reach both SC-10 and SC-11 directly`） |

## バックエンド（BFF・xUnit）

対象: `src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/DashboardBffEndpoints.cs`
テスト: `src/platform/backend/Bff/Platform.Bff.Tests/DashboardBffEndpointTests.cs`

| # | 観点 | 起点 | 検証内容 | ケース |
| --- | --- | --- | --- | --- |
| 1 | 集約 | —| `DashboardService`（利用状況・検索傾向）と `FeedbackService`（回答品質）を 1 応答へ集約する | `GetSummary_AggregatesUsageAndQuality` |
| 2 | 資格情報の伝播 | 業務指標ダッシュボードの集計方針 | 後段の**管理系ロール要求**（admin ＋ operator。**#544**）を満たすため `Authorization` を引き継ぐ | `GetSummary_PropagatesAuthorizationHeader` |
| 3 | **ロール制限**（広げすぎない） | 同上 | **管理系ロール**（admin ＋ operator）が無ければ **403**（**#544** で名称と趣旨を実態へ） | `GetSummary_WithoutPrivilegedRole_Returns403` |
| 3-b | **ロール開放**（**#544**） | 計画側の運用ダッシュボード・裁定 Q19 / Q28 | **運用者は 200**。**この対が無いと「広げる」作業は検査にならない**（権限を全開にしても 3 は緑のまま） | `GetSummary_AsOperator_IsAllowed` |
| 4 | 後段障害 | — | 後段の非成功ステータスをそのまま伝播し、空サマリへ縮退させない | `GetSummary_WhenDashboardFails_PropagatesStatus` ／ `GetSummary_WhenFeedbackStatsFails_PropagatesStatus` |
| 5 | 本文欠落 | — | 後段が本文を返さなければ 502 | `GetSummary_WhenDashboardBodyNull_Returns502` |

集計そのもの（期間の丸め・日次集計・上位語）は `DashboardService` 側で検証する
（`src/knowledge/backend/Services/DashboardService/Tests/DashboardEndpointTests.cs`）。

## E2E（Playwright）

| # | 観点 | 検証内容 |
| --- | --- | --- |
| E1 | 認証ガード（**未認証の導線だけ**） | 未認証で `/admin/ops` を開くと `/login` へ誘導される。🔴 **ルートの実在は測っていない**（未知のパスの受け皿が認証ガード配下に居るため区別できない）。**ルートの実在はルート木の組み立てを走査する単体テストが固定する** |

## テストデータ

- ロール別のダミー利用者（セッション身元の `roles` 配列。`renderUnitRoute` が生成する）。
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
  **一致するとは限らない**ため、管理者設定画面・構成ビューアと同じ形で母集合を明記する。
- `pnpm run test -- knowledge/frontend/src/features/opsFlow.test.tsx`（導線）
- `pnpm run test:coverage`（カバレッジ・ラチェット維持）
- `pnpm --filter @platform/frontend run test:e2e`

## 未決事項

- 契約の不在 3 件（SLO・LLM コスト・一意利用者数）は
  `feedback/20260805_sc09-11-admin-ops-contract-gaps.md`。裁定までテストも書かない。
  **閲覧ロールの差異（提案 7）は [2026-08-09 / #544] で解決した**（計画が正。3 層を広げて一致）。
- ナレッジ健全性節は、関係探索・AI 提案の着手保留の解除待ちである。
  **［2026-08-07 / #586］ナレッジグラフのデータモデル・グラフ探索での ABAC 強制・GraphRAG 検索戦略の
  各計画 ADR は `Accepted` へ移り、保留は解除された**（計画リポジトリのコミット `3e58b97`）。
  **待っていた条件は成立している。**
  節を実装するか、したがって観点を書くかは **#504 / #452** が判断する（#586 は pin 更新と事実の追随に限る）。
  画面仕様書側の対の追記は [画面仕様書](../screens/SC-10_operations-dashboard.md) §実装しない要素の理由 (a)・§未決事項 5。

<!-- trace-table:
row1: FR-10
row2: ADR-0031
row3: FR-10
-->
