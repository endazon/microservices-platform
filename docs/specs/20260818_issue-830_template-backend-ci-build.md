---
title: 作業仕様書 — 雛形（`templates/*/backend`）を配置後の位置へ複製して CI で実際にビルド・テストする（#830）
type: spec
status: done
related_ids:
  - NFR
  - FR-14
  - IADR-0056
  - IADR-0060
  - IADR-0064
  - IADR-0141
  - IADR-0169
  - IADR-0179
  - IADR-0182
  - IADR-0183
author: claude
created: 2026-08-18
updated: 2026-08-18
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)"
  - "../../planning/docs/ai-implementation-workflow-guide.md"
related_specs:
  - "./20260816_issue-801_frontend-tests-paths-templates.md"
  - "./20260711_issue-230_submodule-unit-ops.md"
  - "./20260816_issue-455_template-tests-single-project.md"
  - "./20260804_issue-467_cpm-version-inline-check.md"
---

# 作業仕様書: 雛形 backend を CI で実際にコンパイル・テストする（#830）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **`FR-14`**（構成変更で完結する疎結合ユニット。雛形 `templates/unit-template` は
  その配布物である）。ただし**本作業そのものは CI の検証網に関する工程の統制**であり、製品の機能を変えない。
- ユースケース（UC）/ 画面（SC）: なし
- 非機能要件: **`NFR`（無採番）** —— CI 基盤に関するメタ作業であり、計画側の非機能要件表
  （`NFR-01`〜`NFR-27`）に当たる番号が無い（[IADR-0179](../adr/IADR-0179_unnumbered-nfr-for-meta-work.md)
  決定 1）。**無いことは「実装側で採番してよい」ではない**（同 決定 2）。**環流しない。**
- 関連 ADR:
  - 計画側: `ADR-0030`（バックエンドアプリケーション層標準）。**本作業では制約に触れない。**
  - 実装側: [IADR-0060](../adr/IADR-0060_submodule-unit-operations.md)（雛形の位置づけ・CI 自動発見・
    単独ビルド規約）／[IADR-0064](../adr/IADR-0064_standalone-build-props-fallback.md)（`.sample`
    フォールバック props）／[IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md)／
    [IADR-0169](../adr/IADR-0169_cross-repo-ref-scan-beyond-markdown.md)（`.github/workflows/` は編集できる）／
    [IADR-0182](../adr/IADR-0182_required-check-contexts-and-blocked-record.md)（必須チェックは check 名で指定する）／
    [IADR-0183](../adr/IADR-0183_false-green-warning-on-worktree-state.md)（偽の緑を返す条件は警告する）
- 計画書リンク: [`planning/docs/ai-implementation-workflow-guide.md`](../../planning/docs/ai-implementation-workflow-guide.md)
  （フェーズ末監査は**証跡（実行コマンドと出力）必須**。宣言だけの検証は不合格）

## 目的・背景

**配布中の雛形が一度もコンパイルできない状態のまま出荷されていた。** 原因は
`templates/unit-template/backend/.../SampleService.Tests/GlobalUsings.cs` の欠落である
（`ImplicitUsings` は `Xunit` を含まないため `[Fact]` / `IClassFixture<>` が CS0246 で落ちる）。
ファイル自体は PR #829 で足されたが、**「なぜ誰も気付かなかったか」の側**が本 issue の射程である。

着手時点（`develop` = `3ad5ad15`）の実測。**雛形 backend をコンパイルするジョブは 1 つも無い**:

| ワークフロー | 雛形 backend への効き | 実測 |
| --- | --- | --- |
| `ci.yml` `lint` | 当たらない | `450: for slnx in src/*/backend/backend.slnx` |
| `ci.yml` `build-and-test` | 当たらない | `479: for slnx in src/*/backend/backend.slnx` |
| `ci.yml` `backend-libraries` / `cpm-versions` | 静的検査のみ | `templates` の言及 3 件はすべて**コメント**であり配線ではない |
| `codeql.yml` | 当たらない | `66: for slnx in src/*/backend/backend.slnx` |
| `security.yml` / `copilot-setup-steps.yml` | **明示除外** | `-not -path './templates/*'` |
| `frontend.yml` / `frontend-tests.yml` | frontend のみ | `templates/*/frontend/**`（#801 / PR #814） |

