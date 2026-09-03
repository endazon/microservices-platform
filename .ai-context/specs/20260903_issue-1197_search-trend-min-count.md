---
title: 作業仕様書 — 検索傾向は出現件数 3 件未満の語を落とし、しきい値を応答契約と SC-10 へ併記する
type: spec
status: done
related_ids:
  - FR-10
  - UC-05
  - SC-10
  - ADR-0006
  - ADR-0031
  - ADR-0068
  - ADR-0071
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - "ADR-0071 決定 1（出現件数 3 件未満の語は上位一覧に出さない。伏せた語は落とし『その他 M 件』を出さない。本値は配備時の構成で変更できる）"
  - "ADR-0071 決定 2（画面には現在のしきい値を併記する。併記のために応答契約へしきい値を 1 項目足す）"
  - "ADR-0071 決定 3（正規化は『前後空白の除去 ＋ 小文字化』。実装の現状を計画の値として採る。環流の推奨は採らない）"
  - "ADR-0071 決定 4（ハッシュ化は採らない）"
  - "ADR-0071 §結果（SC-10 の画面テストに『しきい値未満の語が出ない』検査が要る）"
  - "planning#514（環流。裁定 2026-09-03） / planning#525（計画 PR）"
related_adrs:
  - IADR-0354
  - IADR-0353
  - IADR-0343
  - IADR-0122
  - IADR-0044
issue: "#1197"
---

# 作業仕様書: 検索傾向の出現件数しきい値

## 起点

`ADR-0071`（Accepted・2026-09-03）が 4 論点を確定させた。

| # | 論点 | 裁定 |
| --- | --- | --- |
| 1 | 伏せ方 | **出現件数 3 件未満の語を上位一覧に出さない。落とす**（「その他 M 件」を出さない —— M 自体が推測材料になる）。**本値は配備時の構成で変更できる** |
| 2 | 併記 | **画面に現在のしきい値を併記する。** そのために**応答契約へしきい値を 1 項目足す** |
| 3 | 正規化 | **前後空白の除去 ＋ 小文字化**（現状追認）。環流の推奨（小文字化まで畳まない）は**採らない** |
| 4 | ハッシュ化 | **採らない**（読めない語は「検索傾向」として成立しない） |

決め手は ADR の言葉で「**しきい値は、消すことなく『個人の行動』だけを落とす線である**」。
1 回・2 回は同じ人の打ち直しで説明できるが、3 回目からは説明しにくくなる。

受け皿は #1197。前段は #1103（`IADR-0343` 決定 6 の 1 が「集計側の設計変更であり実装が独断で
決めない」として環流したもの）。

## 母集合（着手前に私が自分で引いた。issue 本文の実測は転記していない）

**走査は「検索傾向の算出 → 契約 → BFF → 画面」の経路を 5 本の語で引いた。**
起点コミットは `develop` `45853885`。`git rev-parse --is-shallow-repository` → **`false`**
（履歴は打ち切られていない。`git log` を出典に引ける）。

### 走査 1 — 算出（`AggregateTrendsAsync`）

```console
$ git grep -n "AggregateTrendsAsync"
.ai-context/adr/IADR-0343_usage-event-producer-at-bff.md:98,177   （凍結記録。触らない）
.ai-context/specs/20260830_issue-1062_three-level-slices-knowledge-rest.md:183 （凍結記録。触らない）
src/knowledge/backend/Services/DashboardService/Features/Dashboard/DashboardEndpoints.cs:62 （定義）
src/knowledge/backend/Services/DashboardService/Features/Dashboard/Summary/Endpoint.cs:18   （呼び出し）
src/knowledge/backend/Services/DashboardService/Features/Dashboard/Trends/Endpoint.cs:15    （呼び出し）
```

**呼び出しは 2 箇所ちょうど**（`DashboardEndpoints` の冒頭コメントが宣言している「2 操作が使う」と一致）。

### 走査 2 — 契約（`SearchTrendDto`）

```console
$ git grep -ln "SearchTrendDto"
.ai-context/adr/IADR-0343_usage-event-producer-at-bff.md            凍結記録
.ai-context/specs/20260703_FR-10_usage-dashboard.md                 凍結記録
.ai-context/specs/20260711_issue-229_migrate-dashboard-feedback-bff.md 凍結記録
.ai-context/specs/20260805_issue-520_openapi-response-required.md    凍結記録
.ai-context/specs/20260822_issue-882_dashboardservice-xunit1051.md   凍結記録
docs/api/openapi.yaml                                               ★ 追随
docs/functional/FR-10_dashboard.md                                  ★ 追随
scripts/contract-schema-baseline.json                               ★ 再生成
src/.../DashboardService/Features/Dashboard/DashboardEndpoints.cs   ★ 変更
src/.../DashboardService/Features/Dashboard/Trends/Endpoint.cs      ★ 変更
src/.../DashboardService/Tests/Features/Dashboard/DashboardEndpointTests.cs ★ 変更
src/.../Knowledge.Contracts/Dtos/DashboardDto.cs                    ★ 変更
src/knowledge/frontend/.../OperationsDashboardPage.tsx              ★ 変更
src/knowledge/frontend/.../types/dashboardCharts.ts                 参照のみ（構造型 `SearchTermLike`。変更不要）
src/platform/backend/Bff/Platform.Bff.Tests/BffTestFactory.cs       ★ 変更（スタブ）
src/platform/frontend/src/lib/api/generated/bff.schemas.ts          ★ 再生成
```

