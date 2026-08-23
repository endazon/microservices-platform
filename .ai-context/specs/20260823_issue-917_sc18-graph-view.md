---
title: 作業仕様書 — SC-18 ナレッジグラフビュー（#917）
type: spec
status: done
related_ids:
  - FR-17
  - FR-05
  - UC-10
  - SC-18
  - SC-03
  - SC-09
  - SC-20
  - ADR-0033
  - ADR-0034
  - ADR-0039
  - ADR-0049
  - ADR-0054
  - IADR-0036
  - IADR-0119
  - IADR-0121
  - IADR-0122
  - IADR-0124
  - IADR-0125
  - IADR-0134
  - IADR-0242
  - IADR-0274
author: claude
created: 2026-08-23
updated: 2026-08-23
plan_refs:
  - "05_screens/01_screens.md §SC-18（主要素 8 件・辺の型の描き分け表・表示上限と間引き・入力/バリデーション・アクセス制御・ヘルプ文言）"
  - "07_adr/ADR-0039（描画ライブラリ導入の可否と 4 条件。決定 4: ライブラリ選定は実装 IADR で行う）"
  - "07_adr/ADR-0034（ホップごと ABAC・表示上限・存在秘匿）"
  - "07_adr/ADR-0049（総数の算出に限り表示上限を超えて探索してよい。二段上限・間引き基準 3 択）"
  - "07_adr/ADR-0054（doc_scope 属性。決定 5: 取り込み経路の既定は organization）"
  - "06_technical/14_knowledge-graph-graphrag.md §7（グラフ可視化ライブラリの評価表 — 選定の入力）"
  - "05_screens/mockups/hi-fi/sc-18.html / wireframe/sc-18.html（2026-08-02 受領）"
issue: "#917"
---

# 作業仕様書: SC-18 ナレッジグラフビュー（#917）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-17（文書間リンクとグラフ探索）/ FR-05（ABAC アクセス制御）
- ユースケース（UC）: UC-10（関連をたどる）
- 画面（SC）: SC-18（ナレッジグラフビュー・読み取り専用）
- 関連 ADR: ADR-0039（描画ライブラリの可否と 4 条件）/ ADR-0034（ホップごと ABAC・上限）/
  ADR-0049（二段上限・総数・間引き基準）/ ADR-0033 決定 7（承認済み提案だけが辺）/ ADR-0054（doc_scope）
- 関連 IADR: 本作業の [[IADR-0274]]（描画ライブラリ選定・4 条件の実測・doc_scope 無値の扱い）/
  [[IADR-0242]]（ホップごと ABAC の型ゲート）/ [[IADR-0121]]・[[IADR-0124]]・[[IADR-0125]]・[[IADR-0134]]（SPA の骨格）
- 計画書の参照手段: 隣接クローン `../project-planning`（`git fetch origin` 済み。
  `origin/main` = `b6c3cc0`・2026-08-22。読み取り専用）

## 目的・背景

SC-18（ナレッジグラフビュー）を実装する。読み取り専用で、起点ありの近傍探索を主用途とし、
辺の型の描き分け・ホップ深度コントロール・辺の型フィルタ・ノード選択サイドパネル・
表示上限の打ち切り表示・グラフ内検索・空状態の描き分けを備える。

ADR-0039 は「導入してよい」という射程の合意までを決めており、**ライブラリの選定と 4 条件の
確認は実装側の宿題**である（決定 4）。選定は「測って選ぶ」——候補筆頭（既存依存 ECharts）を
先に実測し、満たせない場合にのみ別候補を測る。結果は [[IADR-0274]] に残す。

## 着手前の実測（前提が本当に載っているかの確認・2026-08-23）