`ci.yml` は `pull_request` に `paths:` を持たない（実測は下記「検証の実測」）。すなわち
**雛形を触る PR で CI は起動するのに、コンパイルする経路が無い。**

### #801 と同型だが、同じ直し方は使えない（実測）

`templates/` 位置のままでは**ビルドできない**。これは設計どおりである（IADR-0060 決定 3・決定 4）。

```console
$ dotnet build templates/unit-template/backend/backend.slnx
  Skipping project ".../templates/platform/backend/Shared/Platform.Shared.Contracts/..." because it was not found.
  Skipping project ".../templates/platform/backend/Shared/Platform.Shared.Infrastructure/..." because it was not found.
  Failed to restore .../SampleService.Api.csproj (in 52 ms).
NuGet.targets(198,5): error MSB4181: The "RestoreTask" task returned false but did not log an error.
Build FAILED.   1 Error(s)   EXIT=1
```

原因は 2 つあり、**`.sample` を外すだけでは直らない**（`.sample` は第 2 の原因にしか効かない）:

1. **相対 `ProjectReference` が配置後の位置を前提にしている。**
   `SampleService.Api.csproj` の `..\` × 6 は、配置後の
   `src/<unit>/backend/Services/SampleService/src/SampleService.Api/` から数えて `src/` に着く。
   `templates/` 位置から数えると `templates/platform/...` に着き、存在しない（上のログ）。
2. **共通 props が `.sample` 付きで配布される。** 配置時は `src/Directory.Build.props` /
   `src/Directory.Packages.props`（単一情報源）を階層継承させるため、ユニット側に常設の props を
   置かない（IADR-0060 決定 4）。`.sample` は**単独リポジトリでビルドするときだけ**拡張子を外して置く。

**`.sample` の設計意図の出典**（記憶ではなく実文書で確かめた）:
[`templates/unit-template/README.md`](../../templates/unit-template/README.md) §「単独リポジトリで
ビルドする場合（任意）」、両 `.sample` ファイルのヘッダコメント、IADR-0060 決定 4、
[IADR-0064](../adr/IADR-0064_standalone-build-props-fallback.md)。

したがって **「配置後の位置へ複製してからビルドする」ハーネス**が要る。

## 対象範囲

- `.github/workflows/ci.yml`: 雛形の配置ビルドジョブ `template-backend-build` を新設。
  併せて `cpm-versions` のコメントを規則 10 で引き直す。
- `templates/unit-template/backend/backend.slnx` / `.../SampleService.Tests.csproj`:
  規則 10 で引き直したコメント（雛形自身が「CI のビルド対象外」と書いていた）。
- `scripts/scripts.repo.test.js`: 配線の回帰テスト（**末尾へ追記**。中盤は #852 が編集中のため触らない）。
- 本仕様書。

**射程外（並行レーンの領域・本 PR では触らない。親へ報告する）**:
`.github/workflows/claude-code-review.yml` / `.devcontainer/**` / `scripts/setup.sh` /
`src/platform/backend/Services/LlmGateway/**` / `docs/adr/README.md`。
**新規 IADR は起こさない**（採番衝突を避けるため親が調整する）。

## 母集合の実測（`.claude/rules/traceability.md` 規則 1〜8 ／ `traceability.repo.md` 規則 9・10）

**軸を 1 本で終わらせない。** 誤りの側の文字列で**追跡下の全ファイル**を走査した（拡張子で絞らず、
パス除外だけ。除外は `planning`（submodule・編集禁止）と `src/ai-stock-trading`（submodule・射程外））。

| 軸 | 走査した文字列 | 件数 |
| --- | --- | --- |
| 1 | `ビルド対象外` | 13 件 |
| 2 | `ビルド対象`（1 を含む上位集合） | 40 件 |
| 3 | `src/*/backend/backend.slnx` | 27 件 |
| 4 | `必須チェック` | 50 件超（`docs/ai-workflow.md` の表を含む） |
| 5 | `not -path './templates` | 6 件 |
| 6 | `slnx にも` | 1 件（IADR-0060 決定 3） |
| 7 | `unit-template`（ファイル単位） | 41 ファイル |

