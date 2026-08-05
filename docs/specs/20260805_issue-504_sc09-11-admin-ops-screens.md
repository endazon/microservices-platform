---
title: SC-09〜11（管理者設定・運用ダッシュボード・構成ビューア）の新スタックでの再実装
type: spec
status: done
related_ids: [SC-09, SC-10, SC-11, UC-05, FR-05, FR-09, FR-10, FR-15, ADR-0031, IADR-0009, IADR-0046, IADR-0119, IADR-0121, IADR-0124, IADR-0125, IADR-0129]
author: Claude
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/06_technical/05_observability-ops.md"
  - "../../planning/projects/microservices-platform/06_technical/10_composability-design.md"
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
  - "../../planning/projects/microservices-platform/INDEX.md"
related_specs:
  - ../screens/SC-09_admin-abac-settings.md
  - ../screens/SC-10_operations-dashboard.md
  - ../screens/SC-11_configuration-viewer.md
  - ../tests/SC-09_admin-abac-settings.md
  - ../tests/SC-10_operations-dashboard.md
  - ../tests/SC-11_configuration-viewer.md
  - ../adr/IADR-0129_sc09-11-admin-ops-screen-composition.md
  - ../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md
  - ../adr/IADR-0121_spa-stack-migration-staging.md
  - ../adr/IADR-0124_tanstack-router-unit-composition.md
  - ../adr/IADR-0125_ui-primitives-i18n-catalog-and-storybook.md
  - ../adr/IADR-0046_config-version-history-source.md
  - ./20260805_issue-503_sc05-08-admin-screens.md
  - ../adr/IADR-0006_abac-management-validation.md
---

# 仕様書: SC-09〜11 の新スタックでの再実装（管理者設定・運用ダッシュボード・構成ビューア）

> 本仕様書は実装着手前に作成した。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: **SC-09**（管理者設定〔ABAC〕）・**SC-10**（運用ダッシュボード）・**SC-11**（構成ビューア）
- ユースケース（UC）: **UC-05**（ABAC 権限を管理する）——SC-09 / SC-10 / SC-11 とも計画の
  画面一覧が対応づける UC は **UC-05** である（SC-11 は「—（運用・保守要求）」）。
- 機能要求（FR）: **FR-05 / FR-09**（SC-09）・**FR-10 ＋ NFR〔運用・可観測性〕**（SC-10）・**FR-15**（SC-11）
- モックアップ（**実装の正**）:
  [hi-fi/sc-09.html](../../planning/projects/microservices-platform/05_screens/mockups/hi-fi/sc-09.html) /
  [sc-10.html](../../planning/projects/microservices-platform/05_screens/mockups/hi-fi/sc-10.html) /
  [sc-11.html](../../planning/projects/microservices-platform/05_screens/mockups/hi-fi/sc-11.html)
  （補助: 同名の `wireframe/`）
- 関連 ADR（計画）:
  [ADR-0031](../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md)（Accepted。
  React 19 / Vite / TanStack Router / TanStack Query / Tailwind v4 ＋ shadcn/ui / Lingui。逸脱不可）／
  [ADR-0018](../../planning/projects/microservices-platform/07_adr/ADR-0018_composable-architecture.md)（SC-11 の前提）
- 関連 IADR: **[[IADR-0129]]（本作業の内部設計判断。本書と対で読む）**・[[IADR-0009]]（存在秘匿）・
  [[IADR-0119]]（FR-17〜21 の着手保留）・[[IADR-0046]]（構成バージョン履歴の正データ源）・
  [[IADR-0029]]・[[IADR-0030]]・[[IADR-0035]]・[[IADR-0036]]・[[IADR-0040]]・
  [[IADR-0121]]・[[IADR-0124]]・[[IADR-0125]]・[[IADR-0127]]
- 本リポジトリの起点: **#504**（親 #452 / #446 / #454。分割 1 本目 = #502＝PR #505、2 本目 = #503＝PR #508。いずれもマージ済み）

### issue #504 の対応表と計画の食い違い（**計画を正とした**）

issue #504 §スコープ の表は SC-09 を「FR-13・UC-08」、SC-10 を「UC-07」と書いている。
**計画の実測はいずれも異なる**——

| 画面 | issue #504 の表 | 計画の実測 | 出所 |
| --- | --- | --- | --- |
| SC-09 | FR-13・UC-08 | **FR-05, FR-09, FR-17 ／ UC-05** | 05_screens 画面一覧（`01_screens.md:55`）。hi-fi のバッジも `FR-05,09` / `UC-05` |
| SC-10 | UC-07 | **FR-10 ＋ 非機能要件〔運用・可観測性〕 ／ UC-05** | 同（`:56`）。hi-fi のバッジは `NFR(運用)` / `UC-05` |
| SC-11 | FR-15 | **FR-15 ／ —（運用・保守要求）** | 同（`:57`）。**一致している** |

計画で **FR-13 は「Wiki サービスでの閲覧」**（関連画面は SC-04）、**UC-07 は「Wiki で閲覧する」**、
**UC-08 は「外部 AI エージェントからナレッジを利用する」**（関連画面は SC-12）であり、本 3 画面とは対応しない。
**本書・画面仕様書・テスト仕様書・コード内コメントはすべて計画側の ID を採る。**
issue 側の表の訂正は親へ申し送る（§親への申し送り）。**#503 でも同型の食い違い（SC-08 の UC）が起き、
issue 側が訂正された**先例がある。

### FR-17（辺の型）の射程確認 — **保留対象を実装しない**

issue #504 は「**辺の型そのもの**は FR-17 に属し [[IADR-0119]] で保留中だが、**その値集合の管理画面は
FR-13（ABAC・管理者設定）の一部**である。着手時に計画で射程を確認すること」と注意している。
**計画を実測した結果、辺の型辞書は FR-17 に属する**——

| # | 実測した記述 | 出所 |
| --- | --- | --- |
| 1 | 画面一覧の SC-09 行の関連要求は **`FR-05, FR-09, FR-17`** である（FR-13 ではない） | `01_screens.md:55` |
| 2 | §SC-09 の当該節の見出しが「**辺の型（値集合）の管理（起案・2026-08-02。FR-17・ADR-0033 決定3）**」と、FR-17 を明示している | `01_screens.md:272` |
| 3 | 同節は初期値集合・対称/非対称・フォールバック・削除/改名規則のすべてを **ADR-0033 決定3 / 決定9** に依拠させている。**ADR-0033 の状態は `Proposed`** である | `01_screens.md:272-282`／`07_adr/README.md` |

[[IADR-0119]] 決定 1・2 は「FR-17 の実装には着手しない。着手条件は前提 ADR が **`Accepted`** になること
（`Proposed` は満たさない）」と定める。したがって **SC-09 の「辺の型」区画は本 issue で実装しない**（分類 **A**）。
**辞書の「値集合」だけを先に作ることもしない**——値集合そのものが ADR-0033 決定3 の内容であり、
中核 5 種・推奨追加 4 種・逆向きの表示語はいずれも `Proposed` な ADR に由来するためである。
同じ理由で **SC-10 の「辺の型ごとの使用件数」「フォールバック警告」「ナレッジ健全性」も実装しない**（分類 A）。

### SC-11 の構成バージョン履歴 — **計画は 2026-08-04 に確定済み**

issue #504 は「planning#190 が審議中の可能性がある。着手時に計画の最新を確認せよ」と注意している。
**planning pin `d980a01` の実測では確定している**——
[06_technical/05_observability-ops.md](../../planning/projects/microservices-platform/06_technical/05_observability-ops.md)
（`:94-96`）が次を定める。

- **正データ源は GitOps 層**（Git のコミット履歴・ArgoCD のリビジョン履歴）。
- **プラットフォームのサービスに履歴ストアを持たない**（制約）。API と SC-11 は**永続化せず surfacing する**。
- **保持範囲（2026-08-04 確定・planning#190）**: Git のコミット履歴を正とし**無期限**。
  **ArgoCD のリビジョン履歴は既定（`revisionHistoryLimit` = 10 世代）**を採用し、それを超える遡及は Git で行う。
  **SC-11 が表示できる履歴の上限はこの規則から定まる。**

