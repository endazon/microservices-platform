---
title: 作業仕様書 — 雛形 unit-template の Tests/ を実サービスと同じ 3 段の鏡写しへ揃える（#1146）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0065
  - ADR-0068
  - IADR-0282
  - IADR-0319
  - IADR-0321
  - IADR-0334
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md (Accepted 2026-08-30) 決定 1・3・4
  - planning:projects/microservices-platform/07_adr/ADR-0068_three-level-slice-split-rule.md (Accepted 2026-08-30) 決定 1・2
related_specs:
  - ./20260831_issue-1063_tests-mirror-body-structure.md
  - ./20260818_issue-830_template-backend-ci-build.md
---

# 作業仕様書: 雛形の `Tests/` を 3 段の鏡写しへ揃える（#1146）

起点: 実装 issue #1146（#1063 の申し送り 2。`.ai-context/specs/20260831_issue-1063_tests-mirror-body-structure.md` §7）。

基点 `origin/develop` = **`3d0a7048`**。`git rev-parse --is-shallow-repository` = **`false`**
（履歴の打ち切りではないので `git log` を出典に使える）。

## 0. issue 本文の誤り（先に正す）

🔴 **issue #1146 本文は鏡写しの規則を [[IADR-0331]] としているが、これは誤りである。**
`IADR-0331` は `planning-submodule-residual-references`（planning submodule 撤去後の残存参照）であり、
テストの鏡写しとは無関係である。**規則の正本は [[IADR-0334]]**
（`tests-mirror-target-resolution`。#1063 で Accepted）である。本仕様書は `IADR-0334` に従う。
（受け入れ基準の文言としては「実サービスと同じ規則」で読み替える。）

## 1. 母集合（着手時に自分で引き直した。issue の数えを転記していない）

### 軸 1 — 雛形の `Tests/` 配下（追跡下のパスから引く）

```console
$ git ls-files 'templates/unit-template/backend/Services/SampleService/Tests/*'
templates/unit-template/backend/Services/SampleService/Tests/Features/CreateSampleHandlerTests.cs
templates/unit-template/backend/Services/SampleService/Tests/Features/HealthEndpointTests.cs
templates/unit-template/backend/Services/SampleService/Tests/GlobalUsings.cs
templates/unit-template/backend/Services/SampleService/Tests/SampleService.Tests.csproj
```

**4 件**（`.cs` 3 ＋ `.csproj` 1）。`templates/` 配下に他の雛形は無い
（`git ls-files templates/ | wc -l` = 37、すべて `unit-template/`）。

### 軸 2 — 雛形の本体側（鏡写しの相手。issue の 2 つ目の「やること」）

```console
$ git ls-files 'templates/unit-template/backend/Services/SampleService/*' | grep -v '/Tests/'
.../Domain/SampleAggregate.cs
.../Features/Samples/Create/Command.cs
.../Features/Samples/Create/Endpoint.cs
.../Features/Samples/Create/Handler.cs
.../Features/Samples/Create/SampleCreated.cs
.../Program.cs
.../README.md
.../SampleService.csproj
```

**本体側は既に `ADR-0068` / `IADR-0319` と揃っている**（後述 §2 の判定を実施した）。**触らない。**

### 軸 3 — 雛形を参照している文書・検査器（`git grep -l "unit-template"`）

```console
$ git grep -l "unit-template" -- . | wc -l
89
$ git grep -l "unit-template" -- . ':!.ai-context/'
CHANGELOG.md  docs/how-to/adding-a-unit-submodule.md  docs/tests/TEST_STRATEGY.md
scripts/check-backend-libraries.js  scripts/check-cpm-versions.js  scripts/check-xunit1051-ratchet.js
scripts/scripts.repo.test.js  scripts/setup.sh  scripts/xunit1051-baseline.json
src/README.md  src/platform/frontend/README.md  src/platform/frontend/src/app/Layout.tsx
src/platform/frontend/tsconfig.app.json  src/platform/frontend/vite.config.ts
src/plopfile.js  src/pnpm-lock.yaml  src/pnpm-workspace.yaml
templates/unit-template/README.md  templates/unit-template/frontend/tsconfig.json
```