| 確かめたこと | 実測（作業ツリー `claude/implementation-repo-all-issues-hilvbs`） |
| --- | --- |
| #980（ADR-0049 の実装）が載っているか | **載っている。** `GraphTraversal.cs` に `CountingMaxNodes=2_000` / `CountingMaxEdges=5_000`、`ExploreAsync(origin, scope, hops, thinning)` の二段（探索→選抜）、`GraphThinning`（distance/updated/degree・未知は distance へ縮退）。応答は `TotalNodes` / `TotalEdges` / `TotalIsLowerBound` を運ぶ（`GraphViewResponse` / `GraphViewDto`）。`TwoTierTraversalTests` が固定している |
| #962（辺の型辞書の BFF 公開）が載っているか | **載っている。** `GraphBffEndpoints.cs` の `GET /bff/graph/edge-types` → 後段 `/graph/edge-types/catalog`（認証のみ・使用件数なし）。openapi にも `EdgeTypeCatalogItem`（id/name/layer/isSymmetric）があり、orval 生成フック `useBffGraphEdgeTypes` も生成済み |
| ADR-0039 / ADR-0049 / ADR-0054 の `status`（原文） | いずれも **`Accepted`**（planning `origin/main` = `b6c3cc0` の frontmatter を直接読んだ。要約・issue 本文を根拠にしない）。ADR-0049 は「決定 3 の上限値だけは実測で覆り得る」と自ら明記（構造は覆らない） |
| 画面仕様と既存実装の食い違い 1: **辺の種別フィルタのサーバ側適用** | 🔴 **未実装。** 画面仕様（05_screens §SC-18「辺の種別フィルタはサーバ側で適用する。実装側で neighbors にフィルタ引数を足す」・環流 planning#446 の帰結）に対し、`/graph/{id}/neighbors` の引数は `hops` と `by` だけ（GraphService・BFF・openapi の 3 面とも）。**本作業で足す** |
| 画面仕様と既存実装の食い違い 2: **ノードの doc_scope** | 🔴 **未実装。** 描き分け（円＋📄 / 角丸四角＋👤）には各ノードの個人資料フラグが要るが、`GraphNodeItemDto` は `DocumentId` / `Title` のみ。**`IsPrivateNote` を足す**（漏洩検討は [[IADR-0274]]） |
| サイドパネルの表示項目（タイトル/種別/更新日/タグ） | グラフ応答は運ばないが、**`GET /bff/documents/{id}`（`DocumentDto`: tags / attributes / updatedAt）が既存**であり、選択時に 1 件引けば足りる。グラフ応答へタグ等を足さない（200 ノード分の複製を運ぶ理由が無い） |
| 個人資料がグラフへ流れる経路 | **現状は存在しない。** GraphService の属性複製の取り込み（DocumentUpdated 購読・#911/#912）は未配線で、`doc_scope` の実データも 0 件（ADR-0054 §結果）。したがって `IsPrivateNote` は当面 false のみが流れる。**「ナレッジグラフに表示する」（SC-20・既定 OFF）のサーバ側強制は本作業の射程外**（§除外） |
| ECharts の現況 | `echarts@6.1.0` 導入済み（SC-08/SC-10 で使用）。`echartsBundle.ts` が `echarts/core` ＋ Line/Bar/Grid/Tooltip/Legend/SVGRenderer だけを `use()` する形が確立。`GraphChart` は未登録。vendor-echarts チャンクは **557,207 bytes**（導入前の実測・`dist/assets/vendor-echarts-B4l2fgxm.js`）。チャンク上限は 600,000 bytes（`chunk-budget-baseline.json`） |
| SC-03 → SC-18 の導線 | `DocumentDetailPage.tsx` のコメントに「引き受けるのは #452」と明記があり、**本作業の射程外**（§除外） |

## ライブラリ選定の進め方（測って選ぶ）

1. 候補筆頭 **ECharts（`GraphChart`・既存依存）** で 4 条件を先に実測する（`perf/graph-render/measure.mjs`。
   ノード 200 / 辺 500・力学レイアウト・ズーム/パン/ノード選択/フィルタ切替を headless Chromium で駆動）。
2. 条件を満たせないと実測で判明した場合に限り、planning `14_knowledge-graph-graphrag.md` §7 の
   比較表の次候補（Cytoscape.js / sigma.js）を測る。
3. 実測値と判断は [[IADR-0274]] へ記録する（条件 1 の操作別実測値・条件 3 のバンドル差分を数値で）。

## 設計（要点。詳細は IADR-0274）

### バックエンド（GraphService / Knowledge.Contracts / BFF / openapi）

