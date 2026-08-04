---
title: IADR-0124 TanStack Router とユニット合成の両立 — 型付きルート木・旧契約ブリッジ・型登録の実装形
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0031, SC-01, SC-02, SC-03, SC-16, IADR-0033, IADR-0035, IADR-0056, IADR-0070, IADR-0116, IADR-0120, IADR-0121]
author: Claude
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md"
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/INDEX.md"
related_specs:
  - ../specs/20260804_issue-490_spa-router-shell.md
  - ../specs/20260804_issue-446_spa-foundation-stack-migration.md
---

# IADR-0124: TanStack Router とユニット合成の両立（型付きルート木・旧契約ブリッジ・型登録の実装形）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-08-04
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID:
  [ADR-0031](../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md)（Accepted。
  **ルーティング = TanStack Router**。§理由 が採用根拠に「ルート・検索パラメータ（`/search?q=` 等）まで
  型安全にできる」を挙げる＝**型安全が目的、手段は問わない**）／
  [13_frontend-stack](../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md)（fixed）／
  [01_screens](../../planning/projects/microservices-platform/05_screens/01_screens.md) §共通シェル（ルートパス）／
  [INDEX](../../planning/projects/microservices-platform/INDEX.md) 決定 21（色だけで意味を持たせない）
- 関連する実装 ADR:
  [IADR-0121](IADR-0121_spa-stack-migration-staging.md)（5 段分割。決定 1 の第 2 段が本作業。
  「実行時に `RouteObject[]` を連結する現行の合成点のままでは型安全は得られない」と述べている）／
  [IADR-0056](IADR-0056_repo-unit-structure-platform-knowledge.md)（**決定 3: platform → 可変ユニットの参照禁止**・
  **決定 4: 合成点は 1 ファイル・ユニット追加は submodule 配置＋合成点 1 行**。本決定はこれらを維持する）／
  [IADR-0035](IADR-0035_frontend-role-based-nav-and-existence-hiding.md)（ナビの存在秘匿）／
  [IADR-0070](IADR-0070_ast-frontend-integration.md)（AST が `@knowledge` と同形で合成される形）／
  [IADR-0120](IADR-0120_excluded-units-from-gitmodules.md)（**AST は別プロジェクト＝本リポから変更できない**）／
  [IADR-0116](IADR-0116_reimplementation-branching-and-pr-policy.md)（規約 4）
- 関連する実装仕様書:
  [20260804_issue-490](../specs/20260804_issue-490_spa-router-shell.md)（本決定と対で読む）
- 関連 issue: #490（起点・親 #454）／#446（第 1 段 = PR #489）／#452（画面実装）

## コンテキストと課題

計画は `react-router-dom` を捨て **TanStack Router** を採ると確定した。採用根拠は「ルート・検索
パラメータまで型安全にできる」ことである。ところが本リポジトリのフロントエンドは
[IADR-0056](IADR-0056_repo-unit-structure-platform-knowledge.md) の**ユニット第一構成**を採っており、
次の 2 つが正面から衝突する。

1. **TanStack Router の型安全は「1 本の静的なルート木」から生まれる。** 型は木の値から推論されるため、
   木に何が載るかがコンパイル時に確定していなければ、ルート ID・パス・検索パラメータの型は
   すべて失われる。
2. **ユニット分離は「platform が可変ユニットを知らないこと」を要求する。** IADR-0056 決定 3 は
   platform → 可変ユニットの参照を禁止し、例外は**合成点 1 ファイル**のみである。可変ユニットは
   submodule で足せる（決定 4）。

さらに 3 つ目の制約がある。

3. **`src/ai-stock-trading`（AST）は本リポジトリから変更できない**（[IADR-0120](IADR-0120_excluded-units-from-gitmodules.md)。
   別プロジェクトの submodule）。AST の 3 features は現行契約
   `FeatureModule { id, routes: RouteObject[], nav }` で platform の合成点に束ねられており、
   `test/foundation-stub/routing/featureRegistry.ts` がその契約を写像している。
   **契約を非互換に変えると、本リポジトリの CI が AST 側の修正なしには green にならない。**

## 検討した選択肢

### 論点 A: 型付きルート木とユニット分離の両立方式

