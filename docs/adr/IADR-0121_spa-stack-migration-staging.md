---
title: IADR-0121 SPA 新スタック移行の内部設計 — pnpm workspace / orval の配置と出口 / @platform/ui の切り出し単位 / SSE チャットの状態管理 / 段階分割
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0031, ADR-0032, IADR-0033, IADR-0034, IADR-0056, IADR-0116, IADR-0117, IADR-0119, IADR-0120, IADR-0124, IADR-0125]
author: Claude
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md"
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
  - "../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md"
related_specs:
  - ../specs/20260804_issue-446_spa-foundation-stack-migration.md
  - "../../feedback/20260804_frontend-migration-staging-interpretation.md"
---

# IADR-0121: SPA 新スタック移行の内部設計（段階分割・pnpm・orval・`@platform/ui`・SSE 状態管理）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-08-04
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID（FR/UC/SC/ADR）:
  [ADR-0031](../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md)（Accepted。
  React 19 + Vite + TanStack。**§結果 フォローアップが「共有 UI パッケージの切り出し単位を実装初期に確定する」を
  実装側へ申し送っている**）／
  [ADR-0032](../../planning/projects/microservices-platform/07_adr/ADR-0032_spa-auth-bff-session.md)（BFF セッション認証）／
  [13_frontend-stack](../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md)（fixed。
  §リスク・未決事項が「右レール AI チャットパネル（SSE）の状態管理パターンは実装時に検証する」を申し送っている）／
  [08_data-egress-policy](../../planning/projects/microservices-platform/06_technical/08_data-egress-policy.md)（自己ホスト）
- 関連する実装 ADR:
  [IADR-0033](IADR-0033_frontend-spa-foundation.md)（**本決定が Superseded にする**現行 SPA 基盤）／
  [IADR-0034](IADR-0034_frontend-coverage-gate.md)（カバレッジ ratchet）／
  [IADR-0056](IADR-0056_repo-unit-structure-platform-knowledge.md)（ユニット第一構成・依存規則 例外 2。
  本決定 4 が**フロントの許可先を 1 → 2 へ部分改定**する。先例は
  [IADR-0117](IADR-0117_platform-shared-kernel-placement.md) の backend 2 → 3）／
  [IADR-0116](IADR-0116_reimplementation-branching-and-pr-policy.md)（規約 4・5。決定 1 の根拠）／
  [IADR-0120](IADR-0120_excluded-units-from-gitmodules.md)（AST は別プロジェクト）
- 関連する実装仕様書:
  [20260804_issue-446](../specs/20260804_issue-446_spa-foundation-stack-migration.md)（本決定と対で読む）
- 関連 issue: #446（起点・親 #454）／#452（画面実装）／#439（BFF セッション認証）

## コンテキストと課題

計画は SPA スタックを React 19 + Vite + TanStack へ確定し、実装をそれに合わせる裁定
（planning#78・2026-07-30）が出ている。移行対象はパッケージマネージャ・フレームワーク・ルーティング・
状態管理・API 契約生成・CSS/UI・認証・テスト・CI の 9 系統に及ぶ。

計画は 2 点を**実装側の判断として明示的に申し送っている**。

- 共有 UI パッケージ（shadcn/ui ベース）の**切り出し単位**（ADR-0031 §結果 フォローアップ）
- 右レール AI チャットパネル（SSE）の**状態管理パターン**（13_frontend-stack §リスク・未決事項）

加えて、移行を成立させるために実装側で決めなければならない論点が 3 つある。

- **どの単位で PR を切るか**（IADR-0116 規約 4・5 との整合。9 系統を 1 PR に入れられない）
- **orval 生成物をどこへ置き、HTTP の出口をどこにするか**（BFF 境界を機械的に守れるか）
- **`oidc-client-ts` を今撤去するか**（計画は「不採用」。だが ADR-0032 のサーバ側は未実装）

## 検討した選択肢

### 論点 A: 移行の PR 単位