### 規則 10: この変更で**新たに誤り（または誤解を招く形）になる**記述を引き直した

| 箇所 | 何が偽になるか | 扱い |
| --- | --- | --- |
| `ci.yml:291-292`（`cpm-versions` のコメント） | 「`templates/` は build-and-test のビルド対象外の**ため他で捕まらない**」。**実測で偽になった** —— 複製ビルドは版直書きを `NU1008` で落とす | **本 PR で是正**（追記ブロックで、なお本検査を残す理由も書いた） |
| `templates/.../SampleService.Tests.csproj:20` | 「非互換の runner と組み合わさった雛形が**配置されるまで誰も気付けない**」 | **本 PR で是正** |
| `templates/unit-template/backend/backend.slnx:3` | 偽ではないが、雛形と CI の関係を書いた唯一の場所で新経路に触れていない | **本 PR で追記** |
| `scripts/check-cpm-versions.js:43,357` | 「`ci.yml` のビルド対象外のため、走査しないと**誰にも捕まらない**」。上と同じ理由で偽になった | **射程外**。親へ報告（本 PR では触らない） |
| `scripts/check-backend-libraries.js:142,458` | 同上（不採用ライブラリは CPM に版が無いため復元段で落ちうる。ただし版を CPM へ足された場合は静的検査だけが捕まえるので、**部分的に**偽） | **射程外**。親へ報告 |
| `templates/unit-template/README.md:6` | 「このディレクトリは本体リポジトリの**ビルド対象ではない**」「**このテンプレート位置のままではビルドしない**」 | **偽にならない**（実測どおり templates/ 位置ではビルドできず、ビルドするのは複製である）。ただし新経路への導線が無い → 親へ報告 |
| `docs/adr/IADR-0060.md:52`（決定 3） | 「テンプレートは本リポジトリのビルド対象ではない（`src/` 外・どの slnx にも含めない）」 | **偽にならない**。`templates/` は依然としてどの slnx にも登録せず、`src/` 外に在る。ビルドするのは**一時的な複製**である。**改定 IADR は要らない**と判断した（下記「未決事項」に残す） |
| `docs/ai-workflow.md` の必須チェック表 | 新ジョブは表に無い | **偽にならない**（表は「必須にする check 名」の列挙であり、全ジョブの目録ではない）。射程外・親へ報告 |
| 確定済み `docs/specs/`（`20260816_issue-455_...` ほか 4 件） | 当時の事実として正しい | **書き換えない**（`traceability.repo.md`「確定済みの `docs/specs/` は書き換えない」） |

### 規則 9 の再走査（是正**後**の語で引き直す）

是正後に新設した語（`template-backend-build` / `.template-buildcheck-`）で全走査し、
**本 PR で作った 4 ファイル以外に出現が無い**ことを確かめた（下記「検証の実測」）。

## 設計

### 設計 1: `ci.yml` に配置ビルドジョブ `template-backend-build` を新設する

`templates/*/backend/backend.slnx` を**自動発見**し、各々を `src/.template-buildcheck-<name>/backend/`
へ複製して `dotnet build` → `dotnet test` する。要点:

- **`.sample` は複製先へ置かない**（`find ... -name '*.sample' -delete`）。置くと `src/` の単一情報源より
  近い階層で発見され上書きする（IADR-0060 決定 4）。
- **`--artifacts-path` で `obj`/`bin` を作業ツリーの外へ逃がす。** これが無いと `ProjectReference` 先の
  `src/platform/backend/Shared/*/{bin,obj}` まで作業ツリーへ生え、受け入れ基準
  「`git status --short --ignored -- src/` が空」を満たせない（実測は下記）。
- **`trap cleanup EXIT`** で複製を必ず片付け、**`if: always()` の後段ステップ**が
  `git status --short --ignored -- src/` で残骸ゼロを実測する（**落ちた回にこそ残骸が出る**）。
