---
title: SC-05〜08（文書管理・データソース管理・変換ジョブ・AI 分析ダッシュボード）の新スタックでの再実装
type: spec
status: done
related_ids: [SC-05, SC-06, SC-07, SC-08, UC-03, UC-04, UC-05, UC-06, UC-02, FR-01, FR-02, FR-06, FR-07, FR-09, FR-11, FR-12, ADR-0031, IADR-0119, IADR-0121, IADR-0124, IADR-0125, IADR-0126, IADR-0127]
author: Claude
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
  - "../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md"
  - "../../planning/projects/microservices-platform/INDEX.md"
related_specs:
  - ../screens/SC-05_document-management.md
  - ../screens/SC-06_datasource-management.md
  - ../screens/SC-07_conversion-jobs.md
  - ../screens/SC-08_ai-analysis-dashboard.md
  - ../tests/SC-05_document-management.md
  - ../tests/SC-06_datasource-management.md
  - ../tests/SC-07_conversion-jobs.md
  - ../tests/SC-08_ai-analysis-dashboard.md
  - ../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md
  - ../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md
  - ../adr/IADR-0121_spa-stack-migration-staging.md
  - ../adr/IADR-0124_tanstack-router-unit-composition.md
  - ../adr/IADR-0125_ui-primitives-i18n-catalog-and-storybook.md
  - ../adr/IADR-0126_sse-answer-state-and-search-url-state.md
  - ./20260804_issue-502_sc01-03-search-flow.md
---

# 仕様書: SC-05〜08 の新スタックでの再実装（管理者の運用導線 ＋ AI 分析）

> 本仕様書は実装着手前に作成した。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: **SC-05**（文書管理）・**SC-06**（データソース管理）・**SC-07**（変換ジョブ）・**SC-08**（AI 分析ダッシュボード）
- ユースケース（UC）: **UC-03**（文書を管理する）／**UC-04**（データソースを登録・同期する）／
  **UC-06**（文書を正規化変換する）／**UC-02**（AI 分析を依頼する）。
  着手時点の issue #503 は SC-08 の対応 UC を「UC-05」と書いていたが、**計画（05_screens 画面一覧・
  03_usecases UC-02 §関連画面）は SC-08 を UC-02 に対応づけている**。UC-05（ABAC 権限を管理する）の
  関連画面は SC-09 / SC-17 / SC-10 であり本 issue の 4 画面に含まれない。**本書は計画を正とし UC-02 を採った**。
  **issue 本文は 2026-08-05 に UC-02 へ訂正済みである**（実装側の写像は当初から計画どおりで、変更は無い）。
- 機能要求（FR）: FR-06 / FR-09（SC-05）・FR-01 / FR-02（SC-06）・FR-12（SC-07）・FR-07 / FR-11 / FR-05（SC-08）
- 関連 ADR（計画）:
  [ADR-0031](../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md)（Accepted。
  React 19 / Vite / **TanStack Router** / **TanStack Query** / Tailwind v4 ＋ shadcn/ui / Lingui。逸脱不可）
- 関連する技術検討（計画）:
  [13_frontend-stack](../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md)
  （**§shadcn/ui 派生の範囲 の 4 基準**・§実装への移行方針「**旧画面（13 画面）の完全削除**は移行の完了条件の一部」）／
  [08_data-egress-policy](../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md)／
  [INDEX](../../planning/projects/microservices-platform/INDEX.md) 決定 21（**色だけで意味を持たせない**）
- モックアップ（**実装の正**）:
  [hi-fi/sc-05.html](../../planning/projects/microservices-platform/05_screens/mockups/hi-fi/sc-05.html) /
  [sc-06.html](../../planning/projects/microservices-platform/05_screens/mockups/hi-fi/sc-06.html) /
  [sc-07.html](../../planning/projects/microservices-platform/05_screens/mockups/hi-fi/sc-07.html) /
  [sc-08.html](../../planning/projects/microservices-platform/05_screens/mockups/hi-fi/sc-08.html)
- 関連 IADR: **[[IADR-0127]]（本作業の内部設計判断。本書と対で読む）**・[[IADR-0119]]・[[IADR-0121]]・
  [[IADR-0124]]・[[IADR-0125]]・[[IADR-0126]]・[[IADR-0039]]・[[IADR-0041]]・[[IADR-0042]]
- 本リポジトリの起点: **#503**（親 #452 / #446 / #454。分割 1 本目 = #502＝PR #505 はマージ済み。連携 **#501**）

## 目的・背景

#502（PR #505）が利用者の主導線（SC-01〜03）を新スタックへ載せ替えた。本 issue は #452 の分割 2 本目として、
**管理者が使う一覧・詳細型の 4 画面**を新スタックで作り直し、旧実装を削除する。
4 画面をまとめるのは、いずれも `Table` / `Alert` / `Select` などの同じプリミティブ群を共有し、
まとめて作ると部品の使い方が揃うためである（issue #503 §スコープ）。

**SC-07 は計画が 2026-08-04 に API と状態モデルを確定した**（05_screens §SC-07 §データソース。環流 planning#191 への裁定）。
実装はこれに従う。API 側の管理者ロール強制の突合は **#501** が担当し、本 issue は**画面側のアクセス制御**を実装する。

## 対象範囲

### 対象

1. **SC-05 / SC-06 / SC-07 / SC-08 の再実装**（hi-fi モックアップと 05_screens を正とする）。
   - サーバ状態は **TanStack Query**（`useQuery` / `useMutation`）。
   - UI は **`@platform/ui`** のプリミティブ（**新規プリミティブは追加しない**。判定は §4）。
   - 文言は **Lingui のカタログ**（ja / en）。
2. **旧実装の削除**（`features/sc05-documents` / `sc06-datasources` / `sc07-conversions` / `sc08-analysis` の
   実装・テスト・index）——「置き換え」であって、上書きではなく**削除して作り直す**。
3. **SC-07 の再変換を管理者ロール限定にする**（計画 2026-08-04 確定）。§2。
4. **`eslint-plugin-lingui` の適用範囲を本 4 feature へ拡大**（#502 が確立した「画面を作り直すたびに `files` を伸ばす」運用）。
5. テスト（単体・純関数・E2E）とカバレッジ床の維持・引き上げ。

### 対象外（送り先を明記する。**繰り延べであって放棄ではない**）

