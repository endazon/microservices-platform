---
title: 作業仕様書 — 8 要素プロジェクト時代の残存記述を現況へ直す（テストプロジェクト名・Foundation/Composable の一般化）
type: spec
status: done
related_ids:
  - NFR
  - ADR-0065
  - IADR-0027
  - IADR-0280
  - IADR-0282
author: claude
created: 2026-08-30
updated: 2026-08-30
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0065_backend-service-single-project-vsa.md
related_specs:
  - "20260828_wave45-vsa-migration.md"
  - "20260830_issue-1061_remove-worker-layer.md"
  - "20260830_src-readme-drop-transitional-layout.md"
issue: "#1080"
---

# 作業仕様書 — 8 要素プロジェクト時代の残存記述を現況へ直す

## 何が古いか

`IADR-0282`（8 要素プロジェクトの撤回・単一プロジェクト＋VSA/DDD フォルダ規範）と
計画 `ADR-0065`（`.Worker` 接尾辞・`Worker/` 中間層の廃止）で古くなった記述が、
`docs/` ・ `templates/` ・ `deploy/` ・ `.github/` に残っている。

古さは 3 種類ある。

1. **テストプロジェクト名**を `<Service>.Api.Tests` と書いている（実体は `<Service>.Tests`）。
   パスも `Services/<Name>/tests/<Name>.Api.Tests` と書いており、実体は `Services/<Name>/Tests/`。
2. **`Foundation/` / `Composable/` の区分を「各プロジェクト内」の話として一般化**している。
   実体はユニット共有プロジェクト（`Shared/` 配下）にしか無い。
3. **樹形図・雛形の実体不一致**（`Worker/<Name>.Worker.csproj` の枝、`SampleService.Api.csproj`、
   層プロジェクト前提の相対参照の階層数）。

## 🔴 実在の確認（2026-08-30・本作業で自分で数え直した）

**`Foundation/` は消えていない。** `git ls-files '*Foundation/*'` の実測は **60 ファイル**である。

| 置き場 | ファイル数 |
| --- | ---: |
| `src/platform/backend/Shared/Platform.Shared.Infrastructure/Foundation/` | 28 |
| `src/platform/backend/Shared/Platform.Shared.Infrastructure.Tests/Foundation/` | 20 |
| `src/platform/backend/Bff/Platform.Bff/Foundation/` | **12** |
| **合計** | **60** |

`git ls-files '*Composable/*'` は **10 ファイル**である。

| 置き場 | ファイル数 |
| --- | ---: |
| `src/platform/backend/Shared/Platform.Shared.Infrastructure/Composable/Adapters/Storage/` | 4 |
| `src/platform/backend/Shared/Platform.Shared.Infrastructure.Tests/Composable/Adapters/Storage/` | 6 |
| **合計** | **10** |

`Services/` 配下の `Foundation/` / `Composable/` は **0 件**。
`*Worker*.csproj` は **0 件**。`Services/*/src/` ・ `Services/*/tests/` の中間層も **0 件**
（`git ls-files 'src/*/backend/Services/*/**.csproj'` は 28 件＝14 サービス × 本体 1 ＋ テスト 1）。

🔴 **issue #1080 は `Foundation/` を「48 ファイル（本体 28 ／ テスト 20）」と書いているが、
これは `Platform.Bff/Foundation/` の 12 件を数え落としている。実測は 60 である。**
本作業では **`Shared/` だけでなく `Bff/Platform.Bff` にも現役である**ことを前提に文言を書く。

したがって `Foundation/` / `Composable/` の記述は **消さず、当てはまる範囲へ限定する**。
消すと `IADR-0027` の固定/可変の分類そのものが失われる。

### 移送先の対応（記述を直すときの写像）

| 8 要素時代の言い方 | 現況 |
| --- | --- |
| `<Service>/Composable/Steps/` | `<Service>/Features/<集約>/<操作>/`（`*Consumer.cs`） |
| `<Service>/Composable/Adapters/` | `<Service>/Infrastructure/ExternalServices/` |
| `<Service>/Foundation/Ports/` | `<Service>/Domain/Ports/` |
| `<Service>/Foundation/Domain/` | `<Service>/Domain/` |
| `<Service>/Foundation/Endpoints/` `Foundation/Services/` | `<Service>/Features/<集約>/…` |
| `tests/<Name>.Api.Tests/` | `Tests/<Name>.Tests.csproj` |

