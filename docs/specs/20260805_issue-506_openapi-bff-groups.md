---
title: docs/api/openapi.yaml へ BFF の欠落群を追加し AiAnswerDto を実体へ是正する
type: spec
status: done
related_ids: [SC-01, SC-03, SC-05, SC-06, SC-07, SC-08, SC-09, SC-10, SC-11, UC-01, UC-02, UC-03, UC-04, UC-05, UC-06, FR-01, FR-04, FR-06, FR-08, FR-09, FR-10, FR-12, FR-15, ADR-0031, IADR-0009, IADR-0040, IADR-0053, IADR-0116, IADR-0121, IADR-0122, IADR-0126, IADR-0127, IADR-0128, IADR-0129, IADR-0131]
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
  - ../adr/IADR-0131_openapi-as-bff-contract-source.md
  - ../adr/IADR-0121_spa-stack-migration-staging.md
  - ../adr/IADR-0122_contract-schema-source-and-compat-gate.md
  - ../adr/IADR-0127_sc07-retry-admin-only-and-derived-states.md
  - ../adr/IADR-0129_sc09-11-admin-ops-screen-composition.md
  - ./20260805_issue-503_sc05-08-admin-screens.md
  - ./20260805_issue-504_sc09-11-admin-ops-screens.md
---

# 仕様書: OpenAPI への BFF 群の追加と `AiAnswerDto` の是正（#506）

> 本仕様書は実装着手前に作成した。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: **SC-01**（検索・チャット）・**SC-03**（文書詳細）・**SC-05**（文書管理）・
  **SC-06**（データソース管理）・**SC-07**（変換ジョブ）・**SC-08**（AI 分析）・
  **SC-09**（管理者設定〔ABAC〕）・**SC-10**（運用ダッシュボード）・**SC-11**（構成ビューア）
- ユースケース（UC）: UC-01 / UC-02 / UC-03 / UC-04 / UC-05 / UC-06
- 機能要求（FR）: FR-01 / FR-04 / FR-06 / FR-08 / FR-09 / FR-10 / FR-12 / FR-15
- 関連 ADR（計画）:
  [ADR-0031](../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md)（Accepted。
  BFF 境界と生成クライアント）
- 関連 IADR: **[[IADR-0131]]（本作業の内部設計判断。本書と対で読む）**・[[IADR-0121]] 決定 3（BFF 境界・
  手書き HTTP クライアント禁止）・[[IADR-0122]]（契約スキーマの正本）・[[IADR-0009]]（存在秘匿）・
  [[IADR-0040]]（ABAC 管理 API の透過中継）・[[IADR-0127]]・[[IADR-0129]]
- 本リポジトリの起点: **#506**（親 #454。出所は #502 / PR #505 §付-1、#503 / PR #508 §未決事項、
  #504 / PR #511 §未決事項）

## 目的・背景

`CLAUDE.md` と [[IADR-0121]] 決定 3 は「BFF への呼び出しは **orval 生成フック**（入力は
`docs/api/openapi.yaml` の `/bff/` 配下のみ）か `foundation/api` の `apiFetch` / `apiStream`」と定める。
**OpenAPI に無いエンドポイントには生成フックが存在しない**ため、対象画面は `apiFetch` ＋ 手書きの
TypeScript 型で実装されている。規約違反ではないが、**BFF の DTO（契約）が変わっても型検査が素通りする**。

本作業は**契約の単一情報源を揃える**ところまでを引き受ける（画面の載せ替えは分割する。§分割）。

## 着手時の実測（issue の記述を鵜呑みにしない）

### 実測 1: OpenAPI に載っている `/bff` パス

```console
$ grep -c '^  /bff' docs/api/openapi.yaml
8
```

母集合は `docs/api/openapi.yaml` の**トップレベル `paths` 直下**（インデント 2）の `/bff` 始まりのキーである。
8 本の内訳は `/bff/search`・`/bff/analysis/ask`・`/bff/analysis/analyze`・`/bff/feedback`・
`/bff/feedback/stats`・`/bff/dashboard/summary`・`/bff/admin/config`・`/bff/admin/config/drift`。
**issue の記述と一致する。**

### 実測 2: BFF のルートグループ（実装側）

```console
$ grep -rn 'MapGroup("/bff' src/platform/backend src/knowledge/backend | wc -l
10
```

10 行のうち `DocumentBffEndpoints.cs` の 2 行は**同じ接頭辞 `/bff/documents` を読み取り用と書き込み用で
2 回宣言している**ため、**接頭辞で数えると 9 群**である。**数え方で 10 にも 9 にもなる**ので基準を添える。

| # | 接頭辞 | 宣言箇所 | OpenAPI |
| --- | --- | --- | --- |
| 1 | `/bff/search` | `SearchBffEndpoints.cs:23` | 載っている |
| 2 | `/bff/analysis` | `AnalysisBffEndpoints.cs:15` | `ask` / `analyze` は載っている。**`ask/stream` は意図的に載せない**（§SSE） |
| 3 | `/bff/feedback` | `FeedbackBffEndpoints.cs:16` | 載っている |
| 4 | `/bff/dashboard` | `DashboardBffEndpoints.cs:21` | 載っている |
| 5 | `/bff/documents` | `DocumentBffEndpoints.cs:23`（読み）・`:77`（書き） | **無い**（本作業で追加） |
| 6 | `/bff/datasources` | `DataSourceBffEndpoints.cs:20` | **無い**（本作業で追加） |
| 7 | `/bff/conversion/jobs` | `ConversionBffEndpoints.cs:21` | **無い**（本作業で追加） |
| 8 | `/bff/admin/authz` | `AuthzBffEndpoints.cs:16` | **無い**（本作業で追加） |
| 9 | `/bff/admin/config` | `ConfigBffEndpoints.cs:20` | **一部だけ載っている**（後述） |

### 実測 3: 対象画面の通信コード（手書き型 / 生成フックの仕分け）

母集合は `src/knowledge/frontend/src/features/sc*/use*.ts`（**10 ファイル**）である
（`ls src/knowledge/frontend/src/features/sc*/use*.ts | wc -l` = 10。下表も 10 行ある）。