1. **辺の型フィルタのサーバ側適用**: `GET /graph/{id}/neighbors?types=<uuid,uuid,...>` を足す。
   - 形式不正（GUID として読めない要素）は **400 `edge_type_filter_invalid`**（hops と同じく、
     文書に依存しない入力検証なので**認可より前**に置く——後に置くと 400/404 の打ち分けから存在が漏れる）。
   - 未指定・空は「絞らない」。実在しない GUID は単に 1 本も一致しない（辺の型辞書は認証のみで
     全利用者へ公開済みの語彙であり、実在の有無は秘匿対象ではない）。
   - 絞りは `GraphTraversal.ExploreAsync` の**探索の入口**（辺を候補へ入れる前）で適用する。
     打ち切り後のクライアント絞りだと「上位 200 件のうち一致したもの」になり範囲が狭まる
     （planning#446 の指摘そのもの）。計数（総数）もフィルタ後の母集合で数える。
2. **ノードの個人資料フラグ**: `GraphNodeDto` / `GraphNodeItemDto` へ `IsPrivateNote`（bool・既定 false）を足す。
   - 導出は Seal 時に `Attributes["doc_scope"] == "private-note"`（大文字小文字不問・集合帰属で判定）。
   - **値が無い ⇒ 組織文書（false）**。暫定ではなく決定として [[IADR-0274]] に残す（ADR-0054 決定 5 が根拠）。
   - 既定値つきで足す（既定値の無いメンバー追加は契約上の破壊的変更。[[IADR-0122]] 決定 2）。
3. **BFF**: `types` を不透明な文字列としてそのまま後段へ渡す（hops / by と同じ作法。検証は GraphService の一箇所）。
4. **openapi**: neighbors の `types` パラメータと `GraphNodeItem.isPrivateNote` を追記 → `pnpm run codegen` で
   orval 生成物を再生成（生成物はコミット）。

### フロントエンド（`src/knowledge/frontend/src/features/sc18-graph/`）

- ルート `/graph`（計画の確定ルート）。検索パラメータ: `root`（uuid・任意）/ `hops`（1|2|3・既定 2）/
  `by`（distance|updated|degree・既定 distance）/ `types`（辺型 id の配列・省略＝全型）。
  外部由来の値は validateSearch で正規化する（不正値は既定へ）。
- 遅延チャンク（`lazyRouteComponent`）。ECharts の graph 面は `echartsGraphBundle.ts`（`GraphChart` だけを
  静的 import して `use()`）＋ `echartsGraphLoader.ts`（動的 import・1 回だけ解決）で、既存の
  `echartsBundle.ts` と同じ形に閉じる（バレルの動的 import は tree-shaking が効かない——既存実測）。
- 描き分け（すべて色以外の手掛かりを併用）:
  - ノード: 組織文書＝円＋📄 / 個人資料＝角丸四角＋👤・破線輪郭。起点は太枠＋サイズで強調。
    孤立文書（表示中の辺が 0 本のノード）は点線輪郭＋凡例で示す。
  - 辺: `related` 実線・細（無向）/ `cites` 実線・矢印 / `supersedes` 太実線・矢印 /
    `derived-from` 破線・矢印 / `embeds` 点線・矢印。型名→線種の対応は表示名で引き、
    未知の型は `related` 相当の既定線へ縮退。**AI 提案由来（provenance=ai-approved）は破線**。
    向きは辞書の `isSymmetric` で決める（無向は矢印なし）。
  - 凡例は HTML で常時表示（ノード 2 種＋孤立＋中核 5 種＋推奨 4 種は凡例のみ・AI 提案由来）。
- 打ち切り帯: 「上位 200 件を表示（全 N 件）」。`totalIsLowerBound` 時は「全 2,000 件以上」の形＋
  「更新日順・次数順は厳密な上位 200 件ではない」の注記。文言はフィルタを絞ることを促す
  （「もっと読み込む」を置かない）。間引き基準の 3 択セレクタを併置。
- サイドパネル: ノード選択で表示。タイトル / 種別（組織文書・個人資料）/ 更新日 / タグ
  （`useBffDocumentDetail` で 1 件取得。404 は「表示できません」に縮退）/ 接続辺の一覧（型名で集計）/
  「文書を開く」→ `/docs/$id`（SC-03）。
- グラフ内検索: 表示中ノードのタイトル部分一致。該当ノードを強調（フォーカス）。
- 空状態の描き分け: ① root 未指定 → 起点の指定を促す案内 ② 200 応答で辺 0 本 →
  「関係する文書がありません」 ③ 404 → 「権限のある文書がありません」（存在秘匿のため
  不存在と区別しない）。ヘルプ固定文言（「関係が存在しないのか、閲覧権限がないのかは
  区別できません…」）は**結果が 0 件でないときにも常に**画面内に出す。
