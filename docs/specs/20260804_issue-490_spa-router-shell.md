---
title: SPA 移行 第 2 段 — TanStack Router へのルータ差し替え・共通シェル・旧画面のルート載せ替え
type: spec
status: done
related_ids: [NFR, ADR-0031, ADR-0032, SC-01, SC-02, SC-03, SC-04, SC-05, SC-06, SC-07, SC-08, SC-09, SC-10, SC-11, SC-16, IADR-0056, IADR-0116, IADR-0118, IADR-0120, IADR-0121, IADR-0124]
author: Claude
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md"
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/INDEX.md"
related_specs:
  - ./20260804_issue-446_spa-foundation-stack-migration.md
  - "../../feedback/20260804_frontend-migration-staging-interpretation.md"
  - ./20260802_issue-454_reimplementation-kickoff.md
  - ../adr/IADR-0124_tanstack-router-unit-composition.md
  - ../adr/IADR-0121_spa-stack-migration-staging.md
  - ../adr/IADR-0056_repo-unit-structure-platform-knowledge.md
  - ../adr/IADR-0120_excluded-units-from-gitmodules.md
---

# 仕様書: SPA 移行 第 2 段（TanStack Router・共通シェル・旧画面のルート載せ替え）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR: 保守性・アクセシビリティ。全画面の土台）
- 画面（SC）: SC-01〜SC-11（既存実装のルート載せ替え）／SC-16（共通シェルのユーザーアイコンからの遷移先）
- 関連 ADR（計画）:
  [ADR-0031](../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md)（Accepted。
  **ルーティング = TanStack Router を確定**。§理由 が「ルート・検索パラメータ（`/search?q=` 等）まで
  型安全にできる」を採用根拠に挙げる）／
  [ADR-0032](../../planning/projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md)（認証は第 3 段）
- 関連する技術検討・画面設計（計画）:
  [13_frontend-stack](../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md)（fixed。採用技術一覧が正）／
  [01_screens](../../planning/projects/microservices-platform/05_screens/01_screens.md)（**§共通シェル の
  ルートパス・左ナビ 4 グループ・ユーザーアイコン → SC-16 が本作業の受け入れ根拠**）／
  [INDEX](../../planning/projects/microservices-platform/INDEX.md) 決定 21（色だけで意味を持たせない）
- 関連 IADR:
  [IADR-0124](../adr/IADR-0124_tanstack-router-unit-composition.md)（**本作業の内部設計判断。本書と対で読む**）／
  [IADR-0121](../adr/IADR-0121_spa-stack-migration-staging.md)（5 段分割。決定 1 の第 2 段が本作業）／
  [IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md)（ユニット第一構成・依存規則・合成点）／
  [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md)（規約 4: 1 PR の大きさ）／
  [IADR-0118](../adr/IADR-0118_backend-coverage-floor.md)（床の ratchet 原則）／
  [IADR-0120](../adr/IADR-0120_excluded-units-from-gitmodules.md)（AST は別プロジェクト＝本リポから変更できない）
- 本リポジトリの起点: #490（親 #454 / 第 1 段 #446 = PR #489 / 協調 #452）

## 目的・背景

[IADR-0121](../adr/IADR-0121_spa-stack-migration-staging.md) 決定 1 が SPA 移行を 5 段へ分割し、
第 1 段（pnpm / React 19 / TanStack Query / Tailwind v4 ＋ `@platform/ui` / orval / 機械強制 lint）は
PR #489 でマージ済みである。第 1 段は**ルーティングと画面に触れていない**ため、現時点の実装は
「サーバー状態は新（TanStack Query）・ルータは旧（react-router-dom 6）」という**系統ごとの切替時期のずれ**を
抱えている。本作業（第 2 段）がこのずれを解消する。

第 2 段が独立 issue（#490）になっている理由は、旧画面の削除・再実装を伴い単体でも大きく、かつ
ルータ差し替えと画面の書き換えを**同じ段に置かないと同じ画面を 2 回書く**（IADR-0121 決定 1）ためである。

## 対象範囲

### 対象

1. **ルータの差し替え**: `react-router-dom` 6 → **TanStack Router**。`platform/frontend` と
   `knowledge/frontend` の双方から `react-router-dom` を依存ごと撤去する。
2. **ユニット合成点の契約変更**: **本リポジトリが所有するユニット（`@knowledge`）については**
   実行時 `RouteObject[]` 連結をやめ、**型付きルート木（タプル）**へ移す
   （設計は [IADR-0124](../adr/IADR-0124_tanstack-router-unit-composition.md)）。
   **ただし実行時連結が完全に無くなるわけではない**——本リポジトリから変更できない
   `src/ai-stock-trading`（[IADR-0120](../adr/IADR-0120_excluded-units-from-gitmodules.md)）のために、
   旧契約を TanStack のルートへ実行時変換して接ぎ木する**互換ブリッジが 1 経路だけ残る**
   （[IADR-0124](../adr/IADR-0124_tanstack-router-unit-composition.md) 決定 2。
   AST が新契約へ移れば削除できる）。issue #490 の「実行時 `RouteObject[]` 連結の合成点は廃止」は、
   **MSP 所有ユニットについては達成し、AST 互換ブリッジのみ残る**という状態である。