（実測で確認: `IPipelineStep` 実装は 8 件すべて `Features/<集約>/<操作>/`、
ポートは `Domain/Ports/`、外部アダプタは `Infrastructure/ExternalServices/`。）

## 母集合（規則 9: 誤りの側の文字列で全走査してから挙げる）

**issue 本文の「22 ファイル」は他人の数えである。転記せず、着手時に自分で引き直した。**

走査した誤りの側の文字列（issue の 8 語に 2 語を足した）:

```
\.Api\.  \.Worker\.  Composable/  Foundation/
Services/[A-Za-z]+/(src|tests)/          （src/ tests/ 中間層。issue の src/<ServiceName>. / tests/<ServiceName>. を一般化）
\.Application\b  \.SharedKernel\b
SampleService\.(Api|Application|Infrastructure)   （★ 追加。雛形の層プロジェクト名。issue の 8 語では拾えない）
```

パスで絞り、拡張子・行フィルタでは絞らない（規則 3・4）。除外は
`src/ai-stock-trading`（submodule）・`.ai-context/`（凍結記録）・`CHANGELOG.md`（自動生成物）のみ。

### 走査結果（追跡下・上記 3 除外のみ）

ヒットは **63 ファイル**。うち **是正対象は 27 ファイル**、**現況として正しい・
または恒久的に対象外が 36 ファイル**である。

#### 是正対象（27 ファイル）

| 分類 | ファイル |
| --- | --- |
| A. テストプロジェクト名（15） | `docs/tests/` の `FR-01` `FR-04` `FR-09` `FR-10` `FR-11` `FR-14` `FR-15` `FR-19_private-note-wikijs-exclusion` `FR-19_private-notes-lifecycle` `FR-20` `SC-02` `SC-05` `SC-06` `UC-11` `TEST_STRATEGY` |
| B. Foundation/Composable の一般化（6） | `docs/tech/tech-requirements.md` `docs/tech/composability-classification.md` `docs/tech/composable-component-guide.md` `docs/functional/FR-14_composability.md` `templates/unit-template/README.md` `deploy/helm/microservices-platform/files/README.md` |
| C. 樹形図・雛形の実体不一致（3） | `.github/workflows/ci.yml` `docs/how-to/adding-a-unit-submodule.md` `templates/unit-template/backend/Directory.Packages.props.sample` |
| D. ★ issue に無い新規発見（3） | `docs/operations/llm-model-pin-runbook.md` `docs/screens/SC-06_datasource-management.md` `src/README.md` |

#### issue の数えとの差

| 差 | 内容 |
| --- | --- |
| ＋1 | issue 表 #3 は 14 ファイルを列挙しながら件数を **13** と書いている（`FR-14` を数え落としている）。実測 14。 |
| ＋3 | **D の 3 件は issue の 22 に無い。** 下記のとおり自分の走査で出た。 |
| ＋1 | `templates/unit-template/backend/Directory.Packages.props.sample`（層プロジェクト名のコメント）。issue の 8 語では拾えず、**9 語目 `SampleService\.(Api|Application|Infrastructure)` を足して初めて出た**（規則 2: あり得る形をすべて列挙してから引く）。 |
| 計 | issue **22** → 実測 **27**。 |

**D の 3 件の中身**

- `docs/operations/llm-model-pin-runbook.md` —— **Runbook のコマンドが動かない。**
  `src/platform/backend/Services/LlmGateway/src/LlmGateway.Api/appsettings.json` を 3 箇所で参照するが、
  実体は `src/platform/backend/Services/LlmGateway/appsettings.json`。`node -e require(...)` が落ちる。
  issue が `docs/operations/**` をファイル領域に挙げていないのは、issue の走査が
  `Services/<Name>/src/` 中間層の形を母集合へ入れていなかったためと見られる。
- `docs/screens/SC-06_datasource-management.md:138` —— 実測の出所として
  `.../Foundation/Services/DataSourceSyncHostedService.cs` を挙げるが、実体は
  `Features/DataSources/Sync/DataSourceSyncHostedService.cs`。
- `src/README.md` §依存規則 1・2 —— `Foundation/` → `Composable/` の一方向依存と
  `Composable/Steps/` の段の規則を**リポジトリ全体の依存規則**として書いている。
  #1079 は同ファイルの**レイアウト節**だけを畳んだため、依存規則節は一般化のまま残った。
  規則そのものは `check-unit-dependencies.js` が今も強制しているので**消さず、範囲を限定する**。