| 事項 | 送り先 | 理由 |
| --- | --- | --- |
| **SC-07 の人手補正 2 ペイン編集**（変換結果の編集 ＋ 原本プレビュー ＋「補正して再登録」） | **契約拡張後**（本書 §環流） | 補正済み Markdown を受け取る API が無い。`retry` は**変換を最初からやり直す**もので、編集結果を受け取らない |
| **SC-06 の「次回同期」「同期異常（再試行中 N/M）」「設定」** | 同上 | ソース別のスケジュール・連続失敗回数・更新 API のいずれも契約に無い |
| **SC-05 の「変換」列** | 同上 | 文書 → 変換ジョブの対応を返す契約が無い（失敗ジョブは `documentId` を持たない） |
| **SC-08 の分析対象チップ（タグ／フォルダ）** | **planning#197 の裁定待ち**（**新規記録は作らない**） | SC-01 の対象範囲フィルタと**同型の論点**（権限内候補を返す API が無い）。§3 |
| **共通シェルのパンくず・権限バッジ** | 共通シェルの作業（#452 系） | `foundation/ui/Layout` の責務。#490 仕様書が #452 へ渡している |
| **右レール AI チャットパネル** | 移行**第 4 段** | [[IADR-0121]] 決定 1・5 |
| **API 側の管理者ロール強制の突合** | **#501** | issue #503 が明示的に分担している |
| SC-09〜SC-12 の再実装 | #452 の残り 1 分割（SC-09〜11）／ #445 待ち（SC-12） | 本 issue の分割方針 |
| SC-18〜21 | [[IADR-0119]] の保留解除後 | 前提 ADR が `Proposed` |
| `oidc-client-ts` の撤去 | 第 3 段（#439） | [[IADR-0121]] 決定 6 |

## 設計

内部設計の判断（選択肢の比較・棄却理由）は [[IADR-0127]] を正とする。本節は実装の形を記す。
画面ごとの詳細は画面仕様書（[SC-05](../screens/SC-05_document-management.md) /
[SC-06](../screens/SC-06_datasource-management.md) / [SC-07](../screens/SC-07_conversion-jobs.md) /
[SC-08](../screens/SC-08_ai-analysis-dashboard.md)）を正とする。

### 1. ファイル構成

```text
src/knowledge/frontend/src/features/
├── abac/
│   └── confidentiality.ts         機密区分（ABAC 文書属性）の値集合 — **画面ではなく語彙の単位**
├── sc05-documents/
│   ├── index.tsx                  ルート（/admin/documents）＋ ナビ項目
│   ├── DocumentManagementPage.tsx 画面（一覧 ＋ 編集フォーム）
│   ├── DocumentForm.tsx           新規登録／編集フォーム（1 つの部品で両用）
│   ├── useDocumentAdmin.ts        useQuery（一覧）＋ useMutation（作成・更新・公開・アーカイブ・削除）
│   └── *.test.ts(x)
├── sc06-datasources/
│   ├── index.tsx                  ルート（/admin/sources）＋ ナビ項目
│   ├── DataSourceManagementPage.tsx
│   ├── DataSourceForm.tsx         ソース登録フォーム
│   ├── useDataSources.ts          useQuery ＋ useMutation（登録・手動同期・無効化）
│   ├── syncState.ts               同期状態の導出（status × lastSyncedAt）— 純関数
│   └── *.test.ts(x)
├── sc07-conversions/
│   ├── index.tsx                  ルート（/admin/conversions）＋ ナビ項目
│   ├── ConversionJobsPage.tsx
│   ├── useConversionJobs.ts       useQuery（?status=）＋ useMutation（retry）
│   ├── jobStatus.ts               4 値 → tone / ラベル / 再変換可否 — 純関数
│   └── *.test.ts(x)
└── sc08-analysis/
    ├── index.tsx                  ルート（/analyze）＋ ナビ項目
    ├── AnalysisDashboardPage.tsx
    ├── useAnalysisTask.ts         orval 生成フック（useAnalysisAnalyze）のラッパ
    ├── analysisRange.ts           入力 → AnalysisDataRange の組み立て — 純関数
    └── *.test.ts(x)
```

**純関数を別ファイルへ出す**のは、判定（同期状態・ジョブ状態・範囲の組み立て）を
DOM を描かずに試験できるようにするためである（#502 と同じ作法）。

**`features/abac/` を新設する理由**: SC-05（文書の機密区分。必須）と SC-06（データソースの既定の機密区分）が
**同じ値集合**を使う。どちらかの画面フォルダへ置くともう一方が「文書管理画面に依存するデータソース管理画面」に
なり、別々に定数を持つと値集合が増えたとき片方だけ更新されて静かに割れる（**旧実装は実際に 2 箇所へ複製していた**）。
画面ではなく**語彙の単位**で 1 ファイルだけ置く。`eslint-plugin-lingui` の適用範囲にも加える。

### 2. SC-07 の権限（計画 2026-08-04 確定への追随）

計画（05_screens §SC-07 §データソース）:

> **再変換の実行権限は管理者ロールに限る**（2026-08-04 確定）。本画面のアクセス制御と API の権限を揃える
> —— API 側だけ緩いと画面の制御が意味を持たないためである。

| 対象 | 現状（`de55761`） | 本 issue の実装 |
| --- | --- | --- |
| SC-07 の**閲覧**（ルート・ナビ） | `platform-admin` **または** `platform-operator`（[[IADR-0039]] / [[IADR-0042]]） | **変えない**（[[IADR-0127]] 決定 1） |
| SC-07 の**再変換の実行** | 画面はロールを見ずに `failed` 行へボタンを出す | **`platform-admin` のみ**にボタンを出す。運用者には出さない |
| 再変換 API（`POST /bff/conversion/jobs/{id}/retry`） | admin **または** operator | **#501 の射程**（本 issue は触らない） |

**本 PR の状態は計画確定事項の未達である。** 計画（`01_screens.md:257`）は
「本画面のアクセス制御と API の権限を**揃える**」と確定しており、画面側だけを狭めた本 PR は
これを満たしていない。**解消は #501（再変換 API を admin 限定にする）であり、#503 の直後に片付ける。**
**API を直接叩ける運用者は依然 retry でき、画面の制御はその穴を塞がない。**
計画側の裁定は不要である（計画は既に admin 限定と確定している）——要るのは実装の追随だけである
（[[IADR-0127]] 決定 1）。

### 3. 実装しない画面要素（**モックに描かれているのに実装しないもの**）

**後から「作り忘れ」と誤解されないよう、各画面仕様書へ行番号つきの対応表を置く。**
理由は 3 種類に分類する（#502 の A / B に、本 issue で C を追加）。

| 種別 | 対象 | 根拠 |
| --- | --- | --- |
| **A. FR の着手保留** | （本 issue では**該当なし**。SC-05〜08 はいずれも FR-01/02/06/07/09/11/12 に属し、[[IADR-0119]] の保留対象 FR-17〜21 を含まない） | [[IADR-0119]] 決定 1・2 |
| **B. 契約の不在** | SC-05 の変換列／SC-06 の次回同期・同期異常（再試行中 N/M）・設定／SC-07 の人手補正 2 ペイン・デッドレターの内訳表示／SC-08 の分析対象チップ（タグ・フォルダ） | BFF ＋ 後段サービスの契約に載る先が無い。実測は各画面仕様書 |
| **C. 他 issue の射程** | 共通シェルのパンくず・権限バッジ（#452 系）／右レール AI チャットパネル（第 4 段）／再変換 API の認可（#501） | 引き受け先が既に決まっている |

**B の環流先の切り分け**（重複起票を避けるため明示する）:

| B の項目 | 環流先 |
| --- | --- |
| SC-08 の分析対象チップ（タグ／フォルダ） | **planning#197**（SC-01 の対象範囲フィルタと同型＝「**権限内候補**を返す API が無い」）。**新しい記録は作らない** |
| SC-05 の変換列／SC-06 の次回同期・同期異常・設定／SC-07 の人手補正 2 ペイン | **新規の環流記録**（`feedback/20260805_sc05-07-admin-contract-gaps.md`）。planning#197 と論点が異なる |

**「動かない UI を置く」形は採らない**（#502 で確立）。押しても結果が変わらないトグルや常に空の列は、
計画が画面へ与えた役割（管理者が状況を正確に把握し操作する）をむしろ損なう。

### 3-b. モックに無いが実装する要素（**逆向きの漏れも名指しする**）

対応表はモック → 実装の一方向しか見ない。実装にあってモックに無い要素を書かないと、
「勝手に足した機能」と「計画が別の場所で要求している機能」の区別がつかなくなる。

| 画面 | 要素 | 計画上の根拠 |
| --- | --- | --- |
| SC-05 | 公開 / アーカイブ / 削除の行操作 | 05_screens §SC-05 目的「正規化文書の**CRUD**・版管理」／ FR-06「文書の**CRUD**・バージョン管理・メタデータ管理」／ [[IADR-0041]]（Accepted） |
| SC-06 | 無効化の行操作 | FR-01「データソースを**登録・同期し、カタログ化**する」のライフサイクル／ [[IADR-0039]]（Accepted） |
| SC-08 | タスク種別（分析 / 比較 / 抽出）の選択 | FR-07「AI に対し、指定データ範囲での**分析・比較・抽出**を依頼できる」。選択肢が無いと比較・抽出へ到達できない |
| SC-08 | モデル・トークン数の脚注 | 02_requirements トレーサビリティ表 **FR-11 → SC-08（モデル振り分けの利用面）**／ [[IADR-0111]]（空 model ＝ AI へ未送信の縮退） |

### 4. UI 部品（`@platform/ui`）— **新規プリミティブは追加しない**

計画 [13_frontend-stack §shadcn/ui 派生の範囲](../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md)
の 4 基準で判定する。本 issue で必要になった唯一の新しい部品は SC-05 の**タグ編集**（`経理 ✕　規程 ✕　＋`）である。

| 基準 | タグ編集（削除可能なチップ ＋ 追加欄） | 判定理由 |
| --- | --- | --- |
| 1. フォーカストラップ | **非該当** | モーダルでもポップオーバーでもない。通常フローに置く |
| 2. ロービングタブインデックス等の複合キーボード操作 | **非該当** | チップの削除は個々の `<button>`。既定のタブ順で足りる |
| 3. ポータル／ポップアップの配置計算 | **非該当** | 候補リストのポップアップを持たない（タグ辞書の補完は計画に無い） |
| 4. `aria-*` の動的な同期を要する開閉状態 | **非該当** | 開閉状態を持たない |

→ **Radix を使わない。** さらに **`@platform/ui` へも入れない**——タグ（「既定辞書に整合」）は
**ドメイン語彙**であり、[[IADR-0125]] 決定 1 が共有 UI へ入れることを禁じている。
既存の `Tag`（分類名）＋ `Button` ＋ `Input` を feature 内で組み合わせる。

**`Tag` と `StatusBadge` の使い分けを踏襲する**（#502 が確立）:

| 用途 | 部品 | 理由 |
| --- | --- | --- |
| 機密区分（SC-05）・データソース種別（SC-06）・タスク種別 | **`Tag`** | **分類の名前**。意味は文字が担う |
| 同期状態（SC-06）・ジョブ状態（SC-07） | **`StatusBadge`** | **状態**。`tone → 固定アイコン` で色 ＋ アイコン ＋ テキストを型で強制する（INDEX 決定 21） |

`StatusBadge` を分類名に使うと `Info` アイコンが付いて意味が変わるため、逆向きの流用もしない。

### 5. 状態表示（INDEX 決定 21）

**SC-07 のジョブ状態（4 値）**——計画確定のモデルをそのまま写す。

| `status` | 表示 | `StatusBadge` の `tone` | アイコン（`tone` から自動） |
| --- | --- | --- | --- |
| `queued` | 待機中 | `neutral` | `Info` |
| `processing` | 変換中 | `neutral` | `Info` |
| `succeeded` | 完了 | `success` | `CircleCheck` |
| `failed` | 失敗 | `danger` | `CircleX` |
| 上記以外（未知の値） | 生値をそのまま | `neutral` | `Info` |

**SC-06 の同期状態**——契約にある値（`status` × `lastSyncedAt`）だけから導く。

| 条件 | 表示 | `tone` |
| --- | --- | --- |
| `status = disabled` | 無効 | `neutral` |
| `status = active` かつ `lastSyncedAt` あり | 同期済み（日時） | `success` |
| `status = active` かつ `lastSyncedAt` なし | 未同期 | `neutral` |
| （同期異常） | — | **`warning`（琥珀）は空けておく** |

**琥珀はどの状態にも充てない**（05_screens モック間相違の確定 ②「SC-06 の**同期異常表示**の警告色＝琥珀」）。
琥珀が指すのは**異常**であり、管理者が意図して無効化した**正常な設定状態**ではない。
モックが琥珀を充てた「⚠ 再試行中（3/5）」そのものは連続失敗回数の契約が無く実装しない（B）ため、
**契約が同期健全性を持つまで琥珀を保留する**（[[IADR-0127]] 決定 2。色の割当も環流記録の提案 3 に含めた）。

### 6. データソース（BFF 境界）

| 画面 | 用途 | エンドポイント | 呼び出し方 | 認可（サーバ側） |
| --- | --- | --- | --- | --- |
| SC-05 | 一覧 | `GET /bff/documents` | `useQuery` ＋ `apiFetch` | 認証（ABAC スコープ内のみ） |
| SC-05 | 作成 / 更新 / 公開 / アーカイブ / 削除 | `POST|PUT|POST /publish|POST /archive|DELETE /bff/documents[/{id}]` | `useMutation` ＋ `apiFetch` | admin または operator |
| SC-06 | 一覧 / 登録 / 手動同期 / 無効化 | `GET|POST /bff/datasources`・`POST /bff/datasources/{id}/sync`・`DELETE /bff/datasources/{id}` | 同上 | admin または operator |
| SC-07 | 一覧（`?status=`） / 再変換 | `GET /bff/conversion/jobs`・`POST /bff/conversion/jobs/{id}/retry` | 同上 | admin または operator（**#501** が retry を admin のみへ） |
| SC-08 | 分析実行 | `POST /bff/analysis/analyze` | **orval 生成フック `useAnalysisAnalyze`** | 認証（ABAC は後段が narrowing-only で適用） |

- **SC-08 だけが orval 生成フックに載る。** `docs/api/openapi.yaml` に `/bff/analysis/analyze` があるためである。
  **SC-05 / 06 / 07 の 3 群（`/bff/documents` / `/bff/datasources` / `/bff/conversion/jobs`）は openapi.yaml に無く、
  生成フックが存在しない**ため `apiFetch` ＋ 手書き型で呼ぶ（ADR-0031 が許す 2 経路のうちの後者）。
  この欠落は **#506**（SC-03 で同じ問題が出て起票済み）の射程を広げる形で申し送る（§親への申し送り）。
