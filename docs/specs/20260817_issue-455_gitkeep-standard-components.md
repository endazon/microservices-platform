---
title: 標準構成 7 要素の不在を .gitkeep で可視化するための決定（#455 の断片・適用はしない）
type: spec
status: done
related_ids:
  - ADR-0019
  - ADR-0030
  - IADR-0027
  - IADR-0056
  - IADR-0060
  - IADR-0117
  - IADR-0218
  - NFR
author: implementation-agent
created: 2026-08-17
updated: 2026-08-17
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0019_unit-first-repo-structure.md"
related_specs:
  - "../adr/IADR-0218_gitkeep-standard-components-scope.md"
  - "../adr/IADR-0117_platform-shared-kernel-placement.md"
  - "../adr/IADR-0027_composability-folder-structure.md"
  - "../tech/tech-requirements.md"
  - "20260803_issue-455_backend-application-standard.md"
  - "20260816_issue-455_template-tests-single-project.md"
  - "20260816_chore_unit-template-frontend-drift.md"
---

# 仕様書: 標準構成 7 要素の `.gitkeep` 適用範囲を決める（適用は次の作業）

> 本仕様書は実装着手前に作成した。計画書（`project-planning` の `projects/microservices-platform/`）を
> 一次情報とし、本書は「この作業で何をどう決めるか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（機能追加ではない）
- 非機能要件（NFR）: **無採番**。本作業は「標準構成の枠を可視化する」文書統制・レイアウト規律である。
  **着手前に計画の ID 列を実際に読んだ**（`01_requirements.md` L119-145 の `NFR-01`〜`NFR-27`）。
  性能・可用性・スケーラビリティ・セキュリティ・運用保守・拡張性のいずれにも当たる行が無く、
  同ファイル L113 が「**実装側のメタ作業には当たる `NFR-xx` が無い**。無採番の `NFR` を起点 ID として
  用いてよい。**無理に近い番号を付けない**」と明記している（キット規約「起点 ID の種別」の
  無採番 2 に該当。**環流しない**）
- ユースケース（UC）/ 画面（SC）: なし
- 関連 ADR:
  - 計画 [`12_backend-application-stack`](../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md)
    §規範性・粒度・置き場（**`status: fixed`**・2026-08-04 確定・利用者裁定 planning#180）
  - 計画 [`ADR-0030`](../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md)（ライブラリ標準・選定基準 2「構成要素を増やさない」）
  - 計画 [`ADR-0019`](../../planning/projects/microservices-platform/07_adr/ADR-0019_unit-first-repo-structure.md)（ユニット第一構成・決定 4）
  - 実装 [`IADR-0117`](../adr/IADR-0117_platform-shared-kernel-placement.md)（**Accepted**。共有カーネルの物理配置）
  - 実装 [`IADR-0027`](../adr/IADR-0027_composability-folder-structure.md)（Foundation / Composable 構造）
- 起票: #455（バックエンドアプリケーション層標準）の断片

## 目的・背景

計画 §規範性・粒度・置き場は次を定める（引用）。

> **7 つは全リポジトリ共通の標準構成である。** 実体のあるものは通常どおりプロジェクト（`.csproj`）として
> 作る。**実体が無いものは、空のフォルダを作り `.gitkeep` だけを置く**（`.csproj` は作らない）。
> - **理由**: 何も無いと、その構成要素が**意図的に不在なのか単に作り忘れなのかが一見して分からない**。
> - この規則は**基盤（`microservices-platform`）と可変機能ユニット（`ai-stock-trading` 等）の双方に適用する**。

実測（後述）では、フロント雛形には適用済みだが**バックエンドには 1 件も適用されていない**。

**本作業は適用しない。決定だけを書く。** 適用は決定が固まってからの別作業とする
（[[IADR-0116]] 規約 1・4。適用は多数の空フォルダを一度に足す作業であり、決定の是非と混ぜるとレビューできない）。

## 対象範囲