これは実装側の [[IADR-0046]]（Accepted・2026-07-09）と**同じ内容**である。**実装の変更は要らない**——
画面は API（`GET /bff/admin/config/history`）が返すスライスを新しい順に表示するだけでよい。
**画面が独自に件数を切り詰めることはしない**（上限は GitOps 側が決めるため）。

### SC-11 の未決事項 5（draw.io ワイヤーフレーム）— **解決済み**

`docs/screens/SC-11_configuration-viewer.md` の未決事項 5 は「計画側 `05_screens/wireframes/sc-11.drawio`
の作成」を計画リポジトリへ送っていた。**計画は HTML モックアップを正とし draw.io を作成しない**方針である
（`01_screens.md` §HTMLモックアップ が hi-fi / wireframe の HTML を挙げ、SC-11 にも
[wireframe/sc-11.html](../../planning/projects/microservices-platform/05_screens/mockups/wireframe/sc-11.html) が揃っている。
**`.drawio` は計画リポジトリに 1 件も存在しない**）。本書で**解決済みとして畳む**（画面仕様書の未決事項からも外す）。

## 目的・背景

#502（PR #505）が利用者の主導線（SC-01〜03）を、#503（PR #508）が管理者の一覧・詳細型 4 画面（SC-05〜08）を
新スタックへ載せ替えた。本 issue は #452 の分割 3 本目として、**管理者・運用者に限定された 3 画面**を
新スタックで作り直し、旧実装を削除する。

3 画面をまとめる理由は issue #504 が述べるとおり **「権限による出し分けと存在秘匿（[[IADR-0009]]）が
共通の関心事」**だからである。3 画面はロールの境界がそれぞれ異なり（SC-09＝admin のみ／SC-10＝admin のみ〔後述〕／
SC-11＝admin または operator）、**まとめて作らないと秘匿の作法が画面ごとにばらつく**。

## 対象範囲

### 対象

1. **SC-09 / SC-10 / SC-11 の再実装**（hi-fi モックアップと 05_screens を正とする）。
   - サーバ状態は **TanStack Query**（`useQuery` / `useMutation`）。
   - UI は **`@platform/ui`** のプリミティブ（**新規プリミティブは追加しない**。判定は §4）。
     **SC-09 の区画切替には `Tabs`（#496 で移植済み）を使う**（issue #504 の明示）。
   - 文言は **Lingui のカタログ**（ja / en）。
2. **旧実装の削除**（`features/sc09-admin-abac` / `sc10-operations` / `sc11-config` の実装・テスト・index）
   ——「置き換え」であって、上書きではなく**削除して作り直す**。
   **ただし `sc11-config/access.test.tsx` の観点は新実装へ引き継ぐ**（§テスト方針）。
3. **`eslint-plugin-lingui` の適用範囲を本 3 feature へ拡大**（#502 が確立し #503 が継いだ運用）。
4. テスト（単体・純関数・E2E）とカバレッジ床の維持・引き上げ。
5. **左ナビの表示名を計画・モックへ揃える**（SC-09「ABAC設定」・SC-10「**ダッシュボード**」・SC-11「構成ビューア」）。

### 対象外（送り先を明記する。**繰り延べであって放棄ではない**）

| 事項 | 送り先 | 理由 |
| --- | --- | --- |
| **SC-09 の「辺の型」区画**（辞書・3 層構成・対称/非対称・逆向きの表示語・使用件数・削除拒否/改名） | **[[IADR-0119]] の保留解除後**（前提 = ADR-0033 の `Accepted` 化） | **FR-17 に属する**（上記 §射程確認）。契約も無い |
| **SC-09 の「タグ辞書」区画**（値の一覧・追加・**使用件数つき削除拒否**・**改名の追随**） | **本書 §環流**（新規記録） | 契約の不在。タグ辞書の値集合を返す口・使用件数・改名の追随のいずれも無い |
| **SC-09 の「検証」ボタン**（保存せずに矛盾検証だけを行う） | 同上 | 契約の不在。dry-run の検証エンドポイントが無い（検証は `POST` の 400 としてのみ得られる） |
| **SC-09 の自由記述のポリシー条件式**（`<=` 等の比較・「含む」） | 同上 | 契約は**属性キー → 許可値の集合**（`Dictionary<string, List<string>>`）だけを表現する。**部分未実装**（§3 の「隠れた部分未実装」） |
| **SC-10 の SLO カード・LLM コストカード・「人/日」** | 同上 | 契約の不在。`DashboardSummaryDto` に SLO・コスト・**一意利用者数**が無い |
| **SC-10 の「ナレッジ健全性」節**（孤立文書・未解決リンク・未要約クラスタ・陳腐化文書）・**辺の型ごとの使用件数**・**フォールバック警告** | **[[IADR-0119]] の保留解除後** | **FR-17 / FR-18 に属する**（計画は「起案・2026-08-01。Phase 3」と明記） |
| **共通シェルのパンくず・権限バッジ** | 共通シェルの作業（#452 系） | `foundation/ui/Layout` の責務。#490 仕様書が #452 へ渡している |
| **右レール AI チャットパネル** | 移行**第 4 段** | [[IADR-0121]] 決定 1・5 |
| **SC-12（MCP クライアント管理）への導線** | **#445 待ち** | 遷移先の画面自体が未実装（ルートが無い） |
| **`/bff/dashboard/summary` の閲覧ロールを運用者へ広げること** | **本書 §環流**（裁定と後段の改修の両方が要る） | 後段 `DashboardService` も AdminOnly であり、BFF だけ広げても 403 になる |
| SC-12 の再実装 | #445 待ち | 本 issue の分割方針 |
| SC-18〜21 | [[IADR-0119]] の保留解除後 | 前提 ADR が `Proposed` |
| `oidc-client-ts` の撤去 | 第 3 段（#439） | [[IADR-0121]] 決定 6 |

## 設計

内部設計の判断（選択肢の比較・棄却理由）は [[IADR-0129]] を正とする。本節は実装の形を記す。
画面ごとの詳細は画面仕様書（[SC-09](../screens/SC-09_admin-abac-settings.md) /
[SC-10](../screens/SC-10_operations-dashboard.md) / [SC-11](../screens/SC-11_configuration-viewer.md)）を正とする。

### 1. ファイル構成

```text
src/knowledge/frontend/src/features/
├── sc09-admin-abac/
│   ├── index.tsx                    ルート（/admin/abac）＋ ナビ項目
│   ├── AdminAbacSettingsPage.tsx    画面（Tabs: 属性体系 / ポリシー定義）
│   ├── AttributeDictionaryPanel.tsx 属性辞書（一覧 ＋ 追加 ＋ 削除）
│   ├── PolicyEditorPanel.tsx        ポリシー（一覧 ＋ 構造化条件エディタ ＋ 検証結果）
│   ├── useAbacAdmin.ts              useQuery（属性・ポリシー）＋ useMutation（追加・削除・切替）
│   ├── abacVocabulary.ts            アクション 3 値・スコープ 2 値・条件の組み立て — 純関数
│   └── *.test.ts(x)
├── sc10-operations/
│   ├── index.tsx                    ルート（/admin/ops）＋ ナビ項目
│   ├── OperationsDashboardPage.tsx
│   ├── useDashboardSummary.ts       useQuery（?days=）
│   ├── opsTools.ts                  実行時 config → 外部ツール導線 — 純関数
│   └── *.test.ts(x)
└── sc11-config/
    ├── index.tsx                    ルート（/admin/config-viewer）＋ ナビ項目
    ├── ConfigViewerPage.tsx
    ├── useConfigViewer.ts           useQuery ×3（実効構成・ドリフト・履歴）
    ├── driftView.ts                 ドリフト種別 5 値・深刻度 2 値の写像 — 純関数
    ├── access.test.tsx              **既存の観点を引き継ぐ**（存在秘匿）
    └── *.test.ts(x)
```

