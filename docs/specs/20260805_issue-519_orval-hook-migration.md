---
title: 画面の通信を orval 生成物へ載せ替える
type: spec
status: completed
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
| `sc02-results/useSearchQuery.ts` | 旧 `(await apiFetch(...)) ?? EMPTY` | **唯一の削除。** 旧既定値は `apiFetch` が本文なし（204・空ボディ）で `undefined` を返すことへの備えだったが、`bffFetch` は同じ場合に **`{}` を返す**ため `??` が発火しない——**置いても何も守らないコード**になる。空ボディの縮退は画面側の `search.data?.results ?? []` / `?? results.length` が受けており、**「本文が来なくても画面が壊れない」という性質は保たれている**（理由はコードのコメントにも残した） |

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

**測定条件**: worktree `chore/SC-03-11-orval-hook-migration`（`origin/develop` `3398a53` 基点。
**作業中に develop が 2 コミット進んだため `origin/develop`〔#549 = IADR-0134 のバンドル分割 ／
#551 = `SearchRequest` の検索モード〕を merge で取り込み、下表はすべて取り込み後に測った**）／
Node 22.22.2 ／ pnpm 10.33.0 ／ Vitest 3.2.7（v8 provider）／ orval 8.23.0 ／
**submodule `src/ai-stock-trading` と `planning` は populate 済み**。
スコープは断りがない限り**ワークスペース全体**（`src/` の 4 パッケージ ＋ AST）である。

| 検査 | コマンド | 結果 |
| --- | --- | --- |
| 型検査 | `pnpm run typecheck` | green（4 パッケージ。AST は**無改修**） |
| lint | `pnpm run lint` | green（**0 errors / 9 warnings**。warning は全件 `react-refresh/only-export-components` で、**着手前と同数**＝ errors も warnings も増やしていない） |
| 単体テスト | `pnpm run test` | 全 green（**件数は書かない**※。本作業が足したのは `orvalSelect.test.ts` と `useDocumentAdmin.test.tsx` の 2 ファイル、削除は 0） |
| カバレッジ | `pnpm run test:coverage` | green（**床を割らない**。lines 90 / statements 90 / functions 88 / branches 85。**床は動かしていない**——実測値は他 PR のマージで動くため書かない※） |
| ビルド | `pnpm run build` | green（最大チャンク `index-*.js` 274.47 kB / gzip 83.57 kB。**#549 の分割規則を壊していない**——500 kB 警告は出ない） |
| E2E | Playwright（後述の条件） | **13 tests 全 green**（12 tests ＋ #549 の `bundle-splitting.smoke.spec.ts` 1 件） |
| 生成物の乖離 | `pnpm run codegen` ＋ `git diff --exit-code -- src/platform/frontend/src/foundation/api/generated` | green（コミット後に再実行して差分なし） |
| 静的 egress | `node scripts/check-static-egress.js --require src/platform/frontend/dist` | green（**検出 0 件**。走査ファイル数は分割構成で動くため書かない※） |
| ドキュメントリンク | `node scripts/check-doc-links.js` | green（**件数は書かない**※） |
| コミット件名 | `node scripts/check-commit-messages.js --base origin/develop` | green（**件数は書かない**※——自分でコミットを積むたびに動く。マージコミットは `--no-merges` で対象外） |
| 契約スキーマ | `node scripts/check-contract-schema.js` | green（baseline と一致・未消化の承認 0 件。**C# はコメント 1 行しか触っていない**） |
| テスト・トレーサビリティ | `node scripts/check-test-traceability.js` | green（未写像 0 件。**allowlist は着手前と同じ 7 件**＝増やしていない） |
| テスト仕様書の被覆 | `node scripts/check-test-spec-coverage.js` | green（**床 68 は動かしていない**——バックエンドテストを足していない） |
| ユニット依存方向 | `node scripts/check-unit-dependencies.js` | green |
| i18n カタログ | `node scripts/check-i18n-catalogs.js` | green（2 ロケール・未翻訳 0 件。**カタログは 1 件も増減していない**——表示文言を足していない） |
| BFF 後段 | `node scripts/check-bff-downstreams.js` | green（ドリフト 0） |
| スクリプト自己試験 | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | green（**件数は書かない**※） |
| バックエンドのビルド | `dotnet build knowledge/backend/backend.slnx`（`mcr.microsoft.com/dotnet/sdk:10.0` コンテナ） | green（0 errors / 2 warnings。**warning は既存の `CS0618`**〔Testcontainers の廃止 API〕で本作業とは無関係） |