3. **共通シェル**: 左ナビの 4 グループ化・ブランド表示名・**ユーザーアイコン → SC-16**・**通知**。
4. **旧画面のルート載せ替え**: SC-01〜SC-11 のルート定義・検索パラメータ・遷移・ガードを新方式で書き直し、
   ルートパスを [01_screens §共通シェル](../../planning/projects/microservices-platform/05_screens/01_screens.md)
   の値へ是正する。画面内部のロジック・UI・API 呼び出しは現行の挙動を維持する（後述 §#452 との分担）。
5. 既存テスト（画面 11 ＋ Layout ＋ RequireAuth ほか）の新ルータへの移行と、E2E スモークの新ルータ化。
6. カバレッジ床の維持（引き下げ禁止。IADR-0118 の ratchet 原則）。

### 対象外（送り先を明記する）

| 事項 | 送り先 | 理由 |
| --- | --- | --- |
| 画面内容の計画書準拠 再設計（モックアップ準拠のレイアウト・項目・文言） | **#452** | 後述 §#452 との分担 |
| 未実装画面 SC-12・SC-17〜SC-21 の新規実装 | **#452** | 同上 |
| パンくず・権限バッジ（管理／システム管理／運用） | **#452** | issue #490 の共通シェル範囲は「ナビ・ユーザーアイコン → SC-16・通知」に明示的に限定されている |
| 右レール AI チャットパネル（SSE） | 第 4 段（IADR-0121 決定 5） | SSE 状態管理の実装段 |
| BFF セッション認証（`oidc-client-ts` の撤去） | 第 3 段（#439） | IADR-0121 決定 6。BFF 側未実装のため先に撤去できない |
| Lingui（i18n）・Storybook・shadcn/ui コンポーネントの本移植 | **要起票（第 2 段の残り）** | #446 仕様書の第 2 段表には含まれるが、issue #490 の §スコープ には無い。ルータ移行と同一 PR に入れると IADR-0116 規約 4 に反する（後述 §計画書との差異） |
| Knip / Plop / Renovate / Husky | 第 5 段 | IADR-0121 決定 1 |

### 既存実装の扱い

- `knowledge/frontend/src/features/home/`（`HomePage`）は**削除する**。計画の画面一覧（SC-01〜21）に
  home に相当する画面は存在せず、SC-01 が「本システムの主入口」と定義されている。ルート `/` は
  **SC-01（`/ask`）へリダイレクト**し、その定義は **platform 側**（`foundation/routing/shell.tsx`）が持つ
  ——可変ユニットに置くと、そのユニットを外したときに `/` そのものが消えるためである。
- SC-01〜SC-11 の Page コンポーネント（実装 2792 行）は**残す**。ルーティングに関わる部分
  （`Link` / `useSearchParams` / `useParams`）のみ新方式へ書き換える。

## #452 との分担（issue #490 が「着手時の作業仕様書で確定」と委任した事項）

issue #490 本文は「協調: #452（画面実装 — 同一段。**分担は着手時の作業仕様書で確定**）」と明示的に
本書へ委任している。次のとおり確定する。

> **［2026-08-04］この分担は利用者の明示的裁定を得た。** 裁定原文:
> **「最終的に結果が同じになるなら進め方はそれでもいいです」**。
> 先行する一般裁定（「段階分けは認めます。最終的に一括になっていれば問題なし」）からの
> 実装側の解釈にとどまっていた点を PR #495 の AI レビューが指摘し、利用者へ報告して得た 2 度目の裁定である。
> **条件付き承認である**——承認されたのは「最終的に結果が同じになるなら」という条件の下での進め方であり、
> **条件が満たされるのは #452 が旧 13 画面の削除・再実装を完了した時点**である。
> 経緯・原文・完了条件は
> [feedback/20260804 §追加裁定](../../feedback/20260804_frontend-migration-staging-interpretation.md) を正とする。

| | #490（本作業） | #452 |
| --- | --- | --- |
| ルート定義（パス・階層・親子） | **○** | — |
| 検索パラメータ（`?q=` 等）の型・検証 | **○** | 画面が新しい検索条件を足すときは #452 |
| 画面遷移（`Link` / `navigate`）の配線 | **○**（現行の遷移先を維持したまま新方式へ） | 計画の遷移図に無い遷移の追加・削除 |
| ルートガード（`RequireAuth` / `RequireRole`） | **○** | — |
| 共通シェル（ナビ・ユーザーアイコン → SC-16・通知） | **○** | パンくず・権限バッジ・AI チャットレール |
| 画面内部のレイアウト・項目・文言・モックアップ準拠 | **×**（現行挙動を維持） | **○** |
| SC-12・SC-17〜SC-21 の新規実装 | **×** | **○** |

**この分担にする理由**:

1. **リスクの分離**（IADR-0116 規約 4）。ルータ差し替えは合成点＝アーキテクチャの変更であり、
   13 画面の仕様準拠 再設計と同一 PR にすると独立した 2 つのリスクが結合し、レビューが成立しない。
   本作業だけで実装 6 ファイル・テスト 12 ファイル・foundation 8 ファイルが動く。
2. **同じ画面を 2 回書かない**（IADR-0121 決定 1）ことは、**ルート定義**について守れれば足りる。
   #452 が画面内部を作り直すとき、本作業が確定したルート定義（パス・検索パラメータの型・ガード）は
   そのまま使える。逆に本作業が画面内部へ踏み込むと、#452 が捨てる UI を書くことになる。
3. **計画書のルートパスは #452 を待たずに確定している**（01_screens §共通シェル の「ルートパス
   （wireframe の URL バー準拠）」）。ルートは画面デザインより先に決まっており、先に是正できる。