- クラスタ表示: 力学レイアウトの空間的な凝集で示す（クラスタ要約の表示形式は計画側で未確定
  ——05_screens §SC-18 §未確定——のため、要約・色分け等は作らない）。
- hops=4 等を URL に書かれた場合: クライアントは 1/2/3 へ正規化するため送信されない。
  サーバの 400 は防御として残る（丸めずエラーは GraphService が一箇所で実装済み）。
- 文言は Lingui カタログ（ja/en）へ。`@platform/ui` へは何も足さない（ドメイン・文言を入れない）。

## 母集合（規則 1〜10 に従って自分で引いた）

本作業は「新規画面の追加」だが、既存の記述が**本作業の後に誤りになる**箇所と、
**同じ事実の複製**を先に引いた。誤りの側の文字列（「未実装」「未着手」「〜だけ」等）と
契約の型名の両方から、拡張子で絞らず（`--include` なし）、パス除外（`node_modules` / `obj` / `bin` /
`dist` / `coverage` / `.git` / `src/ai-stock-trading`）だけで走査した。

### 軸 1: `SC-18`（リポジトリ全文・37 ファイル）

| 該当 | 扱い |
| --- | --- |
| `GraphTraversal.cs` / `GraphEndpoints.cs` / `GraphBffEndpoints.cs` / `GraphViewDto.cs` / `EdgeTypeDictionaryDto.cs` / openapi | 変更対象（本作業で編集） |
| `DocumentDetailPage.tsx`（「SC-18 導線は実装しない…引き受けるのは #452」） | **除外**。導線は #452 の射程と当該コメントが明記しており、本作業で足すと 1 issue = 1 PR を破る |
| `.ai-context/specs/` 15 件・`IADR-0119`・`IADR-0124` | **除外**（凍結記録。書き換えない） |
| `scripts/check-test-traceability.js` / `test-traceability-allowlist.json` | 検査器の自己試験の例示データ・過去の経緯コメント。**除外**（SC-18 を語as例に使っているだけで、実装状態の主張ではない） |
| `docs/screens/SC-01` / `SC-03` / `docs/tests/SC-03` / `docs/data/knowledge-graph.md` | trace ブロックの ids 列挙のみ。**除外**（実装状態の主張ではない） |
| `docs/how-to/*-annex.md` | ID レンジの経緯記録。**除外** |
| `src/coverage/**`（2 件） | 生成物。**除外** |

### 軸 2: 「SC-18 は未実装／未着手」型の主張（`未実装|未着手|着手保留` × グラフ関連語で交差）

- `TwoTierTraversalTests.cs` 冒頭「**これらを消費する画面は未実装でテスト仕様書も無いため、
  画面の ID をここから参照しない**」→ 🔴 **本作業で誤りになる**。画面とテスト仕様書 (docs/tests/SC-18)
  が揃うため、注記を現況へ追随させる（テストの参照 ID 自体は変えない——このテストが検証している
  のは FR-17 の探索であり、画面の受け入れ基準ではない）。
- `docs/screens/SC-10_operations-dashboard.md`・`OperationsDashboardPage.tsx`（ナレッジ健全性節の保留）
  → **除外**。SC-10 の話であり SC-18 の実装で誤りにならない。

### 軸 3: ECharts の登録面の複製（`折れ線・棒|LineChart, BarChart|BarChart, LineChart`）

| 該当 | 扱い |
| --- | --- |
| `echartsLoader.ts`（「使う 2 種（折れ線・棒）…だけを登録する」） | 🔴 graph 面を**別バンドル**に閉じるため本文は正のまま。ただし「登録はこのモジュールに閉じる」という単数の主張になっている箇所へ graph バンドルの存在を追記する |
| `echartsBundle.ts` | 同上（コメント追記のみ） |
| `scripts/chunk-budget-baseline.json` の `$comment_maxChunkBytes`（「これ以上落とせない」） | vendor-echarts の実測が変わる場合はコメントと実測値を追随（`--update` は初期ロード床の話であり、遅延チャンクの増は maxChunkBytes 600,000 以内なら床更新不要） |

### 軸 4: グラフ契約の複製（`GraphNodeItem|GraphNodeDto|GraphViewDto|GraphView\b`）