- **`dotnet test --verbosity normal`** で**実行されたテスト名**をログへ出し、
  `Passed` 行の数が `[Fact]`/`[Theory]` の数を**下回ったら fail** する。
  `dotnet test` の終了コードだけでは **`Skip` された 1 件を緑と読む**（実測 E2E-4）。
  `[Theory]` は `InlineData` で件数が増えうるため**下限**で判定する。
- 属性は**行頭（インデントのみ）**に限って数える。注釈中の `[Fact]` という文字列を拾って
  `expected` が水増しされる（プロトタイプで実際に 3 と数えた）。

**ジョブを分けた理由**（`build-and-test` へ相乗りしない）: 同ジョブは
`--collect:"XPlat Code Coverage"` の Cobertura を `check-coverage-floor.js` が集計しており、
**雛形の複製が床の分母に混ざる**。失敗名の可読性（`template-backend-build` が赤くなる）も分ける側に効く。

**`dotnet format` はここで走らせない。** `--artifacts-path` を解さず、`--no-restore` を付けても
`src/platform/backend/Shared/*/obj` を作業ツリーへ書き出し、「`src/` を汚さない」を破る（実測は下記）。
`lint` の glob にも同じ穴がある（`450: src/*/backend/backend.slnx`）が、**別 issue へ回す**。

### 設計 2: `scripts/scripts.repo.test.js` へ配線の回帰テストを足す（末尾へ追記）

**固定するのは「配線が在ること」であって「雛形がビルドできること」ではない**（node から `dotnet` は
呼べず、`scripts-tests` ジョブは `setup-dotnet` を持たない）。実コンパイルは新ジョブが行う。
併せて、**実際に出荷された欠陥そのもの**（`global using Xunit;` の欠落）を静的にも固定した。

同ファイルは develop で 8,193 行あり、直近の #852 が中盤 4998〜7645 行を書き換えている。
**`module.exports` の閉じ `};` の直前へ追記**し、中盤に触れていない（差分は `@@ -8192,0 +8193,180 @@` の
1 ハンクのみ。下記「検証の実測」）。

## 受け入れ基準（#830 逐語）

- [x] `templates/*/backend/**` だけを触る変更で、**雛形が実際にコンパイルされる**ジョブが走る
      （`ci.yml` は `paths:` を持たないので全 PR で起動する。ジョブ ID `template-backend-build`）
- [x] **変異試験で実測する** —— 雛形の `GlobalUsings.cs` を消すと、そのジョブが **fail する**
      （E2E-2。生ログは下記）
- [x] **実行されたテスト名**がログに出る（件数だけでなく）
- [x] 配置ビルドの後片付けが効いており、`git status --short --ignored -- src/` が空である

## テスト方針

- **配線**: `scripts/scripts.repo.test.js`（8 件）。各々を変異試験で「壊すと落ちる」ことまで実測する。
- **実挙動**: `ci.yml` の `run:` を YAML から**逐語で取り出して**ローカル実行し（手写しではない）、
  正常系 1 本＋変異 3 本を回した。

## 検証の実測

**走査の時点**: `develop` = `3ad5ad15`。`git rev-parse --is-shallow-repository` = `false`
（`git log` を出典に引ける状態であるが、本作業では引いていない）。
実行環境の `dotnet --version` = `10.0.400`（`global.json` は SDK `8.0.0` + `rollForward: latestMajor`）。

### 起動条件・必須チェック名が変わっていないこと

```console
$ python3 -c "import yaml; d=yaml.safe_load(open('.github/workflows/ci.yml')); print(d[True]); print(list(d['jobs']))"
{'push': {'branches': ['develop', 'main']}, 'pull_request': {'types': ['opened', 'synchronize', 'reopened']}}
['commit-messages', 'scripts-tests', 'doc-links', 'feedback-dispatched', 'kit-sync', 'feedback-status-sync',
 'reading-budget', 'pipeline-config', 'ai-workflow-config', 'unit-dependencies', 'backend-libraries',
 'cpm-versions', 'contract-schema', 'test-traceability', 'realm-constraints', 'k8s-local-up-smoke',
 'bff-downstreams', 'unit-service-ownership', 'lint', 'build-and-test', 'template-backend-build']
```