#### 是正しない（現況として正しい・36 ファイル中の主なもの）

| 対象 | 理由 |
| --- | --- |
| `docs/screens/SC-05` `SC-09` `SC-11` ／ `docs/tests/SC-09` `SC-11` `FR-14`(表) `FR-15`(表) ／ `docs/security/security.md` ／ `src/Directory.Packages.props` ／ `src/knowledge/frontend/.../driftView.ts` ／ `scripts/check-bff-authz-docs.js` | 指しているのは `Platform.Shared.Infrastructure/Foundation/` または `Platform.Bff/Foundation/` で、**実在する**（上の実測 60 件の一部）。直すと嘘になる。 |
| `docs/how-to/session-handoff.md` | issue が恒久対象外に指定。**「`git grep -l '\.Api\.'` は `.Worker.Composable` を拾わない」という母集合の引き方の教訓の題材そのもの**であり、書き換えると教訓が消える。 |
| `docs/tech/tech-requirements.md` L137（`従前ここは…と書いていた` の引用）・L169（`IADR-0056 決定 3 の「2 プロジェクト」`） | **過去の記述を明示的に引用している歴史記述**。引用文を書き換えると引用でなくなる。 |
| `scripts/check-backend-libraries.js` `check-coverage-floor.js` `check-cpm-versions.js` `check-image-mapping.js` `check-unit-dependencies.js` `check-event-topology.js` `scripts.repo.test.js` `scripts/README.md` | 旧名は**検査器の自己試験に与える合成入力**（パス解析器が旧レイアウトでも壊れないことを見る）であり、リポジトリの現況の主張ではない。書き換えると検査器の意味論が変わるため、**別作業**とする。 |
| `deploy/docker-compose.yml` `scripts/k8s-local-images.sh` ＋ `check-image-mapping.js` の対応 fixture | 指しているのは **AST（`src/ai-stock-trading`）submodule 側**のサービス（`ConfigurationService` / `RiskManagementService` / `MarketMonitorService`）のレイアウトである。AST は本リポの `ADR-0065` の射程外で、**本リポからは触らない**。 |
| 各サービスの `.csproj` 先頭コメント（`層プロジェクト（.Domain / .Application / …）は撤去し` 等 14 件） | **移送の経緯を正しく述べている**。現況の主張ではない。 |
| `.ai-context/adr/` `.ai-context/specs/` `.ai-context/superpowers/` | 凍結記録。**本文プロズを後から書き換えない**（CLAUDE.md）。 |
| `CHANGELOG.md` | 自動生成物。手で書き足さない。 |
| `src/ai-stock-trading/**` | submodule。 |

### 凍結の射程についての自己判断（`.claude/rules/traceability.repo.md` §Superseded「凍結の射程」）

同節は「`.ai-context/specs/` は `［YYYY-MM-DD 追記 / #NNN］` 書式の経過追記が**可**／
`.ai-context/superpowers/` は**不可**」と定める。本作業では:

- **`.ai-context/adr/` `.ai-context/superpowers/` は一切触らない。**
- **`.ai-context/specs/` の既存記録も触らない。** 本作業は「決定を変える追記」でも
  「状態欄の是正」でもなく、単なる用語の追随であり、追記する価値のある新事実が
  既存の specs に対して生じないためである（新事実は本仕様書と PR に置く）。
- 例外は **本仕様書（作業中の PR の仕様書）**のみで、これは凍結対象ではない。

### 規則 10（この変更で新たに誤りになる自分の記述）の引き直し

本 PR が新たに書く語は `<Name>.Tests` / `Tests/` / `Features/<集約>/<操作>/` /
`Domain/Ports/` / `Infrastructure/ExternalServices/` / `Shared/` 限定の `Foundation/`。
これらの**新しい側**で全走査し、矛盾する自分の記述が無いことを確認する（PR 前）。
**導出値（60 / 10 / 28 / 0）は走査ではなく数え直した値である**（上表）。

## 変更方針

- **A** は表示テキストの名前とパスを実体に合わせる。リンク先パスも実体へ合わせる。
- **B** は**削らず限定する**。「各プロジェクト内」→「共有基盤プロジェクト（`Shared/` 配下・`Platform.Bff`）」。
  サービス側は移送先（上の写像表）を書く。`IADR-0027` の固定/可変の分類は残す。