**純関数を別ファイルへ出す**のは、値集合と写像を DOM を描かずに試験できるようにするためである
（#502 / #503 と同じ作法）。**#503 の変異試験 M31 が示したとおり、値集合の欠落は画面テストでは捕まらない**
——`abacVocabulary.ts` / `driftView.ts` / `opsTools.ts` の 3 つとも `*.test.ts` を持たせる。

### 2. 権限（3 画面それぞれ境界が違う）

| 画面 | ルート・ナビ | 計画の記述 | 実装 | 根拠 |
| --- | --- | --- | --- | --- |
| SC-09 | **`platform-admin` のみ** | §共通シェル「SC-09・SC-12・SC-17 = **システム管理者**」／§SC-09「システム管理者ロール限定」 | 同じ | [[IADR-0040]]（BFF も AdminOnly。operator も 403） |
| SC-10 | **`platform-admin` のみ**（**据え置き**） | §SC-10「**運用者・管理者**ロール限定」 | **管理者のみ**（差異） | データ源 `/bff/dashboard/summary` と後段 `DashboardService` がともに **AdminOnly**。画面だけ広げると運用者に 403 の画面を見せることになる（[[IADR-0129]] 決定 4）。**§環流 の提案 5 で裁定を求める** |
| SC-11 | **`platform-admin` または `platform-operator`** | §SC-11「管理者・運用者ロール限定。権限外にはメニュー・画面自体を表示しない」 | 同じ | [[IADR-0030]]（`ConfigViewer` ポリシー）／API は 404 で秘匿（[[IADR-0029]]） |

**存在秘匿の作法（3 画面共通。#490 が確立し本 issue が踏襲する）**:

1. ルートは `RequireRole` でラップし、権限外は **`NotFound` を描画する**（`/login` へも誘導しない）。
2. **未知パスの `NotFound` と権限による秘匿の `NotFound` は markup が一致する**——テストで固定する。
3. **権限外では BFF を呼ばない**（要求の有無から存在を推測させない）。
4. サーバ側の 403 / 404 は**同一の中立文言**へ寄せる（[[IADR-0129]] 決定 3）。

### 3. 実装しない画面要素（**モックに描かれているのに実装しないもの**）

**後から「作り忘れ」と誤解されないよう、各画面仕様書へ行番号つきの対応表を置く。**
理由は #502（A / B）・#503（C）が定めた 3 種類で分類する。

| 種別 | 対象 | 根拠 |
| --- | --- | --- |
| **A. FR の着手保留** | SC-09 の**辺の型区画**（タブ・表・削除拒否の注記・3 層構成の注記）／SC-10 の**ナレッジ健全性節**（4 KPI・辺の型ごとの使用件数・フォールバック警告・除外の注記） | [[IADR-0119]] 決定 1・2（FR-17 / FR-18。前提 ADR-0033 は `Proposed`） |
| **B. 契約の不在** | SC-09 の**タグ辞書区画**・**検証ボタン**・**自由記述の条件式**（部分）／SC-10 の **SLO カード**・**LLM コストカード**・**「人/日」** | BFF ＋ 後段サービスの契約に載る先が無い。実測は各画面仕様書 |
| **C. 他 issue の射程** | SC-09 の **SC-12 への導線**（#445）／共通シェルのパンくず・権限バッジ（#452 系）／右レール AI チャットパネル（第 4 段） | 引き受け先が既に決まっている |

**「動かない UI を置く」形は採らない**（#502 で確立）。空のタブ・押しても何も起きないボタン・
常に「—」の KPI カードは、計画が画面へ与えた役割（管理者・運用者が状況を正確に把握する）をむしろ損なう。

#### 隠れた部分未実装（**二値判定の外側**。#503 の教訓）

対応表の判定を「する／しない」の二値にすると、**「する」行の中の部分未実装が集計から漏れる**
（#503 で SC-05 のタグ辞書整合が実際に漏れた）。本 issue で該当するのは次の 2 件であり、
**対応表の備考に明記し、件数の記述にも併記する**。

| # | 画面 | 対応表の判定 | 満たしていない制約 |
| --- | --- | --- | --- |
| 1 | SC-09 | ポリシー条件の行は「**する**」 | 計画の入力表「ポリシー条件｜必須｜**条件式**」のうち、**比較演算子・「含む」を伴う自由記述の式**は実装しない。契約が表現するのは**属性キー → 許可値の集合所属**だけである |
| 2 | SC-10 | 利用状況カードの行は「**する**」 | モックの数値は「**312 人/日**」＝**一意利用者数**であり、契約が返すのは**イベント件数**（`UsagePointDto(Date, EventType, Count)`）である。実装は件数を出す |

### 3-b. モックに無いが実装する要素（**逆向きの漏れも名指しする**）

| 画面 | 要素 | 計画上の根拠 |
| --- | --- | --- |
| SC-09 | 属性辞書の**一覧・追加・削除**（モックは「属性体系」タブの見出しだけを描き中身を描いていない） | 05_screens §SC-09 §主要素「**属性体系エディタ**」／FR-09「ABAC 属性・ポリシーを管理する」 |
| SC-09 | ポリシーの**有効／無効切替・削除** | FR-09（管理）。削除せず一時停止できる形は既存契約（`PATCH …/active`）が持つ |
| SC-10 | **集計期間**（7 / 30 / 90 日）の切替 | FR-10（利用状況の可視化）。モックの KPI 副題が「/日」「/週」「今月」と**期間で語る**のに対し、契約の期間指定は `?days=` の 1 本だけである |
| SC-10 | **回答品質（満足率）** | FR-10「利用状況・検索傾向・**回答品質**を可視化する」／ FR-08（フィードバック収集） |
| SC-10 | **利用状況（日次）・検索傾向（上位語）**の一覧 | FR-10 の 3 本柱のうち 2 本。モックは KPI カードの副題に畳んでいる |
| SC-11 | **再取得**（更新）ボタン | 05_screens §SC-11 は参照専用と定める。再取得は**参照の操作**であり構成を変更しない。障害調査で「いまの実効構成」を取り直す用途（画面仕様書 §アクション・イベント が #113 時点から挙げている） |

### 4. UI 部品（`@platform/ui`）— **新規プリミティブは追加しない**

計画 [13_frontend-stack §shadcn/ui 派生の範囲](../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md)
の 4 基準で判定する。本 issue で新しく要るのは次の 2 つである。

| 部品 | 1. フォーカストラップ | 2. 複合キーボード操作 | 3. ポータル配置計算 | 4. `aria-*` の動的同期 | 判定 |
| --- | --- | --- | --- | --- | --- |
| **区画の切替（SC-09）** | 非該当 | **該当**（ロービングタブインデックス＝矢印キー移動） | 非該当 | **該当**（`role="tab"` / `aria-selected` / `aria-controls`） | **既存の `Tabs` を使う**（#496 で Radix ベースで移植済み。**新規は作らない**） |
| **折りたたみセクション（SC-11）** | 非該当 | 非該当（`<summary>` は既定でフォーカス可・Enter/Space で開閉） | 非該当 | 非該当（`<details>` が `open` を持ち、支援技術へ状態が伝わる） | **ネイティブの `<details>` / `<summary>`。`@platform/ui` へは入れない** |

**`Tag`（分類名）と `StatusBadge`（状態）の区別を踏襲する**（#502 が確立し #503 が継いだ）:

| 用途 | 部品 | 理由 |
| --- | --- | --- |
| 属性のスコープ（SC-09）・ポリシーのアクション（SC-09）・構成バージョン/適用者（SC-11 ヘッダ） | **`Tag`** | **分類の名前・識別子**。意味は文字が担う |
| ポリシーの有効/無効（SC-09）・ドリフトの有無と深刻度（SC-11）・段の有効/無効（SC-11） | **`StatusBadge`** | **状態**。`tone → 固定アイコン` で色 ＋ アイコン ＋ テキストを型で強制する（INDEX 決定 21） |

### 5. 状態表示（INDEX 決定 21「色だけで意味を持たせない」）

**SC-11 のドリフト深刻度**——契約（`DriftDetector`）が返す 2 値をそのまま写す。

| `severity` | 表示 | `tone` | アイコン（`tone` から自動） |
| --- | --- | --- | --- |
| `Warning` | 警告 | `warning`（琥珀） | `AlertTriangle` |
| `Info` | 情報 | `neutral` | `Info` |
| 上記以外（未知） | 生値をそのまま | `neutral` | `Info` |

**SC-11 のドリフト種別**——契約の 5 値に表示名を与える。未知の値は**生値をそのまま**出す（丸めない）。

| `kind` | 表示 |
| --- | --- |
| `MissingApply` | 適用漏れ（宣言にあり実効に無い） |
| `UndeclaredSubscription` | 宣言に無い購読 |
| `StaleStage` | 古い段の残留 |
| `BindingMismatch` | 接続の不一致 |
| `Unverifiable` | 検証不能（担当サービスへ到達できない） |

**SC-11 の全体ドリフト状態**（ヘッダのバッジ）: 0 件 = `success`「ドリフトなし」／N 件 = `warning`「ドリフト N 件」。
**取得不能はバッジを出さない**（「0 件」と見分けが付かなくなるため。§縮退）。

**SC-09 のポリシー状態**: 有効 = `success`「有効」／無効 = `neutral`「無効」。
**琥珀（`warning`）はどの状態にも充てない**——#503（SC-06）と同じ判断で、
琥珀が指すのは**異常**であって管理者が意図した無効化ではない。

### 6. データソース（BFF 境界。**実在を実測した**）

`grep -rn 'MapGroup("/bff' src/platform/backend src/knowledge/backend` で全 10 グループを確認し、
本 3 画面が使う 3 グループの各エンドポイントを個別に読んで突合した。

| 画面 | 用途 | エンドポイント | 呼び出し方 | 認可（サーバ側） |
| --- | --- | --- | --- | --- |
| SC-09 | 属性辞書 一覧 / 追加 / 削除 | `GET|POST /bff/admin/authz/attributes`・`DELETE …/{id}` | `useQuery` / `useMutation` ＋ `apiFetch` | **AdminOnly**（401/403） |
| SC-09 | ポリシー 一覧 / 追加 / 有効切替 / 削除 | `GET|POST /bff/admin/authz/policies`・`PATCH …/{id}/active`・`DELETE …/{id}` | 同上 | **AdminOnly** |
| SC-10 | サマリ | `GET /bff/dashboard/summary?days=` | `useQuery` ＋ `apiFetch` | **AdminOnly** |
| SC-11 | 実効構成 / ドリフト / 履歴 | `GET /bff/admin/config`・`…/drift`・`…/history` | `useQuery` ×3 ＋ `apiFetch` | **`ConfigViewer`**（admin または operator）。**非権限は 404 で秘匿**（`RequireAuthorization` を付けず、無認証も 404 へ寄せる） |

- **3 群とも `docs/api/openapi.yaml` に無く orval 生成フックが存在しない**ため、`apiFetch` ＋ 手書き型で呼ぶ
  （ADR-0031 が許す 2 経路のうちの後者。出口は `foundation/api` の 1 箇所に収束している）。
  この欠落は **#506** の射程を広げる形で申し送る（#502 / #503 からの継続）。
- **キャッシュキー**: `['bff','admin','authz','attributes']` / `['bff','admin','authz','policies']` /
  `['bff','dashboard','summary',days]` / `['bff','admin','config']`・`['bff','admin','config','drift']`・
  `['bff','admin','config','history']`。変更操作の成功後は該当キーを `invalidateQueries` する
  （手で再取得を書かない。#503 の変異試験 M25〜M27 が固定した作法）。

### 7. 縮退（SC-11 は 3 本の問い合わせを独立に扱う）

実効構成・ドリフト・履歴は**別のエンドポイント**である。1 本の失敗で画面全体を落とさず、
**その領域だけを縮退させる**（旧実装が持っていた性質。#138 / #139 で確立した）。

| 問い合わせ | 失敗時 |
| --- | --- |
| 実効構成 | 404 → 中立文言（存在秘匿）／その他 → `role="alert"`。**この 1 本が落ちたら他の 2 領域も出さない**（構成が無い状態でドリフトだけ出しても読めない） |
| ドリフト | ドリフト領域のみ「利用できません」。ヘッダのバッジは出さない |
| 履歴 | 履歴領域のみ「利用できません」 |

### 8. 削除する旧実装

```text
src/knowledge/frontend/src/features/sc09-admin-abac/{AdminAbacSettingsPage.tsx,AdminAbacSettingsPage.test.tsx,index.tsx}
src/knowledge/frontend/src/features/sc10-operations/{OperationsDashboardPage.tsx,OperationsDashboardPage.test.tsx,index.tsx}
src/knowledge/frontend/src/features/sc11-config/{ConfigViewerPage.tsx,ConfigViewerPage.test.tsx,index.tsx}
```

同名のファイルを新しい内容で置き換えるのではなく、**削除してから作る**（計画の「完全に削除する」）。
**`sc11-config/access.test.tsx` は削除しない**——観点（許可 2 ロール・存在秘匿・API 未呼出・ナビの限定）は
新実装でも成立し、**#490 が確立した markup 一致の検査をここへ足す**（§テスト方針）。

## 受け入れ基準

issue #504 §受け入れ基準 を検証可能な形へ展開する。

- [ ] **SC-09〜11 が hi-fi モックアップと計画の画面仕様どおりに実装されている。**
      各画面仕様書の「hi-fi モックアップとの対応」表の全行が **実装した／実装しない（理由つき）** で埋まり、
      **部分未実装は備考に明記**されている。
- [ ] **権限外アクセスが存在秘匿になる。** 3 画面とも、権限外は `NotFound` を描画し、
      **未知パスの `NotFound` と markup が一致**し、**BFF を呼ばない**。
- [ ] **既存の `sc11-config/access.test.tsx` の観点が保存されている**（4 ケースとも新実装で成立し、
      markup 一致の検査が加わっている）。
- [ ] **SC-09 で FR-17 の保留対象を実装していない。** 「辺の型」の語が画面に現れないことを、
      **まず「見えるはずの条件」（属性体系・ポリシー定義のタブが在ること）を確かめてから** assert する。
- [ ] **旧実装が残っていない。** 3 画面は同じパスに置き直すため差分では上書きに見える。
      次の 4 検査で確かめる: (1) `useEffect` による取得が無い、(2) `apiFetch` の直接呼び出しが画面本体に無い
      （`use*.ts` に閉じる）、(3) インラインの `style={{…}}` が無い、(4) 未国際化リテラルが無い。
- [ ] **UC-05 の導線が通る。** SC-10 →「構成ビューア →」→ SC-11 の導線テスト。
- [ ] **未国際化リテラルが無い。** `eslint-plugin-lingui` の `files` に本 3 feature を追加した状態で
      `pnpm run lint` が **0 errors**。**発火確認**として、未国際化リテラルを混ぜると error になることを実測する。
- [ ] **i18n カタログが最新。** `pnpm run i18n` ＋ `git diff --exit-code` が green。
      `node scripts/check-i18n-catalogs.js` が green（未翻訳 0 件）。
- [ ] **カバレッジ床を割らない**（現行 88 / 88 / 84 / 83）。実測値を測定条件つきで記録し、
      引き上げの余地があれば **MSP 所有分 −5pt 切り捨て**の既存規則で引き上げる。**`coverage.exclude` は増やさない。**