`on:` は変更前と同一（`paths:` 無し）。既存ジョブ ID は 20 件すべて不変で、末尾に 1 件増えただけである。
必須チェックの context（`build-and-test` / `lint` / `commit-messages` / `pr-title` / `image-build` /
`claude-review`。`docs/ai-workflow.md` の表）は**1 つも変えていない**。新ジョブは表に無い＝必須ではない
（`cpm-versions` 等の既存検査ジョブと同じ扱い。必須化はリポ管理者の設定であり AI では完結しない。IADR-0182）。

### 雛形が実際にビルドできるか（★ 実測）

**テンプレート位置のまま**（上の「目的・背景」に生ログ）: `MSB4181` で **失敗**。

**配置後の位置へ複製**（`.sample` を置かない・`--artifacts-path` あり）: **成功**。

```console
$ bash step2.sh     # ci.yml の run: を逐語で取り出したもの
::group::dotnet build src/.template-buildcheck-unit-template/backend/backend.slnx
Build succeeded.
::group::dotnet test src/.template-buildcheck-unit-template/backend/backend.slnx
  Passed SampleService.Tests.Unit.CreateSampleHandlerTests.Handle_名前を与えるとイベントに反映される [15 ms]
  Passed SampleService.Tests.Integration.HealthEndpointTests.Health_は200を返す [220 ms]
template=unit-template expected_test_attributes=2 executed_passed=2
OK: 1 件の雛形をビルド・テストした。
STEP2_EXIT=0
$ bash step3.sh
OK: src/ に残骸なし（git status --short --ignored -- src/ が空）。
STEP3_EXIT=0
```

`--artifacts-path` **なし**だと `src/` が汚れることの実測（設計の根拠）:

```console
$ git status --short --ignored -- src/      # 複製を消した直後
!! src/platform/backend/Shared/Platform.Shared.Contracts/bin/
!! src/platform/backend/Shared/Platform.Shared.Contracts/obj/
!! src/platform/backend/Shared/Platform.Shared.Infrastructure/bin/
!! src/platform/backend/Shared/Platform.Shared.Infrastructure/obj/
```

`dotnet format` を同ジョブへ入れない根拠の実測（`--verify-no-changes` 自体は合格するが `obj` が生える）:

```console
$ dotnet format <staged>/backend/backend.slnx --verify-no-changes --no-restore ; echo $?
0
$ git status --short --ignored -- src/   # 複製を消したあとも残る
!! src/platform/backend/Shared/Platform.Shared.Contracts/obj/
!! src/platform/backend/Shared/Platform.Shared.Infrastructure/obj/
```

`cpm-versions` のコメントを引き直した根拠の実測（版直書きは複製ビルドでも落ちる）:

```console
$ # 複製の SampleService.Api.csproj へ Version="6.24.4" を足してビルド
error NU1008: The following PackageReference items cannot define a value for Version: WolverineFx. ...
Build FAILED.   EXIT=1
```

### 変異試験（配線テスト。すべて「壊すと落ちる」を実測）

| # | 変異 | 結果 |
| --- | --- | --- |
| M1 | ジョブ ID を `template-backend-build-renamed` へ改名 | `AssertionError: ジョブ ID template-backend-build が無い（雛形をコンパイルする経路が消えている）` |
| M2 | `dotnet build` の行を外す | `AssertionError: 複製した雛形の dotnet build が無い` |
| M3 | `.sample` の除去を外す | `AssertionError: .sample の除去が無い。置いたままだと src/Directory.Build.props より近い階層で発見され上書きする（IADR-0060 決定 4）` |
| M4 | `test` 側の `--artifacts-path` を外す | `AssertionError: build と test の双方に --artifacts-path が要る（実測 1 件）。…` |
| M5 | 実行件数の下限判定を `if false` へ | `AssertionError: 実行件数の下限判定が無い（1 件だけ動いて 1 件が拾われない、を見逃す）` |
| M6 | 後片付け検査の `if: always()` を外す | `AssertionError: 後片付け検査に if: always() が無い` |
| M7 | `on: pull_request` へ `paths: ["src/**"]` を足す | `AssertionError: ci.yml に paths: フィルタが入っている` |

