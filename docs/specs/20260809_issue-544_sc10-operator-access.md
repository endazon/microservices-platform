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

### 4. ★ 追随の母集合は 8 件ではなく **16 件**だった（レビュー 1 巡目の 🟡 2 件）

**レビューは 2 件を指摘したが、同型を全数走査すると 8 件だった**（未指摘 6 件）。

| # | 箇所 | 指摘 | 種別 |
| --- | --- | --- | --- |
| 1 | `DashboardBffEndpoints.cs:43`「集計は `AdminOnly` のため」 | **あり** | コード内コメント |
| 2 | `FR-10_dashboard.md:61,81`（散文 2 箇所） | **あり** | 機能仕様書 |
| 3 | `DashboardBffEndpointTests.cs:53` | なし | テスト内コメント |
| 4 | `docs/tests/FR-10_dashboard.md:56` | なし | テスト仕様書の観点一覧 |
| 5 | `docs/tests/SC-10_*:135` | なし | テスト仕様書の表 |
| 6 | `docs/screens/SC-10_*:260` | なし | **画面仕様書のデータソース表**（現在の仕様） |
| 7 | [[IADR-0011]] 決定「認可」 | なし | **ADR 本文** → 日付つき追記 |
| 8 | [[IADR-0128]] 決定 1 の根拠 | なし | **他 ADR からの引用**（「`/bff/dashboard` が使う既存の `AdminOnly`」）→ 日付つき追記 |

**#8 は自分でも危うく「別資源」へ分類しかけた。** [[IADR-0128]] は変換ジョブの再変換についての決定で
一見無関係だが、**その根拠として `/bff/dashboard` を「`AdminOnly` を使う既存例」に挙げていた**
——本作業でその前提が崩れる。**「別資源」の判定は資源ではなく〈引用されているか〉で決まる。**

#### なぜ 2 件では済まなかったか

**§1 の走査（`SC-10.{0,40}(管理者のみ|admin のみ|AdminOnly)` 等）が近接条件つきだった。**
`AdminOnly` と `dashboard` が**同じ行に無い**箇所（散文・表の 1 セル・他 ADR の引用）が落ちる。

是正後は **`AdminOnly` を単独で引いてから文脈で絞る**形にした——
**先に広く引き、あとで人が判断する**（規則 4 の「行フィルタで絞らない」と同じ趣旨である）。

### 5. ★★ 同じ機構で **3 回**落とした —— 2 段目の絞り込み（レビュー 2 巡目の 🟡 2 件）

**§4 で「`AdminOnly` を単独で引いてから文脈で人が絞る形にした」と書いたが、実際にはそうしていなかった。**

```console
$ grep -rn 'AdminOnly' . --include=... | grep -iE 'dashboard|SC-10|FR-10'
                                        ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ これが 2 段目
```

`openapi.yaml` の `/dashboard/usage|trends|summary`（**DashboardService 自身の契約**）の 5 箇所は
`description: "利用傾向は運用情報のため管理者ロール（AdminOnly）に限定する。"` のように書かれており、
**行内に `dashboard` も `SC-10` も `FR-10` も無い**。2 段目で全部落ちた。

**同じ機構での失敗は本セッションで 3 回目である。**

| # | 事故 | 2 段目の式 | 落ちたもの |
| --- | --- | --- | --- |
| 1 | #645（`openapi.yaml` の群コメント） | `grep -iE 'document｜文書'` | `/bff/documents:` 直上のブロックコメント |
| 2 | #544 一巡目 | `grep -iE 'dashboard｜SC-10｜FR-10'` | 散文・表のセル・他 ADR の引用 |
| 3 | **#544 二巡目（本件）** | **同上（直すと書いたのに使い続けた）** | **`openapi.yaml` の後段 3 パス 5 箇所** |

**規則 4 は「行フィルタで絞らない。パスから引く」と既に書いてある。** 3 回とも**その違反**である。

**是正は「気をつける」ではない。** 手順を変えた:

1. **2 段目を書かない。** `grep -rn 'AdminOnly' .` を素で流す（本件では **261 ヒット / 88 ファイル**）
2. **ファイル単位で絞る**（行ではなく）。当該資源に関係するファイルを列挙し、
   **そのファイル内の全ヒットを 1 行ずつ読む**
3. 残す判断には**理由を書く**（履歴の地の文／別資源／追記済みの ADR 原文）

**261 件を人が読むのは現実的でないという反論は当たらない** —— 88 ファイルのうち
**dashboard に関係するのは 14 ファイル**であり、そこだけ全行読めばよい。**絞るなら行ではなくファイルである。**

### 6. `updated:` の更新漏れ（レビュー 2 巡目の 🟢）

内容を変えた文書 **9 件**の frontmatter `updated:` が据え置きだった。まとめて `2026-08-09` へ揃えた。

### 7. ★★★ 走査語そのものが誤りだった（レビュー 3 巡目の 🟡 2 件）