- **キャッシュキー**: `['bff','documents']` / `['bff','datasources']` / `['bff','conversion','jobs',status]`。
  変更操作の成功後は該当キーを `invalidateQueries` する（手で再取得を書かない）。

### 7. 削除する旧実装

```text
src/knowledge/frontend/src/features/sc05-documents/{DocumentManagementPage.tsx,DocumentManagementPage.test.tsx,index.tsx}
src/knowledge/frontend/src/features/sc06-datasources/{DataSourceManagementPage.tsx,DataSourceManagementPage.test.tsx,index.tsx}
src/knowledge/frontend/src/features/sc07-conversions/{ConversionJobsPage.tsx,ConversionJobsPage.test.tsx,index.tsx}
src/knowledge/frontend/src/features/sc08-analysis/{AnalysisDashboardPage.tsx,AnalysisDashboardPage.test.tsx,index.tsx}
```

同名のファイルを新しい内容で置き換えるのではなく、**削除してから作る**（計画の「完全に削除する」）。
差分では上書きに見えるため、**旧実装の構造が 1 行も残らないこと**を機械検査で確かめる（§検証）。

## 受け入れ基準

issue #503 §受け入れ基準 を検証可能な形へ展開する。

- [ ] **SC-05〜08 が hi-fi モックアップと計画の画面仕様どおりに実装されている。**
      各画面仕様書の「hi-fi モックアップとの対応」表の全行が **実装した／実装しない（理由つき）** で埋まっている。
- [ ] **SC-07 が計画確定の 4 状態モデル・直列化・管理者ロール限定に従っている。**
      4 値の表示（§5）・`processing` 中の 409 を「実行中のため受け付けられない」と伝えること・
      **再変換ボタンが `platform-admin` にのみ出ること**をテストで固定する。
- [ ] **旧実装が残っていない。** 4 画面は同じパスに置き直すため `git log --diff-filter=D` では現れない。
      次の 4 検査で確かめる（§検証 に実測を記録）:
      (1) `useEffect` による取得が無い、(2) `apiFetch` の直接呼び出しが画面本体に無い（`use*.ts` に閉じる）、
      (3) インラインの `style={{…}}` が無い、(4) 未国際化リテラルが無い（`eslint-plugin-lingui` が 0 errors）。
- [ ] **UC の導線が通る。** SC-06 →（変換ジョブを見る）→ SC-07 →（結果 →）→ SC-03 の導線テスト。
- [ ] **未国際化リテラルが無い。** `eslint-plugin-lingui` の `files` に本 4 feature を追加した状態で
      `pnpm run lint` が **0 errors**。**発火確認**として、未国際化リテラルを混ぜると error になることを実測する。
- [ ] **i18n カタログが最新。** `pnpm run i18n` ＋ `git diff --exit-code` が green。
      `node scripts/check-i18n-catalogs.js` が green（未翻訳 0 件）。
- [ ] **カバレッジ床を割らない**（現行 88 / 88 / 82 / 81）。実測値を測定条件つきで記録し、
      引き上げの余地があれば **MSP 所有分 −5pt 切り捨て**の既存規則で引き上げる。**`coverage.exclude` は増やさない。**
- [ ] **変異試験**（壊すと落ちることの実測）を行い、結果を表で残す。
      **保留対象・非表示要素を検証するテストは、まず「見えるはずの条件」で描画されていることを確かめてから
      無いことを assert する**（#502 の M3 の教訓）。
- [ ] `pnpm run typecheck` / `lint` / `test` / `test:coverage` / `build` / `test:e2e` が green。
- [ ] `node scripts/check-doc-links.js` / `check-commit-messages.js --base origin/develop` /
      `check-unit-dependencies.js` / `check-test-traceability.js` / `check-i18n-catalogs.js` /
      `check-static-egress.js --require src/platform/frontend/dist` が green。
- [ ] **`test-traceability-allowlist.json` を増やしていない。**
- [ ] AST（submodule）の typecheck / lint / テストが**無改修で**通る。

## テスト方針

**受け入れ基準 → テストの写像**はテスト仕様書（`docs/tests/SC-0{5,6,7,8}_*.md`）を正とする
（`check-test-traceability.js` の対象）。写像する範囲は次のとおり。

| UC | 写像する | 写像しない（理由） |
| --- | --- | --- |
| **UC-03** | 基本 1（登録／更新と属性・タグ設定）・**例外**（必須属性未設定は保存拒否） | 基本 2（取り込みイベント発行・索引と Wiki への反映）はサーバ側 |
| **UC-04** | 基本 1（ソース登録）・**代替**（手動同期）・**例外**（接続失敗のアラート表示） | 基本 2（定期取得）はサーバ側の hosted service |
| **UC-06** | **代替**（変換ジョブの状況照会・失敗した変換の再実行）・**例外**（恒久失敗＝`failed` の表示） | 基本 1〜4（受領・pandoc・図の LLM コード化・登録）はワーカー側 |
| **UC-02** | 基本 1（分析対象と分析内容の指定）・基本 3（LLM ゲートウェイ経由の実行）・基本 4（結果と出典）・**代替**（セルフホスト LLM への振り分けの**利用面**＝モデル脚注）・**例外**（権限外は対象から除外し権限の有無を開示しない） | 基本 2（認可による範囲限定）はサーバ側（narrowing-only） |

| 層 | 対象 | 見るもの |
| --- | --- | --- |
| 純関数 | `syncState.ts` / `jobStatus.ts` / `analysisRange.ts` / `confidentiality.ts`（**4 つとも `*.test.ts` を持つ**） | 状態の導出・4 値の写像・範囲の組み立て・値集合 |
| コンポーネント | SC-05 / 06 / 07 / 08 | 表示条件・**権限別の出し分け**・エラー状態・存在秘匿の中立表示・i18n（ja / en） |
| 導線 | SC-06 → SC-07 → SC-03 | 管理者の運用フロー（1 本のルータへ 3 ルートを載せる） |
| E2E | Playwright | 未認証で各ルートが `/login` へ誘導されること（ルートの実在も同時に固定される） |

- **権限別の出し分け**: SC-05 / 06 / 07 は `RequireRole`（admin または operator）配下であり、
  **ロールを持たない利用者には `NotFound` が出る**（存在秘匿）。SC-07 の再変換ボタンは
  **`platform-admin` のみ**に出る。SC-08 はロール限定が無い。
- **変異試験**: 「壊すと落ちる」ことを実測する。結果は §検証 に表で記録する。

## 検証（実測）

**測定条件**: worktree `feat/SC-05-08-admin-screens`（`origin/develop` `de55761` 基点）／
Node 22.22.2 ／ pnpm 10.33.0 ／ Vitest 3.2.7（v8 provider）／ TypeScript 5.9.3 ／ Vite 6.4.3 ／
Lingui 6.6.0 ／ **submodule `src/ai-stock-trading` と `planning`（pin `d980a01`）は populate 済み**。
スコープは断りがない限り**ワークスペース全体**（`src/` の 4 パッケージ ＋ AST）である。