| | A. platform に `routes/` ファイルツリーを置き knowledge の画面を import | **B. コードベースの型付きルート木（採用）** | C. Virtual File Routes（ファイルベースの型生成＋ルート定義はコード） |
| --- | --- | --- | --- |
| IADR-0056 決定 3（platform → 可変ユニット禁止） | **反する**。画面ごとに 1 ファイルが knowledge を import し、合成点が 1 ファイルでなくなる | 適合（参照は合成点 1 ファイル。ユニットは shell を引数で受け取る） | 反する度合いは A と同じ（仮想ルート定義が各画面ファイルを名指しする） |
| IADR-0056 決定 4（ユニット追加＝submodule ＋ 1 行） | **反する**（画面数だけファイルが要る） | 適合（合成点へスプレッド 1 行） | 反する（画面数だけ行が要る） |
| ルート・検索パラメータの型安全 | 最大 | **同等（実測で確認。§実測）** | 同等 |
| 追加のツールチェーン | 不要 | **不要** | Vite プラグイン＋生成物のコミット＋codegen ドリフト検査が要る |
| AST（変更不可）の扱い | ブリッジが要る | ブリッジが要る | ブリッジが要る（生成木の外に残る） |

issue #490 は「ファイルベース定義で確立」と書くが、これは ADR-0031 §理由 の
**目的（型安全）に対する手段の例示**である。B は同じ目的を、ユニット分離を壊さず・追加の
ツールチェーンなしで達成する。C は A と同じ構造上の問題（合成点が 1 ファイルでなくなる）を抱えつつ
生成物のコミットと codegen ドリフト検査を増やし、**AST のブリッジが生成木の外に残る点は B と変わらない**。

### 論点 B: 旧契約（AST）の扱い

| | B1. 契約を新方式へ一新（AST は壊れる） | **B2. 旧契約を実行時ブリッジとして残す（採用）** | B3. AST を合成点から外す |
| --- | --- | --- | --- |
| 本リポの CI | **赤**（AST を修正できないため） | green | green |
| AST の画面 | 動かない | 動く | **動かない**（機能の後退） |
| 型安全（MSP 所有の画面） | 最大 | 最大（§決定 2 の分離により汚染しない） | 最大 |
| 型安全（AST の画面） | — | 無い（`Link` の union に現れない） | — |
| 可逆性 | — | AST が新契約へ移れば削除できる | 再合成の作業が要る |

### 論点 C: `Register` 型登録の宛先

TanStack の公式手順は `declare module '@tanstack/react-router'` だが、当リポの解決では
`Register` インターフェースの**宣言元は `@tanstack/router-core`** であり、`@tanstack/react-router` は
それを再エクスポートしているだけである。再エクスポート側を augment しても**別のインターフェースが
できるだけで型は結びつかない**（実測。§実測）。

## 決定

### 決定 1: ユニットは「型付きルート factory のタプル ＋ 宣言的ナビ」を公開する（論点 A = B）

- 可変ユニットの各 feature は `(shell: ShellRoute) => Route` の**ファクトリ**を公開する。
  `shell`（認証済みレイアウトルート）は platform が引数で渡す。**ユニットは platform の
  ルート木を import しない**——依存方向は従来どおり「可変ユニット → `@foundation` の型のみ」である。
- ユニットの束ね役（`knowledge/frontend/src/features/index.ts`）は
  `[createSc01Route(shell), …] as const` の**タプル**を返す。
- 合成点（`platform/frontend/src/features/index.ts`）はタプルを**スプレッド**して 1 本の配列にする。
  ユニット追加は**スプレッド 1 行**であり、IADR-0056 決定 4 の性質を保つ。
- **タプルであることが型安全の必要条件である。** `Array.prototype.flatMap` を挟む、あるいは
  戻り値へ `readonly AnyRoute[]` の型注釈を付けると、**ルート ID とパスの union が失われる**
  （検索パラメータの型は残る）。実測は §実測 の表を正とする。
  したがって束ね役・合成点に**型注釈を書かない**（`satisfies` も使わない）。

### 決定 2: 旧契約 `FeatureModule` は「実行時だけの橋」として残し、型付き木を汚染させない（論点 B = B2）

- `FeatureModule { id, routes: { path, element }[], nav }` の**形は変えない**。変えるのは
  `routes` の要素型の出所だけで、`react-router-dom` の `RouteObject` から自前の
  `LegacyFeatureRoute` へ移す。AST の object literal はこれを構造的に満たすため、
  **AST 側の修正は不要**である（実測: AST の typecheck / lint / テストが無改修で green）。
- platform は旧契約のルートを `createRoute({ getParentRoute: () => shell, path, component })` へ
  実行時に変換し、**型付きの `addChildren` を済ませた後で** shell の `children` へ足す。
  型付き木の推論に旧契約のルートは現れない。
  - **これを守らないと型安全が全部消える。** `AnyRoute[]` を `addChildren` の配列へ
    スプレッドすると、ルート ID・パスの union も検索パラメータの型も失われる（実測。§実測）。
- 旧契約は `@deprecated` とし、「本リポジトリから変更できないユニット（IADR-0120）のための互換橋である」
  ことをコード内に明記する。**AST が新契約へ移った時点でブリッジごと削除できる。**