### 含む

- 実装 ADR [`IADR-0218`](../adr/IADR-0218_gitkeep-standard-components-scope.md) の新規作成（決定 1〜4）
- `docs/adr/README.md` の索引へ 1 行
- 本仕様書

### 含まない（明示）

- **`.gitkeep` を 1 つも置かない。`src/` を 1 バイトも変更しない**（適用は次の作業）
- `src/ai-stock-trading`（別リポジトリの submodule。本ワークツリーでは未 populate。計画の規則は
  当該リポジトリにも及ぶが、**追随は向こうの issue** で行う）
- `planning/`（読むだけ）
- `CLAUDE.md` / `.claude/rules/`（1 バイトも増やさない）

## 母集合（自分で引いた結果と、除外したものとその理由）

**規則 8 の但し書き**: 軸 3 は**本仕様書を書く前**に引いた値である。本書は検索語「gitkeep」を多数含むため、
書いた後に同じコマンドを回すと増える。**追試するときは基点 `d2901da4` で引くこと。**

### 軸 1 — `src/` 配下の `.gitkeep`（追跡下）

```
git ls-files 'src/**/.gitkeep'   →  0 件
```

### 軸 2 — リポジトリ全体の `.gitkeep`（追跡下・パスで絞らない）

```
git ls-files '*.gitkeep'   →  26 件
```

内訳は `docs/<種別>/.gitkeep` **15 件**（`api` `authz` `batch` `data` `errors` `functional` `how-to`
`infra` `integration` `migration` `observability` `screens` `specs` `tech` `tests`）と
`templates/unit-template/frontend/src/**` **11 件**（`app` `assets` `components` `hooks` `lib`
`locales` `stores` `testing` `types` `utils` の 10 ＋ `features/sample/stores` の 1）。

**issue 本文の「フロント雛形に 11 件」は再現した**（軸 2 で引き直して一致）。ただし
**`docs/` 側の 15 件は issue 本文に無い**。数え直して初めて出た。

### 軸 3 — 文字列「gitkeep」の全走査（拡張子で絞らない・行フィルタを継がない）

```
git grep -n -I --untracked -e 'gitkeep' -- . ':(exclude)planning'   →  28 行
```

`docs/specs/` 2 行・`feedback/` 2 行・`scripts/kit-sync-classification.json` 15 行・
`templates/unit-template/README.md` 8 行・`templates/.../useSampleFilter.ts` 1 行。
**`.ts` と `.json` が入っている**（`--include='*.md'` で絞っていたら 16 行を落としていた。規則 3）。

### 軸 4 — サービスの列挙（ファイルシステムから引く。issue の「11」を検証する）

`find src/{platform,knowledge}/backend/Services -maxdepth 1 -mindepth 1 -type d` → **11 件**
（platform 2 = `AuthorizationService` / `LlmGateway`、knowledge 9 = `AiAnalysisService` /
`ConversionService` / `DashboardService` / `DataSourceService` / `DocumentService` / `FeedbackService` /
`IngestionService` / `RetrievalService` / `WikiService`）。**issue 本文の「11 サービス」と一致した。**

### 軸 5 — 7 構成要素の実体走査（`.csproj` の有無。11 × 7 の全マス）

各サービスについて `src/<Name>.{Api,Worker,Application,Domain,Infrastructure,Contracts,SharedKernel}` と
`tests/*` の存在と `.csproj` 数を機械的に数えた。結果は下表（§現状表）。

### 軸 6 — 規範記述の走査（`.gitkeep` の語を含まない条文が落ちるため、別の語で引く。規則 4・5）

```
git grep -n -I -e '存在しない区分' -e '標準構成' -e 'SharedKernel' -e '7 つ' -- . ':(exclude)planning'
```

ここで初めて **`src/README.md:70`「存在しない区分のフォルダは作らない（空フォルダを置かない）。」**と
**`docs/tech/tech-requirements.md:128`（標準構成図に `SharedKernel` が無い）**が出た。
**軸 1〜3（`.gitkeep` の語）だけでは 1 行も捕まらない。**