> **※ リポジトリ全体を数える値は、この表に固定値で書かない。** 他 PR のマージで必ず動き、
> 書いた瞬間から嘘になり始めるためである（#520 が同じ理由で採った作法）。**本表に残すのは
> green / 赤の別と、本 PR 固有の不変量**（allowlist を増やしていない・床を動かしていない・
> lint の警告数が着手前と同じ・C# はコメントだけ）**である**。

**E2E の実行条件**: この環境では `playwright install` がブラウザを取得できない。導入済みの
`/opt/pw-browsers/chromium-1194/chrome-linux/chrome` を `launchOptions.executablePath` で指す
**ローカル専用 config を一時的に置いて実走し、確認後に削除した**（#490 / #496 / #502〜#506 と同じ作法）。
**リポジトリの `platform/frontend/playwright.config.ts` は無改変であり、作業ツリーは clean である。**

> **1 件だけ、環境に起因する追加の手当てが要った。** 既定ポート 4173 は**別の作業ツリーの
> プレビューが使用中**だったため 4180 で実走した。ところが #549 が足した
> `bundle-splitting.smoke.spec.ts` は**応答の URL を `http://localhost:4173` の文字列で絞り込む**ため、
> 別ポートでは収集が空になり `chunks.length > 1` が落ちる。**本作業の変更に起因する失敗ではない。**
> ポート番号だけを 4180 へ読み替えたローカル複製で同じアサーションを実走して green を確認し、
> 複製は削除した（リポジトリの `e2e/` は無改変）。**この spec はポートを直書きしているため、
> 4173 が空いていない環境では常に落ちる**——申し送り 3。

### 受け入れ基準 1: 9 ファイルが生成物に載ったこと

```console
$ grep -rnE "apiFetch[<(]" src/knowledge/frontend/src src/platform/frontend/src \
    --include=*.ts --include=*.tsx | grep -v 'apiClient.ts\|apiClient.test.ts'
（出力なし＝呼び出しは 0 件）

$ grep -rn "from '@foundation/api/generated" src/knowledge/frontend/src/features/sc*/use*.ts | wc -l
18
```

`apiFetch` の**呼び出し**は SPA から消えた（残る文字列は 3 ファイルのコメント内の言及だけである）。
`foundation/api` の直接利用として残るのは **SC-01 の `apiStream`（SSE）のみ**で、これは恒久的に対象外である。

### 受け入れ基準 2: 画面の挙動が変わっていないこと

- **アサーションを変えたテストは 1 件も無い。** 変えたのは (a) モックの差し替え先（`apiFetch` →
  `apiRequest`）、(b) 応答の作り方（素の JSON → `Response` 相当）、(c) 要求の検証の**書き方**
  （`('/x', { json })` → `('/x', objectContaining({ method }))` ＋ 本文の JSON 比較）だけである。
  **パス文字列と呼び出し回数の期待値はそのまま**である。
- issue が名指しした 4 系統はいずれも**当該テストが無改修のアサーションで通り続けている**:
  存在秘匿の markup 一致（`sc11-config/access.test.tsx` / `sc09` / `sc10`）／403・404 の中立化と
  5xx の `role="alert"` の区別（`sc10` / `sc11`）／SC-09 の 409 の Problem 詳細（参照元ポリシー名）／
  `beginOperation()` による直近の操作結果の表示（`sc05` / `sc06` / `sc09`）。
- **SC-11 の「1 回の再取得で 3 本」も既存テストのまま通っている**（実装は前方一致から
  明示的な 3 本の無効化へ変わったが、**観測される挙動は同じ**）。

### 変異試験（**受け入れ基準そのもの**）

**件数の基準**: 契約（OpenAPI）側 **13 件（M1〜M13）**＋ コード側 **3 件（MC1〜MC3）**である。
手順は契約側が「変異を当てる → `pnpm run codegen` → `pnpm run typecheck` → **復元して差分 0 を確認**」、
コード側が「変異を当てる → `pnpm exec vitest run <画面>` → **復元して差分 0 を確認**」。
**全 16 件で `restored OK`（作業ツリーに残骸なし）を確認した。**

