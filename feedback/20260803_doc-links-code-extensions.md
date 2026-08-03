---
title: check-doc-links の検査対象拡張子にコードファイルが無く、仕様書からコードへの live link が一切検査されない
type: plan-feedback
status: open
category: その他
related_ids: [NFR, IADR-0115]
source_repo: endazon/microservices-platform
source_ref: issue #470 / PR #477（`fix/NFR-doc-links-code-extensions`）/ docs/specs/20260803_issue-470_doc-links-code-extensions.md
author: Claude
created: 2026-08-03
---

# フィードバック: `check-doc-links` がコードファイルへのリンクを検査しない

## 種別

その他（`impl-handoff-kit` の `repo-template` が配布する検査器の不足）。計画書（要求・UC・画面）の
記述に対する誤り指摘ではなく、**キットが配布する成果物**（`repo-template/scripts/check-doc-links.js`）
に対するフィードバックである。

## 起点となる計画書

- 機能要求（FR）/ ユースケース（UC）/ 画面（SC）: なし（開発基盤・NFR: 保守性）
- 関連 ADR: 本リポジトリの
  [`docs/adr/IADR-0115_impl-handoff-kit-as-single-source.md`](../docs/adr/IADR-0115_impl-handoff-kit-as-single-source.md)
  （キットを足場の単一情報源とする同期規約。`check-doc-links.js` は**分類 A＝キットとバイト一致**の
  ファイルであり、実装リポで足したものは**暫定デルタ**として扱い、キット反映後の同期で撤去して
  バイト一致へ戻す）
- 計画書リンク:
  - `tools/impl-handoff-kit/repo-template/scripts/check-doc-links.js`
  - `tools/impl-handoff-kit/repo-template/.github/workflows/ci.yml`（`doc-links` ジョブ）

## 現状（As-Is）

キットが配布する `check-doc-links.js` の検査対象拡張子は、導入以来 仕様書・図・スキーマだけである。

```js
// 参照として実在検査を行う拡張子（仕様書・図・スキーマ等）
const LINK_EXT = /\.(md|ya?ml|json|puml|mmd|png|jpe?g|svg|drawio)$/i;
```

そのため**仕様書からコードへの live link は 1 件も検査されない**。破損していても検査器は素通しし、
`OK: N 件` と報告する。

本リポジトリで実害が出た。`docs/tests/TEST_STRATEGY.md` が当時未マージだった
`check-backend-libraries.js` へ live link しており、当該ファイルが存在しないブランチ上で参照が
破損していたにもかかわらず、検査器は「OK: 384 件」を返した。**検査器を整備する PR が、検査器の穴で
自分の参照切れを見逃した**形である。

あわせて、キットの検査器群のうち `check-doc-links.js` だけが `--self-test` を持たない。
`check-test-traceability.js` 等は自己試験を備えており、`ci.yml` でも自己試験ステップが先に走る。
検査対象が静かに狭いという本件の failure mode は、まさに自己試験が塞ぐ型である。

## 問題点 / あるべき姿（To-Be）

1. **コードは改名・移動が最も頻繁に起きる対象であり、仕様書からの参照は最も壊れやすい。**
   にもかかわらず壊れても CI は緑のままで、劣化が「気付けない」方向に効く。検査器を導入した
   目的（リンク切れの再発防止）が、対象の穴によって最も重要な領域で成立していない。
2. **「検査しているつもりで何も見ていない」ことが報告文からは判らない。** 件数は増え続けるため
   一見健全に見える。これは本リポジトリで繰り返し塞いできた失敗モード（成果物が正しく見えるのに
   実質未検査）と同型である。
3. **自己試験の非対称。** キットの他の検査器に在る `--self-test` が `check-doc-links.js` に無く、
   検査ロジック自体の正例／負例が固定されていない。対象拡張子を広げる変更は、まさに
   「正例（実在 → OK）と負例（不在 → 検出）を対で足す」規約が要る種類の変更である。
4. あるべき姿: キット配布時点でコードファイルが検査対象に含まれ、`--self-test` を備え、
   `ci.yml` の `doc-links` ジョブが他の検査器と同じ作法（自己試験 → 本走査）で走ること。

## 実装で判明した経緯

- 検出: 本リポジトリ issue #470（`docs/tests/TEST_STRATEGY.md` の壊れた前方参照を検査器が素通しした）。
- 作業: [`docs/specs/20260803_issue-470_doc-links-code-extensions.md`](../docs/specs/20260803_issue-470_doc-links-code-extensions.md)。
  PR #477（`fix/NFR-doc-links-code-extensions`）。