### 除外したものと理由

| 除外 | 理由 |
| --- | --- |
| `planning/` submodule | 読むだけ（本作業の制約）。計画書は一次情報であって是正対象ではない |
| `src/ai-stock-trading` | 別リポジトリの submodule。**本ワークツリーでは未 populate**（空ディレクトリ）。計画の規則は及ぶが追随は向こう側の issue |
| `Bff/` の 3 プロジェクト（`Platform.Bff` / `Platform.Bff.Tests` / `Knowledge.Bff.Endpoints`） | 7 要素表は**サービス単位**の構成である。BFF はサービスではない（`Services/` 配下に無い） |
| `Shared/` の 4 プロジェクト（`Platform.Shared.Contracts` / `Platform.Shared.Infrastructure` / `Knowledge.Contracts` / `Knowledge.Contracts.Tests`） | **ユニット単位**の共有物。計画 §規範性は per-service と per-unit の併存を明示しており、per-unit 側は 7 要素表の対象外 |
| `knowledge/backend/Tests/Knowledge.IntegrationTests` | ユニット単位の統合テスト。サービスの `Tests`（1 プロジェクト）とは別枠 |
| `docs/<種別>/.gitkeep` 15 件 | 仕様書フォルダの枠であり、バックエンド標準構成とは別の面。**既に適用済みで是正不要** |
| `templates/unit-template/frontend/**` 11 件 | フロント側は 2026-08-16 に適用済み（[`20260816_chore_unit-template-frontend-drift`](20260816_chore_unit-template-frontend-drift.md) 未決事項 2）。**是正不要だが、本作業の先例として引く** |
| `templates/unit-template/backend/Services/SampleService` | **7 要素中 6 つが実体としてある**（`Api` / `Application` / `Domain` / `Infrastructure` / `Contracts` / `Tests`）。残る `SharedKernel` は決定 1 により対象外。よって**適用対象 0 件**。雛形は既に適合している |

## 現状表 — 11 サービス × 7 構成要素（実測）

`◯` = `.csproj` を持つ実体がある / `—` = 実体が無い（`.gitkeep` の候補）。

| # | ユニット | サービス | Api | Application | Domain | Infrastructure | Contracts | SharedKernel | Tests |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | platform | AuthorizationService | ◯ `.Api` | — | — | — | — | 対象外 | ◯ `.Api.Tests` |
| 2 | platform | LlmGateway | ◯ `.Api` | — | — | — | — | 対象外 | ◯ `.Api.Tests` |
| 3 | knowledge | AiAnalysisService | ◯ `.Api` | — | — | — | — | 対象外 | ◯ `.Api.Tests` |
| 4 | knowledge | ConversionService | ◯ **`.Worker`** | — | — | — | — | 対象外 | ◯ `.Worker.Tests` |
| 5 | knowledge | DashboardService | ◯ `.Api` | — | — | — | — | 対象外 | ◯ `.Api.Tests` |
| 6 | knowledge | DataSourceService | ◯ `.Api` | — | — | — | — | 対象外 | ◯ `.Api.Tests` |
| 7 | knowledge | DocumentService | ◯ `.Api` | — | — | — | — | 対象外 | ◯ `.Api.Tests` |
| 8 | knowledge | FeedbackService | ◯ `.Api` | — | — | — | — | 対象外 | ◯ `.Api.Tests` |
| 9 | knowledge | IngestionService | ◯ **`.Worker`** | — | — | — | — | 対象外 | ◯ `.Worker.Tests` |
| 10 | knowledge | RetrievalService | ◯ `.Api` | — | — | — | — | 対象外 | ◯ `.Api.Tests` |
| 11 | knowledge | WikiService | ◯ `.Api` | — | — | — | — | 対象外 | ◯ `.Api.Tests` |
| | | **合計（実体あり）** | **11 / 11** | **0 / 11** | **0 / 11** | **0 / 11** | **0 / 11** | — | **11 / 11** |

