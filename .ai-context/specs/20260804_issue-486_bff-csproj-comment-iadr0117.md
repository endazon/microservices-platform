---
title: Knowledge.Bff.Endpoints.csproj のコメント「Shared の 2 プロジェクト」を IADR-0117（3 プロジェクト）へ追随させる
type: spec
status: done
related_ids: [NFR, IADR-0056, IADR-0057, IADR-0063, IADR-0115, IADR-0117]
author: Claude
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
related_specs:
  - ./20260804_issue-484_unit-deps-comment-iadr0117.md
  - ./20260804_issue-478_staged-policy-citation-fix.md
  - ./20260711_issue-231_unit-dependency-guard.md
  - "../adr/IADR-0117_platform-shared-kernel-placement.md"
---

# 仕様書: `Knowledge.Bff.Endpoints.csproj` のコメントを IADR-0117 の 3 プロジェクトへ追随させる

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR: 保守性 — csproj のコメントが旧制約値のままだと、
  参照を足そうとした実装者が `Platform.Shared.Kernel` を「許されていない参照先」と誤読する）
- ユースケース（UC）/ 画面（SC）: なし
- 関連 ADR:
  [IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md)（ユニット外参照の 2 → 3 改定。**是正後の正**）／
  [IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md)（決定 3。IADR-0117 が部分改定した被改定側）／
  [IADR-0057](../adr/IADR-0057_unit-dependency-machine-check.md)（依存規則の機械検査の方式根拠）／
  [IADR-0063](../adr/IADR-0063_bff-unit-endpoint-composition.md)（本 csproj そのものの根拠）／
  [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)（キット由来ファイルの分類。編集可否の判断根拠）
- 先行判断の一次情報:
  [20260804_issue-484](./20260804_issue-484_unit-deps-comment-iadr0117.md) 「据え置き」表とフォローアップ 1
  （本件を #484 のスコープ外として記録し、別 issue 候補としていた）
- 規約: [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)
- 本リポジトリの起点: #486（先行 #484 → PR #485 / 検出元 #478 / 親 #454）

## 目的・背景

[IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md) は
[IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md) 決定 3 を部分改定し、
ユニット外から参照可能な `src/platform/backend/Shared/` のプロジェクトを **2 → 3**（`Platform.Shared.Contracts` /
`Platform.Shared.Infrastructure` / `Platform.Shared.Kernel`）とした。同 IADR 理由 4 番目のとおり
**改定で更新が要るのは件数を書いた文書だけ**であり、その追随は
`src/README.md` ほか（PR #455 系）→ `scripts/README.md`（PR #483 / #478）→
`scripts/check-unit-dependencies.js`（PR #485 / #484）と順に進んだ。

`src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/Knowledge.Bff.Endpoints.csproj` 12 行目のコメントは
その**最後の残り**であり、#484 の作業仕様書がフォローアップ 1 として別 issue 候補に挙げたもの。それが本 issue #486。

## 対象範囲

### grep による全量洗い出し（実測）

#### 測定条件（これを書かない実測値は再現不能 — issue #484 で 2 度学んだ教訓）

1. **対象ツリーの時点**: `origin/develop` = **`5031483`**（PR #485 マージ済み）。以下の値は
   `git grep <rev>` で**コミットを固定**して測っている（作業ツリーで測ると本作業自身の増減が混ざる）。
2. **submodule の扱い**: 本セッションの実行環境では `planning` / `src/ai-stock-trading` の両方が
   **未 populate**（`git submodule status` の先頭が `-`。`ls src/ai-stock-trading` は空）。
   したがって以下は**追跡ファイルのみ**の値であり、`src/` 配下の submodule ユニット
   `src/ai-stock-trading` の内容は含まない（populate 時の増分は本環境では測定不能）。
3. **本仕様書自身のヒットを含むか**: 本書は据え置き判断のために `2 プロジェクト` を引用するため、
   リポジトリ全体の値には本書のヒットが乗る。ただし本書は `docs/specs/` にあり **`src/` の外**なので、
   受け入れ基準 1 の `grep -rn "2 プロジェクト" src/` には影響しない。

#### 全量走査の結果（`| head` で打ち切っていない）

```bash
git grep -n "2 プロジェクト" 5031483 -- src/                                              # src/ 配下: 2 行 / 2 ファイル
grep -rn "プロジェクト" --include='*.csproj' --include='*.props' --include='*.targets' src/  # ビルドファイル: 15 行 / 15 ファイル
git grep -n "2 プロジェクト" 5031483                                                        # リポジトリ全体: 37 行 / 14 ファイル
```

`src/` 配下の csproj / props / targets は **32 ファイル**（`find src -name '*.csproj' -o -name '*.props'
-o -name '*.targets' | wc -l`）。うち `プロジェクト` を含むのは 15 ファイルで、14 は
テストプロジェクト向けカバレッジ設定の定型コメント（「**テストプロジェクトが**〜」）、
残る 1 が本件の `Knowledge.Bff.Endpoints.csproj` である。件数表記の別綴り
（`2つ` / `２ プロジェクト` / `二つ` / `two project`）・`ユニット外` / `のみ許可` を含む行も
ビルドファイル全体に対して走査したが、ヒットは同じ 1 行のみだった（**同型の残りはビルドファイルには無い**）。

