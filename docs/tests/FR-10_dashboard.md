---
title: テスト仕様書 — FR-10 利用状況・検索傾向・回答品質ダッシュボード
type: test-spec
status: in-progress
created: 2026-07-03
updated: 2026-08-29
author: claude
---
<!-- trace:
ids: [FR-10, FR-17, FR-18, FR-19, UC-05, SC-10]
adrs: [ADR-0002, ADR-0006, ADR-0033, ADR-0034, ADR-0044, ADR-0054]
iadrs: [IADR-0011, IADR-0265, IADR-0299]
specs: [20260703_FR-10_usage-dashboard, 20260823_issue-443_llm-usage-metrics-and-pricing, 20260829_issue-443_knowledge-health-producer]
issues: [#443]
-->

# テスト仕様書: 利用状況・検索傾向・回答品質ダッシュボード

## 対象・方針

- `DashboardService.Api.Tests`：記録・バリデーション・集計・認可・ヘルス。集計はグローバルのため、各テストは
  専用の InMemory DB（`TestWebApplicationFactory` を per-test 生成）で独立させる。
- `KnowledgePlatform.Bff.Tests`：BFF `/bff/dashboard/summary` の集約・資格情報伝播・認可。DashboardService と
  FeedbackService はスタブハンドラで差し替える。認可は `TestAuthHandler`（既定 `platform-admin`）で検証する。

## テストケース

| ID | 対象 | 内容 | 期待 |
| --- | --- | --- | --- |
| T-01 | DashboardService | `search`（検索語つき）を記録 | 201 |
| T-02 | DashboardService | `ANSWER`（大文字）を記録 | 201（正規化） |
| T-03 | DashboardService | 不正な `EventType`（`click`） | 400 |
| T-04 | DashboardService | 検索×2・回答×1 → 利用状況集計 | search 合計 2・answer 合計 1 |
| T-05 | DashboardService | 同一語×3・別語×1 → 検索傾向 | 件数降順、先頭が該当語（count=3） |
| T-06 | DashboardService | `Foo`/` foo `/`FOO` → 検索傾向 | 1 語 `foo`（count=3、正規化） |
| T-07 | DashboardService | サマリ集約 | 総件数・利用状況・検索傾向が整合 |
| T-08 | DashboardService | **管理系ロール以外**で `/dashboard/{usage,trends,summary}` | 403（**#544** で名称と趣旨を実態へ）／**運用者は 200**（`Aggregates_AsOperator_AreAllowed`） |
| T-09 | DashboardService | 非管理ロールで `POST /dashboard/events` | 201（記録は開放） |
| T-10 | BFF | `/bff/dashboard/summary` 集約 | 利用状況・検索傾向・回答品質を集約して返す |
| T-11 | BFF | **管理系ロール以外**で `/bff/dashboard/summary` | 403（**#544** で名称と趣旨を実態へ）／**運用者は 200**（`GetSummary_AsOperator_IsAllowed`） |
| T-12 | BFF | DashboardService が 5xx | 後段ステータスを透過（500） |
| T-13 | BFF | FeedbackService（満足率）が 5xx | 後段ステータスを透過（503） |
| T-14 | BFF | 後段が 2xx でも本文が null | 502（BadGateway） |
| T-15 | FeedbackService | `GET /feedback/stats?days=1` | 期間内（当日）投入分を含めて集計 |
| — | BFF | 資格情報伝播 | `Authorization` を DashboardService へ伝播 |
| — | 両サービス | `/health/live` | 200 |

### ナレッジ健全性の指標

**4 つの規則（全体集計・ロール限定・個人資料除外・件数のみ）を、それぞれ独立に固定する。**
1 つでも欠けると存在秘匿が崩れるため、まとめて 1 本のテストにしない。

> **観測値の受け口は `/internal/...` にあり、認可を課さない。** 生産者は利用者 JWT を持たない
> 定期処理であり、統制は mTLS ＋ ネットワーク分離である（**認可ではない**）。
> **統制が働いていること自体はここでは測れない** —— 機械検査が押さえているのは
> 「host 公開しない」「Service は既定 ClusterIP」の 2 点だけである。

| ID | 対象 | 内容 | 期待 |
| --- | --- | --- | --- |
| T-20 | DashboardService | 観測値を報告し集計を引く | 指標ごとの件数が返る |
| T-21 | DashboardService | 個人資料を含む観測値（綴りの大小を含む） | **個人資料は一律で除外**される |
| T-22 | DashboardService | **スコープ属性を持たない観測値**（陽性対照） | **集計に含まれる**（「組織文書でない」で書いた実装をここで落とす） |
| T-23 | DashboardService | 運用者 / 管理者で閲覧 | 200 |
| T-24 | DashboardService | 運用者・管理者以外で閲覧 | **403**。かつ**本文に指標名も件数も現れない** |
| T-25 | DashboardService | 応答本文の検査（否定形） | **文書の識別子を含まない**（件数のみ） |
| T-26 | DashboardService | 観測値が 1 件も無い | **7 指標すべてが 0 件**で返る（欠落と 0 を混同させない） |
| T-27 | DashboardService | 同じ指標を 2 回報告 | **スナップショット置換**（後の報告で置き換わる） |
| T-28 | DashboardService | 未知の指標名 | 400（語彙は閉じる） |
| T-29 | DashboardService | **受け口のパスが生産者側の宣言と同値である** | 同値。かつそのパスで 202（サービスを跨ぐため定数を共有できない。一致を守る機械はこのテストと生産者側のテストだけ） |
| T-30 | DashboardService | 受け口の終端メタデータ | **認可メタデータ無し**（生産者は利用者 JWT を持たない定期処理）／**OpenAPI 記述から除外**（内部 API） |
| T-31 | DashboardService | 閲覧側の終端メタデータ（回帰対照） | **認可メタデータあり**（受け口を無認証にしても閲覧側は緩めない） |
| T-32 | DashboardService | **生産者が組み立てる生の JSON** で報告 | 束縛できる（`subjectKey` / `docScope` の綴りと大小が噛み合う。型では守られない） |

### LLM 利用実績（単価表・金額換算）

> **ID は節ごとに独立している**（本節の T-30 以降は上の健全性の T-30 とは別物である）。
> 番号を振り直すと、既存の受け入れ基準の対応表とテスト側のコメントが一斉に指す先を失う。

| ID | 対象 | 内容 | 期待 |
| --- | --- | --- | --- |
| T-30 | LlmGateway | 区間の内側の時刻 | 当該区間の単価 |
| T-31 | LlmGateway | **切替時刻ちょうど** | **新しい単価**（開始は含む・終了は含まない） |
| T-32 | LlmGateway | 切替の直前 1 tick | 旧い単価 |
| T-33 | LlmGateway | 改定をまたぐ 2 回の呼び出し | **呼び出し時点ごとの単価**で按分される |
| T-34 | LlmGateway | どの区間にも該当しない時刻 / 未登録モデル | **期間外 / 該当なし**（**0 円にしない**） |
| T-35 | LlmGateway | 入力と出力 | **別々の単価**で按分（百万トークンあたり） |
| T-36 | LlmGateway | 区間の重なり・空区間・負値・空項目 | **起動時に落とす** |
| T-37 | LlmGateway | 送信が成立した補完 | トークンと金額が**用途別・モデル別**に計上される |
| T-38 | LlmGateway | 単価を解決できない補完 | **金額を記録せず**、解決漏れのカウンタが増える |
| T-39 | LlmGateway | 未定義の用途 | `other` へ集約（カーディナリティを閉じる） |
| T-40 | LlmGateway | **系列名・ラベル名の契約** | ダッシュボードが依存する名前が固定される |

### ナレッジ健全性の観測値の生産（GraphService 側）

**受け口があっても、送る側が無ければ指標は永久に 0 である。** 生産者側は次を固定する。

| ID | 対象 | 内容 | 期待 |
| --- | --- | --- | --- |
| P-01 | GraphService | 辺を持つ文書と持たない文書が混在 | **持たない文書だけ**が孤立として報告される |
| P-02 | GraphService | 被参照だけの文書（Target 側にしか現れない） | **孤立ではない**（対称型の正規化で Source だけを見る実装をここで落とす） |
| P-03 | GraphService | 辺が 1 本も無い（境界） | 全文書が孤立 |
| P-04 | GraphService | 個人資料の孤立 | 観測値に**文書スコープが添えられる**（受け手はこの値でしか落とせない） |
| P-05 | GraphService | **スコープ属性を持たない / 組織文書**（陽性対照） | **スコープ無しで送られる**（否定形で書いた実装をここで落とす） |
| P-06 | GraphService | 個人資料の綴りの大小 | 判定は変わらない |
| P-07 | GraphService | **リースを取得できない周期** | **1 通も送らない**（全量置換の撃ち合いで件数が恒久的に過少になるのを防ぐ） |
| P-08 | GraphService | リースを取得できた周期（対照） | 報告し、**リースを解放する** |
| P-09 | GraphService | **孤立が 0 件** | **空のスナップショットを送る**（送らないと前回の件数が残り続ける） |
| P-10 | GraphService | 送出のパスと本文 | 受け口の宣言と同値のパス。本文は指標名と観測値だけ |
| P-11 | GraphService | 受け口が落ちている（5 態） | **例外を投げない**（fail-open。購読ホストを止めない） |
| P-12 | GraphService | 呼び出し元のキャンセル | **伝播させる**（握るとシャットダウンを続行したように見える） |

## 受け入れ基準との対応

- 業務指標（利用状況・検索傾向・回答品質）の集計・提供 … T-04〜T-07, T-10。
- 利用状況と満足率の期間整合（BFF が同一 `days` を伝播）… T-15（満足率の期間指定）＋ T-10。
- 後段障害時の透過・退化（非 2xx 透過・502）… T-12〜T-14。
- 運用情報の保護（**管理系ロール限定 = admin ＋ operator**。#544）… T-08, T-11。
- 独立稼働（受け入れ基準④）… ヘルスチェック。
- 入力バリデーション … T-03, T-28, T-36。
- **ナレッジ健全性の 4 規則**（全体集計・ロール限定・個人資料除外・件数のみ）… T-20〜T-27。
- **受け口の移設**（内部 API 化。生産者が利用者 JWT を持たないため）… T-29〜T-32。
- **観測値の生産**（孤立文書。両端点・スコープ付与・単一書き手化・0 件でも送る・fail-open）… P-01〜P-12。
- **LLM 利用実績の用途別・モデル別の計測**（総額のみを採らない）… T-37, T-39, T-40。
- **有効期間つき単価表と期間をまたぐ集計**（境界を含む）… T-30〜T-35。
- **期間外・該当なしは警告として表に出す**（無音の 0 円にしない）… T-34, T-38。

## 実装マッピング

- `KnowledgeHealthEndpointTests` — ナレッジ健全性指標（個人資料の集計除外・運用者以外は 403）
- `LlmUsageMetricsTests` — 用途別・モデル別の利用実績
- `ModelPriceTableTests` — 有効期間つき単価表と金額換算（区間は半開・期間外は無音の 0 円にしない）
- `DashboardEndpointTests` — ダッシュボードの集計端点
- `KnowledgeHealthProducerTests` — 観測値の生産（孤立文書の判定・スコープ付与・単一書き手化・送出）
