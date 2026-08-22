---
title: 作業仕様書 — xUnit1051 段階採用の器を置く（第 1 PR・src/ のテスト .cs を 1 行も変えない）（#882）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0030
  - IADR-0140
  - IADR-0231
  - IADR-0235
author: claude
created: 2026-08-22
updated: 2026-08-22
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md (テスト = xUnit v3)
related_specs:
  - "../adr/IADR-0235_xunit1051-staged-adoption-ratchet.md"
  - "../adr/IADR-0231_xunit-v3-simultaneous-switch.md"
issue: "#882"
---

# 作業仕様書 — xUnit1051 段階採用の器を置く（第 1 PR）

## 目的と射程

`IADR-0231` 決定 4 が据え置いた `xUnit1051` を段階採用するための**器だけ**を置く。
**`src/` のテストの `.cs` を 1 行も変えない。** これにより「テスト件数が減らない」は自明に満たされ、
レビューは器の設計だけに集中できる。

決定そのものは [`IADR-0235`](../adr/IADR-0235_xunit1051-staged-adoption-ratchet.md) にある。
本書は**引いた母集合・実測値・変異試験**を残す。

## 着手前の実測（前提の確認）

**移行は 1 箇所も始まっていない。**

```
$ git grep -n "TestContext" 8abff2e -- '*.cs'
（出力なし、exit=1）
```

リポジトリ全体の 5 ヒットはすべて `.md` と `src/Directory.Build.props` のコメントである。

ブランチは develop と同一（`git rev-list --left-right --count develop...HEAD` → `0	0`）。
`git rev-parse --is-shallow-repository` は `false`（履歴を出典に引ける）。

## 母集合の引き方と、その結果

### 対象プロジェクトの母集合

**誤りの側から引く。** props の条件が `$(MSBuildProjectName.EndsWith('Tests'))` なので、
**同じ形（`*Tests.csproj`）で引く**。拡張子や `grep -i test` で引くと過大に取れる
（実際、初回に `grep -i test` で引いて AST を **42 本**と数えたが、`BacktestService.Application.csproj`
のように名前に `test` を含むだけのものを拾っていた。**正しくは 38 本**である）。

| 母集合 | 件数 | 扱い |
| --- | --- | --- |
| `src/**`（AST を除く）の `*Tests.csproj` | **16** | 対象。baseline に載せる |
| `templates/**` の `*Tests.csproj` | **1**（`SampleService.Tests`） | 対象。**17 本目**として baseline に載せる |
| `src/ai-stock-trading/**` の `*Tests.csproj` | **38** | **対象外**（別プロジェクト。`check-backend-libraries.js` と同じ扱い） |

**除外の理由**:

- **AST**: 別プロジェクトの submodule である。許可リスト方式のため `WarningsAsErrors` は
  AST へ届かず、本作業で AST の挙動は変わらない（実測で確認。後述）。
- **雛形を 17 本目として勘定に入れた理由**: CI の `template-backend-build` が
  `templates/*/backend` を **`src/.template-buildcheck-<name>/backend/`** へ複製してビルドするため、
  `src/Directory.Build.props` が効く。`SampleService.Tests` は `EndsWith('Tests')` に一致する。
  **勘定に入れないと器の射程を取り違える。**
- `src/.template-buildcheck-*` 自体は追跡下に無い一時ディレクトリなので、検査器の走査からは外す。

**AST と MSP のテストプロジェクト名に衝突は無い**（`comm -12` が空）。
したがって許可リストの項目が誤って AST のプロジェクトを掴むことはない。

### 件数の母集合 —— 🔴 3 つの文書に載っていた 1,886 件は 2 倍の重複計上だった

`IADR-0231` 決定 4・`src/Directory.Build.props`・`docs/tech/tech-requirements.md` の 3 箇所が
**1,886 件**と書いていた。実測すると:

```
$ dotnet build src/platform/backend/backend.slnx  -t:Rebuild -p:NoWarn= -m:1   → 417 個の警告
$ dotnet build src/knowledge/backend/backend.slnx -t:Rebuild -p:NoWarn= -m:1   → 528 個の警告（うち CS0618 が 2）
  'warning xUnit1051' のログ行の総数 …… 1,886
  ファイル・行・列で一意化 ………………… 943
  MSBuild のサマリからの導出 …… 417 + (528 - 2) = 943   ← 一致
```

MSBuild は 1 件の警告を**ビルド中の行と末尾のサマリの 2 箇所**へ出力する。
**独立した 2 つの数え方（サマリ／一意化）が 943 で一致した**ので、943 を実数として採る。
`LlmGateway.Api.Tests` を単独ビルドで数え直しても **114 件**で slnx 経由と一致した（相互検算）。

