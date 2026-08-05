---
title: 既存の応答スキーマへ required を入れ、生成型を必須化する
type: spec
status: done
related_ids: [SC-01, SC-02, SC-03, SC-05, SC-06, SC-07, SC-08, SC-09, SC-10, SC-11, UC-01, UC-02, UC-05, UC-06, FR-03, FR-04, FR-08, FR-09, FR-10, FR-11, FR-15, NFR, ADR-0031, IADR-0121, IADR-0122, IADR-0131, IADR-0132]
author: Claude
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/05_screens/01_screens.md"
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md"
related_specs:
  - ../api/BFF_bff-surface.md
  - ../adr/IADR-0131_openapi-as-bff-contract-source.md
  - ../adr/IADR-0132_openapi-required-from-csharp-nullability.md
  - ../adr/IADR-0122_contract-schema-source-and-compat-gate.md
  - ../adr/IADR-0121_spa-stack-migration-staging.md
  - ./20260805_issue-506_openapi-bff-groups.md
---

# 仕様書: 既存の応答スキーマへ `required` を入れる（#520）

> 本仕様書は実装着手前に作成した。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- **NFR**（非機能要件・保守性）: 契約変更が型検査で捕まること。**本 issue の主たる起点はこれである。**
- 画面（SC）: SC-01 / SC-02 / SC-03 / SC-05〜SC-11（生成型を読む／今後読む画面）
- ユースケース（UC）: UC-01 / UC-02 / UC-05 / UC-06
- 機能要求（FR）: FR-03 / FR-04 / FR-08 / FR-09 / FR-10 / FR-11 / FR-15
- 関連 ADR（計画）:
  [ADR-0031](../../planning/projects/microservices-platform/07_adr/ADR-0031_frontend-stack.md)（Accepted。
  BFF 境界と生成クライアント）
- 関連 IADR: **[[IADR-0131]]（本作業は同 ADR フォローアップ 1 の前提／作業仕様書 #506 §未決事項 2 の消化）**・
  **[[IADR-0132]]（本作業の内部設計判断。本書と対で読む）**・[[IADR-0122]]（契約スキーマの正本）・
  [[IADR-0121]] 決定 3（BFF 境界・手書き HTTP クライアント禁止）
- 本リポジトリの起点: **#520**（親 #454。出所は[作業仕様書 #506](./20260805_issue-506_openapi-bff-groups.md)
  ＝ PR #518 が書いたもの——の §未決事項 2・変異試験 M2 / M2b）

## 目的・背景

**orval は `required` の無いスキーマの全プロパティを省略可（`?`）で生成する。**
PR #518 の変異試験 M2 は、この性質のせいで `AiAnswerDto.citations` を誤った型（`SearchResultDto[]`）へ
戻しても **TypeScript の構造的部分型で typecheck が素通りする**（exit=0）ことを実測した。
同 PR は**追加した 11 個**の応答スキーマにだけ `required` を入れ、同じ変異が
`TS2739: missing properties: number, snippet` で落ちること（M2b）を実測している。

**残る既存の応答スキーマは手つかず**であり、その範囲では
「OpenAPI を変えると SPA の型検査が落ちる」（[[IADR-0131]] 決定 1 が与えると宣言した保証）が効かない。
本作業はこの非対称を解消する。

## 着手時の実測（issue の「23 個」を鵜呑みにしない）

### 実測 1: 母集合と数え方（**再現可能なコマンド**）

母集合は `docs/api/openapi.yaml` の `components.schemas` のうち、**応答（`responses`）から
`$ref` で到達できる**もの（推移閉包）である。`requestBody` からしか到達しないスキーマは対象外。

```console
$ python3 - docs/api/openapi.yaml <<'PY'
import sys, yaml
spec = yaml.safe_load(open(sys.argv[1])); S = spec['components']['schemas']
M = ['get','put','post','delete','options','head','patch','trace']
def refs(n, o):
    if isinstance(n, dict):
        for k, v in n.items():
            if k == '$ref' and isinstance(v, str) and v.startswith('#/components/schemas/'): o.add(v.rsplit('/', 1)[1])
            else: refs(v, o)
    elif isinstance(n, list):
        for v in n: refs(v, o)
def closure(seed):
    seen = set(seed); st = list(seed)
    while st:
        x = st.pop()
        if x not in S: continue
        o = set(); refs(S[x], o)
        for m in o - seen: seen.add(m); st.append(m)
    return seen & set(S)
def side(part):
    s = set()
    for _, item in spec['paths'].items():
        for m in M:
            if item.get(m): refs(item[m].get(part) or {}, s)
    return closure(s)
resp, req = side('responses'), side('requestBody')
tgt = [n for n in S if n in resp and 'required' not in (S[n] or {})]
print('total=%d resp=%d req=%d resp_with_required=%d TARGET=%d both=%s'
      % (len(S), len(resp), len(req), len(resp) - len(tgt), len(tgt), sorted(resp & req)))
print(' '.join(tgt))
PY
total=53 resp=36 req=18 resp_with_required=11 TARGET=25 both=['AbacConditionMap']
SearchResponse SearchResultDto AiAnswerDto AccessScopeResponse AbacConditionMap ProblemDetails
ValidationProblemDetails FeedbackDto FeedbackStatsDto UsageEventCreatedDto UsagePointDto SearchTrendDto
DashboardUsageDto DashboardSummaryDto CompletionApiResponse EffectiveConfigDto ConfigVersionDto
ConfigVersionEntryDto PipelineStageDto EventBindingDto PortSelectionDto ConnectorDto DriftReportDto
DriftFindingDto EmbedApiResponse
```