89 件のうち **70 件は `.ai-context/`（凍結記録。書き換えない）**、1 件は `CHANGELOG.md`（自動生成。
手で書き足さない）。残り 18 件を **「`Tests/` のフォルダ構造に依存するか」**で仕分けた。

| 参照元 | 構造依存 | 判定 |
| --- | --- | --- |
| `templates/unit-template/README.md` | **する**（§構成のツリー L26-28・L108 の散文） | **直す** |
| `templates/.../Services/SampleService/README.md`（`unit-template` 文字列を含まないため上の一覧に出ない。**軸 3 を文字列だけで引くと落ちる**ので、雛形配下の `*.md` を別途走査して拾った） | **する**（`Tests/` の行） | **直す** |
| `templates/.../Tests/SampleService.Tests.csproj` | **する**（コメントが `Tests/Features/`・`Tests/Domain/` を説明） | **直す** |
| `docs/how-to/adding-a-unit-submodule.md` | **する**（§1 の最小構成ツリーが `Tests/<Name>.Tests.csproj` の 1 行だけ） | **直す** |
| `docs/tests/TEST_STRATEGY.md` | **する**（「雛形は…がこの形を示す」「鏡写しは 14 サービス全件で済んでいる」） | **直す** |
| `scripts/check-xunit1051-ratchet.js` | しない（`SCAN_ROOTS = ['src','templates']` を**再帰**走査。深さに依存しない） | 触らない |
| `scripts/xunit1051-baseline.json` | しない（キーは `.csproj` のパス。`.csproj` は動かない） | 触らない |
| `scripts/check-backend-libraries.js` / `check-cpm-versions.js` | しない（`*.props.sample` / `*.csproj` のパスのみ） | 触らない |
| `scripts/scripts.repo.test.js` | しない（一時ツリーの合成パスであって実雛形を読まない） | 触らない |
| `scripts/setup.sh` | しない（`backend.slnx` への言及のみ） | 触らない |
| `src/README.md` / `src/platform/frontend/**` / `src/plopfile.js` / `src/pnpm-*.yaml` / `templates/unit-template/frontend/tsconfig.json` | しない（**フロントエンド**の雛形契約。バックエンドの `Tests/` と無関係） | 触らない |
| `.github/workflows/ci.yml` の `template-backend-build`（`templates/` で引くと出る。`unit-template` の文字列は持たない —— **これも文字列 1 本では落ちる位置**） | しない（`templates/*/backend/backend.slnx` を glob し、`[Fact]/[Theory]` を `--include='*.cs'` で**再帰**に数える） | 触らない（**再実行して緑を実測する**） |

> **陽性対照**（`unit-template` の文字列 1 本を信用しない）: 誤りの側の文字列で引き直した。
>
> ```console
> $ git grep -n "Tests/Features\|Tests/Domain" -- . ':!.ai-context/' ':!CHANGELOG.md' | wc -l
> 16
> ```
>
> 内訳: **10 件**は `docs/tests/*.md` の**実サービス**への実パス参照（`src/knowledge/...`）で雛形とは
> 無関係。**2 件**は規則を述べる散文（`docs/tech/tech-requirements.md` L148・`docs/tests/TEST_STRATEGY.md`
> L304）。**4 件**が雛形の実体（`templates/unit-template/README.md` L108 ＋ `Tests/` 配下 3 件）。
>
> 🔴 **この引きでも `templates/.../SampleService/README.md`（`Features/・Domain/（実装の鏡写し）`）と
> `.github/workflows/ci.yml` は出ない。** どちらの文字列も持たないためで、
> **雛形配下の全ファイル走査と `templates/` 文字列での走査を重ねて初めて拾えた。**
> 上表の「直す」5 件は 3 本の軸の**和**である。

### 軸 4 — 検査器の新設有無

**新設しない。** `IADR-0334` §結果が「機械検査は置かない（テストの主題の静的判定にはシンボル解決が要る）。
同型の事故が 2 回起きたら検討する。**本 IADR は 0 回目の記録である**」と決めている。
雛形の乖離は本 issue で 1 回目であり、`CLAUDE.md` の「同型の事故が 2 回起きたら」に届かない。

## 2. 判定（`IADR-0334` の適用結果）

### 本体側（issue の 2 つ目の「やること」）

