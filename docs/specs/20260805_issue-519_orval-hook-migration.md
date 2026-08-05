---
title: 画面の通信を orval 生成物へ載せ替える
type: spec
status: in-progress
related_ids: [SC-01, SC-02, SC-03, SC-05, SC-06, SC-07, SC-08, SC-09, SC-10, SC-11, UC-01, UC-02, UC-03, UC-04, UC-05, UC-06, FR-01, FR-03, FR-04, FR-06, FR-08, FR-09, FR-10, FR-12, FR-15, NFR, ADR-0031, IADR-0009, IADR-0040, IADR-0121, IADR-0122, IADR-0126, IADR-0127, IADR-0129, IADR-0131, IADR-0132, IADR-0135]
author: Claude
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/03_usecases/01_usecases.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
related_specs:
  - ../api/BFF_bff-surface.md
  - ../adr/IADR-0135_generated-client-adoption-and-cache-keys.md
  - ../adr/IADR-0131_openapi-as-bff-contract-source.md
  - ../adr/IADR-0132_openapi-required-from-csharp-nullability.md
  - ../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md
  - ../adr/IADR-0129_sc09-11-admin-ops-screen-composition.md
  - ./20260805_issue-506_openapi-bff-groups.md
  - ./20260805_issue-520_openapi-response-required.md
---

# 仕様書: 画面の通信を orval 生成物へ載せ替える（#519）

> 本仕様書は実装着手前に作成した。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: **SC-03**（文書詳細）・**SC-05**（文書管理）・**SC-06**（データソース管理）・
  **SC-07**（変換ジョブ）・**SC-09**（管理者設定〔ABAC〕）・**SC-10**（運用ダッシュボード）・
  **SC-11**（構成ビューア）——issue の件名が挙げる 7 画面。
  加えて **SC-01**（フィードバック送信のみ）・**SC-02**（検索）が母集合に入る（§着手時の実測 2）。
- ユースケース（UC）: UC-01 / UC-02 / UC-03 / UC-04 / UC-05 / UC-06
- 機能要求（FR）: FR-01 / FR-03 / FR-04 / FR-06 / FR-08 / FR-09 / FR-10 / FR-12 / FR-15
- **NFR**（保守性）: 契約変更が型検査で捕まること。**本作業の主たる価値はこれである。**
- 関連 ADR（計画）:
  [ADR-0031](../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md)（Accepted。
  BFF 境界と生成クライアント）
- 関連 IADR: **[[IADR-0135]]（本作業の内部設計判断。本書と対で読む）**・[[IADR-0131]]（OpenAPI を
  BFF 契約の単一情報源とする。**本作業は決定 3 を改定する**。§設計 6）・[[IADR-0132]]（`required` は
  C# の非 null 性から／`?? 既定値` は残す）・[[IADR-0121]] 決定 3（BFF 境界）・[[IADR-0126]]（SSE と
  検索の URL 状態）・[[IADR-0127]]（決定 3 = 載せ替え前の暫定／決定 5 = invalidate だけ／
  決定 7 = 直近の操作結果）・[[IADR-0129]]（403・404 の中立化と 5xx の区別）・[[IADR-0009]]（存在秘匿）・
  [[IADR-0040]]（ABAC 管理 API の透過中継と 409 の Problem 詳細）
- 本リポジトリの起点: **#519**（親 #454。#506 の分割 2 本目。#520 は先に消化済み）

## 目的・背景