| 検査 | コマンド | 結果 |
| --- | --- | --- |
| 型検査 | `pnpm run typecheck` | green（4 パッケージ。AST は**無改修**） |
| lint | `pnpm run lint` | green（**0 errors / 9 warnings**。warning は全件 `react-refresh/only-export-components` で、本作業の着手前と同数） |
| 単体テスト | `pnpm run test` | **53 files / 473 tests** 全 green（本作業前は 52 files / 423 tests。レビュー / 監査の是正で 4 件を追加し、`abac/confidentiality.test.ts` の 1 ファイルが増えた） |
| カバレッジ | `pnpm run test:coverage` | 後述（床を 88/81/82 → **88/83/84** へ引き上げ） |
| ビルド | `pnpm run build` | green（`dist/assets/index-*.js` 585.83 kB / gzip 174.95 kB） |
| E2E | `playwright test`（後述の条件） | **11 tests 全 green**（本作業で 3 本追加） |
| 生成物の乖離 | `pnpm run codegen` ＋ `git diff --exit-code -- …/generated` | green（差分なし） |
| i18n カタログ | `node scripts/check-i18n-catalogs.js` | green（2 ロケール・未翻訳 0 件。ja / en とも **178 件**） |
| ドキュメントリンク | `node scripts/check-doc-links.js` | green（415 件） |
| ユニット依存方向 | `node scripts/check-unit-dependencies.js` | green |
| テスト・トレーサビリティ | `node scripts/check-test-traceability.js` | green（仕様書のある 28 件中 28 件が写像済み。**allowlist は本 issue の着手前と同じ 7 件**＝増やしていない） |
| 静的 egress | `node scripts/check-static-egress.js --require src/platform/frontend/dist` | green（4 ファイル・検出 0 件） |
| コミット件名 | `node scripts/check-commit-messages.js --base origin/develop` | green（**件数はここに書かない**——この表を直すコミット自身が件数を変えるため。最終形は CI の `commit-messages` ジョブが検査する） |

**`pnpm run i18n` ＋ `git diff --exit-code -- …/locales` は「コミット後に差分が出ないこと」を見る検査である。**
本作業ではカタログを更新したため、コミット前の `git diff` は当然に差分を出す（HEAD との比較であるため）。
**コミット後に `pnpm run i18n` を再実行して差分が出ないことを確認した。**

### 受け入れ基準 1: hi-fi モックアップとの対応

4 つの画面仕様書に、hi-fi モック（planning `d980a01`）の**全要素を行番号つきで写像した表**を置いた。
粒度の規則は #502 の 3 画面と共通である（(a) メイン領域は個別に 1 行、(b) 共通シェルはまとめて 1 行、
(c) モックに無い状態は表外）。

**数え方は「対応表の行数」である**（要素名ではない）。同じ事象がモック上で 2 か所に描かれていれば
2 行になる——SC-07 の人手補正は「行操作のボタン」と「下部のパネル」の 2 行である。

| 画面 | 対応表の行数 | 実装する | 実装しない | モックに無いが実装する |
| --- | --- | --- | --- | --- |
| SC-05 | 15 行 | 11 行 | 4 行（**契約の不在 2**〔変換列・機密区分の表示名〕・共通シェル 2） | 2 要素（公開/アーカイブ/削除・変更メモ） |
| SC-06 | 13 行 | 8 行 | 5 行（**契約の不在 3**〔再試行中・次回同期・設定〕・共通シェル 2） | 2 要素（登録フォームの項目・無効化） |
| SC-07 | 14 行 | 9 行 | 5 行（**契約の不在 3**〔デッドレター内訳・人手補正のボタン・人手補正のパネル〕・共通シェル 2） | 1 要素（SC-06 へ戻るリンク） |
| SC-08 | 12 行 | 9 行 | 3 行（**契約の不在 1**〔タグ／フォルダのチップ〕・共通シェル 2） | 2 要素（タスク種別・モデル脚注） |

**「契約の不在」は要素名で数えて 8 件**（上表の**行数では 9 行**——SC-07 の人手補正が 2 行に分かれるため）
——SC-05 の変換列・機密区分の表示名／SC-06 の再試行中・次回同期・設定／
SC-07 のデッドレター内訳・人手補正／SC-08 のタグ／フォルダのチップ。このうち
**機密区分の表示名（#502 から継続）と SC-08 のチップは planning#197 の射程**であり、
**残る 6 件を新しい環流記録へ載せた**（`feedback/20260805_sc05-07-admin-contract-gaps.md`）。
**同じ事象を数える基準が 2 つあるので、件数を書くときは必ず基準を添える。**

**さらに、行数基準では捕まらない部分未実装が 1 件ある**——**SC-05 のタグ辞書整合**である
（[SC-05 対応表](../screens/SC-05_document-management.md) row 11 は追加・削除の UI を実装したので「**する**」だが、
計画が同じ行で課している「**既定タグ辞書に整合**」という制約は満たしていない）。
**行の判定が二値（する／しない）だと、「する」行の中に隠れた部分未実装が集計に載らない。**
これは #502 の「数え方の割れ」と同じ場所の残り火である——**行で数える**と決めたこと自体は良いが、
**判定を二値にした時点で「一部だけ実装した」が表現できなくなる**。
以後、件数を書くときは基準（行数／要素名）に加えて、**二値判定の外にある部分未実装**も併記する。

### 受け入れ基準 2: 旧実装が残っていない（実測）

4 画面は同じパスへ置き直したため、削除は差分上は置き換えに見える（ただし
`git rm` を明示的に実行しており、`git status` では `D` として現れた）。
そのうえで §受け入れ基準 の 4 検査を実測した（対象は `features/sc0{5,6,7,8}-*` の**実装ファイル**）。

| # | 検査 | 結果 |
| --- | --- | --- |
| 1 | `useEffect(` による取得が無い | **0 件**（`import` からの取り込みも 0 件） |
| 2 | 画面本体（`*Page.tsx` / `*Form.tsx`）に `apiFetch(` の直接呼び出しが無い（`use*.ts` に閉じる） | **0 件** |
| 3 | インライン `style={{…}}` が無い | **0 件** |
| 4 | 未国際化リテラルが無い | `eslint`: **0 errors**（`eslint-plugin-lingui` の `files` へ本 4 feature ＋ `abac/` を追加した状態） |

### 受け入れ基準 3: UC の導線

`knowledge/frontend/src/features/adminFlow.test.tsx`（3 ケース）が、**4 ルートを 1 本のルータへ載せて**
実際に遷移する。

1. SC-06（データソース）→「変換ジョブの状況を見る →」→ SC-07 →「結果 →」→ SC-03（本文表示）。
2. SC-07 →「← データソース管理へ戻る」→ SC-06。
3. SC-05（文書管理）→ 一覧のタイトル → SC-03。