| 画面 | ファイル | 呼び方 | OpenAPI にパスが在るか |
| --- | --- | --- | --- |
| SC-01 | `sc01-search/useAskStream.ts` | `apiStream`（SSE）＋ `apiFetch('/feedback')` | ストリームは**意図的に対象外**。`/bff/feedback` は**在る** |
| SC-02 | `sc02-results/useSearchQuery.ts` | `apiFetch('/search')` | **在る** |
| SC-03 | `sc03-document/useDocumentQueries.ts` | `apiFetch` ×3 | 無い |
| SC-05 | `sc05-documents/useDocumentAdmin.ts` | `apiFetch` ×5 | 無い |
| SC-06 | `sc06-datasources/useDataSources.ts` | `apiFetch` ×4 | 無い |
| SC-07 | `sc07-conversions/useConversionJobs.ts` | `apiFetch` ×2 | 無い |
| SC-08 | `sc08-analysis/useAnalysisTask.ts` | **`useAnalysisAnalyze`（生成フック）** | 在る |
| SC-09 | `sc09-admin-abac/useAbacAdmin.ts` | `apiFetch` ×7 | 無い |
| SC-10 | `sc10-operations/useDashboardSummary.ts` | `apiFetch('/dashboard/summary')` | **在る** |
| SC-11 | `sc11-config/useConfigViewer.ts` | `apiFetch` ×3 | **2/3 が在る**（`/history` だけ無い） |

**生成フックを使っているのは 10 ファイル中 1 つ（SC-08）だけであり、残る 9 ファイルは `apiFetch`
（SC-01 は `apiStream` も）＋ 手書き型である**（`grep -l apiFetch src/knowledge/frontend/src/features/sc*/use*.ts | wc -l` = 9）。

### 実測 4（issue が確認を求めた点）: SC-10 / SC-11 は「載っているのに使っていない」

**issue の推測どおりであり、これは OpenAPI の欠落とは別種の是正である。**

| 画面 | パス | OpenAPI | 呼び方 | 判定 |
| --- | --- | --- | --- | --- |
| SC-10 | `GET /bff/dashboard/summary` | **在る**（`openapi.yaml:173`） | `apiFetch` ＋ 手書き `DashboardSummary` | **載っているのに生成フック未使用** |
| SC-11 | `GET /bff/admin/config` | **在る**（`:205`） | `apiFetch` ＋ 手書き `EffectiveConfig` | 同上 |
| SC-11 | `GET /bff/admin/config/drift` | **在る**（`:226`） | `apiFetch` ＋ 手書き `DriftReport` | 同上 |
| SC-11 | `GET /bff/admin/config/history` | **無い** | `apiFetch` ＋ 手書き `ConfigVersionEntry` | **OpenAPI の欠落**（`ConfigBffEndpoints.cs:58` に実装あり） |

**コード内コメントの記述が実測と食い違っている。** `useDashboardSummary.ts:10` は
「`/bff/dashboard` は docs/api/openapi.yaml に無く」、`useConfigViewer.ts:9` は
「`/bff/admin/config` 群は docs/api/openapi.yaml に無く」と書くが、**いずれも在る**。
これは #504 の作業仕様書 §6 の記述（「3 群とも `docs/api/openapi.yaml` に無く」）が
そのままコメントへ写されたものである。**コメントの是正は載せ替え側（分割 2 本目）で行う**
——本作業でコメントだけ直すと、直後に同じ行を書き換えることになるためである。

### 実測 5: OpenAPI はコードから生成されていない（手書きである）

`scripts/generate-openapi.sh` は**存在しない**。`.github/workflows/openapi.yml` は当該ファイルが無い場合
`gen-openapi-skeleton.js` を `--force` **無し**で呼ぶだけで、既存の `docs/api/openapi.yaml` を上書きしない
（同ファイル `:64-70` のコメントが「手書きの OpenAPI 3.1.0 リッチ仕様」と明記している）。

**これは本作業が与えられる保証の上限を決める。** 本作業の後に得られるのは
「**OpenAPI を変えると型検査が落ちる**」であって、「**C# の DTO を変えると型検査が落ちる**」ではない。
C# → OpenAPI の追随は人手であり、そこを機械化するのは本 issue の射程外である（§親への申し送り）。

## 分割（§進め方の注意に従う）

issue #506 と [[IADR-0116]] 規約 4 に従い、**2 本に分割する**。

| 本数 | 範囲 | 本 worktree |
| --- | --- | --- |
| **1 本目（本作業）** | OpenAPI への BFF 群の追加 ＋ `AiAnswerDto` の是正 ＋ 生成物の更新 ＋ SSE の明記 | **完了させる** |
| **2 本目（#519）** | 各画面の `apiFetch` ＋ 手書き型 → 生成フックへの載せ替え（SC-01 feedback / SC-02 / SC-03 / SC-05 / SC-06 / SC-07 / SC-09 / SC-10 / SC-11） | **やらない**（§残りとして何をどうするか） |

**分割の理由**:

1. **1 本目だけで独立した価値がある**——契約の単一情報源が揃い、生成物（型・フック・MSW モック）が
   全 BFF 面について存在するようになる。載せ替えはその後いつでもできる。
2. **1 本目の diff は「宣言の追加」に閉じ、画面の挙動を一切変えない**。2 本目は 9 ファイルの
   通信層と、それに依存する画面・テストへ波及する。**レビュー可能な単位が明確に違う。**
3. **順序が逆にできない**——`AiAnswerDto.citations` が実体と食い違ったまま載せ替えると、
   誤った型が生成される（issue の指摘どおり）。

**勝手に範囲を狭めたのではない**——2 本目の作業内容を §残りとして何をどうするか に具体化して申し送る。

## 対象範囲

### 対象

1. **`docs/api/openapi.yaml` へ BFF の欠落分を追加する**（**実装と突合した定義**。推測で書かない）。
   - `/bff/documents` 群（**6 パス**）／`/bff/datasources` 群（3 パス）／`/bff/conversion/jobs` 群（3 パス）／
     `/bff/admin/authz` 群（**5 パス**）／`/bff/admin/config/history`（1 パス）／`/bff/analysis/ask/stream`（1 パス。SSE）。
     **合計 19 パスで 8 → 27 になる**（数え方は `paths` 直下のキー。§検証）。
2. **`AiAnswerDto` を実体へ是正する**（`citations` の型 ＋ 欠落フィールド）。
3. **`pnpm run codegen` で生成物を更新しコミットする**。
4. **SSE（`/bff/analysis/ask/stream`）が意図的に対象外であることを明記する**
   （「載せ忘れ」と「意図的な除外」を区別できるようにする）。
5. 2 の型変更に**追随が必要になるコードだけ**を直す（SC-08 の出典表示。§4）。
6. **通信仕様書**（`docs/api/BFF_bff-surface.md`）を新設し、BFF 面の一覧と生成対象／対象外の境界を書く。

