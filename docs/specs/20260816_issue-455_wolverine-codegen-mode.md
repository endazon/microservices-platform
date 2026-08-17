---
title: Wolverine のコード生成方式（実行時コンパイル / 事前 codegen）を決め、CPM へ宣言を足す（#455 の断片）
type: spec
status: done
related_ids:
  - ADR-0027
  - ADR-0030
  - ADR-0041
  - IADR-0217
  - NFR
author: implementation-agent
created: 2026-08-16
updated: 2026-08-17
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md"
  - "../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md"
related_specs:
  - "../adr/IADR-0217_wolverine-runtime-compilation-standard.md"
  - "../adr/IADR-0196_shared-kernel-result-library-allowlist.md"
  - "../tech/tech-requirements.md"
  - "20260803_issue-455_backend-application-standard.md"
  - "20260815_issue-500_result-type-adr-0041-followup.md"
---

# 仕様書: Wolverine のコード生成方式の確定と CPM 宣言の追加

> 本仕様書は実装着手前に作成した。計画書（`project-planning` の `projects/microservices-platform/`）を
> 一次情報とし、本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（機能追加ではない）
- 非機能要件（NFR）: **無採番**。理由は後述「起点 ID の選び方」を参照
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR:
  - 計画 `ADR-0027`（非同期メッセージングを Wolverine へ移行。**Accepted**。2026-08-04 追記で移行時の必須設定 3 点を確定）
  - 計画 `ADR-0030`（バックエンドアプリケーション層のライブラリ標準）
  - 計画 `ADR-0041`（Result 型に外部ライブラリを認め SharedKernel で包む。`Proposed` だが決定の効力は停止しない）
- 実装 ADR: [`IADR-0217`](../adr/IADR-0217_wolverine-runtime-compilation-standard.md)（本作業で新設）
- 計画書リンク:
  - [`ADR-0027`](../../planning/projects/microservices-platform/07_adr/ADR-0027_messaging-wolverine.md)
  - [`ADR-0030`](../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md)
  - [`ADR-0041`](../../planning/projects/microservices-platform/07_adr/ADR-0041_result-type-external-library.md)
  - [`12_backend-application-stack`](../../planning/projects/microservices-platform/06_technical/12_backend-application-stack.md)（`fixed`。ライブラリ表と Wolverine 移行チェックリスト）
- 関連する既存仕様書:
  - [`20260803_issue-455_backend-application-standard.md`](20260803_issue-455_backend-application-standard.md)（CPM 中央定義と ratchet の導入）
  - [`20260815_issue-500_result-type-adr-0041-followup.md`](20260815_issue-500_result-type-adr-0041-followup.md)（`SHARED_KERNEL_ALLOWED` の実装）
  - [`../adr/IADR-0196_shared-kernel-result-library-allowlist.md`](../adr/IADR-0196_shared-kernel-result-library-allowlist.md)
  - [`../tech/tech-requirements.md`](../tech/tech-requirements.md)（ライブラリ標準の実装側要点）

### 起点 ID の選び方

`.claude/rules/traceability.md`「起点 ID の種別」の `NFR` の項に照らし、本作業は **無採番 `NFR`** とする。

- 計画側は非機能要件表に ID 列を持つ（`NFR-01`〜`NFR-27`）ため、ケース 1（ID 列が無い）ではない。
- 本作業は**ライブラリ標準の実施方式の確定と CPM への版宣言**であり、稼働する製品の品質要件
  （性能・可用性・セキュリティ等）のどの番号にも対応しない。ケース 2（ID 列はあるが当たる番号が無い）に当たる。
- したがって無理に近い番号を付けず、計画へも環流しない。
- 実装判断そのものは `IADR-0217` に残す。コミット / PR のスコープは `IADR-0217` を用いる。

## 目的・背景