**E2E では認証済みの導線を実走できない**（トークンは `InMemoryWebStorage` に保持され外部から注入できない）。
E2E は各ルートが**存在し認証ガードが先に効く**ことを見る（ルート未登録なら `NotFound` が出て
`/login` へ行かないため、この 1 本でルートの実在も固定できる）。
**この環境では `playwright install` がブラウザを取得できない**ため、インストール済みの
`/opt/pw-browsers/chromium-1194` を `launchOptions.executablePath` で指すローカル専用 config を
一時的に置いて実走し、**確認後に削除した**（#490 / #496 / #502 と同じ作法）。
**リポジトリの `playwright.config.ts` は無改変である。**

### 受け入れ基準 4: カバレッジ床

| 集計 | lines/statements | branches | functions |
| --- | --- | --- | --- |
| 全ユニット横断（本 PR・**レビュー / 監査の是正後**） | **95.06%** | **88.13%** | **89.25%**（357/400） |
| MSP 所有分（本 PR・同上） | **93.94%** | **88.56%** | **89.80%**（264/294） |
| （参考）是正前 `f11d4c3` の全ユニット横断 | 95.05% | 87.97% | **89.19%**（355/398） |
| （参考）本作業前 `de55761` の MSP 所有分 | 93.08% | 86.30% | 87.69% |
| 床 | 88（据え置き） | 81 → **83** | 82 → **84** |

MSP 所有分は `src/coverage/lcov.info` から `ai-stock-trading` のファイルを除いて再集計した値である
（`LF/LH`・`BRF/BRH`・`FNF/FNH` を全ファイルで合算）。導出規則は既存どおり**実測から 5pt 下・切り捨て**
（lines は 93.94 − 5 = 88.94 の切り捨てで 88 のまま）。**`coverage.exclude` は増やしていない。**

**［2026-08-05 是正・PR #508 のレビュー / 監査 P-3］** 本表は当初、全ユニット横断の functions を
**89.20%** と書いていたが、その時点（`f11d4c3`）の実測は **89.19%（355/398）**であり 0.01pt の転記誤りだった
（`f11d4c3` の worktree で `pnpm run test:coverage` を再実走して確認した。355/398 = 89.1959…% であり、
v8 の要約は 2 桁で**切り捨てる**——同じ実行の statements 4724/4970 = 95.0503…% が 95.05% と出ることで確かめられる）。
上表の本 PR 行はレビュー / 監査の是正（別ミューテーションの失敗バナー・琥珀の充て先・機密区分の純関数テスト）を
反映した**再測定値**である。functions の分母が 398 → 400 に増えたのは `beginOperation()` を 2 画面へ足したためで、
2 つとも被覆されている。**床の導出には影響しない**（是正の前後いずれの実測でも 88 / 88 / 84 / 83 が出る）。

### 変異試験（「壊すと落ちる」ことの実測）

**27 件を試し、うち 24 件は最初から落ちた。3 件が素通りしたので、テストを足して落ちることを再確認した。**
**その後、PR #508 のレビュー・監査の是正で 4 件（M28 / M29 / M30 / M31）を追加し、M5 を取り下げた（合計 30 件）。**

| # | 壊した箇所 | 落ちたもの |
| --- | --- | --- |
| M1 | SC-07 の再変換可否を運用者へも開く（`useHasAnyRole(Admin, Operator)`） | `hides the retry button from an operator and says why`（1 件） |
| M2 | `isRetryable` に `processing` を足す（**直列化を壊す**） | `allows retry only for failed jobs` ＋ `offers no retry for jobs that are not failed`（**2 件**） |
| M3 | 未知のジョブ状態を「不明」へ丸める | `shows an unknown status verbatim instead of hiding it`（1 件） |
| **M4** | SC-07 の 409 を通常のエラーと同じ扱いにする（tone とラベル） | **初回は素通りした**（後述）。是正後は `explains the 409 rejection as a serialisation conflict`（1 件） |
| ~~M5~~ | ~~SC-06 の `disabled` から琥珀の警告を外す~~ | **取り下げ**（琥珀を `disabled` へ充てること自体を是正した。M30 を参照） |
| M6 | SC-05 の 409 を通常のエラーと同じ扱いにする | `explains a 409 version conflict`（1 件） |
| M7 | SC-08 で 403/404 を存在秘匿へ寄せない（**権限を開示する**） | `shows the same neutral message for a 403` ＋ `… for a 404`（**2 件**） |
| M8 | SC-08 の空回答の縮退を検出しない | `shows the same neutral message for an empty answer (server-side degradation)`（1 件） |
| M9 | SC-08 で空の `range` を常に付ける | `omits the range entirely when no search condition is given`（1 件） |
| M10 | SC-05 でアーカイブ済みにも公開ボタンを出す | `offers publish only for unpublished documents`（1 件） |
| M11 | SC-05 で必須未設定でも保存できるようにする（**UC-03 例外を壊す**） | `refuses to save until the required title is filled (UC-03 exception flow)`（1 件） |
| M12 | SC-05 の更新で既存属性（部門）を落とす | `updates a document with the optimistic-lock version and the change note`（1 件） |
| M13 | SC-06 で無効なソースにも無効化ボタンを出す | `disables an active source and offers no disable action for a disabled one`（1 件） |
| M14 | SC-06 の取得失敗を 0 件表示へ縮退させる | `shows an error instead of an empty list when the query fails`（1 件） |
| M15（参考） | SC-07 のルートガードの `anyOf` を空にする | 同ファイルの **13 件**（存在秘匿が全面的に効いていることの確認） |
| M16 | SC-05 へ**契約に無い「変換」列**を足す | `does not render the conversion column (no contract links a document to its job)`（1 件） |
| M17 | SC-06 へ**契約に無い「次回同期」列**を足す | `does not render the next-sync column, the retry state, or a settings action`（1 件） |
| M18 | SC-07 へ**保存先の無い「人手補正」ボタン**を足す | `does not render the manual-correction pane (no contract to save into)`（1 件） |
| M19 | SC-08 へ**権限内候補の契約が無いタグチップ**を足す | `does not render tag or folder chips (no contract for permitted candidates)`（1 件） |
| M20 | SC-06 へ**日本語の**未国際化リテラルを混ぜる | `eslint`: `lingui/no-unlocalized-strings` **1 error** |
| **M21** | SC-08 へ**英語の**未国際化リテラルを混ぜる | **`Result`（空白なし ASCII 1 語）では素通りした**（後述）。`Analysis result` では **1 error** |
| M22 | `en` カタログの `msgstr` を 1 件空にする | `check-i18n-catalogs.js` が **exit 1** |
| M23 | SC-07 の状態フィルタを API へ送らない | `sends the status filter to the query API`（1 件） |
| M24 | SC-06 で未知の種別を空欄へ丸める | `shows an unknown source type verbatim`（1 件） |
| **M25** | SC-05 の更新成功後の `invalidateQueries` を外す | **初回は素通りした**（後述）。是正後は `refetches the list after a successful save`（1 件） |
| **M26** | SC-07 の再変換成功後の `invalidateQueries` を外す | **初回は素通りした**（後述）。是正後は `refetches the list after a successful retry`（1 件） |
| M27 | SC-06 の手動同期成功後の `invalidateQueries` を外す | `refetches the list after a successful sync`（1 件。M26 と同時に足したテスト） |
| **M28** | SC-05 の `beginOperation()` を外す（**是正前の実装そのもの**＝別のミューテーションの失敗状態を残す） | `shows only the latest operation result (a stale failure banner does not survive)`（1 件） |
| **M29** | SC-06 の `beginOperation()` を外す（同上） | `shows only the latest operation result (neither a stale failure nor a stale success survives)`（1 件） |
| **M30** | SC-06 の `disabled` へ琥珀（`warning`）を充て直す（**是正前の実装そのもの**） | `marks a disabled source as neutral, leaving amber for a real sync fault` ＋ `never uses the amber warning tone for any state the contract can express`（**2 件**） |
| **M31** | `CONFIDENTIALITY_VALUES` から `restricted` を落とす（**ABAC 一次情報からの逸脱**） | `fixes exactly the four values of the ABAC attribute dictionary`（1 件）。**画面テストは 1 件も落ちない**——選択肢の数を数えているテストが無いため、値集合の欠落は画面経由では検出できない |