### 決定 3: 画面は `useSearch({ from })` / `useParams({ from })` を使う（`Route.useSearch()` は使わない）

当リポの構成では `route.useSearch()` / `route.useParams()` / `getRouteApi(id).useSearch()` の戻り値は
**`any` になる**（実測）。ルートオブジェクト → `Register` → router → ルート木 → ルートオブジェクト、の
循環型参照を TypeScript が解けないためである。スタンドアロンのフックへ**リテラルのルート ID**を渡す形
（`useSearch({ from: '/_shell/search' })`）は循環を作らず、**厳密に型が付く**。

- 副作用として画面コードにルート ID のリテラル（`/_shell/...`）が現れる。存在しない ID は
  `tsc` が落とすため、綴り間違いは検出できる。

### 決定 4: `Register` の型登録は `@tanstack/router-core` へ行う

- `declare module '@tanstack/router-core' { interface Register { router: typeof router } }` とし、
  `@tanstack/router-core` を `platform/frontend` の **devDependency として明示的に宣言**する
  （pnpm の厳密解決では、宣言していないパッケージ名は module augmentation の宛先にできない）。
- `@tanstack/react-router` を augment した場合、型エラーは出ないまま
  **`useSearch` / `useParams` / `Link` の型が全て緩む**（実測）。**失敗が静かである**ため、
  §実測 の負のプローブ（違反サンプルが `tsc` で落ちること）を受け入れ基準に置く。

### 決定 5: ナビ項目の遷移先は「実行時に検査する」

共通シェルのナビはユニットが公開する**データ**（`{ label, to, requiresAnyRole, group }`）であり、
`<Link to>` の静的検査は効かない（`to` が `string` 型のため）。代わりに、
**全ナビ項目の `to` が組み上がったルート木に解決すること**を単体テストで固定する。
これは型で捕まえられない穴を、型と同じ粒度（配線の誤り）で塞ぐためのものである。
`Link` への受け渡しに 1 箇所だけキャストが要る——その 1 箇所に本決定への参照を書く。

### 決定 6: ルートパスは計画書 §共通シェル の値へ是正する