`src/Directory.Packages.props` は #455 で「各サービスの再実装 issue が参照するための中央定義」を持ったが、
**`WolverineFx.RuntimeCompilation` が未宣言**である。ただし版を足すだけでは意味がない。
可変機能ユニット `ai-stock-trading` の環流記録
（`src/ai-stock-trading/feedback/20260804_adr0027-wolverine-migration-caveats.md` L101-122）は、
**Wolverine 6 系はコア本体（`WolverineFx`）から Roslyn のランタイムコンパイラを別パッケージへ分離しており、
既定の `TypeLoadMode.Dynamic` のまま `UseWolverine(...)` したホストは起動時に例外で停止する**ことを実測し、
回避策が 2 案（実行時コンパイル / 事前 codegen ＋ `TypeLoadMode.Static`）あることを示したうえで、
**どちらを標準とするかの判断を基盤側へ明示的に差し戻していた**。

したがって本作業は「未宣言の版を足す」作業ではなく、**方式を決めてから、決めた側に必要な版を宣言する**作業である。

## 対象範囲

- 対象:
  - 実装 ADR `IADR-0217` の新設（方式の決定と根拠の記録）
  - `src/Directory.Packages.props` への `PackageVersion` 追加（決定した方式に必要なもの ＋ `CSharpFunctionalExtensions`）
  - 同ファイルのコメントブロック（`ADR-0030` 中央定義の説明）の追随
  - `docs/adr/README.md` 索引への 1 行追加
- 対象外:
  - **Wolverine 移行の実装そのもの**。計画 `12_backend-application-stack` の「Wolverine 移行チェックリスト」
    手順 1〜8（イベント型 → 購読サービス対応表・全サービスへの参照追加・共通ヘルパ・実ブローカ結合テスト）は
    各サービスの再実装 issue（#438〜#451 / #441）が行う。本 PR は**決定と宣言だけ**である。
  - `.csproj` への `PackageReference` 追加（＝チェックリスト手順 2）。
  - `templates/unit-template/backend/Directory.Packages.props.sample` への追加（理由は下記「母集合」§除外）。
  - `scripts/check-backend-libraries.js` の `BANNED` / baseline の変更（後述のとおり変更不要）。

## 母集合の走査（`.claude/rules/traceability.repo.md`「是正・追随の母集合の取り方」）

**走査条件**: 作業ツリー `/home/user/wt-wolv`（`feat/nfr-wolverine-codegen-mode`・基点 `1110b5c4`）。
コマンドは `git grep -n -i --untracked -- <語> ':!planning' ':!src/ai-stock-trading'`。

- `--untracked` を付けたのは、**`git ls-files` / 既定の `git grep` は追跡下のファイルしか返さず、
  本作業で新規追加する `docs/specs/` と `docs/adr/` の 2 ファイルが母集合から落ちる**ためである（規則 8）。
- **拡張子で絞らず、パスの除外だけで取った**（規則 3）。`--include` を使っていない。
- **行フィルタ（後段の `grep`）を継いでいない**（規則 4）。
- **軸を 1 本で終わらせていない**（規則 5）。誤り側・現象側・機構側の 3 系統で 9 軸を引いた（規則 1）。

### 走査結果（本仕様書を書く前の時点。規則 8 の引き算は末尾）