**これは分担の決定であってスコープの切り捨てではない。** #452 が残りを負うことを、issue #490 の
完了報告と #454 のチェックリストへ明記する（§親への申し送り）。

## 設計

内部設計の判断（選択肢の比較・棄却理由）は [IADR-0124](../adr/IADR-0124_tanstack-router-unit-composition.md)
を正とする。本節は実装の形のみを記す。

### 全体構成

```text
platform/frontend/src/
├── App.tsx                         RouterProvider（TanStack Router）
└── foundation/
    ├── routing/
    │   ├── shell.tsx               rootRoute / loginRoute / callbackRoute / shellRoute（認証済みシェル）
    │   │                            ＋ homeRedirectRoute（`/` → SC-01）／catchAllRoute（未知パス）
    │   ├── featureRegistry.ts      ユニット契約（型付き factory ＋ ナビ宣言 ＋ 旧契約ブリッジ）
    │   ├── router.tsx              ルート木の組み立て・createRouter・Register 型登録
    │   └── nav.ts                  ナビ項目の集約（グループ順・ロール絞り込みは Layout）
    ├── ui/
    │   ├── Layout.tsx              共通シェル（ブランド・4 グループナビ・ユーザーアイコン→SC-16・通知領域）
    │   └── notifications.tsx       通知（sonner。アイコン＋テキストラベルを型で強制）
    └── testing/
        └── renderUnitRoute.tsx     ユニット画面テスト用ハーネス（カバレッジ母数から除外）

knowledge/frontend/src/features/
├── index.ts                        createKnowledgeRoutes(shell)（タプル）＋ knowledgeNavItems
└── sc01-search/ … sc11-config/     各 feature が createXxxRoute(shell) と xxxNav を公開
```

### 1. ルート木と型安全

- `shellRoute` は **path を持たないレイアウトルート**（`id: '_shell'`）とし、`RequireAuth` ＋ `Layout` を担う。
  したがって配下ルートの ID は `/_shell/<path>` になる。
- 各ユニットは `(shell: ShellRoute) => Route` の**ファクトリ**を公開し、ユニットの束ね役が
  `[...] as const` の**タプル**として返す。合成点はタプルをスプレッドする。
  タプルを保つことが型安全の必要条件である（`flatMap` や `AnyRoute[]` を挟むと**ルート ID・パスの
  union が失われる**。実測は IADR-0124 §実測）。
- 型登録は `declare module '@tanstack/router-core'`（`@tanstack/react-router` ではない。IADR-0124 決定 4）。
- 画面側は `useSearch({ from: '/_shell/search' })` / `useParams({ from: '/_shell/docs/$id' })` を使う
  （`Route.useSearch()` は循環参照のため `any` になる。IADR-0124 決定 3）。
- **`/`（ルート直下）と未知パスの受け皿は platform が持つ**（可変ユニットではない。IADR-0124 決定 6・8）。
  - `homeRedirectRoute`: `/` → `ENTRY_ROUTE_PATH`（`/ask` = SC-01。計画が「本システムの主入口」と定義）。
    ユニット側に置くと、そのユニットを外したときに `/` そのものが消える。
  - `catchAllRoute`: `path: '$'` を**共通シェル配下**に置く。`rootRoute` の `notFoundComponent` だけでは
    未知パスがシェルの外に出て、権限による秘匿（シェルの中）と描画が割れる——「シェルが出るかどうか」で
    資源の存在を推測できてしまい存在秘匿（IADR-0009）に反する。移行前の catch-all と同じ配置である。

### 2. ルートパス（計画書 §共通シェル に合わせる）

| SC | 画面 | 旧パス | 新パス（計画書の値） |
| --- | --- | --- | --- |
| — | home（削除） | `/`（index） | `/` → `/ask` へリダイレクト |
| SC-01 | 検索／チャット質問 | `/search` | `/ask` |
| SC-02 | 検索結果一覧 | `/results` | `/search`（`?q=` を型付き検索パラメータで受ける） |
| SC-03 | 文書詳細 | `/documents/:id` | `/docs/$id` |
| SC-04 | Wiki 閲覧導線 | `/wiki` | `/wiki`（変更なし。計画では Wiki.js 別ホストであり SPA 側は導線ページ） |
| SC-05 | 文書管理 | `/documents` | `/admin/documents` |
| SC-06 | データソース管理 | `/datasources` | `/admin/sources` |
| SC-07 | 変換ジョブ | `/conversions` | `/admin/conversions` |
| SC-08 | AI 分析 | `/analysis` | `/analyze` |
| SC-09 | 管理者設定（ABAC） | `/admin/abac` | `/admin/abac`（変更なし） |
| SC-10 | 運用ダッシュボード | `/ops` | `/admin/ops` |
| SC-11 | 構成ビューア | `/config` | `/admin/config-viewer` |

`/login` `/callback` は SPA 内部の認証導線であり計画書のルート表に無い。第 3 段（#439）で
BFF セッション方式へ移るまで現状のパスを維持する。

> **［2026-08-04 追記］計画が「本表の対象外」と確定した**
> （[01_screens §共通シェル ［2026-08-04 確定］](../../planning/projects/microservices-platform/05_screens/01_screens.md)。
> planning#185。本表は画面と 1 対 1 に対応する表であり、画面を持たない中継点を入れると表の定義が緩む。
> **ADR-0032 への移行時に見直す**）。本 PR の扱いは計画と一致する。

### 3. 共通シェル

