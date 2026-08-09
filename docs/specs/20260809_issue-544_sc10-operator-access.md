---
title: 作業仕様書 — 運用ダッシュボード（SC-10）の閲覧を運用者へ広げる（#544）
type: work-spec
status: fixed
related_ids:
  - FR-10
  - SC-10
  - UC-05
  - IADR-0011
  - IADR-0035
  - IADR-0039
  - IADR-0044
  - IADR-0119
author: claude
created: 2026-08-09
updated: 2026-08-09
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
related_specs:
  - "../screens/SC-10_operations-dashboard.md"
  - "../tests/SC-10_operations-dashboard.md"
---

# 作業仕様書 — 運用ダッシュボード（SC-10）の閲覧を運用者へ広げる（#544）

## 起点となる計画書（トレーサビリティ）

| 種別 | ID | 何を求めているか |
| --- | --- | --- |
| 画面 | **SC-10** | 閲覧は「**運用者・管理者ロール限定**」（モックの「運用」バッジ準拠） |
| 要求 | **FR-10** | 利用状況・検索傾向・回答品質の可視化 |
| ユースケース | **UC-05** | 運用・管理 |

**計画を正とする**（issue の明示）。裁定 **Q19 / Q28**、環流元 planning#198・planning#199。

**方向が #628 / #629 と逆である** —— あちらは「計画が狭く実装が広い」ので**狭めた**。
本件は「**計画が広く実装が狭い**」ので**広げる**。同じ「計画を正とする」原則の適用である。

## 射程

- **BFF・DashboardService の両層**で閲覧の認可を admin ＋ operator へ広げる（[[IADR-0044]] 多層防御）
- **画面のルートゲート**（`RequireRole`）と `requiresAnyRole` を揃える
- 「狭めすぎない」だけでなく「**広げすぎない**」も対でテストに固定する

### 射程外（理由つき）

| 項目 | 理由 |
| --- | --- |
| `POST /dashboard/events`（利用イベントの記録） | **書き込みであり、認可を変えない**。現状 `RequireAuthorization()`（認証済みなら誰でも）で、集計の入力だからである。本 issue は「**参照専用であり書き込み権限を広げるものではない**」と明示している |
| 「ナレッジ健全性」節 | **そもそも実装されていない**（後述の ★ 判断 2） |
| SC-09（管理者設定） | 計画が **`platform-admin` のみ**と定める（[[IADR-0129]]）。本件と無関係 |

## 母集合（[[IADR-0141]] 決定 1・走査基準 `6447062`）

**issue 本文を転記していない。** すべて自分で引いた実測である。

### 軸 1: 認可を持つ層（全数）

| 層 | 箇所 | 現在 |
| --- | --- | --- |
| **BFF** | `DashboardBffEndpoints.cs:71` | `RequireAuthorization(AdminOnly)`（**1 口**） |
| **サービス** | `DashboardEndpoints.cs:50` `DashboardUsage` | `AdminOnly` |
| **サービス** | `DashboardEndpoints.cs:59` `DashboardTrends` | `AdminOnly` |
| **サービス** | `DashboardEndpoints.cs:72` `DashboardSummary` | `AdminOnly` |
| **サービス** | `DashboardEndpoints.cs:41` `RecordUsageEvent` | `RequireAuthorization()`（**射程外**） |
| **画面** | `sc10-operations/index.tsx:36` `RequireRole anyOf` | `[Admin]` |
| **画面** | `sc10-operations/index.tsx:51` `requiresAnyRole` | `[Admin]` |

**広げるのは 6 箇所**（BFF 1・サービス 3・画面 2）。

### 軸 2: ★ **誰がこの口を呼ぶか**（機械クライアント。#629 で引き漏らした軸）

```console
$ grep -rn --exclude-dir={bin,obj,coverage,node_modules} \
    -E '"/dashboard|/bff/dashboard|dashboard/summary|dashboard/events' src/ \
    --include=*.cs --include=*.ts --include=*.tsx | grep -viE '\.test\.|/tests?/|TestFactory|generated/'
```

本番の呼び出し元は **`useDashboardSummary.ts`（SC-10 の画面）だけ**である。
**`src/ai-stock-trading` からの呼び出しは 0 件**（submodule を populate して走査した）。

→ **機械クライアントは居ない。** かつ本件は**広げる**方向なので、仮に居ても締め出しは起きない。

### 軸 3: ★ **誰がこの応答を読むか**（#640 で引き漏らした軸）