- `Api` 列の `◯` は決定 2（`.Worker` は `Api` の別形）に依る。**決定 2 を β にすると 2 マスが `—` へ変わる。**
- `SharedKernel` 列の「対象外」は決定 1 に依る。**per-service の実体は 11 サービスすべてで無い**が、
  集約先 `Platform.Shared.Kernel`（未作成）へ置くことが [[IADR-0117]] で確定済みである。
- **各サービスは 1 プロジェクトに畳まれている。** 層は `<Name>.{Api,Worker}/Foundation/` の
  フォルダとして存在する（[[IADR-0027]]・`src/README.md`）。実測: `Foundation/Domain` は **8 / 11**、
  `Foundation/Persistence` は **7 / 11** のサービスが持つ。**「Domain プロジェクトが無い」ことと
  「ドメインのコードが無い」ことは別である**（決定 3 の注記事項）。

**適用の総数**: 4 要素 × 11 サービス = **44 ファイル**。
**issue 本文の「11 × 5 = 約 55」とは 11 件ずれる** —— 差は `SharedKernel` 列であり、決定 1 の帰結である。

## 決定した内容（本体は IADR-0218）

| | 決定 |
| --- | --- |
| 1 | `SharedKernel` の**論理粒度はサービス単位**（計画の図が正）。ただし**物理配置は [[IADR-0117]] が集約済み**であり、per-service の `.gitkeep` は**置かない** |
| 2 | **`.Worker` は `Api` の別形**として「実体あり」と扱う（案 α）。`Api/.gitkeep` は置かない |
| 3 | 適用は **1 PR で 44 ファイルを一度に置く**。フォルダ名は `<Name>.<要素>`。読み替えの注記を 1 箇所へ足す |
| 4 | **機械検査は置かない**（「同型の事故が 2 回」の条件を満たさない。置く条件を先に書いておく） |

決定の根拠・却下案の代償・再評価条件は [`IADR-0218`](../adr/IADR-0218_gitkeep-standard-components-scope.md) が持つ。
**ここへ複写しない。**

## 受け入れ基準

- [x] 母集合を自分で引き、**結果と除外理由**を本書へ書いた（軸 6 本・除外 8 件）
- [x] 11 サービス × 7 構成要素の現状表を**実測**で作った（platform / knowledge の両方）
- [x] `SharedKernel` の粒度について、**計画の記述を自分で読んで**裏取りした（下記「裏取りの記録」）
- [x] `.Worker` の HTTP 面を**実測**した（推測で決めていない）
- [x] `IADR-0218` に決定 1〜4 を書き、**却下案の代償と再評価条件**を書いた
- [x] `docs/adr/README.md` へ索引 1 行（**200 文字以内**・本体 `title:` との LCS 12 以上）
- [x] `.gitkeep` を 1 つも置いていない。`src/` の差分が 0 である
- [x] 検査器 7 本を実行し、**判定行**を読んだ

## 裏取りの記録（親からの指示に対する実測）

### `SharedKernel` はサービス単位か

**計画の記述は「サービス単位」である。** 自分で読んだ箇所は 3 つ。

1. §プロジェクト構成の見出しが**「（サービス単位）」**であり、その木（L32-41）が
   `src/ ├── Api … ├── SharedKernel └── Tests` と**同じ木の中に** `SharedKernel` を置いている。
2. L29「Domain 層は **`SharedKernel` を除き**外部ライブラリへ依存しない。**Result 型は `SharedKernel` が
   公開する自前の型**（`Result` / `Result<T>` / `Error`）を用い、その内部実装としてのみ外部ライブラリ
   （`CSharpFunctionalExtensions`）を使う」。
3. L50 が `Contracts` について「**per-service と per-unit は併存する**」と明示するのに対し、
   **`SharedKernel` には同種の記述が無い**。書き分けられている以上、無い側を「両方あり」と読めない。

