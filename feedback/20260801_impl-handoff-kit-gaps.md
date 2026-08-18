---
title: impl-handoff-kit の不足 17 件（submodule リンク判定の一般化・Actions 版数・自己不整合・CI 未結線ほか）
type: plan-feedback
status: accepted
category: その他
related_ids: [NFR, IADR-0115]
source_repo: microservices-platform
source_ref: docs/specs/20260801_impl-handoff-kit-sync.md
author: Claude
created: 2026-08-01
---

# フィードバック: impl-handoff-kit の不足 17 件（初回 6 件 ＋ 追加 11 件・うち 1 件は取り下げ）

## 種別

その他（計画リポジトリの成果物 `tools/impl-handoff-kit` に対する改善提案）。
要求・ユースケース・画面の内容に関する指摘ではない。

計画リポジトリへ起票済み: [planning#96](https://github.com/endazon/project-planning/issues/96)
（`plan-feedback` ラベル。計画側 `/triage-feedback` の取り込み対象）。

**反映結果（2026-08-01）**: planning#98（`12cc9b8`）で **6 件すべてが反映された**（ai-stock-trading
からの planning#97 と併せて計 12 件）。本リポジトリは同 pin へ再同期済みで、1・6 の固有デルタ
（`check-doc-links.js` / `setup.sh` / `security.yml`）は**解消してキットと一致**した。
その後の再同期で追加 11 件（下記「残課題」7〜17）を検出し、いずれも起票済み（13 は前提誤りで取り下げ）。

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

→ [planning#111](https://github.com/endazon/project-planning/issues/111) として起票し、
**planning#113（`c72dbf2`）で反映済み**（`autobuild` に【落とし穴】注記と置き換え例が入った）。
本リポジトリの明示ビルドは、その注記が示す置き換えそのものであるため固有デルタとして維持する。

### 10. `scripts.local.test.js` が消えてもテストは緑のまま（第 5 ラウンドで判明）

planning#112 で導入された固有テストの受け口（companion ファイル）は非常に有効で、本リポジトリは
これにより `scripts/scripts.test.js` を**キットとバイト一致に戻せた**（それまで同期のたびに手作業で
スプライスしていた）。一方で受け口の実装は「あれば読む」だけであり、companion が消えても
**exit 0 のまま件数だけが静かに減る**。実測: 削除前 101 件 → 削除後 53 件、いずれも exit 0。
受け口自体の回帰テストも「companion が既に存在すると skip」するため、**この仕組みを実際に
使っているリポジトリでは一度も実効しない**。

planning#108（`scripts.test.js` が CI に載っていない）や PLAN_PROJECT の fail-open 可視化（planning#110）と
同じ「ジョブは成功するのに検査が効いていない」型である。

→ [planning#114](https://github.com/endazon/project-planning/issues/114) として起票
（(1) companion があるのに 1 件も登録しなければ失敗させる、(2) 必須化の opt-in を設ける）。
**planning#116（`30a4b78`）で反映済み**。提案より良い実装になっており、受け口の回帰テストを
一時ディレクトリ上で行うことで**実 companion がある環境でも常に実効する**ようになった
（旧実装は companion があると skip され、この仕組みを使っているリポジトリでだけ検証されなかった）。
あわせて未追跡検出と、`.local` からの改名（planning#115 由来）が入った。

### 11. companion があるのに `REQUIRE_REPO_TESTS` 未設定だと無言（第 6 ラウンドで判明）

planning#116 の消失検出は `REQUIRE_REPO_TESTS=1` の opt-in であり、`ci.example.yml` では既定で
コメントアウトされている。つまり指摘 10 の防御は「companion を作る」「env を有効化する」の
**2 ステップを両方こなしたリポジトリにだけ**効く。2 つ目を忘れると指摘 10 の挙動がそのまま残るが、
**その状態に対する注意喚起が一切出ない**（未追跡のときは警告が出るのに、より起きやすいこちらは無言）。

→ [planning#117](https://github.com/endazon/project-planning/issues/117) として起票
（companion を検出したのに `REQUIRE_REPO_TESTS` 未設定なら 1 行 notice を出す。`PLAN_PROJECT` の
fail-open 可視化と同じ形）。本リポジトリは `ci.yml` で `REQUIRE_REPO_TESTS: "1"` を有効化済み。
**planning#119（`cff9b6c`）で反映済み**（新旧 companion 同居の警告も併せて追加された）。

### 12. `git -C planning` の許可が `settings.json` に未追随（第 7 ラウンドで判明）

planning#119 は `Bash(git -C planning …:*)` 4 件を `claude-coding` / `claude-code-review` の
**2 系統にだけ**追加し、3 つ目の `.claude/settings.json` が追随していない。キット自身の検証器が
キット自身の設定に warn を出す状態である（実測）。`settings.json` の `//` 注記が
「3 系統を手作業で同期する構造であり、実際に乖離した実績がある」と警告しているのと同じ失敗モード。

→ [planning#121](https://github.com/endazon/project-planning/issues/121) として起票し、
**planning#125（`25b4291`）で反映済み**（`settings.json` に 4 件が追加され、3 系統が揃った）。
本リポジトリは `settings.json` をキットとバイト一致に戻し、`check-ai-workflow-config.js` の
warn が消えたことを確認した。

### 14. 「複製漏れは機械検出する」は部分的なドリフトを検出しない（第 8 ラウンドで判明）

planning#125 は `ci.example.yml` のヘッダに「記法誤り・複製漏れは ai-workflow-config ジョブが
機械検出する」と追記したが、**部分的な複製漏れは検出されない**。`check-ai-workflow-config.js` の
検査は 1 ファイル単位であり、`claude-coding` と `claude-code-review` のツール集合を突き合わせる
処理は無い（`parityWarnings` は各ファイルを `settings.json` と比べるだけ）。

実測: レビュー側から `build` / `format` / `restore` を落として `test` だけ残しても、
エラーも警告も出ない（`setup-dotnet` に対して実行ツールが 1 つ残っているため 3 番目の検査を通る）。
検出されるのは「レビュー側の実行系が**全滅**した」場合だけである。

「機械検出する」と書くと読み手は手作業の突き合わせをやめるため、部分的なドリフトが
「一部のコマンドだけ承認待ち」という**全滅より気付きにくい**劣化を生む。

→ [planning#126](https://github.com/endazon/project-planning/issues/126) として起票し、
**planning#127（`3325903`）で反映済み**（`toolchainDrift` が新設された）。提案より正確な実装で、
比較をコマンド名（`dotnet`）ではなく**ツール指定そのもの**（`Bash(dotnet build:*)`）の粒度で行う。
本リポジトリで実際にドリフトを作って検出（exit 1）・復元して合格を確認した。

### 15. `toolchainDrift` が `setup-*` 非対称時に誤検知する（第 9 ラウンドで判明・不具合）

planning#127 の `toolchainCommandsOf(text, tools)` は比較対象を**各ファイル自身の `uses: setup-*`**
から決めるため、2 ファイルの `--allowedTools` が**完全に同一**でも `setup-*` の構成が片方だけ
異なると差分として報告される。実測（キットの実装をそのまま使用）:

```
実装側 setup-dotnet + setup-node / レビュー側 setup-dotnet のみ、ツールは両方とも
'Read,Bash(dotnet test:*),Bash(npm run:*)' で同一

→ "claude-code-review.example.yml: 実装用にあるスタック別の実行ツールが欠けている:
   Bash(npm run:*)"
```

レビュー側に `Bash(npm run:*)` は**入っている**。WARN ではなく ERROR（exit 1）であり、
メッセージも実態と食い違う。「両ファイルを同じ内容に保つ」という規約を守っている利用者ほど混乱する。

副次的に、`toolchainDrift` はファイル名を `claude-coding` / `claude-code-review` の部分一致で
解決し、見つからなければ黙って空を返す（別名・統合構成では検査が無効になる）。

→ [planning#130](https://github.com/endazon/project-planning/issues/130) として起票し、
**planning#132（`7149fc6`）で反映済み**。比較基準を `TOOLCHAINS` 全体（`requireUses: false`）へ変え、
誤検知（指摘 15）と偽陰性（`setup-*` を書かない `node` の複製漏れ＝planning#131）を同時に解消した。
副次指摘（既定名で引き当てられないと無言で無効）にも `driftScopeWarnings` が新設された。
本リポジトリで 3 ケースとも独立に再現確認済み。

### 16. 検査が成立していない warn が CI のどこにも現れない（第 11 ラウンドで判明）

planning#135 は「既定名のファイルはあるが `claude_args` を解析できない」状態を warn で可視化した。
実ツリーで陽性対照（キー名を 1 文字変える）を取り、期待どおり検出されることを確認した。

ただしこの warn は **exit 0** であり、かつ検証器は GitHub Actions の annotation
（`::warning::` / `::error::`）を一切出していない（`grep -c` で 0 件）。したがって warn は
ジョブの結果にもチェック画面にも現れず、**ログを開いて読んだ人にだけ**見える。

その 1 行が出ている間、指摘 14・15 で 4 ラウンドかけて作ったドリフト検査を含む**全検査が無効**になる。
`claude_args` のキー名やインデントが崩れるだけで、CI は緑のまま検証が止まる。

→ [planning#136](https://github.com/endazon/project-planning/issues/136) として起票
（(1) GitHub Actions では `::warning::` で annotation として出す、
(2) `REQUIRE_REPO_TESTS` と同じ形の厳格モード opt-in を設ける。どちらも fail-open の既定は変えない）。

### 17. planning#138 の反映で `scripts.test.js` が GitHub Actions 上で失敗する（第 12 ラウンドで判明・不具合）

指摘 16（planning#136）は planning#138 で反映され、`scripts/lib/ci-annotate.js` による
アノテーション化と `STRICT_AI_WORKFLOW_CONFIG` の opt-in が両方入った。動作は実ツリーで確認済み
（既定 exit 0 / 厳格モード exit 1 / `GITHUB_ACTIONS=true` で `::warning::`）。

**ただし同じ変更で `scripts.test.js` が Actions 上で失敗する。** `ci-annotate` は Actions 上では
必ず **stdout** へ書くのに対し、テストの `captureStderr` は **stderr しか捕捉していない**。
その結果、

1. 「複数プロジェクト構成で退避したときは警告を出す」が空文字と突き合わせて失敗し、
   **`scripts-tests` ジョブが exit 1**（`GITHUB_ACTIONS=true node scripts/scripts.test.js` で再現）
2. テストのフィクスチャが**実 PR へ `::warning::` を 2 件漏らす**
   （`PLAN_PROJECT="no-such-project"` / `"<project-name>"` ——どちらも実設定ではない）

ローカルでは stderr のままなので**通る**＝「ローカルで緑・CI で赤」という最も気付きにくい形。

→ [planning#140](https://github.com/endazon/project-planning/issues/140) として起票
（`captureStderr` が stdout も捕捉する。あわせて自己試験を `GITHUB_ACTIONS=true` でも回す案を添えた）。
本リポジトリは CI を赤にできないため、**`scripts/scripts.test.js` の `captureStderr` のみ暫定デルタ**
として先行修正した。**planning#141（`326a31a`）で反映されたため暫定デルタは撤去済み**で、
`scripts.test.js` はキットとバイト一致に戻っている（`GITHUB_ACTIONS=true` ＋ `REQUIRE_REPO_TESTS=1`
の CI 同条件で exit 0・漏れる annotation 0 件を確認）。

## 結び（2026-08-01 時点）

**環流した 17 件のうち 17 件が決着した**（14 件がキットへ反映、1 件＝指摘 13 は前提誤りで取り下げ、
指摘 16・17 も反映済み）。
起票した planning issue のうち planning#96 / planning#104 / planning#108 / planning#111 / planning#114 / planning#117 / planning#121 / planning#126 / planning#130 は
に加え planning#136 / planning#140 もクローズ済みで、**全件決着**である。

以後キット側に新たな不足を見つけた場合は、本記録に追記せず**別の記録として起こす**
（計画側の `/sync-impl` は「記録 1 件 ↔ 環流 1 件」で到達を判定するため、
1 ファイルに多数の指摘を集約すると個々の未決着が見えなくなる）。

### 13.（取り下げ）`git -C planning` が CI で誤答するという報告は誤りだった

第 7 ラウンドで「`Bash(git -C planning …)` は PR CI では submodule 未取得のため実装リポの履歴を
静かに返す」と報告し planning#123 として起票したが、**前提が誤っており取り下げた**。

`claude-code-review.yml` には `actions/checkout` の後に submodule 取得の専用ステップ
（`Fetch planning submodule (read-only PAT)`）があり、`git submodule update --init --recursive` を
実行している。`actions/checkout` に `submodules:` が無いことだけを見て後続ステップを確認しなかった
のが誤りの原因である。実ジョブのログで `Submodule path 'planning': checked out 'cff9b6c…'` を確認し、
AI レビューが同ジョブ内で `git -C planning log` を実行して正しい submodule の HEAD を得ている。

再現手順として示した「空ディレクトリでの `git -C`」は一般的な git の挙動としては正しいが、
実際の `planning/` は populate 済みであり、この構成の検証になっていなかった。
PAT 未登録等で取得に失敗した場合も当該ステップが**失敗してジョブが落ちる**ため、
空ディレクトリのまま先へ進む経路は無い。

→ [planning#123](https://github.com/endazon/project-planning/issues/123) は取り下げ・クローズ済み。
`Bash(git -C planning …:*)` 4 件は意図どおり機能するため、指摘 12（`settings.json` への追随）は
**単独で有効**であり、むしろ必要性が上がった。

**教訓**: 「動かないはず」の主張は、動く経路（実ジョブのログ）を確認してから出す。
ワークフローの一部（checkout の引数）だけを見て全体の挙動を推論したのが誤りだった。

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
  - 10: companion があるのに 1 件も登録しなければ失敗させ、必須化の opt-in（環境変数等）を設ける。
  - 11: companion を検出したのに `REQUIRE_REPO_TESTS` 未設定なら 1 行 notice を出す。
  - 12: `settings.json` にも `git -C planning` 4 件を追加する（13 の取り下げにより単独で有効）。
  - 14: `TOOLCHAINS` 由来の実行ツール集合を 2 ワークフロー間で突き合わせる（またはヘッダ記述を実装に合わせる）。
  - 15: `toolchainDrift` の比較基準を 2 ファイルの `setup-*` の和集合にする。
  - 16: warn を GitHub Actions の `::warning::` で出し、厳格モードの opt-in を設ける。
  - 17: `captureStderr` が stdout も捕捉するようにする（自己試験を `GITHUB_ACTIONS=true` でも回す）。
  - 13: （取り下げ）誤報告のため対応不要。

## 影響範囲

- キットから生成済み・生成予定の**すべての実装リポジトリ**に及ぶ（本リポジトリと
  `ai-stock-trading` を含む）。ただし 1〜17 のいずれも足場の改善であり、計画書の要求・UC・画面・
  計画 ADR の内容には影響しない。

### 反映状況（2026-08-01 時点・planning `9cd3499`）

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
| 9 `codeql.example.yml` の雛形トラップ | planning#113 | 注記が示す置き換えを実施済み（明示ビルドを維持） |
| 10 companion 消失が検出されない | planning#116 | `scripts.repo.test.js`（改名）＋ `REQUIRE_REPO_TESTS=1` 有効化 |
| 11 opt-in 忘れが無言 | planning#119 | キットと一致（notice を確認） |
| 12 `git -C planning` が settings.json に未追随 | planning#125 | キットと一致（warn 解消を確認） |
| 13 `git -C planning` が CI で誤答 | **取り下げ**（planning#123・前提誤り） | キット準拠のまま。誤報告を訂正済み |
| 14 部分的な複製漏れを検出しない | planning#127 | キットと一致（検出・復元を実測） |
| 15 `toolchainDrift` の誤検知 | planning#132 | キットと一致（誤検知の解消を独立に再現確認） |
| 16 検査不成立の warn が CI に現れない | planning#138 | キットと一致＋`STRICT_AI_WORKFLOW_CONFIG=1` を有効化 |
| 17 `scripts.test.js` が Actions 上で失敗 | planning#141 | 暫定デルタを撤去しキットと一致（CI 同条件で確認） |

9 の注記が示す「実ビルド対象の明示指定」は `find` の除外では代替できないため、`codeql.yml` の
明示ビルドは今後も固有デルタ（構成起因）として維持する。