| # | 壊した箇所 | 読む画面 | 期待 | **実測** |
| --- | --- | --- | --- | --- |
| **M1** | `DriftFindingDto.detail` を削除（**#520 の M6 の再実行**） | SC-11 | 落ちる | **落ちた**。`typecheck exit=2` / error 1 件 / `TS2339`。**#520 では素通りしていた** |
| **M2** | `SearchResponse.totalHits` を削除（**#520 の M7 の再実行**） | SC-02 | 落ちる | **落ちた**。`exit=2` / `SearchResultsPage.tsx(71,34): error TS2339: Property 'totalHits' does not exist on type 'NoInfer<SearchResponse>'`。**#520 では素通りしていた** |
| M2′ | 同じ `totalHits` を **`required` からだけ外す**（プロパティは残す） | SC-02 | — | **素通りした**（`exit=0`）。**変異が不完全だっただけ**だが、#520 の M4 と同じ事象を再現している——`required` を落とす退行は型検査ではなく**生成物の再生成差分検査**が捕まえる |
| **M3** | `DocumentDto.title` を `titleRenamed` へ改名 | SC-03 / SC-05 | 落ちる | **落ちた**。`exit=2` / error 3 件（`DocumentDetailPage` / `DocumentForm` / `DocumentManagementPage`） |
| **M4** | `DataSourceDto.lastSyncedAt` を削除 | SC-06 | 落ちる | **落ちた**。`exit=2` / error 2 件 / `TS2339` |
| **M5** | `ConversionJobDto.attempts` を削除（**#506 の M5 の再実行**） | SC-07 | **素通りする** | **素通りした**（`exit=0`）。**載せ替え後も**である——理由は下表 |
| **M6** | `AbacPolicyDto.isActive` を削除 | SC-09 | 落ちる | **落ちた**。`exit=2` / error 5 件 / `TS2339` |
| **M7** | `DashboardSummaryDto.quality` を削除 | SC-10 | 落ちる | **落ちた**。`exit=2` / error 3 件 / `TS2339` |
| **M8** | `ConfigVersionEntryDto.hadDrift` を削除 | SC-11（履歴） | 落ちる | **落ちた**。`exit=2` / error 1 件 / `TS2339` |
| **M9** | `FeedbackRequest.rating` を改名 | SC-01（送信） | 落ちる | **落ちた**。`exit=2` / `useAskStream.ts(120,43): error TS2353`（**要求側の型でも網が効く**） |
| **M10** | `DocumentContentDto.markdown` を削除 | SC-03 | 落ちる | **落ちた**。`exit=2` / error 1 件 / `TS2339` |
| **M11** | `/bff/admin/config` の 200 応答型を `EffectiveConfigDto` → `DriftReportDto` へ**差し替え** | SC-11 | 落ちる | **落ちた**。`exit=2` / `useConfigViewer.ts(31,57): error TS2322`。**フィールドの増減だけでなく「応答型そのものの取り違え」も捕まる**（§設計 1 の主張の実測） |
| **M12** | `operationId` を旧記載へ戻す（`bff-analysis-analyze` → `analysis-analyze`） | SC-08 | 落ちる | **落ちた**。`exit=2` / `TS2724: has no exported member named 'useBffAnalysisAnalyze'` |
| **M13** | `EmbedApiResponse.model` を削除（**画面が読まない面**） | — | **素通りする** | **素通りした**（`exit=0`）。**ただし生成物には差分が出る**（`bff.schemas.ts` が 1 行減る）＝再生成差分検査は捕まえる |
| **MC1** | SC-11 の再取得を **1 本だけ**の無効化にする | SC-11 | 落ちる | **落ちた**。`ConfigViewerPage (SC-11) > refetches all three queries when the refresh button is pressed` が失敗（1 failed / 34 passed） |
| **MC2** | SC-11 のドリフト取得から `select: okData` を外す（封筒が漏れる） | SC-11 | 落ちる | **落ちた**（12 failed / 23 passed）。**封筒剥がしが外れると画面は例外を出さずに静かに空になる**——テストが唯一の防波堤である |
| **MC3** | SC-07 の再変換後の無効化キーを**条件つきキー**にする | SC-07 | 落ちる | **落ちた**。`refetches the list after a successful retry` が失敗（1 failed / 21 passed） |
| **MD1** | SC-11 の履歴取得の `select: okArray` を `okData` へ戻す（**🔴 是正前の状態**） | SC-11 | 落ちる | **落ちた**。`TypeError: entries.map is not a function` で `degrades to the empty-history message when the history response has no body (204)` が失敗（1 failed / 17 passed）。**是正前はこの経路でルートごとクラッシュしていた** |
| **MD2** | SC-05 の `documentInvalidationKeys` を**一覧キーだけ**へ戻す（**🔴 是正前の状態**） | SC-05 → SC-03 | 落ちる | **落ちた**。`useDocumentAdmin.test.tsx` が **5 件失敗**（`expected [true, false, false, false] to deeply equal [true, true, true, true]`）。公開・アーカイブ・更新・削除の各成功後に SC-03 の 3 クエリへ届かないことを捕まえる |
| **MD3** | SC-05 / SC-06 / SC-07 の一覧の `select: okArray` を `okData` へ戻す（**AI レビューが見つけた同型の残り**） | SC-05・SC-06・SC-07 | 落ちる | **落ちた**。3 画面とも `TypeError: items.map is not a function` で 204 縮退テストが失敗 |