- **左ナビ 4 グループ**（01_screens §共通シェル）: 利用者 / 個人 / 管理 / 運用。項目が 0 件の
  グループは見出しごと描画しない（「個人」= SC-19・SC-20 は未実装のため現時点で非表示）。
  ~~グループ未宣言のユニット（AST 等）の項目は末尾の「その他」へ置く。~~（**#496 で是正済み。下記参照**）
  - > **［2026-08-04 追記・是正済み］計画が「総称としての『その他』は使わない」と確定した**
    > （[01_screens §共通シェル ［2026-08-04 確定］](../../planning/projects/microservices-platform/05_screens/01_screens.md)。planning#185）。
    > 本 PR が置いた「その他」は **#496 で「ユニットの機能名」（`ai-stock-trading` → 「株式自動売買」）へ
    > 置き換えた**（[IADR-0125](../adr/IADR-0125_ui-primitives-i18n-catalog-and-storybook.md) 決定 9）。
    > 並び順（計画の 4 グループの後）は本 PR のままである。
  表示は従来どおりロールで絞り込む（権限外は描画しない＝存在秘匿）。
- **ブランド表示名**: 「汎用プラットフォーム」（01_screens §共通シェル。従来の "Knowledge Platform" を是正）。
- **ユーザーアイコン**: 右上に置き、押下で **SC-16（Keycloak アカウントコンソール）へ遷移**する。
  遷移先は実行時 config の `oidc.authority` から `${authority}/account` を組み立てる
  （ビルドへ焼き込まない）。SC-16 は共通シェル適用外の別ホストであるため `<a>` による外部遷移とする。
- **通知**: `sonner` を `foundation/ui/notifications.tsx` で包み、`notify.success/info/warning/error`
  の 4 種のみを公開する。各種は**アイコンとテキストのラベル**（「成功」「情報」「注意」「エラー」）を
  必ず伴い、呼び出し側が省略できない API にする（INDEX 決定 21「色だけで意味を持たせない」を
  `StatusBadge` と同じ作法で型に落とす）。

### 4. 旧契約ブリッジ（AST 対応）

`src/ai-stock-trading`（submodule・別プロジェクト。[IADR-0120](../adr/IADR-0120_excluded-units-from-gitmodules.md)）は
本リポジトリから変更できないが、その 3 features は platform の合成点から束ねられている。
既存契約 `FeatureModule { id, routes: {path, element}[], nav }` を**そのままの形で残し**
（`RouteObject` 型の出所だけを `react-router-dom` から自前の型へ移す）、
platform 側で TanStack のルートへ**実行時に変換して**木へ足す。詳細と型安全への影響は IADR-0124 決定 2。

## 受け入れ基準

issue #490 §受け入れの観点 の 4 件を、検証可能な形へ展開する。

