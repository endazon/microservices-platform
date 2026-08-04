---
title: 作業仕様書 — 検査器 3 つの EXCLUDED_UNITS を .gitmodules 由来の共通ヘルパへ寄せる
type: spec
status: done
related_ids: [NFR, IADR-0056, IADR-0115, IADR-0118, IADR-0120]
author: Claude
created: 2026-08-03
updated: 2026-08-04
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md"
related_specs:
  - "../adr/IADR-0120_excluded-units-from-gitmodules.md"
  - ./20260803_issue-453_regression-test-foundation.md
  - ./20260803_issue-455_backend-application-standard.md
  - ./20260803_issue-474_backend-floor-iadr-and-0116-followup.md
  - ./20260803_issue-470_doc-links-code-extensions.md
  - "../adr/IADR-0056_repo-unit-structure-platform-knowledge.md"
  - "../adr/IADR-0115_impl-handoff-kit-as-single-source.md"
  - "../adr/IADR-0118_backend-coverage-floor.md"
---

# 作業仕様書: 検査器 3 つの EXCLUDED_UNITS を .gitmodules 由来の共通ヘルパへ寄せる

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR: 保守性 — 検査器の除外判定を単一情報源から導出し、ユニット追加時の
  取りこぼしを構造的に防ぐ）
- ユースケース（UC）/ 画面（SC）: なし
- 関連 ADR:
  - [`IADR-0056`](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md) 決定 6
    （追加の可変機能ユニットは **submodule でリンクする**）。本作業が塞ぐ劣化の直接の原因である。
  - [`IADR-0118`](../adr/IADR-0118_backend-coverage-floor.md) 決定 4
    （`ai-stock-trading` は床の集計対象外＝`EXCLUDED_UNITS`）。
  - [`IADR-0115`](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)（キット同期規約）。
    対象 3 検査器の位置づけ確認に用いる（下記「IADR-0115 の位置づけ確認」）。
  - [`IADR-0120`](../adr/IADR-0120_excluded-units-from-gitmodules.md)（**本作業で起票**）。
    本書が定める導出規則・fail-closed・環境変数の口を作らない方針、および
    「submodule だが本プロジェクト所属のユニット」が現れた場合の**再判断の条件**を恒久記録する。
    導出規則は将来のユニット追加すべてに自動適用される恒久ポリシーであり、作業仕様書だけでは
    記録先として弱いため（`CLAUDE.md`「重要な実装判断は実装 ADR に必ず残す」）。