#### MD1 / MD2 —— **クロス監査が見つけた 2 つの退行を、恒久のテストで固定した**

MD1 / MD2 はいずれも**載せ替えが持ち込んだ実挙動の退行**であり、クロス監査が実測で発見した。

- **MD1**: `bffFetch` は本文が空なら `{}` を返す。**`{} ?? []` は `{}` なので `??` は発火しない**——
  「`?? []` を残したから安全」という当初の記録は事実と逆で、**空ボディで `.map` に `{}` が届いてクラッシュ**
  していた（develop では `apiFetch → undefined → ?? []` が実際に効いていた）。`okArray` で実効ガードへ置き換えた。
- **MD2**: 階層キー（`['bff','documents']` / `['bff','documents',id,…]`）が持っていた前方一致が、
  生成キー（`['/bff/documents']` / `['/bff/documents/{id}']`）では**成立しない**。SC-05 の更新が SC-03 の
  詳細・本文・版履歴へ届かなくなっていた。明示列挙（`documentInvalidationKeys`）で復元した。

**どちらも画面テストでは原理的に検出できない。** テスト用 QueryClient（`renderUnitRoute.tsx`）が
`staleTime: 0 / gcTime: 0` で作られるため、無効化が届かなくても再マウントで必ず再取得されるからである。
MD2 は **QueryClient を直接使う単体テスト**（`useDocumentAdmin.test.tsx`）でしか固定できない。

#### MD3 —— **是正が 4 箇所で止まっており、同型が 3 画面に残っていた**

MD1 の是正（`?? []` → `okArray`）を **SC-03 版履歴・SC-09 属性・SC-09 ポリシー・SC-11 履歴の 4 箇所**に
当てたが、**本文そのものが配列である一覧エンドポイントが他に 3 つあり、手つかずだった**
（`useAdminDocuments` / `useDataSources` / `useConversionJobs`）。AI レビューの指摘で判明した。

**これも本作業が持ち込んだ退行である。** 載せ替え前は `apiFetch` が空ボディで `undefined` を返すため
`items = data ?? []` が実効ガードだった。生成物の `bffFetch` は `{}` を返すので `??` は発火せず、
さらに **`items.length === 0` も `{}.length === undefined` で救えない**ため `items.map` が投げる。

3 画面とも `okArray` へ寄せ、**204 縮退の回帰テストを各画面に足した**（MD3 で落ちることを実測）。

> **教訓**: 「同じ失敗モードが他にもないか」を**母集合で確かめずに個別対処した**のが原因である。
> 配列を返す面の一覧は `grep -n 'select: ok' features/**/use*.ts` で数え切れる。**是正は母集合から入る。**

#### 素通りしたもの（**3 件。隠さない**）