GraphService（`AuthorizedGraphView.cs`）/ Knowledge.Contracts（`GraphViewDto.cs`）/ openapi
（`GraphNodeItem`）/ orval 生成物（`bff.schemas.ts` / `graph/graph.ts` / `graph/graph.msw.ts`）/
バックエンドテストの受け皿 record（`TwoTierTraversalTests` ほか）。生成物は `pnpm run codegen` で
再生成し、手では触らない。テストの受け皿は必要な面だけ持つ設計（余計な欄は無視される）のため、
`IsPrivateNote` を検証するテストにだけ足す。

### 軸 5: neighbors の引数の複製（`by=|BuildQuery|hops=`）

`GraphEndpoints.cs` / `GraphBffEndpoints.cs`（`BuildQuery`）/ openapi / `BffGraphEndpointTests.cs` /
`TwoTierTraversalTests.cs`（URL 組み立て）。`types` を足す面はこの 5 箇所で全数である。

### 軸 6: `doc_scope|private-note|PrivateNote`（判定の複製を作らないか）

既存の判定は `McpServer.Api/Foundation/Services/DocumentScope.cs`（platform ユニット）と
DocumentService / WikiService 側にある。**knowledge ユニットの GraphService からは参照できない**
（ユニット外参照は `platform/backend/Shared` の 3 プロジェクトのみ。`Platform.Shared.Kernel` は
Result/Error の公開であり属性語彙の置き場ではない）。GraphService 内に同じ集合帰属の判定を
最小で持ち、出典コメントで `DocumentScope.cs` と ADR-0054 を指す。**共有化（Shared への昇格）は
しない**——ユニット外参照の追加は IADR-0117 の改定事項であり、bool 1 個の導出に見合わない。

### 除外の総括

- `src/ai-stock-trading`（submodule・別プロジェクト）/ 生成物（`dist` / `coverage` / orval 出力は再生成）/
  凍結記録（`.ai-context/specs` 既存分・superpowers）。
- 走査は変更前の作業ツリーに対して行った（本仕様書自身が `SC-18` を含むため、書いた後の再走査では
  件数が +1 以上になる。規則 8）。

## 変更対象ファイル（宣言）

- `src/knowledge/backend/Services/GraphService/src/GraphService.Api/Foundation/Services/GraphTraversal.cs`（types 絞り）
- `src/knowledge/backend/Services/GraphService/src/GraphService.Api/Foundation/Services/AuthorizedGraphView.cs`（IsPrivateNote）
- `src/knowledge/backend/Services/GraphService/src/GraphService.Api/Foundation/Services/GraphDocumentScope.cs`（新規・判定）
- `src/knowledge/backend/Services/GraphService/src/GraphService.Api/Foundation/Endpoints/GraphEndpoints.cs`（types 受け口）
- `src/knowledge/backend/Services/GraphService/tests/GraphService.Api.Tests/`（追加テスト）
- `src/knowledge/backend/Shared/Knowledge.Contracts/Dtos/GraphViewDto.cs`
- `src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/GraphBffEndpoints.cs`・`src/platform/backend/Bff/Platform.Bff.Tests/BffGraphEndpointTests.cs`
- `docs/api/openapi.yaml`・orval 生成物（`src/platform/frontend/src/foundation/api/generated/**`）
- `src/knowledge/frontend/src/features/sc18-graph/**`（新規）・`src/knowledge/frontend/src/features/index.ts`・
  `src/knowledge/frontend/src/components/echartsGraphBundle.ts` / `echartsGraphLoader.ts`（新規）・
  `echartsBundle.ts` / `echartsLoader.ts`（コメント追記）
- `src/platform/frontend/src/foundation/i18n/locales/{ja,en}/messages.{po,ts}`（`pnpm run i18n`）
- `src/platform/frontend/e2e/sc18-graph.smoke.spec.ts`（新規・未認証リダイレクト）
- `src/platform/frontend/vite.config.ts` / `scripts/chunk-budget-baseline.json`（必要になった場合のみ——
  vendor-echarts が 600,000 bytes を超えた場合の graph 面の分割）
- `perf/graph-render/measure.mjs`（新規・条件 1 の実測ハーネス）
- `.ai-context/adr/IADR-0274_*.md`（新規）・`.ai-context/adr/README.md`（索引）
- `docs/screens/SC-18_knowledge-graph.md`・`docs/tests/SC-18_knowledge-graph.md`（新規・必須仕様書）
- 本仕様書

