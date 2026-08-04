---
title: IADR-0123 カバレッジ床の集計は Cobertura の class 直下 <lines> を正とし、<class filename> でユニットへ帰属させて除外する
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0034, IADR-0115, IADR-0118, IADR-0120]
author: Claude
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - ../specs/20260804_issue-468_coverage-ast-exclusion.md
  - ../specs/20260803_issue-453_regression-test-foundation.md
  - ../tests/TEST_STRATEGY.md
---

# IADR-0123: カバレッジ床の集計は Cobertura の class 直下 `<lines>` を正とし、`<class filename>` でユニットへ帰属させて除外する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-08-04
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID（FR/UC/SC/ADR）: NFR（品質・保守性。再実装期間中の退行検知の精度）
- 関連する実装 ADR:
  - [IADR-0118](IADR-0118_backend-coverage-floor.md)（バックエンドのカバレッジ床）。本決定は同 IADR
    **決定 4 の「既知の限界」（合成点テスト経由の混入）を解消**し、決定 1 の集計方式を**詳細化**する。
    決定そのものを覆さないため `Supersedes` ではなく**補完**である（IADR-0118 は Accepted のまま）。
  - [IADR-0120](IADR-0120_excluded-units-from-gitmodules.md)（検査対象外ユニットの単一情報源）。
    本決定は除外**集合**を変えず、除外の**適用面**をファイルパスから行の帰属へ広げる。
  - [IADR-0034](IADR-0034_frontend-coverage-gate.md)（フロントのカバレッジ ratchet。対をなすゲート）
  - [IADR-0115](IADR-0115_impl-handoff-kit-as-single-source.md)（キット同期規約。対象スクリプトは
    **固有デルタ種 3**＝本リポにしか存在しないスクリプト）
