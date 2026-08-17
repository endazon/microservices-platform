---
title: 作業仕様書 — 雛形（`templates/unit-template/backend`）のテストプロジェクトを 2 本から 1 本へ畳む（#455 規範性 A-4）
type: spec
status: done
related_ids:
  - FR-14
  - NFR
  - ADR-0030
  - ADR-0041
  - IADR-0056
  - IADR-0060
  - IADR-0064
  - IADR-0117
  - IADR-0141
  - IADR-0179
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - "../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md (§規範性・粒度・置き場。`Tests` は 1 プロジェクト)"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md"
related_specs:
  - "./20260803_issue-455_backend-application-standard.md"
  - "./20260816_chore_unit-template-frontend-drift.md"
  - "../adr/IADR-0060_submodule-unit-operations.md"
  - "../adr/IADR-0064_standalone-build-props-fallback.md"
  - "../tests/TEST_STRATEGY.md"
  - "../tech/tech-requirements.md"
---

# 作業仕様書: 雛形のテストプロジェクトを 2 本から 1 本へ畳む（#455 規範性 A-4）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **`FR-14`**（追加可変機能ユニット。雛形 `templates/unit-template/` はその配布物）
- ユースケース（UC）/ 画面（SC）: なし
- 非機能要件: **`NFR`（無採番）** —— 雛形の構成を計画の標準へ適合させる保守性の作業であり、計画側の
  非機能要件表（`NFR-01`〜`NFR-27`）に当たる番号が無い（`.claude/rules/traceability.md`
  「起点 ID の種別」の 2 の場合。[IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 1）。
  **無いことは「実装側で採番してよい」ではない**（同 決定 2）。**環流しない。**
- 関連 ADR（計画）: **ADR-0030**（バックエンドアプリケーション層標準）／ADR-0041（Result 型）
- 関連 ADR（実装）: [IADR-0060](../adr/IADR-0060_submodule-unit-operations.md)（雛形はビルド対象外）／
  [IADR-0064](../adr/IADR-0064_standalone-build-props-fallback.md)（`.sample` フォールバック）／
  [IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md)／
  [IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md)（母集合の引き方）
- 本リポジトリの起点: **#455**（親 #454 フェーズ 0）の断片

### 規範の一次情報（実測で確認した原文）

計画 `06_technical/12_backend-application-stack.md`（`status: fixed`）§規範性・粒度・置き場（2026-08-04 確定）の
4 つ目の箇条書きが本作業の唯一の根拠である。

```console
$ grep -n "フォルダで分け" projects/microservices-platform/06_technical/12_backend-application-stack.md
52:- **`Tests` は 1 プロジェクトとする。** Unit / Integration はプロジェクトを分けず、フォルダで分ける（プロジェクトを分けるとビルド時間と参照管理のコストが増える）。
```

裁定の出典は同ファイルの変更履歴（2026-08-04 追補）にある **利用者裁定 2026-08-04（質問票 第 1 回 Q6・Q8・Q9／
第 2 回 Q25〜Q27／第 3 回 Q30）・planning#180**（実装からの環流）である。

## 目的・背景

実サービスは全て 1 テストプロジェクト（`Services/<Name>/tests/<Name>.Api.Tests` 等）で A-4 に準拠しているが、
**雛形 `templates/unit-template/backend` だけが `SampleService.UnitTests` / `SampleService.IntegrationTests` の
2 本に割れている**。雛形は新ユニットの出発点であり、放置すると**これから作られる全ユニットが不適合を継承する**
（`templates/unit-template/README.md` 自身が同じ理由で「knowledge を真似て 1 階層へ戻さないこと」と書いている）。

## 母集合（自分で引き直した結果）

**issue 本文・親エージェントの申し送りの「反映先」は母集合として使わない**（`.claude/rules/traceability.repo.md`
§是正・追随の母集合の取り方）。着手時点（`c01bc093`）で以下の 10 軸を引いた。**行フィルタで絞らず・拡張子で
絞らず・パスの除外だけで取った。**

| 軸 | 引き方（実行したコマンド） | 結果 |
| --- | --- | --- |
| 1a | `git grep -n -I -F "SampleService.UnitTests" -- . ':(exclude)planning'` | 4 行 / 4 ファイル |
| 1b | `git grep -n -I -F "SampleService.IntegrationTests" -- . ':(exclude)planning'` | 3 行 / 3 ファイル |
| 2 | `git grep -n -I -E "(Unit\|Integration)Tests?" -- . ':(exclude)planning'` | 137 行 / 74 ファイル |
| 3 | `git ls-files \| grep -E "(Unit\|Integration)Tests?"`（**パス軸**） | 26 パス |
| 4 | `git grep -l -I -F "SampleService" -- . ':(exclude)planning'` | 14 ファイル |
| 5 | `git grep -n -I -F "unit-template" -- . ':(exclude)planning'`（**雛形の参照元軸**） | 73 行 |
| 6 | `rg --no-ignore --hidden -F -e "SampleService.UnitTests" -e "SampleService.IntegrationTests" -e "SampleService.Tests"`（**未追跡ファイル込み**、`.git`/`planning`/`node_modules`/`bin`/`obj` のみ除外） | 7 行 / 5 ファイル |
| 7 | `git grep -n -I -E "tests/" -- docs/how-to/adding-a-unit-submodule.md templates/unit-template/README.md` | 3 行 |
| 8 | 計画側原文 `grep -rn "フォルダで分け\|Tests は 1\|テストプロジェクトは 1" /home/user/project-planning` | 1 行（規範の一次情報） |
| 9 | `git grep -n -I -E "[<A-Za-z_>]\.(Unit\|Integration)Tests" -- . ':(exclude)planning'`（**プレースホルダ形 `<Name>.UnitTests` を捕まえる軸**） | 109 行 / 69 ファイル |
| 10 | `git grep -n -I -E "7 ?(プロジェクト\|つ\|本)\|７" -- . ':(exclude)planning'`（**導出値の軸**） | 37 行 |

**軸 1（誤りの側の literal）だけでは足りなかった。** 軸 9 と軸 10 がそれぞれ**追加の反映先を出した**
（下表の 8〜10）。これは母集合規則 5「軸を 1 本で終わらせない」の実例である。

### 反映先（変更する。全 10 件）

| # | 反映先 | 内容 | 検出した軸 |
| --- | --- | --- | --- |
| 1 | `templates/unit-template/backend/Services/SampleService/tests/SampleService.Tests/SampleService.Tests.csproj` | **新設**。`PackageReference` は旧 2 本の和集合 9 件、`ProjectReference` は Application ＋ Api | — |
| 1b | `.../tests/SampleService.Tests/GlobalUsings.cs` | **新設**（`global using Xunit;`）。**配置ビルドが暴いた既存欠陥の是正**。下記「配置ビルドで判明した既存欠陥」を参照 | 配置ビルドの実測 |
| 2 | `.../tests/SampleService.Tests/Unit/CreateSampleHandlerTests.cs` | 移動＋ namespace を `SampleService.Tests.Unit` へ | 1a |
| 3 | `.../tests/SampleService.Tests/Integration/HealthEndpointTests.cs` | 移動＋ namespace を `SampleService.Tests.Integration` へ | 1b |
| 4 | `.../tests/SampleService.UnitTests/SampleService.UnitTests.csproj` | **削除**（フォルダごと） | 3 |
| 5 | `.../tests/SampleService.IntegrationTests/SampleService.IntegrationTests.csproj` | **削除**（フォルダごと） | 3 |
| 6 | `templates/unit-template/backend/backend.slnx` | `<Project>` 2 行 → 1 行 | 1a / 1b |
| 7 | `templates/unit-template/README.md` | 構成図の 2 行 → 1 行 ＋ **「Unit / Integration はフォルダで分ける」規範と出典を明記** | 1a / 1b |
| 8 | `templates/unit-template/backend/Directory.Packages.props.sample` | コメント 2 箇所 —— L45 のテスト節の見出し（`SampleService.UnitTests / .IntegrationTests` → `SampleService.Tests`）と **L24 の導出値「雛形の 7 プロジェクト」→ 6**（**軸 10 が出した。軸 1 では捕まらない**） | 1a / **10** |
| 9 | `docs/tech/tech-requirements.md:129` | 標準構成図 `tests/{<Name>.UnitTests, <Name>.IntegrationTests}` → 1 プロジェクト形 | **9** |
| 10 | `docs/tests/TEST_STRATEGY.md:262-263` | テスト種別表の「置き場所」列が 2 プロジェクトを指している | **9** |

**9 と 10 を含めた理由**（黙って除外しない／黙って含めない、を両方避けるため明記する）:

- どちらも **live な権威文書**であり、A-4 と**同一の違反**を、プレースホルダ表記のために軸 1 が
  取りこぼしていた箇所である。母集合規則 9（「追随する文書」を記憶で挙げず、誤りの側の文字列で
  全文書を走査してから挙げる）に従って引き当てた。
- とくに `docs/tech/tech-requirements.md` は、**本作業で書き換える `templates/unit-template/README.md` と
  `backend.slnx` の両方が「詳細はこれを参照」と名指ししている宛先**である。ここを直さないと、
  雛形が「1 プロジェクト」と言い、その雛形が指す先が「2 プロジェクト」と言う状態を新たに作る。
- 変更は合計 3 行であり、計画外の大規模リファクタには当たらない。

### 除外したものと、その理由（全数）

| 除外対象 | 件数の目安 | 除外の理由 |
| --- | --- | --- |
| `src/**` の `Knowledge.IntegrationTests`（`src/knowledge/backend/Tests/`） | 軸 2 の大半（約 25 ファイル） | **ユニット横断の統合テストプロジェクト**であり、A-4 が言う「サービス単位の `Tests`」とは別の層である（`src/README.md` L37 が「ユニット横断」と明示）。加えて本作業は `src/` を 1 バイトも変更しない前提で走らせている |
| `src/**` の各サービスの `<Name>.Api.Tests` | 14 プロジェクト | **A-4 の前段「1 プロジェクト」には準拠済み**（`git ls-files "src/*/backend/**/*Tests*.csproj"` で全数確認）。**ただし後段「Unit / Integration はフォルダで分ける」は 14 本すべて未達である** —— 実測: `git ls-files 'src/**' \| grep -E "Tests/(Unit\|Integration)/"` が **0 件**で、単体と統合がフラットに同居している。本作業は `src/` を 1 バイトも変更しない前提なので**射程外**だが、**「A-4 に準拠」と一括りにすると後段の未達が隠れる**ので分けて書く。後段の是正は #455 が引き続き追跡する |
| `docs/specs/**`（23 ファイル） | 軸 2/9/10 の多数 | **確定済みの作業仕様書は書き換えない**（`.claude/rules/traceability.repo.md`）。過去の実測値・当時の構成を記録した歴史であり、現在形の規範ではない |
| `docs/adr/**`（IADR-0061 / 0062 / 0130 / 0151 / 0163 / 0186） | 6 ファイル | 全数を目視した。**いずれも実在する `Knowledge.IntegrationTests`（および旧名 `KnowledgePlatform.IntegrationTests`）の改名・脆弱性ピン・パス実在検査の記録**であり、雛形のテストプロジェクト構成とは無関係 |
| `scripts/check-unit-dependencies.js` / `backend-library-baseline.json` / `scripts.repo.test.js` / `check-cpm-versions.js` L358・L377 | 5 行 | 実在する `Knowledge.IntegrationTests` の**実パス**、または検査器の合成フィクスチャ（`templates/unit-template/backend/T/T.csproj` 等の架空パス）。雛形の実ファイルを指していない |
| `scripts/check-cpm-versions.js:20`（`src 30 + templates 7 = 37 プロジェクト / 195 参照`） | 1 行 | **判断を要した唯一の境界事例。** 本作業後にこの値は **36 プロジェクト / 190 参照**へ動くが、原文は「ratchet / baseline を持たないのは、**着手時点**の違反が 0 件であることを実測したためである」と**時点を明示した過去の実測**である。書き換えると #467 の記録を改竄することになる。**載っている主張（違反 0 件）は本作業後も真**である。よって触らず、本書と報告で明示する |
| `deploy/istio/README.md` / `docs/data/document-and-version.md` / `docs/tests/FR-01・FR-06・FR-07・FR-09・SC-07` | 8 行 | 実在する `Knowledge.IntegrationTests` 配下の実テストファイルへの参照 |
| `docs/how-to/adding-a-unit-submodule.md:22`（`tests/<Name>.Api.Tests/`） | 1 行 | **すでに 1 プロジェクト形**で A-4 に適合している。是正不要 |
| `CHANGELOG.md` | 1 行 | 自動生成物。過去のコミット件名であり手で書き足さない |
| `planning/` / `src/ai-stock-trading/` | — | 本作業では変更禁止（別リポジトリ・別 ID 名前空間） |
| `.claude/rules/**` / `CLAUDE.md` | — | 該当記述なし（軸 2・9 いずれもヒット 0）。かつ必読規約の総量予算のため増やさない |

## 対象範囲

- **対象**: 上表「反映先」10 件のみ。
- **対象外**: 実サービス（`src/**`）の構成変更、xUnit v2 → v3 の切替（独立 issue）、
  `Directory.Packages.props.sample` のバージョン値、フロントエンド雛形。

## 設計

### 新しいレイアウト

```text
Services/SampleService/
  src/{SampleService.Api, .Application, .Domain, .Infrastructure, .Contracts}   ← 5 プロジェクト（不変）
  tests/SampleService.Tests/                    ← 1 プロジェクト（A-4）
    SampleService.Tests.csproj
    Unit/CreateSampleHandlerTests.cs            ← namespace SampleService.Tests.Unit
    Integration/HealthEndpointTests.cs          ← namespace SampleService.Tests.Integration
```

`.csproj` 本数は **7 → 6**。

### `SampleService.Tests.csproj` の中身

- `PackageReference` は旧 2 本の**和集合 9 件**: `Microsoft.NET.Test.Sdk` / `xunit` /
  `xunit.runner.visualstudio` / `AwesomeAssertions` / `NSubstitute` /
  `Microsoft.AspNetCore.Mvc.Testing` / `Testcontainers.PostgreSql` / `Respawn` / `coverlet.collector`。
  （旧 UnitTests 6 件 ∪ 旧 IntegrationTests 8 件 = 9 件。重複は 5 件）
- `ProjectReference` は `SampleService.Application` ＋ `SampleService.Api`。相対パスは
  `..\..\src\` から `..\..\..\src\` へは変わらない（`tests/<Proj>/` の階層は同じである）。
- **CPM への追加・削除は不要**である。和集合 9 件はすべて既存の `PackageVersion` 宣言に収まる
  （`Directory.Packages.props.sample` の L48〜L56 と本体 `src/Directory.Packages.props`）。

### 検証（★ 着地条件）

**雛形はその場ではビルドできない。設計どおりである**（IADR-0060 / #230。共通 props が `.sample` 配布のため
`TargetFramework` 未定義、相対 `ProjectReference` も `src/<unit>/backend/...` への配置を前提とする）。
さらに **CI に雛形を `dotnet build` / `dotnet test` する網が 1 つも無い**。したがって次を実測する。

1. 雛形を一時的に `src/<unit>/backend/Services/` 配下へ配置し、共通 props を継承させる
2. `dotnet build` と `dotnet test` を通し、**`[Fact]` 2 件が両方とも実行されて通ることをテスト名まで確認**する
   （1 件だけ動いて 1 件が拾われていない、が最も危険な失敗である）
3. 作業ツリーを元へ戻し、`git status --short` で `src/` に何も残っていないことを示す

### 配置ビルドで判明した既存欠陥（`global using Xunit;` の欠落）

配置ビルドの 1 回目が **CS0246 × 5**（`Fact` / `FactAttribute` / `IClassFixture<>` が解決できない）で落ちた。
**これは本作業が壊したのではなく、雛形が最初から持っていた欠陥である。** 変更前（`HEAD` = `c01bc093`）の
雛形を `git archive` で取り出して同じ手順で配置したところ、**2 プロジェクト構成のまま同一の 5 エラーで
落ちる**ことを実測した（証跡は報告に添付）。

原因は、`src/Directory.Build.props` の `ImplicitUsings` が `Xunit` を含まないこと。本リポジトリの
**実テストプロジェクト 12 本はすべて `GlobalUsings.cs` に `global using Xunit;` を持っており**
（`git ls-files "src/**/GlobalUsings.cs"` で全数確認）、雛形だけがこれを欠いていた。
**雛形が CI のビルド対象外である（IADR-0060 決定 3）ため、誰も気付けなかった。**

したがって、実サービスと同じ 1 行の `GlobalUsings.cs` を新テストプロジェクトへ置く。これは既存パターンへの
追随であり、新規パターンの導入ではない。**この 1 行が無いと、雛形を複製した新ユニットは最初の
`dotnet build` で必ず落ちる。**

## 受け入れ基準

- [x] `templates/unit-template/backend/Services/SampleService/tests/` 配下の `.csproj` が **1 本**である
- [x] `Unit/` と `Integration/` のフォルダ分割で `[Fact]` 2 件が保存されている（削らない）
- [x] `backend.slnx` の `tests` フォルダに登録された `<Project>` が 1 行である
- [x] `README.md` から「Unit / Integration はプロジェクトを分けずフォルダで分ける」ことが読み取れ、出典（計画 12_backend-application-stack §規範性・粒度・置き場 / planning#180）が引かれている
- [x] **配置ビルドで `dotnet build` が成功し、`dotnet test` が 2 件を実行して 2 件とも通る**（テスト名を出力で確認）
- [x] 最終状態で `src/` に差分が 1 バイトも無い
- [x] `node scripts/check-backend-libraries.js` が緑（判定行を読む）
- [x] `node scripts/check-cpm-versions.js` が緑（判定行を読む）
- [x] `docs/tech/tech-requirements.md` と `docs/tests/TEST_STRATEGY.md` が A-4 と一致する

### 実測ログ（配置ビルド。最終状態の雛形をそのまま `src/tmpl-final/backend/` へ置いて実行）

```console
$ dotnet build backend.slnx
Build succeeded.
    0 Warning(s)
    0 Error(s)
BUILD_EXIT=0

$ dotnet test backend.slnx --logger "console;verbosity=detailed"
  Passed SampleService.Tests.Unit.CreateSampleHandlerTests.Handle_名前を与えるとイベントに反映される [26 ms]
  Passed SampleService.Tests.Integration.HealthEndpointTests.Health_は200を返す [371 ms]
Test Run Successful.
Total tests: 2
     Passed: 2
TEST_EXIT=0

$ dotnet format backend.slnx --verify-no-changes
FORMAT_EXIT=0
```

**変更前（`HEAD` = `c01bc093`）の雛形を同じ手順で配置した対照実験**（既存欠陥であることの証拠）:

```console
$ git archive HEAD templates/unit-template/backend | tar -x -C <tmp>   # 2 プロジェクト構成のまま
$ dotnet build backend.slnx
error CS0246: The type or namespace name 'FactAttribute' could not be found   [SampleService.UnitTests.csproj]
error CS0246: The type or namespace name 'IClassFixture<>' could not be found [SampleService.IntegrationTests.csproj]
Build FAILED.    5 Error(s)
EXIT=1
```

検査器の判定行:

```console
$ node scripts/check-backend-libraries.js
[check-backend-libraries] OK: 新規混入 0 件 / Domain 依存規律 OK（既知残件 42 件は baseline 済み）。
EXIT=0

$ node scripts/check-cpm-versions.js
[check-cpm-versions] OK: 36 プロジェクト / 190 件の PackageReference にバージョン直書き 0 件（VersionOverride 0 件）。
EXIT=0    ← 変更前は「37 プロジェクト / 195 件」。予告どおり 1 プロジェクト・5 参照 減った
```

## テスト方針

雛形は本体 CI のビルド対象外であるため、恒久的な自動テストは持たない（IADR-0060 決定 3）。
本作業では**一時配置による実走**をもって代える。既存の `[Fact]` 2 件は内容を変えず、
フォルダと namespace のみを移す（テストの意味を変えないため、受け入れ基準の写像は不変である）。

## 計画書との差異

- 差異: **なし**。本作業は計画 12_backend-application-stack §規範性・粒度・置き場（`fixed`）の
  A-4 へ雛形を適合させるものであり、計画書に対する逸脱を含まない。

## 未決事項

1. **雛形に恒久的なビルド網が無い**（`ci.yml` は静的検査 2 本しか雛形を読まない）。本作業で
   壊れていないことは実測したが、**次に雛形を触った人は同じ手作業を強いられる**。CI へ
   「雛形を一時配置してビルドする」ジョブを足すかは独立した設計判断であり、本作業の射程外とする。
2. `scripts/check-cpm-versions.js:20` の実測値は本作業後に現況と一致しなくなる（上表参照）。
   時点明示の過去記録として据え置いたが、扱いの是非は監査の判断に委ねる。
