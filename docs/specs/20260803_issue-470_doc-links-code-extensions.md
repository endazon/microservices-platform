---
title: 作業仕様書 — check-doc-links の検査対象にコードファイルの拡張子を加える
type: spec
status: done
related_ids: [NFR, IADR-0115]
author: Claude
created: 2026-08-03
updated: 2026-08-03
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md"
related_specs:
  - ./20260704_chore_ci-doc-links-check.md
  - ./20260803_issue-453_regression-test-foundation.md
  - "../adr/IADR-0115_impl-handoff-kit-as-single-source.md"
---

# 作業仕様書: check-doc-links の検査対象にコードファイルの拡張子を加える

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR: 保守性 — 仕様書とコードの参照整合を機械で担保する）
- ユースケース（UC）/ 画面（SC）: なし
- 関連 ADR: [`IADR-0115`](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)
  （impl-handoff-kit を足場の単一情報源とする同期規約）。新たな技術選定は伴わないが、
  対象の [`scripts/check-doc-links.js`](../../scripts/check-doc-links.js) は `develop` 時点で
  **キット原本とバイト一致の分類 A**（実測済み）であるため、本作業の変更は**暫定デルタ**として
  扱う——ソースコメントに環流先 issue を明記し、キット反映後の同期で撤去してバイト一致へ戻す。