| # | 軸（検索語） | 系統 | 件数 | 扱い |
| --- | --- | --- | --- | --- |
| 1 | `RuntimeCompilation`（-i） | 誤り側（未宣言の当該パッケージ名） | **0 件** | 追随先なし。本作業が初出 |
| 2 | `TypeLoadMode` | 現象側（案 B の設定名） | **0 件** | 同上 |
| 3 | `codegen write` | 現象側（案 B の手順名） | **0 件** | 同上 |
| 4 | `ランタイムコード生成` / `ランタイムコンパイ` | 現象側（日本語表記） | **0 件** | 同上 |
| 5 | `Roslyn`（-i） | 現象側（実装機構名） | **1 件** | `docs/adr/IADR-0156_bff-authz-contract-checker.md:113`。BFF 認可検査器が Roslyn 構文解析を代替案として却下した記述で、Wolverine と無関係 → **除外** |
| 6 | `CSharpFunctionalExtensions` | 機構側（同時宣言する側） | **8 ファイル** | 下表で個別に判断 |
| 7 | `Wolverine`（-i） | 機構側（決定の適用先） | **27 ファイル**（`CHANGELOG.md` 除く） | 下表で個別に判断 |
| 8 | `PackageVersion` | 機構側（宣言の置き場） | **21 ファイル**。うち実体の CPM ファイルは **2 つ** | `src/Directory.Packages.props`（対象） / `templates/unit-template/backend/Directory.Packages.props.sample`（除外・理由は下記） |
| 9 | `未参照エントリ` | 機構側（許容の根拠文言） | **1 件** | `src/Directory.Packages.props:59`。**「CPM の未参照エントリは無害」と既に許容している**ことを確認した。追随して本作業の 2 件を明記する |

### 変更する / しないの内訳（軸 6・7・8 の個別判断）

| ファイル | 変更 | 理由 |
| --- | --- | --- |
| `src/Directory.Packages.props` | **する** | 中央定義の単一情報源。`WolverineFx.RuntimeCompilation` と `CSharpFunctionalExtensions` を宣言し、コメントブロックを追随させる |
| `docs/adr/IADR-0217_*.md` | **する**（新設） | 本作業の決定の記録 |
| `docs/adr/README.md` | **する** | 索引の欠番を作らない |
| `docs/specs/20260816_issue-455_wolverine-codegen-mode.md` | **する**（新設） | 本書 |
| `templates/unit-template/backend/Directory.Packages.props.sample` | **しない** | 同ファイルは自身のコメントで**「雛形の 7 プロジェクトが `PackageReference` する全パッケージ」**と範囲を定めている。参照を足す（＝チェックリスト手順 2）のは本 PR の対象外であり、参照の無い版定義をここへ足すと**ファイル自身が宣言した範囲と食い違う**。参照追加と同時に足すのが整合的 |
| `docs/tech/tech-requirements.md` | **しない** | ライブラリ標準は「**要点**」の表であり、**全量は計画書が正**と本文が明記している（L110-114）。Infrastructure 層の個別パッケージ（`WolverineFx.RabbitMQ` / `WolverineFx.Kafka`）も載っていないため、`RuntimeCompilation` だけを足すと表の粒度が壊れる。`CSharpFunctionalExtensions` は既に L121 / L144 / L157 に**採用条件つきで記載済み**で、CPM への版宣言で変わる記述は無い |
| `scripts/check-backend-libraries.js` / `scripts/backend-library-baseline.json` | **しない** | 同スクリプト冒頭 L14-17 が「**`PackageVersion`（CPM のバージョン定義）は違反にしない**」と設計を明示している。`BANNED` にある `CSharpFunctionalExtensions` を CPM へ宣言しても検出対象外であり、baseline も動かない（実測は §検証） |
| `docs/adr/IADR-0117` / `IADR-0196` / `docs/specs/20260803_*` / `20260815_*` / `scripts/README.md` / 雛形の `SampleService.Domain.csproj` | **しない** | いずれも「`CSharpFunctionalExtensions` を SharedKernel の内部実装としてのみ許す」という**参照可否**の記述であり、**版を CPM のどこに書くか**とは別の関心。本作業で誤りになる記述は無い |
| 軸 7 の残り（`docs/adr/IADR-0122` / `IADR-0137` / `docs/data/data-source.md` / `docs/operations/operations.md` / `docs/tests/TEST_STRATEGY.md` / `feedback/20260704_*` / `docs/specs/` の 8 件 / `scripts/check-contract-schema.js` / `scripts/scripts.repo.test.js` / `CLAUDE.md` / `templates/unit-template/` の 6 件） | **しない** | Wolverine への言及ではあるが、コード生成方式に触れた記述は 1 件も無い（軸 1〜4 が 0 件であることがその裏づけ）。本決定で誤りになる記述は無い（規則 10 の引き直し） |
| `CHANGELOG.md` | **しない** | 自動生成物（手で書き足さない）。なお `Wolverine` の出現は **0 件**である（`git grep -c -i` が exit 1） |
| `planning/` | **しない** | submodule。本リポジトリから変更しない |
| `src/ai-stock-trading/` | **しない** | 別プロジェクトの submodule。読むだけ |

