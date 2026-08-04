---
title: 作業仕様書 — カバレッジ床の集計から合成点テスト経由で混入する AST の行を除く（filename 帰属除外）
type: spec
status: in-progress
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
    本作業の環境に .NET SDK が無く（後述「測定条件」）、除去後の実測は **CI 実走のログでしか得られない**。
    値の置き直しは CI 実測を見てから、**同ファイルの 2 定数のみ**の変更で行う（本作業はその構造を保つ）。
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

**本作業の環境では実レポートを取得できない**（.NET SDK 無し・導入経路も遮断。後述「測定条件」）。
したがって「属性の形を仮定して書いたらフィルタが何にもマッチせず素通りした」という失敗（issue #468 の
着手時注意）を、**仮定を置かない実装**と**診断出力**の二段で防ぐ。

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

よって帰属判定は次の順で行い、**どの解釈で当たったかを診断に出す**（当たり方が想定と違えば読み取れる）。

| 順 | 解釈 | 判定 |
| --- | --- | --- |
| 1 | `filename` をそのまま見る | パスの途中に `src/<unit>/` を含むか（相対 `src/…` も絶対 `/home/…/src/…` も、deterministic の `/_/src/…` も同じ規則で当たる） |
| 2 | `<source>` の各値と結合して見る | base path が `…/src/` より深い場合（`filename` が `ai-stock-trading/backend/…` や `Endpoints/Foo.cs` になる場合）に当たる |
| 3 | どちらでも当たらない | **未帰属**として集計に残し（＝黙って落とさない）、件数とサンプルを診断に出す |

## 二重記載（`<methods>` 配下と class 直下の `<lines>`）の扱い

**決定: class 直下の `<lines>` を正とし、`<methods>` 配下の `<line>` は内訳として数えない。**

- 根拠: coverlet の Cobertura では、class 直下の `<lines>` は当該クラスの全行の一覧であり、
  `<methods>` 配下はその**メソッド別の内訳**である（同じ行が両方に現れる）。両方数えると、メソッドを
  持つクラスの行だけが 2 票を持ち、**メソッド外の行（初期化子・属性行など）との重みが崩れる**。
  IADR-0118 が「ファイル単位の単純平均は実態より高く出る」として行数加重を選んだのと同じ理屈である。
- 副作用: 集計の**分母と分子がともに約半分になる**（比率はほぼ不変）。PR #464 の実測
  `line 34.46%（18894/54826）` の絶対数は本改修後の表示と直接比較できない。**床は比率であり、
  比率の変化は小さい**見込みだが、確定は CI 実測で行う（本作業では床を触らない）。
- **仮定であることを診断で検証可能にする**: `<coverage>` 要素の `lines-valid` / `lines-covered`
  （coverlet 自身の集計値）をレポート単位で読み出し、本実装の集計値と並べて出す。両者が一致すれば
  「class 直下が正」という前提が実レポートで裏づけられる。乖離すれば数値として現れる。
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
| [`scripts/check-coverage-floor.js`](../../scripts/check-coverage-floor.js) | `parseCobertura` を class 単位走査へ作り替え。新規の公開 API: `parseSources` / `classBlocks` / `unitOfFilename` / `classLineStats` / `aggregateReports` / `attributionMessages` / `isExcludedUnitFilename`。`--self-test` を拡張 |
| [`scripts/scripts.repo.test.js`](../../scripts/scripts.repo.test.js) | Cobertura フィクスチャによる単体テストを追加（既存の coverage-floor 節へ） |
| [`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md) | 「既知の限界: 合成点テスト経由の混入」を解消済みへ書き換え、ゲート一覧の対象欄を更新 |
| [`docs/adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md`](../adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md) | 新規起票 |
| [`docs/adr/README.md`](../adr/README.md) | 索引へ 1 行追加 |
| [`src/coverage-floor.json`](../../src/coverage-floor.json) | **本作業では値を変更しない**。`$comment` に「絶対数の意味が変わったこと」「置き直しは CI 実測後」を追記 |

## 受け入れ基準（issue #468）

- [ ] 混入行数を実レポートで測り直し、確定させる → **CI 実測待ち**（本環境では .NET SDK が無く測れない。
      診断出力が CI ログへ確定値を出す）
- [ ] 実レポートに対して AST 由来の行が集計から落ちることを実測で確認する → **CI 実測待ち**
      （除外サマリの「除外クラス一覧」「除外前後の実測値」で確認できる）
- [x] フィルタが何にもマッチしなかった場合に気付ける（帰属 0 件で warn／class 外の行で warn／
      除外 0 行で notice）。**warn 経路は単体テストで固定**する
- [ ] 除去後の実測値で床を置き直す → **CI 実測後に `src/coverage-floor.json` の 2 定数のみで実施**
- [x] [`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md) の「既知の限界」節を解消済みに更新する
      （**数値は書かない**——確定値は CI 実測後に定まるため、機構の説明に留める）
- [x] `node scripts/check-coverage-floor.js --self-test` が exit 0
- [x] `node scripts/scripts.test.js`（`REQUIRE_REPO_TESTS=1` でも）が緑で、テスト件数が着手前から減らない
- [x] `node scripts/check-doc-links.js` が exit 0
- [x] レポート 0 件のローカル環境で `node scripts/check-coverage-floor.js` が従来どおり
      「切り分け可能な warn ＋ exit 0」で終わる（fail-open の挙動を変えない）

## 測定条件（再現性）

- 対象コミット: `origin/develop` = `0c2cd83` から作成した worktree。
- **submodule は未 populate**（`git submodule status` が `planning` / `src/ai-stock-trading` に `-` を付ける）。
  したがって `src/ai-stock-trading` は空であり、レポートも 0 件である。
- **.NET SDK は無く、導入もできない**（`builds.dotnet.microsoft.com` への接続がネットワークポリシーで
  遮断されることを実測確認済み）。`dotnet test --collect:"XPlat Code Coverage"` をローカルで実走できない。
  よって実レポートに対する検証は **CI 実走のログ経由**で行う（この条件を書かない実測値は再現不能である。
  #484 / #486 の教訓）。
- Node: 実行環境の Node（CI は 20）。本スクリプトは Node 標準モジュールのみを使う。

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

## フォローアップ（本作業では行わない）

1. **床の置き直し**（CI 実測後・`src/coverage-floor.json` の 2 定数）。あわせて
   [IADR-0118](../adr/IADR-0118_backend-coverage-floor.md) の記載値・
   [`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md)・
   [`docs/DEFINITION_OF_DONE.md`](../DEFINITION_OF_DONE.md)・
   [IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 6 の値を追随させる
   （値の正は `src/coverage-floor.json`）。
2. 各ドメイン issue がテストを追加したら床を引き上げる（ratchet。IADR-0118 決定 3）。