[作業仕様書 #506](./20260805_issue-506_openapi-bff-groups.md) が契約（`docs/api/openapi.yaml`）を
BFF の全面について揃え、[#520](./20260805_issue-520_openapi-response-required.md) が応答スキーマを
`required` で必須化した。**しかし画面はまだ `apiFetch` ＋ 手書き型で呼んでいる**ため、
「OpenAPI を変えると型検査が落ちる」という [[IADR-0131]] 決定 1 の保証は **SC-08 の 1 画面にしか掛かっていない**
（#520 の変異試験 M6 / M7 が実測。**載せ替え待ちで素通りした**）。

本作業はその載せ替えを行い、**契約と画面を型でつなぐ**。

## 着手時の実測（#506 の申し送りを鵜呑みにしない）

### 実測 1: 母集合は 10 ファイル・`apiFetch` は 27 呼び出し

```console
$ ls src/knowledge/frontend/src/features/sc*/use*.ts | wc -l
10
$ grep -l 'apiFetch' src/knowledge/frontend/src/features/sc*/use*.ts | wc -l
9
$ grep -c 'apiFetch(\|apiFetch<' src/knowledge/frontend/src/features/sc*/use*.ts
… 合計 27
```

| # | 画面 | ファイル | `apiFetch` | 判定 |
| --- | --- | --- | --- | --- |
| 1 | SC-01 | `sc01-search/useAskStream.ts` | 1（`/feedback`） | **対象**（`apiStream` は恒久的に対象外） |
| 2 | SC-02 | `sc02-results/useSearchQuery.ts` | 1 | **対象** |
| 3 | SC-03 | `sc03-document/useDocumentQueries.ts` | 3 | **対象** |
| 4 | SC-05 | `sc05-documents/useDocumentAdmin.ts` | 5 | **対象** |
| 5 | SC-06 | `sc06-datasources/useDataSources.ts` | 4 | **対象** |
| 6 | SC-07 | `sc07-conversions/useConversionJobs.ts` | 2 | **対象** |
| 7 | SC-08 | `sc08-analysis/useAnalysisTask.ts` | 0 | **対象外**（#506 で載せ替え済み。ただし §設計 6 の改名で import 1 行を追随する） |
| 8 | SC-09 | `sc09-admin-abac/useAbacAdmin.ts` | 7 | **対象** |
| 9 | SC-10 | `sc10-operations/useDashboardSummary.ts` | 1 | **対象** |
| 10 | SC-11 | `sc11-config/useConfigViewer.ts` | 3 | **対象** |

**対象は 9 ファイル**（10 − SC-08）であり、#506 §残りとして何をどうするか の 9 と一致する。

### 実測 2: issue の件名が挙げる ID（7 画面）と母集合（9 ファイル）が食い違う

issue の件名は `SC-03,SC-05,SC-06,SC-07,SC-09,SC-10,SC-11` の **7 画面**だが、本文は **9 ファイル**を
対象と書く。差は **SC-01（フィードバック送信）と SC-02（検索）**である。

**9 ファイルを採る。** 理由は issue の受け入れ基準そのものにある——
**「#520 の M7（`SearchResponse.totalHits` を削除）を再実行して落ちることを確かめる」**と定めており、
`SearchResponse` を読むのは **SC-02** である。SC-02 を載せ替えなければ M7 は素通りしたままで、
受け入れ基準を満たせない。SC-01 のフィードバック送信も #506 §残り の表の 1 行目である。

**コミットの起点 ID には SC-01 / SC-02 も併記する**（件名の ID 列だけに揃えると、
SC-01 / SC-02 の変更が起点 ID を持たない変更になる）。

### 実測 3: `useBffSearch` は **mutation** である（#506 の申し送りの誤り）

#506 §残り の表は SC-02 の載せ替え先として `useBffSearch` を挙げるが、`/bff/search` は **POST** であり、
orval は POST を `useMutation` として生成する。

```console
$ grep -n 'export const useBffSearch' src/platform/frontend/src/foundation/api/generated/search/search.ts
118:export const useBffSearch = <TError = unknown, …>(…): UseMutationResult<…>
```

**`useMutation` は `useQuery` の代わりにならない**——キャッシュに載らず、マウント時に自動で走らず、
検索語をキーにした再訪の即時表示（[[IADR-0126]] 決定 3・4 が明示した性質）が失われる。
そのまま従うと**画面の挙動が変わる**。対処は §設計 2。

### 実測 4: テストのモック層が `apiFetch` に当たっている（#506 の落とし穴 5 点に無い）

```console
$ grep -ln 'apiFetch' src/knowledge/frontend/src/features/**/*.test.tsx src/knowledge/frontend/src/features/*.test.tsx | wc -l
13
```

生成コードは `bffFetch`（mutator）→ **`apiRequest`** を通るため、**`apiFetch` を差し替えても
生成コードの経路には効かない**。載せ替えると 13 のテストファイルが一斉に赤くなる。
**これは #506 の「共通の注意」5 点に挙がっていない**——本作業で最も手数の掛かる部分である。

先例は既にある: SC-08 のテスト（`AnalysisDashboardPage.test.tsx:10-17`）が
「モックは `apiRequest` に当てる」と明記して同じことをしている。**その作法へ揃える**（§設計 4）。

### 実測 5: `useConfigViewer` の前方一致は生成キーでは成立しない（#506 の予告どおり）

生成されるキーは URL 文字列 1 要素である。

```console
$ grep -A3 'getBffConfigEffectiveQueryKey = ' src/platform/frontend/src/foundation/api/generated/config/config.ts
    return [ `/bff/admin/config` ] as const;
$ grep -A3 'getBffConfigDriftQueryKey = ' …
    return [ `/bff/admin/config/drift` ] as const;
```

TanStack Query の部分一致は**配列の要素単位**なので、`['/bff/admin/config']` は
`['/bff/admin/config/drift']` に**当たらない**（文字列の前方一致では照合しない）。
現行の `['bff','admin','config']` が持っていた「1 回の無効化で 3 本」は失われる。対処は §設計 3。

**一方 SC-07 は成立する**——`getBffConversionJobListQueryKey()` は `['/bff/conversion/jobs']`、
絞り込みつきは `['/bff/conversion/jobs', { status }]` であり、**前者は後者の前方一致になる**
（要素 0 が一致し、キーが長い側が該当する）。SC-07 の「条件を問わず束ごと無効化」はそのまま保てる。

### 実測 6: 生成物の応答は `{ data, status, headers }` に包まれる（#506 の落とし穴 1）

`orvalMutator.ts` の `OrvalResponse` である。**`data` の中身は成功／失敗の union** で、
成功枝は `{ data: T; status: 200 }`、失敗枝は `{ data: void; status: 404 }` 等になる。
非 2xx は `apiRequest` が `ApiError` を投げるため**失敗枝は実行時に解決しない**が、型の上には残る。

## 対象範囲

### 対象

1. **9 ファイルの通信層を生成物へ載せ替える**（§設計 1〜4）。手書きの DTO 型（interface）を削除し、
   生成型（`bff.schemas.ts`）へ置き換える。
2. **キャッシュキーを生成キーへ差し替え、無効化の対象を漏れなく直す**（§設計 3）。
3. **13 のテストファイルのモック層を `apiRequest` へ移す**（§設計 4）。**アサーションの意図は変えない。**
4. **コード内コメントの誤りを是正する**——`useDashboardSummary.ts:10` と `useConfigViewer.ts:9` が
   「`docs/api/openapi.yaml` に無く」と書くが**在る**（#506 §実測 4 が指摘。#506 §未決事項 6 が本作業へ送った）。
5. **既存 2 本の `operationId` を規約へ揃える**（`analysis-ask` / `analysis-analyze` →
   `bff-analysis-ask` / `bff-analysis-analyze`。#506 §未決事項 4）。**[[IADR-0131]] 決定 3 の改定である**（§設計 6）。
6. **`DataSourceBffEndpoints.cs:80` のコメントを実体へ揃える**（#506 §未決事項 5）。
7. `pnpm run codegen` の生成物を更新しコミットする。
8. **変異試験**で「BFF の DTO（＝ OpenAPI）を変えると型検査が落ちる」ことを実測する。
   **#520 の M6 / M7 を再実行する。**
9. 波及する文書を追随させる（通信仕様書・画面仕様書の「通信」節・[[IADR-0127]] 決定 3 の追記）。

### 対象外（送り先を明記する）

| 事項 | 送り先 | 理由 |
| --- | --- | --- |
| `sc01-search/useAskStream.ts` の **`apiStream`（SSE）** | **やらない（恒久）** | orval は SSE を扱えない（[[IADR-0131]] 決定 4）。生成物に該当の関数が存在しない |
| `sc08-analysis/useAnalysisTask.ts` の作り | 対象外 | 既に生成フックに載っている。改名の追随（import 1 行）だけ行う |
| **C# → OpenAPI の追随の機械化** | [[IADR-0131]] フォローアップ 2 | 本 issue の射程外。保証の上限は「OpenAPI を変えると落ちる」まで |
| `/bff/feedback` の端点認可 | **#521** | 認可の変更は挙動の変更 |
| `useBffDocumentGet` / `useBffConversionJobGet` / `useBffFeedbackStats` など**画面が使わない生成物** | 使わない | 呼ぶ画面が無い。使わない関数を呼ぶ層は作らない |
| MSW（`*.msw.ts`）への fixture 移行 | **§未決事項 1** | 既存テストの fixture を一斉に置き換える別種の作業。挙動の固定（本作業の受け入れ基準）と混ぜない |

## 設計

判断の根拠（選択肢の比較・棄却理由）は [[IADR-0135]] を正とする。本節は**何をどう書くか**を確定する。

### 1. 封筒（`{ data, status, headers }`）の剥がし方

**`select` で剥がす**。`@foundation/api/orvalSelect.ts`（新設）に 1 つだけ道具を置く。

```ts
export type OkPayload<R> = Extract<R, { status: 200 }>['data'];
export const okData = <R extends { status: number; data: unknown }>(res: R): OkPayload<R> => …;
```

- **型は生成物から導出する**（`Extract<…, { status: 200 }>`）。呼び出し側で型引数を書かないため、
  契約側で応答型が変わればそのまま追随する（＝**網が張られる**）。
- `select` を使うので、画面が読む `query.data` は**載せ替え前と同じ形**（本文そのもの）になる。
  **画面側の読み出しコードを書き換えない**で済む＝挙動の差分が入り込む余地が小さい。
- 既存の `?? []` は**残す**（[[IADR-0132]] 決定 3。§設計 5）。

### 2. SC-02（検索）は生成された**操作関数**を `useQuery` に据える

`/bff/search` は POST なので生成されるのは mutation である（§実測 3）。
**生成関数 `bffSearch` を `useQuery` の `queryFn` に据える**——フックは使わないが、
**URL・要求型・応答型はすべて生成物由来**であり、手書き HTTP クライアントでもない
（`bffSearch` → `bffFetch` → `apiRequest` の一本道）。

- キャッシュキーは現行の `['bff','search', q]` を**維持する**（生成キーが存在しないため。
  [[IADR-0126]] 決定 3 の「検索語がキー」という性質をそのまま保つ）。
- `useQuery` が渡す `signal` を `bffSearch` へ渡す（生成フックと同じ扱い）。

### 3. キャッシュキーと無効化（**落とし穴 3。ここだけ設計判断が要る**）

| 画面 | 一覧のキー | 無効化 |
| --- | --- | --- |
| SC-05 | `getBffDocumentListQueryKey()` | 5 つの更新系すべての成功後に 1 本 |
| SC-06 | `getBffDataSourceListQueryKey()` | 3 つの更新系すべての成功後に 1 本 |
| SC-07 | `getBffConversionJobListQueryKey(params)` | 再変換の成功後に **`getBffConversionJobListQueryKey()`（引数なし）** ＝ 全条件に前方一致（§実測 5） |
| SC-09 | `getBffAuthzListAttributesQueryKey()` / `getBffAuthzListPoliciesQueryKey()` | 属性系・ポリシー系で別々に 1 本ずつ（現行と同じ） |
| SC-11 | 3 本とも生成キー | **3 本を明示的に無効化する**（前方一致が成立しないため） |

**SC-11 の「1 回の再取得で 3 本」は挙動として維持する。** 既存テスト
（`ConfigViewerPage.test.tsx` の「再取得で 3 本飛ぶ」）が**改修なしで通り続ける**ことで固定する
（モック層の移行を除く）。棄却案は [[IADR-0135]] 決定 3。

### 4. テストのモック層（**アサーションの意図を変えない**）

| 変えるもの | 変えないもの |
| --- | --- |
| `vi.mock` の差し替え先: `apiFetch` → **`apiRequest`** | 期待する DOM・文言・role |
| 応答の作り方: 素の JSON → **`Response` 相当**（`{ status, text(), headers }`） | 失敗の作り方（`ApiError` を throw する形は同じ） |
| 呼び出しの検証: `('/x', { json })` → `('/x', objectContaining({ method, body }))` | **どのパスが何回呼ばれたか**という検証の意図 |

**パス文字列は変わらない**——`bffFetch` が `/bff` 接頭辞を外して `apiRequest` へ渡すため、
`'/admin/config'`・`'/documents'` のような既存の期待値がそのまま使える。

共通の道具（`jsonResponse` / `noContent`）は
`@foundation/testing/bffResponse.ts`（新設）へ置く——13 ファイルへ同じ 3 行を写すのを避ける。

### 5. `?? 既定値` は消さない（[[IADR-0132]] 決定 3 の踏襲）

生成型は #520 で必須化済みなので、`?? []` は「型の上では常に左辺」になる。**それでも残す。**
「契約上は必須」と「実行時に必ず来る」は別であり、応答本文を実行時に検証する層は無い。
**本作業ではむしろ根拠が強まる**——`bffFetch` は本文が空のとき `{}` を返す（`orvalMutator.ts:26`）ため、
`data` が配列でない値になる経路が**型の外に実在する**。

| ファイル | 式 | 判断 |
| --- | --- | --- |
| `sc03-document/useDocumentQueries.ts` | 版履歴の `?? []` | **残す** |
| `sc09-admin-abac/useAbacAdmin.ts` | 属性・ポリシー一覧の `?? []` | **残す** |
| `sc11-config/useConfigViewer.ts` | 履歴の `?? []` | **残す** |
| `sc02-results/SearchResultsPage.tsx` | `search.data?.totalHits ?? results.length` | **残す**（`data` は取得前 `undefined`。契約とは無関係） |
| 各ページの `data ?? []` | | **残す**（同上） |

**新たに `??` を足すことはしない。**

### 6. `operationId` の統一（**[[IADR-0131]] 決定 3 の改定**）

[[IADR-0131]] 決定 3 は「既存 2 本（`analysis-ask` / `analysis-analyze`）は改名しない——
`useAnalysisAnalyze` の改名が SC-08 へ波及する」と書いた。**本作業でこれを改定する。**

- 改定の根拠: 同 PR の[作業仕様書 #506](./20260805_issue-506_openapi-bff-groups.md) §未決事項 4 が
  **「2 本目で `useAnalysisAnalyze` に触るついでが最も安い」と本作業へ送っている**。
  issue #519 の本文も明示的に要求している。
- 波及の実測: SC-08 の `useAnalysisTask.ts` の **import 1 行 ＋ 呼び出し 1 行**、および生成物。
  `useAnalysisAsk`（`/bff/analysis/ask`）は**どの画面も呼んでいない**。
- 改定後: `operationId` は**例外なく** C# の `WithName` のケバブケースになる（規約の穴が閉じる）。

[[IADR-0131]] 本体には**日付つき［追記］**で改定を記録する（決定を消さない）。

### 7. C# のコメント是正（#506 §未決事項 5）

`src/knowledge/backend/…/DataSourceBffEndpoints.cs:80` は同期の同期応答を `{ fetchId, status }` と
書くが、後段の実体は `{ fetched, failed, connectorAvailable, message }` である
（`DataSourceEndpoints.cs:61-66`。OpenAPI の `DataSourceSyncResultDto` は実体側で正しい）。
**コメントだけを直す**（挙動・契約は変えない）。

## 受け入れ基準

- [ ] **9 ファイルすべてが `foundation/api/generated` 由来の型／関数を使っている**
      （`grep 'apiFetch' src/knowledge/frontend/src/features/sc*/use*.ts` が SC-01 の SSE 周辺を除いて 0 件）。
- [ ] **BFF の DTO を変えると型検査が落ちる**——**変異試験で実測する**。
      **#520 の M6（`DriftFindingDto.detail` 削除）／ M7（`SearchResponse.totalHits` 削除）を再実行し、
      今度は落ちること**を確かめる。**素通りしたものは隠さず理由を書く。**
- [ ] **画面の挙動が変わっていない**——次が通り続ける（モック層の移行を除きアサーション不変）:
      存在秘匿の markup 一致（`sc11-config/access.test.tsx`）／**403・404 の中立化と 5xx の
      `role="alert"` の区別**（[[IADR-0129]] 決定 3。画面内の全クエリに適用）／**SC-09 の 409 の
      Problem 詳細**（[[IADR-0040]] 決定 2）／**`beginOperation()` による直近の操作結果の表示**
      （[[IADR-0127]] 決定 7）／**SC-11 の「1 回の再取得で 3 本」**。
- [ ] **コメントの誤りが是正されている**（「openapi.yaml に無く」が 0 件）。
- [ ] **`operationId` が全 38 操作で `WithName` のケバブケースと一致する。**
- [ ] `pnpm run codegen` の後に
      `git diff --exit-code -- src/platform/frontend/src/foundation/api/generated` が差分なし。
- [ ] `typecheck` / `lint`（errors を増やさない）/ `test` / `test:coverage`（**床を割らない**）/
      `build` / E2E が green。
- [ ] リポジトリの機械検査が green（`check-doc-links` / `check-commit-messages` / `check-contract-schema` /
      `check-test-traceability` / `check-test-spec-coverage` / `check-unit-dependencies` /
      `check-i18n-catalogs` / `check-bff-downstreams` / `check-static-egress` / `scripts.test.js`）。

## テスト方針

**本作業は「通信層の実装を差し替えても画面の挙動が変わらない」ことを示す作業である。**
したがって**新しい振る舞いのテストは足さない**——既存テストが（モック層の移行を除いて）
**アサーション不変で通り続ける**ことが担保である。

| 手段 | 見るもの |
| --- | --- |
| 既存の画面テスト（13 ファイル） | 画面の挙動が変わっていないこと |
| **変異試験** | 「契約を壊すと型検査が落ちる」——**本作業の主目的そのもの** |
| 生成物の再生成差分検査（CI） | OpenAPI と生成物の乖離 |
| `orvalSelect` の単体テスト | 封筒剥がしの規約（成功枝だけを取る）を固定する |

## 検証（実測）

（実装後に記入する）

## 未決事項・親への申し送り

（実装後に記入する）