### 規則 8 の引き算（自己参照）

軸 1〜4 は本仕様書と `IADR-0217` を書いた**後**に非 0 件になる（本書自身が `RuntimeCompilation` /
`TypeLoadMode` / `codegen write` / `ランタイムコード生成` を含む）。上表の件数は**書く前**の値である。
書いた後の再走査は §検証 の末尾に、追加分の内訳つきで記録する。

## 設計

### 決定（詳細は [`IADR-0217`](../adr/IADR-0217_wolverine-runtime-compilation-standard.md)）

**案 A（`WolverineFx.RuntimeCompilation` を参照し、実行時コンパイルを使う）を基盤の標準とする。**
案 B（`dotnet run -- codegen write` による事前生成 ＋ `TypeLoadMode.Static`）は採らない。

決め手は 5 軸で、うち **軸 1（上位決定との整合）だけで結論が決まる**。

| 軸 | 案 A | 案 B | 判定 |
| --- | --- | --- | --- |
| 1. 上位決定との整合 | 計画 `ADR-0027` §決定 の 2026-08-04 追記が**必須**と明記 | 同追記が**「採らない」と明記** | **A**（拘束。B は fixed / Accepted への逸脱） |
| 2. 起動の確実性 | 現物のアセンブリから毎回生成するのでずれない | 生成物が実装からずれると、古い挙動のまま緑になる | A |
| 3. 配布物の大きさ・起動コスト | Roslyn を同梱し、起動時に生成が走る（**代償**） | 小さく・速い | **B**（案 A が負う唯一の不利） |
| 4. ビルド手順の複雑さ | 増えない | サービス数分の生成・版管理・再生成差分検査が要る | A |
| 5. 実測の裏づけ | `ai-stock-trading` が全 10 サービスで案 A を実運用（`AST/IADR-0129` 決定 6） | 実測なし | A |

軸 3 が案 A の代償であり、**再評価条件**（起動時間・イメージサイズの数値要求が非機能要件として顕在化した時点、
または生成コードの管理コストが読める時点）を `IADR-0217` に書く。

### `CSharpFunctionalExtensions` を同時に宣言する理由と、CI が赤くならない根拠

計画 `ADR-0041` 決定 1 が `CSharpFunctionalExtensions`（MIT）の採用を確定している一方、
`scripts/check-backend-libraries.js` の `BANNED` にも同名が載っている。**矛盾ではない。**

- `BANNED` に残しているのは、`Platform.Shared.Kernel` **以外**での直接参照を素通りさせないためである
  （`IADR-0196`。`bannedListFor()` が共有カーネルのときだけ `SHARED_KERNEL_ALLOWED` を差し引く）。
- 一方、**検査対象は `PackageReference` / `GlobalPackageReference` と `.cs` の `using` だけ**であり、
  **`PackageVersion`（CPM の版定義）は違反にしない**（同スクリプト L14-17 が設計として明示）。
- したがって CPM への版宣言は `BANNED` と衝突しない。版定義は「どこで使えるか」ではなく
  「使うときにどの版か」を決めるものであり、参照の可否は別の検査が持つ。

**この点は `IADR-0217` 本文へ明記する**（次の読み手が「禁止なのに宣言していいのか」で必ず止まるため）。

### 追加する宣言