[01_screens](../../planning/projects/microservices-platform/05_screens/01_screens.md) §共通シェル
「ルートパス（wireframe の URL バー準拠）」を正とし、`/ask` `/search` `/docs/$id` `/admin/*` `/analyze` へ
是正する。ルートは画面デザインより先に確定しており、#452（画面内容の準拠）を待つ理由がない。
対応表は[作業仕様書 §2](../specs/20260804_issue-490_spa-router-shell.md#2-ルートパス計画書-共通シェル-に合わせる)。

`home` 画面は計画の画面一覧に存在しないため削除し、`/` は SC-01（`/ask`。計画が「本システムの主入口」と
定義する画面）へリダイレクトする。

### 決定 7: 通知は「アイコン ＋ テキストラベル」を型で強制する

`sonner`（ADR-0031 の採用技術）を `foundation/ui/notifications.tsx` で包み、
`notify.success / info / warning / error` の 4 種のみを公開する。各種は固定のアイコンと
テキストのラベル（「成功」「情報」「注意」「エラー」）を必ず伴い、呼び出し側は省略できない。
[INDEX 決定 21](../../planning/projects/microservices-platform/INDEX.md)「色だけで意味を持たせない」を、
[IADR-0121](IADR-0121_spa-stack-migration-staging.md) 決定 4 の `StatusBadge` と同じ作法で API に落とす
（選択肢を作らなければ省略されない）。

## 理由

- **決定 1 が守っているもの**は IADR-0056 の 2 つの性質——「platform は可変ユニットを知らない」
  「ユニット追加は submodule 配置＋合成点 1 行」——である。ファイルベース（A / C）はどちらも壊す。
  型安全は目的であって、ファイルベースはその手段の一つにすぎない（ADR-0031 §理由 の書きぶりが
  そのことを示している）。
- **決定 2 の分離が要る理由**は、TanStack の型が**値から推論される**という一点にある。
  旧契約のルートは型情報を持たない（`path` が `string`）ため、型付きの配列に 1 つでも混ざると
  推論結果が `AnyRoute` へ落ち、**MSP 所有の 11 画面の型安全まで巻き添えで消える**。
  実行時にだけ足せば、失う型安全は AST の 3 画面分に限定できる。
- **決定 3・決定 4 はいずれも「静かに壊れる」型の失敗である。** 型が緩んでもコンパイルは通り、
  テストも通る。だから**負のプローブを実測して記録する**ことが決定そのものと同じだけ重要である。
- **決定 5 は型で解けない領域の線引き**である。ナビをデータで持つ限り遷移先は静的に検査できない。
  データで持つことをやめる（ナビをハードコードする）と存在秘匿のロール絞り込みとユニット独立性を失う。
  よって「型では無理」と認めたうえで、同じ誤りを実行時テストで捕まえる。

## 結果

- 良い影響:
  - ルート ID・パス・パスパラメータ・検索パラメータが `tsc` で検査される（実測。§実測）。
    `?q=` の欠落や `/docs/$id` のパラメータ忘れがコンパイルエラーになる。
  - `react-router-dom` が platform / knowledge から消え、SPA のルータが 1 本になる
    （IADR-0121 決定 1 の「系統ごとに 1 度だけ切り替える」の第 2 系統目が完了する）。
  - ユニット分離（IADR-0056 決定 3・4）が無傷で残る。AST は無改修で動き続ける。
- 悪い影響・トレードオフ:
  - **AST の 3 画面は型安全の外に残る**（`Link` の union に現れない・ID を `useSearch` へ渡せない）。
    これは AST を本リポから変更できない制約の写像であり、AST 側が新契約へ移るまで解消しない。
  - 画面コードにルート ID のリテラル（`/_shell/...`）が現れる（決定 3）。
  - 束ね役・合成点に**型注釈を書けない**（決定 1）。書くと型安全が静かに失われるため、
    その旨をコード内コメントとテストで固定する必要がある。
  - `@tanstack/router-core` を直接の devDependency として持つ（決定 4）。react-router の
    内部パッケージに依存する形であり、メジャー更新時に追随が要る。
- フォローアップ:
  - **AST 側への申し送り**: 新契約（型付き factory）へ移ると AST の 3 画面も型安全の中に入る。
    本リポからは変更できないため、AST リポジトリでの issue 起票が要る（判断は #490 の完了報告）。
    移行が済めば旧契約ブリッジ（決定 2）は削除できる。
  - Virtual File Routes（論点 A の C）は、AST が新契約へ移り**旧契約ブリッジが不要になった時点**で
    再評価する。それまでは C を採っても AST が生成木の外に残り、B に対する利得がない。
  - パンくず・権限バッジ・右レール AI チャットは #452 / 第 4 段。

## 実測

**測定条件**: worktree `feat/ADR-0031-spa-router-shell`（`origin/develop` `be3c71c` 基点）／
Node 22.22.2 ／ pnpm 10.33.0 ／ TypeScript 5.9.3 ／ `@tanstack/react-router` 1.170.18
（`@tanstack/router-core` 1.171.15 を解決）／`tsc -p platform/frontend/tsconfig.app.json --noEmit`。

### 型登録と型安全の成否（負のプローブ）

「負のプローブ」= **落ちるべきコード**。落ちなければ型安全が失われている。

| 構成 | `useSearch({ from: '存在しない ID' })` | 検索パラメータの型不一致 | `<Link to="/nope">` | パスパラメータの欠落 |
| --- | --- | --- | --- | --- |
| 型登録なし | 素通り | 素通り | 素通り | 素通り |
| `declare module '@tanstack/react-router'` | 素通り | 素通り | 素通り | 素通り |
| **`declare module '@tanstack/router-core'`（採用）** | **落ちる** | **落ちる** | **落ちる** | **落ちる** |

### ルート木の組み方と型安全

| 束ね方 | ルート ID の union | パスの union（`Link`） | 検索パラメータの型 |
| --- | --- | --- | --- |
| タプルをスプレッド（**採用**） | 保たれる | 保たれる | 保たれる |
| `features.flatMap((f) => f.createRoutes(shell))` | **失われる** | **失われる** | 保たれる |
| `createRoutes` の戻り値へ `readonly AnyRoute[]` を注釈 | **失われる** | **失われる** | **失われる** |
| 型付き配列へ `...legacyRoutes`（`AnyRoute[]`）をスプレッド | **失われる** | **失われる** | **失われる** |
| 型付き `addChildren` の後に `children` へ実行時追加（**採用**） | 保たれる | 保たれる | 保たれる |

### ルートオブジェクト経由のフック

| 参照方法 | 型 |
| --- | --- |
| `route.useSearch()` / `route.useParams()` | **`any`**（循環型参照） |
| `getRouteApi('/_shell/search').useSearch()` | **`any`**（同上） |
| `route.types.fullSearchSchema` | 厳密（`{ q: string }`） |
| **`useSearch({ from: '/_shell/search' })`（採用）** | **厳密** |

## 関連

- Supersedes: なし（[IADR-0121](IADR-0121_spa-stack-migration-staging.md) 決定 1 が予告した第 2 段の
  内部設計を具体化するものであり、既存の決定を覆さない）
- Superseded by: なし
