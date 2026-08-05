---
title: テスト戦略（退行防止テスト基盤）
type: test-spec
status: in-progress
related_ids:
  - NFR
  - IADR-0034
  - IADR-0115
  - IADR-0116
author: Claude
created: 2026-08-03
updated: 2026-08-03
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
---

# テスト戦略 — 再実装の退行防止基盤

> リポジトリ単位の横断ドキュメント。個別の FR/SC のテスト仕様書は同ディレクトリの
> `FR-xx_*.md` / `SC-xx_*.md` に置く。作業仕様書:
> [20260803_issue-453_regression-test-foundation.md](../specs/20260803_issue-453_regression-test-foundation.md)

## なぜ要るか

全面再実装（#454）では**既存実装を破棄し得る**。コードが入れ替わるため、退行の検知手段をコードでは
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
| **記載の被覆（[#510](https://github.com/endazon/microservices-platform/issues/510)）** | **`docs/tests/` の仕様書ファイル × `src/**/*Tests.cs` のクラス**（AST を除く）の対 | [`check-test-spec-coverage.js`](../../scripts/check-test-spec-coverage.js) | [`test-spec-coverage-baseline.json`](../../scripts/test-spec-coverage-baseline.json) の床にある対が消えた → **fail**（節の消失。**他の仕様書に同じクラスの記載が残っていても落ちる**——落ちるのは節であり、節は仕様書に属するため）。床にある対のクラスが実在しない → **fail**。記載された対が床に無い → **fail**（`--update` で上げる）。どの仕様書にも載らず床にも無いクラス → warn。走査 0 件・床が読めない → **fail**（[IADR-0130](../adr/IADR-0130_test-spec-coverage-ratchet.md)） |
| **バックエンド カバレッジ床** | `src/platform/backend/**` ・ `src/knowledge/backend/**`（**AST は対象外**。レポートのファイルパスに加え、**行を `<class filename>` でユニットへ帰属させて**合成点経由の混入も落とす——後述「合成点テスト経由の混入」・[#468](https://github.com/endazon/microservices-platform/issues/468) / [IADR-0123](../adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md)） | [`check-coverage-floor.js`](../../scripts/check-coverage-floor.js) ＋ `ci.yml` | [`src/coverage-floor.json`](../../src/coverage-floor.json) の床（現在 `line 34` / `branch 17`）未満 → **fail**（[IADR-0118](../adr/IADR-0118_backend-coverage-floor.md)） |
| **フロント カバレッジ ratchet** | `src/*/frontend/**` | [`frontend-tests.yml`](../../.github/workflows/frontend-tests.yml) | [`src/vitest.config.ts`](../../src/vitest.config.ts) の `thresholds` 未満 → **fail**（[IADR-0034](../adr/IADR-0034_frontend-coverage-gate.md)） |
| **ユニット依存規則** | `.csproj` の `ProjectReference` ・Foundation→Composable | [`check-unit-dependencies.js`](../../scripts/check-unit-dependencies.js) | 違反 → **fail** |
| **BFF 境界** | BFF の downstream | [`check-bff-downstreams.js`](../../scripts/check-bff-downstreams.js) | 違反 → **fail** |
| **ライブラリ標準（ADR-0030）** | `.csproj` ・`.props` / `.targets` の `PackageReference`（`PackageVersion` は対象外）/ `using` ・Domain 層の依存 | [`check-backend-libraries.js`](../../scripts/check-backend-libraries.js) | 新規混入・baseline 減らし忘れ → **fail**（#455 / [#471](https://github.com/endazon/microservices-platform/issues/471)） |
| **CPM バージョン直書き禁止** | `src/`（AST を除く）と `templates/` の `.csproj` の `PackageReference`（`.props` / `.targets` は正当な版記述があるため対象外） | [`check-cpm-versions.js`](../../scripts/check-cpm-versions.js) | `Version` 属性 / `<Version>` 子要素 → **fail**（着手時点の違反 0 件を実測したため ratchet 無しで最初から fail）。`VersionOverride` は**許可**し使用箇所を warn ＋実行サマリへ（[#467](https://github.com/endazon/microservices-platform/issues/467)） |
| **契約の後方互換（`Shared.Contracts`）** | `src/<unit>/backend/Shared/*.Contracts`（AST を除く）の public 型・メンバー・enum 値・`const` 値・属性 | [`check-contract-schema.js`](../../scripts/check-contract-schema.js) | 削除・型変更・必須化・位置引数の並べ替え・enum/`const` 値の変更・属性の変更・既定値の無いメンバーの追加 → **破壊的・fail**。非破壊の追加でも [`contract-schema-baseline.json`](../../scripts/contract-schema-baseline.json) と差分があれば **fail**（`--update` で更新し差分を PR の diff に載せる）。破壊的変更は [`contract-breaking-allowlist.json`](../../scripts/contract-breaking-allowlist.json) の承認エントリで通す（[#465](https://github.com/endazon/microservices-platform/issues/465) / [IADR-0122](../adr/IADR-0122_contract-schema-source-and-compat-gate.md)） |

※ `scripts/check-backend-libraries.js` と `scripts/backend-library-baseline.json` は **#455（PR #463）で導入済み**。
未マージ成果物への前方参照は live link ではなくバッククォート表記で書く
（[`docs/DEFINITION_OF_DONE.md`](../DEFINITION_OF_DONE.md) の同項と同じ作法）。
なお [#470](https://github.com/endazon/microservices-platform/issues/470) で
[`check-doc-links.js`](../../scripts/check-doc-links.js) の検査対象拡張子に `.js` などコードファイルを
追加したため、**コードへの live link の破損は機械で検出される**（前方参照を live link で書くと CI が落ちる）。

### 検査対象ユニットの切り分け

写像検査・カバレッジ床・ライブラリ標準はいずれも **`ai-stock-trading` を対象外**とする。AST は独自の
計画リポジトリ・ADR・ID 体系を持つ**別プロジェクト**（submodule）であり、本リポジトリの計画 ID や
ADR-0030 の標準を適用するのは誤りである（`.claude/rules/traceability.md`「複数プロジェクトを跨ぐ場合」）。

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

[#468](https://github.com/endazon/microservices-platform/issues/468) /
[IADR-0123](../adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md) で
[`check-coverage-floor.js`](../../scripts/check-coverage-floor.js) を class 単位走査へ作り替え、次の 2 点の
機構を導入した。

1. **行の帰属**: 各 `<class filename>` を `src/<unit>/` へ帰属させ、集計対象外ユニット（[IADR-0120](../adr/IADR-0120_excluded-units-from-gitmodules.md)
   の単一情報源から導出）の行を集計から落とす。`filename` は相対・絶対・`<sources>` との結合の順に
   多段で解釈する（coverlet は base path で始まらないファイルを絶対パスのまま書くとみられ、片方に決め打つと
   フィルタが何にもマッチせず「除外したつもりで素通り」になる）。**帰属が 1 件も成立しなければ warn**、
   帰属できなかったクラス・`<class>` の外にある行は集計に残して可視化する。
2. **二重記載の排除**: coverlet は同じ行を `<methods>` 配下と class 直下の `<lines>` に二重に書く。集計は
   **行・分岐とも class 直下の `<lines>` を正**とする（`<methods>` 配下は内訳として数えない）。素朴な
   `<line>` カウントは**実際の 2 倍を報告する**——PR #464 のレビューが記録した混入量 266 行 / 230 行は、
   いずれも 2 倍が効いた値である（**266 と 230 の差そのものはスコープ差**＝全プロジェクト実行と
   `Platform.Bff.Tests` 単体実行の違いであり、二重記載とは別の要因。出典:
   [`docs/specs/20260803_issue-453_regression-test-foundation.md`](../specs/20260803_issue-453_regression-test-foundation.md)
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
> （[IADR-0123](../adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md) 決定 4 の［2026-08-04 追記］）。

> **注**: 上記 2 により集計の**絶対数**（`covered/lines`）の意味が変わった。旧値は新方式の**厳密に 2 倍**
> だった——`18894 = 9447 × 2` / `54826 = 27413 × 2` / `3154 = 1577 × 2` / `17896 = 8948 × 2`。
> 全項が 2 倍で揃うことは、二重記載が一律に効いていたこと（＝ class 直下を正とする扱い）の強い裏づけである。
> 比率はほぼ不変だが、PR #464 の実測値（`18894/54826`）と #468 以降の表示は直接比較できない。

### 共通する設計原則: ratchet

上記のうち写像検査・カバレッジ床・ライブラリ標準・契約の後方互換はいずれも **ratchet**（床は下げられるが
上げっぱなしにできない）で設計している。これは impl-handoff-kit の段階ポリシー設計
（[`scripts/README.md`](../../scripts/README.md) の `check-permission-denials.js` 節、planning#146・planning#160
（前段の失敗モード）／planning#161・planning#162（段階ポリシーの導入））が
示した「**成果物は正しいのに赤**」の常態化——拒否の赤を無視する学習を生み、検査の目的を逆から壊す
——を避けるためである（キットの同期規約そのものは
[IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)）。既知の残件を
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

> **余裕は薄い**: line は **+0.14pt**、branch は **+0.26pt** しかない。テスト 1 件の skip や被覆済みコードの
> 削除で床を割りうる幅である。ratchet で床を引き上げる際は、この薄さを踏まえて「成果物は正しいのに赤」に
> ならない幅を確認してから上げること。

> バックエンド床の方式・値の置き方・AST 除外・fail-open の決定と根拠は
> [IADR-0118](../adr/IADR-0118_backend-coverage-floor.md) を正とする（フロントは
> [IADR-0034](../adr/IADR-0034_frontend-coverage-gate.md)）。

## テスト種別と責務

| 種別 | 置き場所 | 使うもの | 責務 |
| --- | --- | --- | --- |
| 単体（バックエンド） | `Services/<Name>/tests/<Name>.UnitTests` | **xUnit v2**（ADR-0030 の標準は v3。切替は独立 issue——[`src/Directory.Packages.props`](../../src/Directory.Packages.props) の `xunit.runner.visualstudio` が v2 用の 2.8.2 固定のため）＋ AwesomeAssertions ＋ NSubstitute（ADR-0030） | ドメイン規則・ハンドラの分岐 |
| 統合（バックエンド） | `Services/<Name>/tests/<Name>.IntegrationTests` | Testcontainers（PostgreSQL / RabbitMQ / Redis / Qdrant）＋ Respawn ＋ `Mvc.Testing` | 実依存を伴う往復・イベント連鎖 |
| 単体（フロント） | 実装と同居（`*.test.tsx`） | Vitest（jsdom）＋ Testing Library | 画面要素・状態遷移 |
| E2E | `src/*/frontend/e2e` | Playwright | 主要導線（**統合スタックでの拡充は後続 issue**） |
| 契約 | `scripts/contract-schema-baseline.json`（スナップショット） | [`check-contract-schema.js`](../../scripts/check-contract-schema.js)（C# ソース構文解析。外部依存ゼロ Node） | `Shared.Contracts` のイベント/API スキーマの後方互換（[#465](https://github.com/endazon/microservices-platform/issues/465) / [IADR-0122](../adr/IADR-0122_contract-schema-source-and-compat-gate.md)） |
| 性能（NFR） | [`NFR-01_performance-load-test.md`](NFR-01_performance-load-test.md) | — | 検索 p95 1.5s / RAG 初回 5s / 取り込み 1 万件・時（[#196](https://github.com/endazon/microservices-platform/issues/196)） |

### xUnit のバージョンは v2 のまま書く（v3 へ先走らない）

ADR-0030 の標準は xUnit v3 だが、**CPM（[`src/Directory.Packages.props`](../../src/Directory.Packages.props)）の
`xunit.runner.visualstudio` は v2 用の 2.8.2 に固定**されている。#455（PR #463）で入る
`check-backend-libraries.js` の `xunitRunnerMismatch` 検査は「`xunit.v3` を参照しているのに runner が 2.x」を
**CI で fail** させるため、各ドメイン issue が v3 で新規テストを書くと赤くなる。v3 への切替は runner の
更新を伴う独立 issue とし、本基盤の期間中は **v2（`xunit` 2.9.3）で書く**。

## 本基盤の未整備部分（後続 issue へ切り出し）

#453 のスコープのうち、以下は独立した設計判断を伴うため別 issue とする
（[IADR-0116](../adr/IADR-0116_reimplementation-branching-and-pr-policy.md) 規約 4: 大きくなる場合は
PR ではなく issue を分割する）。

| 項目 | 切り出す理由 |
| --- | --- |
| ~~契約テスト基盤（`Shared.Contracts` のスキーマ後方互換）~~ | [#465](https://github.com/endazon/microservices-platform/issues/465) として切り出し、**実装済み**（上の「ゲート一覧」を参照）。抽出方式は C# ソース構文解析を採り、[IADR-0049](../adr/IADR-0049_composability-standards-phased-adoption.md) 決定 1 のうち「CI 契約テスト」だけを繰延解除した（[IADR-0122](../adr/IADR-0122_contract-schema-source-and-compat-gate.md)。共通エンベロープの繰延は継続） |
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
4. ADR-0030 の不採用ライブラリを増やさない。移行したら `scripts/backend-library-baseline.json` から
   自プロジェクトを削除する。
5. 後回しにする場合は `scripts/test-traceability-allowlist.json` へ**理由とともに**追加し、解消した PR で
   削除する。未写像（仕様書はあるがテストが無い）は `pending`、実装先行（テストはあるが仕様書が無い）は
   `specMissing` に書く。
6. **テスト仕様書を「全面改訂」するときは、既存の節を落としていないか確かめる**
   （[#510](https://github.com/endazon/microservices-platform/issues/510) の再発防止）。
   #503 は SC-05〜08 をフロントエンドの構造で置き換え、**バックエンド試験の節を落とした**
   ——テストは消えていないのに記載だけが消え、レビューでも当時の CI でも捕まらなかった。
   **本項は注意書きではなく、上記「記載の被覆」ゲートの説明である**——記載を落とすと
   `check-test-spec-coverage.js` がクラス名とパスを挙げて fail する。記載を増やしたら
   `node scripts/check-test-spec-coverage.js --update` で床を上げ、差分を PR に載せること。
   **ただし fail するのは、当該クラス名が仕様書ファイルから**（節の中だけでなく改訂ノート・
   §対象・§実行 も含めて）**消えたときである。表と見出しだけを落として他所に名前が残っていれば
   緑になる**（[IADR-0130](../adr/IADR-0130_test-spec-coverage-ratchet.md) §限界 2 の追記で実測）。
   **検査に頼り切らず、全面改訂のときは落とした節を自分で読み直すこと。**

> **未着手の FR/UC/SC が仕様書を持たないのは正当**であり、fail にはしない。計画レンジ
> （[`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)「起点 ID の種別」節）にあって
> 仕様書が無い ID は **warn** として実行サマリに列挙されるだけである（[#472](https://github.com/endazon/microservices-platform/issues/472)）。
> 着手した issue が 1 番を守ることで、この warn が 1 件ずつ減っていく。