### 走査 3 — 受け渡し（`topSearchTerms` 大小文字無視）

走査 2 に加えて次が出た。

```console
$ git grep -iln "topSearchTerms"
docs/screens/SC-10_operations-dashboard.md                          ★ 追随
docs/tests/SC-10_operations-dashboard.md                            ★ 追随
src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/DashboardBffEndpoints.cs ★ 変更
src/knowledge/frontend/src/features/opsFlow.test.tsx                ★ 確認（導線テスト）
src/knowledge/frontend/.../OperationsDashboardPage.test.tsx         ★ 変更
src/platform/backend/Bff/Platform.Bff.Tests/DashboardBffEndpointTests.cs   ★ 変更
src/platform/frontend/e2e/sc10-operations.smoke.spec.ts             ★ 確認（E2E）
src/platform/frontend/src/lib/api/generated/dashboard/dashboard.faker.ts   ★ 再生成
```

### 走査 4 — 封筒 DTO（`DashboardUsageDto` / `DashboardSummaryDto`）

走査 2・3 に加えて `src/platform/frontend/src/lib/api/generated/dashboard/{dashboard.ts,dashboard.msw.ts}`
（**いずれも orval 生成物。手で触らない**）。

### 走査 5 — 表示文言（`検索傾向`）

`README.md` / `docs/tech/system-architecture.md` / `docs/data/usage-event.md` /
`deploy/docker-compose.yml` / `deploy/helm/.../values.yaml` はいずれも
**サービスの役割紹介**（「利用状況・検索傾向・回答品質の集計サービス」）であり、
**しきい値の話を持たない**。**追随不要**として除外する。
`src/platform/frontend/src/locales/**` は `pnpm run i18n` で再生成する。

### 陽性対照つきの否定 —— 「しきい値を運ぶ経路はまだ 1 本も無い」

```console
$ git grep -il "SearchTrendOptions\|SearchTermMinCount\|MinimumCount" | wc -l   → 0
$ git grep -il "StaleDocumentThresholdDays" | wc -l                             → 8   （陽性対照）
```

**同型の語（`StaleDocumentThresholdDays`）は非 0 で出る**ので、上の 0 件は走査の不備ではない。

### 除外の理由

| 除外したもの | 理由 |
| --- | --- |
| `.ai-context/specs/**` · `.ai-context/adr/IADR-0343` ほか確定済み記録 | **凍結記録は書き換えない**（`traceability.repo.md`） |
| `RecordEvent/Endpoint.cs` の `Normalize` | **`ADR-0071` 決定 3 が現状を追認した。触らない**（#1198 の宣言領域でもある） |
| `Domain/UsageEvent.cs` · `RecordEvent/**` | **#1198（`ADR-0072`）の宣言領域**。本 PR の後に着手される |
| `README.md` · `docs/tech/system-architecture.md` · `docs/data/usage-event.md` · `deploy/**` | 走査 5 のとおり役割紹介のみ。しきい値の記述を持たない |
| `deploy/helm/**` の `extraEnv` | **前例（`IADR-0353`）が置いていない。** 既定は `appsettings.json` にあり、必要になった配備で足す |

## 設計

### 1. しきい値の置き場所 —— `SearchTrendOptions`（`IADR-0353` 決定 3 をなぞる）

`src/knowledge/backend/Services/DashboardService/Features/Dashboard/SearchTrendOptions.cs`。
**2 操作（`Trends` / `Summary`）が使う**ため、段は `Features/Dashboard/` 直下である（`ADR-0068` 決定 2）。

- `SectionName = "SearchTrend"` ／ `DefaultMinimumCount = 3`
- 構成キー `SearchTrend:MinimumCount`（環境変数 `SearchTrend__MinimumCount`）。`appsettings.json` に既定を明記
- 🔴 **`ValidateOnStart` を付けない。不正値（0 以下）は既定へ倒し、警告ログを残す**
- 🔴 **報告する値は倒した後の値**（`EffectiveMinimumCount`）—— 倒したのに構成値を画面へ出すと、
  **見える語と表示されたしきい値が食い違う**（画面が嘘をつく）。`IADR-0353` 決定 3 と同じ要点である

### 2. `GET /dashboard/trends` はどうしきい値を運ぶか（issue やること 3 の宿題）

| 案 | 帰結 | 判断 |
| --- | --- | --- |
| **A. 運ばない。配列のまま。しきい値は封筒 DTO だけが持つ** | 契約が**非破壊**のまま。画面が使う経路（`/bff/dashboard/summary`）は完全に満たす | **採用** |
| B. `DashboardTrendsDto { minCount, terms[] }` へ包む | `GET /dashboard/trends` の応答の**形が変わる（破壊的）** | 却下 |
| C. `SearchTrendDto` の各行に持たせる | 🔴 **行が 0 件のときしきい値も消える。** 0 件はしきい値の効果が最も強く出る状態であり、そこで併記が欠けるのは本末転倒（`IADR-0353` 決定 4 と同じ理由） | 却下 |

