---
title: IADR-0507 integration.yml は Category=Integration の試験をユニットごとに数え、宣言のあるユニットで実走 0 件か「依存を得られない」skip が 1 件でもあれば赤にする。統合試験の識別は TRX ではなく VSTest の発見に訊き、skip 件数そのものの上限は置かない
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0007, ADR-0090, IADR-0232, IADR-0414]
author: claude
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0090（決定 3）
related_specs:
  - ../specs/20261008_1788_integration-all-skip-gate.md
---

# IADR-0507: 統合試験の「全 skip で緑」を区別する門（#1788）

- 状態: Accepted
- 日付: 2026-10-08
- 決定者: claude（#1788。第 4 回全体監査 #1772 の行「ADR-0090 決定 3」の切り出し。作業仕様書 20261008_1788）

## 起点・関連

- 起点 issue: #1788（台帳 #1772）
- 計画: ADR-0090 決定 3（planning#575 の裁定）。要求は「統合テストが 1 件も実走しなかった CI 実行を、実走した実行と区別できること」。手段は実装の裁量で、決めた形を IADR に残すことを求めている（フォローアップ 2）。
- 前提: IADR-0414（統合試験の門は依存ごとに「得られるか」を訊いて skip する）、IADR-0232 改定 3（`integration.yml` は `--filter` を付けず全量を 1 回走らせる）
- 先例: AST#1200（AST の IADR-0497。`check-integration-skips.js` で skip 件数の上限 0）
- 基点コミット: `origin/develop` `2f2aa957`

## コンテキストと課題

`integration.yml` には床（`check-coverage-floor.js`）しか門が無い。床は単体試験で満たせるため、統合試験が全 skip でも割れない（ADR-0090 決定 3 が名指しで退けた形）。

### 実測（2026-10-08）

1. **TRX は xUnit の Trait を書かない。** `Knowledge.IntegrationTests` を `--logger trx` で走らせた TRX の `UnitTest` 要素に `TestCategory` も `Properties` も無い。TRX だけでは「どの結果が `Category=Integration` か」を判別できない。
2. **`dotnet test --list-tests --filter "Category=Integration"` はフィルタを効かせる。** 同プロジェクトで 59 件（フィルタなしは 124 件）。knowledge の slnx 全体でも 59 件（統合試験の宣言は `Knowledge.IntegrationTests` だけ）。platform ユニットの宣言は 0 件（platform の結合試験は `TestKind=Integration` で、Docker を要さない）。
3. **並列の発見は出力が割り込む。** slnx への `--list-tests` は「見出し → 別プロジェクトの `Test run for` → 見出し」の並びで出る。
4. **依存を要らない統合試験がある。** Docker を隠して（`unshare -m` で `/var/run` に tmpfs を被せ、`CI`・`DOCKER_HOST` を外す）knowledge の slnx を走らせると、`dotnet test` は全プロジェクト `Passed!`、統合試験は宣言 59 件のうち **実走 1 件**（`WolverineBrokerEdgeTests` の構成の試験）・skip 58 件（うち門の「依存を得られない」57 件、`PLATFORM_TEST_RABBITMQ 未設定のため判定対象外` 1 件）だった。**「実走 0 件」だけを門にすると、依存が 1 つも揃わない実行でも実走 1 件で緑になる。**
5. **依存が揃った CI でも skip する統合試験がある。** develop の回収実行（run 37649876100）は knowledge で `Skipped: 1`（`WolverineBrokerEdgeTests.外部ブローカが設定されていればDockerが無くても実走する`。外部ブローカを与えたときだけ走る条件 skip）。
6. **CI では門が Docker を「ある」と答える。** `DockerRequired.IsAvailable()` は `CI=true` で `true` を返すため、現状の GitHub ランナーでは依存不足の skip は起きない（Docker が無ければ skip ではなく失敗になる）。本門が守るのは、その前提が崩れたとき（`CI` の扱いの変更、外部供給だけの実行、ランナーの変更）と、Trait・一覧・TRX の配線が壊れたときである。

## 検討した選択肢

| 案 | 評価 |
| --- | --- |
| A. AST と同じ「skip 件数の上限 0」（対象アセンブリの全 skip） | **採らない。** 実測 5 の条件 skip が毎回赤になる。対象外の名簿を持つと名簿が腐る |
| B. 実走件数の下限（ユニットごとに 1） | **単独では採らない。** 実測 4 のとおり依存なしで実走する統合試験があり、依存が揃わない実行を止められない。ADR-0090 決定 3 の字面（実走 0 件）は満たすが趣旨を満たさない |
| C. 宣言件数に対する固定の下限（「59 件以上」等） | **採らない。** 試験の増減で写し直す導出値になり腐る（規則 10） |
| D. 依存の事前疎通検査（試験の前に Postgres 等へ繋ぐ step） | **採らない。** 依存の一覧と入手経路（Docker / 外部供給）をワークフローに二重に持つことになり、門（IADR-0414）とずれる |
| **E. B ＋「依存を得られない」skip の上限 0**（理由の文言の目印で判別） | **採用。** 条件 skip を通し、依存不足の skip だけを止める。目印の腐りは repo 試験で止める |