| # | ファイル / 行 | 現在の記述 | 扱い |
| --- | --- | --- | --- |
| 1 | [`src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/Knowledge.Bff.Endpoints.csproj`](../../src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/Knowledge.Bff.Endpoints.csproj) 12 | `ユニット外は Shared の 2 プロジェクトのみ許可。` | **是正**（現行値としての誤り） |
| 2 | [`src/README.md`](../../src/README.md) 77 | `IADR-0056 決定 3 の「2 プロジェクト」を [IADR-0117] が 3 へ部分改定した` | 据え置き（**経緯としての 2**。同 76 行が現行値「3 プロジェクト」を既に述べており、消すと改定の記録が壊れる） |

`src/` 配下で `2 プロジェクト` を含むのはこの 2 行のみ。すなわち **`src/` の現行値としての誤りは 1 件**で、
本作業がそれを是正すると 0 件になる。

### 洗い出しで見つかった「本 issue では是正しない」箇所と理由（据え置き判断）

`src/` の外にも `2 プロジェクト` は残るが、いずれも #484 の据え置き表と同じ判断であり触らない
（本 issue のスコープは csproj のコメント 1 行。過剰修正を避ける）。

| ファイル / 行 | 内容 | 据え置きの理由 |
| --- | --- | --- |
| [`src/README.md`](../../src/README.md) 77、[`docs/tech/tech-requirements.md`](../../docs/tech/tech-requirements.md) 126、[IADR-0056](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md) 76 / 83、[IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md) 47 / 62 / 125 / 134、[`docs/adr/README.md`](../adr/README.md) 143 | 「**改定前は** 2 プロジェクトだった」という経緯としての 2 | 是正すると改定の記録が壊れる。現行値としての誤りではない |
| [IADR-0057](../adr/IADR-0057_unit-dependency-machine-check.md) 35 / 73 | 現行値の書き方で読める 2 箇所 | **`Accepted` の本文であり書き換えない**（[IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md) フォローアップ 3 で決定済み。現行値は IADR-0117 を正とする） |
| 過去仕様書（[20260711_issue-231](./20260711_issue-231_unit-dependency-guard.md) 58、[20260710_FR-14](./20260710_FR-14_repo-restructure-platform-knowledge.md) 106、[20260803_issue-455](./20260803_issue-455_backend-application-standard.md) 101 / 199、[20260804_issue-478](./20260804_issue-478_staged-policy-citation-fix.md) 108 / 132、[20260804_issue-484](./20260804_issue-484_unit-deps-comment-iadr0117.md) 各所） | 当時の事実としての 2 プロジェクト | 記述時点で正しく、IADR-0117 の経緯を語る文脈でもある |
| [`scripts/scripts.test.js`](../../scripts/scripts.test.js) 682、[20260712_issue-245](./20260712_issue-245_ai-stock-trading-unit-integration.md) 68 | `番号帯が重複する 2 プロジェクトを合成する` / `32 プロジェクト / 675 合格` | grep の**別義ヒット**（計画プロジェクト 2 件 / `32` の部分一致）。Shared の許可数とは無関係 |

### 含まないもの

- **ビルド設定の変更**。`ProjectReference` / `FrameworkReference` / `PropertyGroup` は一切触らない
  （`Platform.Shared.Kernel` は未作成であり、参照を足すことはできない）。
- 上記「据え置き」表の各行。
- `Platform.Shared.Kernel` の実体作成（IADR-0117 フォローアップ 1）。

## 設計

### 是正後の文言

先行是正（[`scripts/README.md`](../../scripts/README.md) 13 行目 /
[`scripts/check-unit-dependencies.js`](../../scripts/check-unit-dependencies.js) ヘッダ）と揃え、
3 プロジェクトを列挙したうえで **`Platform.Shared.Kernel` が未作成**である旨を併記する
（コメントの「3」と実在の「2」が食い違って読めるため。IADR-0117 決定 4）。

```xml
<!-- 横断基盤（フォワーディングクライアント・認可ポリシー定数）。ユニット外は Shared の 3 プロジェクト
     （Platform.Shared.Contracts / Platform.Shared.Infrastructure / Platform.Shared.Kernel）のみ許可。
     2 → 3 の改定は IADR-0117（Platform.Shared.Kernel は配置のみ確定で実体は未作成）。 -->
```

### XML validity

コメントのみの変更だが、複数行コメント化で XML が壊れていないことを担保する必要がある
（受け入れ基準 3）。本来は `dotnet build src/knowledge/backend/backend.slnx` の成功で担保するが、
**本セッションの実行環境には .NET SDK が無く、取得もできない**（下の「ビルド検証の実行可否」参照）。
代替として XML パーサでの整形式検査を行い、**未実行の事実を隠さず記録する**。

## IADR-0115 の分類（編集可否の確認）