#### 素通りした 3 件と、その是正

**M4（SC-07 / SC-05 の 409 の tone）**: `tone` を `warning` から `danger` へ変えても、テストは
**文言だけ**を見ていたため落ちなかった。文言（「このジョブは再変換できません…」）は同じ `isConflict()` で
選ばれるが**別の場所**にあり、tone だけが無検査だった。
**是正は tone を検査可能にする形**で行った——`Alert` の**ラベルの文言も tone に揃える**
（`warning` なら「注意」、`danger` なら「エラー」）。これは INDEX 決定 21 の敷衍でもある：
琥珀のアイコンに「エラー」と書かれていると、**色を除いたときに 409（拒否）と 5xx（障害）の区別が消える**。
SC-05 側も同じ形に揃え、M6 として実測した。

**M21（英語の未国際化リテラル）**: `<Trans>結果</Trans>` を `Result` に置き換えても error が出なかった。
これは**穴ではなく `eslint.config.js` が明記している既知の限界**である——
`ignore: ['^[a-z0-9-]+$', '^[A-Za-z0-9_./:#$?&=@%+-]*$']` は
「空白を含まない ASCII トークン（`Docs` 等）は素通りする。識別子・列挙値・ルート ID・クラス名の断片と
区別できないため、これは意図的に残す」と書かれている。**空白を含む英語の文章（`Analysis result`）では
1 error が出る**ことを実測した。#502 の M8 と同じ検査だが、**1 語の英単語では発火しない**点を本書で明記する。

**M25 / M26（`invalidateQueries` の欠落）**: 更新系の成功後に一覧が取り直されることを**誰も見ていなかった**。
[[IADR-0127]] 決定 5 が定めた挙動そのものが無検査だったということであり、外れると
「保存したのに一覧が古いまま」という、旧実装が `load()` を手で呼び直して防いでいた不具合が復活する。
**3 画面それぞれに再取得の回数を数えるテストを足し**（SC-05 / SC-06 / SC-07）、M25〜M27 で落ちることを確認した。

**M28 / M29（別ミューテーションの古い失敗バナー）**: PR #508 の AI レビューが指摘した**実在する欠陥**である。
既存の 27 件はいずれもこれを捕まえていなかった。**どのテストも 1 回の操作しか行っておらず、**
**「操作を 2 つ続ける」経路が無かった**ためである。是正（[[IADR-0127]] 決定 7）の前後で
**是正前は 2 件とも落ち、是正後は通る**ことを実測した。
**教訓**: 変異試験は「壊したら落ちるか」を測るが、**そもそも到達しない経路は壊しても落ちない**。
複数の操作を持つ画面には「操作を跨いだ後の表示」を 1 本置く。

**「無いことを確かめるテスト」の作法**（#502 の M3 の教訓）: M16〜M19 の 4 件は
**まず「見えるはずの条件」で描画されていることを確かめてから**無いことを assert している
（例: SC-07 は「管理者として再変換ボタンが在ること」を先に見てから人手補正が無いことを見る）。
この作法を採ったため、4 件とも初回から落ちた。

## 計画書との差異

| 事項 | 計画の記載 | 実装 | 根拠 |
| --- | --- | --- | --- |
| **SC-08 の対応 UC**（**解消済み**） | 着手時点の issue #503 の表は「UC-05」（**2026-08-05 に UC-02 へ訂正済み**） | **UC-02** として写像する | 計画 05_screens 画面一覧が SC-08 → UC-02、03_usecases UC-02 §関連画面が SC-08 を挙げる。UC-05 の関連画面は SC-09 / SC-17 / SC-10。**計画を正とした**。issue 側の訂正により**計画・issue・実装の 3 者が一致した**（本行は経緯の記録） |
| **SC-05〜07 の閲覧ロール** | 05_screens §共通シェル に加え、**§SC-05（`01_screens.md:234`。「モックの『管理』バッジ準拠」と根拠つき）・§SC-06（`:242`）・§SC-07（`:250`）の各節が独立して**「管理者ロール限定」と定める | **admin または operator**（据え置き） | [[IADR-0039]]（Accepted・2026-07-08）が「データソース・変換ジョブ・文書 CRUD はいずれも運用／コンテンツ管理者の職務」として operator を含めた既存決定。**計画 4 箇所と正面から食い違う**ため、どちらが正かは**計画側の裁定**（planning#198 提案 8）を要する。**2026-08-04 の確定は「再変換の実行権限」に限られる**ため、本 issue はそこだけを狭める（[[IADR-0127]] 決定 1） |
| **SC-07 の再変換の権限** | 05_screens §SC-07（`01_screens.md:257`）「再変換の実行権限は管理者ロールに限る。**本画面のアクセス制御と API の権限を揃える**」 | **画面は `platform-admin` のみ**。API は admin/operator のまま | **計画確定事項の未達**（§2）。正当化しない——解消は **#501**（#503 の直後）。API を直接叩ける運用者は依然 retry でき、画面の制御はその穴を塞がない。**計画側の裁定は不要**（実装の追随だけが要る） |
| **SC-05 の「変換」列** | 05_screens §SC-05 主要素「変換状況」・hi-fi の「変換」列 | **実装しない** | 文書 → 変換ジョブの対応を返す契約が無い。なお 02_requirements トレーサビリティ表（2026-07-24 是正）は **FR-12 の関連画面から SC-05 を外している**（「SC-05 はモックの FR バッジ準拠で対象外」）。§環流 |
| **SC-06 の「次回同期」列・「⚠ 再試行中（3/5）」・「設定」** | hi-fi の同名要素 | **実装しない** | ソース別スケジュール・連続失敗回数・更新 API のいずれも契約に無い。§環流 |
| **SC-06 の琥珀（警告色）の充て先** | `01_screens.md:125`（モック間相違の確定 ②）・`:241`「同期異常は警告表示（警告色＝琥珀）」 | **どの状態にも充てない**（`disabled` は中立） | 琥珀が指すのは**異常**であり、管理者が意図した無効化＝正常な設定状態ではない。契約が同期健全性を持つまで空けておく（[[IADR-0127]] 決定 2）。**色の割当の裁定も環流記録の提案 3 に含めた** |
| **SC-07 の人手補正 2 ペイン** | 05_screens §SC-07 主要素「人手補正の2ペイン編集」 | **実装しない**（再変換は実装する） | 補正済み Markdown を受け取る API が無い。§環流 |
| **SC-07 の「デッドレター」表示** | 05_screens §SC-07「デッドレター状態の表示は `failed` の内訳」 | **`failed` として表示し、内訳は区別しない** | `ConversionJobDto` にデッドレターの標識が無い。§環流 |
| **SC-08 の分析対象チップ（タグ／フォルダ）** | 05_screens §SC-08 主要素「タグ・フォルダのチップ＋検索条件による追加」 | **検索条件（`range.query`）のみ実装** | **権限内**のタグ／フォルダ候補を返す API が無い（SC-01 と同型）。**planning#197 の裁定待ち** |
| **SC-05 の機密区分の表示名** | 05_screens §SC-05・hi-fi 421「社内限」/ 422「秘」（**4 値中 2 値だけ**に表示名がある） | **生値**（`internal` / `confidential` …）を一覧に出す | #502（SC-03）と同じ扱い。実装が残り 2 値の表示名を決めると、それが事実上の用語定義になる。**planning#197 の裁定待ち** |