**A の根拠**: (1) `ADR-0071` §結果 が契約追加先として「`DashboardSummaryDto` / `GET /dashboard/trends`」を
括弧書きしているが、**issue の受け入れ基準でしきい値の同梱を求めているのは
`/dashboard/summary` と `/bff/dashboard/summary` の 2 段だけ**である。
(2) 走査 1・3 のとおり **`/dashboard/trends` の呼び出し元はテスト以外に 0 件**（BFF は `/dashboard/summary` だけを呼ぶ）。
(3) 破壊的変更は `scripts/contract-breaking-allowlist.json` の承認が要る（`IADR-0122` 決定 3）。
**受け入れ基準が求めていない破壊を承認で通すのは割に合わない。**

**残るもの**: `/dashboard/trends` を直接叩く運用者はしきい値を知れない。
`ADR-0071` 決定 2 の射程は**画面**であり、そこは満たしている。

### 3. 契約の追加項目

`DashboardUsageDto` / `DashboardSummaryDto` の**末尾**へ `int SearchTermMinCount = 0` を足す。

- **既定値を付ける**——付けないと `check-contract-schema.js` は「既定値の無いメンバーの追加」を
  **破壊的**に分類する（`IADR-0122` 決定 2）
- **既定 0 の向きが安全側である**。旧 `DashboardService` が項目を返さない配備では BFF の
  逆直列化が 0 を入れ、画面のふるい落としは**素通り**になる。逆（既定 3）だと、
  しきい値を知らないサービスの応答から画面が勝手に語を消す

### 4. 画面（SC-10）

- 検索傾向カードに **`{minCount} 件以上検索された語のみを表示します`** を併記（Lingui）
- 🔴 **画面側でも `count >= minCount` でふるう**（`ADR-0071` §結果 の
  「しきい値未満の語が出ない」検査は**しきい値未満の語を含むスタブ**を前提にしている）。
  `IADR-0044` の多層防御と同じ向き —— 片側だけだと、後段の取りこぼしがそのまま画面へ出る
- ふるいは**表と棒グラフの手前 1 箇所**で行う（`SearchTrendTable` の入口）。2 箇所に置くと片方が腐る

## タスク

- [ ] `SearchTrendOptions` 新設・`Program.cs` で `Configure` ・`appsettings.json` に既定 3
- [ ] `AggregateTrendsAsync` に `minCount` 引数と下限述語（`Take(top)` の**前**）
- [ ] `Trends` / `Summary` の各 `Endpoint` が `IOptions<SearchTrendOptions>` を受ける
- [ ] `DashboardUsageDto` / `DashboardSummaryDto` へ `SearchTermMinCount`
- [ ] BFF `/bff/dashboard/summary` が透過
- [ ] `docs/api/openapi.yaml` 手更新 → `pnpm run codegen` → orval 生成物をコミット
- [ ] `node scripts/check-contract-schema.js --update`
- [ ] SC-10 の併記とふるい・Lingui カタログ再生成
- [ ] xUnit（陽性 3 / 陰性 2 / 境界 3 / 構成 5 / 「その他」行なし / 両段同値）
- [ ] Vitest（併記の文言 / しきい値未満の語が表にも図にも出ない）
- [ ] 変異試験 1 本（下限述語を外す → 陰性テストが落ちる → 戻して残渣 0）
- [ ] `IADR-0354` ＋索引・`docs/` 4 件の追随
- [ ] 稼働 k3s で `/bff/dashboard/summary` の陽性・陰性を生出力で実測

## 受け入れ基準

issue #1197 §受け入れ基準（12 項目）をそのまま採る。**追加も削減もしない。**

## 実測で分かって設計が動いた点

🔴 **稼働 k3s の 2 版混在が、単体テストでは出ない穴を見せた。**
しきい値を知らない**旧 BFF は `searchTermMinCount` を 0 で返すのではなく、JSON から項目ごと落とす**。
`count >= undefined` は全件 false なので、画面のふるいが**一覧を丸ごと空にする**。
設計 3 の「既定 0 が安全側」は**項目が 0 として届くときにしか成り立たなかった**。
画面側で `Number.isFinite` へ倒す処理と、そのケースの Vitest を足した
（`IADR-0354` 決定 3 の 2026-09-03 追記）。**スタブが常に契約どおりの形を返していたため、
単体テストでは 1 度も現れなかった。**

## やらないこと

- ハッシュ化（`ADR-0071` 決定 4）／全角半角・かなの正規化（決定 3 が定めないとした）
- `RecordEvent/Endpoint.cs` の `Normalize` の変更（決定 3 は現状追認）
- 「その他 M 件」の集約行（決定 1 が名指しで禁じた）
- `GET /dashboard/trends` の応答の形の変更（設計 2 の案 B）
- `deploy/helm` への `extraEnv` 追加（前例が置いていない）