### 対象外（送り先を明記する）

| 事項 | 送り先 | 理由 |
| --- | --- | --- |
| **9 ファイルの生成フックへの載せ替え** | **分割 2 本目 = #519**（起票済み） | 上記 §分割 |
| `useDashboardSummary.ts` / `useConfigViewer.ts` / `useDataSources.ts` などの**「OpenAPI に無い」コメントの是正** | 分割 2 本目 | 同じ行を 2 度書き換えないため |
| **C# → OpenAPI の自動生成**（`scripts/generate-openapi.sh` の整備） | §親への申し送り | 本 issue の射程外。実測 5 |
| **`/bff/analysis/ask/stream` の生成** | やらない（恒久） | orval は SSE を扱えない。§SSE |
| `POST /bff/internal/config/drift-run` | やらない（恒久） | `ExcludeFromDescription()`。メッシュ内部限定で ingress へ公開しない（`ConfigBffEndpoints.cs:74-94`） |
| 既存 2 件の `operationId` 命名の不統一（`analysis-ask` / `analysis-analyze`） | §親への申し送り | 直すと `useAnalysisAnalyze` が改名され SC-08 に波及する。本作業の目的（宣言の追加）と混ざる |

## 設計

内部設計の判断（選択肢の比較・棄却理由）は [[IADR-0131]] を正とする。本節は書く内容を確定する。

### 1. 定義の起こし方（**実装と突合する手順**）

各エンドポイントについて、次の 3 つを**実際に開いて**突合した。推測で書いた行は無い。

| 段 | 読んだもの | 得るもの |
| --- | --- | --- |
| a | `*BffEndpoints.cs` | パス・メソッド・**BFF 自身が返すステータス**・`WithName` |
| b | 後段サービスの `*Endpoints.cs` | **透過中継されるステータスと本文**（BFF が `Results.Content` / `Results.StatusCode` で素通しする分） |
| c | `*Dto.cs`（`Knowledge.Contracts` / `Platform.Shared.Contracts`） | スキーマのフィールド・必須・型 |

**b を省くと 409 と 400 が落ちる**——BFF のハンドラ本体には現れず、後段の応答をそのまま返す形でしか
存在しないためである（issue が「落とすな」と名指しした 4 件はすべてこの型である）。

### 2. `operationId` の規約（**新規分**）

**`operationId` は C# の `WithName(...)` のケバブケースにする。** 突合が機械的にできるようにするためである。

```console
$ grep -rn 'WithName("Bff' src/platform/backend src/knowledge/backend | wc -l
39
```

**39 個の `WithName("Bff…")` のうち 1 つ（`BffConfigDriftRun`）は `ExcludeFromDescription()` で契約に
載せない**（§対象外）ため、契約に載る `/bff` 配下の操作は **38** である（§検証 受け入れ基準 1 の内訳と一致する）。

既存 8 パスのうち **6 本はこの規約に合致**し、**2 本（`analysis-ask` / `analysis-analyze`）は合致しない**
（`WithName` は `BffAnalysisAsk` / `BffAnalysisAnalyze`）。**既存は改名しない**（§対象外）。

### 3. `AiAnswerDto` の是正（**2 か所ある**）

実体は `src/knowledge/backend/Shared/Knowledge.Contracts/Dtos/SearchResultDto.cs` の `AiAnswerDto` である。

| # | OpenAPI（現状） | 実体 | 是正 |
| --- | --- | --- | --- |
| 1 | `citations: SearchResultDto[]` | `List<CitationDto> Citations` | **`CitationDto[]` へ**。`CitationDto` スキーマを新設 |
| 2 | （フィールドが無い） | `Guid AnswerId { get; init; }` | **`answerId` を追加**。FR-08 のフィードバック紐付け先で、**SC-01 が実際に使っている** |

`CitationDto(int Number, Guid DocumentId, string DocumentTitle, Guid ChunkId, string? SourceUri,
float Score, string Snippet)` の 7 フィールドをそのまま写す。

**共通するのは `documentId` / `documentTitle` / `chunkId` / `score` の 4 つで、
`SearchResultDto` にしか無いのは `text` / `markdownUri` / `attributes` / `tags`、
`CitationDto` にしか無いのは `number` / `snippet` / `sourceUri` である。**
SC-08 は共通部分のうち `documentId` / `documentTitle` / `chunkId` だけを使って回避していた。

### 4. 型是正に追随するコード（**これだけを直す**）

`src/knowledge/frontend/src/features/sc08-analysis/AnalysisDashboardPage.tsx` の `CitationLink` は
引数型に `SearchResultDto` を指定している（`:226`）。**是正後は `CitationDto` になる**ので追随する。
**画面の見た目・DOM・文言は変えない**——`CitationLink` が読むのは `documentId` / `documentTitle` /
`chunkId` の 3 つで、いずれも `CitationDto` にも在るためである。回避コメント（`:223-225`）は
**是正済みの事実へ書き換える**（残すと後任が「まだ食い違っている」と読む）。

### 5. SSE の扱い（**明記する**）

`/bff/analysis/ask/stream`（`AnalysisBffEndpoints.cs:64`、`WithName("BffAnalysisAskStream")`）は
`text/event-stream` を中継する。**orval は SSE を扱えないため生成対象にしない。**

「載せ忘れ」と区別できるよう、**2 か所に書く**。

1. `docs/api/openapi.yaml` の `paths` に **`/bff/analysis/ask/stream` を載せる**（`text/event-stream` の
   応答として）。——**「宣言としては在る／生成としては対象外」が 1 か所で読める形にする**。
2. `docs/api/BFF_bff-surface.md`（新設）の一覧表で「生成される関数: **無し（SSE。`apiStream`）**」と明示する。

> **［着手時の想定と、実測でどう変わったか］** 着手時は「`application/json` を持たないので orval は
> フックを作らないはず」と想定し、「作ってしまうなら宣言を載せない側へ倒す」と条件を先に決めていた。
> **実測では作った**——`useBffAnalysisAskStream` という *動きそうに見える* mutation が生成され、
> mutator（`bffFetch`）が本文を全部読んでから `JSON.parse` するため、**ストリーミングにならないうえ
> SSE 本文で例外になる**。そこで**第 3 の案**を採った: **宣言は載せたまま、生成の前処理
> （`orval-bff-only.cjs`）が「SSE の応答を持ち JSON の応答を持たない操作」を落とす**。
> 契約は完全なまま、罠のフックは存在しない。判断の根拠は [[IADR-0131]] 決定 4。