| **SC-05 のタグ入力の辞書整合** | 05_screens §SC-05 入力表（`01_screens.md:232`）「タグ｜任意｜**複数選択**｜**既定タグ辞書に整合**」 | **自由入力のチップ**（辞書整合の制約を持たない） | タグ辞書は `/bff/admin/authz`（**システム管理者限定**）にあり、SC-05 の利用者（admin / operator）が引ける保証が無い。**画面仕様書の対応表では row 11 を「する」と判定している**（追加・削除の UI は実装したため）が、**辞書整合という制約は満たしていない**。環流記録の提案 7 として planning#198 へ渡した |

**上表は SC-05 の行である。SC-08 は機密区分を表示しない**——SC-08 の hi-fi 対応表に機密区分の要素は無く、
生値を出しているのは SC-05 の一覧である（[2026-08-05 是正]。従来この行は「SC-08 の機密区分の表示名」と
名乗りながら「本画面では機密区分を表示しない」と書いており、同一行の中で矛盾していた）。

## 親への申し送り

### この PR で消化したもの

- SPA 移行の完了条件のうち **SC-05〜08 の 4 画面**（#452 の分割 2 本目）。
- **計画 2026-08-04 確定（SC-07）の画面側の実装**——4 状態モデル・状態フィルタ・
  **再変換の管理者ロール限定**・直列化（409 の扱いを含む）。
- `eslint-plugin-lingui` の適用範囲を本 4 feature ＋ `abac/` へ拡大（#502 が確立した運用の継続）。

### 残るもの（引き受け先を明記する）

| 項目 | 引き受け先 |
| --- | --- |
| **SC-07 再変換 API の管理者ロール強制** | **#501**（#503 の直後に片付ける）。**#501 が閉じるまで計画確定事項（`01_screens.md:257`「画面と API の権限を揃える」）は未達である**——API を直接叩ける運用者は依然 retry でき、画面の制御はその穴を塞がない（[[IADR-0127]] 決定 1） |
| 契約の不在 6 件（**要素名基準**。SC-05 の変換列／SC-06 の再試行中・次回同期・設定／SC-07 のデッドレター内訳・人手補正） | `feedback/20260805_sc05-07-admin-contract-gaps.md`。**planning#198 として起票済み・裁定待ち** |
| SC-08 のタグ／フォルダのチップ・SC-05 の機密区分の表示名 | **planning#197 の裁定待ち**（#502 から継続。**新規起票はしない**） |
| `docs/api/openapi.yaml` への `/bff/datasources` / `/bff/conversion/jobs` の追加と、`AiAnswerDto.citations` の型の是正 | **#506**（射程を広げる。計画の裁定は不要） |
| SC-05/06/07 の**閲覧ロール**（管理者のみか運用者も含むか） | 計画側の裁定（環流記録 §提案 8） |
| SC-09〜SC-11 の再実装 | #452 の残り 1 分割 |
| SC-12 | #445 待ち |
| SC-18〜21 | [[IADR-0119]] の保留解除後 |
| パンくず・権限バッジ | 共通シェルの作業（#452 系） |
| 右レール AI チャットパネル | 移行**第 4 段**（[[IADR-0121]] 決定 1・5） |
| `notify`（sonner トースト）の本番の呼び出し元 | 依然 **0 件**（#496 / #502 からの申し送りを引き継ぐ。本 issue は画面内 `Alert` を採った。[[IADR-0127]] 決定 6） |
| バンドルサイズ（586 kB / gzip 175 kB） | 全画面の再実装が終わってからのコード分割（#490 / #496 / #502 の未決事項を引き継ぐ） |

### 注意（レビュー時に見てほしい点）

1. **`features/abac/` を新設した。** SC-05 と SC-06 が同じ機密区分の値集合を使うため、
   どちらかの画面フォルダに置くともう一方が「文書管理画面に依存するデータソース管理画面」になる。
   別々に定数を持つと値集合が増えたとき片方だけ更新されて静かに割れる（**旧実装は実際に 2 箇所へ複製していた**）。
   画面ではなく**語彙の単位**で 1 ファイルだけ置いた。
2. **`Alert` のラベル文言を tone に揃える形へ改めた**（変異試験 M4 の是正）。琥珀のアイコンに
   「エラー」と書かれていると、色を除いたときに 409（拒否）と 5xx（障害）の区別が消える。
   INDEX 決定 21 の敷衍であり、**#502 の 3 画面には同じ形の箇所が無い**（SC-01〜03 は
   `warning` を UC-01 例外フローの縮退にだけ使っており、そこは文言も「注意」である）。
3. **カバレッジ床の branches を 81 → 83、functions を 82 → 84 へ上げた。** 伸びの一部は
   「取得・再取得の分岐が TanStack Query 側へ寄って**測るべき分岐そのものが減った**」ことに由来する
   （#502 と同じ向きの効果）。

## 未決事項

1. **契約の不在 6 件**（環流記録）。**planning#198 として起票済み**であり、実装の再開は裁定の後になる。
2. **SC-08 のタグ／フォルダのチップ・機密区分の表示名**（planning#197）。裁定待ち。
3. **再変換の権限（計画確定事項の未達）**（#501）。計画側の裁定は不要で、要るのは実装の追随だけである。#501 の完了をもって解消する。
4. **SC-05/06/07 の閲覧ロール**。計画 §共通シェル（管理者）と [[IADR-0039]]（admin/operator）の差異。
5. **ページング**（SC-05 / SC-06 / SC-07）。計画が送り方を定めていない（SC-02 と同じ）。
   実装は BFF が返す一覧をそのまま表示する。