| | A1. 9 系統を 1 PR（計画文言の直読） | **A2. 5 段へ分割（採用）** | A3. 系統ごとに 9 PR |
| --- | --- | --- | --- |
| IADR-0116 規約 4 | 反する（巨大 PR） | 適合 | 適合 |
| IADR-0116 規約 5 | 反する（画面 13 枚を #446 で削除） | 適合 | 適合 |
| レビュー可能性 | 事実上不能 | 段ごとに成立 | 成立 |
| 旧新の並行運用 | 起きない | 起きない（各系統は 1 度だけ切替） | **起きる**（ルータだけ先に切ると画面が二重方言に） |
| 二重作業 | なし | なし（ルータ切替と画面再実装を同じ段に置く） | **あり**（画面を 2 回書く） |
| develop の健全性 | 一時に全部壊れる | 各段の後で常に green | green だが中間状態が長い |

### 論点 B: 共有 UI パッケージの切り出し単位

| | B1. 「共通で使えそうなもの」を全部入れる | **B2. トークン＋プリミティブのみ（採用）** | B3. 作らず各ユニットで複製 |
| --- | --- | --- | --- |
| 依存方向 | UI → ドメイン/通信の逆流が起きやすい | 一方向を保てる | — |
| 画面未確定時の判断コスト | 高い（何が共通かまだ分からない） | 低い（トークンと素部品は確定済み） | 低い |
| 重複 | 少 | 中（複合部品は当面重複） | 大 |
| ADR-0031 との整合 | 過剰 | 適合（「shadcn/ui ベース共有 UI・設定」） | 反する |

### 論点 C: orval 生成物の HTTP 出口

| | C1. orval 既定（生成コードが素の `fetch`） | **C2. mutator で `foundation/api` へ集約（採用）** | C3. 生成物を使わず手書き |
| --- | --- | --- | --- |
| 実行時 config（`bffBaseUrl`） | 効かない（`/bff/...` 固定） | 効く | 効く |
| 401 再ログイン導線 | 生成コードごとに素通り | 1 箇所で担保 | 実装者依存 |
| 「手書きクライアント禁止」 | 満たす | 満たす | **反する**（計画が明示的に禁止） |
| lint での機械強制 | 生成物が `fetch` を持つため規則が緩む | 生成物にも `fetch` が現れない＝規則を厳格化できる | — |

### 論点 D: SSE チャットの状態管理

| | D1. TanStack Query `experimental_streamedQuery` | **D2. 自前フック ＋ Query は確定済み履歴のみ（採用）** | D3. Zustand に全部持つ |
| --- | --- | --- | --- |
| API の安定性 | `experimental_` 接頭辞＝非安定 | 安定（自前） | 安定 |
| 現行の経路との適合 | `EventSource` / GET ストリーム前提。当リポは **Authorization 付き POST ＋ fetch ストリーム**（`apiStream`） | 適合（`apiStream` の上に載る） | 適合するが再発明 |
| キャッシュ・再取得 | 得られる | 確定済み履歴のみ Query が担う | 自前 |
| ADR-0031「サーバー状態は TanStack Query」 | 完全適合 | 概ね適合（ストリーム中の途中状態のみ外） | 反する |

## 決定

### 決定 1: 移行を 5 段へ分割し、第 1 段を #446 の範囲とする（論点 A = A2）