#493 と交差するのは `src/pnpm-lock.yaml` のみ（統括の宣言どおり）。**依存は足さない**
（ECharts は導入済み）ため、ロックファイルには触らない見込みである。

［2026-08-23 追記 / #917］実装の結果、宣言に対して次が確定した。
- `src/pnpm-lock.yaml` には**触れなかった**（依存 0 追加）。
- `vite.config.ts` / `chunk-budget-baseline.json` の「必要になった場合」は**発生した** ——
  GraphChart を vendor-echarts へ同居させると 603,300 bytes で 1 チャンク上限（600,000）を
  超えたため、`vendor-echarts-graph` チャンクを新設し、初期ロード床を 575,856 → 582,842 へ
  実測で更新した（内訳は IADR-0274 §条件 3）。
- 宣言に無かった追随 1 件: `scripts/contract-schema-baseline.json`（契約スキーマ検査の baseline。
  `GraphNodeItemDto.IsPrivateNote` の **additive** なメンバー追加を `--update` で記録した。
  検査器の設計どおり「差分そのものをレビュー対象として同じ PR に載せる」）。
- `.ai-context/adr/README.md` は #493（IADR-0275 の行）とも交差した（行レベルの追記どうしで、
  統合は FIFO マージが解決する）。

## 受け入れ基準（issue #917 スコープ節 → 検証の写像）

| # | 基準（05_screens §SC-18） | 検証 |
| --- | --- | --- |
| 1 | グラフ描画領域（7 割以上）・起点ノードの明示 | Vitest（option 純関数: 起点の強調・ノード/辺の写像）＋レイアウト目視相当は E2E スモークの範囲外と明記 |
| 2 | 探索深さ 1/2/3・既定 2・上限超過は丸めずエラー | Vitest（URL→クエリ写像・不正値の正規化）＋既存の GraphService 400 テスト |
| 3 | 辺の種別フィルタ（サーバ側適用） | xUnit（GraphService: 絞り＋総数への効き＋400・陽性対照）/ BFF passthrough / Vitest（UI→types パラメータ） |
| 4 | ノード選択サイドパネル（タイトル/種別/更新日/タグ/開く/接続辺） | Vitest（選択→パネル・detail 404 縮退・辺の集計） |
| 5 | 表示上限と間引きの表示（総数・以上・3 択・近似の注記） | Vitest（帯の文言 4 態: 非打ち切り/打ち切り/以上/基準切替） |
| 6 | グラフ内検索 | Vitest（部分一致・該当なし） |
| 7 | 空状態 2 種＋root 未指定の案内・ヘルプ固定文言の常時表示 | Vitest（404→権限なし文言 / 辺 0→関係なし文言 / 非 0 件でもヘルプが出る） |
| 8 | 描き分け（形＋アイコン・色だけにしない）・凡例常時・AI 提案由来は破線・approved のみ | Vitest（option 純関数: symbol/線種の写像）＋凡例の DOM。approved のみは探索側（ADR-0033 決定 7）で担保済みの前提を明記 |
| 9 | ADR-0039 の 4 条件の実測記録 | perf ハーネスの実測値・バンドル差分を IADR-0274 へ記録 |

## テスト方針（否定形は陽性対照と対）

- GraphService: 「`types` で絞ると当該型の辺（とそこからしか到達できないノード）が消える」（否定形）と
  「絞らなければ現れる」（陽性対照）を同一データで対にする。総数（TotalNodes/TotalEdges）が
  **フィルタ後の母集合**で数え直されることも対で固定する。
- 変異試験: 実装完了後、(a) `types` 絞りを外す（全通し）、(b) `IsPrivateNote` 導出を定数 false にする、
  (c) 打ち切り帯の「以上」分岐を外す、の 3 変異で落ちるテスト数を実測し、戻して残渣 0 を grep で確認する。

## 未決事項・残件（着手時点）

- 個人資料の「ナレッジグラフに表示する」（SC-20・既定 OFF）のサーバ側強制は、属性複製の取り込み
  （#911/#912）と SC-20 側の実装に属する。本画面は `IsPrivateNote` の描き分けまでを持つ。
- クラスタ要約の表示形式・全体俯瞰の間引きアルゴリズム詳細は計画側で未確定（05_screens §SC-18 §未確定）。
- SC-03 からの導線は #452。
