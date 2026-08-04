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

#### 合成点テスト経由の混入（[#468](https://github.com/endazon/microservices-platform/issues/468) で対処。実レポートでの成立確認は CI 実走）

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
   **class 直下の `<lines>` を正**とする（`<methods>` 配下は内訳として数えない）。素朴な `<line>` カウントが
   計測条件で振れていた——PR #464 のレビューは Release 構成で 2 度計測し、全プロジェクト実行時と
   `Platform.Bff.Tests` 単体実行時で結果が割れた——原因はこれである。

除外した行数・除外前後の実測値・`filename` の解釈の内訳・除外したクラス名は、**CI ログと実行サマリへ
毎回出力**する（`COVERAGE_FLOOR_DEBUG=1` でレポート単位の内訳も出る）。あわせて `<coverage>` の
`lines-valid` / `lines-covered`（coverlet 自身の集計値）と本実装の集計値を並べ、二重記載の扱いが実レポートで
妥当かを毎回照合できるようにしている。

> **状態**: 上記は**機構の導入まで**である。実レポート（coverlet の実出力）に対して帰属と除外が
> 成立していることの確認・混入行数の確定・除去後の実測に基づく床の置き直しは、**CI 初回実走の診断出力**
> （帰属の内訳・除外クラス一覧・coverlet 値との照合）**を読んでから**行う。#468 の作業環境には .NET SDK が
> 無く、実レポートを取得できなかったためである（[IADR-0123](../adr/IADR-0123_cobertura-class-attribution-and-line-dedup.md) 決定 4・5、
> [作業仕様書](../specs/20260804_issue-468_coverage-ast-exclusion.md) の受け入れ基準）。

> **注**: 上記 2 により集計の**絶対数**（`covered/lines`）の意味が変わった（分母・分子とも約半分になる）。
> 比率はほぼ不変だが、PR #464 の実測値（`18894/54826`）と #468 以降の表示は直接比較できない。
> 床（比率）の置き直しは CI 実測に基づき [`src/coverage-floor.json`](../../src/coverage-floor.json) で行う。

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
床は比率であり判定の意味は保たれるが、**除去後の実測に基づく置き直し**は #468 の CI 実測を見てから
[`src/coverage-floor.json`](../../src/coverage-floor.json) で行う。

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

> **未着手の FR/UC/SC が仕様書を持たないのは正当**であり、fail にはしない。計画レンジ
> （[`.claude/rules/traceability.md`](../../.claude/rules/traceability.md)「起点 ID の種別」節）にあって
> 仕様書が無い ID は **warn** として実行サマリに列挙されるだけである（[#472](https://github.com/endazon/microservices-platform/issues/472)）。
> 着手した issue が 1 番を守ることで、この warn が 1 件ずつ減っていく。