- **C** は雛形の実体（`SampleService.csproj`・相対参照 `..\ × 4`）に合わせる。
- **D** はパスを実体へ直し、`src/README.md` の依存規則は適用範囲を明示する。

## 受け入れ基準

- [ ] 上記 27 ファイルで、実在しない構造を**現況として**述べている箇所が 0 件である
- [ ] `docs/tests/**` のテストプロジェクト名が `git ls-files 'src/*/backend/Services/*/Tests/*.csproj'` と一致する
- [ ] B の 6 ファイルが `Foundation/` の現役（`Shared/` ・ `Platform.Bff`）を否定していない
- [ ] `.github/workflows/ci.yml` のコメントが雛形の実体（`SampleService.csproj`・`..\ × 4`）と一致する
- [ ] `docs/operations/llm-model-pin-runbook.md` のコマンドが実在するパスを指す
- [ ] `check-trace-blocks` / `check-doc-links` / `check-doc-updated` / `gen-knowledge-graph --check` /
      `check-adr-numbering` / `check-commit-messages` / `check-doc-type-vocabulary` /
      `check-doc-status-vocabulary` / `check-reading-budget` が緑
- [ ] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が緑
- [ ] `dotnet build src/knowledge/backend/backend.slnx` が緑（`templates/` を触るため）

## 環流（計画・別 issue）

本作業の走査で、**本 issue の射程外だが古い記述**を 2 件見つけた。別 issue として起票する。

1. `docs/how-to/adding-a-unit-submodule.md` —— `KnowledgePlatform.Shared.Contracts` /
   `KnowledgePlatform.Shared.Infrastructure` という**存在しないプロジェクト名**（#228 で
   `Platform.Shared.*` へ改名済み）。本 PR は同じコードブロックの階層数を直すため、
   **書き直す以上は実在名で書く**（見た目の副作用として是正される）。
2. 同ファイル —— 「本体リポと各ユニットは private な `planning` を submodule として持つ」。
   **`ADR-0048` 決定 2 / `IADR-0228` で planning submodule は撤去済み**であり、事実に反する。
   本 issue の母集合（9 語）では拾えない別系統の古さのため、**本 PR では触らず #1092 として起票した**
   （同ファイルには他に 3 箇所ある —— `submodules: recursive` を避ける理由・`.gitmodules` の件数・
   Dependabot の記述。実測で `.gitmodules` のエントリは `src/ai-stock-trading` の 1 件のみ）。

## 実施結果（2026-08-30）

- 是正 27 ファイル。うち **A 15 / B 6 / C 3 / D 3**（上表のとおり）。
- **追加で 2 箇所を直した**（着手時の走査では母集合に入らず、編集の副作用として矛盾が生じたため。規則 10）:
  - `docs/tests/TEST_STRATEGY.md` の見出し「（Unit / Integration はフォルダで分ける）」と
    末尾の「既存テストの `Unit/` / `Integration/` 区分は現状のまま」——
    **追跡下に `Unit/` / `Integration/` フォルダは 0 件**であり、同じ段落で私が書いた
    「実装の現況は `Services/<Name>/Tests/<Name>.Tests.csproj`」と矛盾していた。
  - `src/README.md` の `Foundation/` 実測値 48 → **60**（`Bff/Platform.Bff` の 12 件を補った）。
- **`check-trace-blocks.js` が 1 件で落ちた** —— `docs/tech/tech-requirements.md` の本文へ
  `ADR-0065` を可視で書いてしまった。trace ブロックの `adrs:` へ移して緑にした
  （`docs/` の書式規約はこの検査器が唯一の歯止めであり、書いた本人は気づけない）。
- 検証: `check-trace-blocks` / `check-doc-links` / `check-doc-updated` /
  `gen-knowledge-graph --check` / `check-adr-numbering` / `check-commit-messages` /
  `check-doc-type-vocabulary` / `check-doc-status-vocabulary` / `check-reading-budget` すべて緑。
  `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` = 664 tests passed。
  `dotnet build src/knowledge/backend/backend.slnx` = 0 エラー（警告 3・既存の CS0618）。
- **実測で確かめたこと**: 直した Runbook のコマンド
  （`node -e "require('./src/platform/backend/Services/LlmGateway/appsettings.json')"`）を実際に走らせ、
  `PurposeModels` が 8 用途返ることを確認した（直す前は `MODULE_NOT_FOUND` で落ちていた）。
