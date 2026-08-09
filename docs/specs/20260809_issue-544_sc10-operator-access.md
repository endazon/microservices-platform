---
title: 作業仕様書 — 運用ダッシュボード（SC-10）の閲覧を運用者へ広げる（#544）
type: work-spec
status: draft
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

**計画を正とする**（issue の明示）。裁定 **Q19 / Q28**、環流元 planning#198・#199。

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

## 検証記録（実測）

（実装後に記入する）