`Features/Samples/Create/` の 4 ファイルは**いずれも `Create` 操作 1 つにしか使われない**ため
`ADR-0068` 決定 2 / `IADR-0319` により 3 段目で正しい。`Domain/SampleAggregate.cs` は集約 1 件で
`Domain/` 直下、`Program.cs` はサービス直下。`Infrastructure/` と `Common/` は実体が無いため
**存在しない**（`ADR-0065` 決定 4 / `IADR-0321`。空枠を置かない）。**本体側の変更は無い。**

### テスト側

| 現在 | 移送先 | 根拠 |
| --- | --- | --- |
| `Tests/Features/CreateSampleHandlerTests.cs` | `Tests/Features/Samples/Create/` | `IADR-0334` 決定 3（`CreateSampleHandler` を直接呼ぶ。定義は `Features/Samples/Create/Handler.cs`） |
| `Tests/Features/HealthEndpointTests.cs` の `/health` | `Tests/` **直下** | `IADR-0334` 決定 4(b)（`Program.cs` 由来。本体でも `Program.cs` はサービス直下） |
| 同ファイルの「作成スライスの入口が疎通する」 | `Tests/Features/Samples/Create/CreateSampleEndpointTests.cs`（新規） | `IADR-0334` 決定 2（叩く操作は `POST /samples` の 1 つ → 3 段目） |
| `Tests/GlobalUsings.cs` | `Tests/` 直下（**据え置き**） | `IADR-0334` 決定 4(a)（テスト専用の器） |
| `Tests/SampleService.Tests.csproj` | 据え置き | プロジェクトは 1 本のまま（`ADR-0065` 決定 3） |

**名前空間はフォルダへ追随させる**（`IADR-0334` 決定 5）。`using` は 1 行も足さない。

### 本仕様書で下した判断 2 件（`IADR-0334` の適用であり、新 IADR は起こさない）

1. 🔴 **`HealthEndpointTests` を 2 つに割る。** #1063 は実サービスで「テストの内容を書き換えない」を
   制約に置いたが、**雛形は規則の例示物**であり、`Program.cs` 由来の検証とスライスの検証を 1 クラスに
   同居させたまま `Tests/` 直下へ置くと、**雛形が「スライスのテストを直下に置いてよい」と教えてしまう。**
   `IADR-0321` が正した事故（**撤回された形を雛形が再生産する**）と同型である。割るのは
   `IADR-0334` 決定 2・4 を素直に適用した結果であって、新しい規則ではない。
2. **`Tests/Domain/SampleAggregateTests.cs` を新設する。** 雛形の 2 つの README は**すでに
   `Tests/Domain/` を構成図に書いている**が、実体は無い。これは `IADR-0321` が名指しした
   「**適合の見え方**（枠だけが揃っていて中身が無い）」そのものである。選択肢は
   (a) 実体を与える／(b) README から `Domain/` を消す の 2 つで、**本体に `Domain/SampleAggregate.cs` が
   在り鏡写しの相手が実在する**以上 (a) を採る。`SampleAggregate.IsNamed` は現在 1 件も検証されていない。

## 3. 変更するファイル（宣言済みファイル領域）

- `templates/unit-template/backend/Services/SampleService/Tests/**`（移送 2・新設 2・コメント修正）
- `templates/unit-template/README.md`
- `templates/unit-template/backend/Services/SampleService/README.md`
- `docs/how-to/adding-a-unit-submodule.md`
- `docs/tests/TEST_STRATEGY.md`
- `.ai-context/specs/20260903_issue-1146_template-tests-mirror.md`（本書）

🔴 **`src/<unit>/` の本体側 `Tests/` には触らない**（#1063 で済んでいる）。**検査器は 1 バイトも触らない。**

## 4. 受け入れ基準 → 検証の写像