### 6. 追加するパスと応答（**実装から起こした一覧**）

**認可はすべて BFF 実装の実測である。**「401/403」は `RequireAuthorization` の既定挙動、
「404（秘匿）」は `IADR-0009` の存在秘匿を指す。

#### `/bff/documents`（SC-03 / SC-05・FR-06・UC-03）

| メソッド・パス | 認可 | 応答 | 出所 |
| --- | --- | --- | --- |
| `GET /bff/documents` | 無し（読み） | 200 `DocumentDto[]`（**スコープ解決不能は空配列**） | `DocumentBffEndpoints.cs:26-36` |
| `GET /bff/documents/{id}` | 無し | 200 `DocumentDto` / **404**（不在とスコープ外を区別しない） | `:39-44` |
| `GET /bff/documents/{id}/content` | 無し | 200 `DocumentContentDto` / 404 | `:61-71` |
| `GET /bff/documents/{id}/versions` | 無し | 200 `DocumentVersionDto[]` / 404 | `:47-58` |
| `POST /bff/documents` | admin または operator | 201 `DocumentDto` / 400 / **403**（許可ポリシー無し）/ 401 | `:84-94` ＋ 後段 `DocumentEndpoints.cs:44-65` |
| `PUT /bff/documents/{id}` | 同上 | 200 `DocumentDto` / 400 / 404 / **409（版競合）** / 401 / 403 | `:97-100` ＋ 後段 `:67-97` |
| `POST /bff/documents/{id}/publish` | 同上 | 200 `DocumentDto` / 404 / **409（不正遷移）** | `:103-106` ＋ 後段 `:124-141` |
| `POST /bff/documents/{id}/archive` | 同上 | 200 `DocumentDto` / 404 | `:109-112` ＋ 後段 `:145-157` |
| `DELETE /bff/documents/{id}` | 同上 | 204 / 404 | `:115-118` ＋ 後段 `:177-190` |

**409 が 2 種類ある**（`version_conflict` / `invalid_transition`）。本文はどちらも
`{ error, … }` の素の JSON で、RFC7807 ではない。**そのまま書く。**

#### `/bff/datasources`（SC-06・FR-01/FR-02・UC-04）

グループ全体が **admin または operator**（`DataSourceBffEndpoints.cs:20-24`）。

| メソッド・パス | 応答 | 出所 |
| --- | --- | --- |
| `GET /bff/datasources` | 200 `DataSourceDto[]` / **502**（後段不達を空へ縮退させない） / 401 / 403 | `:30-45` |
| `POST /bff/datasources` | 201 `DataSourceDto` / 401 / 403 | `:59-70` |
| `GET /bff/datasources/{id}` | 200 `DataSourceDto` / 404 | `:48-56` |
| `POST /bff/datasources/{id}/sync` | **202** `DataSourceSyncResultDto` / 404 | `:73-83` ＋ 後段 `DataSourceEndpoints.cs:48-66` |
| `DELETE /bff/datasources/{id}` | 204（論理削除＝無効化） / 404 | `:86-92` ＋ 後段 `:68-75` |

**BFF のコメント（`:80`）は同期応答を `{ fetchId, status }` と書くが、後段の実体は
`{ fetched, failed, connectorAvailable, message }` である**（`DataSourceEndpoints.cs:61-66`）。
**OpenAPI には実体を書く**（コメントの是正は §親への申し送り）。

#### `/bff/conversion/jobs`（SC-07・FR-12・UC-06）

グループは **admin または operator**、**`retry` だけ AdminOnly**（`ConversionBffEndpoints.cs:21-25, 72`。
認可メタデータは AND 合成されるため実効要件は admin のみ。[[IADR-0128]] 決定 1）。

| メソッド・パス | 応答 | 出所 |
| --- | --- | --- |
| `GET /bff/conversion/jobs?status=` | 200 `ConversionJobDto[]` / 502 / 401 / 403 | `:31-47` |
| `GET /bff/conversion/jobs/{id}` | 200 `ConversionJobDto` / 404 | `:50-58` |
| `POST /bff/conversion/jobs/{id}/retry` | **202（本文なし）** / 404 / **409（`failed` 以外）** / 401 / **403（admin 以外）** | `:66-72` ＋ 後段 `ConversionJobEndpoints.cs:31-45` |

**重要（実測）**: 後段は 409 の本文に `{ "error": "not_retryable", "status": … }` を載せるが、
**BFF は `Results.StatusCode((int)resp.StatusCode)` で中継するため本文を落とす**（`:70`）。
**OpenAPI には「409・本文なし」と書く**——後段の本文を書くと、生成物が実在しない本文の型を作る。

#### `/bff/admin/authz`（SC-09・FR-09・UC-05）

グループ全体が **AdminOnly**（`AuthzBffEndpoints.cs:16-18`）。全パスに **401 / 403 / 502**（後段不達）が付く。

| メソッド・パス | 応答 | 出所 |
| --- | --- | --- |
| `GET /bff/admin/authz/policies` | 200 `AbacPolicyDto[]` | `:21-23` |
| `POST /bff/admin/authz/policies` | 201 `AbacPolicyDto` / **400（保存前の矛盾検証。RFC7807）** | `:30-32` ＋ 後段 `AuthzEndpoints.cs:41-56` |
| `GET /bff/admin/authz/policies/{id}` | 200 `AbacPolicyDto` / 404 | `:25-27` |
| `PUT /bff/admin/authz/policies/{id}` | 200 `AbacPolicyDto` / 400 / 404 | `:34-36` ＋ 後段 `:59-73` |
| `DELETE /bff/admin/authz/policies/{id}` | 204 / 404 | `:43-45` ＋ 後段 `:88-97` |
| `PATCH /bff/admin/authz/policies/{id}/active` | 200 `AbacPolicyDto` / 404 | `:39-41` ＋ 後段 `:76-85` |
| `GET /bff/admin/authz/attributes` | 200 `AttributeDefinitionDto[]` | `:48-50` |
| `POST /bff/admin/authz/attributes` | 201 `AttributeDefinitionDto` / 400 | `:56-58` ＋ 後段 `:113-135` |
| `GET /bff/admin/authz/attributes/{id}` | 200 `AttributeDefinitionDto` / 404 | `:52-54` |
| `PUT /bff/admin/authz/attributes/{id}` | 200 `AttributeDefinitionDto` / 400 / 404 | `:60-62` ＋ 後段 `:138-154` |
| `DELETE /bff/admin/authz/attributes/{id}` | 204 / 404 / **409（参照中。RFC7807 の `detail` に参照元ポリシー名）** | `:65-67` ＋ 後段 `:159-180` |