統合試験の識別は、TRX が Trait を持たない（実測 1）ため、**VSTest の発見に同じフィルタで訊く**（`--list-tests --filter "Category=Integration"`）。試験を `Category` で 2 回に分けて走らせる案は採らない —— Cobertura が 1 プロジェクト 2 本になり、床の件数突合（IADR-0232 改定 3 の (a)）を壊す。

## 決定

1. **`integration.yml` の試験 step はユニットごとに 2 つの入力を残す。** `dotnet test` に `--logger trx` を足す（TRX は各試験プロジェクトの `TestResults/`）。🔴 **TRX ロガーは収集した Cobertura を `TestResults/<run>/In/<machine>/` へ複製する**（PR #1794 の初回の回収実行で床の件数突合が「38 件 / 期待 19 件」で落ちて判明。ローカルでも再現）。複製は試験の直後に `find "src/$unit" -type d -path '*/TestResults/*/In' -prune -exec rm -rf {} +` で消す（本体の `TestResults/<guid>/coverage.cobertura.xml` と TRX は残る）。床の判定器・床の値には触れない。続けて `dotnet test <slnx> --no-build --list-tests --filter "Category=Integration"` の出力を `$RUNNER_TEMP/integration-lists/<unit>.list` へ残す。**`--filter` は発見だけに掛かり、実行は全量のまま**（IADR-0232 改定 3 を崩さない）。
2. **`scripts/check-integration-executed.js` がユニットごとに判定する。** 一覧の名前（引数部を落とした「クラス.メソッド」）と TRX の結果を突き合わせ、宣言・実走（合格＋失敗）・skip（うち依存不足）・結果なしを数える。次のいずれかで赤:
   - 一覧が読めない（見出しが無い＝ビルド失敗・コマンド失敗。「宣言 0 件」と読まない）
   - 宣言のあるユニットで実走が下限（既定 1）を割る
   - **「依存を得られない」skip が上限（既定 0）を超える**
   - 壊れた TRX がある
   - 検査対象のどのユニットにも宣言が無い（Trait・一覧の配線の破れ）
3. **依存不足の判別は門の skip 理由の先頭の文言で行う**（`この試験が要るサービスを得られない`〔`RequiredServices`〕・`No broker available`〔`BrokerRequired`〕）。目印が門の実装に在ることを `scripts.repo.test.js` が検査し、片方だけの変更を落とす。
4. **skip 件数そのものの上限は置かない。** 条件 skip（実測 5）は通す。
5. **submodule のユニット（ai-stock-trading）は対象外**（`lib/excluded-units.js`）。AST 自身の `integration.yml` が同じ守りを持つ（AST#1200）。
6. **床と同じく、テストが赤くても評価する**（`!cancelled() && steps.tests.outcome != 'skipped'`）。ユニットごとの表を `$GITHUB_STEP_SUMMARY` へ出す。

### AST#1200（AST の IADR-0497）との異同

| | AST | MSP（本 IADR） |
| --- | --- | --- |
| 統合試験の識別 | 専用アセンブリ（`AiStockTrading.IntegrationTests`）の TRX をまるごと | `Category=Integration` の発見一覧 × TRX（統合試験のアセンブリに `Category=Deployment` 等が同居するため） |
| skip | 件数の上限 0（理由を問わない） | 依存不足の skip だけ上限 0。条件 skip は通す |
| 実走 | 下限 1 | ユニットごとに下限 1（宣言のあるユニットだけ） |
| 共通 | TRX の結果を 1 件ずつ数える（`Counters` の `notExecuted` は skip を数えない）。壊れた TRX・TRX なしは赤。件数の下限は置かない。テストが赤くても評価する |

## 結果・影響

- 回収実行の所要が `--list-tests`（発見だけ。ユニットごとに数秒〜十数秒）ぶん延びる。
- 起動条件（push: develop・日次・手動）とジョブ名（`integration`）は変えない。必須 check ではない（`docs/ai-workflow.md` の表は不変）。
- 依存なしで走る統合試験を `Category=Integration` から外す・依存を要る試験の門を別の文言で作る、のどちらかをすると本門の効きが変わる。後者は repo 試験が止める。

## 残余リスク

- **部分的な依存不足**（例: Qdrant だけ得られない）は依存不足の skip として赤になる。依存が揃わない実行を緑にしない側へ倒した。意図して外部供給を一部だけ与える回収実行が必要になったら、`--max-dependency-skips` を上げるのではなく、その実行を別ワークフローにすること。
- 門を通らずに `Assert.Skip` を直接呼ぶ統合試験が依存不足で skip しても、依存不足とは数えない（実走下限だけが効く）。新しい門を足すときは目印へ加え、repo 試験の対象ファイルへ足すこと。
- `CI=true` で Docker を「ある」と答える現状（実測 6）では、依存不足の skip は CI で出ない。本門は ADR-0090 フォローアップ 4 の「発火したら報告する」状態にまだ無い。