（最後の 1 行は実際には 1 行で出力される。ここでは可読性のため折り返した。）

**母集合は 25 個であり、issue の「23 個」ではない。** 数え直した結果である。

### 実測 2: issue の「23」との差分（**どちらが誤りでもない。数え方が違う**）

issue の 23 は[作業仕様書 #506](./20260805_issue-506_openapi-bff-groups.md)（PR #518 が書いた）
§未決事項 2 の定義、すなわち
「`components.schemas` 直下で `required` を持たないキー **27 個**から、#518 が追加した
**意図的に `required` を持たない 4 個**を引いた数」である。

```console
$ python3 -c "
import yaml
S=yaml.safe_load(open('docs/api/openapi.yaml'))['components']['schemas']
no=[n for n in S if 'required' not in (S[n] or {})]
print('schemas without required =', len(no)); print(' '.join(no))
"
schemas without required = 27
SearchResponse SearchResultDto AnalysisDataRange AiAnswerDto AccessScopeResponse UpdateMetadataRequest
AbacConditionMap ProblemDetails ValidationProblemDetails FeedbackDto FeedbackStatsDto UsageEventCreatedDto
UsagePointDto SearchTrendDto DashboardUsageDto DashboardSummaryDto CompletionApiResponse EffectiveConfigDto
ConfigVersionDto ConfigVersionEntryDto PipelineStageDto EventBindingDto PortSelectionDto ConnectorDto
DriftReportDto DriftFindingDto EmbedApiResponse
```

| | 集合 | 個数 |
| --- | --- | --- |
| a | `required` を持たない全スキーマ | **27** |
| b | a のうち **要求専用**（`AnalysisDataRange` / `UpdateMetadataRequest`） | **2** |
| c | a のうち **#518 が意図的に `required` を持たせなかった 4 個**（`AbacConditionMap` / `ConfigVersionEntryDto` / `ProblemDetails` / `ValidationProblemDetails`） | **4** |
| **issue の 23** | a − c | 23 |
| **本作業の母集合 25** | a − b | **25** |

**本作業は a − b（＝応答に使われるもの全部）を母集合に採り、c の 4 個は「検討したうえで
`required` を入れない」判断を明示的に下す**（§設計 2）。issue の 23 は c を先に引いてしまっており、
「なぜ入れないか」を本 PR で再確認できないため採らない。

### 実測 3: 「両方に使われるスキーマ」（**最も事故りやすい箇所**）

上記コマンドの `both=['AbacConditionMap']` が答えである。**応答と要求の両方に使われるスキーマは
`AbacConditionMap` の 1 個だけ**で、それは `properties` を持たない写像（`additionalProperties` のみ）である。

```yaml
AbacConditionMap:
  type: object
  additionalProperties: { type: array, items: { type: string } }
```

**`required` は `properties` のキーを指すため、この形には適用できない**（書いても意味を持たない）。
したがって**本作業に「要求側の必須化が巻き添えで起こる」箇所は 1 つも無い。**
残る 24 個はすべて応答専用である。

### 実測 4: 生成物へ効く範囲（**着手時の想定が実測で覆った**）

**［着手時の想定］** `orval-bff-only.cjs` は入力から `/bff/` 以外のパスを落とす（[[IADR-0121]] 決定 3）。
そこで「母集合 25 個のうち `/bff/` の応答から到達する 20 個だけが生成され、残る 5 個
（`AccessScopeResponse` / `UsageEventCreatedDto` / `DashboardUsageDto` / `CompletionApiResponse` /
`EmbedApiResponse`）は生成物に現れない」と想定していた。

**［実測］この想定は誤りだった。** 前処理が落とすのは **`paths` だけ**で、`components.schemas` は
そのまま渡る。**53 スキーマすべてが `bff.schemas.ts` に出力されている。**

```console
$ grep -c '^export interface ' src/platform/frontend/src/foundation/api/generated/bff.schemas.ts
53
$ python3 -c "
import yaml, re
S=yaml.safe_load(open('docs/api/openapi.yaml'))['components']['schemas']
gen={m.group(1) for m in (re.match(r'export interface (\w+)', l)
     for l in open('src/platform/frontend/src/foundation/api/generated/bff.schemas.ts')) if m}
print('openapi schemas:', len(S)); print('missing from generated:', sorted(set(S)-gen))
"
openapi schemas: 53
missing from generated: []
```

**気付いた経路も実測である**——変異試験 M8（`EmbedApiResponse.model` を削除）で
**生成物に差分が出た**ことから、「生成されない」という想定が崩れた（§変異試験 M8）。

**したがって網の有無を決めるのは「生成されるか」ではなく「その型を画面が読んでいるか」である。**

| 区分 | スキーマ | 型検査の網 |
| --- | --- | --- |
| 画面が読んでいる | `AiAnswerDto`（＋ #518 の `CitationDto`） | **効く**（SC-08） |
| 生成されるが誰も読んでいない | 残り 24 個 | **効かない**（#519 で載せ替えたら効く） |

**5 個（`/bff/` から到達しない分）にも `required` を入れる。** 契約ファイルは SPA だけのものではなく、
「C# の非 null 性を写した記述」という一貫性を面ごとに崩さないほうが後任が読み違えない。
**ただし型検査では落ちないので、素通りとして表に残す**（§変異試験 M8）。

### 実測 5: 生成型を実際に読んでいる画面は 1 つだけ

```console
$ grep -rln "generated" --include=*.ts --include=*.tsx knowledge/frontend/src platform/frontend/src \
    | grep -v 'src/foundation/api/generated/'
knowledge/frontend/src/features/sc08-analysis/useAnalysisTask.ts
knowledge/frontend/src/features/sc08-analysis/analysisRange.ts
knowledge/frontend/src/features/sc08-analysis/AnalysisDashboardPage.tsx
platform/frontend/src/foundation/api/orvalMutator.test.ts
platform/frontend/src/foundation/i18n/locales/en/messages.ts
```

（`orvalMutator.test.ts` と `messages.ts` は本文中に "generated" の語が出るだけで、生成物を import
していない。生成型を import しているのは **SC-08 の 3 ファイル**だけである。）

**したがって既存コードへの波及は SC-08 に閉じる。** これは「#519 より先に入れる方が手戻りが少ない」
という issue の順序判断（[[IADR-0131]] フォローアップ 1）が正しいことの裏付けでもある
——#519 が 9 ファイルを載せ替えた後だと、波及先が 10 ファイルに広がる。

## 対象範囲

### 対象

1. **`docs/api/openapi.yaml` の応答スキーマ 25 個**（実測 1）について、`required` を入れるか否かを
   **C# の非 null 性から**決め、入れる分を書く。**推測で決めた行は無い**——§設計 1 の表のうち
   **22 行は `ファイル:行` で出所を示し、残り 3 行は「C# に出所が無い」ことを明記する**
   （`AbacConditionMap` = 写像そのもの／`ProblemDetails`・`ValidationProblemDetails` = RFC7807 で
   対応する C# `record` が存在しない）。
2. **`pnpm run codegen`** で生成物（`platform/frontend/src/foundation/api/generated/`）を更新しコミットする。
3. 既存コードが壊れないことを確認する。**`?? 既定値` の扱いは方針を決めて [[IADR-0132]] へ残す**（§設計 3）。
4. **変異試験**で「フィールドを消す／改名すると型検査が落ちる」ことを実測する（§変異試験）。
5. **消化の記録を 2 か所へ入れる**（日付つき［追記］）。**§未決事項という節を持つのは
   [作業仕様書 #506](./20260805_issue-506_openapi-bff-groups.md) の方であり、[[IADR-0131]] には無い**
   ——両者を取り違えると「消化した」と書いた先に消化対象が無い状態になる。
   - **[作業仕様書 #506](./20260805_issue-506_openapi-bff-groups.md) §未決事項 2**（＝消化対象の本体）。
   - **[[IADR-0131]] §結果 > フォローアップ 1**（＝本作業はその**前提**を消化する。
     項番 1 本体である画面の載せ替え = #519 は未消化のまま残る）。
6. 通信仕様書（`docs/api/BFF_bff-surface.md`）へ **`required` の扱い（横断の規約 5）** を追記する。

### 対象外（送り先を明記する）

| 事項 | 送り先 | 理由 |
| --- | --- | --- |
| **画面の生成フックへの載せ替え（9 ファイル）** | **#519** | 本 issue は「型を厳しくする」だけで、呼び出し側は変えない |
| **`AccessScopeResponse` に `granted` が欠けている**（C# には在る。§設計 1 の脚注） | **§未決事項 1** | フィールドの追加は「`required` を入れる」作業と別種の是正であり、混ぜるとレビュー単位が壊れる |
| **要求スキーマの `required` 見直し**（`AnalysisDataRange` / `UpdateMetadataRequest`） | やらない（本 issue の範囲外） | 応答スキーマの話である。要求側の必須化は**画面の送信コードを壊す**ので独立に扱うべき |
| **C# → OpenAPI の追随の機械化** | [[IADR-0131]] フォローアップ 2 | 本 issue の射程外。実測は #506 §実測 5 |
| テストの fixture（`ANSWER` 等）を生成型で型付けする | **§未決事項 2** | 現状 fixture は `unknown` を経由するため型検査に掛からない。掛けると別の設計判断が要る |

## 設計

内部設計の判断（`required` の起こし方・`?? 既定値` の扱い）は [[IADR-0132]] を正とする。
本節は**何をどう書くか**を確定する。

### 1. `required` は C# の非 null 性から起こす（**25 個の突合表**）

`src/Directory.Build.props` で `Nullable` は既定 ON であり、`?` の有無が意味を持つ。
規則は #518 が追加した 11 個と同じ——**`?` が付かないメンバーは `required` に入れ、付くものは入れない**。
値型（`int` / `bool` / `Guid` / `float` / `double` / `DateTimeOffset` / `DateOnly`）は非 null なら入れる。
コレクション（`List<T>` / `IReadOnlyList<T>` / `T[]` / `Dictionary<K,V>`）も非 null なら入れる。

**既定値つきの引数（`bool Sent = true` 等）も「非 null」なので `required` に入れる。**
既定値は「省略できる」ではなく「呼び出し側が省いたらこの値になる」であり、
**JSON へは必ず出力される**（`System.Text.Json` は既定でプロパティを省略しない）。

**その系として、`required` に入れたプロパティからは OpenAPI の `default` を落とす**
（[[IADR-0132]] 決定 2 の系）。応答側の `default` は「欠けていたらこの値と読め」の意味なので、
**「必ず出る」と言う `required` と同居すると契約が自己矛盾する。** 該当は 3 プロパティ
（`CompletionApiResponse.sent` / `EmbedApiResponse.embedded` / `EmbedApiResponse.retryable`）で、
いずれも C# の**引数既定**を写しただけだった。**要求スキーマの `default` は本来の意味で
機能しているので落とさない**（`AnalysisAskRequest.topK` / `EmbedApiRequest.purpose` 等）。

| # | スキーマ | 出所（C#） | `required` に入れる | 入れない（理由） |
| --- | --- | --- | --- | --- |
| 1 | `SearchResponse` | `Knowledge.Contracts/Dtos/SearchDto.cs:14-17` | `results` `totalHits` `elapsedMs` | — |
| 2 | `SearchResultDto` | `Knowledge.Contracts/Dtos/SearchResultDto.cs:4-12` | `chunkId` `documentId` `documentTitle` `text` `score` `attributes` `tags` | `markdownUri`（`string? MarkdownUri`） |
| 3 | `AiAnswerDto` | 同 `:28-40` | `answer` `citations` `model` `inputTokens` `outputTokens` `answerId` | — （`answerId` は `Guid AnswerId { get; init; }`＝非 null。`:39`） |
| 4 | `AccessScopeResponse` | `Platform.Shared.Contracts/Dtos/AccessScopeDto.cs:12-15` | `userId` `allowedFilters`（＋ inline の `allowedFilters[]` に `key` `allowedValues`。出所 `AttributeFilter` `:18`） | — （**C# の `bool Granted = false` に対応する `granted` が OpenAPI に無い**。§未決事項 1） |
| 5 | `AbacConditionMap` | **C# に出所なし**（写像そのもの。対応する `record` が存在しない） | **入れない** | `properties` が無く `required` を適用できない（§設計 2） |
| 6 | `ProblemDetails` | **C# に出所なし**（RFC7807。`Results.Problem` が組み立てるため DTO `record` が存在しない） | **入れない** | §設計 2 |
| 7 | `ValidationProblemDetails` | **C# に出所なし**（RFC7807。`Results.ValidationProblem` が組み立てるため DTO `record` が存在しない） | **入れない** | §設計 2 |
| 8 | `FeedbackDto` | `Knowledge.Contracts/Dtos/FeedbackDto.cs:17-25` | `id` `answerId` `rating` `userId` `createdAt` `updatedAt` | `comment`（`string?`）・`question`（`string?`） |
| 9 | `FeedbackStatsDto` | 同 `:29-33` | `up` `down` `total` `satisfactionRate` | — |
| 10 | `UsageEventCreatedDto` | **匿名型** `DashboardService.../DashboardEndpoints.cs:40`（`new { ev.Id }`）。`ev.Id` は `UsageEvent.cs:10` の `Guid Id` | `id` | — |
| 11 | `UsagePointDto` | `Knowledge.Contracts/Dtos/DashboardDto.cs:25` | `date` `eventType` `count` | — |
| 12 | `SearchTrendDto` | 同 `:28` | `term` `count` | — |
| 13 | `DashboardUsageDto` | 同 `:32-36` | `totalSearches` `totalAnswers` `usageTrend` `topSearchTerms` | — |
| 14 | `DashboardSummaryDto` | 同 `:43-48` | `totalSearches` `totalAnswers` `usageTrend` `topSearchTerms` `quality` | — |
| 15 | `CompletionApiResponse` | `Platform.Shared.Contracts/Dtos/CompletionDto.cs:52-60` | `text` `model` `inputTokens` `outputTokens` `sent` | `endpoint` `routingReason` `stopReason`（いずれも `string?`） |
| 16 | `EffectiveConfigDto` | `Platform.Shared.Contracts/Dtos/ConfigInfoDto.cs:28-33` | `version` `pipeline` `eventBindings` `ports` `connectors` | — |
| 17 | `ConfigVersionDto` | 同 `:36` | **入れない**（1 つも無い） | `gitCommit` `appliedAt` `appliedBy` が**すべて nullable** |
| 18 | `ConfigVersionEntryDto` | 同 `:41-45` | **入れない**（1 つも無い） | 4 メンバーが**すべて nullable**（#518 の判断を再確認した） |
| 19 | `PipelineStageDto` | 同 `:48-54` | `name` `service` `consumer` `input` `outputs` `enabled` | — |
| 20 | `EventBindingDto` | 同 `:57-60` | `event` `publishers` `subscribers` | — |
| 21 | `PortSelectionDto` | 同 `:22` | `port` `implementation` | `target`（`string? Target`） |
| 22 | `ConnectorDto` | 同 `:25` | `name` `enabled` | — |
| 23 | `DriftReportDto` | 同 `:63-66` | `hasDrift` `checkedAt` `findings` | — |
| 24 | `DriftFindingDto` | 同 `:69` | `kind` `severity` `target` `detail` | — |
| 25 | `EmbedApiResponse` | `Platform.Shared.Contracts/Dtos/EmbedDto.cs:30-38` | `vector` `dimensions` `model` `collection` `embedded` `retryable` | `endpoint` `routingReason`（`string?`） |

**出所不明のスキーマは 1 個ある**——`UsageEventCreatedDto`（**表 No.10**）は契約 `record` を持たず、
`DashboardEndpoints.cs:40` の**匿名型 `new { ev.Id }`** がそのまま JSON になる。
`DataSourceSyncResultDto`（#518 が同じ扱いで追加した）と同型の事象であり、**推測ではなく実装を読んで確定した。**

**出所欄の内訳（数え直した）**: `ファイル:行` を持つのは **22 行**（うち 21 行は契約 `record`、
1 行は上記の匿名型 `DashboardEndpoints.cs:40`）。**残り 3 行（No.5 / No.6 / No.7）は C# に出所が無い**
——`AbacConditionMap` は写像そのもの、`ProblemDetails` / `ValidationProblemDetails` は RFC7807 を
ASP.NET の `Results.Problem` / `Results.ValidationProblem` が組み立てるため対応する `record` が存在しない。
**「25 個すべてに `ファイル:行` がある」ではない**——3 行については「出所が無いこと」自体が確認結果である。

### 2. `required` を入れないスキーマ（**5 個。理由を 1 つずつ書く**）

「書き忘れ」と「入れない判断」を後任が区別できるよう、**YAML のコメントで理由を残す。**

| スキーマ | 理由 |
| --- | --- |
| `AbacConditionMap` | `properties` を持たない写像（`additionalProperties` のみ）。`required` は `properties` のキーを指すため適用できない。**唯一の要求兼応答スキーマだが、この理由で要求側への波及も起こらない** |
| `ProblemDetails` | RFC7807 はどのメンバーも省略され得る（`Results.Problem` は指定しなかった項目を出力しない） |
| `ValidationProblemDetails` | 同上 |
| `ConfigVersionDto` | C# の 3 メンバーがすべて nullable（GitOps 未注入時は空。`ConfigInfoDto.cs:36`） |
| `ConfigVersionEntryDto` | C# の 4 メンバーがすべて nullable（同 `:41-45`） |

### 3. `?? 既定値` / optional chaining をどうするか（**残す。理由を書く**）

`required` を入れると生成型が `?` から必須へ変わり、既存の `x.foo ?? 既定値` は
「型の上では常に左辺」になる。**それでも消さない。** 判断の根拠は [[IADR-0132]] 決定 3。

- **「契約上は必須」と「実行時に必ず来る」は別である。** BFF が 200 で契約違反の本文を返した場合
  （後段の縮退・将来の改修ミス）、`??` を消すと `undefined.trim()` で画面が白くなる。
  いま消して得られるのは行数の減少だけで、失うのは縮退時の耐性である。
- 本リポジトリの型検査は**型なし lint**（`@typescript-eslint` の型情報を使う
  `no-unnecessary-condition` は有効化していない）ため、**残しても警告は出ない**
  ——「lint が消せと言うから消す」という強制力は無い。
- **消さない以上、挙動は 1 ミリも変わらない。** 本作業のプロダクションコード差分は **0 行**になる。

該当箇所（SC-08。実測 5 のとおりここだけである）:

| ファイル:行 | 式 | 判断 |
| --- | --- | --- |
| `sc08-analysis/useAnalysisTask.ts:38` | `(answer.answer ?? '').trim()` | **残す**（空縮退の判定そのもの。`answer` が来なければ「該当なし」表示へ倒す設計） |
| `sc08-analysis/useAnalysisTask.ts:46` | `mutation.data?.status === 200` | **残す**（`mutation.data` は TanStack Query の `undefined` であり、契約とは無関係） |
| `sc08-analysis/AnalysisDashboardPage.tsx:165, 168` | `(outcome.answer.citations ?? [])` | **残す**（出典ゼロ件と本文欠落を同じ「表示しない」へ倒す） |
| `sc08-analysis/AnalysisDashboardPage.tsx:169` | `citation.chunkId ?? index` | **残す**（`chunkId` は #518 で既に required。本作業の前から冗長であり、本作業が作った冗長ではない） |
| `sc08-analysis/AnalysisDashboardPage.tsx:206, 207` | `answer.inputTokens ?? 0` | **残す**（トークン数が欠けても 0 と表示して画面を壊さない） |

## 受け入れ基準

- [ ] **応答スキーマの `required` が C# の非 null 性と一致する**（§設計 1 の 25 行のうち
      **22 行が `ファイル:行` で辿れ、残り 3 行は C# に出所が無いことを明記している**）。
      **本 PR の最初のコミット本文は「25 個すべて `ファイル:行`」と書いており、この点で本書と食い違う**
      ——履歴は書き換えないため（force push 禁止）、正しいのは本書と [[IADR-0132]] 決定 1 の側である。
- [ ] **`required` を入れないスキーマ 5 個に、YAML コメントで理由が書いてある。**
- [ ] **代表的なフィールドを消す／改名すると `pnpm run typecheck` が落ちる**（実測。§変異試験）。
      **素通りしたものは表に残す。**
- [ ] **`pnpm run codegen` の後に `git diff --exit-code -- src/platform/frontend/src/foundation/api/generated` が差分なし**
      （パスはリポジトリルート基準。CI は `src` を作業ディレクトリにするため、そこでは `src/` 接頭辞が要らない）。
- [ ] **既存コードの挙動が変わっていない**（プロダクションコード差分 0 行・既存テスト無改修で全 green）。
- [ ] `pnpm run typecheck` / `lint` / `test` / `test:coverage` / `build` / E2E が green。**カバレッジ床を割らない。**
- [ ] リポジトリの機械検査（`check-doc-links` / `check-commit-messages` / `check-contract-schema` /
      `check-test-traceability` / `check-test-spec-coverage` / `check-unit-dependencies` /
      `check-i18n-catalogs` / `check-bff-downstreams` / `scripts.test.js`）が green。
- [ ] **消化の記録が[作業仕様書 #506](./20260805_issue-506_openapi-bff-groups.md) §未決事項 2 に日付つき
      ［追記］として入っている**（＝消化対象の本体。**[[IADR-0131]] に §未決事項という節は無い**）。
- [ ] [[IADR-0131]] §結果 > フォローアップ 1 に日付つき［追記］があり、**本作業が消化したのは
      同項の「前提」であって項番 1 本体（画面の載せ替え = #519）ではない**ことが読み取れる。

## テスト方針

**本作業はプロダクションコードを 1 行も変えない**（契約の宣言と生成物のみ）。
したがって**新規のテストは足さない**——足せば「宣言を書いたこと」を宣言で確かめるトートロジーになる。
[[IADR-0131]] 決定 4 が SSE 除外について採ったのと同じ考え方で、**担保は成果物と変異試験に置く。**

| 手段 | 見るもの |
| --- | --- |
| **生成物の再生成差分検査**（CI） | OpenAPI と生成物の乖離（`required` を消せば生成型に `?` が戻り差分が出る） |
| **変異試験**（本書 §変異試験） | 「OpenAPI を壊すと型検査が落ちる」——**生成フックに載っている面についてのみ成立する** |
| 既存テスト（無改修） | 画面の挙動が変わっていないこと |

## 検証（実測）

**測定条件**: worktree `fix/NFR-openapi-response-required`（`origin/develop` `727d021` 基点。
**クロス監査の是正時に `origin/develop`（#515 / #522 を含む）を merge で取り込み、下表を再測した**）／
Node 22.22.2 ／ pnpm 10.33.0 ／ Vitest 3.2.7（v8 provider）／ orval 8.23.0 ／
**submodule `src/ai-stock-trading` と `planning` は populate 済み**。
スコープは断りがない限り**ワークスペース全体**（`src/` の 4 パッケージ ＋ AST）である。

| 検査 | コマンド | 結果 |
| --- | --- | --- |
| 型検査 | `pnpm run typecheck` | green（`exit=0`。4 パッケージ。AST は**無改修**） |
| lint | `pnpm run lint` | green（**0 errors / 9 warnings**。warning は全件 `react-refresh/only-export-components`。**着手前も 0 errors / 9 warnings** で同数） |
| 単体テスト | `pnpm run test` | **57 files / 539 tests** 全 green。**着手前と同数**——テストを 1 件も足さず、既存テストを 1 件も改変していない |
| カバレッジ | `pnpm run test:coverage` | statements/lines **95.93%**（5447/5678）／ branches **89.77%**（1124/1252）／ functions **91.83%**（416/453）。床（lines 90 / statements 90 / functions 88 / branches 85）を満たす |
| ビルド | `pnpm run build` | green（`dist/assets/index-Bw-dS6vy.js` 632.98 kB / gzip 190.04 kB）。**バンドルのハッシュが #506 の記録と一致する**＝プロダクションコードを 1 バイトも変えていないことの傍証 |
| E2E | Playwright（後述の条件） | **12 tests 全 green**（所要時間は測定ごとに揺れるため書かない） |
| 生成物の乖離 | `pnpm run codegen` ＋ `git diff --exit-code -- src/platform/frontend/src/foundation/api/generated` | green（コミット後に再実行して差分なし） |
| 静的 egress | `node scripts/check-static-egress.js --require src/platform/frontend/dist` | green（4 ファイル・検出 0 件） |
| ドキュメントリンク | `node scripts/check-doc-links.js` | green（**件数は書かない**※） |
| コミット件名 | `node scripts/check-commit-messages.js --base origin/develop` | green（**件数は書かない**※） |
| 契約スキーマ | `node scripts/check-contract-schema.js` | green（baseline と一致・未消化の承認 0 件。**C# を 1 行も触っていない**）。総数は書かない※ |
| テスト・トレーサビリティ | `node scripts/check-test-traceability.js` | green（未写像 0 件。**allowlist は着手前と同じ 7 件**＝増やしていない）。総数は書かない※ |
| テスト仕様書の被覆 | `node scripts/check-test-spec-coverage.js` | green（**床 68 は動かしていない**——バックエンドテストを足していないため）。総数は書かない※ |
| ユニット依存方向 | `node scripts/check-unit-dependencies.js` | green |
| i18n カタログ | `node scripts/check-i18n-catalogs.js` | green（2 ロケール・未翻訳 0 件。**カタログは 1 件も増減していない**——表示文言を足していない） |
| BFF 後段 | `node scripts/check-bff-downstreams.js` | green（ドリフト 0） |
| スクリプト自己試験 | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | green（**件数は書かない**※） |

> **※ リポジトリ全体を数える値は、この表に固定値で書かない。** 本 PR の変更とは無関係な他 PR の
> マージで必ず動き、書いた瞬間から嘘になり始めるためである（実際に develop の取り込みで
> `check-doc-links` と `scripts.test.js` の件数は動いた）。**本表に残すのは green / 赤の別と、
> 本 PR 固有の不変量**（「allowlist を増やしていない」「床を動かしていない」「C# を触っていない」）
> **だけ**にする。件数が要るときはコマンドを実走して読むこと。
> 上の行のうち `lint` / `単体テスト` / `カバレッジ` / `ビルド` は `src/` ワークスペースに閉じた
> 測定であり、**本 PR が「挙動を変えていない」ことの根拠そのもの**なので値を残す。

**E2E の実行条件**: この環境では `playwright install` がブラウザを取得できない。導入済みの
`/opt/pw-browsers/chromium-1194/chrome-linux/chrome` を `launchOptions.executablePath` で指す
**ローカル専用 config を一時的に置いて実走し、確認後に削除した**（#490 / #496 / #502〜#506 と同じ作法）。
**リポジトリの `platform/frontend/playwright.config.ts` は無改変であり、作業ツリーは clean である。**

### 受け入れ基準 1: 生成型から `?` が消えたこと

```console
$ grep -c '?:' src/platform/frontend/src/foundation/api/generated/bff.schemas.ts
# 着手前: 153   /   本作業後: 72
```

**応答から到達する 36 スキーマのうち `required` を持つものが 11 → 31 になった**
（残る 5 個は §設計 2 の明示的な除外）。検算コマンドは §着手時の実測 1 と同じもので、
出力は `resp=36 resp_with_required=31 still_without=5` である。

`required` に書いたキーがすべて `properties` に実在することも同じスクリプトで検算した
（`required-without-property: []`）。

### 変異試験（**受け入れ基準そのもの**）

**件数の基準**: 下表は **M1〜M8 の 8 件**である。手順は
「変異を当てる → `pnpm run codegen` → 生成物の md5 比較 → `pnpm run typecheck` → **復元して md5 一致を確認**」で、
各行の末尾に **`typecheck` の exit コードと `TS####`** を実測値で記す。**復元漏れが無いことも毎回検査した**
（全 8 件で `restored OK`）。

| # | 壊した箇所 | 種別 | 期待 | **実測** |
| --- | --- | --- | --- | --- |
| **M1** | `AiAnswerDto.answer` を削除（`required` からも外す） | 削除 | 落ちる | **落ちた**。`typecheck exit=2` ／ `error TS2551: Property 'answer' does not exist on type 'AiAnswerDto'. Did you mean 'answerId'?` ×2 |
| **M2** | `AiAnswerDto.citations` → `citationsRenamed` | 改名 | 落ちる | **落ちた**。`exit=2` ／ `TS2339: Property 'citations' does not exist on type 'AiAnswerDto'.` ×2 ＋ `TS7006`（暗黙 any）×2 |
| **M3** | `AiAnswerDto.citations` を旧記載（`SearchResultDto[]`）へ戻す（#518 の M2 / M2b と同じ変異） | 退行 | 落ちる | **落ちた**。`exit=2` ／ `TS2739: Type 'SearchResultDto' is missing the following properties from type 'CitationDto': number, snippet` |
| **M4** | **`AiAnswerDto` の `required` 行だけを削除**（プロパティは触らない＝本 PR の退行） | 退行 | 生成物に差分が出る | **出た**。生成物 md5 が変化＝`git diff --exit-code -- …/generated` が exit=1 で落ちる。**`typecheck` は exit=0（素通り）** |
| **M5** | `AiAnswerDto.model` → `modelRenamed` | 改名 | 落ちる | **落ちた**。`exit=2` ／ `TS2339: Property 'model' does not exist on type 'AiAnswerDto'.` |
| **M6** | `DriftFindingDto.detail` を削除（**SC-11 は手書き型のまま**） | 削除 | **素通りする** | **素通りした**（`exit=0` ／ error 0 件）。生成物には差分が出る |
| **M7** | `SearchResponse.totalHits` を削除（**SC-02 は手書き型のまま**） | 削除 | **素通りする** | **素通りした**（`exit=0` ／ error 0 件）。生成物には差分が出る |
| **M8** | `EmbedApiResponse.model` を削除（**`/bff/` から到達しない**） | 削除 | **素通りする**（かつ**生成物にも出ないはず**と想定していた） | **素通りした**（`exit=0`）。**しかし生成物には差分が出た**——`components.schemas` は前処理で落ちないため、53 スキーマすべてが `bff.schemas.ts` へ出力されている（§実測 4 はこの結果を受けて書き直した） |

#### 素通りしたもの（**3 件。隠さない**）

| # | 素通りした事象 | いま網が無い理由 | 引き受け先 |
| --- | --- | --- | --- |
| M6 | `DriftFindingDto` のフィールドを消しても型検査が落ちない | SC-11（`useConfigViewer.ts`）は `apiFetch` ＋ 手書き型で、生成型を読んでいない | **#519**（載せ替え） |
| M7 | `SearchResponse` のフィールドを消しても落ちない | SC-02（`useSearchQuery.ts`）も同様 | **#519** |
| M8 | `EmbedApiResponse` のフィールドを消しても落ちない | `/embed` は SPA が呼ぶ面ではない（BFF 境界）。**恒久的に画面が読まない** | **恒久**。契約記述の正しさは人手の突合（§設計 1）に依存する |

**素通りの原因は `required` の不足ではなく、画面が生成型を読んでいないことである。**
M1〜M3・M5 が示すとおり、**読んでいる面（SC-08）では削除も改名も型置換もすべて捕まる。**
**M4 は、`required` を消す退行そのものは全スキーマについて再生成差分検査が捕まえる**ことを示す
——`typecheck` が素通りしても CI は止まる。

## 未決事項・親への申し送り

| # | 事項 | 種別 | 送り先 |
| --- | --- | --- | --- |
| 1 | **`AccessScopeResponse` に `granted` が無い**（C# `AccessScopeDto.cs:15` には `bool Granted = false` が在る）。`granted=false` は「許可ポリシーが 1 つも一致しなかった」＝**deny-by-default の判定材料**であり、`allowedFilters` が空でも「全件開放」ではないことを示す**意味のあるフィールド**である | **契約の欠落** | **#525（起票済み）**。`/authz/access-scope` はサービス直接 API で SPA は呼ばないため実害はいま無いが、**契約が実体と食い違っている**。フィールドの追加は「`required` を入れる」作業と別種の是正なので本 PR では入れていない |
| 2 | **テストの fixture が生成型で型付けされていない**（`AnalysisDashboardPage.test.tsx:28` の `ANSWER` は `jsonResponse(body: unknown)` を経由するため、`CitationDto` の `number` / `snippet` を欠いていても型検査に掛からない） | 網の穴 | **#519**。載せ替えで MSW モック（`*.msw.ts`）を使うなら自然に解消する。使わないなら fixture へ `satisfies AiAnswerDto` を付ける小さな判断が要る |
| 3 | **画面の載せ替え（9 ファイル）** | 本 issue の残り | **#519**。**本 PR で生成型はすでに必須化されている**ので、載せ替えた瞬間に「消したフィールドを読んでいる」箇所が型エラーになる（M6 / M7 が素通りしているのは、まさにこの載せ替えがまだ無いためである）。**［2026-08-05 追記］#519 本文の誤記（本 issue を `#516` と書いていた）は訂正済みで、「#520 は先に消化済み・生成型は既に必須化されている」旨も本文へ追記されている**——引き継ぎに際して #519 を読み直す必要は無い |
| 4 | **C# → OpenAPI の追随は人手のまま**。しかも本作業で `required` を増やしたぶん、**C# 側で `?` を足したのに OpenAPI の `required` を外し忘れると「嘘の必須」が残る**面が増えた | 構造的な穴 | [[IADR-0131]] フォローアップ 2 ／ [[IADR-0132]] フォローアップ 1。**本 issue で穴が広がったことは自覚した差異である** |
| 5 | **`IADR-0132` の採番衝突は解消済み**——**［2026-08-05 追記］**並行作業（`wt512`）が `IADR-0133` を確保したため、本 PR の `IADR-0132` は**改番不要**である。当初の懸念（`.claude/rules/traceability.md` §採番衝突時の改番手順＝**先着尊重**により後発が改番する）は発生しなかった | 運用（解消） | — （対応不要。改番が必要になった場合の追随先は本仕様書 / `docs/adr/README.md` / [[IADR-0131]] の追記 / `docs/api/BFF_bff-surface.md` / **PR タイトル**） |
| 6 | **要求スキーマの `required` は見直していない**（`AnalysisDataRange` / `UpdateMetadataRequest` が `required` を持たない）。要求側の必須化は**画面の送信コードを壊す**ため、独立に扱うべきである | 範囲外 | 必要なら別 issue。**本 PR では応答側だけを触った** |
| 7 | **ワークフロー変更は不要**（`.github/workflows/` を触っていない） | 情報 | `frontend.yml` の `paths` に `docs/api/openapi.yaml` が既に入っており、契約変更で CI が起動する |