| パッケージ | 版 | 根拠 |
| --- | --- | --- |
| `WolverineFx.RuntimeCompilation` | `6.24.4` | 既存の `WolverineFx` / `WolverineFx.RabbitMQ` / `WolverineFx.Kafka` と**同版に揃える**（family の版ずれを作らない）。当該版が NuGet に実在することを確認済み |
| `CSharpFunctionalExtensions` | `3.7.0` | 計画 `ADR-0041` 決定 1。実在する最新の安定版 |

いずれも**この時点ではどの `.csproj` からも参照されない**。`src/Directory.Packages.props` L58-64 付近の
コメントブロックが既に「CPM の未参照エントリは無害」と許容しており、本作業の 2 件も同じ扱いである
（コメントブロックへ 2 件の由来を追記して追随させる）。

## 受け入れ基準

- [x] 案 A / 案 B のどちらを基盤の標準にするかを **1 つに決め**、`IADR-0217` に記録した（両論併記で終わっていない）
- [x] 決め手を**軸を明示して**比較した（起動の確実性・配布物の大きさ・ビルド手順の複雑さ・上位決定・実測）
- [x] **却下した案（B）の代償**を書き、再評価条件を示した
- [x] `CSharpFunctionalExtensions` を CPM へ同時宣言し、**`BANNED` と衝突しない根拠**（`PackageVersion` は違反にしない設計）を `IADR-0217` に明記した
- [x] `src/Directory.Packages.props` のコメントブロックを追随させた
- [x] `docs/adr/README.md` の索引へ 1 行追加した（タイトルは 200 文字以内・本体 `title:` との LCS 12 以上）
- [x] 検査器 6 本と両ユニットの `dotnet build` / `dotnet test` を実測し、**テスト件数が動いていない**ことを確認した

## テスト方針

本作業はコードを 1 行も追加しない（決定の記録と CPM の版宣言のみ）ため、新規テストは書かない。
代わりに**既存の機械検査で退行が無いことを実測**する（§検証）。とくに次の 2 点を実測で確かめる。

1. `check-backend-libraries.js` が `CSharpFunctionalExtensions` の `PackageVersion` を違反として上げないこと
   （＝設計コメントの主張が現物で成り立つこと）。
2. `dotnet build` / `dotnet test` が両ユニットで変わらないこと（CPM へ足しただけでビルドが壊れないこと）。
   **テストを 1 件も変更しないので、件数が動いたら異常**として報告する。

## 検証（すべて実測）

実行環境: `/home/user/wt-wolv`（`feat/nfr-wolverine-codegen-mode`）。`planning` submodule を populate 済み。
`src/ai-stock-trading` も populate した（**読むだけ**。`git status --short` が clean であることを確認済み）。

### 機械検査

| # | コマンド | EXIT | 判定行 |
| --- | --- | --- | --- |
| 1 | `node scripts/check-cpm-versions.js` | 0 | `OK: 37 プロジェクト / 191 件の PackageReference にバージョン直書き 0 件（VersionOverride 0 件）。` |
| 2 | `node scripts/check-backend-libraries.js` | 0 | `OK: 新規混入 0 件 / Domain 依存規律 OK（既知残件 29 件は baseline 済み）。`（notice: 残件 29 件 / 22 プロジェクト）|
| 3 | `node scripts/check-adr-numbering.js` | 0 | `OK: IADR の採番は重複・欠番なし、索引とも双方向で一致し昇順です。` |
| 4 | `node scripts/check-doc-links.js` | 0 | `OK: 675 件の Markdown に破損した相対リンクはありません。` |
| 5 | `node scripts/check-cross-repo-refs.js` | 0 | `走査 1706 件 / 除外 73 件` → `OK: 1706 件に他リポジトリ参照の表記違反はありません。` |
| 6 | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 0 | `✓ 637 tests passed`。うち索引タイトルの実データ検査 `索引タイトル: 本リポの docs/adr/README.md が baseline を超えていない（実データ・ラチェット）` が **ok** |