**§5 で「2 段目を書かずファイル単位で絞る」へ改めたが、まだ足りなかった。**
今度は**1 段目の語（`AdminOnly`）が母集合を張れていなかった。**

| 落ちた箇所 | 実際の書かれ方 | `AdminOnly` を含むか |
| --- | --- | --- |
| `SC-10_*:241` | 「SC-10 に到達できるのは **`platform-admin` だけ**であり」 | **含まない** |
| `SC-10_*:253` | 「左ナビは **`requiresAnyRole: [platform-admin]`**」 | **含まない** |
| `SC-10_*:72`（**レビュー未指摘**。自分で見つけた） | 「アクセス: **`platform-admin` のみ**（計画との差異）」 | **含まない** |
| [[IADR-0030]]:74,82 | 「SC-10 ダッシュボード（FR-10）は…**`AdminOnly` のまま維持する**」 | 含む（**ファイルは開いたが 1 行しか見なかった**） |

**認可は `AdminOnly` という 1 語では書かれない。** `platform-admin` / `管理者のみ` /
`requiresAnyRole` / 「維持する」「不可のまま」——**同じ事実が何通りにも書かれる。**

#### 是正: 語ではなく **ファイル** から母集合を張る

```console
# 誤: 語で母集合を張る（語の変種を数え落とす）
$ grep -rn 'AdminOnly' .

# 正: 資源の名前でファイルを引き、そのファイルを全文読む
$ grep -rln -E 'SC-10|FR-10|dashboard|ダッシュボード' . --include=...
```

**この形にしたら、レビューが挙げなかった `SC-10_*:72` を自分で見つけられた。**
[[IADR-0030]] も「ファイルは走査に出ていたのに 1 行だけ見て閉じた」——**開いたら全文読む。**

#### 3 巡分の総括

| 巡 | 何が足りなかったか | 直した先 |
| --- | --- | --- |
| 1 | `.cs` を走査対象から外していた | 拡張子を外した（規則 3） |
| 2 | 2 段目の行フィルタで真の該当行を落とした | 2 段目を廃した（規則 4） |
| **3** | **1 段目の語が母集合を張れていなかった** | **語ではなくファイルから引く** |

**規則 1〜8 はいずれも「語をどう選ぶか」の話である。** 本件で分かったのは、
**語で母集合を張ること自体に限界がある**ということだった——資源に関係する**ファイル**を特定し、
**その全文を読む**ほうが確実である（ファイル数は語のヒット数よりはるかに少ない。本件では 88 → 14）。

**これは #647（検査器）の設計にも効く。** 検査器は「`AdminOnly` という語」ではなく
**「その端点の実効ロール」と「文書が主張するロール」**を突き合わせるべきである。

### 8. `updated:` の据え置き —— **同型 2 回目**（レビュー 4 巡目の 🟢 1 件）

[[IADR-0030]] は本 PR の 3 巡目（`cb38b0c`）で決定本文・影響欄へ追記したにもかかわらず、
frontmatter の `updated:` が `2026-07-08` のまま据え置きだった。`2026-08-09` へ揃えた。

**§6 と合わせて同型の事故が 2 回**である（1 回目 = 9 件まとめて是正）。
原因も同じで、**「本文を編集した」と「frontmatter を更新した」が別操作**であり、
**前者だけでも機械検査が通る**ことにある。

`CLAUDE.md`「**検査器・規約の追加は同型の事故が 2 回起きたら**」の条件を満たすため、
**検査器を起票した（#649）**。本 PR には入れない（1 issue = 1 PR。[[IADR-0116]] 規約 1）:

**判定式は 3 案を本 PR の 12 件へ当てて実測した**（思いついた形をそのまま書かない）:

| 案 | 判定 | 挙げた件数 | 評価 |
| --- | --- | --- | --- |
| A | `updated:` が base から変わっていない | **2** | **誤検知あり**。`BFF_bff-surface.md` は develop 側が既に `2026-08-09` で、同日中の再編集は据え置きが正しい |
| B | `updated:` < その文書を最後に変えたコミットの日付 | **10** | **誤検知だらけ**。コミットが UTC の日付境界を跨ぐと全件落ちる（本 PR は `2026-08-10` に着地した） |
| **C** | **`updated:` < PR の最初のコミットの日付** | **1** | **[[IADR-0030]] だけを正しく挙げる** |

> **採るのは案 C。** PR の差分に含まれる `docs/**/*.md` のうち、**frontmatter 以外の行に変更があり、
> かつ `updated:` が PR の最初のコミットの日付より古いもの**を fail させる。

**案 A・B を先に書いていたら誤検知で無効化されていた。** 検査器は「思いついた式」ではなく
**手元の実例へ当てて誤検知を数えてから**入れる。

**この検査は #647（宣言ロールと実装の突合）とは別物**である。#647 は「文書の主張と実装の一致」を、
本件は「文書を触ったのに更新日が動いていない」を見る。**射程が交わらないので束ねない。**

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