🔴 **`-m:1` を落とすとノード接頭辞 `N>` が付き、一意化そのものが失敗する**（同じ箇所が
2 つの別文字列になり、228 件と出た）。数え直すときは必ず付ける。

🔴 **`NoWarn` を打ち消すのに props を書き換える必要は無い。**
`-p:NoWarn=` はコマンドラインのグローバルプロパティで、props 側の代入に勝つ。

### 実測値（推定ではない）

| プロジェクト | 実測 | 事前の推定値 | 乖離 |
| --- | ---: | ---: | --- |
| `Platform.Bff.Tests` | 229 | ~317 | −88 |
| `ConversionService.Worker.Tests` | 138 | ~239 | −101 |
| `LlmGateway.Api.Tests` | 114 | ~161 | −47 |
| `DocumentService.Api.Tests` | 94 | ~202 | −108 |
| `DataSourceService.Api.Tests` | 75 | ~183 | −108 |
| `AuthorizationService.Api.Tests` | 74 | ~110 | −36 |
| `WikiService.Api.Tests` | 51 | ~137 | −86 |
| `RetrievalService.Api.Tests` | 47 | ~108 | −61 |
| `IngestionService.Worker.Tests` | 33 | ~79 | −46 |
| `AiAnalysisService.Api.Tests` | 30 | ~54 | −24 |
| `FeedbackService.Api.Tests` | 30 | ~41 | −11 |
| `DashboardService.Api.Tests` | 24 | ~32 | −8 |
| `Knowledge.IntegrationTests` | **4** | ~255 | **−251** |
| `Knowledge.Contracts.Tests` | 0 | 0 | — |
| `Platform.Shared.Kernel.Tests` | 0 | 0 | — |
| `Platform.Shared.Infrastructure.Tests` | 0 | 0 | — |
| `SampleService.Tests`（雛形） | 1 | （未計上） | — |
| **合計** | **943**（雛形を含め 944） | ~1,930 | — |

事前の推定は「`await` の出現数 × 1.32」という外挿だったが、**全プロジェクトで過大**であり、
`Knowledge.IntegrationTests` では **60 倍以上外した**（推定 ~255 に対し実測 4）。
`await` の数は `CancellationToken` を受けるオーバーロードの有無と相関しないため、代理指標として使えない。

**相互検算**: 事前に `DashboardService.Api.Tests` を目視で数えた結果は **24 箇所**であり、
本実測の 24 と一致した。目視と機械が独立に一致したので、一意化の方法は妥当である。

## 設計上の要点（実測に基づく）

### 「剥がしたら 0 件」を担保するのは `WarningsAsErrors` である

`TreatWarningsAsErrors` は **`false`**。`NoWarn` を外しただけでは再発しても **CI は緑のまま**になる。

### 許可リストにした理由

`src/Directory.Build.props` は **AST へ import-chain で届く**（`IADR-0231` 決定 1 の
「両ファイルとも `<Import>` を持たず」は `Directory.Build.props` について誤り。同 ADR へ日付つき追記で訂正した）。
AST のテストプロジェクトは **38 本すべてが `Tests` で終わる**ため、拒否リストだと
`WarningsAsErrors` が AST 全体へ届く。

AST を守っていたのは AST 自身の `.editorconfig` の `dotnet_diagnostic.xUnit1051.severity = none` だが、
**AST はその行を後続コミットで削除している**:

```
$ git -C src/ai-stock-trading diff 9b9c676 abce001 -- .editorconfig
-dotnet_diagnostic.xUnit1051.severity = none
```

gitlink（`9b9c676`）には在るが、作業ツリーが指す `abce001` には無い。
**他リポジトリの `.editorconfig` の現状に本リポジトリの CI の成否を依存させない。**

### 抑止は `WarningsAsErrors` に勝つ（実測）

| 条件 | 結果 |
| --- | --- |
| `WarningsAsErrors` のみ | **error・exit 1** |
| `WarningsAsErrors` ＋ `.editorconfig` の `severity = none` | `.editorconfig` が勝つ・exit 0 |
| `WarningsAsErrors` ＋ `NoWarn` | `NoWarn` が勝つ・exit 0 |

→ 移行済みへ後から抑止を足せば ratchet は黙って外れる。検査器の `stray-suppression` が止める。

### `Contains` / `EndsWith` は序数・大文字小文字を区別する（実測）

綴りを誤った項目は「`NoWarn` も `WarningsAsErrors` も付かない」プロジェクトを生み、
**警告は出るが CI は緑**になる。検査器の `props-desync` が止める。