- 関連する実装仕様書: [20260804_issue-468](../specs/20260804_issue-468_coverage-ast-exclusion.md)
- 関連 issue: [#468](https://github.com/endazon/microservices-platform/issues/468)（本決定の起点。
  親 [#453](https://github.com/endazon/microservices-platform/issues/453) から分割）

## コンテキストと課題

[`scripts/check-coverage-floor.js`](../../scripts/check-coverage-floor.js) は
`dotnet test --collect:"XPlat Code Coverage"` が出す Cobertura XML を直接読み、行/分岐で加重した被覆率を
床と比較する（IADR-0118 決定 1）。集計対象外ユニット（AST）の除外は**レポートファイルのパス**
（`src/ai-stock-trading/…/coverage.cobertura.xml`）でのみ行っていた。

しかし `Platform.Bff` は BFF の合成点として AST の `AiStockTrading.Bff.Endpoints` を `ProjectReference`
しており（FR-14 / IADR-0063 の例外 3）、`Platform.Bff.Tests` はそれをプロセス内で読み込む。よって
**`src/platform/` 配下のレポートの中身に AST のクラスの行が入る**（6 クラス。PR #464 レビューの Release
構成での実測）。パスによる除外はこれに届かない。放置すると **AST の submodule pin 更新だけで床の実測値が
動き**、混入行はすべて被覆済みのため実測値を押し上げる方向にしか働かない——床を引き上げていく過程で
MSP 自身の実力より高い床を置く。IADR-0118 決定 4 が「合算は双方向に濁る」として名指しした劣化が、
除外をすり抜けて残っている状態である。

塞ぐには Cobertura の `<class filename>` で行をファイルへ帰属させる必要があり、既存の
「文書全体の `<line>` を正規表現で数える」パーサは class の文脈を持たないため作り替えが要る。そのとき
**同時に決めねばならないのが二重記載の扱い**である。coverlet は同じ行を `<methods>` 配下と class 直下の
`<lines>` の両方に書くため、素朴な `<line>` カウントは計測条件で振れる（PR #464 のレビューが 2 度計測し
**266 行 / 230 行**と結果が割れた原因）。

さらに制約がある。**本決定を実装する作業環境には .NET SDK が無く、導入もできない**（配布元への接続が
ネットワークポリシーで遮断されることを実測確認済み）。すなわち**実レポートを見ずに設計せざるを得ない**。
issue #468 が着手時注意として名指ししたのはこの危険である——「属性の形を仮定して書くと、フィルタが
何にもマッチせず**除外したつもりで素通り**になる（黙って混入が残るため気付けない）」。

決めるべきは次の 3 点である。(1) 行をどう帰属させるか、(2) 二重記載のどちらを正とするか、
(3) 実レポートを見られない状況で誤った仮定をどう検出可能にするか。

## 検討した選択肢

### 行の帰属（除外の適用面）

| | A. `<class filename>` で帰属（採用） | B. レポートのファイルパスのみ（現状） | C. `<package name>` で帰属 |
| --- | --- | --- | --- |
| 合成点経由の混入 | **落とせる** | 落とせない（本 issue） | 落ちない（package はテスト対象アセンブリ名＝`Platform.Bff` になり得る） |
| 情報源 | ソースファイルのパス（ユニット構成と同じ次元） | 出力先ディレクトリ | アセンブリ名（ユニットとの対応は命名規約頼み） |
| 除外集合との整合 | `src/<unit>/` 規則をそのまま適用できる（IADR-0120） | 同左 | 別の対応表が要る |

### `filename` の解釈

coverlet は「全ソースファイルのうち最も浅いディレクトリ」を base path として `<sources>` に出し、
**base path で始まらないファイルは絶対パスのまま `filename` に書く**（`GetBasePaths` /
`GetRelativePathFromBase`）。deterministic build 指定時は `<source>` が空で `filename` が `/_/src/…` に
なる。すなわち**同一レポート内で相対と絶対が混在し得る**。

| | A. 多段解釈（採用） | B. 相対と決め打ち | C. 絶対と決め打ち |
| --- | --- | --- | --- |
| 相対 `src/…` | 当たる | 当たる | 外れる |
| 絶対 `/home/…/src/…` | 当たる | 外れる | 当たる |
| base が `…/src/` より深い（`ai-stock-trading/backend/…`） | `<sources>` 結合で当たる | 外れる | 外れる |
| deterministic（`/_/src/…`） | 当たる | 外れる | 当たる |
| 外したときの現れ方 | — | **無音で素通り** | **無音で素通り** |

### 二重記載（`<methods>` 配下 と class 直下）

| | A. class 直下を正（採用） | B. 全 `<line>` を数える（現状） | C. `(filename, 行番号)` で重複排除 |
| --- | --- | --- | --- |
| メソッドを持つ行の重み | 1 | **2**（メソッド外の行と重みが崩れる） | 1 |
| 計測条件による振れ | 無い | **ある**（266 / 230 の食い違い） | 無い |
| 部分クラス・非同期ステートマシン（同一ファイルの別 class） | それぞれ数える（coverlet の集計と一致） | 二重に数える | **合算して消える**（どちらの hits を採るかの恣意が入る） |
| coverlet 自身の集計値（`lines-valid`）との照合 | **できる** | できない | できない |

C は「1 ソース行 1 票」に見えて、`Foo` と `<Foo>d__2`（非同期ステートマシン）のように**同一行を
異なる観点で計測している行**を潰す。潰す際にどちらの hits を採るかは恣意的であり、coverlet や
reportgenerator のどの集計とも一致しなくなる。A は coverlet 自身の `lines-valid` / `lines-covered` と
一致するため、**前提が正しいかを実レポートで機械的に照合できる**（後述の決定 4）。

### 誤った仮定の検出（実レポートを見られない制約への手当て）

| | A. 診断出力＋段階的な warn/notice（採用） | B. 何も出さない | C. 帰属 0 件なら fail |
| --- | --- | --- | --- |
| 素通りへの気付き | 出る（CI ログ・実行サマリ） | **気付けない** | 出る |
| カバレッジと無関係な PR への影響 | 無い | 無い | **赤になる**（fail-open の設計〔IADR-0118 決定 5〕と矛盾） |

## 決定

1. **除外は `<class filename>` によるユニット帰属で行う。** レポートファイルのパスによる除外
   （IADR-0118 決定 4）は**併用**する（除外ユニット配下のレポートは読まずに済み、走査も速い）。
   除外ユニット集合は [`scripts/lib/excluded-units.js`](../../scripts/lib/excluded-units.js) から
   のみ導出する（IADR-0120。検査器側にリストを持たない）。
2. **`filename` は多段解釈する。** (1) `filename` そのもの、(2) `<sources>` の各値との結合、の順に
   `src/<unit>/` を探し、最初に当たった解釈を採る。どちらでも当たらない行は「未帰属」として
   **集計に残す**（黙って落とすと実測値が理由不明に下がる）。**どの解釈で当たったかを診断に出す。**
3. **二重記載は class 直下の `<lines>` を正とし、`<methods>` 配下は内訳として数えない。**
   class 直下に `<lines>` が無く `<methods>` にだけ行があるクラスは、**行番号で重複排除した**メソッド行を
   採用し、その発生件数を診断に出す。`<class>` の外にある `<line>`（帰属不能）は集計に残し warn する。
4. **前提の正しさを実レポートで照合可能にする。** `<coverage>` 要素の `lines-valid` / `lines-covered`
   （coverlet 自身の集計値）を読み、本実装の集計値と並べて診断に出す。一致すれば決定 3 の前提が
   実レポートで裏づけられ、乖離すれば数値として現れる。
5. **段階的な可視化**（終了コードは変えない）。
   - 1 クラスもユニットへ帰属しなかった（＝フィルタが no-op） → **warn**
   - `<class>` 外の `<line>` があった → **warn**
   - 帰属は成立しているが除外行が 0 だった → **notice**（合成点の参照が外れれば正常に 0 になる。
     恒常的な warn は「成果物は正しいのに黄」を常態化させ、警告を読まない学習を生む
     ——IADR-0118 決定 6 の段階ポリシー）
   - class 直下の `<lines>` が無くフォールバックしたクラスがあった → **notice**
   - 床未満 → **fail**（従来どおり）
6. **診断は既定で出力する**（`ci.yml` を変更せずに CI ログから読めるようにする）。既定は数行の
   サマリ（除外ユニット由来の行数・除外前後の実測値・解釈の内訳・除外クラス一覧・coverlet 値との照合）、
   レポート単位の詳細は `COVERAGE_FLOOR_DEBUG=1` に置く。`$GITHUB_STEP_SUMMARY` にも除外行数と
   除外前の実測を出す。
7. **床の値は本決定では変更しない。** 決定 3 により集計の絶対数は変わる（比率はほぼ不変）。除去後の
   実測は CI 実走のログでしか得られないため、床の置き直しは実測を見てから
   [`src/coverage-floor.json`](../../src/coverage-floor.json) の 2 定数のみで行う（IADR-0118 決定 2 の
   「実測からの整数切り下げ」の作法を維持する。ratchet の例外ではなく、**混入込みの実測から切り下げた
   床を、混入抜きの実測へ置き直す**作業である）。

## 理由

- **帰属の次元を合わせたこと**が要点である。除外したい単位は「ユニット（ソースの所在）」であり、
  出力先ディレクトリでもアセンブリ名でもない。`<class filename>` はユニット構成と同じ次元にあり、
  IADR-0120 の `src/<unit>/` 規則をそのまま適用できる。
- **多段解釈を選んだのは、実レポートを見られない制約への唯一誠実な対応**だからである。決め打ちは
  外れたときに「フィルタが何にもマッチしない＝除外したつもりで素通り」という**無音の失敗**になる。
  これは #453 が実際に踏んだ失敗（collector 参照が無く 0 件のまま床が緑）と同型であり、
  IADR-0118 決定 5 が「原因不明の warn は、この検査が無いのと同じ」と書いた教訓に当たる。
- **class 直下を正とする理由は重みの一貫性**にある。全 `<line>` を数えるとメソッドを持つ行だけが
  2 票を持ち、メソッド外の行との重みが崩れる。IADR-0118 が「ファイル単位の単純平均は実態より高く出る」
  として行数加重を選んだのと同じ理屈を、class 内部の粒度へ適用したものである。
- **coverlet 自身の集計値との照合を仕込む理由**は、決定 3 が**実レポート確認前の仮定**だからである。
  仮定を仮定のまま置かず、CI の初回実走で真偽が数値として出る形にする。
- **段階的な可視化にとどめ fail にしない理由**は、床が fail-open（IADR-0118 決定 5）で設計されている
  ことと揃えるためである。カバレッジと無関係な PR を赤くすると迂回を誘発する。

## 結果

- 良い影響:
  - AST の submodule pin 更新が MSP の床判定へ影響しなくなる（IADR-0118 決定 4 の既知の限界が解消）。
  - 二重記載の扱いが確定し、**計測条件によって実測値が振れる**（266 / 230）状態が解消する。
  - 除外が効いているか・仮定が正しいかが CI ログと実行サマリで**毎回**読める。将来 `Platform.Bff` の
    参照構成が変わっても、除外 0 行が notice として現れる。
- 悪い影響・トレードオフ:
  - **集計の絶対数が変わる**（分母・分子とも約半分）。PR #464 の実測値（`18894/54826`）と本改修後の
    表示は直接比較できない。床は比率なので判定の意味は保たれるが、**過去の記録を読むときは
    本決定の前後を区別する必要がある**。
  - 正規表現ベースの XML 走査を続ける（外部依存ゼロの原則）。`<class>` が入れ子にならず属性値に `>` を
    含まないという Cobertura の構造に依存する。想定外の構造は「未帰属」として診断に出る。
  - 診断出力のぶん CI ログが数行増える。
  - 決定 3 の前提（class 直下が正）は**実レポートでの確認前**である。反証されれば決定 4 の照合値が
    乖離として現れるため、そのときは本 IADR を改定する新 IADR で扱う。
- フォローアップ:
  1. CI 実走のログで混入行数の確定値と除去後の実測値を読み取り、**床を置き直す**（決定 7）。
     置き直したら IADR-0118 の記載値・[`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md)・
     [`docs/DEFINITION_OF_DONE.md`](../DEFINITION_OF_DONE.md)・IADR-0116 規約 6 を追随させる
     （値の正は `src/coverage-floor.json`）。
  2. 決定 4 の照合値が乖離した場合は、二重記載の扱いを再判断する（新 IADR）。

## 関連

- Supersedes: なし（[IADR-0118](IADR-0118_backend-coverage-floor.md) 決定 1・4 を**補完**する。
  IADR-0118 は Accepted のまま）
- Superseded by: なし
- 実装: [`scripts/check-coverage-floor.js`](../../scripts/check-coverage-floor.js)（`--self-test` 付き）／
  [`scripts/scripts.repo.test.js`](../../scripts/scripts.repo.test.js)／
  [`scripts/lib/excluded-units.js`](../../scripts/lib/excluded-units.js)（除外集合の単一情報源・参照のみ）