- [ ] **変異試験**（壊すと落ちることの実測）を行い、結果を表で残す。**素通りしたものも書く。**
      **値集合・列挙は純関数テストで固定する**（#503 の M31）。
      **操作を跨いだ状態も見る**（#503 の M28 / M29）。
- [ ] `pnpm run typecheck` / `lint` / `test` / `test:coverage` / `build` / `test:e2e` が green。
- [ ] `node scripts/check-doc-links.js` / `check-commit-messages.js --base origin/develop` /
      `check-unit-dependencies.js` / `check-test-traceability.js` / `check-i18n-catalogs.js` /
      `check-static-egress.js --require src/platform/frontend/dist` が green。
- [ ] **`test-traceability-allowlist.json` を増やしていない。**
      **保留対象（FR-17 / FR-18）の ID をテストのコメントへ書かない**（#502 の是正で確立した運用）。
- [ ] **テスト仕様書のバックエンド試験の節を落としていない**（SC-09 の BFF 10 ケース・SC-11 の T-17 / T-18）。
      改訂前後で節の構成を突き合わせる。
- [ ] AST（submodule）の typecheck / lint / テストが**無改修で**通る。

## テスト方針

**受け入れ基準 → テストの写像**はテスト仕様書（`docs/tests/SC-{09,10,11}_*.md`）を正とする
（`check-test-traceability.js` の対象）。

| UC | 写像する | 写像しない（理由） |
| --- | --- | --- |
| **UC-05** | 基本 1（属性辞書の定義）・基本 2（ポリシーの定義）・**基本 3**（保存前の矛盾検証）・**例外**（矛盾があれば保存を拒否） | 認可判定への即時反映（`AbacEvaluator`）はサーバ側 |

| 層 | 対象 | 見るもの |
| --- | --- | --- |
| 純関数 | `abacVocabulary.ts` / `opsTools.ts` / `driftView.ts`（**3 つとも `*.test.ts` を持つ**） | 値集合（3 / 2 / 5 / 2 値）・条件の組み立て・導線の絞り込み・写像 |
| コンポーネント | SC-09 / SC-10 / SC-11 | 表示条件・**権限別の出し分け**・エラー状態・**存在秘匿の中立表示**・縮退・i18n（ja / en） |
| アクセス | `sc11-config/access.test.tsx`（**既存を引き継ぐ**） ＋ SC-09 / SC-10 の同型 | 許可ロール・**NotFound の markup 一致**・API 未呼出・ナビの限定 |
| 導線 | SC-10 → SC-11 | 運用者の導線（1 本のルータへ 2 ルートを載せる） |
| E2E | Playwright | 未認証で各ルートが `/login` へ誘導されること（ルートの実在も同時に固定される） |

## 検証（実測）

**測定条件**: worktree `feat/SC-09-11-admin-ops-screens`（`origin/develop` `cf0a0b0` 基点）／
Node 22.22.2 ／ pnpm 10.33.0 ／ Vitest 3.2.7（v8 provider）／
**submodule `src/ai-stock-trading` と `planning`（pin `d980a01`）は populate 済み**。
スコープは断りがない限り**ワークスペース全体**（`src/` の 4 パッケージ ＋ AST）である。

| 検査 | コマンド | 結果 |
| --- | --- | --- |
| 型検査 | `pnpm run typecheck` | green（4 パッケージ。AST は**無改修**） |
| lint | `pnpm run lint` | green（**0 errors / 9 warnings**。warning は全件 `react-refresh/only-export-components` で、本作業の着手前と同数） |
| 単体テスト | `pnpm run test` | **57 files / 536 tests** 全 green（本作業前は 53 files / 473 tests） |
| カバレッジ | `pnpm run test:coverage` | 後述（床を 88/88/84/83 → **90/90/88/85** へ引き上げ） |
| ビルド | `pnpm run build` | green（`dist/assets/index-*.js` 632.19 kB / gzip 189.84 kB） |
| E2E | `playwright test`（後述の条件） | **12 tests 全 green**（本作業で 1 本追加＝SC-09） |
| 生成物の乖離 | `pnpm run codegen` ＋ `git diff --exit-code -- …/generated` | green（差分なし） |
| i18n カタログ | `node scripts/check-i18n-catalogs.js` | green（2 ロケール・未翻訳 0 件。ja / en とも **320 件**。本作業で 142 件増） |
| ドキュメントリンク | `node scripts/check-doc-links.js` | green（419 件） |
| ユニット依存方向 | `node scripts/check-unit-dependencies.js` | green |
| テスト・トレーサビリティ | `node scripts/check-test-traceability.js` | green（仕様書のある 28 件中 28 件が写像済み。**allowlist は本 issue の着手前と同じ 7 件**＝増やしていない） |
| 静的 egress | `node scripts/check-static-egress.js --require src/platform/frontend/dist` | green（4 ファイル・検出 0 件） |
| コミット件名 | `node scripts/check-commit-messages.js --base origin/develop` | green（**件数はここに書かない**——この表を直すコミット自身が件数を変えるため。最終形は CI の `commit-messages` ジョブが検査する） |

**`pnpm run i18n` ＋ `git diff --exit-code -- …/locales` は「コミット後に差分が出ないこと」を見る検査である。**
本作業ではカタログを更新したため、コミット前の `git diff` は当然に差分を出す（HEAD との比較であるため）。
**コミット後に `pnpm run i18n` を再実行して差分が出ないことを確認した。**

### 受け入れ基準 1: hi-fi モックアップとの対応

3 つの画面仕様書に、hi-fi モック（planning `d980a01`）の**全要素を行番号つきで写像した表**を置いた。
粒度の規則は #502 / #503 と共通である（(a) メイン領域は個別に 1 行、(b) 共通シェルはまとめて 1 行、
(c) モックに無い状態は表外）。

**数え方は「対応表の行数」である**（要素名ではない）。同じ事象がモック上で 2 か所に描かれていれば
2 行になる——SC-09 の辺の型は「タブ」「注記」「状態例」の 3 行に分かれる。

| 画面 | 対応表の行数 | する | しない | 本画面では作らない | モックに無いが実装する |
| --- | --- | --- | --- | --- | --- |
| SC-09 | 19 行 | 11 行 | 7 行（**A: 保留 3**・**B: 契約の不在 2**・**C: 他 issue 1**・右レール 1） | 1 行 | 3 要素 |
| SC-10 | 17 行 | 8 行 | 8 行（**A: 保留 5**・**B: 契約の不在 2**・右レール 1） | 1 行 | 4 要素 |
| SC-11 | 19 行 | **17 行** | 1 行（右レール） | 1 行 | 2 要素 |

**「契約の不在」は要素名で数えて 6 件**（上表の**行数では 4 行**——条件式と一意利用者数は
「する」行の中の**部分未実装**であって独立した行を持たないため）——SC-09 のタグ辞書・検証ボタン・
**条件式の表現力**／SC-10 の SLO・LLM コスト・**一意利用者数**。
**同じ事象を数える基準が 2 つあるので、件数を書くときは必ず基準を添える。**

**さらに、行数基準では捕まらない部分未実装が 2 件ある**——

| # | 画面 | 対応表の判定 | 満たしていない制約 |
| --- | --- | --- | --- |
| 1 | SC-09 | ポリシー条件（#8 / #10）は「**する**」 | 計画の入力表「ポリシー条件｜**条件式**」の比較演算子・包含を満たさない（契約は集合所属のみ） |
| 2 | SC-10 | 利用状況カード（#4）は「**する**」 | モックの「**312 人/日**」は**一意利用者数**であり、実装が出すのは**件数**である |

**行の判定が二値（する／しない）だと、「する」行の中に隠れた部分未実装が集計に載らない**
（#503 が SC-05 のタグ辞書整合で踏んだ穴と同型）。**両方とも環流記録の提案 3・6 として渡した。**