| # | 素通りした事象 | いま網が無い理由 | 引き受け先 |
| --- | --- | --- | --- |
| M2′ | `required` からだけ外す退行が型検査で落ちない | orval は `required` の無いプロパティを `?` で生成するだけで、**プロパティ自体は残る**。読み出しは型として妥当なままになる | **恒久（設計どおり）**。この退行は生成物の再生成差分検査（CI）が捕まえる（#520 の M4 と同じ） |
| M5 | `ConversionJobDto.attempts` を消しても落ちない | **SC-07 の画面が `attempts` を読んでいない**（「デッドレターの内訳」は契約に標識が無く**意図的に未実装**。画面仕様書 §hi-fi モックアップとの対応）。載せ替えは「画面が読むフィールド」にしか網を張らない | **恒久**。読まないフィールドに型検査の網は原理的に張れない。fixture 側の型付け（IADR-0135 フォローアップ 1）で別種の網は張れる |
| M13 | `EmbedApiResponse.model` を消しても落ちない | `/embed` は **SPA が呼ぶ面ではない**（BFF 境界の外） | **恒久**。契約記述の正しさは人手の突合に依存する（#520 決定 5 の但し書きと同じ） |

**素通りの原因は「型検査の網の弱さ」ではなく「その型を画面が読んでいないこと」である。**
M1〜M4・M6〜M12 が示すとおり、**読んでいる面では削除も改名も型の差し替えも、要求側の型まで含めて捕まる。**

## 未決事項・親への申し送り

| # | 事項 | 種別 | 送り先 |
| --- | --- | --- | --- |
| 1 | **テストの fixture が生成型で型付けされていない**（#520 §未決事項 2 は**未解消のまま**）。`jsonResponse(body: unknown)` を経由するため、契約に無いフィールドを持つ fixture も、必須フィールドを欠く fixture も検出されない（実測: M5 の `attempts: 3` は fixture に残っているが型検査に掛からない） | 網の穴 | **[[IADR-0135]] フォローアップ 1**。生成 MSW モック（`*.msw.ts` / `*.faker.ts`）へ移すのが筋。**本作業では行っていない**——13 ファイルの fixture を一斉に置き換えると「挙動が変わっていない」ことの確認と混ざる |
| 2 | **C# → OpenAPI の追随は人手のまま**。本作業で届いたのは「**OpenAPI を変えると型検査が落ちる**」までで、「**C# の DTO を変えると落ちる**」ではない | 構造的な穴 | [[IADR-0131]] フォローアップ 2 ／ [[IADR-0132]] フォローアップ 1。**載せ替えが済んだいま、残る穴はここだけ**である |
| 3 | **`e2e/bundle-splitting.smoke.spec.ts` がポート 4173 を文字列で直書きしている**（#549 が追加）。`baseURL` を変えても応答の絞り込みが追随しないため、**4173 が空いていない環境では常に落ちる**。CI（専用 runner）では問題にならないが、**ローカル並行作業では落ちる**（本作業で実際に踏んだ） | 小さな是正 | 親。`baseURL` から組み立てるか、`new URL(url).origin` と `page.url()` を突き合わせる形にすれば解消する。**本作業では直していない**（#549 の成果物であり、範囲が混ざる） |
| 4 | **SC-02 だけが生成「フック」を使わない**（`/bff/search` が POST であるため。[[IADR-0135]] 決定 2） | 自覚した非対称 | 恒久。「生成フックに載っているか」で機械的に検査することはできない——検査するなら「生成物由来の型／関数を import しているか」で見る必要がある |
| 5 | **`orval-bff-only.cjs` に「`components.schemas` は素通りする」知見が無い**（#520 §未決事項 8） | 知見の置き場所 | **未消化**。本作業でも同ファイルは触っていない。次に同ファイルを触る issue で入れるのが妥当 |
| 6 | **`/bff/feedback`・`/bff/feedback/stats` の端点認可が未裁定**（#521） | 要裁定 | **#521**（起票済み）。本作業では判断しない（認可の変更は挙動の変更） |
| 7 | **`.github/workflows/` は触っていない**（権限外） | 情報 | `frontend.yml` の `paths` に `docs/api/openapi.yaml` が入っており、契約変更で CI が起動する。#520 §未決事項 7（`frontend-tests.yml` の `paths` に契約が無い）は**未解消のまま**である |