**復元**: 各変異後に backup から書き戻し、`md5sum` 一致を確認した
（`1ec504d17c0daae27e8db610a2ea7461`）。`git diff --stat` も期待どおり。

### 変異試験（実挙動。ci.yml の `run:` を逐語実行）

**E2E-2: 実際に出荷された欠陥そのもの**（`GlobalUsings.cs` を外す）:

```console
$ mv templates/.../SampleService.Tests/GlobalUsings.cs /tmp/… ; bash step2.sh
.../Unit/CreateSampleHandlerTests.cs(10,6): error CS0246: The type or namespace name 'Fact' could not be found …
.../Unit/CreateSampleHandlerTests.cs(10,6): error CS0246: The type or namespace name 'FactAttribute' could not be found …
.../Integration/HealthEndpointTests.cs(8,36): error CS0246: The type or namespace name 'IClassFixture<>' could not be found …
Build FAILED.
STEP2_EXIT=1
$ bash step3.sh          # 落ちた回でも後片付けは効いている
OK: src/ に残骸なし（git status --short --ignored -- src/ が空）。
$ REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js
AssertionError: templates/unit-template/backend/Services/SampleService/tests/SampleService.Tests に
global using Xunit; が無い。ImplicitUsings は Xunit を含まないため [Fact] が CS0246 で落ちる（#830 の実害）
```

**E2E-3**（テストクラスを `internal` へ）: `Build FAILED. STEP2_EXIT=1`。

**E2E-4: 「1 件だけ動いて 1 件が拾われない」**（`[Fact(Skip = "mutation")]`）:

```console
  Skipped SampleService.Tests.Unit.CreateSampleHandlerTests.Handle_名前を与えるとイベントに反映される [1 ms]
  Passed SampleService.Tests.Integration.HealthEndpointTests.Health_は200を返す [213 ms]
template=unit-template expected_test_attributes=2 executed_passed=1
::error::templates/unit-template/backend/backend.slnx の実行件数 1 が [Fact]/[Theory] の 2 を下回った。
STEP2_EXIT=1
```

**この回は `dotnet test` 自身が成功を返している**（`Skip` は失敗ではない）。
**下限判定が無ければ緑と読んでいた。**

**復元**: いずれも backup から書き戻し、`git diff --stat -- <file>` が空であることを確認した。

### `scripts/scripts.repo.test.js` の追記が中盤に触れていないこと

```console
$ git diff -U0 scripts/scripts.repo.test.js | grep '^@@'
@@ -8192,0 +8193,180 @@ module.exports = ({ ok, assert }) => {
$ git diff --numstat scripts/scripts.repo.test.js
180	0	scripts/scripts.repo.test.js
```

## 計画書との差異

なし。IADR-0060 決定 3・決定 4 に反しない（`templates/` は `src/` 外のまま・どの slnx にも登録しない）。

## 未決事項（親へ報告する）

1. **射程外の追随 3 件**（規則 10 で引いた）: `scripts/check-cpm-versions.js:43,357` と
   `scripts/check-backend-libraries.js:142,458` の「他で捕まらない」、
   `templates/unit-template/README.md` の新経路への導線。**並行レーンの領域のため本 PR では触らない。**
2. **`lint` ジョブの同型の穴**（`dotnet format` が雛形に当たらない）。`--artifacts-path` を解さない
   ため同ジョブへは入れられない。**別 issue が要る。**
3. **`template-backend-build` を必須チェックにするか。** `docs/ai-workflow.md` の表の更新と
   ブランチ保護の設定が要り、**いずれも本 PR の射程外**（IADR-0182: 設定は AI では完結しない）。
4. **IADR は起こしていない**（採番衝突を避ける指示による）。決定 3 に反しないと判断したため
   改定 IADR は不要と考えるが、**「複製してビルドする」という運用を ADR に残すか**は親の判断を仰ぐ。