| ファイル | 分類 | 根拠 |
| --- | --- | --- |
| [`src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/Knowledge.Bff.Endpoints.csproj`](../../src/knowledge/backend/Bff/Knowledge.Bff.Endpoints/Knowledge.Bff.Endpoints.csproj) | **C（本リポの中身そのもの）** | キット `repo-template` に対応物が無い、本リポジトリのユニット構成（IADR-0056 / IADR-0063）に固有の実体。同期対象外でありデルタを増やさない |
| `docs/specs/`（本仕様書） | **C（リポ固有）** | 雛形から書き起こした実体 |

## 受け入れ基準（issue #486）

- [x] `grep -rn "2 プロジェクト" src/` に**現行値としての誤りが 0 件**（経緯記述・別義は上の据え置き表で記録）
- [x] 是正後の記述が [IADR-0117](../adr/IADR-0117_platform-shared-kernel-placement.md) と整合する
      （3 プロジェクト＝ Contracts / Infrastructure / Kernel、`Platform.Shared.Kernel` は未作成、典拠が IADR-0117 と分かる）
- [ ] knowledge ユニットのビルドが成功する（`dotnet build src/knowledge/backend/backend.slnx`）
      — **本セッションでは未実行**（.NET SDK 不在・取得不可）。代替検証と CI での判定に委ねる（下記参照）
- [x] `node scripts/check-unit-dependencies.js` が成功する
- [x] `node scripts/check-doc-links.js` が成功する
- [x] `node scripts/scripts.test.js` が成功する
- [x] `node scripts/check-commit-messages.js --base origin/develop` が成功する
- [x] `git diff` の変更が XML コメントのみで、`ProjectReference` 等のビルド設定に差分が無い

## 検証結果（実測）

| コマンド | 結果 |
| --- | --- |
| `grep -rn "2 プロジェクト" src/` | 1 行（`src/README.md` 77 の**経緯記述**のみ）。現行値としての誤りは **0 件** |
| `grep -rn "プロジェクト" --include='*.csproj' --include='*.props' --include='*.targets' src/` | 15 行 / 15 ファイル（是正前と同数）。内訳は「テストプロジェクトが〜」の定型コメント 14 件＋本件 1 件（`2 プロジェクト` → `3 プロジェクト`）。件数表記の誤りの残存なし |
| `dotnet build src/knowledge/backend/backend.slnx` | **未実行**（`dotnet: command not found`。下記「ビルド検証の実行可否」） |
| 代替: XML 整形式検査（`xml.dom.minidom` で knowledge ユニットの csproj 22 件＋`backend.slnx`＋`src/Directory.Build.props` / `Directory.Packages.props` の計 **25 ファイル**をパース） | 25 / 25 成功。対象 csproj のコメントノードが 4 件正しく分離され、`ProjectReference` の `Include` 2 件は改定前と同一 |
| `node scripts/check-unit-dependencies.js` | exit 0（違反なし） |
| `node scripts/check-doc-links.js` | exit 0 |
| `node scripts/scripts.test.js` | exit 0 |
| `node scripts/check-commit-messages.js --base origin/develop` | exit 0 |
| `git diff origin/develop -- src/` | XML コメント 1 箇所（1 行 → 3 行）のみ。ビルド設定に差分なし |

### ビルド検証の実行可否（実測・受け入れ基準 3 の扱い）

受け入れ基準 3 は `dotnet build src/knowledge/backend/backend.slnx` の成功だが、**本セッションでは実行できなかった**。
「実行したことにする」ほうが検証の目的を壊すため、実測どおり記録する。

1. **SDK 不在**: `dotnet build …` は `dotnet: command not found`（exit 127）。環境の
   `check-tools` にも .NET の項が無く、Python / Node / Java のみが導入されている。
2. **取得不可**: `curl https://dot.net/v1/dotnet-install.sh` はエージェントプロキシに拒否され
   `CONNECT tunnel failed, response 403`。`$HTTPS_PROXY/__agentproxy/status` の
   `recentRelayFailures` にも `builds.dotnet.microsoft.com:443` への
   `connect_rejected`（policy denial）が記録された。SDK を入れて実走する経路が無い。
3. **代替検証**: XML 整形式検査（上表）で、複数行コメント化によって XML が壊れていないこと・
   MSBuild が読む要素（`PropertyGroup` / `FrameworkReference` / `ProjectReference`）に差分が無いことを確認した。
   MSBuild はコメントノードを評価しないため、**この 2 点が成り立てばビルド結果は改定前と同一**である。
4. **最終判定は CI**: `ci.yml` の backend ジョブが両ユニットに `restore/build/test/format` を実行する。
   受け入れ基準 3 の充足はその実走をもって確認する（本 PR の必須チェック）。

- 影響は XML コメントの文言のみ。ビルド出力・依存関係・CI ゲートの判定は変わらない。
- 実体プロジェクト未作成の期間は文言の「3」と実在の「2」が食い違うが、IADR-0117 決定 4 が
  意図した状態であり、コメント内に「未作成」と明記して読み手の混乱を避ける。

## フォローアップ（本 issue の範囲外）

1. `Platform.Shared.Kernel` の実体作成（IADR-0117 フォローアップ 1）。作成時に本 csproj の
   コメント「実体は未作成」も併せて更新する。