**409 の本文は `Results.Problem(title, detail, 409)`** であり、`detail` が
「属性 '<key>' (scope=<scope>) は次のポリシーが参照しているため削除できません: <ポリシー名, …>」である
（`AuthzEndpoints.cs:171-175`）。**この本文形式を OpenAPI に書く**——SC-09 の表示がこれに依存している
（[[IADR-0040]] 決定 2）。

#### `/bff/admin/config/history`（SC-11・FR-15）

| メソッド・パス | 認可 | 応答 | 出所 |
| --- | --- | --- | --- |
| `GET /bff/admin/config/history` | **`ConfigViewer`**（admin または operator） | 200 `ConfigVersionEntryDto[]` / **404（非権限。無認証を含む）** | `ConfigBffEndpoints.cs:58-72` |

**`/bff/admin/config` 群は `RequireAuthorization` を使わない。** ハンドラ内で
`IAuthorizationService.AuthorizeAsync(user, ConfigViewer)` を評価し、**失敗をすべて 404 へ寄せる**
（`:102-117`）。**403 は返らない**——`RequireAuthorization` を付けると無認証が 404 到達前に 401 で
短絡して存在が漏れるため、意図的に避けている（`:12-14` のコメント）。既存 2 パスの記述
（`openapi.yaml:213`「401/403 は返さない」）と同じ扱いで書く。

### 7. 追加するスキーマ

| スキーマ | 出所（C#） | 備考 |
| --- | --- | --- |
| `DocumentDto` | `Knowledge.Contracts/Dtos/DocumentDto.cs` | `status` は `draft`/`normalized`/`published`/`archived` |
| `DocumentContentDto` | 同上（`record`） | `id` / `title` / `markdown` / `sourceUri` |
| `DocumentVersionDto` | 同上 | 版スナップショット |
| `DataSourceDto` | `Knowledge.Contracts/Dtos/DataSourceDto.cs` | `config` は**秘密キーがマスク済み**（`***`。IADR-0053） |
| `CreateDataSourceRequest` | 同上 | |
| `DataSourceSyncResultDto` | `DataSourceEndpoints.cs:61-66`（匿名型） | 契約 record が無いため**匿名型の形をそのまま写す** |
| `ConversionJobDto` | `Knowledge.Contracts/Dtos/ConversionJobDto.cs` | `status` は 4 値 |
| `CitationDto` | `Knowledge.Contracts/Dtos/SearchResultDto.cs` | §3 |
| `AbacPolicyDto` / `AttributeDefinitionDto` | `Platform.Shared.Contracts/Dtos/AbacManagementDto.cs` | |
| `CreatePolicyRequest` / `CreateAttributeRequest` / `UpdateAttributeRequest` / `SetActiveRequest` | `AuthzEndpoints.cs:201-220` | BFF は本文を**素通し**するため後段の要求型がそのまま契約になる |
| `ConfigVersionEntryDto` | `Platform.Shared.Contracts/Dtos/ConfigInfoDto.cs` | |

**`/bff/documents` の書き込み本文は既存の `CreateDocumentRequest` / `UpdateDocumentRequest` を再利用する。**
BFF の record（`DocumentCreateRequest` / `DocumentUpdateRequest`）は
「DocumentService の要求と **JSON 互換**」と宣言されており（`DocumentBffEndpoints.cs:220`）、
フィールドは 1 対 1 で一致する。**同じ形の型を 2 つ生成させない。**

## 受け入れ基準

- [ ] **`grep -c '^  /bff' docs/api/openapi.yaml` が 8 → 27 になる**（`/bff/analysis/ask/stream` を含む）。
- [ ] **追加した全パスが実装と突合されている**（§6 の出所欄がすべて埋まり、行番号で辿れる）。
- [ ] **issue が名指しした 4 件が落ちていない**——属性削除の 409（参照元ポリシー名つき）／
      再変換の 409（本文なし）／文書更新の 409（版競合）／`/bff/admin/config` 系の **404 のみ（403 なし）**。
- [ ] **`AiAnswerDto.citations` が `CitationDto[]` である**。`answerId` が在る。
- [ ] **`pnpm run codegen` の後に `git diff --exit-code -- platform/frontend/src/foundation/api/generated` が差分なし。**
- [ ] **SSE が「意図的な対象外」と読める**（OpenAPI と `docs/api/BFF_bff-surface.md` の両方）。
- [ ] **画面の挙動が変わっていない**——既存テストが**無改修で**全 green。
      とくに #502〜#504 が積み上げた次が通り続ける: 存在秘匿の markup 一致／403・404 の中立化と
      5xx の `role="alert"` の区別／SC-09 の 409 の Problem 詳細／`beginOperation()` の直近結果表示。
- [ ] **変異試験**（OpenAPI を壊すと型検査が落ちること）を実測し、結果を表で残す。**素通りは必ず書く。**
- [ ] `pnpm run typecheck` / `lint` / `test` / `test:coverage` / `build` / `test:e2e` が green。
- [ ] `node scripts/check-doc-links.js` / `check-commit-messages.js --base origin/develop` /
      `check-unit-dependencies.js` / `check-test-traceability.js` / `check-contract-schema.js` /
      `check-test-spec-coverage.js` / `check-i18n-catalogs.js` /
      `check-static-egress.js --require src/platform/frontend/dist` が green。
- [ ] **カバレッジ床を割らない。**

## テスト方針

**本作業はプロダクションコードの振る舞いを変えない**（宣言の追加と、型注釈 1 か所の差し替えのみ）。
したがって**新規のテストは足さない**——足せば「宣言を書いたこと」を宣言で確かめるだけの
トートロジーになる。代わりに次の 2 つで担保する。

| 手段 | 見るもの |
| --- | --- |
| **生成物の再生成差分検査**（CI） | OpenAPI と生成物の乖離 |
| **変異試験**（本書 §検証） | 「OpenAPI を壊すと型検査が落ちる」——**生成フックに載っている面についてのみ成立する**（現時点では SC-08 だけ） |

