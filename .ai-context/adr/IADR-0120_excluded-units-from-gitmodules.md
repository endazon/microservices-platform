---
title: IADR-0120 検査対象外ユニットの単一情報源を .gitmodules（src/ 直下 1 階層の submodule）とし、読めなければ停止する
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0056, IADR-0115, IADR-0118]
author: Claude
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md
  - planning:projects/microservices-platform/07_adr/ADR-0030_backend-application-libraries.md
related_specs:
  - ../specs/20260803_issue-473_excluded-units-single-source.md
---

# IADR-0120: 検査対象外ユニットの単一情報源を .gitmodules（src/ 直下 1 階層の submodule）とし、読めなければ停止する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は計画側へ環流する（`/plan-feedback`）。

- 状態: Accepted
- 日付: 2026-08-04
- 決定者: Claude（実装）

## 起点・関連

- 関連する計画書 ID（FR/UC/SC/ADR）: NFR（保守性）／
  ADR-0030（計画リポ）
  （`check-backend-libraries.js` が強制する計画 ADR。適用範囲は MSP に限る）
- 関連する実装 ADR: [IADR-0056](IADR-0056_repo-unit-structure-platform-knowledge.md)（決定 6:
  追加の可変機能ユニットは **submodule でリンク**する。本決定が自動追随の対象とする事象）／
  [IADR-0118](IADR-0118_backend-coverage-floor.md)（決定 4: AST を床の集計対象外とする。本決定は
  その除外判定の**導出経路**を定める）／[IADR-0115](IADR-0115_impl-handoff-kit-as-single-source.md)
  （キット同期規約。対象スクリプトは**固有デルタ種 3**＝本リポにしか存在しないスクリプト）
- 関連する実装仕様書:
  [20260803_issue-473](../specs/20260803_issue-473_excluded-units-single-source.md)
- 関連 issue: #473（本決定の起点）／#453・#455（3 検査器の導入）／#139（`check-doc-links.js` の
  `planning/` 固定判定を `.gitmodules` 由来へ一般化した先例）／#209（ユニット第一構成）
- 起票の根拠: issue #473 の実装（3 検査器の除外判定を単一情報源へ寄せる）と、その**クロス監査の指摘**
  ——「`src/<unit>` の submodule = 検査対象外」は将来のユニット追加すべてに自動適用される**恒久ポリシー**
  であり、作業仕様書のみでは記録先として弱い（`CLAUDE.md`「重要な実装判断は実装 ADR に必ず残す」）。

## コンテキストと課題

`check-backend-libraries.js` / `check-test-traceability.js` / `check-coverage-floor.js` の 3 検査器は、
本リポジトリの規約を `src/` 配下へ機械強制する。いずれも **`ai-stock-trading`（AST）を対象から外す**
必要がある——AST は独自の計画リポジトリと ADR を持つ**別プロジェクト**（submodule）であり、
本リポジトリの標準を適用するのは誤りで、submodule のため本リポジトリからは是正もできない
（`.claude/rules/traceability.md`「複数プロジェクトを跨ぐ場合」）。

着手時点の実装は、3 ファイルが同じ集合を**独立にハードコード**していた。

```js
const EXCLUDED_UNITS = new Set(['ai-stock-trading']);  // 3 ファイルに同一の宣言
```

値は一致しており、現時点の広さも妥当である。問題は**導出経路が 3 本ある**ことにある。
[IADR-0056](IADR-0056_repo-unit-structure-platform-knowledge.md) 決定 6 により、追加の可変機能ユニットは
submodule として `src/<unit>` にリンクされる。次に submodule ユニットが 1 つ増えた瞬間、3 箇所が
**同時に狭すぎ**になる。そのとき起きるのは検査器の停止ではなく、

- 他プロジェクトのコードを自リポジトリの規約で検査した**赤**（自リポジトリの PR が他プロジェクトの
  違反を背負う。しかも submodule なので当該 PR では直せない）、または
- 他プロジェクトのカバレッジを自リポジトリの床へ**合算した濁り**（[IADR-0118](IADR-0118_backend-coverage-floor.md)
  決定 4 が名指しした劣化。退行を薄めて隠す／他プロジェクトの pin 更新だけで床判定が動く）

であり、いずれも「除外リストの更新漏れ」だと気付くまでに時間がかかる。3 箇所に分散しているため、
1 箇所だけ直して残り 2 箇所が残る形の直し漏れも起こる。同じ問題は `check-doc-links.js` が
`planning/` 固定判定で一度踏んでおり、`.gitmodules` 由来の一般則へ拡張して解決している（#139）。

決めるのは次の 3 点である。(1) 除外集合を何から導出するか、(2) 情報源を読めないときにどう振る舞うか、
(3) 将来「submodule だが本プロジェクト所属」のユニットが現れたらどうするか。

## 検討した選択肢

### 除外集合の導出元