**A（FR の着手保留）は要素名で数えて 2 件**（SC-09 の辺の型辞書／SC-10 のナレッジ健全性節。
**行数では 8 行**——辺の型が 3 行、ナレッジ健全性が 5 行に分かれるため）。

### 受け入れ基準 2: 旧実装が残っていない（実測）

3 画面は同じパスへ置き直したため、削除は差分上は置き換えに見える（ただし `git rm` を明示的に実行しており、
`git status` では `D` として現れた）。そのうえで §受け入れ基準 の 4 検査を実測した
（対象は `features/sc0{9,10,11}-*` の**実装ファイル**）。

| # | 検査 | 結果 |
| --- | --- | --- |
| 1 | `useEffect(` による取得が無い | **0 件**（`import` からの取り込みも 0 件） |
| 2 | 画面本体（`*Page.tsx` / `*Panel.tsx`）に `apiFetch(` の直接呼び出しが無い（`use*.ts` に閉じる） | **0 件** |
| 3 | インライン `style={{…}}` が無い | **0 件** |
| 4 | 未国際化リテラルが無い | `eslint`: **0 errors**（`eslint-plugin-lingui` の `files` へ本 3 feature を追加した状態） |

### 受け入れ基準 3: UC の導線

`knowledge/frontend/src/features/opsFlow.test.tsx`（2 ケース）が、**2 ルートを 1 本のルータへ載せて**
実際に遷移する。

1. SC-10（運用ダッシュボード）→「構成ビューア →」→ SC-11（構成バージョンの表示）。
2. 運用者は SC-11 へ直接到達できるが、SC-10 は `NotFound`（**計画との差異を固定するテスト**）。

**E2E では認証済みの導線を実走できない**（トークンは `InMemoryWebStorage` に保持され外部から注入できない）。
E2E は各ルートが**存在し認証ガードが先に効く**ことを見る。
**この環境では `playwright install` がブラウザを取得できない**ため、インストール済みの
`/opt/pw-browsers/chromium-1194` を `launchOptions.executablePath` で指すローカル専用 config を
一時的に置いて実走し、**確認後に削除した**（#490 / #496 / #502 / #503 と同じ作法）。
**リポジトリの `playwright.config.ts` は無改変である。**

### 受け入れ基準 4: カバレッジ床

| 集計 | lines/statements | branches | functions |
| --- | --- | --- | --- |
| 全ユニット横断（本 PR） | **95.91%**（5429/5660） | **89.79%**（1118/1245） | **91.81%**（415/452） |
| MSP 所有分（本 PR） | **95.22%**（4162/4371） | **90.72%**（851/938） | **93.06%**（322/346） |
| （参考）本作業前 `cf0a0b0` の MSP 所有分 | 93.94% | 88.56% | 89.80% |
| 床 | 88 → **90** | 83 → **85** | 84 → **88** |

MSP 所有分は `src/coverage/lcov.info` から `ai-stock-trading` のファイルを除いて再集計した値である
（`LF/LH`・`BRF/BRH`・`FNF/FNH` を全ファイルで合算）。導出規則は既存どおり**実測から 5pt 下・切り捨て**。
**`coverage.exclude` は増やしていない。**

### 変異試験（「壊すと落ちる」ことの実測）

**40 件を試し、うち 39 件は最初から落ちた。1 件（M8）が素通りしたので、テストを直して落ちることを再確認した。**
実行は「変異を当てる → 当該 feature のテストだけ走らせる → 必ず復元する」を機械化した
（`pnpm vitest run <feature>`）。M37〜M40 は lint / カタログ検査であり手で実行した。

| # | 壊した箇所 | 落ちたもの |
| --- | --- | --- |
| M1 | `POLICY_ACTIONS` から `manage` を落とす（**契約の値集合からの逸脱**） | `fixes exactly the three policy actions the contract defines`（1 件） |
| M2 | `ATTRIBUTE_SCOPES` から `user` を落とす | `fixes exactly the two attribute scopes the contract defines` ほか（複数） |
| M3 | 未知のアクションを「不明」へ丸める | `shows an unknown action or scope verbatim instead of hiding it`（1 件） |
| M4 | `buildConditions` の重複除去を外す | `does not duplicate a value that is added twice`（1 件） |
| M5 | `buildConditions` のスコープ振り分けを逆にする | `splits the accumulated conditions into user and document buckets by scope` ほか（複数） |
| M6 | `parseAllowedValues` の空要素除去を外す | `parses the comma separated allowed values and drops the blanks` ほか |
| M7 | SC-09 の 409 を通常のエラーと同じ扱いにする（tone とラベル） | `explains a 409 when deleting a referenced attribute`（1 件） |
| **M8** | SC-09（属性）の `beginOperation()` を外す | **初回は素通りした**（後述）。是正後は `shows only the latest operation result across different mutations`（1 件） |
| M8b | SC-09（ポリシー）の `beginOperation()` を外す | `clears a stale validation error when another policy operation succeeds`（1 件） |
| M9 | ポリシー切替後の `invalidateQueries` を外す | `refetches the policy list after a successful toggle`（1 件） |
| M10 | SC-09 へ**保留中の「辺の型」タブ**を足す | `does not render the edge-type dictionary (its requirement is on hold)`（1 件） |
| M11 | SC-09 へ**契約の無い「タグ辞書」タブ**を足す | `does not render the tag dictionary or a dry-run validate button`（1 件） |
| M12 | SC-09 へ**dry-run の「検証」ボタン**を足す | 同上（1 件） |
| M13 | 保存ボタンの `disabled` を外す | `refuses to save until the policy name is filled`（1 件） |
| M14 | 条件エディタの属性選択肢を属性辞書から切り離す | `builds a policy condition from the defined attributes only`（1 件） |
| M15 | SC-09 のポリシー取得失敗を 0 件表示へ縮退させる | `shows an error instead of an empty list when the query fails`（1 件） |
| M16 | SC-09 のルートガードを全ロールへ開く（**存在秘匿を壊す**） | アクセス 2 件（`hides existence …` / `produces the same not-found markup …`） |
| M17 | `opsTools` の定義から Kiali を落とす（**値集合からの逸脱**） | `fixes exactly the three tools the plan lists` ほか（複数） |
| M18 | `opsTools` の「未設定は落とす」規則を外す | `drops tools whose URL is not injected at runtime` ほか |
| M19 | 未知の利用イベント種別を「不明」へ丸める | `shows an unknown usage event type verbatim`（1 件） |
| M20 | SC-10 の 403 を中立化から外す（**権限の有無を開示する**） | `shows the same neutral message for a 403`（1 件） |
| M21 | SC-10 の 5xx まで中立文言へ寄せる（**障害を秘匿してしまう**） | `surfaces a server failure as an alert instead of the neutral message`（1 件） |
| M22 | `days` をキャッシュキーから外す | `starts at seven days and sends the selected period to the API`（1 件） |
| M23 | SC-10 へ**契約の無い SLO カード**を足す | `renders only the three KPI cards the contract can fill`（1 件） |
| M24 | SC-10 へ**保留中の「ナレッジ健全性」節**を足す | `does not render the knowledge-health section (its requirement is on hold)`（1 件） |
| M25 | SC-10 のルートガードを全ロールへ開く | アクセス 2 件 |
| M26 | `DRIFT_KINDS` から `BindingMismatch` を落とす（**契約の値集合からの逸脱**） | `fixes exactly the five drift kinds the contract emits` ほか |
| M27 | 深刻度 `Warning` の tone を琥珀から中立へ | `maps the severity Warning to a labelled badge`（1 件） |
| M28 | 未知のドリフト種別を「不明」へ丸める | `shows an unknown drift kind verbatim instead of hiding it`（1 件） |
| M29 | `hadDrift` の `null` を「なし」へ丸める（**3 値を 2 値へ潰す**） | `keeps the three-valued hadDrift distinct (true / false / unknown)`（1 件） |
| M30 | ドリフト取得不能でも全体バッジを出す | `degrades only the drift section when the drift query fails`（1 件） |
| M31 | 実効構成が取れなくても他の 2 領域を出す | `hides the drift and history sections when the effective config is unavailable`（1 件） |
| M32 | SC-11 の 404 を中立化から外す（`role="alert"` にする） | `shows a neutral message for a 404 without revealing whether it exists`（1 件） |
| M33 | チェーンのドリフト強調を無効化する | `lists the drift findings and highlights the affected stage`（1 件） |
| M34 | 再取得の `invalidateQueries` を外す | `refetches all three queries when the refresh button is pressed`（1 件） |
| M35 | SC-11 のルートガードから operator を外す（**計画の許可ロールを狭める**） | `grants access to platform-operator (ConfigViewer)` ほか |
| M36 | 無効段の `StatusBadge` を外す（**色〔淡色〕だけで意味を持たせる**） | `marks a terminal stage and labels a disabled stage with text, not only opacity`（1 件） |
| M37 | SC-11 へ**日本語の**未国際化リテラルを混ぜる | `eslint`: `lingui/no-unlocalized-strings` **1 error** |
| M38 | SC-11 へ**英語の**未国際化リテラル（空白を含む `Refresh configuration`）を混ぜる | 同 **1 error**（#503 の M21 が名指しした「1 語の ASCII は素通りする」限界の**外側**であることを再確認した） |
| M39 | `en` カタログの `msgstr` を 1 件空にする | `check-i18n-catalogs.js` が **exit 1** |
| M40 | 保留対象の ID（`FR-17` 等）をテストのコメントへ書く | `check-test-traceability.js` が **exit 1**（「実装先行・仕様書なし」。後述） |