**この非対称は隠さない。** 1 本目の時点で網が掛かるのは SC-08 の 1 画面だけであり、
**残り 9 ファイルは 2 本目（#519）まで素通りのままである**（母集合 10 ファイル − SC-08 の 1 ファイル）。

## 残りとして何をどうするか（分割 2 本目 = #519 への申し送り）

**対象は 9 ファイル**（`src/knowledge/frontend/src/features/sc*/use*.ts` のうち、
すでに生成フックに載っている `sc08-analysis/useAnalysisTask.ts` を除く 8 ファイル ＋
`sc01-search/useAskStream.ts` の**フィードバック送信部分だけ**）。

| # | ファイル | 載せ替える呼び出し | 使う生成フック（本作業で生成される） | 注意 |
| --- | --- | --- | --- | --- |
| 1 | `sc01-search/useAskStream.ts` | `apiFetch('/feedback')` のみ | `useBffSubmitFeedback` | **`apiStream` は載せ替えない**（SSE。恒久的に対象外） |
| 2 | `sc02-results/useSearchQuery.ts` | `apiFetch('/search')` | `useBffSearch` | |
| 3 | `sc03-document/useDocumentQueries.ts` | 3 本 | `useBffDocumentDetail` / `useBffDocumentContent` / `useBffDocumentVersions` | **版履歴は詳細の成功後にだけ有効化**（[[IADR-0126]] 決定 4）。`enabled` を生成フックの `query` オプションで渡す |
| 4 | `sc05-documents/useDocumentAdmin.ts` | 5 本 | `useBffDocumentList` ほか | 状態遷移の `publish` / `archive` / `delete` は**別フック**になる（現状は 1 つの `useMutation` が分岐している）。**分岐を残すなら生成フックを 3 つ束ねる薄い層が要る** |
| 5 | `sc06-datasources/useDataSources.ts` | 4 本 | `useBffDataSourceList` ほか | |
| 6 | `sc07-conversions/useConversionJobs.ts` | 2 本 | `useBffConversionJobList` / `useBffConversionJobRetry` | 一覧は `?status=` をクエリパラメータで受ける形に変わる |
| 7 | `sc09-admin-abac/useAbacAdmin.ts` | 7 本 | `useBffAuthzListPolicies` ほか | **409 の Problem 詳細（参照元ポリシー名）が `ApiError.details` に載り続けること**を確かめる |
| 8 | `sc10-operations/useDashboardSummary.ts` | 1 本 | `useBffDashboardSummary` | **本作業より前から生成フックが在った**（実測 4）。コメントの是正も同時に行う |
| 9 | `sc11-config/useConfigViewer.ts` | 3 本 | `useBffConfigEffective` / `useBffConfigDrift` / `useBffConfigHistory` | 同上。**404 秘匿の扱いを変えない** |

**共通の注意（載せ替えで必ず踏むもの）**:

1. **応答が包まれる**。生成フックの `data` は `{ data, status, headers }` である
   （`orvalMutator.ts` の `OrvalResponse`）。画面は `data.data` を読むことになる。
   SC-08 は `mutation.data?.status === 200 ? mutation.data.data : undefined` の形で吸収している。
2. **エラーの投げ方は変わらない**。非 2xx は `apiRequest` が `ApiError` を投げる
   （`bffFetch` は `apiRequest` を通る）。**403/404 の中立化と 5xx の `role="alert"` の区別は保たれる。**
3. **キャッシュキーが変わる**。生成フックのキーは `` [`/bff/…`] `` であり、現状の
   `['bff','documents',id]` 等とは別物である。**`invalidateQueries` の対象を漏れなく差し替える**
   （[[IADR-0127]] 決定 5 の「invalidate だけを行う」作法は維持する）。
   `useConfigViewer.ts` の「1 回の無効化で 3 本とも当たる」前方一致の性質は
   **生成キーでは成立しない**（`/bff/admin/config` と `/bff/admin/config/drift` は別配列だが、
   TanStack Query の前方一致は要素単位のため `['/bff/admin/config']` は `['/bff/admin/config/drift']` に
   当たらない）。**ここは明示的に 3 本無効化する必要がある。**
4. **MSW モックが生成される**。`*.msw.ts` を使うとテストの fixture が OpenAPI 由来になる
   （「fixture が実応答の形を再現していない」死角が減る）。
5. **変異試験は 2 本目でこそ意味を持つ**——載せ替えた各画面について、対応する DTO のフィールド名を
   変える／消すと `pnpm run typecheck` が落ちることを実測する。

## 検証（実測）

**測定条件**: worktree `chore/SC-03-11-openapi-bff-groups`（`origin/develop` `68d91ce` 基点）／
Node 22.22.2 ／ pnpm 10.33.0 ／ Vitest 3.2.7（v8 provider）／orval 8.23.0 ／
**submodule `src/ai-stock-trading` と `planning` は populate 済み**。
スコープは断りがない限り**ワークスペース全体**（`src/` の 4 パッケージ ＋ AST）である。

| 検査 | コマンド | 結果 |
| --- | --- | --- |
| 型検査 | `pnpm run typecheck` | green（4 パッケージ。AST は**無改修**） |
| lint | `pnpm run lint` | green（**0 errors / 9 warnings**。warning は全件 `react-refresh/only-export-components` で、本作業の着手前と同数） |
| 単体テスト | `pnpm run test` | **57 files / 539 tests** 全 green。**着手前と同数**——本作業はテストを 1 件も足しておらず、既存テストを 1 件も改変していない |
| カバレッジ | `pnpm run test:coverage` | lines/statements **95.93%** ／ branches **89.77%** ／ functions **91.83%**。床（90 / 90 / 88 / 85）を満たす。**着手前と同値**（生成物は `coverage.exclude` 済みで母数に入らず、計測対象の実装は型注釈 1 行しか変えていない） |
| ビルド | `pnpm run build` | green（`dist/assets/index-Bw-dS6vy.js` 632.98 kB / gzip 190.04 kB） |
| E2E | Playwright（後述の条件） | **12 tests 全 green**（本作業で増減なし） |
| 生成物の乖離 | `pnpm run codegen` ＋ `git diff --exit-code -- …/generated` | green（差分なし） |
| i18n カタログ | `pnpm run i18n` ＋ `node scripts/check-i18n-catalogs.js` | green（2 ロケール・未翻訳 0 件。**本作業でカタログは 1 件も増減していない**——表示文言を足していないため） |
| ドキュメントリンク | `node scripts/check-doc-links.js` | green（424 件） |
| ユニット依存方向 | `node scripts/check-unit-dependencies.js` | green |
| テスト・トレーサビリティ | `node scripts/check-test-traceability.js` | green（仕様書のある 28 件中 28 件が写像済み。**allowlist は着手前と同じ 7 件**＝増やしていない） |
| 契約スキーマ | `node scripts/check-contract-schema.js` | green（2 プロジェクト / 20 ファイル / 56 型が baseline と一致。**C# を触っていないため当然だが、変異試験 M3 で発火も確認した**） |
| テスト仕様書の被覆 | `node scripts/check-test-spec-coverage.js` | green（テストクラス 107 件のうち 63 件が仕様書 29 件から参照済み。床 68 と一致。**床は動かしていない**——バックエンドテストを足していないため `--update` は不要） |
| 静的 egress | `node scripts/check-static-egress.js --require src/platform/frontend/dist` | green（4 ファイル・検出 0 件） |
| スクリプト自己試験 | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | green（**247 tests**） |
| コミット件名 | `node scripts/check-commit-messages.js --base origin/develop` | green（**件数はここに書かない**——この表を直すコミット自身が件数を変えるため） |

