---
title: SC-01〜03（検索・結果一覧・文書詳細）の新スタックでの再実装 — 利用者の主導線
type: spec
status: done
related_ids: [SC-01, SC-02, SC-03, UC-01, UC-02, FR-03, FR-04, FR-05, FR-08, FR-12, ADR-0031, IADR-0119, IADR-0121, IADR-0124, IADR-0125, IADR-0126]
author: Claude
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
  - "../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md"
  - "../../planning/projects/microservices-platform/INDEX.md"
related_specs:
  - ../screens/SC-01_search-chat.md
  - ../screens/SC-02_search-results.md
  - ../screens/SC-03_document-detail.md
  - ../tests/SC-01_search-chat.md
  - ../tests/SC-02_search-results.md
  - ../tests/SC-03_document-detail.md
  - ../adr/IADR-0126_sse-answer-state-and-search-url-state.md
  - ../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md
  - ../adr/IADR-0121_spa-stack-migration-staging.md
  - ../adr/IADR-0124_tanstack-router-unit-composition.md
  - ../adr/IADR-0125_ui-primitives-i18n-catalog-and-storybook.md
  - ./20260804_issue-496_ui-i18n-storybook.md
  - ./20260804_issue-490_spa-router-shell.md
---

# 仕様書: SC-01〜03 の新スタックでの再実装（利用者の主導線）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: **SC-01**（検索／チャット質問。**本システムの主入口**）・**SC-02**（検索結果一覧）・**SC-03**（文書詳細／プレビュー）
- ユースケース（UC）: **UC-01**（検索・質問する）／ **UC-02**（AI 分析を依頼する。**出典から SC-03 へ到達する経路**として本 issue に含まれる）
- 機能要求（FR）: FR-03（ハイブリッド検索）・FR-04（根拠付き AI 回答）・FR-05（ABAC）・FR-08（フィードバック）・FR-12（正規化文書の閲覧面）
- 関連 ADR（計画）:
  [ADR-0031](../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md)（Accepted。
  React 19 / Vite / **TanStack Router** / **TanStack Query** / Tailwind v4 ＋ shadcn/ui / Lingui。逸脱不可）
- 関連する技術検討（計画）:
  [13_frontend-stack](../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md)
  （**§shadcn/ui 派生の範囲 の 4 基準**・§実装への移行方針「**旧画面（13 画面）の完全削除**は移行の完了条件の一部」）／
  [08_data-egress-policy](../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md)（外部 CDN・Web フォント・analytics を使わない）／
  [INDEX](../../planning/projects/microservices-platform/INDEX.md) 決定 21（色だけで意味を持たせない）・決定 26（`private-note` の画面ラベルは「個人資料」）
- モックアップ（**実装の正**）:
  [hi-fi/sc-01.html](../../planning/projects/microservices-platform/05_screens/mockups/hi-fi/sc-01.html) /
  [sc-02.html](../../planning/projects/microservices-platform/05_screens/mockups/hi-fi/sc-02.html) /
  [sc-03.html](../../planning/projects/microservices-platform/05_screens/mockups/hi-fi/sc-03.html)
- 関連 IADR: **[[IADR-0119]]（FR-17〜21 の着手保留）**・[[IADR-0121]]・[[IADR-0124]]・[[IADR-0125]]・**[[IADR-0126]]（本作業の内部設計判断。本書と対で読む）**
- 本リポジトリの起点: **#502**（親 #452 / #446 / #454。前提 #490＝PR #495・#496＝PR #499 はマージ済み）

## 目的・背景

[13_frontend-stack §実装への移行方針](../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md)
（2026-08-04 追補）が「**旧画面（13 画面）の完全削除**は移行の完了条件の一部であり、段階分割によって
省略されるものではない」と定めている。#490（ルータ・共通シェル）と #496（プリミティブ・i18n・Storybook）で
土台は揃ったが、**画面そのものは旧スタックのまま**である（#496 §親への申し送り「プリミティブの
本番の呼び出し元は現時点で 0 件」）。

本 issue は #452 から分割された 1 本目として、**利用者の主導線（検索 → 結果 → 文書詳細）**を成す
SC-01〜03 を新スタックで作り直し、旧実装を削除する。3 画面を 1 本にまとめるのは、
途中で切ると導線そのものを検証できないためである。