- 本リポジトリの起点: [#473](https://github.com/endazon/microservices-platform/issues/473)
- 先行作業: [#455](https://github.com/endazon/microservices-platform/issues/455)（`check-backend-libraries.js`）/
  [#453](https://github.com/endazon/microservices-platform/issues/453)（`check-test-traceability.js` /
  `check-coverage-floor.js`）/ [#139](https://github.com/endazon/microservices-platform/issues/139)
  （`check-doc-links.js` の `.gitmodules` 由来への一般化。本作業が流儀を合わせる先例）

## 目的・背景

3 つの検査器が、除外ユニットの集合をそれぞれ独立に**ハードコード**している。

| 検査器 | 現状の宣言 |
| --- | --- |
| [`scripts/check-backend-libraries.js`](../../scripts/check-backend-libraries.js) | `const EXCLUDED_UNITS = new Set(['ai-stock-trading']);` |
| [`scripts/check-test-traceability.js`](../../scripts/check-test-traceability.js) | 同上 |
| [`scripts/check-coverage-floor.js`](../../scripts/check-coverage-floor.js) | 同上 |

現時点で 3 つの値は一致しており、**今の広さは妥当**である（`ai-stock-trading` は独自の計画リポジトリ・
独自 ADR を持つ別プロジェクトであり、本リポジトリの標準を適用するのは誤りで、submodule のため
是正もできない）。問題は値ではなく**導出経路**にある。

[`IADR-0056`](../adr/IADR-0056_repo-unit-structure-platform-knowledge.md) 決定 6 により、追加の
可変機能ユニットは submodule として `src/<unit>` にリンクされる。次に submodule ユニットが 1 つ増えた
瞬間、3 箇所が**同時に狭すぎ**になる。そのとき起きるのは「検査器が落ちる」ではなく、
**他プロジェクトのコードを自リポジトリの規約で検査して赤が出る**か、あるいは
**他プロジェクトのカバレッジを自リポジトリの床に合算して床判定が濁る**（IADR-0118 決定 4 の懸念そのもの）
である。いずれも原因が「除外リストの更新漏れ」だと気付くまでに時間がかかる種類の壊れ方であり、
3 箇所に分散しているため 1 箇所だけ直して残り 2 箇所が残る形の直し漏れも起こる。

同じ問題は [`check-doc-links.js`](../../scripts/check-doc-links.js) で既に一度起きており、
`planning/` 固定だった判定を `.gitmodules` 由来の一般則へ拡張して解決している（#139）。
本作業はその先例を、除外ユニットの導出にも適用する。

## 対象範囲

- 含むもの:
  1. [`scripts/lib/excluded-units.js`](../../scripts/lib/excluded-units.js) の新設。
     `.gitmodules` から `src/<unit>` の submodule を読み、除外ユニット集合を導出する共通ヘルパ。
     `--self-test`（フィクスチャによる追随検査を含む）を持つ。
  2. 上記 3 検査器を、ヘルパからの導出に置き換える（`EXCLUDED_UNITS` / `isExcludedPath` の
     **公開名と意味は保つ**。既存の `module.exports` と自己試験はそのまま通ること）。
  3. [`scripts/scripts.repo.test.js`](../../scripts/scripts.repo.test.js) にヘルパの回帰テストを追加
     （ハードコードへの逆戻り検出・3 検査器の集合一致・`--self-test` の exit 0）。
  4. [`scripts/README.md`](../../scripts/README.md) の表へヘルパの行を 1 行追加。
  5. [`docs/adr/IADR-0120`](../adr/IADR-0120_excluded-units-from-gitmodules.md) の起票（導出規則・
     fail-closed・再判断の条件の恒久記録）と [`docs/adr/README.md`](../adr/README.md) の索引更新。
- 含まないもの:
  - **`check-doc-links.js` への適用**。同ファイルは IADR-0115 の分類 A（キット原本と同一系）であり、
    かつ既に `.gitmodules` 由来の判定を自前で持つ。ここへヘルパを持ち込むと、キットに存在しない
    リポジトリ固有の依存を分類 A ファイルへ埋め込むことになる（同期のたびに手当てが要る）。
  - 他スクリプト（`check-unit-dependencies.js` 等）への適用拡大。issue #473 の対象は 3 検査器である。
  - 除外ユニットの**広さの変更**。導出経路のみを変え、現時点の結果は `{ai-stock-trading}` のまま
    変わらないことを受け入れ基準に置く。
  - `.github/workflows/` の変更（下記「CI での実行経路」）。

## IADR-0115 の位置づけ確認（実測）

キット雛形 `planning/tools/impl-handoff-kit/repo-template/scripts/` を実際に列挙した結果:

```
README.md  action-versions.json  apply-profile.sh  changelog-overrides.json
check-action-versions.js  check-ai-workflow-config.js  check-commit-messages.js
check-doc-links.js  check-permission-denials.js  commit-allowlist.json
gen-changelog.js  gen-openapi-skeleton.js  lib/  scripts.test.js  setup.sh
validate-pipeline-config.js
```

| ファイル | キット原本 | 位置づけ | 本作業での扱い |
| --- | --- | --- | --- |
| `check-backend-libraries.js` | 無し | **固有デルタ種 3**（本リポにしか存在しない成果物・スクリプト） | 自由に改変してよい |
| `check-test-traceability.js` | 無し | **固有デルタ種 3** | 自由に改変してよい |
| `check-coverage-floor.js` | 無し | **固有デルタ種 3** | 自由に改変してよい |
| `lib/excluded-units.js`（新設） | 無し | **固有デルタ種 3** | 新規追加。キットへの環流はしない |
| `lib/ci-annotate.js` | 有り・**バイト一致** | 分類 A | 触らない |
| `scripts.test.js` | 有り・**バイト一致** | 分類 A | 触らない（固有テストは `scripts.repo.test.js` へ） |
| `check-doc-links.js` | 有り・差分あり（#470 の暫定デルタ） | 分類 A + 暫定デルタ | **触らない**（先例として読むのみ） |
| `scripts/README.md` | 有り・差分あり（リポ固有スクリプトの行を既に追記済み） | 分類 B（固有デルタ種 3 を含む） | 同じ作法で 1 行追記 |

**分類（A / B / C）と固有デルタ種（1〜4）は別軸である**（IADR-0115 決定 1・決定 2）。分類 B は
「キット本文を土台に固有部分を再適用する」——すなわち**キットに原本があるファイル**の扱いであり、
キットに存在しないファイルへ当てるラベルではない。対象 3 検査器と新設ヘルパはキットに原本を持たない
**固有デルタ種 3（本リポにしか存在しない成果物・スクリプト。`check-unit-dependencies.js` と同じ位置づけ）**
であり、分類 A/B のデルタ規約（環流先 issue の明記・`feedback/` への記録）の対象ではない。
`src/<unit>` というユニット第一構成は IADR-0056 の本リポジトリ固有の決定でキットの前提ではないため、
ヘルパもキットへは環流しない。

## 方針

### 導出規則

`.gitmodules` の `path = ...` を全件読み、**`src/` 直下ちょうど 1 階層**（`src/<unit>`）のものだけを
ユニットとみなし、その `<unit>` を除外集合とする。

- リポジトリ直下の `planning` は `src/` 配下でないため、規則上ユニットにならない（issue の注意点）。
  「`planning` を名指しで弾く」実装にはしない。名指しは新しいハードコードであり、本作業が
  消そうとしているものと同じ性質を持つ。**`src/` 直下 1 階層という位置の規則**で落とす。
- `src/knowledge/frontend/vendor/x` のような**深い階層**の submodule はユニットではない。これを
  `knowledge` へ丸めると、自リポジトリの主たる成果物が丸ごと検査対象外になる（過剰な除外）。
  1 階層ちょうどに限定し、深い submodule は除外に寄与しない。
- `.gitmodules` の解析は先例 [`check-doc-links.js`](../../scripts/check-doc-links.js) の
  `submodulePaths()` と同じ正規表現（`^\s*path\s*=\s*(.+?)\s*$` の全行走査 + 区切りの正規化）に揃える。
  INI パーサを持ち込まない（外部依存ゼロの原則）。

### 注入方式

テスト可能性のため、ヘルパは 2 段で注入できる。

```js
excludedUnits()                         // 既定: リポジトリルートの .gitmodules
excludedUnits({ root: fixtureDir })     // ルート差し替え（フィクスチャ）
excludedUnits({ gitmodules: text })     // .gitmodules の内容を直接注入（純関数として試験）
```

環境変数ではなく引数注入を採る。`check-doc-links.js` の `DOC_LINKS_ROOT` は「CI の checkout 状態を
再現する」ための実行時スイッチだが、本ヘルパの差し替えは**テスト内でだけ必要**であり、実行時に
除外ユニットを外から変えられる口は作らない方がよい（検査の広さを環境変数で狭められると、
検査を黙って無効化する経路になる）。

### `.gitmodules` が読めない場合の挙動（設計判断）

**fail-closed: 例外を投げて停止する。** 既定値へのフォールバックも、空集合の返却も行わない。

- 空集合を返す（fail-open）と、除外されるはずの別プロジェクトを自リポジトリの規約で検査してしまう。
  issue #473 が名指しで避けろと言っている状態そのものであり、しかも**検査は緑にならず赤で出る**ため、
  「他プロジェクトの違反」を自リポジトリの PR が背負うことになる。
- 既定値（`['ai-stock-trading']`）へフォールバックすると、それは 3 箇所のハードコードを 1 箇所へ
  移しただけで、単一情報源が `.gitmodules` でなくなる。**次の submodule 追加で狭すぎになる**という
  本 issue の劣化が、フォールバック経路の中に残り続ける。
- `.gitmodules` は追跡ファイルであり、正常なチェックアウトには必ず存在する。読めないのは
  「走査対象が本リポジトリではない」異常であって、推測で続行してよい状況ではない。
  例外メッセージには読もうとしたパスと、フォールバックしない理由を書く。

なお `.gitmodules` が**存在して submodule が 1 つも無い**場合は除外 0 件が正しい結果であり、
異常ではない（例外は投げない）。この状態への退行——たとえば解析が壊れて空集合になる——は、
3 検査器の自己試験が `isExcludedPath('src/ai-stock-trading/...') === true` を**実リポジトリの
`.gitmodules` に対して**確かめることで検出される（自己試験を維持する理由の 1 つ）。

### CI での実行経路

`.github/workflows/ci.yml` は変更しない。ヘルパは以下の既存ジョブで毎回実行される。

- `backend-libraries` / `test-traceability` の各ジョブ、および `build-and-test` ジョブ末尾の
  カバレッジ床ステップが、`--self-test` と本走査の双方でヘルパの導出結果を使う
  （導出が壊れれば自己試験が落ちる）。
- `scripts-tests` ジョブ（`node scripts/scripts.test.js`、`REQUIRE_REPO_TESTS=1`）が
  `scripts.repo.test.js` 経由でヘルパの `--self-test` を子プロセス実行し、exit 0 を確かめる。

新規ジョブを足さないのは、追加の CI 面積なしに同じ強制力が得られるためである
（`.github/workflows/` は GitHub App 権限で編集不可であり、必要のない変更は持ち込まない）。

## 実装（変更点）

| ファイル | 変更 |
| --- | --- |
| [`scripts/lib/excluded-units.js`](../../scripts/lib/excluded-units.js) | 新設。公開 API は `submodulePaths` / `unitOfSubmodulePath` / `excludedUnitsFromText` / `excludedUnits` / `makeIsExcludedPath`（＋`toPosix` / `REPO_ROOT`）。自己試験は `module.exports` に載せず **CLI の `--self-test`** からのみ実行する（他の検査器と同じ作法） |
| [`scripts/check-backend-libraries.js`](../../scripts/check-backend-libraries.js) | `EXCLUDED_UNITS` / `isExcludedPath` をヘルパ由来へ。既存の自己試験 4 件は維持 |
| [`scripts/check-test-traceability.js`](../../scripts/check-test-traceability.js) | 同上（自己試験 1 件を維持） |
| [`scripts/check-coverage-floor.js`](../../scripts/check-coverage-floor.js) | 同上（自己試験 2 件を維持）。レポート 0 件時の warn に出す除外ユニット名も導出値になる |
| [`scripts/scripts.repo.test.js`](../../scripts/scripts.repo.test.js) | ヘルパの回帰テストを追加 |
| [`scripts/README.md`](../../scripts/README.md) | 表に `lib/excluded-units.js` の行を追加 |

## 受け入れ基準

- [x] 3 検査器の `EXCLUDED_UNITS` が `scripts/lib/excluded-units.js` からの導出になり、
      **3 検査器に** `new Set(['ai-stock-trading'])` 相当のハードコードが残っていない
      （ヘルパの自己試験・回帰テストは同じ文字列をフィクスチャとして正当に使うため、検査範囲は
      3 検査器のソースに限る。逆戻り検出はシングル / ダブル両方のクォート形を対象にする）。
- [x] `.gitmodules` に仮の submodule（例: `src/foo-unit`）を足したフィクスチャで、除外集合が
      自動的に追随する（ヘルパの `--self-test` で固定）。
- [x] リポジトリ直下の `planning` は除外ユニットにならない。`src/` 配下の深い submodule も
      ユニットへ丸められない（ヘルパの `--self-test` で固定）。
- [x] `.gitmodules` が読めない場合に例外で停止し、空集合を返さない（ヘルパの `--self-test` で固定）。
- [x] 実リポジトリ走査の結果が変更前後で**不変**である。3 検査器の検出件数・対象件数・
      除外ユニット名が一致すること（`ai-stock-trading` を populate した状態で機械比較する）。
- [x] `node scripts/check-backend-libraries.js` / `--self-test` が exit 0。
- [x] `node scripts/check-test-traceability.js` / `--self-test` が exit 0。
- [x] `node scripts/check-coverage-floor.js` / `--self-test` が exit 0
      （レポート 0 件の環境では集計 skip の warn を出して exit 0）。
- [x] `node scripts/lib/excluded-units.js --self-test` が exit 0。
- [x] `node scripts/scripts.test.js` が緑で、テスト件数が 191 件から減っていない。
      `REQUIRE_REPO_TESTS=1` でも緑。
- [x] `node scripts/check-doc-links.js` が exit 0（本仕様書と IADR-0120 の相対リンクを含む）。
- [x] `node scripts/check-commit-messages.js --base origin/develop` が緑。

## 検証（実測）

`origin/develop`（`3bee06c`）から作った worktree で実行した。submodule は実体を持たないため、
**`src/ai-stock-trading` と `planning` に実チェックアウトの内容を配置した状態**で走らせている
（未 populate のままでは除外の効き目が観測できないため）。

| コマンド | 結果 |
| --- | --- |
| `node scripts/lib/excluded-units.js --self-test` | 自己試験 **18 件 OK** / exit 0 |
| `node scripts/check-backend-libraries.js` | 新規混入 0 件・Domain 依存規律 OK（既知残件 **42 件 / 29 プロジェクト**は baseline 済み）/ exit 0 |
| `node scripts/check-backend-libraries.js --self-test` | 自己試験 **49 件 OK** / exit 0 |
| `node scripts/check-test-traceability.js` | 仕様書のある起点 ID **27 件中 27 件が写像済み**、計画レンジ 53 件中 26 件に仕様書あり（仕様書なし 27 件は warn、実装先行 7 件は allowlist 済み）/ exit 0 |
| `node scripts/check-test-traceability.js --self-test` | 自己試験 **34 件 OK** / exit 0 |
| `node scripts/check-coverage-floor.js` | レポート **0 件**（検出 0 / 除外 0、除外ユニット: `ai-stock-trading`）で集計 skip の warn / exit 0 |
| `node scripts/check-coverage-floor.js --self-test` | 自己試験 **14 件 OK** / exit 0 |
| `node scripts/scripts.test.js` | **197 tests passed** / exit 0（変更前 191 件 → +6 件） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **197 tests passed** / exit 0 |
| `node scripts/check-doc-links.js` | OK: **395 件**の Markdown に破損リンクなし / exit 0（変更前 393 件。増分は本仕様書と IADR-0120 の 2 件） |

### 逆戻り検出テストの実効性（クロス監査の指摘を受けた実測）

ハードコードへの逆戻りを検出する正規表現は、当初シングルクォート形のみを見ており
`new Set("ai-stock-trading")` 相当のダブルクォート形を素通りした（「逆戻りを検出するテスト」自体が
逆戻りを見逃す形）。`["']` へ拡大したうえで実効性を実測した——`check-coverage-floor.js` の宣言を
一時的に `new Set(["ai-stock-trading"])` へ戻すと `node scripts/scripts.test.js` が
`AssertionError: check-coverage-floor.js にハードコードが残っている` で **exit 1**。復元後は
再び 197 件緑で、`git status` はクリーンであることを確認した。

### 走査結果の不変（変更前後の機械比較）

変更前（`origin/develop` そのまま）と変更後で、3 検査器の**素実行の標準出力・標準エラー**が
バイト一致した（`check-coverage-floor` の warn 文中の除外ユニット名を含む）。あわせて
各モジュールを `require` して**走査結果そのもの**（`scanTree()` の検出プロジェクトと違反、
`collectSpecIds()` / `collectTestIds()` の ID 集合、`findReportsDetailed()` の全件 / 対象 / 除外、
および 3 者の `EXCLUDED_UNITS`）を JSON へ落として比較し、**219 行が完全一致**した。

カバレッジ床は本環境に Cobertura レポートが 1 件も無く、素実行だけでは除外経路を通らない。
そこで `src/ai-stock-trading` / `src/platform` / `src/knowledge` の 3 箇所に一時的な
`coverage.cobertura.xml` を置き、変更前の worktree（`origin/develop` の detached）と変更後で
比較した。双方とも **検出 3 件 / 対象 2 件 / 除外 1 件（AST）**、集計結果も
`line 50%（2/4）・OK: 床を下回っていません` で一致した（一時ファイルは検証後に削除済み）。

## 影響・リスク

- **除外の広さが変わるリスク**: 導出規則は「`.gitmodules` の `src/` 直下 1 階層」であり、現在の
  `.gitmodules`（`planning` と `src/ai-stock-trading`）からは `{ai-stock-trading}` のみが出る。
  変更前後の走査結果を機械比較して不変を確認する。
- **require 時に例外が飛ぶ**: 3 検査器はモジュール読み込み時にヘルパを呼ぶため、`.gitmodules` を
  読めない環境では `require` 段階で失敗する。これは意図した fail-closed であり、正常な
  チェックアウトでは起こらない。テストは一時ディレクトリを `root` に注入して検証するため影響しない。
- **将来 submodule ユニットが増えたとき**: 3 検査器は `.gitmodules` の更新に自動追随する。
  逆に「submodule だが検査対象に含めたいユニット」が現れた場合は本規則が広すぎになる。その場合は
  ヘルパに例外を持たせるのではなく、その時点で改めて判断し IADR に残す（現時点で存在しない
  ケースへの防御的実装はしない）。