| # | 受け入れ基準（#1146） | 検証 |
| --- | --- | --- |
| 1 | 雛形の `Tests/` が実サービスと同じ規則で並んでいる | 移送後のツリー（§2 の表と一致） |
| 2 | 名前空間がフォルダへ追随している | `grep -rn '^namespace' templates/.../Tests/` |
| 3 | 雛形の CI（#830 のビルド検査）が緑 | `template-backend-build` ジョブと**同じ手順**をローカルで実走（複製 → `dotnet build` → `dotnet test` → `[Fact]/[Theory]` 数と実行数の突合）。**一時ユニットはコミットしない** |
| 4 | 本体側の段が `ADR-0068` と揃っている | §2 の判定（変更なし） |
| 5 | 文書・トレーサビリティ検査が緑 | `check-doc-links` / `check-trace-blocks` / `check-doc-updated` / `check-test-traceability` / `scripts.test.js` |
| 6 | 整形が緑 | 複製先で `dotnet format --verify-no-changes` |

## 5. 移送後のツリー（実測）

```console
$ find templates/unit-template/backend/Services/SampleService/Tests -type f | sort
.../Tests/Domain/SampleAggregateTests.cs                              ← 新設（Domain/ の鏡写し）
.../Tests/Features/Samples/Create/CreateSampleEndpointTests.cs        ← 新設（3 段目・決定 2）
.../Tests/Features/Samples/Create/CreateSampleHandlerTests.cs         ← 移送（3 段目・決定 3）
.../Tests/GlobalUsings.cs                                             ← 据え置き（器・決定 4a）
.../Tests/HealthEndpointTests.cs                                      ← 移送（Program.cs 由来・決定 4b）
.../Tests/SampleService.Tests.csproj                                  ← 据え置き（1 プロジェクト）

$ grep -rn "^namespace" .../Tests --include='*.cs'
Domain/SampleAggregateTests.cs:                      namespace SampleService.Tests.Domain;
Features/Samples/Create/CreateSampleEndpointTests.cs: namespace SampleService.Tests.Features.Samples.Create;
Features/Samples/Create/CreateSampleHandlerTests.cs:  namespace SampleService.Tests.Features.Samples.Create;
HealthEndpointTests.cs:                              namespace SampleService.Tests;
```

`using` は 1 行も足していない（`IADR-0334` 決定 5）。`GlobalUsings.cs`（`SampleService.Tests`）は
外側の名前空間探索により 3 段目からも無修飾で見える —— **実測でビルドが通ることが対照になっている。**

## 6. 検証結果（すべて実走。宣言だけの記録は置かない）

### 6-1. 雛形から一時ユニットを起こしてビルド・テストする（`ci.yml` の `template-backend-build` と同手順）

`templates/unit-template/backend` を `src/.template-buildcheck-unit-template/backend` へ複製し、
`bin`/`obj` と `*.sample` を落として（＝ submodule 配置後の位置を模す）実行した。

```console
$ dotnet build src/.template-buildcheck-unit-template/backend/backend.slnx --configuration Release
ビルドに成功しました。  0 個の警告  0 エラー

$ dotnet test src/.template-buildcheck-unit-template/backend/backend.slnx --no-build --configuration Release
  成功 SampleService.Tests.Domain.SampleAggregateTests.IsNamed_は空白のみの名前を名付け済みと見なさない(name: "sample", expected: True)
  成功 SampleService.Tests.Domain.SampleAggregateTests.IsNamed_は空白のみの名前を名付け済みと見なさない(name: "", expected: False)
  成功 SampleService.Tests.Domain.SampleAggregateTests.IsNamed_は空白のみの名前を名付け済みと見なさない(name: "   ", expected: False)
  成功 SampleService.Tests.Features.Samples.Create.CreateSampleHandlerTests.Handle_名前を与えるとイベントに反映される
  成功 SampleService.Tests.Features.Samples.Create.CreateSampleEndpointTests.作成スライスの入口が疎通する
  成功 SampleService.Tests.HealthEndpointTests.Health_は200を返す
テストの合計数: 6  成功: 6

$ dotnet format src/.template-buildcheck-unit-template/backend/backend.slnx --verify-no-changes
（出力なし＝差分なし）
```

CI が置いている**実行件数の下限検査**も同じ式で再現した（移送前 2 → 移送後 4 / 実行 6）。

```console
$ expected=$(grep -rhE '^[[:space:]]*\[(Fact|Theory)' <stage> --include='*.cs' | wc -l)
$ executed=$(grep -cE '^[[:space:]]+成功[[:space:]]+[A-Za-z]' <log>)
expected_test_attributes=4 executed_passed=6
```