## 変異試験（対応表）

**規律**: 変異が実際に当たったことを先に assert（`git diff` で当該箇所のみ変化）してから結果を読み、
復旧は `cmp` でバイト一致を確認した。EXIT はパイプに通さず直接読んだ。

### ビルド層（本丸）

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| M-BUILD-0 | 変異なし（移行済み 2 本をビルド） | 成功 | **exit 0**（両方） |
| M-BUILD-1 | 移行済み `Knowledge.Contracts.Tests` に `await Task.Delay(1);` を足す | **ビルド失敗** | **`error xUnit1051` / exit 1** |
| M-BUILD-2 | 同じ変異のまま props を**旧版**へ戻す（器を no-op 化） | 素通り | **xUnit1051 の出現 0 件 / exit 0** |
| M-BUILD-3 | 同じ変異のまま「`NoWarn` を外すだけ・`WarningsAsErrors` 無し」 | **緑のまま** | **warning 止まり（2 行）/ exit 0** |

🔴 **M-BUILD-3 が本設計の理由そのものである** —— `NoWarn` を外すだけでは再発を止められない。
M-BUILD-2 は器が load-bearing であること（無ければ緑）を示す。

### プロパティ評価層

| 対象 | `NoWarn` | `WarningsAsErrors` | 判定 |
| --- | --- | --- | --- |
| `Knowledge.Contracts.Tests`（移行済み） | `1701;1702` | `;xUnit1051;…` | 剥がれている ✓ |
| `Platform.Shared.Kernel.Tests`（移行済み） | `1701;1702` | `;xUnit1051;…` | 剥がれている ✓ |
| `Platform.Shared.Infrastructure.Tests`（未移行） | `;xUnit1051` | （無し） | 従来どおり ✓ |
| `DashboardService.Api.Tests`（未移行） | `;xUnit1051` | （無し） | 従来どおり ✓ |
| `Platform.Shared.Kernel`（非テスト） | `1701;1702` | （無し） | 無関係 ✓ |
| **AST の `AiStockTrading.Bff.Endpoints.Tests`** | `;xUnit1051` | （無し） | **新旧 props で完全に同一** ✓ |

### 検査器層

| # | 変異 | 期待 | 実測 |
| --- | --- | --- | --- |
| M-CHK-0 | 変異なし（実データ） | 違反 0 | **exit 0** |
| M-CHK-1 | baseline から `DashboardService.Api.Tests` を消す | `added` | **exit 1・`[added]` 1 件** |
| M-CHK-2 | baseline に実在しない `Ghost.Tests` を足す | `removed` | **exit 1・`[removed]` 1 件** |
| M-CHK-3 | props の許可リストを `Tests` → `tests` に崩す | `props-desync` | **exit 1・`[props-desync]` 2 件（両方向）** |
| M-CHK-4 | **検査器を no-op 化**して M-CHK-1 を再適用 | **緑になってしまう** | **exit 0**（＝検査器が効いていた証拠） |

落ちた件数と種別を毎回読んだ。**全部落ちる（器が壊れただけ）という結果は 1 度も出ていない** ——
M-CHK-1〜3 はいずれも意図した種別だけが、意図した件数（1・1・2）出た。

自己試験は **18 件** all passed（正例・負例を対で固定。`M0` 負例＝正常な世界で違反 0 を含む）。

### 復旧の確認

```
cmp Directory.Build.props / EventMessageUrnTests.cs / check-xunit1051-ratchet.js /
    xunit1051-baseline.json  →  すべて byte-identical
src/.template-buildcheck-unit-template  →  削除済み（git status --short --ignored -- src/ に残骸なし）
```

## 変更したファイル

| ファイル | 変更 |
| --- | --- |
| `src/Directory.Build.props` | 許可リスト `XUnit1051Migrated` ＋ `WarningsAsErrors`。件数を 943 へ訂正 |
| `scripts/xunit1051-baseline.json` | **新規**。残件の単一情報源（実測値・17 本） |
| `scripts/check-xunit1051-ratchet.js` | **新規**。判定 7 種＋自己試験 18 件 |
| `scripts/scripts.repo.test.js` | 検査器の CI 呼び出し口（自己試験・実データ・検出力・実データ突合の 5 件） |
| `scripts/README.md` | 主表と CI 対応表に 1 行ずつ |
| `.ai-context/adr/IADR-0235_*.md` | **新規**。決定 4 件 |
| `.ai-context/adr/IADR-0231_*.md` | 日付つき追記 2 件（`<Import>` の誤り・件数の重複計上）。`updated:` を前進 |
| `.ai-context/adr/README.md` | `IADR-0235` の索引行。**あわせて `IADR-0234`/`0235` の並び順を昇順へ直した**（後述） |
| `docs/tech/tech-requirements.md` | 件数を 943 へ訂正し、許可リスト方式を明記。trace ブロックへ `IADR-0235` |