## 対象範囲

### 対象

1. **SC-01 / SC-02 / SC-03 の再実装**（hi-fi モックアップと 05_screens を正とする）。
   - サーバー状態は **TanStack Query**（[[IADR-0126]]）。
   - UI は **`@platform/ui`** のプリミティブ。
   - 文言は **Lingui のカタログ**（ja / en）。
2. **旧実装の削除**（`features/sc01-search` / `sc02-results` / `sc03-document` の実装・テスト・index）。
   ——「置き換え」であって、上書きではなく**削除して作り直す**。
3. **`Tag` プリミティブの新設**（`@platform/ui`）。判定は §3。
4. **共通シェルのナビ項目の i18n 対応**（`NavItem.label` が翻訳を持てるようにする）。§4。
5. **テスト用ハーネスの拡張**（`renderUnitRoute` に `QueryClientProvider` と `I18nProvider` を入れる）。§5。
6. **`eslint-plugin-lingui` の適用範囲を本 3 feature へ拡大**（#496 §親への申し送りの引き受け）。
7. テスト（単体・導線・E2E）とカバレッジ床の維持。

### 対象外（送り先を明記する。**繰り延べであって放棄ではない**）

| 事項 | 送り先 | 理由 |
| --- | --- | --- |
| **SC-03 の AI 提案承認欄**（FR-18） | **保留解除後の後続 issue** | [[IADR-0119]] 決定 1「保留の対象は当該 FR を実現するプロダクトコードと、**その受け入れを担う画面**」。着手条件（決定 2）は前提 ADR の **`Accepted`** 化であり、`ADR-0033` / `ADR-0034` / `ADR-0035` は planning `d980a01` 時点で **`Proposed`** |
| **SC-03 の SC-18（ナレッジグラフ）への導線**（FR-17） | 同上 | 同上（保留中の画面へ送る導線） |
| **SC-01 の個人資料まわり**（「個人資料を含める」トグル・出典行の `👤`／「個人資料（自分のみ）」）（FR-19 / FR-21） | 同上 | 同上。`ADR-0036` / `ADR-0037` も `Proposed` |
| **SC-01 の対象範囲フィルタ**（タグ／フォルダ） | 契約拡張後（環流済み） | AI 回答要求が属性フィルタを取らず、**権限内候補**を返す API も無い。§2 と `feedback/20260804_sc01-03-bff-contract-gaps.md` |
| **SC-02 の検索モード切替・並び順・更新日時列** | 同上 | 検索 API／DTO に該当の指定軸・項目が無い。§2 |
| **SC-03 の機密区分の表示名の翻訳** | 同上 | 計画が 4 値中 2 値の表示名しか持たない。生値を出す |
| パンくず・権限バッジ | 共通シェルの作業（#452 系） | `foundation/ui/Layout` の責務。#490 仕様書が #452 へ渡している |
| 右レール AI チャットパネル | 移行**第 4 段** | [[IADR-0121]] 決定 1・5 |
| Markdown のレンダリング | 別 issue（要 IADR） | 依存追加とサニタイズ方針の決定を伴う。§2 |
| SC-04〜SC-12 の再実装 | #449 / #452 の残り 2 分割 | 本 issue の分割方針 |
| `oidc-client-ts` の撤去 | 第 3 段（#439） | [[IADR-0121]] 決定 6 |

## 設計

内部設計の判断（選択肢の比較・棄却理由）は [[IADR-0126]] を正とする。本節は実装の形を記す。
画面ごとの詳細は画面仕様書（[SC-01](../screens/SC-01_search-chat.md) /
[SC-02](../screens/SC-02_search-results.md) / [SC-03](../screens/SC-03_document-detail.md)）を正とする。

### 1. ファイル構成

```text
src/knowledge/frontend/src/features/
├── sc01-search/
│   ├── index.tsx              ルート（/ask）＋ナビ項目
│   ├── SearchChatPage.tsx     画面
│   ├── useAskStream.ts        SSE 購読（useMutation ＋ ローカル蓄積。IADR-0126 決定 1）
│   ├── citations.ts           出典の種別判定（📄 / 📖）— 純関数
│   └── *.test.ts(x)
├── sc02-results/
│   ├── index.tsx              ルート（/search?q=）＋ナビ項目
│   ├── SearchResultsPage.tsx
│   ├── useSearchQuery.ts      useQuery（キー ['bff','search',q]）
│   └── *.test.tsx
└── sc03-document/
    ├── index.tsx              ルート（/docs/$id）
    ├── DocumentDetailPage.tsx
    ├── useDocumentQueries.ts  useQuery ×3（詳細・本文・版履歴）
    ├── attributes.ts          属性キー → 表示ラベルの写像 — 純関数
    └── *.test.tsx
```