| | A. `.gitmodules` の `src/` 直下 1 階層（採用） | B. 3 検査器のハードコード（現状） | C. 専用の設定ファイル（`excluded-units.json` 等） |
| --- | --- | --- | --- |
| ユニット追加への追随 | 自動（submodule 追加で追随） | 手動・3 箇所 | 手動・1 箇所 |
| 情報源の重複 | 無し（Git の既存事実） | 3 重 | 2 重（`.gitmodules` と設定の二重管理） |
| 先例 | 有り（#139 の `check-doc-links.js`） | — | 無し |
| ずれたときの気付きやすさ | ずれない | 気付けない（黙って狭すぎになる） | ずれる（更新忘れが起きる） |

### 情報源を読めないときの振る舞い

| | A. 例外で停止（fail-closed。採用） | B. 空集合を返す（fail-open） | C. 既定値へフォールバック |
| --- | --- | --- | --- |
| 誤検査 | 起きない（走査自体が始まらない） | **起きる**（別プロジェクトを自リポの規約で検査） | 起きない（当面は） |
| 単一情報源の維持 | 維持 | 維持 | **崩れる**（`.gitmodules` でない値が実効値になり得る） |
| 本 issue の劣化の再発 | 無し | 無し | **フォールバック経路の中に残る**（次の submodule 追加で狭すぎ） |
| 異常の可視性 | 高い（メッセージ付きで止まる） | 無音 | 無音 |

`.gitmodules` は追跡ファイルであり、正常なチェックアウトには必ず存在する。読めないのは
「走査対象が本リポジトリではない」異常であって、推測で続行してよい状況ではない。

### 差し替え手段（テスト可能性）

| | A. 引数注入（`root` / `gitmodules`。採用） | B. 環境変数（`EXCLUDED_UNITS` 等） |
| --- | --- | --- |
| テストからの差し替え | 可能 | 可能 |
| 実行時に検査を狭める余地 | **無い** | **有る**（CI の env 1 行で検査を黙って無効化できる） |
| 先例との整合 | `check-doc-links.js` の `unpopulatedSubmoduleOf(abs, root)` と同型 | `DOC_LINKS_ROOT` は「CI の checkout 状態の再現」用途で目的が異なる |

## 決定

**3 検査器（`check-backend-libraries.js` / `check-test-traceability.js` / `check-coverage-floor.js`）の
検査対象外ユニットは、`.gitmodules` から導出する単一情報源
[`scripts/lib/excluded-units.js`](../../scripts/lib/excluded-units.js) に一本化する。**

1. **導出規則**: `.gitmodules` の `path = ...` のうち、**`src/` 直下ちょうど 1 階層**（`src/<unit>`）の
   submodule を「本リポジトリの実体でないユニット」とみなし、その `<unit>` を除外集合とする。
   - リポジトリ直下の `planning` は `src/` 配下でないため、**規則上ユニットにならない**。
     名指しで弾く実装にはしない（名指しは本決定が消そうとしているハードコードと同じ性質を持つ）。
   - `src/<unit>/…/vendor/x` のような**深い階層**の submodule はユニットへ丸めない。丸めると
     自リポジトリの主たる成果物（`platform` / `knowledge`）が丸ごと検査対象外になる。
2. **fail-closed**: `.gitmodules` を読めない場合は**例外を投げて停止**する。空集合を返さず、既定値へも
   フォールバックしない。`.gitmodules` が存在して submodule が 0 件なのは正常であり（除外 0 件が
   正しい結果）、例外にはしない。
3. **実行時スイッチを作らない**: 差し替えは引数注入（`excludedUnits({ root })` /
   `excludedUnits({ gitmodules })`）のみとし、除外集合を外から与える環境変数の口は設けない。
4. **適用範囲は上記 3 検査器に限る**。`check-doc-links.js` は
   [IADR-0115](IADR-0115_impl-handoff-kit-as-single-source.md) の分類 A（キット原本と同一系）であり、
   かつ自前の `.gitmodules` 由来判定を既に持つため、本ヘルパを持ち込まない。他スクリプトへの拡大も
   行わない（必要が生じた時点で判断する）。
5. **退行の固定**: ヘルパの `--self-test`（仮 submodule を足したフィクスチャで追随すること・
   `planning` が混じらないこと・深い submodule を丸めないこと・読めなければ例外になること）と、
   [`scripts/scripts.repo.test.js`](../../scripts/scripts.repo.test.js) の回帰テスト
   （3 検査器の集合一致・ハードコードへの逆戻り検出）を維持する。

### 再判断の条件（将来「submodule だが本プロジェクト所属」のユニットが現れた場合）

[IADR-0056](IADR-0056_repo-unit-structure-platform-knowledge.md) 決定 6 は「追加の可変機能ユニットは
submodule でリンクする」と定めるだけで、**submodule ユニット＝別プロジェクト**とは定めていない。
すなわち「本リポジトリの成果物でありながら submodule として置かれるユニット」——たとえば本リポジトリの
計画 ID 体系・ADR に従う可変機能ユニットを、リリース単位の都合で別リポジトリに置く場合——は
**あり得る**。本決定の規則はその場合に**広すぎ**になり、当該ユニットが 3 検査器から静かに外れる。