### 🔴 `.github/workflows/ci.yml` は 1 行も変更していない

`check-adr-numbering` と同じく `scripts/scripts.repo.test.js` から呼ぶ（`IADR-0140` 決定 2 の相乗り）。
既存の `scripts-tests` ジョブ（`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`）が走らせる。
**新ジョブを作らないので、必須チェック名も起動条件も変わらない。**
並行して `ci.yml` を触る作業（#900）との衝突も生じない。

### 🔴 他セッションの赤を 1 件直した（範囲外だが自 PR の CI を通すために必要）

着手中に `.ai-context/adr/README.md` の索引が **`IADR-0235` → `IADR-0234` の順**で着地しており
（`cce974a`）、`check-adr-numbering` が `index-not-sorted` で **exit 1** になっていた。
自 PR の CI もこれで落ちるため、**2 行の並べ替えのみ**行った（本文は 1 文字も変えていない）。

## 受け入れ基準と結果

| 基準 | 結果 |
| --- | --- |
| `src/` のテストの `.cs` を 1 行も変えない | ✅ `git status` に該当なし |
| 剥がしたプロジェクトで再発したらビルドが落ちる | ✅ M-BUILD-1（`error xUnit1051` / exit 1） |
| 剥がしていないプロジェクト・AST・雛形の挙動が変わらない | ✅ プロパティ評価層の表（AST は新旧 props で同一） |
| 検査器が `added` / `removed` で落ちる | ✅ M-CHK-1 / M-CHK-2 |
| 検査器が実際に効いている | ✅ M-CHK-4（no-op 化で緑になる） |
| 残件が推定でなく実数である | ✅ 943 件（2 通りの数え方が一致・目視 24 件と一致） |

## 申し送り（第 2 PR 以降。本 PR では実装しない）

**1 PR = 1 プロジェクト、実測件数の小さい順。** 手順は
「テストの `.cs` を直す → baseline の `remaining` を 0・`migrated: true` → props の許可リストへ追加」。
検査器が 3 点の一致を強制するので、どれか 1 つを忘れると落ちる。

1. **`DashboardService.Api.Tests`（実測 24 件）** —— 最初の実コード PR。全 24 箇所を目視済みで、
   `PostAsJsonAsync` / `GetFromJsonAsync` / `GetAsync` / `SendAsync` / `ReadFromJsonAsync` のみ。
   **判断を要する箇所ゼロの機械的置換**である。
2. `SampleService.Tests`（雛形・1 件）—— 1 行。新規ユニットへ良い形を配るため早めに。
3. `Knowledge.IntegrationTests`（4 件）
4. `Platform.Shared.Infrastructure.Tests`（0 件）—— **U5 / Wolverine 移行チェーンの着地後**。
   先に剥がすと並行 PR が自分の追加分で落ちる（baseline に `deferReason` として記録済み）。
5. 以降 `AiAnalysisService.Api.Tests`（30）/ `FeedbackService.Api.Tests`（30）/
   `IngestionService.Worker.Tests`（33）/ `RetrievalService.Api.Tests`（47）/
   `WikiService.Api.Tests`（51）/ `AuthorizationService.Api.Tests`（74）/
   `DataSourceService.Api.Tests`（75）/ `DocumentService.Api.Tests`（94）/
   `LlmGateway.Api.Tests`（114）/ `ConversionService.Worker.Tests`（138）/ `Platform.Bff.Tests`（229）
6. 全 17 本が `migrated` になったら `src/Directory.Build.props` から
   `NoWarn` / `XUnit1051Migrated` / `WarningsAsErrors` の 3 つの `PropertyGroup` とコメントを削除し、
   `scripts/xunit1051-baseline.json` と `scripts/check-xunit1051-ratchet.js` を退役させる。

## 発見したが本 PR の射程外のこと

- **CI の `discover-units` は submodule を取得しない**ため、`src/*/backend/backend.slnx` の glob に
  `src/ai-stock-trading` が現れず、**AST は `backend-build` の matrix に入っていない**
  （`backend-build` 側は submodule を取得するのに、matrix を決める `discover-units` 側は取得しない）。
  本作業の安全性はこれに依存していない（許可リストのため AST へは届かない）が、
  **AST が MSP の CI でビルドもテストもされていない**という事実は別途扱う価値がある。