**ここまでは親の見立てどおりである。** ただし裏取りの過程で、**親が把握していなかった事実**が出た。

> **本リポジトリには [[IADR-0117]]（Accepted）があり、案 B「サービス単位に `<Name>.SharedKernel` を置く
> （計画構成図どおり）」を明示的に却下している。** 採ったのは案 A =
> `src/platform/backend/Shared/Platform.Shared.Kernel` への集約であり、却下理由は
> 「**サービス 11 個ぶんの Result 型が分裂**し、BFF の集約と `Platform.Shared.Contracts` の
> イベント契約に載せられない」である（同 IADR §検討した選択肢・§理由）。
> `docs/tech/tech-requirements.md` も L128 の標準構成図から `SharedKernel` を外し、
> **「共有カーネルはサービス単位に置かない」**と明記している。

したがって決定 1 は「上位の確定を記録するだけ」では済まない。**論理粒度（計画が正）と物理配置
（[[IADR-0117]] が正）を分けて書き、`.gitkeep` の適用対象からは外す**形にした。詳細は IADR-0218 決定 1。

### `.Worker` は HTTP 面を持つか

**持つ。推測ではなく実測である。**

| 実測点 | ConversionService.Worker | IngestionService.Worker |
| --- | --- | --- |
| `.csproj` の SDK | **`Microsoft.NET.Sdk.Web`** | **`Microsoft.NET.Sdk.Web`** |
| ホスト生成 | `WebApplication.CreateBuilder(args)` | `WebApplication.CreateBuilder(args)` |
| `/internal/introspection` | あり（`MapPlatformIntrospection()`） | あり（`MapPlatformIntrospection()`） |
| health | **あり**（`AddPlatformHealthChecks()` ＋ `MapPlatformHealthChecks()` = `/health/live` `/health/ready`） | 無し |
| 業務エンドポイント | **あり**（`MapConversionJobEndpoints()` = `/jobs` 配下に 5 経路） | 無し |
| 統合テスト用の入口公開 | `public partial class Program { }` | `public partial class Program { }` |
| `.csproj` 冒頭のコメント | 「自己申告エンドポイント（`/internal/introspection`）の最小 HTTP サーフェスのため Web SDK を使用」 | 同左 |

**両方とも ASP.NET Core アプリである。** 差は HTTP 面の広さだけで、有無ではない。

## 未決事項・環流

1. **【要・環流】計画 §規範性と [[IADR-0117]] の関係を計画側で明示してもらう。**
   IADR-0117 のフォローアップ 2 は「`/plan-feedback` で『構成図はサービス内の論理レイヤであり、
   物理配置は実装裁量』の明記を提案する」と書いているが、**計画側にその明記は入っていない**
   （2026-08-04 の §規範性 追補にも無い。実測）。**むしろ §規範性 は「7 つは全リポジトリ共通の
   標準構成である」と物理的な言い方で書かれており、読み方によっては IADR-0117 と衝突する。**
   本作業では決定 1 として実装側の運用を確定したが、**計画側の裁定を仰ぐ**（`/plan-feedback`）。
   - **裁定が「集約された要素にも枠を置く」であれば、`.gitkeep` は 44 → 55 になる。** 決定 1 の
     再評価条件として IADR-0218 へ書いた。
2. **適用 PR の起票**（44 ファイル）。決定 3 の段取りに従う。本作業では起票のみで実装しない。
3. `src/README.md:70`「存在しない区分のフォルダは作らない（空フォルダを置かない）」の扱い。
   **同条文は `<Name>.{Api,Worker}/` の内部区分（`Foundation/` `Composable/` の中）についての記述**であり、
   本件（サービス直下の 7 要素）とは階層が違う。**しかし読み手には区別が付かない**ため、
   **適用 PR で 1 文を足して階層を書き分ける**（決定 3）。**本 PR では触らない**（`src/` を変更しないため）。