この事象が起きたときの手順を決定として定める。

1. **検知**: 新しい submodule ユニットを `src/` へ追加する PR は、当該ユニットが本リポジトリの規約
   （ADR-0030 のライブラリ標準・`docs/tests/` の ID 体系・カバレッジ床）の**適用対象か**を
   作業仕様書に明記する。適用対象なら本決定と矛盾する。
2. **再判断**: 適用対象のユニットが現れた時点で、**本 IADR を改定する新 IADR を起票**し、除外規則へ
   例外を設ける（例: 除外は `.gitmodules` 由来を既定としつつ、`opt-in` で検査対象へ戻すユニットを
   明示する）。先例は [IADR-0117](IADR-0117_platform-shared-kernel-placement.md)（Accepted な
   IADR-0056 決定 3 の部分改定を、追記ではなく新 IADR で行った）。
3. **禁止事項**: その場しのぎで 3 検査器のいずれかにユニット名のハードコードを戻さない。
   本決定の目的（単一情報源）が崩れ、#473 の劣化がそのまま再発する。

「起こり得ないケースへの防御的実装」を避けるため、**例外の仕組みは今は作らない**。上の手順は
「その時が来たら何をするか」を固定するものであり、現時点のコードには入れない。

## 理由

- **導出元を Git の既存事実に置いたこと**が要点である。除外の広さは「submodule かどうか」という
  リポジトリの構造そのものから決まり、人が更新する台帳を持たない。台帳を持てば、更新漏れという
  同じ失敗が別の場所で再発する（選択肢 C の二重管理）。
- **`src/` 直下 1 階層**という位置の規則により、`planning` の除外が「名指し」ではなく構造で説明できる。
  #139 が `planning/` 固定判定を `.gitmodules` 由来へ一般化したのと同じ向きの一般化である。
- **fail-closed** を選んだのは、この検査器群の誤りが「赤くならない」形で現れるためである。除外 0 件で
  素通りさせると、他プロジェクトの違反を自リポジトリの PR が背負う。逆に停止は必ず気付かれる。
- **環境変数の口を作らない**のは、検査の広さを実行時に狭められる経路を残さないためである。
  `check-permission-denials.js` の段階ポリシー（`scripts/README.md`）が示すとおり、検査は
  「無視できる状態」を作った時点で目的を失う。

## 結果

- 良い影響:
  - submodule ユニットの追加（IADR-0056 決定 6）に 3 検査器が**自動追随**する。追加 PR で
    検査器を触る必要がない。
  - 除外の根拠が 1 ファイル（`scripts/lib/excluded-units.js`）に集約され、`.gitmodules` を見れば
    実効値が分かる。3 箇所を突き合わせる読み方が不要になる。
  - 導出が壊れて空集合へ退行した場合、3 検査器の自己試験（実リポジトリの `.gitmodules` に対する
    `isExcludedPath('src/ai-stock-trading/…') === true`）が落ちる。
- 悪い影響・トレードオフ:
  - 3 検査器が `require` 時に `.gitmodules` を読むため、**モジュール読み込み段階で失敗し得る**。
    意図した fail-closed だが、スタックトレースの読み口が 1 段深くなる。
  - 「submodule ＝ 別プロジェクト」という前提に依存する。前提が崩れる条件と手順は上記
    「再判断の条件」で固定した。
  - `.gitmodules` の書式解析を自前で持つ（外部依存ゼロの原則のため INI パーサを入れない）。
    書式の例外（`path` の引用符付き記法等）が現れたら解析側の対応が要る。
- フォローアップ:
  1. 新しい submodule ユニットを `src/` へ追加する PR では、当該ユニットが本リポジトリの規約の
     適用対象かを作業仕様書に明記する（上記「再判断の条件」1）。
  2. 3 検査器以外に除外判定を持つスクリプトを新設する場合は、独自のハードコードではなく本ヘルパを使う。

## 関連

- Supersedes: なし
- Superseded by: なし
- 先例: #139（`check-doc-links.js` の `planning/` 固定判定を `.gitmodules` 由来へ一般化）／
  [IADR-0117](IADR-0117_platform-shared-kernel-placement.md)（Accepted な IADR の部分改定を新 IADR で実施）
- 実装: [`scripts/lib/excluded-units.js`](../../scripts/lib/excluded-units.js)（`--self-test` 付き）／
  [`scripts/check-backend-libraries.js`](../../scripts/check-backend-libraries.js) /
  [`scripts/check-test-traceability.js`](../../scripts/check-test-traceability.js) /
  [`scripts/check-coverage-floor.js`](../../scripts/check-coverage-floor.js) /
  [`scripts/scripts.repo.test.js`](../../scripts/scripts.repo.test.js)