- 先行作業: [`20260704_chore_ci-doc-links-check.md`](./20260704_chore_ci-doc-links-check.md)（検査器の導入）
- 本リポジトリの起点: [#470](https://github.com/endazon/microservices-platform/issues/470)
- 計画側への環流: [planning#167](https://github.com/endazon/project-planning/issues/167)
  （記録: [`feedback/20260803_doc-links-code-extensions.md`](../../feedback/20260803_doc-links-code-extensions.md)）

## 目的・背景

[`scripts/check-doc-links.js`](../../scripts/check-doc-links.js) の検査対象拡張子は、導入以来
仕様書・図・スキーマだけを見ていた。

```js
const LINK_EXT = /\.(md|ya?ml|json|puml|mmd|png|jpe?g|svg|drawio)$/i;
```

そのため**仕様書からコードへの live link は 1 件も検査されず**、破損していても検査器は素通しした。

実害が出た。[`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md) が当時未マージだった
`check-backend-libraries.js` へ live link しており、当該ファイルが存在しないブランチ上で参照が
破損していたにもかかわらず、`check-doc-links.js` は「OK: 384 件」を返した。
**検査器を作る PR が、検査器の穴で自分の参照切れを見逃した**形である。
当該リンク自体は是正済みで、本作業は検査器側の恒久対応にあたる。

放置した場合の劣化は「気付けない」方向に効く。コードは改名・移動が最も頻繁に起きる対象であり、
仕様書からの参照は最も壊れやすい一方、壊れても CI は緑のままだからである。

## 対象範囲

- 含むもの:
  1. [`scripts/check-doc-links.js`](../../scripts/check-doc-links.js): `LINK_EXT` に
     `js|mjs|cjs|ts|tsx|cs|csproj|props|targets|slnx|sh` を追加（既存の 1 本の alternation の形は保つ）。
  2. 同ファイル: `--self-test`（検査ロジックの自己試験）を追加し、`.js` の正例（実在 → OK）／
     負例（不在 → 検出）を対で固定する。作法は
     [`check-test-traceability.js`](../../scripts/check-test-traceability.js) の `selfTest()` に合わせる。
  3. [`scripts/scripts.repo.test.js`](../../scripts/scripts.repo.test.js): 回帰テストを追加
     （`LINK_EXT` の内容・`isBrokenRef` の正例／負例・`collectBroken` の 3 経路・`--self-test` の exit 0）。
  4. 拡張により新たに検出された既存の破損リンクの是正（下記「全走査の結果」）。
  5. [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) の `doc-links` ジョブへ
     自己試験ステップを追加（本走査の前段。他の検査器のジョブと作法を揃える）。
  6. 計画側への環流記録
     [`feedback/20260803_doc-links-code-extensions.md`](../../feedback/20260803_doc-links-code-extensions.md)
     の作成（IADR-0115 の「記録 1 件 ↔ 環流 1 件」規約に従い単独の記録とする）。
- 含まないもの:
  - 検査経路（フロントマター / Markdown リンク / インラインコード）の変更。
  - 未 populate な submodule のスキップ・`DOC_LINKS_ROOT`・アンカー無視といった既存挙動の変更。
  - `docs/` 以外への検査対象の拡大。

## 方針

- **拡張子の選定**: 本リポジトリの仕様書がコードを指すときに実際に使う拡張子に限る。
  スクリプト（`js`/`mjs`/`cjs`/`sh`）・フロント（`ts`/`tsx`）・バックエンド（`cs`/`csproj`/`props`/`targets`/`slnx`）。
  `txt` などの汎用拡張子は加えない（誤検知の芽を作らない）。
- **誤検知の下限**: 検査は `--dir`（既定 `docs`）配下の Markdown だけを走査し、相対リンクのみを解決する。
  `node_modules` や生成物を自ら走査することはなく、拡張子の追加でその性質は変わらない。
- **既存挙動の非破壊**: 変更は `LINK_EXT` の 1 行と `--self-test` の追加に閉じる。既存 176 件のテストが
  緑のまま件数が増えることを非破壊の基準とする。
- **自己試験を対で置く**: 「検査しているつもりで何も見ていない」状態が #470 の本質であるため、
  拡張子を広げるたびに正例と負例を対で足す規約を自己試験のコメントに明記する。
- **分類 A の暫定デルタとして扱う**（[IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)）:
  [`scripts/check-doc-links.js`](../../scripts/check-doc-links.js) は `develop` 時点でキット原本
  （`planning/tools/impl-handoff-kit/repo-template/scripts/check-doc-links.js`）と**バイト一致**である。
  本作業の変更（`LINK_EXT` 拡張・`--self-test` 新設）と `ci.yml` の自己試験ステップは、
  キット反映を待たずに先行適用する**暫定デルタ**であり、次の 3 点を守る。
  1. ソースコメントと `ci.yml` のステップコメントに**環流先 issue**
     [planning#167](https://github.com/endazon/project-planning/issues/167) を明記する。
  2. 環流記録を [`feedback/20260803_doc-links-code-extensions.md`](../../feedback/20260803_doc-links-code-extensions.md)
     に単独で残す（既存記録へ相乗りしない）。
  3. キット側へ反映されたら、次のキット同期でデルタを撤去しバイト一致へ戻す。
  先行適用の理由は、本リポジトリが仕様書からコードへの live link を多用しており（下記「全走査の結果」で
  34 件）、キット反映を待つ間ずっと未検査の状態が続くためである。

## 全走査の結果（拡張子追加によって新たに検出された破損）

`develop`（`ba7cd06`）の全 386 Markdown を、planning submodule を populate した状態でも走査した。
新たに検査対象となった live link は 34 件（`props` 11 / `js` 10 / `ts` 8 / `slnx` 4 / `csproj` 1）で、
破損は **1 件**だった。

| ファイル | 破損リンク | 判定 | 処置 |
| --- | --- | --- | --- |
| [`docs/specs/20260708_ci_frontend-test-coverage.md`](./20260708_ci_frontend-test-coverage.md) | `frontend/vite.config.ts` へ `../../` 起点で live link | 移動漏れ。FR-14 のユニット再構成でカバレッジ設定は [`src/vitest.config.ts`](../../src/vitest.config.ts) へ移った | 同 PR で修正。表示テキストは当時のパスのまま、リンク先を現在位置へ向ける（同ファイル項目 2 の `frontend/package.json` → `../../src/package.json` と同じ扱い）。移設の経緯を 1 行注記する |

正当な前方参照（未マージ PR の成果物への参照）は 0 件だった。
[`docs/tests/TEST_STRATEGY.md`](../tests/TEST_STRATEGY.md) の ※ 注記は「`check-doc-links.js` は `.js` を
検査対象に持たないため壊れた前方参照を機械では検出できない」と述べており、本変更で事実に反するため
併せて更新する（前方参照をバッククォート表記で書く作法自体は維持。むしろ live link にすると
CI が落ちるようになるため、作法の根拠が強まる）。

## 受け入れ基準

- [x] `LINK_EXT` に `js|mjs|cjs|ts|tsx|cs|csproj|props|targets|slnx|sh` が含まれ、既存の対象拡張子
      （`md`/`ya?ml`/`json`/`puml`/`mmd`/画像/`drawio`）を 1 つも落としていない。
- [x] 実在しない `.js` への相対リンクを含む Markdown があると `node scripts/check-doc-links.js` が exit 1 を返す
      （一時ファイルによる実地確認）。実在する `.js` リンクは OK のまま。
- [x] `node scripts/check-doc-links.js --self-test` が exit 0（`.js` の正例・負例を含む）。
- [x] `node scripts/check-doc-links.js`（`docs/` 全走査）が exit 0。planning を populate した状態でも
      新たな破損は 1 件のみで、それが是正済みであること。
- [x] `node scripts/scripts.test.js` が緑で、テスト件数が 176 件から増えている（減っていない）。
- [x] 既存挙動（未 populate submodule のスキップ件数報告・`--require-planning` の fail-loud・
      `DOC_LINKS_ROOT`・アンカー無視）が変わらない。
- [x] `node scripts/check-commit-messages.js` が緑。
- [x] IADR-0115 の分類 A 運用: `scripts/check-doc-links.js` と `.github/workflows/ci.yml` の
      暫定デルタに**環流先 issue**（[planning#167](https://github.com/endazon/project-planning/issues/167)）を
      参照するコメントがあり、環流記録
      [`feedback/20260803_doc-links-code-extensions.md`](../../feedback/20260803_doc-links-code-extensions.md)
      が単独の記録として存在する。
- [x] `node scripts/check-doc-links.js --dir feedback` が exit 0（環流記録の相対リンクが解決する）。

## 影響・リスク

- **誤検知**: 対象は `docs/` 配下の Markdown の相対リンクのみで、走査範囲は変わらない。全走査で
  検出された破損は 1 件であり、いずれも実ファイルの移動に起因する真の破損だった。
- **前方参照の書き味**: 未マージ成果物へ live link すると CI が落ちるようになる。これは意図した
  締め付けであり、DoD の「前方参照はバッククォート表記」の作法で回避する。
- **planning submodule**: 未 populate 時は従来どおりスキップされる。planning 側のコード参照は
  定期ジョブ `doc-links-planning.yml`（`--require-planning`）で検査される。
- **キットとの乖離**: 分類 A のファイルに暫定デルタを持つ間、`diff` によるバイト一致判定は当然に失敗する。
  次のキット同期で planning#167 の反映を確認し、デルタを撤去してバイト一致へ戻すまでが本作業の
  未完部分である（IADR-0115 の第 12・13 ラウンドと同じ 1 往復の運用）。