> 🔴 **ローカルは `dotnet` の表示ロケールが ja のため、CI の式（`Passed`）はそのままでは 0 を数える。**
> **0 件を「実行 0」と読むと誤る**ので、語を `成功` に置換して数えた。CI（ロケール en）では原式が当たる。
> 複製は検査後に `rm -r` し、`git status --short --ignored -- src/` に複製の残骸が無いことを確認した
> （残るのは `src/platform/backend/Shared/**/bin|obj` のみで、これは `--artifacts-path` を付けずに
> ローカル実行した副産物。CI は同オプションを渡すので `src/` を汚さない）。

### 6-2. リポジトリの検査器

| 実行 | 結果 |
| --- | --- |
| `node scripts/check-doc-links.js` | OK（1079 件の Markdown。破損リンク 0） |
| `node scripts/check-trace-blocks.js` | OK（167 件） |
| `node scripts/check-test-traceability.js` | OK（55 件中 52 件写像済み・残 3 件は allowlist 済み） |
| `node scripts/check-xunit1051-ratchet.js` | OK（baseline と実在プロジェクトが双方向一致。**キーは `.csproj` のパスで移送の影響を受けない**ことを実測） |
| `node scripts/check-cpm-versions.js` | OK（41 プロジェクト / 243 件・直書き 0） |
| `node scripts/check-backend-libraries.js` | OK（新規混入 0） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **674 tests passed** |
| `node scripts/check-doc-updated.js` | コミット後に実行（§6-3） |

### 6-3. コミット後

| 実行 | 結果 |
| --- | --- |
| `node scripts/check-doc-updated.js` | OK（変更された `docs/` の Markdown 2 件に `updated:` の据え置き無し。`updated:` を持たない 1 件＝`adding-a-unit-submodule.md` は対象外） |
| `node scripts/check-commit-messages.js` | OK（`origin/develop..HEAD` 1 件が規約に適合） |
| `node scripts/gen-knowledge-graph.js --check` | OK（ノード 1075 / エッジ 8781。in-repo エッジ先の実在違反 0） |
| `node scripts/check-doc-type-vocabulary.js` | OK（1050 件） |
| `node scripts/check-plan-id-qualification.js` | OK（2320 件） |

**移送は 2 件とも rename として履歴に残った**（`git commit` の出力が `rename ... (52%)` /
`rename ... (50%)` を報告した。**内容も編集したので類似度は 100% ではない**）。

## 7. 本 PR で扱わない（申し送り）

1. 🔴 **`docs/tech/tech-requirements.md` L142-143 の陳腐化**: 「**操作単位のスライス分割
   （`Features/<集約>/<操作>/` の 3 分割）はまだ行っていない** —— 器の移送までが移送波の射程であり、
   端点は集約フォルダ直下に 1 枚のまま置かれている」と書かれているが、**実測は逆である。**

   ```console
   $ git ls-files -- src | grep -cE '^src/[^/]+/backend/Services/[^/]+/Features/[^/]+/[^/]+/[^/]+\.cs$'
   156        # 3 段目（<集約>/<操作>/）
   $ git ls-files -- src | grep -cE '^src/[^/]+/backend/Services/[^/]+/Features/[^/]+/[^/]+\.cs$'
   37         # 集約直下（ADR-0068 決定 1 の登録表など、2 段目が正のもの）
   ```

   **本 issue の母集合には入らない**（雛形を正として参照する記述ではなく、実サービスの現況の記述）。
   → **起票せず報告に残す**（依頼者の裁定を仰ぐ。**起票前に既存 issue の検索が要る**）。
2. **単体 / 結合のトレイト付与**は #1145 が持つ（#1063 §7-1）。本 PR は雛形にもトレイトを入れない
   —— 実サービスと雛形が同時に変わるべき事柄であり、#1145 の射程である。
3. **AST（`src/ai-stock-trading`）側**: 本作業ツリーでは submodule が未 populate であり、
   **AST に同型の雛形が在るかを実測できていない**（`ls src/ai-stock-trading` は空を返す。
   これは「無い」の証拠ではない）。AST の `ADR-0065` 追随は `AST#613` が open で持っている
   （#1063 §7-3 が確認済み）ので、**新規起票はしない。**