**純関数を別ファイルへ出す**のは、判定（出典の種別・属性ラベル）を DOM を描かずに試験できるようにするためである。

### 2. 実装しない画面要素（**モックに描かれているのに実装しないもの**）

**後から「作り忘れ」と誤解されないよう、各画面仕様書へ行番号つきの対応表を置いた。**
理由は 2 種類しかない。

| 種別 | 対象 | 根拠 |
| --- | --- | --- |
| **A. FR の着手保留** | SC-03 の AI 提案承認欄・SC-18 導線／SC-01 の個人資料トグル・`👤` 出典 | [[IADR-0119]] 決定 1・2。前提 ADR が `Proposed` |
| **B. 契約の不在** | SC-01 の対象範囲フィルタ／SC-02 の検索モード・並び順・更新日時列／SC-03 の機密区分表示名 | BFF ＋ 検索サービスの契約に載る先が無い。実測は `feedback/20260804_sc01-03-bff-contract-gaps.md` |

B は**計画へ環流した**（同ファイル。反映先候補は FR-03 / FR-04 の要求更新・SC-01 / SC-02 の画面更新・用語追加）。

**「動かない UI を置く」形は採らない。** 押しても結果が変わらないトグルや常に空の列は、
計画が画面へ与えた役割（権限内の結果を正確に見せる）をむしろ損なう。

### 3. `Tag` プリミティブの新設（4 基準の判定）

計画 [13_frontend-stack §shadcn/ui 派生の範囲](../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md)
は「Radix UI を使ってよいのは次のいずれかを伴う部品に限る」として 4 基準を定める。

| 基準 | 該当 | 判定理由 |
| --- | --- | --- |
| 1. フォーカストラップ | **非該当** | 非対話要素（`<span>`）であり、フォーカスを受けない |
| 2. ロービングタブインデックス等の複合キーボード操作 | **非該当** | 同上 |
| 3. ポータル／ポップアップの配置計算 | **非該当** | 通常フローに置く |
| 4. `aria-*` の動的な同期を要する開閉状態 | **非該当** | 状態を持たない |

→ **ネイティブ HTML（`<span>`）＋ `cva` ＋ `cn()`** で実装する（計画の「上記に該当しない部品は
ネイティブ HTML と `cva` ＋ `cn()` で実装する」に従う）。

**既存の `StatusBadge` を流用しない理由**: `StatusBadge` は `tone` ごとに固定アイコン（`Info` / `CircleCheck` /
`AlertTriangle` / `CircleX`）を描く**「状態」の部品**であり、INDEX 決定 21 を型で強制する設計である。
タグ（「組織文書」「経理」「規程」）は状態ではなく**分類の名前**で、`Info` アイコンが付くと意味が変わる。
hi-fi モックも `tag`（分類。全画面で 120 箇所）と状態表示（`ok` / `warn` / `err`）を別の語彙として描き分けている。
なお #496 の選定表は `tag` を `StatusBadge` で足りると見なしていたが、**画面へ適用して初めてこの差が現れた**
（同 PR は「プリミティブの本番の呼び出し元は 0 件」と記録している）。本 issue はその是正である。

`Tag` は `tone`（`accent` / `neutral` / `outline`。モックの 3 種に対応）だけを持ち、
**アイコンも文言も持たない**（[[IADR-0125]] 決定 1 の「プリミティブは文言を持たない」に従う）。

### 4. ナビ項目の i18n（`NavItem.label`）

現在の `FeatureNav.label` は `string` であり、feature が日本語リテラルを直書きしている。
本 issue で `eslint-plugin-lingui` の適用範囲を feature へ広げるため、ここが未国際化リテラルとして残る。

- `label` の型を **`string | MessageDescriptor`** に広げ、`navGroups()` が描画時に `i18n._()` で解決する。
  グループ見出し（`PLAN_NAV_GROUP_MESSAGES`）が既に採っている形と同じである。