**E2E の実行条件**: この環境では `playwright install` がブラウザを取得できない。導入済みの
`/opt/pw-browsers/chromium-1194/chrome-linux/chrome` を `launchOptions.executablePath` で指す
**ローカル専用 config を一時的に置いて実走し、確認後に削除した**（#490 / #496 / #502〜#504 と同じ作法）。
**リポジトリの `platform/frontend/playwright.config.ts` は無改変であり、作業ツリーは clean である。**

### 受け入れ基準 1: 追加したパスの数と実装との突合

```console
$ grep -c '^  /bff' docs/api/openapi.yaml
27
```

**8 → 27**（＋19）。内訳は `/bff/analysis/ask/stream` 1 ＋ `/bff/admin/config/history` 1 ＋
`/bff/admin/authz/*` 5 ＋ `/bff/documents*` 6 ＋ `/bff/datasources*` 3 ＋ `/bff/conversion/jobs*` 3。
**数え方の基準はトップレベル `paths` 直下（インデント 2）のキーである**——1 パスに複数メソッドが
載るため、**操作の数ではない**（`/bff` 配下の操作は **38** で、うち **37** が生成される——SSE の 1 本だけが
生成の入力から落ちる）。

突合の手順は §設計 1 のとおり「BFF 実装 → 後段実装 → 契約 DTO」の 3 段で、
§設計 6 の表の**出所欄がすべて行番号つきで埋まっている**（推測で書いた行は無い）。

### 受け入れ基準 2: issue が名指しした 4 件

| # | 事項 | 書いた場所 | 実装の出所 |
| --- | --- | --- | --- |
| 1 | 属性削除の **409**（参照中。Problem 本文に参照元ポリシー名） | `DELETE /bff/admin/authz/attributes/{id}` の `"409"`（`application/problem+json` の `ProblemDetails`。`detail` の文面を description に写した） | `AuthzEndpoints.cs:171-175` |
| 2 | 再変換の **409**（`processing` 中の拒否） | `POST /bff/conversion/jobs/{id}/retry` の `"409"`（**本文なし**） | `ConversionJobEndpoints.cs:37-42` ＋ **`ConversionBffEndpoints.cs:70` が本文を落とす** |
| 3 | 文書更新の**楽観ロック** | `PUT /bff/documents/{id}` の `"409"`（`VersionConflictDto`。**RFC7807 ではない素の JSON**） | `DocumentEndpoints.cs:84-90` |
| 4 | `/bff/admin/config` の**権限外応答** | `GET /bff/admin/config/history` の応答は **200 / 404 のみ**（401 も 403 も書いていない） | `ConfigBffEndpoints.cs:102-117`。`RequireAuthorization` を**使わず**ハンドラ内で `ConfigViewer` を評価し、無認証を含む非権限をすべて 404 へ寄せる |

**2 は実装を読まなければ確実に間違える。** 後段は `{ "error": "not_retryable", "status": … }` を返すが、
BFF は `Results.StatusCode((int)resp.StatusCode)` でステータスだけを中継するため**本文は届かない**。
issue の文面（「409 `not_retryable`」）をそのまま契約へ書くと、**実在しない本文の型が生成される**。

### 受け入れ基準 3: 画面の挙動を変えていない

- **既存テストを 1 件も改変していない**（57 files / 539 tests が**無改修で** green）。
  #502〜#504 が積み上げた次の 4 系統がそのまま通り続けている——
  存在秘匿の markup 一致（`sc11-config/access.test.tsx` ほか）／403・404 の中立化と 5xx の
  `role="alert"` の区別／SC-09 の 409 の Problem 詳細／`beginOperation()` による直近の操作結果の表示。
- **プロダクションコードの変更は SC-08 の型注釈 1 行と import 1 行だけ**である
  （`CitationLink` の引数型 `SearchResultDto` → `CitationDto`）。DOM も文言も分岐も変えていない。

### 変異試験（「壊すと落ちる」ことの実測）

**件数の基準**: 下表は **M1〜M6 の 6 件**である（M2 と M2b は**同じ変異を強化の前後で 2 回当てた**もので、
変異としては 1 件だが、**強化の有無で結果が反転したので 2 行に分けて残す**）。
手順は「変異を当てる → `pnpm run codegen` → `pnpm run typecheck`（必要なら `test` / 差分検査）→ 必ず復元」。

| # | 壊した箇所 | 期待 | 実測 |
| --- | --- | --- | --- |
| M1 | **OpenAPI** の `CitationDto.documentTitle` を `documentTitleRenamed` へ改名 | 落ちる | **落ちた**。`typecheck exit=2` / `AnalysisDashboardPage.tsx(230,28): error TS2551: Property 'documentTitle' does not exist on type 'CitationDto'` ほか計 2 件 |
| **M2** | **OpenAPI** の `AiAnswerDto.citations` を旧記載（`SearchResultDto[]`）へ戻す | 落ちる | **素通りした**（`typecheck exit=0`）。原因は後述 |
| M2b | 同じ変異を、**新規スキーマへ `required` を入れた後**に当てる | 落ちる | **落ちた**。`error TS2739: Type 'SearchResultDto' is missing the following properties from type 'CitationDto': number, snippet` |
| M3 | **C# の DTO**（`CitationDto.DocumentTitle`）を改名 | — | **型検査は素通りした**（`typecheck exit=0`）。**`check-contract-schema.js` は落ちた**（exit=1・破壊的 2 件） |
| M4 | `orval-bff-only.cjs` の **SSE 除外を外す** | 生成物差分が出る | **出た**。`useBffAnalysisAskStream` が生成物へ現れ `git diff --exit-code -- …/generated` が exit=1 |
| M5 | **OpenAPI** の `ConversionJobDto.attempts` を削除 | — | **素通りした**（`typecheck exit=0` / `test` 539 全 green）。SC-07 は手書き型のままだから |
| M6 | **OpenAPI** の `AiAnswerDto.answerId` を削除 | — | **素通りした**（`typecheck exit=0`）。SC-01 は SSE の `done` イベントを手書き型で読むから |