**#2 が本作業の要点**である。`CSharpFunctionalExtensions` を CPM へ宣言しても残件は **29 件のまま**で
（`IADR-0216` 時点と同値）、新規混入 0 件のまま緑である。**「`PackageVersion` は違反にしない」設計が
現物で成り立つことを実測で確かめた**（宣言だけで済ませていない）。

> **注**: 検査器 1〜5 は EXIT=0 でも `skip` と `pass` を区別しないため、判定行を読んだ。
> #4 は `src/ai-stock-trading` を populate する前は「未 populate の submodule 配下 2 件を対象外にした」
> という notice を出していた。populate 後は notice が消え、**675 件を実際に検査して OK** になっている。
> #5 も populate 前は「untracked 2 件は対象外」と warn していたため、新規 2 ファイルを `git add` して
> 再実行し、走査件数が 1704 → 1706 へ増えた状態で OK を得た（**自分が足したファイルを検査させた**）。

### ビルド / テスト（CPM へ足しただけでビルドが壊れないこと）

| 対象 | コマンド | EXIT | 結果 |
| --- | --- | --- | --- |
| platform | `dotnet build platform/backend/backend.slnx` | 0 | `Build succeeded. 0 Warning(s) 0 Error(s)` |
| knowledge | `dotnet build knowledge/backend/backend.slnx` | 0 | `Build succeeded. 2 Warning(s) 0 Error(s)`（既存の CS0618 = `MinioBuilder` の obsolete。本作業と無関係） |
| platform | `dotnet test platform/backend/backend.slnx` | 0 | **Passed 456** / Failed 0 / Skipped 1（68 + 157 + 231） |
| knowledge | `dotnet test knowledge/backend/backend.slnx` | 0 | **Passed 596** / Failed 0 / Skipped 0（6+101+71+20+68+75+129+28+39+16+43） |

**テスト件数は platform 456 / knowledge 596 で期待値どおり動いていない**（本作業はテストを 1 行も変更していない）。
追加した 2 件はどの `.csproj` からも参照されない `PackageVersion` であり、復元対象にならないことも裏づけられた。

### 母集合の再走査（規則 8 の引き算）

本仕様書と `IADR-0217` を書いた**後**の実測値。走査条件は §母集合 と同一。

| 軸 | 書く前 | 書いた後 | 増分の内訳 |
| --- | --- | --- | --- |
| `RuntimeCompilation`（-i） | 0 ファイル | **4 ファイル** | 本書 / `IADR-0217` / `docs/adr/README.md`（索引行）/ `src/Directory.Packages.props`（宣言） |
| `TypeLoadMode` | 0 ファイル | **4 ファイル** | 同上 |
| `codegen write` | 0 ファイル | **2 ファイル** | 本書 / `IADR-0217` |
| `ランタイムコード生成` | 0 ファイル | **1 ファイル** | 本書のみ |
| `ランタイムコンパイ` | 0 ファイル | **2 ファイル** | 本書 / `IADR-0217` |

**増分はすべて本 PR が意図して作ったものであり、追随漏れの候補は 1 件も無い**
（4 - 4 = 0 / 4 - 4 = 0 / 2 - 2 = 0 / 1 - 1 = 0 / 2 - 2 = 0）。

## 計画書との差異

- 差異: **なし**。計画 `ADR-0027` §決定 の 2026-08-04 追記（必須設定 1）と
  `12_backend-application-stack` のライブラリ表・移行チェックリスト手順 2 に一致する。
- なお `ai-stock-trading` の環流記録 L121-122 が基盤側へ差し戻した問いは、**同日（2026-08-04）の
  計画側の裁定（planning#181）で既に解決している**。本 IADR はその上位決定を実装側の標準として
  確定・記録するものであり、未決を新たに決めるものではない。この事実関係は `IADR-0217` に明記する。

## 未決事項

- なし。案 A の再評価条件は `IADR-0217` §結果 に置いた（本 PR で解消すべき論点ではない）。
