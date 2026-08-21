---
title: テスト戦略（退行防止テスト基盤）
type: test-spec
status: in-progress
created: 2026-08-03
updated: 2026-08-21
author: Claude
---
<!-- trace:
ids: [SC-05, SC-06, SC-07, SC-08]
adrs: [ADR-0027, ADR-0030]
iadrs: [IADR-0034, IADR-0049, IADR-0115, IADR-0116, IADR-0118, IADR-0120, IADR-0122, IADR-0123, IADR-0130, IADR-0137, IADR-0138, IADR-0195]
specs: [20260803_issue-453_regression-test-foundation, 20260807_issue-571_coverage-exclude-generated]
issues: [#454, #503, #510, #568, #571, #580, planning#146, planning#160, planning#161, planning#162, planning#180]
-->

# テスト戦略 — 再実装の退行防止基盤

> リポジトリ単位の横断ドキュメント。個別の FR/SC のテスト仕様書は同ディレクトリの
> `FR-xx_*.md` / `SC-xx_*.md` に置く。作業仕様書:
> 仕様書: 再実装の退行防止テスト基盤

## なぜ要るか

全面再実装では**既存実装を破棄し得る**。コードが入れ替わるため、退行の検知手段をコードでは
なく**テストへ移す**必要がある。#453 は各ドメイン issue（#438〜#452）のテストが載る共通基盤と横断
ルールを、他のすべてに先立って整備する。

## 受け入れ基準 → テストの写像規約

計画書（`02_requirements` / `03_usecases` / `05_screens`）の受け入れ基準を、**テストの直前のコメントに
起点 ID を書く**ことで突合可能にする。

```csharp
// FR-03, UC-01: ハイブリッド検索は語彙一致とベクトル類似の両方を返す
[Fact]
public async Task 検索は語彙一致とベクトル類似の両方を返す() { ... }
```

```ts
// SC-02: 検索結果一覧は 0 件のとき空状態を表示する
it('0 件のとき空状態を表示する', () => { ... })
```

### なぜテスト名ではなくコメントか

テスト名に ID を埋める規約（`FR03_...`）は、**日本語のテスト名**という本リポジトリの既存慣習と両立
しない。また ID が変わるたびにテスト名が変わり、履歴の追跡が切れる。コメントなら
[`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)「テスト: テスト名またはコメントに
起点 ID を残す」の既存規約にそのまま乗る。

### 規約

- テストメソッド / `it` / `test` の**直前**のコメントに起点 ID を 1 つ以上書く（`FR-\d+` / `UC-\d+` / `SC-\d+` / `NFR`）。
- 複数 ID はカンマ区切り（`// FR-03, UC-01: ...`）。
- 他プロジェクトの ID は修飾する（`AST/FR-17`）。**修飾付き ID は本リポジトリの突合対象から除外される**。
- **起点 ID を持たないテストを禁止しない。** 基盤・回帰・検査器自身のテストは計画 ID に紐づかない。
  検査が見るのは「仕様書がある FR/SC にテストが 1 件も無い」ことだけである。

## ゲート一覧

| ゲート | 対象 | 実行 | 判定 |
| --- | --- | --- | --- |
| **写像検査（順方向）** | `docs/tests/` の FR/SC ↔ `src/` のテスト | [`check-test-traceability.js`](../../scripts/check-test-traceability.js) | allowlist（`pending`）に無い未写像 → **fail**。allowlist 内 → warn。写像済みなのに allowlist 残置 → **fail** |
| **写像検査（逆方向・[#472](https://github.com/endazon/microservices-platform/issues/472)）** | 計画レンジ（[`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)「起点 ID の種別」節）↔ `docs/tests/` | 同上 | 仕様書の無い計画 ID → **warn**（未着手は正当）。うち `src/` のテストが参照済み（＝実装先行）で allowlist（`specMissing`）に無いもの → **fail**。仕様書ができたのに `specMissing` 残置 → **fail**。レンジをパースできない → **fail**（0 件検査への退行を止める） |
| **記載の被覆（[#510](https://github.com/endazon/microservices-platform/issues/510)）** | **`docs/tests/` の仕様書ファイル × `src/**/*Tests.cs` のクラス**（AST を除く）の対 | [`check-test-spec-coverage.js`](../../scripts/check-test-spec-coverage.js) | [`test-spec-coverage-baseline.json`](../../scripts/test-spec-coverage-baseline.json) の床にある対が消えた → **fail**（節の消失。**他の仕様書に同じクラスの記載が残っていても落ちる**——落ちるのは節であり、節は仕様書に属するため）。床にある対のクラスが実在しない → **fail**。記載された対が床に無い → **fail**（`--update` で上げる）。どの仕様書にも載らず床にも無いクラス → warn。走査 0 件・床が読めない → **fail**（テスト仕様書の「節ごと落ちる」を、テストクラス単位の被覆 ratchet で機械検査するという実装判断による） |
| **バックエンド カバレッジ床** | `src/platform/backend/**` ・ `src/knowledge/backend/**`（**AST は対象外**。レポートのファイルパスに加え、**行を `<class filename>` でユニットへ帰属させて**合成点経由の混入も落とす——後述「合成点テスト経由の混入」・[#468](https://github.com/endazon/microservices-platform/issues/468) / カバレッジ床の集計は Cobertura の class 直下 `<lines>` を正とし、`<class filename>` でユニットへ帰属させて除外する。**生成コードも対象外**——EF（`Migrations/` 配下・`*ModelSnapshot.cs`）は [#571](https://github.com/endazon/microservices-platform/issues/571) / カバレッジ床は生成コード（EF の Migrations / ModelSnapshot）を集計から落とし、床を置き直す、**source generator の出力（`obj/` 配下）は [#574](https://github.com/endazon/microservices-platform/issues/574) / カバレッジ床は source generator の出力（`obj/` 配下）も集計から落とし、床を置き直す**） | [`check-coverage-floor.js`](../../scripts/check-coverage-floor.js) ＋ `ci.yml` | [`src/coverage-floor.json`](../../src/coverage-floor.json) の床（現在 `line 39` / `branch 27`）未満 → **fail**（バックエンドのカバレッジ床（単一情報源・実測からの切り下げ・ratchet）の実装 ADR） |
| **フロント カバレッジ ratchet** | `src/*/frontend/**` | [`frontend-tests.yml`](../../.github/workflows/frontend-tests.yml) | [`src/vitest.config.ts`](../../src/vitest.config.ts) の `thresholds` 未満 → **fail**（フロントエンドのカバレッジゲート） |
| **ユニット依存規則** | `.csproj` の `ProjectReference` ・Foundation→Composable | [`check-unit-dependencies.js`](../../scripts/check-unit-dependencies.js) | 違反 → **fail** |
| **BFF 境界** | BFF の downstream | [`check-bff-downstreams.js`](../../scripts/check-bff-downstreams.js) | 違反 → **fail** |
| **ライブラリ標準** | `.csproj` ・`.props` / `.targets` の `PackageReference`（`PackageVersion` は対象外）/ `using` ・Domain 層の依存 | [`check-backend-libraries.js`](../../scripts/check-backend-libraries.js) | 新規混入・baseline 減らし忘れ → **fail**（#455 / [#471](https://github.com/endazon/microservices-platform/issues/471)）。**測定範囲＝参照の有無のみ。結合の深さは見ない**（下記「※ ライブラリ標準 ratchet が検出しないこと」） |
| **CPM バージョン直書き禁止** | `src/`（AST を除く）と `templates/` の `.csproj` の `PackageReference`（`.props` / `.targets` は正当な版記述があるため対象外） | [`check-cpm-versions.js`](../../scripts/check-cpm-versions.js) | `Version` 属性 / `<Version>` 子要素 → **fail**（着手時点の違反 0 件を実測したため ratchet 無しで最初から fail）。`VersionOverride` は**許可**し使用箇所を warn ＋実行サマリへ（[#467](https://github.com/endazon/microservices-platform/issues/467)） |
| **契約の後方互換（`Shared.Contracts`）** | `src/<unit>/backend/Shared/*.Contracts`（AST を除く）の public 型・メンバー・enum 値・`const` 値・属性 | [`check-contract-schema.js`](../../scripts/check-contract-schema.js) | 削除・型変更・必須化・位置引数の並べ替え・enum/`const` 値の変更・属性の変更・既定値の無いメンバーの追加 → **破壊的・fail**。非破壊の追加でも [`contract-schema-baseline.json`](../../scripts/contract-schema-baseline.json) と差分があれば **fail**（`--update` で更新し差分を PR の diff に載せる）。破壊的変更は [`contract-breaking-allowlist.json`](../../scripts/contract-breaking-allowlist.json) の承認エントリで通す（[#465](https://github.com/endazon/microservices-platform/issues/465) / 契約スキーマの抽出方式（C# ソース構文解析）と後方互換ゲート） |

### ※ ライブラリ標準 ratchet が検出しないこと

**`check-backend-libraries.js` は「不採用ライブラリを参照しているか」だけを見る検査であり、
結合の深さ（そのライブラリの API・セマンティクスにどれだけ依存しているか）は見ない。**
検査対象は `.csproj` / `.props` / `.targets` の `PackageReference` と `.cs` の `using` 宣言だけである。
したがって **baseline 済みのプロジェクト内で結合が深まっても「新規混入 0 件」の緑のまま**になる。

実例: `bc7bc8e`が
`src/knowledge/backend/Services/ConversionService/src/ConversionService.Worker/Composable/Steps/RawDocumentFetchedConsumer.cs:81`
へ `context.GetRetryAttempt() + 1 >= MassTransitExtensions.MaxAttempts` を追加し、production の判定
ロジックが MassTransit の再試行セマンティクスに依存するようになったが、`using MassTransit;` は既存・
`PackageReference` は baseline 済みのため ratchet は動かない（変換ジョブのデッドレターは導出せず記録し、試行上限は再試行設定を単一情報源にする実装 ADR
が自己開示している）。

**危ういのはこの緑を「結合が増えていない証拠」として読むことである**（Wolverine への移行
コストの見積りにこの緑を使ってはならない）。移行コストは baseline の**件数**ではなく、
MassTransit の API を直接呼んでいる箇所を都度数えて評価する。
この節は、生成コードをカバレッジ集計から落とす実装 ADR の §結果「検出しないこと」と同じ作法である。

※ `scripts/check-backend-libraries.js` と `scripts/backend-library-baseline.json` は **#455（PR #463）で導入済み**。
未マージ成果物への前方参照は live link ではなくバッククォート表記で書く
（[`docs/DEFINITION_OF_DONE.md`](../DEFINITION_OF_DONE.md) の同項と同じ作法）。
なお [#470](https://github.com/endazon/microservices-platform/issues/470) で
[`check-doc-links.js`](../../scripts/check-doc-links.js) の検査対象拡張子に `.js` などコードファイルを
追加したため、**コードへの live link の破損は機械で検出される**（前方参照を live link で書くと CI が落ちる）。

### 検査対象ユニットの切り分け

写像検査・カバレッジ床・ライブラリ標準はいずれも **`ai-stock-trading` を対象外**とする。AST は独自の
計画リポジトリ・ADR・ID 体系を持つ**別プロジェクト**（submodule）であり、本リポジトリの計画 ID や
バックエンド標準ライブラリの決定を適用するのは誤りである（`.claude/rules/traceability.md`「複数プロジェクトを跨ぐ場合」）。

カバレッジ床でとくに重要なのは、`ci.yml` の `build-and-test` が**全ユニットの `backend.slnx` を自動発見
して test する**ため、除外しないと AST のカバレッジが合算されることである。合算すると双方向に濁る
——AST 側のテストが厚ければ platform / knowledge の実際の退行を薄めて隠し、逆に AST の pin 更新だけで
無関係な PR の床判定が動く。

#### 合成点テスト経由の混入（[#468](https://github.com/endazon/microservices-platform/issues/468) で解消。CI 実測で成立確認済み）

`Platform.Bff` は BFF の合成点として
[`AiStockTrading.Bff.Endpoints`](../../src/platform/backend/Bff/Platform.Bff/Platform.Bff.csproj) を
`ProjectReference` しており、`Platform.Bff.Tests` はそれをプロセス内で読み込んで実行する。その結果
**`src/platform/` 配下にあるレポートの中身に AST のクラス・行が含まれる**。レポートの**ファイルパス**に
よる除外はここに届かず、AST の submodule pin を更新するだけで床の実測値が動く状態だった
（混入行はすべて被覆済みのため、実測値を押し上げる方向にしか働かない）。

[#468](https://github.com/endazon/microservices-platform/issues/468) では、Cobertura の class 直下 `<lines>` を
正とし、`<class filename>` でユニットへ帰属させて除外する方式を採り、
[`check-coverage-floor.js`](../../scripts/check-coverage-floor.js) を class 単位走査へ作り替えて、次の 2 点の
機構を導入した。

1. **行の帰属**: 各 `<class filename>` を `src/<unit>/` へ帰属させ、集計対象外ユニット（単一情報源は `.gitmodules`〔`src/` 直下 1 階層の
   submodule〕であり、読めなければ停止する）の行を集計から落とす。`filename` は相対・絶対・`<sources>` との結合の順に
   多段で解釈する（coverlet は base path で始まらないファイルを絶対パスのまま書くとみられ、片方に決め打つと
   フィルタが何にもマッチせず「除外したつもりで素通り」になる）。**帰属が 1 件も成立しなければ warn**、
   帰属できなかったクラス・`<class>` の外にある行は集計に残して可視化する。
2. **二重記載の排除**: coverlet は同じ行を `<methods>` 配下と class 直下の `<lines>` に二重に書く。集計は
   **行・分岐とも class 直下の `<lines>` を正**とする（`<methods>` 配下は内訳として数えない）。素朴な
   `<line>` カウントは**実際の 2 倍を報告する**——PR #464 のレビューが記録した混入量 266 行 / 230 行は、
   いずれも 2 倍が効いた値である（**266 と 230 の差そのものはスコープ差**＝全プロジェクト実行と
   `Platform.Bff.Tests` 単体実行の違いであり、二重記載とは別の要因。出典:
   [`docs/specs/20260803_issue-453_regression-test-foundation.md`](../../.ai-context/specs/20260803_issue-453_regression-test-foundation.md)
   の「既知の限界」節）。

除外した行数・除外前後の実測値・`filename` の解釈の内訳・除外したクラス名は、**CI ログと実行サマリへ
毎回出力**する（`COVERAGE_FLOOR_DEBUG=1` でレポート単位の内訳も出る）。あわせて `<coverage>` の
`lines-valid` / `lines-covered` / `branches-valid` / `branches-covered`（coverlet 自身の集計値）と本実装の
集計値を並べ、二重記載の扱いが実レポートで妥当かを毎回照合できるようにしている。
**分岐は定義差のため照合が反証力を持たない**ので、別の観測点として
**「全 `<line>`（`<methods>` 重複込み）」と「class 直下のみ」の比**（実測は厳密に 2.00）も毎回出す
——これが崩れれば分岐側の二重記載排除が壊れたことに気付ける（無音の失敗を塞ぐ）。

##### CI 実測（成立確認）

測定条件: CI run 30886437108（run_number **1144**）/ job `build-and-test` / commit `594117a` /
Release 構成 / レポート **14 件** / submodule populate 済み。**測定条件のない実測値は再現できない**ため、
本節の数値を引用する際は必ず条件も併記すること。

| 観測点 | 実測 |
| --- | --- |
| 帰属 | クラス **2036 件**（そのまま(相対) 645 / そのまま(絶対) 0 / `<sources>` 結合 1391 / **未帰属 0**） |
| 混入（除外した行） | **6 クラス / 133 行**（すべて被覆済み） / 分岐 50（被覆 41） |
| 除外したクラス | `AssumptionsBffEndpoints` / `MonitorBffEndpoints` / `RiskControlsBffEndpoints` と各 `<ProxyAsync>d__2` |
| 除外前 → 除外後 | `line 34.46%（9447/27413）` → **`line 34.14%（9314/27280）`** / `branch 17.62%（1577/8948）` → **`17.26%（1536/8898）`** |
| coverlet 値との照合（行） | `lines-valid 27413`・`lines-covered 9447` と**完全一致**（＝ class 直下を正とする前提の裏づけ） |
| coverlet 値との照合（分岐） | `branches-valid 9356` に対し本実装 8948 で**一致しない**（下記「分岐の定義差」） |

旧計数方式で記録していた 2 値はいずれも二重記載の 2 倍で説明がつく——**266 = 133 × 2**（全プロジェクト実行）、
**230 = 115 × 2**（`Platform.Bff.Tests` 単体実行）。115 行は本 PR のレビューによる独立実測で、測定条件は
**.NET SDK 10.0.302 / `Platform.Bff.Tests` 単体実行 / commit `594117a` 時点 / ビルド構成はレビューコメントに
記載が無いため断定しない**。したがって **230 行と CI の全体集計（133 行）はスコープが異なり直接比較できない**
（旧値としては説明がついている）。

> **分岐の定義差（既知事項・異常ではない）**: 本実装が数える「分岐」は `<line>` の `condition-coverage` の
> 分母・分子の合算であり、coverlet の `branches-valid` とは**定義が異なる**（後者の算出経路は
> 一次出典未検証）。**行の乖離だけが「class 直下を正とする」前提の反証になる**ため、診断出力は
> 行を「**乖離・要調査**」、分岐を「差 n（定義差・期待される乖離）」と書き分ける。
> なお床 17 は `condition-coverage` 合算方式での実測に基づく——**被覆数を据え置いたまま分母だけ coverlet
> 基準へ置き換える試算**では除外前 `1577 ÷ 9356 = 16.86%`、床が判定に使う除外後の対でも
> `1536 ÷ (9356 − 50) = 16.51%` で、いずれも床 17 を下回る（定義を変えれば分子も変わるため、これは
> 「coverlet 定義での実際の分岐率」ではなく分母差の影響を測る試算である）。よって
> **分岐の定義変更は床の置き直しとセットでしか行えない**
> （カバレッジ床集計の実装 ADR の決定 4 の［2026-08-04 追記］）。

> **注**: 上記 2 により集計の**絶対数**（`covered/lines`）の意味が変わった。旧値は新方式の**厳密に 2 倍**
> だった——`18894 = 9447 × 2` / `54826 = 27413 × 2` / `3154 = 1577 × 2` / `17896 = 8948 × 2`。
> 全項が 2 倍で揃うことは、二重記載が一律に効いていたこと（＝ class 直下を正とする扱い）の強い裏づけである。
> 比率はほぼ不変だが、PR #464 の実測値（`18894/54826`）と #468 以降の表示は直接比較できない。

### 共通する設計原則: ratchet

上記のうち写像検査・カバレッジ床・ライブラリ標準・契約の後方互換はいずれも **ratchet**（床は下げられるが
上げっぱなしにできない）で設計している。これは impl-handoff-kit の段階ポリシー設計
（[`scripts/README.md`](../../scripts/README.md) の `check-permission-denials.js` 節と、計画側で記録された
前段の失敗モードおよび段階ポリシーの導入）が
示した「**成果物は正しいのに赤**」の常態化——拒否の赤を無視する学習を生み、検査の目的を逆から壊す
——を避けるためである（キットの同期規約そのものは
impl-handoff-kit を足場の単一情報源とし固有デルタを 4 種に限定する、という実装 ADR が定める）。既知の残件を
明示（allowlist / baseline / floor）したうえで、**新規の悪化だけを止める**。あわせて「残件が消えたのに
明示が残っている」ことも fail にする。契約の後方互換（[#465](https://github.com/endazon/microservices-platform/issues/465)）だけは
「既知の残件」ではなく「**意図した破壊的変更の承認**」を明示の対象にするが、3 判定（新規は fail /
明示済みは通す / 対応が消えたのに残っていれば fail）は同じである。これが無いと残件表が減らないまま形骸化する。

### 床の置き方（実測からの切り下げ）

カバレッジ床は**実測値を整数へ切り下げて**置く。実測そのままを床にすると、計測ゆらぎ（統合テストの
skip、被覆済みの死コード削除など）だけで「成果物は正しいのに赤」になる。切り上げは初回から fail する
ため行わない。フロントの [`src/vitest.config.ts`](../../src/vitest.config.ts) が実測 lines≈83% に対して
整数の床 78 を置いているのと同じ作法である。

バックエンドの初期値は #453 の CI 実行（`8bfe639`）で得た **line 34.46%（18894/54826） /
branch 17.62%（3154/17896）**（レポート 14 件 = MSP のテストプロジェクト全件）を切り下げた
`line 34` / `branch 17`。**この実測は [#468](https://github.com/endazon/microservices-platform/issues/468)
以前のもの**であり、AST の混入を含み、かつ行を二重に数えていた（上記「合成点テスト経由の混入」）。

**現行の根拠は #468 後の CI 実測**（run_number **1144** / commit `594117a` / Release / レポート 14 件 /
submodule populate 済み）である——**line 34.14%（9314/27280） / branch 17.26%（1536/8898）**。整数への
切り下げは `line 34` / `branch 17` で**従前と同値**のため、[`src/coverage-floor.json`](../../src/coverage-floor.json)
の値は据え置き、根拠のみ差し替えた（同ファイルの `$comment` に測定条件つきで記録。値の正は同ファイル）。

> ~~**余裕は薄い**: line は **+0.14pt**、branch は **+0.26pt** しかない。~~ 上記の薄さが実害になった
> ——[PR #568](https://github.com/endazon/microservices-platform/pull/568) は EF マイグレーションを
> 1 本追加しただけで床を割った。対処は [#571](https://github.com/endazon/microservices-platform/issues/571) /
> 生成コード（EF の Migrations / ModelSnapshot）を集計から落として床を置き直した（下記）。

##### 生成コードの除外と床の置き直し

**`Migrations/` 配下と `*ModelSnapshot.cs` は集計から落とす。** 人が書いていないコードの被覆率が床判定を
動かす状態（マイグレーションを 1 本足すだけで割れる）を塞ぐためである。判定は `<class filename>` を
カバレッジ床集計の実装 ADR の決定 2 が定める多段解釈で解決した経路に対して行い、除外量は毎回診断へ出す。

これに伴い**床を置き直した: `line 34` → `line 33`。`branch` は `17` のまま据え置く。**

- **引き下げ（退行）ではなく、測定基準の変更に伴う置き直しである**（同 ADR の決定 7 が #468 で行ったのと
  同じ性質の作業）。**旧定義の 34 と新定義の 33 は分母・分子が違うため直接比較できない。**
- **下がるのは、生成コードが平均より厚く被覆されているためである。** 統合テストが起動時 `MigrateAsync()`
  を通ると migration の `Up()` と Designer の `BuildTargetModel()`、`ModelSnapshot` の `BuildModel()` が
  実行される（`Down()` は実行されない）。#571 のローカル実測（Postgres / RabbitMQ を実際に起動して
  統合テストを 35/39 通した測定）で **生成コード 2310 行のうち 933 行が被覆**＝ 40.4% となり、全体（約 34%）を
  上回った。分子・分母の両方から同じものを抜いた結果として比率は下がる。
- **branch を据え置くのは生成コードの分岐が 0 だからである**（除外前後で分岐率は同値）。分岐の定義は
  変えていないため、同 ADR の決定 4 の追記が課した「定義変更は床の置き直しとセット」には該当しない。
- **床 33 は CI ログを直接読んだ実測値ではなく、CI が通ることで検証される下限である。** 導出は
  `(9314 − 933) / (27280 − 2310) = 33.56%`（上限側 `(9314 − 969) / (27280 − 2310) = 33.42%`）を整数へ
  切り下げたもの。基準の `9314/27280` は上記 run_number 1144 の実測、`2310` と `933〜969` は #571 の
  ローカル実測である（測定条件は 仕様書: EF 生成コードをカバレッジ集計から除外し、床を置き直す を参照）。

> **［2026-08-15 追記 / #574］上の 3 項は #571 時点の記録である。**
> その後の実装 ADR が **source generator の出力**（`obj/` 配下）も集計から落とし、床を `line 33` → `39` /
> `branch 17` → `27` へ置き直した。**「branch を据え置く」の根拠（生成コードの分岐が 0）は
> EF の生成コードにしか当たらない**——source generator の出力は**分岐 3970**を持つ。
> **床 39 / 27 は導出値ではなく実測値である**（develop `1d7edce` / SDK `10.0.400` / Release /
> レポート **14 件** / 統合テスト **43/43 成功**。`line 39.92%（9486/23762）` /
> `branch 28.01%（1663/5938）`）。**`branch` に切り下げの `28` を採らなかったのは、余裕が `0.01pt` しか
> なく被覆分岐 1 本の喪失で割れるためである**（source generator 出力の除外を定めた実装 ADR の決定 3）。

> バックエンド床の方式・値の置き方・AST 除外・fail-open の決定と根拠は
> バックエンドのカバレッジ床（単一情報源・実測からの切り下げ・ratchet）を定めた実装 ADR を正とする
> （生成コードの除外と床の置き直しは、EF の Migrations / ModelSnapshot を落とす実装 ADR と
> source generator の出力を落とす実装 ADR がそれぞれ定める。フロントはラチェット型のカバレッジゲートである）。

## テスト種別と責務

| 種別 | 置き場所 | 使うもの | 責務 |
| --- | --- | --- | --- |
| 単体（バックエンド） | `Services/<Name>/tests/<Name>.Tests/Unit`（**テストは 1 プロジェクト**。下記） | **xUnit v2**（バックエンド標準ライブラリの決定では v3。切替は独立 issue——[`src/Directory.Packages.props`](../../src/Directory.Packages.props) の `xunit.runner.visualstudio` が v2 用の 2.8.2 固定のため）＋ AwesomeAssertions ＋ NSubstitute | ドメイン規則・ハンドラの分岐 |
| 統合（バックエンド） | `Services/<Name>/tests/<Name>.Tests/Integration`（同上。ユニット横断の統合は `src/<unit>/backend/Tests/<Unit>.IntegrationTests`） | Testcontainers（PostgreSQL / RabbitMQ / Redis / Qdrant）＋ Respawn ＋ `Mvc.Testing` | 実依存を伴う往復・イベント連鎖 |
| 単体（フロント） | 実装と同居（`*.test.tsx`） | Vitest（jsdom）＋ Testing Library | 画面要素・状態遷移 |
| E2E | `src/*/frontend/e2e` | Playwright | 主要導線（**統合スタックでの拡充は後続 issue**） |
| 契約 | `scripts/contract-schema-baseline.json`（スナップショット） | [`check-contract-schema.js`](../../scripts/check-contract-schema.js)（C# ソース構文解析。外部依存ゼロ Node） | `Shared.Contracts` のイベント/API スキーマの後方互換（[#465](https://github.com/endazon/microservices-platform/issues/465) / 契約スキーマの抽出方式（C# ソース構文解析）と後方互換ゲート） |
| 性能（NFR） | [`NFR-01_performance-load-test.md`](NFR-01_performance-load-test.md) | — | 検索 p95 1.5s / RAG 初回 5s / 取り込み 1 万件・時（[#196](https://github.com/endazon/microservices-platform/issues/196)） |

### サービスのテストは 1 プロジェクト（Unit / Integration はフォルダで分ける）

計画 12_backend-application-stack（計画リポ）
§規範性・粒度・置き場（利用者裁定 2026-08-04）が **`Tests` は 1 プロジェクト**と定めている。
種別ごとに `.csproj` を割らない（ビルド時間と参照管理のコストが増えるため）。したがって 1 つの
`.csproj` が単体側（NSubstitute）と統合側（`Mvc.Testing` / Testcontainers / Respawn）の
`PackageReference` を**和集合**で持つ。実装の現況は `<Name>.Api.Tests` / `<Name>.Worker.Tests` であり、
`.csproj` の実名はホスト種別に合わせてよい。**ユニット横断の統合テスト**
（`src/knowledge/backend/Tests/Knowledge.IntegrationTests`）はサービス単位の `Tests` とは別の層であり、
この規則の対象外である。雛形は `templates/unit-template/backend/Services/SampleService/tests/SampleService.Tests`
がこの形を示す。

### xUnit のバージョンは v2 のまま書く（v3 へ先走らない）

バックエンド標準ライブラリの決定では xUnit v3 だが、**CPM（[`src/Directory.Packages.props`](../../src/Directory.Packages.props)）の
`xunit.runner.visualstudio` は v2 用の 2.8.2 に固定**されている。#455（PR #463）で入る
`check-backend-libraries.js` の `xunitRunnerMismatch` 検査は「`xunit.v3` を参照しているのに runner が 2.x」を
**CI で fail** させるため、各ドメイン issue が v3 で新規テストを書くと赤くなる。v3 への切替は runner の
更新を伴う独立 issue とし、本基盤の期間中は **v2（`xunit` 2.9.3）で書く**。

## 本基盤の未整備部分（後続 issue へ切り出し）

#453 のスコープのうち、以下は独立した設計判断を伴うため別 issue とする
（全面再実装の進行方式〔子 issue 単位のブランチ / PR と develop 直接統合〕の規約 4「大きくなる場合は
PR ではなく issue を分割する」）。

| 項目 | 切り出す理由 |
| --- | --- |
| ~~契約テスト基盤（`Shared.Contracts` のスキーマ後方互換）~~ | [#465](https://github.com/endazon/microservices-platform/issues/465) として切り出し、**実装済み**（上の「ゲート一覧」を参照）。抽出方式は C# ソース構文解析を採り、コンポーザビリティ標準の段階適用を定めた実装 ADR の決定 1 のうち「CI 契約テスト」だけを繰延解除した（共通エンベロープの繰延は継続） |
| E2E スモークセット（Istio・Keycloak・BFF の統合スタック） | 実行環境の CI 上での起こし方が主題であり #442（エッジ・実行基盤）と密結合する |
| NFR 性能試験の枠組み | [#196](https://github.com/endazon/microservices-platform/issues/196) が担当。再実装後の受け入れゲートとして接続するのは各サービス完成後 |
| ~~CPM バージョン直書き禁止の機械検査~~ | [#467](https://github.com/endazon/microservices-platform/issues/467) として切り出し、**実装済み**（上の「ゲート一覧」を参照） |

## 各ドメイン issue が守ること

1. **実装する FR/UC/SC のテスト仕様書 `docs/tests/<ID>_<概要>.md` を作成する**（`/new-spec test <ID>`）。
   写像検査は仕様書のある ID を突合の起点にするため、**仕様書が無い ID は順方向の検査対象にならない**。
   仕様書を作らずにテストだけ書くと「実装先行」として **fail** する（下の allowlist の項を参照）。
2. 実装する FR/UC/SC の**受け入れ基準をテストへ写像**し、テストの直前コメントに起点 ID を書く。
3. カバレッジ床を下回らない。テストを増やしたら**床を引き上げる**（`src/coverage-floor.json` /
   `src/vitest.config.ts`）。
4. バックエンド標準ライブラリの決定で不採用となったライブラリを増やさない。移行したら `scripts/backend-library-baseline.json` から
   自プロジェクトを削除する。
5. 後回しにする場合は `scripts/test-traceability-allowlist.json` へ**理由とともに**追加し、解消した PR で
   削除する。未写像（仕様書はあるがテストが無い）は `pending`、実装先行（テストはあるが仕様書が無い）は
   `specMissing` に書く。
6. **テスト仕様書を「全面改訂」するときは、既存の節を落としていないか確かめる**
   （[#510](https://github.com/endazon/microservices-platform/issues/510) の再発防止）。
   #503 は文書管理・データソース管理・変換ジョブ・AI 分析の各画面をフロントエンドの構造で置き換え、**バックエンド試験の節を落とした**
   ——テストは消えていないのに記載だけが消え、レビューでも当時の CI でも捕まらなかった。
   **本項は注意書きではなく、上記「記載の被覆」ゲートの説明である**——記載を落とすと
   `check-test-spec-coverage.js` がクラス名とパスを挙げて fail する。記載を増やしたら
   `node scripts/check-test-spec-coverage.js --update` で床を上げ、差分を PR に載せること。
   **ただし fail するのは、当該クラス名が仕様書ファイルから**（節の中だけでなく改訂ノート・
   §対象・§実行 も含めて）**消えたときである。表と見出しだけを落として他所に名前が残っていれば
   緑になる**（被覆 ratchet の実装 ADR §限界 2 の追記で実測）。
   **検査に頼り切らず、全面改訂のときは落とした節を自分で読み直すこと。**

> **未着手の FR/UC/SC が仕様書を持たないのは正当**であり、fail にはしない。計画レンジ
> （[`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)「起点 ID の種別」節）にあって
> 仕様書が無い ID は **warn** として実行サマリに列挙されるだけである（[#472](https://github.com/endazon/microservices-platform/issues/472)）。
> 着手した issue が 1 番を守ることで、この warn が 1 件ずつ減っていく。