#### M2 が素通りした原因と、その是正（**本作業で最も重要な発見**）

**orval は `required` の無いスキーマの全プロパティを省略可（`?`）として生成する。**
着手時点で `openapi.yaml` の `components.schemas` には **`required` を持つ「応答」スキーマが 1 つも無かった**
（`required` が在ったのは `SearchRequest` / `AskRequest` など**要求**スキーマだけである）。
その結果 `SearchResultDto` も `CitationDto` も**全フィールドが省略可**になり、TypeScript の構造的部分型では
**片方をもう片方へそのまま代入できてしまう**。「契約に載せれば型検査が守る」という前提が、
**載せ方によっては成立しない。**

**是正**: 本作業で**追加した応答スキーマ 11 個に `required` を入れた**（値は C# の**非 null メンバー**から
起こした。`string?` / `Guid?` / `DateTimeOffset?` は入れない）。M2b がそれで落ちることを実測した。

**追加した 20 スキーマの内訳**（数え方 = `components.schemas` 直下のキー）: 応答 11（`required` あり）／
要求 5（`required` あり）／**意図的に `required` を持たない 4**（`AbacConditionMap` は写像そのもの、
`ConfigVersionEntryDto` は C# の 4 メンバーすべてが nullable、`ProblemDetails` / `ValidationProblemDetails` は
RFC7807 でどのフィールドも省略され得る）。

**既存スキーマ（`SearchResultDto` / `AiAnswerDto` / `DashboardSummaryDto` ほか）には手を入れていない。**
入れると SC-08 の `answer.inputTokens ?? 0` のような既存表現の意味が変わり、
**「宣言の追加」という本作業の性質と混ざる**ためである。**新旧で厳しさが揃っていないことは
自覚した差異である**（§未決事項・親への申し送り 2 = **#520**）。

#### 素通りしたもの（**3 件。隠さない**）

| # | 素通りした事象 | いま網が無い理由 | 引き受け先 |
| --- | --- | --- | --- |
| M3 | **C# の DTO を変えても SPA の型検査は落ちない** | `openapi.yaml` は**手書き**であり、C# → OpenAPI の追随が人手だから（実測 5） | [[IADR-0131]] フォローアップ 2。**本 issue の射程外** |
| M5 | 新しく載せた DTO のフィールドを消しても落ちない | **その画面がまだ生成フックに載っていない**（SC-07 は手書き型） | **分割 2 本目**。1 本目の時点で網が掛かるのは SC-08 だけ |
| M6 | `answerId` を消しても落ちない | SC-01 は SSE の `done` イベントを `apiStream` ＋ 手書き型で読む。**SSE は恒久的に生成対象外**なので、ここは 2 本目でも網が掛からない | **恒久。網は SSE イベントの手書き型に対する単体テストで代替する**（既存 `useAskStream` のテストが担う） |

**M3 について補足**: C# 側には検査が在り（`check-contract-schema.js`）、実際に落ちた。
**欠けているのは C# 契約と OpenAPI を突き合わせる辺だけ**である。しかも
**非破壊な追加（既定値付きのメンバー）では `--update` が黙って baseline を更新できる**ため、
「C# にフィールドを足して OpenAPI へ書き忘れる」は**どの検査にも掛からない**。

## 未決事項・親への申し送り

| # | 事項 | 種別 | 送り先 |
| --- | --- | --- | --- |
| 1 | **画面の載せ替え（分割 2 本目）** | 本 issue の残り | **#519**（起票済み）。対象・手順は §残りとして何をどうするか（9 ファイル・共通の注意 5 点）。**#520 を先に通す方が手戻りが少ない**（同じ生成型を触るため競合し、`required` の有無で生成される型の省略可否が変わる） |
| 2 | **既存の応答スキーマ 23 個に `required` が無い**（数え方 = `components.schemas` 直下で `required` を持たないキー 27 個から、本作業で追加した 4 個を引いた数） | 是正提案 | 変異試験 M2 が示したとおり、`required` の無いスキーマは型検査の網にならない。入れるかは**別 PR = #520**（起票済み）。影響が `?? 既定値` の表現に及び、載せ替え〔2 本目 = #519〕とも競合する——**同じ生成型を両方が触るため、#520 を先に入れる方が手戻りが少ない** |
| 3 | **C# → OpenAPI の追随が人手** | 構造的な穴 | [[IADR-0131]] 決定 1 の但し書き・フォローアップ 2。透過中継の応答を覆える方式が要る |
| 4 | 既存 2 本の `operationId` 不統一（`analysis-ask` / `analysis-analyze`） | 小さな是正 | 2 本目で `useAnalysisAnalyze` に触るついでが最も安い |
| 5 | **BFF のコメントが後段の実体と食い違う**: `DataSourceBffEndpoints.cs:80` は同期応答を `{ fetchId, status }` と書くが、実体は `{ fetched, failed, connectorAvailable, message }`（`DataSourceEndpoints.cs:61-66`） | コメントの誤り | 2 本目、または独立の小さな fix |
| 6 | **フロントのコメントが「OpenAPI に無い」と書いているが在る**: `useDashboardSummary.ts:10` / `useConfigViewer.ts:9`（`/bff/admin/config` の**履歴だけ**が無かった） | コメントの誤り | **2 本目**（同じ行を載せ替えで書き換えるため、いま直すと二度手間） |
| 7 | **`/bff/feedback`・`/bff/feedback/stats` に端点認可が無い** | 要裁定 | **#521**（起票済み）。通信仕様書 §未決事項 3。**本作業では判断しない**（認可の変更は挙動の変更） |
| 8 | **ワークフロー変更は不要**（`.github/workflows/` を触っていない） | 情報 | `frontend.yml` の `paths` に `docs/api/openapi.yaml` が既に入っており、契約変更で CI が起動する |