- **モジュール初期化時に文字列へ確定させない**——確定させるとロケール切替に追随しない
  （`nav.ts` が既にこの理由でグループ見出しを `MessageDescriptor` で持っている）。
- 既存の feature（SC-04〜11）は `string` のまま動く（union のため破壊的変更にならない）。
  それらの i18n 化は #452 の残り 2 分割が引き受ける。

### 5. テスト用ハーネスの拡張

`foundation/testing/renderUnitRoute` に次を足す。

- **`QueryClientProvider`**（描画のたびに新しい `QueryClient`）。`retry: false` / `staleTime: 0` にして、
  テストが再試行の待ち時間とキャッシュの持ち越しに影響されないようにする。
- **`I18nProvider`**（`<Trans>` を使う画面のため）。#496 §未決事項 4「テスト用の共通 wrapper に
  `I18nProvider` を組み込むのが自然だが、画面の作り方が決まる #452 で判断する」の引き受けである。

ハーネスは `foundation/testing/**` としてカバレッジの母数から除外済みである（#490）。

### 6. 削除する旧実装

```text
src/knowledge/frontend/src/features/sc01-search/{SearchChatPage.tsx,SearchChatPage.test.tsx,index.tsx}
src/knowledge/frontend/src/features/sc02-results/{SearchResultsPage.tsx,SearchResultsPage.test.tsx,index.tsx}
src/knowledge/frontend/src/features/sc03-document/{DocumentDetailPage.tsx,DocumentDetailPage.test.tsx,index.tsx}
```

同名のファイルを新しい内容で置き換えるのではなく、**削除してから作る**（計画の「完全に削除する」）。
差分では上書きに見えるが、旧実装の構造（手書き state・`useEffect` での取得・素の DOM・日本語リテラル）は
1 行も残らない。

## 受け入れ基準

issue #502 §受け入れ基準 を検証可能な形へ展開する。

- [ ] **SC-01〜03 が hi-fi モックアップと計画の画面仕様どおりに実装されている。**
      各画面仕様書の「hi-fi モックアップとの対応」表の全行が **実装した／実装しない（理由つき）**で埋まっている。
- [ ] **旧実装が残っていない。** 旧 3 画面は同じパスに置き直すため、削除は `git log --diff-filter=D` では
      現れない（差分上は置き換えに見える）。したがって**旧実装の構造が 1 行も残らないこと**を、
      次の 4 つの機械検査で確かめる（§検証 に実測を記録する）。
      (1) `useEffect` による取得が無い、(2) 二重発火ガード（`lastSearched`）が無い、
      (3) インラインの `style={{…}}` が無い（旧実装は素の DOM を直接飾っていた）、
      (4) 未国際化リテラルが無い（`eslint-plugin-lingui` が 0 errors）。
- [ ] **検索 → 結果 → 文書詳細の導線が通る。** 3 ルートを 1 本のルータへ載せた導線テストで、
      SC-01 の「キーワード検索のみ →」→ SC-02 の結果クリック → SC-03 の本文表示までを 1 テストで通す。
      E2E は §検証 に実走結果と限界（認証が要る画面は未認証スモークまで）を記録する。
- [ ] **未国際化リテラルが無い。** `eslint-plugin-lingui` の `files` に本 3 feature を追加した状態で
      `pnpm run lint` が **0 errors**。**発火確認**として、未国際化リテラルを混ぜると error になることを実測する。
- [ ] **i18n カタログが最新。** `pnpm run i18n` ＋ `git diff --exit-code` が green。
      `node scripts/check-i18n-catalogs.js` が green（未翻訳 0 件）。
- [ ] **カバレッジ床を割らない**（現行 87 / 81 / 77）。実測値を測定条件つきで記録し、
      引き上げの余地があれば MSP 所有分 −5pt 切り捨ての既存規則で引き上げる。
- [ ] **AI 提案承認欄・SC-18 導線を実装していないことと理由（[[IADR-0119]]）が仕様書に明記されている。**
      → [SC-03 画面仕様書](../screens/SC-03_document-detail.md) §hi-fi モックアップとの対応 #7〜#9 と §実装しない要素の理由。