`useDashboardSummary.ts` が orval 生成フック ＋ `okData` で読む。
**本件は認可だけを変え、応答の形を変えない**ので、解析層への影響は無い。
（#640 は 409 の本文という**新しい形**を足したので届かなかった。本件にはその要素が無い。）

### 軸 4: ★ **同型の先行実装**（#646 で引き漏らした軸 —— 隣ではなく決定を見る）

| 画面 | ルートゲート | 計画の定め |
| --- | --- | --- |
| SC-05 / SC-06 / SC-07 / SC-11 | `[Admin, Operator]` | 管理者・運用者 |
| **SC-09** | `[Admin]` | **管理者のみ**（[[IADR-0129]]） |
| **SC-10（本件）** | `[Admin]` | **管理者・運用者** ← **食い違い** |

**SC-10 だけが、計画が運用者を含むのに実装が admin のみである。**
[[IADR-0039]] 決定 1 は管理系画面を admin **または** operator と定めており、**本件はその形へ戻す**ことになる。

### 軸 5: この変更で新たに誤りになる記述（規則 8）

`広げる`／`admin のみ`／`AdminOnly`／`管理者ロール` の変種で全走査する（`.cs` / `.tsx` / `.md` / `.yaml` を含む。**拡張子で絞らない**）。実装後に引き直す。

## 判断

### 判断 1: **両層を同時に広げる**（画面だけ・BFF だけにしない）

issue が明示するとおり、**データ源と後段がともに `AdminOnly` のまま画面だけ開くと「開くと必ず 403 になる画面」**になる。
[[IADR-0127]] 決定 1 が SC-07 で踏んだ穴（画面だけ先に変えて API が追随していない）と同型であり、**同じ轍を踏まない**。

### 判断 2: ★ 「ナレッジ健全性」節の**維持すべき制限は存在しない**

issue は「**節の運用者・システム管理者限定はそのまま維持する**」と書いているが、**実測するとこの節は実装されていない**。

| 実測 | 結果 |
| --- | --- |
| `OperationsDashboardPage.tsx:45` | 「実装しない要素」として列挙 |
| `OperationsDashboardPage.test.tsx:193` | `does not render the knowledge-health section (its requirement is on hold)` が**不在を固定** |
| 理由 | **[[IADR-0119]] により節ごと着手保留**（FR-17 / FR-18）。引き受けは #504 / #452 |

**したがって「維持する」対象が無い。** 本作業では**何もしない**——
**節が実装されるときに、その時点で節単位の制限を設ければよい**（画面全体の閲覧ロールとは独立、という issue の整理はそのまま活きる）。

**不在を固定する回帰テストはそのまま残す**（#640 で学んだとおり、**理由が消えていないので反転させない**）。

## 実装方針

1. `DashboardBffEndpoints.cs` の `AdminOnly` を `RequireRole(Admin, Operator)` へ（コメントも直す）
2. `DashboardEndpoints.cs` の照会 3 口を同様に（`RecordUsageEvent` は触らない）
3. `sc10-operations/index.tsx` の 2 箇所を `[Admin, Operator]` へ
4. `docs/api/openapi.yaml` の `/bff/dashboard/summary` の `403` 記述を追随（＋ `pnpm run codegen`）
5. `docs/api/BFF_bff-surface.md` / `docs/screens/SC-10_*` / `docs/tests/SC-10_*` を追随

## テスト（受け入れ基準の写像）

| 受け入れ基準 | テスト |
| --- | --- |
| 運用者が SC-10 を**閲覧できる**（画面・BFF・サービスの 3 層） | 画面のルートゲート ＋ `Summary_AsOperator_IsAllowed` |
| **一般利用者は閲覧できない**（広げすぎない） | `Summary_AsViewer_IsForbidden` ＋ 画面の NotFound |
| 管理者は従来どおり | 既存テストが維持されること |
| 利用イベントの記録は**変えない** | 既存テストが維持されること |

**変異試験を行う** —— 広げる方向は「テストが通ってしまう」罠が逆向きに効く
（**広げ忘れても既存テストは緑のまま**）。運用者で引くテストを足し、効くことを確かめる。

## 実装中に決めたこと（仕様書からの差分）

### 1. 母集合は 6 箇所ではなく **14 箇所**だった（実装 6 ＋ 追随 8）

§軸 5 の予告どおり実装後に引き直したところ、**認可そのもの以外に 8 件**が誤りになった。