- 本リポジトリでは**暫定デルタ**として次の 3 点を先行適用した。
  1. [`scripts/check-doc-links.js`](../scripts/check-doc-links.js) の `LINK_EXT` へ
     `js|mjs|cjs|ts|tsx|cs|csproj|props|targets|slnx|sh` を追加（既存の 1 本の alternation の形は維持）。
  2. 同ファイルへ `--self-test` を新設（`LINK_EXT` の正例／対象外・`isBrokenRef` の正例／負例・
     `collectBroken` の 3 経路）。作法は `check-test-traceability.js` の `selfTest()` に合わせた。
  3. [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) の `doc-links` ジョブへ
     自己試験ステップを追加（本走査の前段）。
- `check-doc-links.js` は `develop` 時点で**キット原本とバイト一致（分類 A）**であることを実測済みで
  あり、上記 1・2 は IADR-0115 の「分類 A のファイルへ一時的にデルタを持つ場合は、コメントで
  環流先の issue を必ず参照し、是正後に撤去してバイト一致へ戻す」に該当する。
  ソースコメントと `ci.yml` のステップコメントに環流先 planning#167 を明記した。
- 待たずに先行適用した理由: 本リポジトリは仕様書からコードへの live link を多用しており
  （全走査で新たに検査対象となった live link は 34 件）、キット反映を待つ間ずっと未検査の状態が
  続くため。全走査で新たに検出された真の破損は 1 件で、同 PR で是正した。

## 提案（計画への反映案）

反映先候補: **`impl-handoff-kit` の修正**（要求更新・新 ADR ではない）

1. **`repo-template/scripts/check-doc-links.js` の `LINK_EXT` へコードファイルの拡張子を追加する。**
   キットはスタック非依存であるため、スクリプト（`js` / `mjs` / `cjs` / `sh`）・
   フロント（`ts` / `tsx`）・.NET（`cs` / `csproj` / `props` / `targets` / `slnx`）を既定で含めるのが素直である
   （いずれもキット自身が配布する `scripts/` と、キットが想定する技術スタック別ルールで実際に参照される）。
   `txt` のような汎用拡張子は加えない（誤検知の芽を作らない）。
2. **`--self-test` を新設する。** 他の検査器（`check-test-traceability.js` 等）と同じ作法で、
   `LINK_EXT` の正例／対象外、`isBrokenRef` の正例／負例、`collectBroken` の 3 経路
   （フロントマター / Markdown リンク / インラインコード）を固定する。
   「対象拡張子を広げるたびに正例と負例を対で足す」規約を自己試験のコメントに明記する。
3. **`repo-template/.github/workflows/ci.yml` の `doc-links` ジョブへ自己試験ステップを追加する。**
   他の検査器のジョブと作法を揃える（自己試験 → 本走査）。
4. 受け入れ時は**陽性対照**を取る（実在しないコードファイルへの相対リンクを含む Markdown を
   一時的に置いて exit 1 を確認し、除去して合格を確認する）。本件は「検査しているつもりで
   何も見ていない」型の欠落であり、緑を見るだけでは反映の成否が判らない。

## 影響範囲

- 影響先: キットを利用する全実装リポジトリの `doc-links` ジョブ。提案 1 は検査対象の**拡大**であり、
  **既存リポジトリでコードへの破損リンクがあれば CI が赤くなる**。これは意図した締め付けだが、
  受け入れ時は各リポジトリで全走査してから配布するか、段階導入（まず warn）も選択肢である。
- 未マージ成果物への前方参照を live link で書いていると落ちるようになる。DoD の
  「前方参照はバッククォート表記」の作法で回避でき、むしろその作法の根拠が強まる。
- 本リポジトリ側: 暫定デルタを保持し、キット反映後の同期で**撤去してバイト一致へ戻す**（IADR-0115 の運用）。
- 関連: planning#163（読み取り専用ツールの非対称。同じくキット配布物の不足を環流したもの）。
  本件はキットの**検査器**側で同型の「静かな不足」が見つかった例である。

## 計画側への起票（2026-08-03）

計画リポジトリへ [planning#167](https://github.com/endazon/project-planning/issues/167)
「impl-handoff-kit: check-doc-links の検査対象拡張子にコードファイルを追加し `--self-test` を新設する
（MSP からの環流）」として起票済み（上記の提案 1〜4 をそのまま記載）。反映されたら本リポジトリの
暫定デルタ（`scripts/check-doc-links.js` と `.github/workflows/ci.yml`）を撤去し、キットとバイト一致へ戻す。