- [ ] `pnpm run typecheck` / `lint` / `test` / `test:coverage` / `build` が green。
- [ ] `node scripts/check-doc-links.js` / `check-commit-messages.js --base origin/develop` /
      `check-unit-dependencies.js` / `check-test-traceability.js` / `check-i18n-catalogs.js` /
      `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が green。
- [ ] AST（submodule）の typecheck / lint / テストが**無改修で**通る。

## テスト方針

**受け入れ基準 → テストの写像**はテスト仕様書（`docs/tests/SC-0{1,2,3}_*.md`）を正とし、
**UC-01 / UC-02 の基本・代替・例外フローを 1 行ずつテストへ写像する**（`check-test-traceability.js` の対象）。

| 層 | 対象 | 見るもの |
| --- | --- | --- |
| 純関数 | `citations.ts` / `attributes.ts` | 出典の種別判定（`wikiBaseUrl` 一致／不一致／未設定）・属性ラベルの写像（既知／未知） |
| コンポーネント | SC-01 / SC-02 / SC-03 | 表示条件・エラー状態・存在秘匿の中立表示・i18n（ja / en） |
| 導線 | 3 ルートを 1 本のルータへ | SC-01 →（`?q=`）→ SC-02 →（`/docs/$id`）→ SC-03 |
| E2E | Playwright | 未認証で各ルートが `/login` へ誘導されること（認証済み導線の限界は §検証 に記録） |

- **権限別の出し分け**: SC-01〜03 は**ロール限定が無い**画面である（05_screens §共通シェル）。
  したがって「ロールを持たない利用者でも 3 画面へ到達でき、左ナビに項目が出る」ことを固定する。
  権限の効きはサーバ側（ABAC）であり、画面側は**空応答・404 を中立に表示する**ことで担保する。
- **変異試験**: 「壊すと落ちる」ことを実測する。少なくとも
  (1) 存在秘匿の中立文言、(2) 版履歴の `enabled` ガード、(3) 出典の種別判定、
  (4) URL 単一情報源（二重発火の不在）で確認し、結果を §検証 に記録する。

## 検証（実測）

**測定条件**: worktree `feat/SC-01-03-search-flow`（`origin/develop` `83ff0fd` 基点。実測時の HEAD は
実装コミット `3717fc2`）／ Node 22.22.2 ／ pnpm 10.33.0 ／ Vitest 3.2.7（v8 provider）／
TypeScript 5.9.3 ／ Vite 6.4.3 ／ Lingui 6.6.0 ／
**submodule `src/ai-stock-trading`（pin `655e2ed`）と `planning`（pin `d980a01`）は populate 済み**。
スコープは断りがない限り**ワークスペース全体**（`src/` の 4 パッケージ＋ AST）である。

| 検査 | コマンド | 結果 |
| --- | --- | --- |
| 型検査 | `pnpm run typecheck` | green（4 パッケージ。AST は**無改修**） |
| lint | `pnpm run lint` | green（**0 errors / 9 warnings**。warning は全件 `react-refresh/only-export-components`。移行前は 0 errors / 8 warnings で、増えた 1 件は `Tag` が cva のバリアントを併せて export するため） |
| 単体テスト | `pnpm run test` | **48 files / 421 tests** 全 green（本作業前は 44 files / 385 tests） |
| カバレッジ | `pnpm run test:coverage` | 後述（床を 87/81/77 → **88/82/81** へ引き上げ） |
| ビルド | `pnpm run build` | green（`dist/assets/index-*.js` 571.88 kB / gzip 170.19 kB） |
| E2E | `playwright test`（後述の条件） | **8 tests 全 green**（本作業で 2 本追加） |
| i18n 乖離 | `pnpm run i18n` ＋ `git diff --exit-code` | green（差分なし） |
| i18n カタログ | `node scripts/check-i18n-catalogs.js` | green（2 ロケール・未翻訳 0 件。ja / en とも 72 件） |
| ドキュメントリンク | `node scripts/check-doc-links.js` | green（413 件） |
| ユニット依存方向 | `node scripts/check-unit-dependencies.js` | green |
| テスト・トレーサビリティ | `node scripts/check-test-traceability.js` | green（仕様書のある 28 件中 28 件が写像済み。**FR-17 / FR-18 / SC-18 を allowlist へ追加**。後述） |
| コミット件名 | `node scripts/check-commit-messages.js --base origin/develop` | green（3 件） |
| 静的 egress | `node scripts/check-static-egress.js --require src/platform/frontend/dist` | green（4 ファイル・検出 0 件） |
| スクリプト自己試験 | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | green（**244 tests**。本作業で増減なし） |

### 受け入れ基準 1: hi-fi モックアップとの対応

3 つの画面仕様書に、hi-fi モック（planning `d980a01`）の**全要素を行番号つきで写像した表**を置いた。
実装しない要素は 2 種類だけで、いずれも理由を名指しした。

| 画面 | 実装する | 実装しない |
| --- | --- | --- |
| SC-01 | 8 要素 | 6 要素（**FR 保留 2**・**契約の不在 1**・共通シェル 3） |
| SC-02 | 6 要素 | 5 要素（**契約の不在 3**・共通シェル 2） |
| SC-03 | 6 要素 | 6 要素（**FR 保留 3**・共通シェル 2・ナビ項目 1） |

### 受け入れ基準 2: 旧実装が残っていない（実測）

旧 3 画面は同じパスへ置き直したため、削除は `git log --diff-filter=D` には現れない。
そこで §受け入れ基準 の 4 検査を実測した（対象は `features/sc0{1,2,3}-*` の**実装ファイル**）。

| # | 検査 | コマンド | 結果 |
| --- | --- | --- | --- |
| 1 | `useEffect` による取得が無い | `grep -rn "useEffect" …/sc0[123]-*/*.{ts,tsx}`（`*.test.*` を除く） | **0 件** |
| 2 | 二重発火ガードが無い | `grep -rn "lastSearched" knowledge/frontend/src` | **0 件** |
| 3 | インライン `style={{…}}` が無い | `grep -rn "style={{" …/sc0[123]-*` | **0 件** |
| 4 | 未国際化リテラルが無い | `pnpm exec eslint …` | **0 errors** |

> 検査 1 は `*.test.tsx` を含めると 1 件当たる（SC-02 のテストが**旧実装の二重発火**を説明したコメント）。
> 実装ファイルに限れば 0 件である。**この但し書きを省くと「grep が 0 件」という記述が誤りになる。**

### 受け入れ基準 3: 検索 → 結果 → 文書詳細の導線

`knowledge/frontend/src/features/searchFlow.test.tsx`（3 ケース）が、**3 ルートを 1 本のルータへ載せて**
実際に遷移する。

1. SC-01 の「キーワード検索のみ →」→ SC-02（`?q=` が引き継がれ再入力なしで検索が走る）→ 結果クリック → SC-03（本文表示）。
2. SC-01 の**出典**から直接 SC-03（一覧を経由しない経路）。
3. **UC-01 例外フロー**（縮退運転）の導線: AI が落ちたときの通知から SC-02 へ 1 クリック。

**E2E では認証済みの導線を実走できない**（トークンは `InMemoryWebStorage` に保持され外部から注入できない。
`foundation/auth/authConfig.ts`）。E2E は各ルートが**存在し認証ガードが先に効く**ことを見る
（ルート未登録なら `NotFound` が出て `/login` へ行かないため、この 1 本でルートの実在も固定できる）。

**この環境では `playwright install` がブラウザを取得できない**ため、インストール済みの
`/opt/pw-browsers/chromium-1194` を `launchOptions.executablePath` で指すローカル専用 config を
一時的に置いて実走し、**確認後に削除した**（#490 / #496 と同じ作法）。
**リポジトリの `playwright.config.ts` は無改変である。**

### 受け入れ基準 4: カバレッジ床

| 集計 | lines/statements | branches | functions |
| --- | --- | --- | --- |
| 全ユニット横断（本 PR） | **94.53%** | **86.48%** | **87.70%** |
| MSP 所有分（本 PR） | **93.07%** | **86.29%** | **87.69%** |
| （参考）本作業前 `83ff0fd` の MSP 所有分 | 92.04% | 82.93% | 86.08% |
| 床 | 87 → **88** | 77 → **81** | 81 → **82** |

MSP 所有分は `src/coverage/lcov.info` から `ai-stock-trading` のファイルを除いて再集計した値である
（`LF/LH`・`BRF/BRH`・`FNF/FNH` を全ファイルで合算し、行数で加重）。
導出規則は既存どおり**実測から 5pt 下・切り捨て**。

**branches の伸び（82.93 → 86.29）は、テストを足したことだけによるものではない。**
旧 3 画面が持っていた手書きの状態遷移（`useEffect` ＋ 4 つの state ＋ 二重発火ガード）を
TanStack Query と URL 単一情報源へ置き換えた結果、**測るべき分岐そのものが減った**
（[[IADR-0126]] 決定 3）。数字の出所を取り違えないよう明記する。

### 変異試験（「壊すと落ちる」ことの実測）

**9 件すべてで、壊すと落ち、戻すと通ることを確認した。**

| # | 壊した箇所 | 落ちたもの |
| --- | --- | --- |
| M1 | SC-03 の版履歴の `enabled` ガードを外す | `never requests the version history when the document is hidden`（1 件） |
| M2 | SC-03 の 404 表示を「権限がありません」へ変える（**存在秘匿を壊す**） | `shows a neutral not-found message on 404` ほか **2 件** |
| M3 | SC-03 へ **SC-18 導線（保留対象）を足す** | `does not render the AI suggestion panel or the knowledge-graph link`（1 件） |
| M4 | SC-02 で入力欄からも取得を発火させる（**二重発火を再現**） | `fires exactly one request per submission`（1 件） |
| M5 | 出典の種別判定で Wiki を推測させる | `never infers a wiki citation…` ほか **2 件**（純関数と画面の両方） |
| M6 | SSE の中断（`AbortError`）を失敗として扱う | `does not show an error when the stream is aborted`（1 件） |
| M7 | SC-01 へ**日本語の**未国際化リテラルを混ぜる | `eslint`: `lingui/no-unlocalized-strings` **1 error** |
| M8 | SC-02 へ**英語の**未国際化リテラルを混ぜる | 同上 **1 error**（#496 が塞いだ穴が本 feature でも効いている） |
| M9 | `en` カタログの `msgstr` を 1 件空にする | `check-i18n-catalogs.js` が **exit 1** |

**M3 は最初の試行で素通りした。** 原因は、当該テストが `wikiBaseUrl` 未設定で描画しており、
導線の行（`{wikiBaseUrl && …}`）ごと描かれていなかったことである。
**テストを是正して（`wikiBaseUrl` を設定して描かせる）から再実行し、落ちることを確認した。**
変異試験をやらなければ、「保留対象が無いことを固定している」というテストが
**実は何も守っていない**まま残っていた。

## 計画書との差異

| 事項 | 計画の記載 | 実装 | 根拠 |
| --- | --- | --- | --- |
| **SC-03 の AI 提案承認欄・SC-18 導線** | 05_screens §SC-03「SC-03 に置くのは次の 2 つのみである: ①SC-18 への導線、②AI 提案の承認欄」 | **実装しない** | **FR-17 / FR-18** に属し、[[IADR-0119]] 決定 1 が「その受け入れを担う画面」まで保留対象としている。決定 2 の着手条件は前提 ADR の `Accepted` 化であり、`ADR-0033` / `0034` / `0035` は planning `d980a01` 時点で全件 `Proposed`。**繰り延べであって放棄ではない**（保留解除後の後続 issue） |
| **SC-01 の個人資料まわり** | 05_screens §SC-01「区別の表示方法」の `👤`／「個人資料（自分のみ）」・「個人資料を含める」トグル | **実装しない**（`📄` / `📖` ＋「組織文書」は実装する） | **FR-19 / FR-21**。同上。組織文書側を先に実装しても表記は変わらない（保留が解けたら `👤` の行が増えるだけ） |
| **SC-01 の対象範囲フィルタ** | 05_screens §SC-01 入力表「対象範囲フィルタ｜任意｜選択｜**権限内のタグ／フォルダのみ選択可**」 | **実装しない** | AI 回答要求（`AnalysisRequest`）が属性フィルタを取らず、**権限内候補**を返す BFF が無い（10 グループを実測）。候補を出せないまま欄だけ置くと、計画の保証（権限内のみ提示）を利用者が受けられない。**環流済み** |
| **SC-02 の検索モード・並び順・更新日時列** | 05_screens §SC-02 主要素 | **実装しない** | `SearchRequest` に指定軸が無く（常にハイブリッド・関連度降順）、`SearchResultDto` に日時が無い。**環流済み** |
| **SC-03 の機密区分の表示名** | hi-fi が `internal`＝「社内限」、SC-05 / SC-09 が `confidential`＝「秘」と描く | **キーだけ写像し値は生値** | 値集合は 4 値だが、モックに現れる表示名は **2 値だけ**。残る 2 値を実装が決めると事実上の用語定義になる。**環流済み** |
| **SC-03 を左ナビに出さない** | 05_screens §共通シェル の「利用者」グループに SC-03 が含まれる | **出さない**（旧実装からの継続） | ルートが文書 ID を必須とするため、ID を持たないナビ項目からは到達できない（`/docs/` は 404）。グループ分けは画面の所属を示すもので、各画面が単独のナビ入口を持つことまでは要求していない |
| **本文の Markdown レンダリング** | 05_screens §SC-03「Markdown プレビュー」 | 原文を等幅・改行保持で表示 | 本文は外部データソース由来であり HTML 化はサニタイズ方針の決定を伴う（誤ると保存型 XSS）。ADR-0031 の採用技術一覧に Markdown レンダラが無い。**旧実装からの継続で、本 issue で変えていない** |
| **ナビ表示名** | hi-fi 左レール「検索・質問」「結果一覧」 | 同左（旧実装は「検索 / AI質問」「検索結果一覧」だった） | モックを正として是正した |

## 親への申し送り

### この PR で消化したもの

- SPA 移行の完了条件のうち **SC-01〜03 の 3 画面**（#452 の分割 1 本目）。
- #496 §親への申し送りの 2 項目: **プリミティブの画面への適用**（`Tag` の新設を含む）と
  **`eslint-plugin-lingui` の適用範囲の拡大**（本 3 feature へ）。
- #496 §未決事項 4（**テストハーネスへの `I18nProvider` 組み込み**）。

### 残るもの（引き受け先を明記する）

| 項目 | 引き受け先 |
| --- | --- |
| SC-04〜SC-12 の再実装 | #449（SC-04）／ #452 の残り 2 分割 |
| SC-03 の AI 提案承認欄・SC-18 導線、SC-01 の個人資料まわり | [[IADR-0119]] の**保留解除後**の issue（解除は決定 6 の手順で行う） |
| 契約の不在 5 件 | 計画の裁定待ち（`feedback/20260804_sc01-03-bff-contract-gaps.md`）。裁定後にバックエンド側の issue が要る |
| パンくず・権限バッジ | 共通シェルの作業（#452 系） |
| 右レール AI チャットパネル | 移行**第 4 段**（[[IADR-0121]] 決定 1・5） |
| SC-04〜11 の文言の i18n 化と `eslint-plugin-lingui` の files 追加 | 各画面を再実装する issue（**画面を作り直すたびに `files` を伸ばす**） |
| `Dialog` の移植 | FR-19 / FR-20 の保留解除後（#496 の申し送りのまま） |
| バンドルサイズ（571 kB / gzip 170 kB） | 全画面の再実装が終わってからのコード分割（#490 / #496 の未決事項を引き継ぐ） |

### 注意（レビュー時に見てほしい点）

1. **`scripts/test-traceability-allowlist.json` の `specMissing` へ FR-17 / FR-18 / SC-18 を追加した。**
   これは「実装先行」ではなく**逆向きの事例**である——保留対象が**描かれないこと**を SC-03 の
   単体テストが固定しているため、テストが当該 ID を参照する。理由は同ファイルの `$comment` に書いた。
   **保留が解けて当該機能に着手する issue が、テスト仕様書を作ってこの 3 件を削除する**
   （残したままだと `check-test-traceability.js` が「allowlist の減らし忘れ」として落とす）。
2. **カバレッジ床の branches を 77 → 81 へ上げた。** 伸びの一部は「分岐そのものが減った」ことに由来する
   （§検証）。今後の画面再実装でも同じ向きの効果が出るはずである。

## 未決事項

1. **契約の不在 5 件**（§2 の B）。環流済み。計画の裁定待ち。
2. **SSE 中断時の再試行方針**（[[IADR-0126]] §フォローアップ 2）。
3. **ページング**（SC-02）。計画が送り方を定めていない。