| # | 箇所 | 内容 |
| --- | --- | --- |
| 1 | `docs/api/openapi.yaml` | `description`（AdminOnly）と `403` の説明 → **`pnpm run codegen`** |
| 2 | `docs/api/BFF_bff-surface.md` | `/bff/dashboard/summary` の認可欄 |
| 3 | `docs/functional/FR-10_dashboard.md` | 口の一覧表 **4 行** |
| 4 | `docs/tests/FR-10_dashboard.md` | T-08 / T-11 |
| 5 | `docs/tests/SC-10_*` | **A2 を「差異の固定」から「一致の固定」へ反転**＋ A2-b を新設 |
| 6 | `docs/screens/SC-10_*` | 計画との差異表・据え置きの根拠・未決事項 4 |
| 7 | `docs/screens/SC-11_*` | 「SC-10 は BFF が `AdminOnly`」という**他画面からの参照** |
| 8 | [[IADR-0129]] 決定 4 | 日付つき追記（決定は置換しない） |

**テスト用の足場（`TestAuthHandler` / `BffTestFactory` / `TestWebApplicationFactory`）のコメントも
`AdminOnly` と書いていた** —— `.cs` を走査対象に含めたので拾えた（規則 3。#646 の教訓）。

### 2. ★ 走査語に当たらない箇所をテストが捕まえた

**`Layout.test.tsx` の `shows the 構成ビューア (SC-11) link for platform-operator` が落ちた。**
「運用者は AdminOnly の SC-10 は見えない」ことを**併せて**固定していたためである。

**私の走査では引けなかった** —— この行は `dashboard` でも `AdminOnly` でもなく
**`ダッシュボード`（リンクの表示名）**で書かれており、認可の語彙を含まない。

**教訓は「走査語を増やす」ではない** —— 表示名まで含めると偽陽性が支配的になる。
**この型は走査ではなくテストで捕まえるのが正しい**（実際そうなった）。
本作業では走査 ＋ 全件テストの二段で押さえている。

### 3. `| tail` が検査器の終了コードを隠していた

`node scripts/check-cross-repo-refs.js 2>&1 | tail -1` は**パイプ最終段（`tail`）の終了コード**を返すため、
**検査器が exit 1 でも成功に見えた**。実際 `docs/specs/20260809_issue-544_*.md:34` に
列挙形の修飾漏れ（`planning#198・#199`）が 1 件あった。

**出力を捨てずにファイルへ落として `$?` を見る**形へ改めて確認した。
**同じ違反が `.cs` にもあった**（検査器は Markdown のみ走査するので素通りする）ので、そちらも揃えた。

## 検証記録（実測。base = `6447062`）

| 検査 | 結果 |
| --- | --- |
| `dotnet build`（両ユニット） | Build succeeded・0 Error |
| `dotnet test Platform.Bff.Tests` | **196 → 197 Passed** / 1 Skipped |
| `dotnet test DashboardService.Api.Tests` | **10 → 16 Passed** |
| `dotnet test knowledge`（全体） | Failed 0 |
| `dotnet format --verify-no-changes`（両ユニット） | exit 0 |
| `pnpm typecheck` / `lint`（**0 errors**）/ `format:check` / `build` | すべて OK |
| `pnpm test:coverage` | **623 → 624 Passed** / 63 files |
| `check-doc-links` / `check-cross-repo-refs` / `check-plan-id-qualification` / `check-contract-schema` / `check-test-traceability` / `check-i18n-catalogs` / `check-static-egress` | すべて OK |
| `check-chunk-budget` | 584.61 → **584.64 kB** へ更新（+0.02 kB） |

### ★ 変異試験（**両方向**）

**「広げる」作業は罠が逆向きに効く** —— 広げ忘れても既存テストは緑のままである。
**広げすぎも検査できなければ意味が無い**ので、両方向で確かめた。

| 変異 | 結果 |
| --- | --- |
| BFF を `AdminOnly` へ戻す（**広げ忘れ**の再現） | **Failed 1** —— `GetSummary_AsOperator_IsAllowed` **だけ** |
| BFF を `RequireAuthorization()` にする（**広げすぎ**の再現） | **Failed 2** —— `GetSummary_WithoutPrivilegedRole_Returns403` ＋ 既存の 403 テスト |
| 戻す | **Failed 0**（197 Passed） |

## 申し送り

- **#543（人手補正 API）は本 PR に含めない。** 別資源であり、[[IADR-0116]] 規約 1（1 issue = 1 PR）に従う。
  同 issue は「**`IADR-0042` の表題を実体へ合わせるか、後継 IADR を起こすか**」という
  **決定待ちの問い**を含むので、着手時にそこから扱う。
