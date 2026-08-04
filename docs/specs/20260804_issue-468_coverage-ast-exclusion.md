---
title: 作業仕様書 — カバレッジ床の集計から合成点テスト経由で混入する AST の行を除く（filename 帰属除外）
type: spec
status: done
related_ids: [NFR, IADR-0115, IADR-0116, IADR-0118, IADR-0120, IADR-0123]
author: Claude
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - ./20260803_issue-453_regression-test-foundation.md
  - ./20260803_issue-473_excluded-units-single-source.md
  - ./20260803_issue-474_backend-floor-iadr-and-0116-followup.md
  - ./20260804_issue-467_cpm-version-inline-check.md
  - "../tests/TEST_STRATEGY.md"
  - "../adr/IADR-0115_impl-handoff-kit-as-single-source.md"
  - "../adr/IADR-0116_reimplementation-branching-and-pr-policy.md"
  - "../adr/IADR-0118_backend-coverage-floor.md"
  - "../adr/IADR-0120_excluded-units-from-gitmodules.md"
  - "../adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md"
---

# 作業仕様書: カバレッジ床の集計から合成点テスト経由で混入する AST の行を除く

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（**NFR**: 品質・保守性 — 再実装期間中の退行検知の精度。起点 ID の種別は
  [`.claude/rules/traceability.md`](../../.claude/rules/traceability.md) および
  [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 3 に従う）
- ユースケース（UC）/ 画面（SC）: なし
- 関連 ADR（実装）:
  - [IADR-0118](../adr/IADR-0118_backend-coverage-floor.md)（バックエンドのカバレッジ床。**決定 4 が
    名指しした「既知の限界」を本作業で塞ぐ**。フォローアップ 1 が本 issue）
  - [IADR-0120](../adr/IADR-0120_excluded-units-from-gitmodules.md)（検査対象外ユニットの単一情報源。
    本作業も除外集合を [`scripts/lib/excluded-units.js`](../../scripts/lib/excluded-units.js) からのみ導出し、
    独自のリストを持たない）
  - [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)（キット同期規約。触ってよい
    ファイルの分類。後述「IADR-0115 の位置づけ確認」）
  - [IADR-0123](../adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md)（本作業で起票。
    Cobertura の行帰属と二重記載の扱いを確定する）
- 本リポジトリの起点: [#468](https://github.com/endazon/microservices-platform/issues/468)
  （親: [#453](https://github.com/endazon/microservices-platform/issues/453) /
  [#454](https://github.com/endazon/microservices-platform/issues/454)。PR #464 で床を武装した際の
  レビュー指摘から分割）

## 目的・背景

[#453](https://github.com/endazon/microservices-platform/issues/453)（PR #464）でバックエンドのカバレッジ床を
武装した。集計対象から `ai-stock-trading`（AST）を外すため
[`scripts/check-coverage-floor.js`](../../scripts/check-coverage-floor.js) は
`EXCLUDED_UNITS` / `isExcludedPath` を持つが、この除外は **Cobertura レポートファイルのパス**が
`src/ai-stock-trading/` 配下かどうかしか見ない。**レポートの中身に他ユニットのコードが含まれる経路は
塞げていない。**

`Platform.Bff` は BFF の合成点として AST のエンドポイントを `ProjectReference` しており
（[`Platform.Bff.csproj`](../../src/platform/backend/Bff/Platform.Bff/Platform.Bff.csproj)。
FR-14 / IADR-0063 の例外 3）、`Platform.Bff.Tests` はそれをプロセス内で読み込んで実行する。その結果
**`src/platform/` 配下に出力されるレポートの中身に AST のクラスの行データが入る**。対象は 6 クラス
（`AssumptionsBffEndpoints` / `MonitorBffEndpoints` / `RiskControlsBffEndpoints` と、それぞれの非同期
ステートマシン `d__2`）で、この経路は `Platform.Bff.csproj` の 1 件のみである（PR #464 レビューの grep 実測）。

放置すると、**AST の submodule pin を更新するだけで platform / knowledge の作業と無関係に床の実測値が
動く**。混入行はすべて被覆済みのため実測値を押し上げる方向にしか働かず、床を引き上げていく過程で
MSP 自身の実力より高い床を置いてしまう。これは IADR-0118 決定 4 が「合算は双方向に濁る」として
名指しした劣化が、パス除外をすり抜けて残っている状態である。

## 対象範囲

- 含むもの:
  1. [`scripts/check-coverage-floor.js`](../../scripts/check-coverage-floor.js) の `parseCobertura` を
     **class 単位走査 ＋ `<class filename>` による行の帰属**へ作り替える。除外ユニット配下へ帰属した行を
     集計から落とす。
  2. 同スクリプトへ**診断出力**を追加する（`<sources>` の値・filename の解釈の内訳・除外したクラスと
     行数・除外前後の実測値）。CI ログと `$GITHUB_STEP_SUMMARY` から**実測値を読み取れる**ようにする。
  3. 帰属が 1 件も成立しなかった場合（＝フィルタが何にもマッチせず素通りしている場合）に **warn** を出す。
  4. [`scripts/scripts.repo.test.js`](../../scripts/scripts.repo.test.js) へ単体テストを追加し、
     同スクリプトの `--self-test` を拡張する（Cobertura フィクスチャ: 相対 filename / 絶対 filename /
     `<sources>` 結合 / 二重記載 / 帰属 0 件）。
  5. [`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md) の「既知の限界: 合成点テスト経由の混入」
     節を**解消済み**として書き換える。
  6. [IADR-0123](../adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md) の起票。
- 含まないもの:
  - **床の値の変更**（[`src/coverage-floor.json`](../../src/coverage-floor.json) の `line 34` / `branch 17`）。
    実装セッションの環境に .NET SDK が無く（後述「測定条件」）、除去後の実測は **CI 実走のログでしか
    得られなかった**。値の置き直しは CI 実測を見てから、**同ファイルの 2 定数のみ**の変更で行う。
    - **［CI 実測後の結論］置き直しの結果は据え置き**——実測 `line 34.14%` / `branch 17.26%` の整数切り下げが
      現在値と同値だったため、2 定数は変更していない。**根拠のみ**（混入込み → 混入抜き）差し替え、
      測定条件とともに `$comment` へ記録した（後述「CI 実測（成立確認）」）。
  - `Platform.Bff` から AST への `ProjectReference` の解消。合成点の設計（IADR-0063 例外 3）そのものであり、
    カバレッジ集計の都合で変えるものではない。
  - 除外ユニット集合の導出規則（IADR-0120）。本作業は同ヘルパを**利用するだけ**で変更しない。
  - `.github/workflows/ci.yml` の変更。診断は**既定で出力**する設計にし、ワークフロー側でフラグを
    立てる必要を無くす（後述「診断出力」）。

## IADR-0115 の位置づけ確認

| ファイル | キット原本（`planning/tools/impl-handoff-kit/repo-template/scripts/`） | 位置づけ | 本作業での扱い |
| --- | --- | --- | --- |
| `check-coverage-floor.js` | 無し | **固有デルタ種 3**（本リポにしか存在しないスクリプト） | 改修する |
| `scripts.repo.test.js` | 無し（companion の受け口はキット側にある） | 固有デルタ種 3 | 追記する |
| `scripts.test.js` | 有り・バイト一致（**分類 A**） | 分類 A | **触らない** |
| `lib/ci-annotate.js` | 有り・バイト一致（**分類 A**） | 分類 A | 参照のみ |
| `lib/excluded-units.js` | 無し | 固有デルタ種 3 | 参照のみ（改変しない） |
| `check-permission-denials.js` | 有り・バイト一致（**分類 A**） | 分類 A | **触らない** |
| `docs/tests/TEST_STRATEGY.md` | 無し | 固有デルタ種 3 | 更新する |

## 実レポートの構造（設計の前提と、その検証手段）

**実装セッションの環境では実レポートを取得できなかった**（.NET SDK 無し・導入経路も遮断。後述「測定条件」。
**当該セッション時点の観測であり、他の環境の性質ではない**——本 PR のレビューは SDK のある環境で
`Platform.Bff.Tests` を実走している）。したがって「属性の形を仮定して書いたらフィルタが何にもマッチせず
素通りした」という失敗（issue #468 の着手時注意）を、**仮定を置かない実装**と**診断出力**の二段で防ぐ。
なお「実レポートでの成立確認は CI 実走を正とする」運用そのものは手元の SDK の在否に依存しない——床が
判定に使うのは CI（Release・全ユニット）の実測だからである。

coverlet（`XPlat Code Coverage`）の Cobertura は概ね次の形である。

```xml
<coverage line-rate="..." lines-covered="..." lines-valid="..." branches-covered="..." branches-valid="...">
  <sources><source>/home/runner/work/msp/msp/src/</source></sources>
  <packages><package name="Platform.Bff">
    <classes>
      <class name="AiStockTrading.Bff.Endpoints.AssumptionsBffEndpoints" filename="ai-stock-trading/backend/Bff/.../AssumptionsBffEndpoints.cs">
        <methods><method name="MapAssumptions"><lines><line number="10" hits="1" /></lines></method></methods>
        <lines><line number="10" hits="1" /></lines>   <!-- 同じ行が二重に現れる -->
      </class>
    </classes>
  </package></packages>
</coverage>
```

**`filename` が相対か絶対かは決め打ちできない。** coverlet は「全ソースファイルのうち最も浅い
ディレクトリ」を base path として `<source>` に出し、base path で始まらないファイルは**絶対パスのまま**
`filename` に書く（`GetBasePaths` / `GetRelativePathFromBase`）。deterministic build 指定時は
`<source>` が空で `filename` が `/_/src/...` の形になる。すなわち**同一レポート内に相対と絶対が混在し得る**。

> **この内部挙動の記述は着手時点の理解であり、coverlet のソース（一次出典）で確認していない。**
> 前提として採らず「決め打ちしない理由」としてのみ用いる。実レポートに対する真偽は、下記の帰属内訳
> （どの解釈で当たったか・未帰属件数）と coverlet 自身の集計値との照合として診断出力に現れる
> （[IADR-0123](../adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md) 決定 4・5）。

よって帰属判定は次の順で行い、**どの解釈で当たったかを診断に出す**（当たり方が想定と違えば読み取れる）。

| 順 | 解釈 | 判定 |
| --- | --- | --- |
| 1 | `filename` をそのまま見る | パスの途中に `src/<unit>/` を含むか（相対 `src/…` も絶対 `/home/…/src/…` も、deterministic の `/_/src/…` も同じ規則で当たる） |
| 2 | `<source>` の各値と結合して見る | base path が `…/src/` より深い場合（`filename` が `ai-stock-trading/backend/…` や `Endpoints/Foo.cs` になる場合）に当たる |
| 3 | どちらでも当たらない | **未帰属**として集計に残し（＝黙って落とさない）、件数とサンプルを診断に出す |

### 開始タグの走査（属性値の `>`）

`<class>` の開始タグは**引用符を跨がない**走査にする。除外したい 6 クラスのうち 3 つは非同期
ステートマシン（`…MonitorBffEndpoints/<Map>d__2`）であり、`name` 属性に `>` が現れうる
（XML では属性値の `>` の実体参照化は必須ではない）。素朴な `[^>]*` はここでタグを途中で切り、
後続の `filename` を読めないまま**そのクラスだけ静かに未帰属**になる——未帰属は集計に残るため、
**除外対象がちょうど抜ける**形で壊れる。`--self-test` の負例で固定する。

## 二重記載（`<methods>` 配下と class 直下の `<lines>`）の扱い

**決定: 行・分岐とも class 直下の `<lines>` を正とし、`<methods>` 配下の `<line>` は内訳として数えない。**

- 根拠: coverlet の Cobertura では、class 直下の `<lines>` は当該クラスの全行の一覧であり、
  `<methods>` 配下はその**メソッド別の内訳**である（同じ行が両方に現れる）。両方数えると、メソッドを
  持つクラスの行だけが 2 票を持ち、**メソッド外の行（初期化子・属性行など）との重みが崩れる**。
  IADR-0118 が「ファイル単位の単純平均は実態より高く出る」として行数加重を選んだのと同じ理屈である。
- 副作用: 集計の**分母と分子がともに半分になる**（比率はほぼ不変）。PR #464 の実測
  `line 34.46%（18894/54826）` の絶対数は本改修後の表示と直接比較できない。**床は比率であり、
  比率の変化は小さい**見込みだが、確定は CI 実測で行う（本作業では床を触らない）。
  - **［CI 実測後］旧値は新方式の厳密に 2 倍だった**——`18894 = 9447 × 2` / `54826 = 27413 × 2` /
    `3154 = 1577 × 2` / `17896 = 8948 × 2`。全項が 2 倍で揃うことは、二重記載が一律に効いていた
    （＝この決定が正しい）ことの強い裏づけである。
- **仮定であることを診断で検証可能にする**: `<coverage>` 要素の `lines-valid` / `lines-covered` /
  `branches-valid` / `branches-covered`（coverlet 自身の集計値）をレポート単位で読み出し、本実装の
  集計値と並べて出す。**行**が一致すれば「class 直下が正」という前提が実レポートで裏づけられる。
  **分岐は定義が異なるため一致を期待しない**（CI 実測で確認。後述）ので、分岐側は別の観測点として
  **「全 `<line>`（`<methods>` 重複込み）」と「class 直下のみ」の比**（実測は厳密に 2.00）を出す
  ——分岐の二重記載排除が壊れても値が増えるだけで照合には現れないため（無音の失敗）。
- フォールバック: class 直下に `<lines>` が無く `<methods>` にだけ行があるクラスは、**行番号で重複排除
  した**メソッド行を採用し、その発生件数を診断に出す（黙って 0 行にしない）。
- class の外（どの `<class>` にも属さない位置）にある `<line>` は、**帰属できないため除外できない**。
  集計には残し、件数を診断に出して warn する（黙って落とすと実測値が理由不明に下がる）。

## 診断出力

**既定で出力する**（`ci.yml` を触らずに CI ログから読めるようにするため）。詳細は
環境変数 `COVERAGE_FLOOR_DEBUG=1` を付けたときのみ出す。

既定（常時・数行）:

1. 集計結果（従来どおり）: `line X%（covered/lines） / branch Y%（…）`、床との比較。
2. **除外サマリ**: 除外ユニット名・除外したクラス数 / 行数（被覆数）/ 分岐数、および
   **除外前の実測値**（`line X%（…）`）。→ 親が「混入行数の確定値」と「除去後の実測値」を
   1 行で読み取れる。
3. **帰属サマリ**: filename 解釈の内訳（`そのまま(相対)` / `そのまま(絶対)` / `<sources> 結合` /
   `未帰属`）、`<sources>` の実値、filename のサンプル、ユニット別の行数。
4. **除外クラスの一覧**（先頭 20 件まで。現状 6 クラスの想定なので全件出る）。
5. **coverlet 自身の集計値との照合**（`lines-valid` / `lines-covered` の合計と本実装の集計値）。

`COVERAGE_FLOOR_DEBUG=1`: レポート 1 件ごとに上記 3〜5 を出す（どのテストプロジェクトが混入源かが分かる）。

`$GITHUB_STEP_SUMMARY` にも「除外行数」「除外前の実測」の行を足す（Checks 画面から 1 クリックで読める）。

### 気付ける仕組み（fail / warn / notice の段階）

| 事象 | 段階 | 理由 |
| --- | --- | --- |
| 1 クラスもユニットへ帰属しなかった（`filename` の形が想定外） | **warn** | フィルタが no-op になっている状態そのもの。issue #468 の「除外したつもりで素通り」 |
| class 外の `<line>` があった | **warn** | 構造の想定外。除外できない行が混ざっている |
| 帰属は成立しているが除外行が 0 だった | **notice** | 合成点の参照が外れれば正常に 0 になる。恒常的な warn は「成果物は正しいのに黄」を常態化させ、警告を読まない学習を生む（IADR-0118 決定 6 の段階ポリシー） |
| class 直下の `<lines>` が無くフォールバックしたクラスがあった | **notice** | 集計は継続できるが、前提（class 直下が正）の反証材料になる |
| 床未満 | **fail** | 従来どおり |

warn / notice は [`scripts/lib/ci-annotate.js`](../../scripts/lib/ci-annotate.js)（分類 A）を使う。
**終了コードは変えない**——本作業が変えるのは「何を数えるか」であり、判定条件ではない。

## 実装（変更点）

| ファイル | 変更 |
| --- | --- |
| [`scripts/check-coverage-floor.js`](../../scripts/check-coverage-floor.js) | `parseCobertura` を class 単位走査へ作り替え。新規の公開 API: `attrOf` / `parseSources` / `parseReportedTotals` / `classBlocks` / `stripMethods` / `methodsOf` / `countLines` / `countLinesUnique` / `classLineStats` / `unitOfFilename` / `aggregateReports` / `attributionMessages` / `formatDiagnostics(agg, floor)`。`--self-test` を拡張。**床の値は診断へ焼き込まず引数の `floor` を表示する**（単一情報源は `src/coverage-floor.json`。IADR-0118 決定 1） |
| [`scripts/scripts.repo.test.js`](../../scripts/scripts.repo.test.js) | Cobertura フィクスチャによる単体テストを追加（既存の coverage-floor 節へ） |
| [`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md) | 「既知の限界: 合成点テスト経由の混入」を解消済みへ書き換え、ゲート一覧の対象欄を更新 |
| [`docs/adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md`](../adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md) | 新規起票 |
| [`docs/adr/README.md`](../adr/README.md) | 索引へ 1 行追加 |
| [`src/coverage-floor.json`](../../src/coverage-floor.json) | **値は変更しない**（CI 実測の切り下げが現在値と同値）。`$comment` に「絶対数の意味が変わったこと」と**現行の根拠（測定条件つきの CI 実測・余裕の薄さ・分岐の定義差）**を記録 |

## 受け入れ基準（issue #468）

- [x] 混入行数を実レポートで測り直し、確定させる → **確定: 6 クラス / 133 行（すべて被覆済み） /
      分岐 50（被覆 41）**（測定条件は下記「CI 実測（成立確認）」）。旧値の 2 つはいずれも二重記載の
      2 倍で説明がつく——**266 = 133 × 2**（全プロジェクト実行）、**230 = 115 × 2**
      （`Platform.Bff.Tests` 単体実行。115 はレビューの独立実測）。**266 と 230 の差そのものはスコープ差**
      であり二重記載とは別の要因である（出典:
      [`20260803_issue-453`](./20260803_issue-453_regression-test-foundation.md) の「既知の限界」節）。
      230 行と CI の全体集計（133 行）はスコープが異なり直接比較できない
- [x] 実レポートに対して AST 由来の行が集計から落ちることを実測で確認する → **確認済み**（未帰属 0 件・
      6 クラスすべて `<sources>` 結合で `ai-stock-trading` へ帰属・除外前後で 27413 → 27280 行）
- [x] フィルタが何にもマッチしなかった場合に気付ける（帰属 0 件で warn／class 外の行で warn／
      除外 0 行で notice）。**warn 経路は単体テストで固定**する
- [x] 除去後の実測値で床を置き直す → **実施（結果は据え置き）**。実測 `line 34.14%` / `branch 17.26%` の
      整数切り下げが現在値（`line 34` / `branch 17`）と同値のため 2 定数は変更せず、**根拠のみ**
      （混入込み・二重記載込み → 混入抜き・class 直下計数）を測定条件つきで `$comment` へ差し替えた。
      **余裕は薄い（line +0.14pt / branch +0.26pt）**
- [x] [`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md) の「既知の限界」節を解消済みに更新する
      （~~**数値は書かない**——確定値は CI 実測後に定まるため、機構の説明に留める~~ →
      **［CI 実測後に改訂］**機構の説明に加え、**測定条件つきの実測表**と「分岐の定義差」を追記した。
      当初は確定値が無いため数値を避けたが、実測が出たため測定条件とともに記載する方針へ変えた）
- [x] `node scripts/check-coverage-floor.js --self-test` が exit 0
- [x] `node scripts/scripts.test.js`（`REQUIRE_REPO_TESTS=1` でも）が緑で、テスト件数が着手前から減らない
- [x] `node scripts/check-doc-links.js` が exit 0
- [x] レポート 0 件のローカル環境で `node scripts/check-coverage-floor.js` が従来どおり
      「切り分け可能な warn ＋ exit 0」で終わる（fail-open の挙動を変えない）

## 測定条件（再現性）

- 対象コミット: `origin/develop` = `0c2cd83` から作成した worktree。
- **submodule は未 populate**（`git submodule status` が `planning` / `src/ai-stock-trading` に `-` を付ける）。
  したがって `src/ai-stock-trading` は空であり、レポートも 0 件である。
- **実装セッションの環境に .NET SDK は無く、導入もできなかった**（`builds.dotnet.microsoft.com` への接続が
  ネットワークポリシーで遮断されることを当該セッションで実測）。`dotnet test --collect:"XPlat Code Coverage"`
  をローカルで実走できないため、実レポートに対する検証は **CI 実走のログ経由**で行った（この条件を書かない
  実測値は再現不能である。#484 / #486 の教訓）。**これは当該セッション・当該環境に限った観測**であり、
  本 PR のレビューは .NET SDK 10.0.302 のある環境で `Platform.Bff.Tests` を実走して独立検証している。
- Node: 実行環境の Node（CI は 20）。本スクリプトは Node 標準モジュールのみを使う。

## 検証（実測）

| コマンド | 結果 |
| --- | --- |
| `node scripts/check-coverage-floor.js --self-test` | 自己試験 **41 件 OK** / exit 0（着手前 14 件） |
| `node scripts/scripts.test.js` | **239 tests passed** / exit 0（着手前 **225 件** → +14。着手前の値は改修 2 ファイルを一時退避して実測） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 239 tests passed / exit 0 |
| `REQUIRE_REPO_TESTS=1 GITHUB_ACTIONS=true node scripts/scripts.test.js` | 239 tests passed / exit 0。フィクスチャ由来のアノテーション漏れ 0 件 |
| `node scripts/check-doc-links.js` | OK: **405 件**の Markdown に破損リンクなし / exit 0 |
| `node scripts/check-coverage-floor.js`（レポート 0 件のローカル） | 従来どおり切り分け可能な warn ＋ exit 0（fail-open の挙動は不変） |

### CI 実測（成立確認・受け入れ基準 1/2/4 の根拠）

測定条件: CI run 30886437108（run_number **1144**）/ job `build-and-test`（91918575452）/
commit `594117a` / Release 構成 / レポート **14 件** / submodule populate 済み / 結果 **success**。
**測定条件のない実測値は再現できない**ため、本表を引用する際は必ず条件も併記すること。

| 観測点 | 実測 |
| --- | --- |
| 集計（除外後） | **line 34.14%（9314/27280） / branch 17.26%（1536/8898）**。床 `line 34` / `branch 17` を上回る |
| 除外（混入） | **ai-stock-trading 由来 6 クラス / 133 行（被覆 133） / 分岐 50（被覆 41）** |
| 除外前 | `line 34.46%（9447/27413）` / `branch 17.62%（1577/8948）` |
| 帰属 | クラス 2036 件（そのまま(相対) 645 / そのまま(絶対) 0 / `<sources>` 結合 1391 / **未帰属 0**） |
| `<sources>` | 複数（例: `…/src/` と `…/src/platform/backend/`）→ **多段解釈が必須だったことの裏づけ** |
| 除外クラス | `AssumptionsBffEndpoints` / `MonitorBffEndpoints` / `RiskControlsBffEndpoints` と各 `<ProxyAsync>d__2` |
| coverlet 照合（行） | `lines-valid 27413`（本実装 27413・**一致**） / `lines-covered 9447`（本実装 9447・**一致**） |
| coverlet 照合（分岐） | `branches-valid 9356`（本実装 8948・**差 -408**）→ **定義差。期待される乖離** |

独立検証（本 PR の AI レビュー）でも同傾向。**測定条件: .NET SDK 10.0.302 / `Platform.Bff.Tests` 単体実行 /
commit `594117a` 時点 / ビルド構成はレビューコメントに記載が無いため断定しない**（CI 側は Release 構成であり、
スコープも構成の記録も異なる）。`lines-valid` / `lines-covered` は 1950/1950・1274/1274 と完全一致、
`branches-valid` のみ 700 対 600 と乖離。
レビューは「全 `<line>`（`<methods>` 重複込み）の `condition-coverage` 分母合算 1200 のちょうど半分が
600 ＝本実装値」であることを生データで確認しており、**本実装の集計は一貫している**。
coverlet 側の算出経路（IL 分岐点ベース等）は**推定であり一次出典未検証**——確定しているのは
「定義が異なり一致しない」という観測事実のみである。

この定義差は床に影響する: **被覆数を据え置いたまま分母だけ coverlet 基準へ置き換える試算**では、除外前が
`1577 ÷ 9356 = 16.86%`（17.62% から低下）、床が判定に使う除外後の対でも `1536 ÷ (9356 − 50) = 16.51%` で、
いずれも床 17 を下回る。**これは分母差の影響を測る試算であって「coverlet 定義での実際の分岐率」ではない**
（定義を変えれば分子も同じ定義で数え直すことになる）。いずれにせよ
**分岐の定義変更は床の置き直しとセットでしか行えない**
（[IADR-0123](../adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md) 決定 4 の［2026-08-04 追記］）。

### 実物に近いレポートでの実挙動（`src/` へ一時設置して素実行）

coverlet の出力を模したレポート（`<sources>` あり・`filename` が相対と絶対の混在・二重記載あり・
AST のクラスと非同期ステートマシン `<Map>d__2` を含む）を `src/platform/…/TestResults/` へ置いて実行した。

| 観測点 | 実測 |
| --- | --- |
| 集計 | `line 50%（3/6）`（platform の 2 クラス・class 直下の行のみ） |
| 除外 | `ai-stock-trading 由来 2 クラス / 4 行（被覆 4）` |
| 除外前 | `line 70%（7/10）` |
| 解釈の内訳 | `そのまま(絶対) 1 / <sources> 結合 3 / 未帰属 0` |
| coverlet 値との照合 | `lines-valid 10（本実装 10・一致）` |
| 除外クラス一覧 | `MonitorBffEndpoints` と `MonitorBffEndpoints/<Map>d__2` を filename 付きで列挙 |

二重記載を素朴に数えた場合（改修前の方式＝文書全体の `<line>` を数える）は同レポートで **16 行**になる。
**class 直下のみを数えることで 10 行**になり、coverlet 自身の `lines-valid`（10）と一致した。撤去後は `git status` がクリーンであることを
確認している（同経路は `scripts.repo.test.js` の子プロセステストとして自動化済み）。

## 影響・リスク

- **床判定が赤くなる可能性**: 二重記載を排したことで絶対数は変わるが比率はほぼ不変、加えて AST の
  混入（すべて被覆済み）を除くため実測値は**わずかに下がる**。PR #464 の推定（`34.19%` / `34.14%`）は
  いずれも床 34 を上回るが、**二重記載の排除による比率の微差**は実測するまで確定しない。CI が赤くなった
  場合は、床の置き直し（実測の整数切り下げ）で対応する——これは退行ではなく、**混入込みの値から
  切り下げた床を、混入抜きの実測へ置き直す**作業である（IADR-0118 決定 2 の作法どおり）。
- **正規表現ベースの XML 走査の限界**: 外部依存ゼロの原則（IADR-0118 決定 1）を守るため XML パーサを
  入れない。`<class>` は入れ子にならず属性値に `>` を含まないという Cobertura の構造に依存する。
  想定外の構造は「未帰属」として診断に出るため、黙って壊れることはない。
- **診断出力の量**: 既定出力は数行に抑え、レポート単位の詳細は `COVERAGE_FLOOR_DEBUG=1` に置く。

## フォローアップ

1. ~~**床の置き直し**（CI 実測後・`src/coverage-floor.json` の 2 定数）。~~ **完了**（run 1144）。
   切り下げが現在値と同値のため値は据え置き、根拠のみ差し替えた。追随先の確認結果:
   [IADR-0118](../adr/IADR-0118_backend-coverage-floor.md) 決定 2 = 日付付き追記で更新／
   [`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md)「床の置き方」= 現行の根拠を追記／
   [`docs/DEFINITION_OF_DONE.md`](../DEFINITION_OF_DONE.md) = **床の値を書いておらず JSON を参照するのみ
   のため変更不要**／[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 6 =
   記載値 `line 34` / `branch 17` が据え置きのため**変更不要**（値の正は `src/coverage-floor.json`）。
2. 各ドメイン issue がテストを追加したら床を引き上げる（ratchet。IADR-0118 決定 3）。
   **余裕は薄い**（line +0.14pt / branch +0.26pt）ため、引き上げ幅は実測を見て決める。
3. **分岐の定義**（`condition-coverage` 合算）を変える場合は、**床の置き直しとセット**でしか行わない
   （新 IADR ＋ `src/coverage-floor.json` を同一 PR で。IADR-0123 決定 4 の［2026-08-04 追記］）。