段の内容・順序・起票先・#452 / #439 との境界は
[作業仕様書 §段階分割](../specs/20260804_issue-446_spa-foundation-stack-migration.md#段階分割全体設計)を正とする。要点のみ。

- **第 1 段（#446）= 新スタックの土台**: pnpm workspace / React 19 / TanStack Query / Tailwind v4 ＋
  `@platform/ui` / orval / 機械強制 lint / CI の pnpm 化。**ルーティングと画面には触れない。**
- **第 2 段 = TanStack Router ＋ アプリシェル ＋ 旧 13 画面の削除・再実装**（#452 と同一 PR 群、または直前の独立 issue）。
  ルータ差し替えは画面の書き換えを伴い、その画面は #452 が作り直すため、**同じ段に置かないと同じ画面を 2 回書く**。
  また TanStack Router を採った理由である型安全（ルート・検索パラメータ）は、ルート定義を画面側で
  書き直して初めて得られる。実行時に `RouteObject[]` を連結する現行の合成点のままでは得られない。
- **第 3 段 = 認証**（#439 協調）、**第 4 段 = 画面機能の土台**（SSE チャット・Zustand・Table・ECharts）、
  **第 5 段 = 運用系**（Knip / Plop / Renovate / Husky）。

計画の「一括で移行する。段階分け・並行運用は行わない」は**旧新スタックの並行運用の禁止**として読む。
各系統は 1 度だけ切り替え、2 つのルータ・2 つの HTTP クライアント・2 つの CSS 体系が同時に存在する
状態を作らない。PR の分割は IADR-0116 規約 4 が要求する別事項である。

> **［2026-08-04］この解釈は利用者裁定により確定した。** 裁定原文:
> **「段階分けは認めます。最終的に一括になっていれば問題なし」**。したがって本決定の 5 段分割は
> 計画と整合する。ただし「最終的に一括」は**完了条件**を伴う——第 2〜5 段をすべて消化し、
> 13_frontend-stack §採用技術一覧と実装が完全に一致した時点で「一括移行の完了」とみなす
> （`react-router-dom` と `oidc-client-ts` がワークスペースから消えていることを含む）。
> 提起の経緯・確定解釈は
> [feedback/20260804_frontend-migration-staging-interpretation.md](../../feedback/20260804_frontend-migration-staging-interpretation.md)
> に記録した。
> **［2026-08-04 追記・反映済み］裁定は計画本文へ入った**——
> [13_frontend-stack §実装への移行方針](../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md)
> の追補（planning `d980a01` / planning#186）が「**この追補により移行完了の定義は確定した**」と明記した。
> **以後、移行完了の定義（完了条件）の正は計画本文であり、feedback 文書は経緯の記録である。**

> **［2026-08-04 追記］決定 1 の「第 2 段」は [[IADR-0124]]（#490）で 2 つへ分割された。**
> 本決定は段の内容・境界を
> [#446 作業仕様書 §段階分割](../specs/20260804_issue-446_spa-foundation-stack-migration.md#段階分割全体設計)
> を正とすると定め、その表は第 2 段を「TanStack Router 移行・共通シェル・旧 13 画面の削除・
> **shadcn/ui コンポーネント本移植・Lingui(ja/en)・Storybook**」としていた。
> **実際の起票（#490）は前 3 者に限定され、後 3 者（shadcn/ui 本移植・Lingui・Storybook）は
> 未起票の残件として繰り延べられた。** 理由は [[IADR-0116]] 規約 4——ルータ差し替え（合成点＝
> アーキテクチャの変更）と UI ライブラリ・i18n・カタログの導入を 1 PR に入れるとレビューが成立しない——である。
> 本決定 1 自体が起票先を「#452 と同一 PR **群**、または直前の独立 issue」と複数形で書いており、
> この分割は本決定の枠内にある。**第 2 段のうち #490 が消化した範囲の現行値は
> [IADR-0124](IADR-0124_tanstack-router-unit-composition.md) と
> [#490 作業仕様書](../specs/20260804_issue-490_spa-router-shell.md) を正とする。**
> 残件（shadcn/ui 本移植・Lingui・Storybook）は要起票であり、#454 のチェックリストへ追加する。
> **［2026-08-04・裁定］この #490 / #452 の分割は利用者の明示的裁定で承認された**（原文:
> **「最終的に結果が同じになるなら進め方はそれでもいいです」**）。**条件付き承認**であり、
> 条件（最終結果の同一性）が満たされるのは **#452 が旧 13 画面の削除・再実装を完了した時点**である
> （[feedback/20260804 §追加裁定](../../feedback/20260804_frontend-migration-staging-interpretation.md)）。
> **［2026-08-04 追記］これは計画本文でも確定した**——
> [13_frontend-stack §実装への移行方針](../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md)
> の追補が「**旧画面（13 画面）の完全削除は移行の完了条件の一部であり、段階分割によって省略されるものではない**」
> と明記している（planning#186）。
> **［2026-08-04 追記］残件（shadcn/ui 本移植・Lingui・Storybook）は #496 として起票・消化された**
> （[IADR-0125](IADR-0125_ui-primitives-i18n-catalog-and-storybook.md) /
> [#496 作業仕様書](../specs/20260804_issue-496_ui-i18n-storybook.md)）。
> **第 2 段の項目はこれで全て消化されたが、完了条件は #452 待ちのままである。**
> 段の順序・第 1 段／第 3〜5 段の内容・「各系統は 1 度だけ切り替える」という並行運用の禁止は
> 本 IADR が引き続き有効である（したがって状態は `Accepted` のまま）。

### 決定 2: パッケージマネージャは pnpm workspace とし、単一情報源を `src/` に置く

- `src/pnpm-workspace.yaml` に `'*/frontend'` と `'packages/*'` を列挙する。ユニットを submodule で
  追加したときに自動認識される性質（IADR-0056 決定 6）を npm workspaces から引き継ぐ。
- 版は `src/package.json` の `packageManager` で固定する（corepack / CI / ローカルの三者が同じ版を使う）。
  **Volta はローカル任意**、**CI は `pnpm/action-setup`**（13_frontend-stack §リスク・未決事項の指示どおり）。
- `src/package-lock.json` は削除し `src/pnpm-lock.yaml` をコミットする。lockfile は 1 リポジトリ 1 本を保つ
  （AST は別プロジェクトのため自前の lock を持つ。IADR-0120）。

### 決定 3: orval を導入し、生成物は「コミットする・`foundation/api` を出口にする・`/bff/*` だけ生成する」（論点 C = C2）

1. 設定は `src/orval.config.ts`、出力は
   `src/platform/frontend/src/foundation/api/generated/`（`mode: tags-split` / `client: react-query` /
   `httpClient: fetch` / `mock: true`）。**生成物はコミットする**（CI・IDE・レビューが codegen の実行順に
   依存しないため）。乖離は `pnpm run codegen` ＋ `git diff --exit-code` を CI に置いて検出する。
2. **HTTP の出口は `foundation/api/orvalMutator.ts` の `bffFetch` 1 箇所**に固定する。orval 既定の生成
   コードは素の `fetch('/bff/...')` を呼び、実行時 config（`bffBaseUrl`）も 401 再ログイン導線も無視する。
   mutator を挟むことで、既存の `apiClient`（IADR-0033 決定 5・6 の資産）をそのまま新スタックの土台に使える。
3. **`/bff/*` 以外は生成しない。** `docs/api/openapi.yaml` は BFF とサービス直接 API を 1 ファイルに束ねており、
   SPA が触れてよいのは `/bff/*` だけである。orval の `input.filters` はタグ／スキーマ単位でしか効かず、
   `/feedback` と `/bff/feedback` のように**同一タグに BFF と非 BFF が混在する**ため使えない（実測）。
   `input.override.transformer`（`src/orval-bff-only.cjs`）で `paths` を前処理して落とす。
   これにより **BFF 境界が生成器の段階で機械的に保証される**（「呼べる API が存在しない」）。
4. 生成物は **lint とカバレッジ計測から除外**する（自動生成物の品質は生成器の責務。母数へ入れると
   カバレッジ床が「生成量」で動いて意味を失う）。**typecheck は行う**——生成物と mutator・スキーマの
   不整合は型で気付きたいためである。

### 決定 4: `@platform/ui` の切り出し単位は「デザイントークン ＋ `cn()` ＋ shadcn/ui 派生プリミティブ」に限る（論点 B = B2。計画の申し送りへの回答）

- 置き場所は `src/packages/ui`、パッケージ名 `@platform/ui`。ユニット（`src/<unit>/{backend,frontend}`）では
  ないため `src/packages/` を「ユニットに属さない共有ワークスペースパッケージ」の置き場として新設する。
- **入れるもの**: Tailwind v4 の `@theme` トークンと base スタイル（`styles.css`）／`cn()`（clsx + tailwind-merge）／
  cva によるバリアントを持つ**プリミティブ**（Button・StatusBadge・以後 Input / Dialog / Table 等を第 2 段で追加）／
  アイコンの再エクスポート方針（`lucide-react`）。
- **入れないもの**: ドメイン語彙（ドキュメント・データソース等）／BFF 通信（生成フック・`apiFetch`）／
  ルーティング／認証・ロール判定／実行時 config。これらに触れた時点でそれは共有 UI ではなく feature である。
- **判定規則**（迷ったときの一本の線）: *「この部品は、このリポジトリの外の SPA へそのまま持って行っても
  意味が通るか」*。通るならプリミティブ（`@platform/ui`）、通らないなら feature 側に置く。
- **依存規則の改定**: [`src/README.md`](../../src/README.md) 依存規則 **例外 2**
  （[IADR-0056](IADR-0056_repo-unit-structure-platform-knowledge.md) 決定 3 の系）は
  「フロントエンドの可変ユニットは `@foundation` を参照してよい」と定めるが、本決定はこれを
  **`@foundation` と `@platform/ui` の 2 つ**へ広げる（`src/README.md` と IADR-0056 決定 3 の追記へ反映）。`@platform/ui` は
  ドメインも通信も持たないため、この拡張はユニットの切り出し可能性を損なわない。
  逆向き（`@platform/ui` → ユニット）の参照は禁止する。
- **アクセシビリティ規約の実装上の型**: 「色だけで意味を持たせない」（INDEX 決定 21）は口約束では守れないため、
  状態表示のプリミティブ `StatusBadge` を**アイコン ＋ テキストラベル必須**の API にして型で強制する。

> **［2026-08-04 追記］本決定の「以後 Input / Dialog / Table 等を第 2 段で追加する」という予告部分は
> [[IADR-0125]]（#496）が実値で埋めた（部分改定）。** 本決定は入れる／入れないの**判定規則**と
> 公開面 1 ファイル・依存規則の改定を定めたが、**どの部品を実際に移植するかは書いていなかった**。
> IADR-0125 決定 1 は「計画の明示・hi-fi モックアップの語彙・既存 11 画面の DOM 要素数の 3 情報源の
> 突き合わせで要求が示せるもの」に限るという基準を置き、**Input / Textarea / Select / Label /
> Table 一式 / Card / Alert / Tabs の 8 件**を移植した。
> **本決定が例示していた `Dialog` は移植していない**（IADR-0125 決定 2）——計画が確認ダイアログを
> 要求するのは SC-19 / SC-20 だけで、これは FR-19 / FR-20 に属し [[IADR-0119]] 決定 1 が
> 「その受け入れを担う画面」ごと着手を保留しているためである（繰り延べであって放棄ではない。
> 引き受け先は #452）。
> **［2026-08-07 追記 / #599］この根拠は SC-20 について失効した** —— 計画は `ADR-0037` の着手可否の注記で
> **SC-20（Obsidian 連携設定）の全体は覆らない**と確定させ、FR-19 / FR-20 の保留は
> **SC-19 の「本文を編集（Wiki.js）」導線ただ 1 つ**に絞られた（[[IADR-0142]]）。**移植の再判断は引き受け先の #452 に委ねる。**
> また IADR-0125 決定 1 は本決定の「入れないもの」へ **表示文言**を加えた——プリミティブが既定文言を
> 持つと i18n の入口が 2 つに割れ、カタログの網羅検査（IADR-0125 決定 4）が抜けるためである。
> 本決定の骨格（判定規則・公開面 1 ファイル・依存規則 例外 2 の改定）は有効なため `Accepted` を維持する。
> **`@platform/ui` の収録物の現行値は [IADR-0125](IADR-0125_ui-primitives-i18n-catalog-and-storybook.md) と
> [`src/packages/ui/README.md`](../../src/packages/ui/README.md) を正とする。**

### 決定 5: 右レール AI チャット（SSE）は「自前フック ＋ TanStack Query は確定済み履歴のみ」とする（論点 D = D2。計画の申し送りへの回答）

- ストリーミング中の途中状態（受信中のトークン列・中断・エラー）は、既存の
  `foundation/api/apiStream`（fetch ＋ `ReadableStream` ＋ SSE パーサ。Authorization 付き POST に対応）の
  上に置く**自前フック**が持つ。
- **確定した会話履歴・フィードバック・引用元などのサーバー状態は TanStack Query が持つ**
  （ADR-0031「サーバー状態は TanStack Query に統一」に従う）。ストリーム完了時に該当キーを
  `setQueryData` / `invalidateQueries` して Query 側へ確定値を引き渡す。
- **`experimental_streamedQuery` を採らない理由**: (1) API が非安定（名前に `experimental_`）、
  (2) 当リポの SSE は `EventSource` ではなく **Authorization 付き POST ＋ fetch ストリーム**であり
  （IADR-0037・SC-01）、streamedQuery の想定する取得経路と噛み合わない。
- **再評価条件**（この決定を見直す唯一のトリガ）: `streamedQuery` が experimental を外し、かつ
  任意の非同期イテレータ（＝当リポの `apiStream`）を素直に受けられるようになったとき。第 4 段の着手時に確認する。
- 実装は第 4 段。本決定はパターンの確定のみを行う（計画が「実装初期に確定する」と申し送った事項のため）。

> **［2026-08-05 追記・適用範囲の明確化（#502 / [IADR-0126](IADR-0126_sse-answer-state-and-search-url-state.md)）］**
> 本決定は**右レール AI チャットパネル（第 4 段）**を対象とする。**SC-01 の本文（メイン領域）の回答**は
> [IADR-0126](IADR-0126_sse-answer-state-and-search-url-state.md) 決定 1 が別に定める。
> 両者は「自前フックが途中経過を持つ」点では同じだが、**キャッシュの扱いが割れている**——
> 本決定は「ストリーム完了時に `setQueryData` / `invalidateQueries` して Query 側へ確定値を引き渡す」、
> IADR-0126 決定 1 は「回答を Query のキャッシュに載せない」である。
> **適用面が異なるため矛盾は生じていない**（本決定は右レール、IADR-0126 は SC-01 本文）。
> 統一するか否かは、実際に右レールを作る**第 4 段で本決定の改定として判断する**
> （IADR-0126 §フォローアップ 1 に未解決事項として記録済み）。
> **本決定は `Accepted` のまま有効であり、IADR-0126 はこれを置換しない。**
> なお、計画（13_frontend-stack §リスク・未決事項）の「SSE の状態管理パターン」への回答は
> **本決定であって IADR-0126 ではない**（IADR-0126 は同じ規則を SC-01 本文へ適用した記録である）。

### 決定 6: `oidc-client-ts` は第 1 段で撤去せず、第 3 段（#439）で撤去する

計画（13_frontend-stack）は `oidc-client-ts` を「★不採用」とするが、その根拠は ADR-0032 の
BFF セッション方式である。**BFF 側のセッション・ログイン・ログアウト経路は未実装**（#439 未着手）であり、
先に撤去すると SPA がログインできなくなる。第 1 段は現行の OIDC(PKCE) 経路を**そのまま温存**し、
第 3 段で `foundation/auth` ごと差し替える（計画も「認証方式の移行と同一の移行作業として扱う。
`foundation/auth/` を二度書き換えないため」と述べており、この順序は計画の趣旨に沿う）。

### 決定 7: [IADR-0033](IADR-0033_frontend-spa-foundation.md) を Superseded とする

ADR-0031 の追補（2026-07-30 裁定）が「実装リポジトリ側で `IADR-0033` の Superseded 化と後継 IADR の
起票が必要である」と申し送っている。IADR-0033 の 7 つの決定のうち、技術スタック（決定 1）・
配置（決定 2）・認証（決定 4）は本決定と第 2〜3 段が置き換える。**存在秘匿・エラー方針（決定 6）と
BFF 境界（決定 5）は思想として引き継ぐ**（本決定 3 の mutator 集約がその実装上の担保である）。
IADR-0033 に `Superseded by IADR-0121` を追記する。

### 決定 8: 機械強制は ESLint で行い、`foundation/api` と生成物以外に HTTP の出口を作らせない

Redux 系 import の禁止・`axios` 等の HTTP クライアント import の禁止・`foundation/api` 外での
`fetch` / `XMLHttpRequest` / `EventSource` 使用の禁止・`@platform/ui` の深い参照の禁止を
`src/eslint.config.js` に置く。専用スクリプトを新設しないのは、対象が **import と識別子の静的検査**で
あり ESLint の守備範囲そのものだからである（検査器を増やすほど、走らせ忘れと二重メンテが増える）。

## 理由

- **決定 1 が守っているもの**は「同じ画面を 2 回書かない」ことと「develop がいつでも green」であること。
  ルータ差し替えを画面再実装から切り離すと、13 画面 ＋ 13 テストを移行用に書き換え、数週後に #452 が
  同じファイルを捨てる。これは CLAUDE.md が禁じる「計画外の大規模リファクタ」に実質的に当たる。
- **決定 3 の 3 点目（`/bff/*` だけ生成）が効く理由**は、境界違反を「規約」から「不可能」へ変えるからである。
  生成物にサービス直叩きの関数が存在しなければ、レビューでの見落としが起きようがない。
- **決定 4 の判定規則**（リポジトリ外へ持って行って意味が通るか）は、shadcn/ui の「コピーして所有する」
  思想と噛み合う。所有するのはトークンと素部品であり、ドメインの語彙を持ち込むと 2 ユニットで
  共有できなくなる。
- **決定 5 は「安定した自前」と「非安定な標準」の比較**である。SSE は当リポでは既に `apiStream` として
  テスト付きで動いており（`sse.test.ts`）、これを捨てて experimental API へ賭ける理由が無い。
  再評価条件を明記することで、この決定が惰性で残り続けるのを防ぐ。
- **決定 6 は順序の問題**であり、計画との対立ではない。計画自身が認証移行を「同一の移行作業」と位置づけている。

## 結果

- 良い影響:
  - 9 系統の移行が、各段で develop を green に保ったままレビュー可能な単位へ分解された。
  - BFF 境界・Redux 不使用・手書きクライアント禁止が、規約から**機械検査**と**生成器の入力制限**へ移った。
  - 計画が実装側へ申し送った 2 つの未決事項（共有 UI の切り出し単位・SSE の状態管理）に決定が付いた。
  - pnpm の厳密解決により phantom dependency が露見し、ユニットの単独ビルド可能性が上がる。
- 悪い影響・トレードオフ:
  - 第 1 段の時点では `react-router-dom` と TanStack Query が同居する（ルータは旧・サーバー状態は新）。
    これは「並行運用」ではなく**系統ごとの切替時期のずれ**であり、第 2 段で解消する。
  - orval 生成物をコミットするためリポジトリのファイル数が増え、OpenAPI 更新時に生成物の差分が
    PR に現れる（レビュー時にノイズになる）。代わりに CI と IDE が codegen 実行順に依存しなくなる。
  - `@platform/ui` を薄く保つため、第 2 段までは複合コンポーネントがユニット側に重複し得る。
  - `src/packages/` はユニットでないディレクトリを `src/` 直下に増やす（IADR-0056 の構成図に追記が要る）。
- フォローアップ:
  - **［消化済み・2026-08-04］[IADR-0056](IADR-0056_repo-unit-structure-platform-knowledge.md) への相互追記。**
    本決定は IADR-0056 の決定 3（フロントの参照可能な共有物 1 → 2）と決定 4（npm workspaces → pnpm
    workspace）を部分改定する。先例 [IADR-0117](IADR-0117_platform-shared-kernel-placement.md) と
    同形式で、IADR-0056 の該当決定の直後へ日付付き［追記］を入れ、§関連 の「Superseded by」欄にも
    部分改定 2 件として記載した（被改定側から改定側を辿れる状態を保つため）。
  - 第 2 段（TanStack Router ＋ アプリシェル ＋ 旧画面削除）の issue 起票と #454 チェックリストへの追加。
  - 第 3 段で本決定 6 の撤去を実行する（#439 のマージが条件）。
  - 第 4 段の着手時に決定 5 の再評価条件を確認する。
  - Vite 6 → 7/8・Vitest 3 → 4・TypeScript 5.6 → 7 系の追随は別 issue（第 1 段では依存レビューの
    high/critical advisory を外すのに必要な最小限だけ上げた。Vite 6.4.3 / Vitest 3.2.7）。

## 関連

- Supersedes: [IADR-0033](IADR-0033_frontend-spa-foundation.md)（決定 1・2・4 を置換。決定 5・6 は思想を継承）
- Superseded by: なし。ただし**部分改定が 2 件ある**（いずれも骨格は有効なため本 IADR は `Accepted` を維持し、
  該当決定の直後へ日付付き［追記］を入れた）。
  1. [IADR-0124](IADR-0124_tanstack-router-unit-composition.md): §決定 1 の「第 2 段」を
     ルータ／シェル／旧画面（#490）と残り（shadcn/ui 本移植・Lingui・Storybook）へ分割（#490）
  2. [IADR-0125](IADR-0125_ui-primitives-i18n-catalog-and-storybook.md): §決定 4 の「以後 Input /
     Dialog / Table 等を第 2 段で追加する」を実値（移植 8 件・Dialog は繰り延べ）で確定し、
     「入れないもの」へ表示文言を追加（#496）
  3. [IADR-0126](IADR-0126_sse-answer-state-and-search-url-state.md): §決定 5 の**適用範囲**を
     「右レール AI チャット（第 4 段）」に限ることを明確化した（SC-01 本文の回答は IADR-0126 決定 1 が持つ）。
     **決定の内容は変えていない。**キャッシュ方針の差（Query へ引き渡す／載せない）は第 4 段で
     本決定の改定として判断する（#502）
