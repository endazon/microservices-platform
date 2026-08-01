---
title: impl-handoff-kit の不足 9 件（submodule リンク判定の一般化・Actions 版数・自己不整合・CI 未結線ほか）
type: plan-feedback
status: open
category: その他
related_ids: [NFR, IADR-0115]
source_repo: microservices-platform
source_ref: docs/specs/20260801_impl-handoff-kit-sync.md
author: Claude
created: 2026-08-01
---

# フィードバック: impl-handoff-kit の不足 9 件（初回 6 件 ＋ 追加 3 件）

## 種別

その他（計画リポジトリの成果物 `tools/impl-handoff-kit` に対する改善提案）。
要求・ユースケース・画面の内容に関する指摘ではない。

計画リポジトリへ起票済み: [planning#96](https://github.com/endazon/project-planning/issues/96)
（`plan-feedback` ラベル。計画側 `/triage-feedback` の取り込み対象）。

**反映結果（2026-08-01）**: planning#98（`12cc9b8`）で **6 件すべてが反映された**（ai-stock-trading
からの planning#97 と併せて計 12 件）。本リポジトリは同 pin へ再同期済みで、1・6 の固有デルタ
（`check-doc-links.js` / `setup.sh` / `security.yml`）は**解消してキットと一致**した。
その後の再同期で追加 3 件（下記「残課題」7〜9）を検出し、いずれも起票済み。

## 起点となる計画書

- 機能要求（FR）: なし（NFR: 保守性・運用性）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: なし（実装側は [IADR-0115](../docs/adr/IADR-0115_impl-handoff-kit-as-single-source.md)）
- 計画書リンク: `planning/tools/impl-handoff-kit/`（`HOWTO.md` / `repo-template/`）

## 現状（計画書の記述 / As-Is）

`impl-handoff-kit` を `6a1cc9f`（planning#95 時点）まで取り込み、`repo-template` の全ファイルを
本リポジトリへ反映した（[IADR-0115](../docs/adr/IADR-0115_impl-handoff-kit-as-single-source.md)）。
その過程で、キット側に次の 6 点の不足・不整合を確認した。

## 問題点 / あるべき姿（To-Be）

### 1. `check-doc-links.js` の submodule 判定が `planning/` 固定（実装側が先行）

- **現状**: 未 populate の submodule 配下リンクを検査から外す処理が、リンク文字列に
  `planning/` を含むかどうかで判定している。
- **問題**: `planning` 以外の submodule（本リポジトリでは `src/ai-stock-trading`）配下への
  リンクが、トークン不要の PR CI（submodule 未取得）で**破損リンクとして誤検知**される。
- **あるべき姿**: `.gitmodules` の `path` 一覧を読み、解決済みパスがいずれかの submodule 配下に
  あり、かつそのディレクトリが空（未 populate）なら検査対象外にする。本リポジトリでは MSP #283 で
  この一般化を実装済み（`submodulePaths()` / `underUnpopulatedSubmodule()`）。
  そのままキットへ取り込める。

### 2. キットの GitHub Actions が古い版に固定されている

- **現状**: `setup-node@v6` / `setup-dotnet@v5` / `create-pull-request@v7` / `upload-artifact@v4`。
- **問題**: キット自体は Dependabot の対象外のため、実装リポジトリがキットと同期するたびに
  Actions の版数が**巻き戻る**。今回も同期後に 4 種を再バンプする必要があった
  （[IADR-0115](../docs/adr/IADR-0115_impl-handoff-kit-as-single-source.md) の固有デルタ 4）。
- **あるべき姿**: いずれか。(a) 計画リポジトリの `.github/dependabot.yml` に
  `package-ecosystem: github-actions` × `directory: /tools/impl-handoff-kit/repo-template/.github/workflows`
  を追加してキットも自動更新する。(b) それが難しければ、`HOWTO.md` に
  「同期後に Actions 版数は実装リポ側の新しい方を採る」と明記して、巻き戻しを規約で防ぐ。

### 3. `traceability-auditor.md` がキット自身の規約を満たしていない（自己不整合）

- **現状**: `repo-template/.claude/rules/traceability.md` は「監査は修飾付き ID を突合対象から
  **除外**する（`.claude/agents/traceability-auditor.md` に同じ規約を書くこと）」と指示しているが、
  キット同梱の `repo-template/.claude/agents/traceability-auditor.md` にその記述が無い。
- **問題**: キットから生成した実装リポジトリは、指示された規約が最初から抜けた状態で始まる。
  本リポジトリでは手作業で追記した（検査手順 3 の下位項目）。
- **あるべき姿**: キット側の `traceability-auditor.md` に、修飾付き ID を突合対象から除外する
  規約を最初から書いておく（本リポジトリの文面をそのまま流用できる）。

### 4. `docs/how-to/`（使い方・デプロイ手順ガイド）が仕様書の種別表に無い

- **現状**: `repo-template/docs/README.md` の種別表・`/new-spec` の種別に `how-to` が無い。
- **問題**: 「ローカル開発の起動手順」「デプロイ手順」「ユニット submodule の追加手順」といった
  **手順ガイド**は、どの仕様書種別にも当てはまらない。本リポジトリでは `docs/how-to/` を独自に
  設けている（`local-development.md` / `deployment.md` / `adding-a-unit-submodule.md`）が、
  キット由来ではないため他リポジトリと構成が揃わない。
- **あるべき姿**: 任意の種別として `how-to`（出力先 `docs/how-to/`）を種別表へ追加する。
  仕様書と違い起点 ID を持たないことがあるため、frontmatter の必須項目は緩めてよい。

### 5. `copilot-setup-steps.example.yml` の .NET が `ci.example.yml` と食い違う

- **現状**: `copilot-setup-steps.example.yml` は `dotnet-version: "8.0.x"`、
  `ci.example.yml` / `claude-coding.example.yml` / `claude-code-review.example.yml` は `"10.0.x"`。
- **問題**: Copilot coding agent の環境だけ SDK が古く、`.NET 10` を対象にしたプロジェクトの
  restore/build が Copilot 側でのみ失敗する。
- **あるべき姿**: キット内の既定 SDK 版を 1 か所に揃える（`ci.example.yml` に合わせて `10.0.x`）。
  合わせて `AI_SETUP.md` の「3 か所を揃える」注記に、Copilot の setup ステップも含めることを検討する。

### 6. ソリューションの「自動発見」既定が、雛形ソリューションを拾って失敗する

- **現状**: `repo-template/scripts/setup.sh` と `security.example.yml` の既定は
  `find . -maxdepth 4 \( -name '*.slnx' -o -name '*.sln' \)` で全ソリューションを自動発見して
  `dotnet restore` する。
- **問題**: リポジトリが**ビルド不可の雛形ソリューション**を持つ場合、それも拾って失敗する。
  本リポジトリの `templates/unit-template/backend/backend.slnx` は `src/` の外にあり
  共通 props（`src/Directory.Build.props`）を継承しないため、`dotnet restore` が
  `error : 無効なフレームワーク識別子 ''`（exit 1）で失敗することを実測で確認した。
  同じ理由で `codeql.example.yml` の `autobuild` も本リポジトリでは使えず、明示ビルドへ置き換えている。
- **あるべき姿**: 既定の自動発見から雛形・足場ディレクトリを除外する
  （例: `-not -path './templates/*' -not -path './repo-template/*'`）。少なくとも
  「雛形ソリューションを同梱するリポジトリでは探索範囲を絞ること」を既定のコメントに明記する。
  自動発見は「編集不要」を謳っている分、この落とし穴が見えにくい。

## 残課題（第 2・第 3 ラウンドで判明した追加指摘）

### 7. `copilot-setup-steps.example.yml` だけ雛形ディレクトリ除外が入っていない

指摘 6 は `scripts/setup.sh` と `security.yml` には `-not -path './templates/*'` として反映されたが、
**同じ自動発見コードを持つ `copilot-setup-steps.example.yml` には入っていない**（planning#98 時点）。
Copilot coding agent の環境だけ雛形ソリューションを拾って restore が失敗する。
本リポジトリは当該ファイルで `src/*/backend/*.slnx` の明示ループを維持して回避している。

→ planning#96 へコメントで追報したが、**同 issue は追報の 10 分前に CLOSED 済み**であったため、
見落とし防止に独立した issue として起票し直した:
[planning#104](https://github.com/endazon/project-planning/issues/104)。
**planning#105（`7546777`）で反映済み**であり、本リポジトリの固有デルタも解消した
（`copilot-setup-steps.yml` はキットとバイト一致に戻った）。

### 8. `scripts.test.js` を実行する CI ジョブがキットに無い（第 3 ラウンドで判明）

キットの `ci.example.yml` は個別スクリプトの `--self-test` は走らせるが、`scripts.test.js`
そのものを実行するジョブが無い（キット全体で `scripts.test.js` への言及は `pr-title.yml` の
コメント 1 行のみ）。結果として、キット由来のリポジトリでは同ファイルが
「誰かが手で叩いたときだけ走るテスト」になる。

実害の実例: planning#98 の反映で入った `gen-changelog.js` の `.map(applyOverride)` 回帰
（`TypeError: overrides.find is not a function`）は、`changelog.yml` が develop/main への push
でしか起動せず、`scripts.test.js` も CI に載っていないため、**PR の CI が全部 green のまま
マージ後まで検出されない**状態だった。planning#105 が同時に追加した「実行して確かめる」
E2E テストも、そのままでは CI で走らない。

→ [planning#108](https://github.com/endazon/project-planning/issues/108) として起票し、
**planning#110（`7701d25`）で反映済み**。本リポジトリは先行追加したジョブをキットの版へ揃え、
`scripts/README.md` にもキットの「検査（CI）」節を取り込んだ。

### 9. 雛形ソリューションのトラップが `codeql.example.yml` だけ未対応（第 4 ラウンドで判明）

指摘 6 は `setup.sh` / `security.yml` / `copilot-setup-steps.example.yml` の 3 ファイルに反映されたが、
**同じトラップを踏む `codeql.example.yml` が対象外**のままである（`7701d25` 時点で
`grep -rln "templates/\*" repo-template/` の結果は 3 ファイルのみ）。

`autobuild` は明示的な `find` を書かない代わりにリポジトリ全体を走査してビルド対象を推定するため、
ビルド不可の雛形ソリューションを拾うとそこで失敗する。原因は同一だが `find` の除外では直せず、
**対処法が異なる**（実ユニットの明示ビルドへ置き換える）。しかもエラーは「ビルド失敗」としか出ず、
原因が雛形であることは出力から読み取れない。本リポジトリは Issue #230 で実際にこれを踏み、
`codeql.yml` の `autobuild` を `src/*/backend/backend.slnx` の明示ビルドへ置き換えている。

→ [planning#111](https://github.com/endazon/project-planning/issues/111) として起票。

## 実装で判明した経緯

`planning` submodule の pin を `10d8ce2` → `6a1cc9f` へ更新し、`repo-template` の全ファイルを
本リポジトリへ反映する作業（作業仕様書 `docs/specs/20260801_impl-handoff-kit-sync.md`）で、
全ファイルの差分を分類する過程で判明した。

1・3 は「本リポジトリ側が進んでいる／キットの指示が実装されていない」ため差分の向きから、
2 は同期後に Actions 版数が巻き戻ったことから、4・5 はキット内の記述同士を突き合わせて、
6 はキット既定を採用したうえで実際に `dotnet restore` を走らせて判明した。

## 提案（計画への反映案）

- 反映先候補: その他（計画リポジトリ `tools/impl-handoff-kit` の更新）
- 提案内容:
  - 1: `repo-template/scripts/check-doc-links.js` を `.gitmodules` 由来の判定へ差し替える
    （本リポジトリの実装を移植）。
  - 2: 計画リポジトリの Dependabot にキットの workflows ディレクトリを追加する。
    難しければ `HOWTO.md` に版数の扱いを明記する。
  - 3: `repo-template/.claude/agents/traceability-auditor.md` に修飾付き ID 除外の規約を追記する。
  - 4: `repo-template/docs/README.md` の任意種別に `how-to` を追加し、`docs/how-to/.gitkeep` を置く。
  - 5: `repo-template/.github/workflows/copilot-setup-steps.example.yml` の `dotnet-version` を
    `10.0.x` へ揃える。
  - 6: `repo-template/scripts/setup.sh` と `security.example.yml` の自動発見から雛形ディレクトリを
    除外する（または既定コメントで注意喚起する）。
  - 7: 同じ除外を `repo-template/.github/workflows/copilot-setup-steps.example.yml` にも入れる。
  - 8: `repo-template/.github/workflows/ci.example.yml` に `scripts.test.js` を実行するジョブを追加し、
    `scripts/README.md` の「自動生成（CI）」節にも記載する。
  - 9: `repo-template/.github/workflows/codeql.example.yml` の `autobuild` に、雛形ソリューションを
    拾って失敗する旨の注意書きを置く（`find` の除外では直せないため対処法も示す）。

## 影響範囲

- キットから生成済み・生成予定の**すべての実装リポジトリ**に及ぶ（本リポジトリと
  `ai-stock-trading` を含む）。ただし 1〜9 のいずれも足場の改善であり、計画書の要求・UC・画面・
  計画 ADR の内容には影響しない。

### 反映状況（2026-08-01 時点・planning `7701d25`）

| 指摘 | 反映 | 本リポジトリの状態 |
| --- | --- | --- |
| 1 `check-doc-links.js` の一般化 | planning#98 | キットと一致 |
| 2 Actions 版数 | planning#98 | キットと一致（以後も新しい側を採る） |
| 3 `traceability-auditor.md` | planning#98 | キットと一致 |
| 4 `how-to` 種別 | planning#98 | キットと一致（`docs/how-to/.gitkeep` 追加） |
| 5 Copilot の .NET 版数 | planning#98 → 105 | キットと一致 |
| 6 雛形ソリューション除外 | planning#98 → 105 | キットと一致 |
| 7 Copilot だけ除外漏れ | planning#105 | キットと一致 |
| 8 `scripts.test.js` の CI 未結線 | planning#110 | キットと一致（`ci.yml` の `scripts-tests`・`scripts/README.md`） |
| 9 `codeql.example.yml` の雛形トラップ | **未反映**（planning#111） | `autobuild` を実ユニットの明示ビルドへ置換（維持） |

9 が取り込まれるまで、`codeql.yml` の明示ビルドは固有デルタ（構成起因）として維持する。