#### 素通りした 1 件と、その是正

**M8（SC-09 の `beginOperation()`）**: 当初のテストは「属性の**削除**を 2 回」続けており、
**同じミューテーションを 2 回動かしただけ**だった。TanStack Query は同じミューテーションの
再実行時に自前で状態を入れ替えるため、`beginOperation()` を外しても落ちない。
**是正は「異なるミューテーションを跨ぐ」形へ改めた**——属性は「削除（409 で失敗）→ 追加（成功）」、
ポリシーは「保存（400 で失敗）→ 有効／無効の切替（成功）」。是正後は M8 / M8b とも落ちる。

**教訓（#503 の M28 / M29 の続き）**: #503 は「**どのテストも操作を 1 回しか行っていない**と
状態の残留が捕まらない」と書いた。本 issue で分かったのは**その先**である——
**操作を 2 回行っても、それが同じミューテーションなら捕まらない。**
`beginOperation()` が守っているのは**ミューテーション間**の状態であり、
テストも**別のミューテーションを跨ぐ**必要がある。

#### M40 の位置づけ（**検査器の側の話**）

`scripts/test-traceability-allowlist.json` は #502 の是正で
「**着手保留中の機能の ID をテストへ書かない**」という運用を明記している。
本 issue の初稿はテストのコメントに `IADR-0119: FR-17 / FR-18 …` と書いており、
`check-test-traceability.js` が **FR-17 / FR-18 / SC-12 を「実装が先行している」と報告**した（実測）。
**allowlist を増やして黙らせるのではなく、テスト側から ID を外した**
（保留の追跡は [[IADR-0119]] とプロダクトコードのコメント・画面仕様書が担う）。
M40 はこの検査が効いていることの実測である。

#### 「無いことを確かめるテスト」の作法（#502 の M3 の教訓）

M10〜M12・M23・M24 に対応する 5 件のテストは、**まず「見えるはずの条件」で描画されていることを
確かめてから**無いことを assert している（SC-09 は「2 つの区画が在ること」、SC-10 は「サマリが出ること」、
SC-11 は「再取得ボタンと注記が在ること」を先に見る）。この作法を採ったため、5 件とも初回から落ちた。

#### 間接被覆では足りない箇所（#503 の M31 の教訓）

M1 / M2 / M17 / M26 の 4 件は**値集合からの脱落**である。いずれも
**純関数テストが落ち、画面テストは落ちない**（画面テストは「その値がモックデータに含まれていたか」しか見ない）。
`abacVocabulary.ts` / `opsTools.ts` / `driftView.ts` の 3 ファイルに純関数テストを置いたのはこのためである
（[[IADR-0129]] 決定 6）。

## 計画書との差異

| 事項 | 計画の記載 | 実装 | 根拠 |
| --- | --- | --- | --- |
| **SC-09 / SC-10 の対応 FR・UC** | 05_screens 画面一覧: SC-09 = `FR-05, FR-09, FR-17` / `UC-05`、SC-10 = `FR-10 ＋ NFR` / `UC-05` | 計画のとおり | **issue #504 §スコープ の表は SC-09 を「FR-13・UC-08」、SC-10 を「UC-07」と書くが、計画と一致しない**（FR-13 = Wiki 閲覧〔SC-04〕、UC-07 = Wiki で閲覧する、UC-08 = 外部 AI エージェント〔SC-12〕）。**計画を正とした。**#503 でも同型の食い違い（SC-08 の UC）が起き、issue 側が訂正された先例がある。**訂正は親へ申し送る** |
| **SC-09 の辺の型辞書** | 05_screens §SC-09 §主要素・§辺の型（値集合）の管理（3 層構成・対称/非対称・逆向きの表示語・使用件数・削除拒否/改名） | **実装しない** | **FR-17 に属する**（計画自身が節見出しに明記。画面一覧も FR-17 を挙げる）。前提 **ADR-0033 は `Proposed`** であり [[IADR-0119]] 決定 2 の着手条件（`Accepted`）を満たさない。**裁定は不要**——前提 ADR の確定を待つ |
| **SC-09 のタグ辞書** | 05_screens §SC-09 §主要素・§タグ辞書の削除・改名（2026-08-02 確定） | **実装しない** | 値集合の照会・使用件数・改名の追随がいずれも契約に無い。§環流 提案 1 |
| **SC-09 の「検証」ボタン** | hi-fi 430 | **実装しない** | dry-run の検証エンドポイントが無い。検証は `POST` の 400 としてのみ得られる。§環流 提案 2 |
| **SC-09 のポリシー条件式** | 05_screens §SC-09 入力表「ポリシー条件｜必須｜**条件式**」・hi-fi 429（`<=`・「含む」） | **構造化エディタ**（属性 × 許可値。**部分未実装**） | 契約（`Dictionary<string, List<string>>`）は**集合所属**しか表現しない。計画の入力表 2 行目（「対象属性｜選択｜定義済み属性のみ」）は満たす。§環流 提案 3 |
| **SC-10 の SLO カード・LLM コストカード** | 05_screens §SC-10 §主要素・hi-fi 419 / 421 | **実装しない** | `DashboardSummaryDto` に該当項目が無い。§環流 提案 4・5 |
| **SC-10 の「人/日」** | hi-fi 420（`312人/日`） | **件数を出す**（**部分未実装**） | `UsagePointDto` はイベント件数であり利用者の一意性を持たない。§環流 提案 6 |
| **SC-10 のナレッジ健全性節** | 05_screens §SC-10 §ナレッジ健全性（4 KPI・辺の型ごとの使用件数・フォールバック警告・固定文言の注記） | **実装しない** | **FR-17 / FR-18 に属する**（計画自身が「起案・2026-08-01。Phase 3」と明記。指標は ADR-0033 に由来）。[[IADR-0119]]。**裁定は不要** |
| **SC-10 の閲覧ロール** | 05_screens §SC-10「**運用者・管理者**ロール限定」 | **`platform-admin` のみ**（据え置き） | `/bff/dashboard/summary` と後段 `DashboardService` がともに `AdminOnly`。画面だけ広げると「開くと必ず 403 になる画面」になる（[[IADR-0129]] 決定 4）。**planning#198 提案 8 と同じ類型だが向きが逆**。§環流 提案 7 |
| **SC-10 の副題** | hi-fi 417「SLO・利用状況・コスト（詳細は各専用ツールへ）」 | 「利用状況・検索傾向・回答品質（SLO・コストは Grafana で参照）」 | 出さないものを名乗ると読み手を誤らせる。**モックの文言を変えた唯一の箇所**である |
| **SC-09 の SC-12 への導線** | hi-fi 418「MCPクライアント管理 →」 | **実装しない** | 遷移先のルートが未実装（#445 待ち）。存在しない先へのリンクは押下時に `NotFound` を出し、**権限による秘匿と未実装が区別できなくなる** |
| **左ナビの表示名** | 05_screens §共通シェル・hi-fi 左レール（`ABAC設定` / `ダッシュボード` / `構成ビューア`） | 計画のとおりへ**是正**した | 従前の実装は「管理者設定」「運用ダッシュボード」だった。`Layout.test.tsx` の 2 ケースを追随させた |
| **SC-11 の未決事項 5**（draw.io ワイヤーフレーム） | — | **取り下げ**（`feedback/20260709_sc11-wireframe-drawio.md` を `closed` へ） | 計画は **HTML モックアップを正とし draw.io を作成しない**（`.drawio` は計画リポジトリに 1 件も存在せず、SC-11 の wireframe HTML は揃っている）。**計画側へ渡す作業自体が成立しない** |