- [x] **`react-router-dom` が依存から消える**: `src/platform/frontend/package.json` と
      `src/knowledge/frontend/package.json` から削除され、
      `grep -rnE "from '(react-router\|react-router-dom)'" src/platform src/knowledge` が 0 件
      （AST は対象外＝本リポから変更できない別プロジェクト）。
      **`grep -rn "react-router" …` は判定に使えない**——`@tanstack/react-router` の import と
      説明コメントに当たるため 0 件にならない（実測 38 件。同じ罠を lint 規則でも踏んだ。
      [IADR-0124 §実測](../adr/IADR-0124_tanstack-router-unit-composition.md#no-restricted-imports-の照合方式機械強制の落とし穴)）
- [x] **ルート定義が型安全**: 存在しないルート ID・存在しないパスへの `Link`・検索パラメータの
      型不一致が `tsc` で落ちることを、違反サンプルで実測して本書に記録する
- [x] **11 画面が新ルータで動作する**: 既存の画面テスト（SC-01〜SC-11）が新ルータで green、
      ルートパスが計画書 §共通シェル の値へ是正されている
- [x] **E2E スモークが新ルータで通る**（もしくは実走不能の理由と CI へ委ねる根拠を本書に記録する）
- [x] **カバレッジ床の引き下げなし**: `src/vitest.config.ts` の `thresholds`（移行前は lines/statements 83 /
      functions 75 / branches 74）を下げない。実測値を測定条件つきで本書に記録する
      （結果: ratchet として **86 / 79 / 77** へ引き上げた）
- [x] 共通シェルが 4 グループナビ・ブランド表示名・ユーザーアイコン → SC-16・通知を備える
- [x] AST（submodule）の typecheck / lint / テストが**無改修で**通る
- [x] `pnpm run lint` / `typecheck` / `test:coverage` / `build` が green
- [x] `node scripts/check-doc-links.js` / `node scripts/check-commit-messages.js --base origin/develop` /
      `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が green

## テスト方針

- **ルート木（新規）**: `router.test.ts` で (1) 計画書のルートパスが全て木に存在すること、
  (2) 全ナビ項目の `to` が木に解決すること、(3) 旧契約ブリッジ経由の AST ルートが木に載ること、
  (4) `/` が実際に SC-01 へリダイレクトすること（**ナビゲートさせて `beforeLoad` を通す**。
  `buildLocation` では通らず「redirect が壊れても緑」になる）、(5) catch-all が実在ルートを
  横取りしないこと を固定する。
  (2) は `<Link to>` の静的検査がナビ（データ駆動）には効かない穴を埋めるためである（IADR-0124 決定 5）。
- **存在秘匿の描画一致（新規）**: `Layout.test.tsx` で「未知パス」と「権限外パス（SC-11）」の
  `NotFound` の markup が**一致する**ことを固定する（IADR-0124 決定 8）。
- **オープンリダイレクト（新規）**: 認証導線の遷移先は 2 経路とも外部由来である
  （`/login?from=` は URL に載る・OIDC の `state.returnTo` は認可サーバを往復する）。
  判定は `foundation/auth/safeRedirect.ts` の `toInternalPath()` へ集約し、**3 層で固定する**。
  1. `safeRedirect.test.ts`: 純関数の判定（内部パス保持・スキーム相対・絶対 URL・`javascript:`・
     **バックスラッシュ**・非文字列・欠落）
  2. `loginRouteSearch.test.ts`: **実装のルート定義**（`loginRoute.options.validateSearch`）を直接呼ぶ
  3. `CallbackPage.test.tsx`: OIDC の `state.returnTo` を与えて `navigate` の宛先を検査する
  **テスト内に判定条件を書き写さない**——写すとテストが自分の写しを検査し、実装が緩んでも気付けない。
  検証条件を外すとテストが落ちることを変異試験で確認する（§検証）。
- **共通シェル**: ロール別のナビ表示（存在秘匿）の既存観点を維持しつつ、グループ見出し・
  ユーザーアイコンの遷移先（SC-16）を追加で固定する。
- **通知**: 4 種すべてが**テキストのラベルを伴う**ことを固定する（色だけに依存していないことの機械検査）。
- **画面テスト**: 既存 11 画面のテストは、`MemoryRouter` によるラップを TanStack の
  テスト用ルータ（`createMemoryHistory` ＋ `RouterProvider`）または素の描画へ置き換える。
  **観点（アサーション）は変更しない**——退行検知の能力を落とさないため。
  `access.test.tsx` 系（権限外は `NotFound`・API を呼ばない）は挙動をそのまま維持する。
- **E2E**: 既存スモーク 6 本のパスを新ルートへ更新し、未認証 → `/login` 誘導が新ルータでも成り立つことを見る。

## 検証（実測）

**測定条件**: worktree `feat/ADR-0031-spa-router-shell`（`origin/develop` `be3c71c` 基点）／
Node 22.22.2 ／ pnpm 10.33.0 ／ Vitest 3.2.7（v8 provider）／ TypeScript 5.9.3 ／
`@tanstack/react-router` 1.170.18（`@tanstack/router-core` 1.171.15 を解決）／
**submodule `src/ai-stock-trading` と `planning` は populate 済み**。

| 検査 | コマンド | 結果 |
| --- | --- | --- |
| 型検査 | `pnpm run typecheck` | green（4 パッケージ。AST は**無改修**） |
| lint | `pnpm run lint` | green（0 errors / 5 warnings。warning は `react-refresh/only-export-components` のみ） |
| 単体テスト | `pnpm run test` | **40 files / 337 tests** 全 green（移行前は 35 files / 227 tests） |
| カバレッジ | `pnpm run test:coverage` | 後述 |
| ビルド | `pnpm run build` | green（`dist/assets/index-*.js` 537.65 kB / gzip 158.08 kB） |
| E2E | `playwright test`（後述の条件） | **6 tests 全 green** |
| ドキュメントリンク | `node scripts/check-doc-links.js` | green（408 件） |
| ユニット依存方向 | `node scripts/check-unit-dependencies.js` | green（違反なし） |
| テスト・トレーサビリティ | `node scripts/check-test-traceability.js` | green（仕様書のある 28 件が全て写像済み） |
| コミット件名 | `node scripts/check-commit-messages.js --base origin/develop` | green |
| スクリプト自己試験 | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | green |

### `react-router-dom` の撤去（受け入れ観点 1）

判定コマンドと実測（本 PR の HEAD で実走）:

| コマンド | 結果 | 意味 |
| --- | --- | --- |
| `grep -rnE "from '(react-router\|react-router-dom)'" src/platform src/knowledge` | **0 件** | 旧ルータの import が無い（**これが判定**） |
| `grep -rn "react-router-dom" src/platform src/knowledge` | **3 件** | いずれも**説明コメント**（`App.tsx` の移行注記 1 件・`featureRegistry.ts` の旧契約の由来 2 件） |
| `grep -rn "react-router" src/platform src/knowledge` | 38 件 | **判定に使えない**。`@tanstack/react-router`（新ルータ）の import と説明コメントに当たる |

`package.json` の依存からも削除した（platform / knowledge の両方）。
3 行目の罠は lint 規則でも踏んでおり（`patterns` の matchBase が `@tanstack/react-router` に当たる）、
[IADR-0124 §実測](../adr/IADR-0124_tanstack-router-unit-composition.md#no-restricted-imports-の照合方式機械強制の落とし穴)
に記録した。

**AST（`src/ai-stock-trading`）には残る**——別プロジェクトの submodule であり本リポジトリから
変更できない（IADR-0120）。AST は自分の `package.json` で `react-router-dom` を宣言しており、
その解決は AST パッケージに閉じる。本リポジトリの SPA が動かすルータは TanStack Router の 1 本だけである
（旧契約ユニットの画面も TanStack のルートへ変換して載せる。IADR-0124 決定 2）。

再混入は lint で機械的に止める（`no-restricted-imports` の `paths`。platform / knowledge のみ適用）。
**発火確認（実測）**: `react-router-dom` / `react-router` を import する違反ファイルを一時的に置き、
`npx eslint` が 2 件の error を出すことを確認して削除した。

### ルート定義の型安全（受け入れ観点 2）

負のプローブ（**落ちるべきコード**）を `tsc` にかけた実測は
[IADR-0124 §実測](../adr/IADR-0124_tanstack-router-unit-composition.md#実測) の 3 表を正とする。要点:

- 存在しないルート ID を `useSearch({ from })` へ渡す → **落ちる**
- 検索パラメータの型不一致（`{ q: string }` を number へ）→ **落ちる**
- 存在しないパスへの `<Link to>` → **落ちる**
- パスパラメータの欠落（`<Link to="/docs/$id">` に `params` なし）→ **落ちる**

型登録の宛先（`@tanstack/router-core`）とタプル保持を誤ると、**型エラーを出さずに**これら 4 つが
すべて素通りになる。両方の失敗モードを実測して IADR へ記録した。

### 画面とルート（受け入れ観点 3）

11 画面（SC-01〜SC-11）が新ルータで動作し、ルートパスが計画書 §共通シェル の値へ是正されている。
`router.test.ts` が (1) 計画のルートが木に存在すること、(2) 全ナビ項目の遷移先が解決すること、
(3) 旧経路（`/results` `/documents` `/datasources` `/conversions` `/analysis` `/ops` `/config`）が
**消えていること**、(4) 旧契約ブリッジ経由の AST 3 画面が載ることを固定する（28 ケース）。

### E2E（受け入れ観点 4）

`platform/frontend/e2e/` の 6 本を新ルートへ更新し、**6 tests 全 green**。

**この環境では `playwright install` がブラウザをダウンロードできない**（`Download failure, code=1`）。
インストール済みの `chromium-1194` を `launchOptions.executablePath` で指すローカル専用 config を
一時的に置いて実走し、確認後に削除した。**CI（`frontend.yml`）は `playwright install --with-deps chromium`
を実行するため、リポジトリの `playwright.config.ts` はそのままで動く**（設定に手を入れていない）。

### オープンリダイレクト対策（実測）

**初版の実装には実際に穴があった。** 前方一致（`startsWith('/') && !startsWith('//')`）は
`/\evil.com` を通すが、WHATWG URL 仕様（＝ブラウザの解釈）ではバックスラッシュがスラッシュと
同一視され `https://evil.com` へ解決される。実測表と判定方式の変更は
[IADR-0124 決定 9・§実測](../adr/IADR-0124_tanstack-router-unit-composition.md) を正とする。

判定を `toInternalPath()`（自 origin を基準に `new URL()` で解決し origin を照合）へ改め、
`/login?from=` と OIDC `state.returnTo` の両方をそこへ集約した。

**検証の実効性（変異試験）**:

| 変異 | 落ちたテスト |
| --- | --- |
| `origin` 照合を外し前方一致だけに戻す（＝初版と同じ） | **5 件**（ヘルパ 3 / `loginRoute` 1 / `CallbackPage` 1） |
| 検証を完全に外す | **10 件**（ヘルパ 6 / `loginRoute` 2 / `CallbackPage` 2） |

いずれも復元で全件 green。**3 層すべてで落ちる**ため、どこか 1 層だけを緩めても検知できる。

### カバレッジ（受け入れ観点 4）

| | 移行前（`be3c71c`） | 本 PR | 床（本 PR で引き上げ） |
| --- | --- | --- | --- |
| 全ユニット横断 lines/statements | 91.46% | **93.79%** | 83 → **86** |
| 全ユニット横断 branches | 82.33% | **83.54%** | 74 → **77** |
| 全ユニット横断 functions | 83.58% | **85.53%** | 75 → **79** |
| MSP 所有分 lines/statements | 88.07% | **91.73%** | （床の導出基準） |
| MSP 所有分 branches | 80.00% | **82.04%** | 同上 |
| MSP 所有分 functions | 80.76% | **84.43%** | 同上 |

**引き下げはしていない。** `src/vitest.config.ts` の既存の導出規則（MSP 所有分の実測から 5pt 下・
切り捨て）をそのまま適用して引き上げた（ratchet。IADR-0034 / IADR-0118）。
MSP 所有分は lcov から AST のファイルを除いて再集計した値である。

計測対象から `platform/frontend/src/foundation/testing/**`（画面テスト用ハーネス）を除外した。
`src/test/**` と同じ理由——足場を母数に入れると「テストを足すほど床が上がる」見かけの改善が起きる。
**この除外が床を甘くしていないことを実測で確認した。** 除外**しない**場合の MSP 所有分は
lines 91.84% / branches 82.19% / functions 84.02% であり、同じ導出規則（−5pt・切り捨て）から出る床は
**3 指標とも同値（86 / 77 / 79）**である。すなわちこの除外は床の水準を動かしていない。

**記録**: `notify`（通知 API）は共通シェルの基盤として先行整備したものであり、**本番の呼び出し元は
現時点で 0 件**（参照は `notifications.test.tsx` のみ）。画面から通知を出すのは #452 以降である。
したがって床の引き上げ分の一部は「まだ利用者のいないモジュールの専用テスト」に由来する。
先行整備自体は #490 のスコープ（共通シェルの通知）に含まれるが、被覆率の読み方としては
この点を差し引いて読む必要がある。

### AST（別プロジェクト）への影響（実測）

- **無改修で green**: typecheck（`tsconfig.standalone.json`）/ lint / テスト 40 件がすべて通る。
- 理由は旧契約 `FeatureModule` の**形を変えなかった**こと（`routes: { path, element }[]`）。
  AST の `test/foundation-stub/routing/featureRegistry.ts` は `RouteObject` を `react-router-dom` から
  import しているが、これは AST の standalone 型検査専用であり、合成時は platform の実体が解決される。
  実体側の要素型が自前の `LegacyFeatureRoute` に変わっても、AST の object literal は構造的に適合する。
- **将来の申し送り**: AST が型付きルート factory（新契約）へ移れば、AST の 3 画面も型安全の中に入り、
  旧契約ブリッジを削除できる。本リポジトリからは変更できないため **AST リポジトリでの issue 起票が要る**
  （優先度は低い——現状で機能欠損は無く、失うのは AST 画面の `Link` 型検査のみ）。

## 計画書との差異

| 事項 | 計画・issue の記載 | 実装 | 根拠 |
| --- | --- | --- | --- |
| ルート定義の方式 | issue #490「**ファイルベース定義**で確立」 | **コードベースの型付きルート木** | ADR-0031 §理由 が挙げる採用根拠は「ルート・検索パラメータまで型安全にできる」ことであり、ファイルベースはその手段の例示である。ファイルベースは IADR-0056 決定 3・4（platform → 可変ユニット禁止／合成点 1 ファイル）を壊す。型安全は実測で同等（[IADR-0124](../adr/IADR-0124_tanstack-router-unit-composition.md) 論点 A） |
| 旧 13 画面 | 13_frontend-stack「旧画面は**完全に削除する**」（主語は**画面**である） | `home` のみ削除。SC-01〜11 は**ルート定義を書き直し、Page は残した**（＝本 PR 単独では未達） | **削除・再実装は同一段内の #452 に割り当てられている**。[feedback/20260804](../../feedback/20260804_frontend-migration-staging-interpretation.md) §完了条件（status: accepted）は「`react-router-dom` がワークスペースから消えている（第 2 段）」と「旧 13 画面が削除され再実装されている（第 2 段 / **#452**）」を**別条件として並置**しており、[IADR-0121](../adr/IADR-0121_spa-stack-migration-staging.md) 決定 1 も第 2 段を #452 と同一段に置いている。したがって本 PR での Page 残置は**放棄ではなく繰り延べ**であり、#452 が SC-01〜11 の Page を作り直した時点で条件が満たされる。`home` は計画の画面一覧（SC-01〜21）に対応画面が無いため本 PR で削除した |
| 第 2 段の範囲 | #446 仕様書の第 2 段表は Lingui・Storybook・shadcn/ui 本移植も含む | 含めない | issue #490 §スコープ が 4 項目（ルータ・共通シェル・旧画面・カバレッジ床）に限定している。同一 PR に入れると IADR-0116 規約 4 に反する（**残りは要起票**。§未決事項） |
| 共通シェル | 05_screens §共通シェル はパンくず・権限バッジ・右レール AI チャットも含む | ナビ・ブランド名・ユーザーアイコン → SC-16・通知のみ | issue #490 §スコープ の明示。パンくず・権限バッジは #452、AI チャットは第 4 段（IADR-0121 決定 5） |
| 左ナビのグループ数 | 05_screens §共通シェル は **4 グループ**（利用者／個人／管理／運用） | **5 番目のグループ「その他」を追加**した（**［2026-08-04］計画が裁定し、#496 で是正済み**） | 計画の 4 グループは MSP の画面（SC-01〜21）に対する割り当てであり、**本リポジトリの計画に属さない可変ユニット**（`src/ai-stock-trading`。独自の計画と画面 ID を持つ別プロジェクト。IADR-0120）の項目に置き場が無い。既存 4 グループのいずれかへ混ぜると計画の割り当てを歪めるため、`group` 未宣言の項目を集める受け皿として末尾に置いた。**［2026-08-04 追記］この差異は計画へ環流し裁定された**（planning#185）——計画は「実装側でグループを設けて分類してよい（4 グループは変更しない）。**ただしグループ名は『ユニットの機能名』とする**。並び順は 4 グループの後。**総称としての『その他』は使わない**」と確定した。本 PR の**並び順は計画どおり**だったが**命名は違反**であり、**#496 で「株式自動売買」へ是正した**（[IADR-0125](../adr/IADR-0125_ui-primitives-i18n-catalog-and-storybook.md) 決定 9） |
| ルート `/` の扱い | 05_screens §共通シェル のルート表に `/` は無い | `/` を SC-01（`/ask`）へリダイレクトする | 計画の画面一覧に home に相当する画面が無い一方、SPA のルート直下に何も無いのは成立しない。SC-01 が「本システムの主入口」と定義されているため、そこへ送る（[IADR-0124](../adr/IADR-0124_tanstack-router-unit-composition.md) 決定 6） |

## 親への申し送り

本作業は **#452 と同一段**であり、第 2 段の完了には次が残る。**分担であって切り捨てではない**ことを、
#490 の完了報告と #454 のチェックリストへ明記する。

**この分担は 2026-08-04 に利用者の明示的裁定を得ている**（原文:
**「最終的に結果が同じになるなら進め方はそれでもいいです」**。
[feedback/20260804 §追加裁定](../../feedback/20260804_frontend-migration-staging-interpretation.md)）。
ただし**条件付き承認**であり、**条件（最終結果の同一性）が満たされるのは #452 の消化まで**である。
分割の承認は「#452 を省いてよい」を意味しない。

### #452 が引き受ける項目（#490 で意図的に触れなかったもの）

| 項目 | 内容 |
| --- | --- |
| **旧 13 画面の削除・再実装** | SC-01〜11 の Page（実装 2792 行）は本 PR ではルーティング部分のみ書き換え、内部は現行挙動を維持した。**#452 が Page を作り直すまで、13_frontend-stack「旧画面は完全に削除する」は未達である**（[feedback/20260804](../../feedback/20260804_frontend-migration-staging-interpretation.md) §完了条件 が「旧 13 画面の削除・再実装」を #452 に割り当てている） |
| 画面内容の計画準拠 再設計 | [05_screens](../../planning/projects/microservices-platform/05_screens/01_screens.md) と `05_screens/mockups/`（hi-fi 正）への準拠。レイアウト・項目・文言・警告色（琥珀）等 |
| 未実装画面の新規実装 | SC-12（MCP クライアント管理）・SC-17（ユーザーアカウント管理）・SC-18（ナレッジグラフ）・SC-19（個人資料）・SC-20（Obsidian 連携）・SC-21（AI 提案一覧） |
| 共通シェルの残り | **パンくず**・**権限バッジ**（管理／システム管理／運用）。issue #490 の共通シェル範囲は「ナビ・ユーザーアイコン → SC-16・通知」に限定されている |
| 左ナビ「個人」グループ | SC-19 / SC-20 の実装により初めて項目が入る（現状は項目 0 件のため見出しごと非表示） |
| SC-04 の左ナビ差し替え | 「閲覧時は左レールを Wiki ページツリーへ置換する」（05_screens モック間相違の確定 ①） |

### #454 チェックリストへの追記内容

1. **第 2 段は 2 つに分かれた**: #490（TanStack Router ＋ 共通シェル ＋ 旧画面のルート載せ替え）＝本 PR で完了。
   残り（**shadcn/ui コンポーネント本移植・Lingui(ja/en)・Storybook**）は**要起票**
   （[IADR-0121 決定 1 の 2026-08-04 追記](../adr/IADR-0121_spa-stack-migration-staging.md) 参照）。
2. **旧 13 画面の削除・再実装は #452 で完了する**（本 PR では未達。上表）。
3. AST（`src/ai-stock-trading`）の新契約移行は**別リポジトリの issue**。本リポからは変更できない
   （[IADR-0120](../adr/IADR-0120_excluded-units-from-gitmodules.md)）。移行すれば旧契約ブリッジを削除できる。

### 残件起票に必要な情報（shadcn/ui 本移植・Lingui・Storybook）

- **起点 ID**: ADR-0031（計画 ADR。[13_frontend-stack](../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md) §採用技術一覧が正）
- **スコープ**: (a) shadcn/ui 派生プリミティブを `@platform/ui` へ本移植（現状は Button / StatusBadge の 2 つ。
  Input・Select・Dialog・Table・Tabs 等、画面が要求する範囲）、(b) Lingui（ja / en。コンパイル時抽出）の導入と
  既存文言の抽出、(c) Storybook のセットアップと `@platform/ui` のカタログ化
- **受け入れ基準の骨子**: `@platform/ui` の公開面が 1 ファイル（`index.ts`）のままであること
  （[IADR-0121](../adr/IADR-0121_spa-stack-migration-staging.md) 決定 4 の切り出し規則を守る）／
  ja / en の切替が動き未翻訳キーが CI で検出できること／Storybook がビルドでき外部 CDN を読まないこと
  （[08_data-egress-policy](../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md)）／
  カバレッジ床を割らないこと
- **依存**: #490（本 PR）のマージ後。#452 とは並行可能だが、shadcn/ui 本移植は #452 の画面実装が
  必要とする部品を先に入れる形が望ましい

## 未決事項

1. ~~**第 2 段の残り（Lingui / Storybook / shadcn/ui コンポーネントの本移植）の起票。**~~
   → **解消（2026-08-04）**: #496 として起票され、[作業仕様書](./20260804_issue-496_ui-i18n-storybook.md) と
   [IADR-0125](../adr/IADR-0125_ui-primitives-i18n-catalog-and-storybook.md) のもとで消化された。
   **第 2 段の完了条件そのものは #452（旧 13 画面の削除・再実装）待ちのままである**（下記 2）。
2. **旧 13 画面の削除・再実装（#452）。** 本 PR は SC-01〜11 の Page を残しており、
   13_frontend-stack「旧画面は完全に削除する」は**本 PR 単独では未達**である（§計画書との差異・§親への申し送り）。
3. **AST の新契約への移行**（別リポジトリの issue）。旧契約ブリッジの削除条件。
4. ~~**`/login` `/callback` の扱い。**~~ → **解消（2026-08-04）**: 計画が
   [01_screens §共通シェル](../../planning/projects/microservices-platform/05_screens/01_screens.md) で
   「**本表の対象外**」と確定した（planning#185）。**ADR-0032 への移行時に見直す**という方針も計画側に入った。
   実装の扱いは計画と一致しており、追加の作業は無い。
5. **バンドルサイズ。** `index.js` が 537 kB（gzip 158 kB）で Vite の 500 kB 警告に触れる。
   コード分割（ルート単位の `lazy`）は TanStack Router の機能で行えるが、画面が確定する #452 の後が適切である。
   **［2026-08-05 追記］#512 / [[IADR-0134]] で消化**（ルート単位の遅延 ＋ `manualChunks` 3 規則。500 kB 警告は解消し、初期ロードは 632.98 → 577.54 kB / gzip 190.04 → 177.94 kB）。