## 環流（計画リポジトリへ）

**契約の不在 6 件**（要素名基準）を新しい環流記録
`feedback/20260805_sc09-11-admin-ops-contract-gaps.md` へ載せ、**起票は親が行う**。

| # | 論点 | planning#197 / planning#198 との関係 |
| --- | --- | --- |
| 1 | SC-09 の**タグ辞書**（値集合の照会・使用件数・改名の追随） | **planning#198 提案 7 の隣接**だが**別の論点**。planning#198 提案 7 は「**SC-05 の利用者**が辞書を引けるか」であり、本件は「**SC-09 自身**に辞書を編集する契約が無い」 |
| 2 | SC-09 の**dry-run 検証 API**（保存せず矛盾検証） | 新規 |
| 3 | SC-09 の**条件式の表現力**（比較演算子・包含） | 新規。**部分未実装**の側 |
| 4 | SC-10 の **SLO 指標**（達成率・p95） | 新規 |
| 5 | SC-10 の **LLM コスト** | 新規 |
| 6 | SC-10 の**一意利用者数（人/日）** | 新規 |
| 7 | SC-10 の**閲覧ロール**（計画=運用者・管理者／実装=管理者のみ） | **planning#198 提案 8 と同じ類型だが別の画面・逆向き**。planning#198 提案 8 は「計画は管理者限定だが実装が広い（SC-05/06/07）」、本件は「**計画は運用者も許すが実装が狭い**（SC-10）」。**planning#198 へ足すのではなく本記録で新たに問う** |

**planning#197（対象範囲フィルタ・検索結果 DTO）と同型の論点は本 issue に無い**——本 3 画面は
権限内候補の一覧を返す API を必要としないためである。**重複起票はしない。**

## 親への申し送り

### この PR で消化したもの

- SPA 移行の完了条件のうち **SC-09〜11 の 3 画面**（#452 の分割 3 本目・最後）。
  **これで旧 13 画面のうち #452 が引き受けた分は片付いた**（残るのは SC-12＝#445 待ちと SC-18〜21＝[[IADR-0119]] 待ち）。
- **左ナビの表示名を計画・モックへ是正**（`ABAC設定` / `ダッシュボード` / `構成ビューア`）。
- `eslint-plugin-lingui` の適用範囲を本 3 feature へ拡大（#502 が確立し #503 が継いだ運用）。
- **`docs/screens/SC-11` の未決事項 5（draw.io）を取り下げ**、環流記録を `closed` にした。

### 残るもの（引き受け先を明記する）

| 項目 | 引き受け先 |
| --- | --- |
| 契約の不在 6 件（**要素名基準**。SC-09 のタグ辞書・検証ボタン・条件式／SC-10 の SLO・LLM コスト・一意利用者数） | `feedback/20260805_sc09-11-admin-ops-contract-gaps.md`。**起票は親**（提案 1〜6） |
| **SC-10 の閲覧ロール**（計画=運用者・管理者／実装=管理者のみ） | 同記録の提案 7。**BFF ＋ 後段 ＋ 画面 ＋ テストを同時に変える必要がある** |
| **issue #504 §スコープ の表の訂正**（SC-09 = FR-13・UC-08 → FR-05/FR-09/FR-17・UC-05 ／ SC-10 = UC-07 → UC-05） | **親**（#503 で SC-08 の UC を訂正したのと同じ作法） |
| SC-09 の辺の型辞書・SC-10 のナレッジ健全性節 | [[IADR-0119]] の保留解除後（ADR-0033・0034・0035 の `Accepted` 化） |
| SC-12 | #445 待ち |
| SC-18〜21 | [[IADR-0119]] の保留解除後 |
| `docs/api/openapi.yaml` への `/bff/admin/authz`・`/bff/dashboard`・`/bff/admin/config` の追加 | **#506**（射程を広げる。計画の裁定は不要） |
| パンくず・権限バッジ | 共通シェルの作業（#452 系） |
| 右レール AI チャットパネル | 移行**第 4 段**（[[IADR-0121]] 決定 1・5） |
| `notify`（sonner トースト）の本番の呼び出し元 | 依然 **0 件**（#496 / #502 / #503 からの申し送りを引き継ぐ。本 issue も画面内 `Alert` を採った） |
| バンドルサイズ（632 kB / gzip 190 kB） | 全画面の再実装が終わってからのコード分割（#490 / #496 / #502 / #503 の未決事項を引き継ぐ）。**#503 の 586 kB から 46 kB 増えた**のは本 issue の 3 画面ぶんである |

### 注意（レビュー時に見てほしい点）

1. **SC-09 は計画の 4 区画のうち 2 区画しか無い**（辺の型＝保留／タグ辞書＝契約の不在）。
   **SC-10 は KPI カード 3 枚のいずれもモックと一致しない**（SLO・コストは契約に無く、
   利用状況は件数であって人数ではない）。**この 2 点は「作り忘れ」ではない**——
   理由と引き受け先は各画面仕様書の対応表と本書 §計画書との差異 にある。
2. **SC-10 の副題の文言をモックから変えた**（唯一の変更）。出さないものを名乗ると読み手を誤らせるためである。
3. **SC-10 の SC-11 導線から条件分岐を外した。** `platform-admin` ⊂ `ConfigViewer` であり、
   旧実装の `useHasAnyRole(Admin, Operator)` は**この画面では常に真**の到達しない分岐だった。
4. **カバレッジ床を 88/88/84/83 → 90/90/88/85 へ上げた。** `coverage.exclude` は増やしていない。
5. **変異試験 M8 が素通りした**——「操作を 2 回行っても、それが**同じミューテーション**なら
   状態の残留は捕まらない」。#503 の M28 / M29 の教訓の**続き**であり、本書 §変異試験 に記録した。

## 未決事項

1. **契約の不在 6 件 ＋ 閲覧ロール 1 件**（環流記録）。**起票は親**。裁定までは当該要素を実装しない。
2. **FR-17 / FR-18 の保留解除**（ADR-0033・0034・0035 の `Accepted` 化）。解除後に
   SC-09 の辺の型区画と SC-10 のナレッジ健全性節を実装する（[[IADR-0119]] 決定 6 の手順に従う）。
3. **ページング**（SC-09 の属性・ポリシー一覧）。計画が送り方を定めていない（SC-02 / SC-05 と同じ）。
4. **`docs/api/openapi.yaml` への `/bff/admin/authz`・`/bff/dashboard`・`/bff/admin/config` の追加**（#506 の射程）。
