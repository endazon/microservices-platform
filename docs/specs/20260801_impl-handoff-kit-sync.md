---
title: planning submodule 最新化と impl-handoff-kit の全面同期
type: spec
status: done
related_ids: [NFR, IADR-0115]
author: Claude
created: 2026-08-01
updated: 2026-08-01
plan_refs: []
---

# 仕様書: planning submodule 最新化と impl-handoff-kit の全面同期

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（NFR: 保守性・運用性。開発基盤の整備）
- 起点 Issue: **#434**（`claude_args` の記法誤りで @claude 実装と AI レビューがビルド・テストを実行できない・最優先）。
  本作業のキット同期がその是正を運ぶ（後述「Issue #434 の受け入れ基準」）。
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)（本作業で新規作成。キットを正とする同期規約）
- 計画書リンク: `planning/tools/impl-handoff-kit/`（`HOWTO.md` / `repo-template/`）

## 目的・背景

計画リポジトリ `project-planning` の submodule pin が `10d8ce2`（ADR-0014）で止まっており、
以降の 9 コミット（`6a1cc9f` まで）を取り込めていない。とくに最新の planning#95
「impl-handoff-kit の Claude 設定・GitHub 設定を是正する」は、**本リポジトリで現に壊れている設定**
（`claude_args` の記法不具合により CI の AI 実装・レビューがツールを使えない）の是正を含む。

同時に、本リポジトリは `impl-handoff-kit/repo-template` から生成された足場を持ちながら、
キット側の改善が長期間反映されていない。以後の乖離を防ぐため、本作業で**キットを正**として
全ファイルを同期し、リポジトリ固有の逸脱を意図的な最小集合へ絞り込む。

## 対象範囲

- 対象: `planning` submodule の pin 更新（`10d8ce2` → `6a1cc9f` → `12cc9b8` → `35b830a` → `7701d25` → `c72dbf2` → `30a4b78`）と、
  `impl-handoff-kit/repo-template` 配下の全ファイルの本リポジトリへの反映。
  キットに不足していた点の計画リポジトリへのフィードバック起票（`feedback/`）。
- 対象外: `src/` 配下のアプリケーション実装、`deploy/`、`src/ai-stock-trading` submodule の pin。
  CHANGELOG.md（`changelog.yml` の生成物）。

## 設計

### 方針（IADR-0115）

`repo-template` の各ファイルを 3 分類し、分類ごとに機械的に扱う。

| 分類 | 扱い | 例 |
| --- | --- | --- |
| A. キット完全一致 | キットの内容で上書きする | `.claude/settings.json` / `scripts/check-commit-messages.js` |
| B. キット＋固有デルタ | キットを土台に、固有部分のみ再適用する | `CLAUDE.md`（技術スタック別ルール）/ `ci.yml` |
| C. 本リポの中身そのもの | 変更しない | `docs/adr/README.md` / `docs/operations/operations.md` / `.gitignore` |

分類 B で許容する「固有デルタ」は次の 4 種のみとする。それ以外の独自記述は本作業で削除する。

1. リポジトリ構成（ユニット第一構成 `src/*/{backend,frontend}`・submodule 取得ステップ）
2. 技術スタック（.NET 10 / React+Vite / npm workspaces）とその CI 配線
3. 本リポにしか存在しない成果物・スクリプト（`images.yml` / `check-unit-dependencies.js` 等）
4. Dependabot が更新した **Actions のバージョン**（キットより新しい側を常に採る）

### 反映内容

**A: キットで上書き**

- `.claude/commands/plan-feedback.md` — 環流の主経路を GitHub Issue へ変更
- `.claude/hooks/check-impl.js` — 作業仕様書の有無をブランチ差分で判定（既存蓄積による形骸化を防ぐ）
- `.claude/settings.json` — `Grep`/`Glob`/`Bash(git show:*)`/`gh issue view` 等を allow に追加
- `.github/dependabot.yml` / `.github/workflows/pr-title.yml`
- `scripts/check-commit-messages.js` — ADR/IADR の実在性検査を追加
- `scripts/check-ai-workflow-config.js` — 新規（キットに追加されたもの）
- `AGENTS.md`

**B: キット＋固有デルタ**

- `CLAUDE.md` — キット本文 ＋ 既存の「技術スタック別ルール」節を保持
- `AI_SETUP.md` — キット本文 ＋ プロファイル宣言 `[x] claude-code` を保持
- `.claude/rules/traceability.md` — キット本文 ＋ 本リポの名前空間定義（MSP の ID レンジ、
  `AST` / `planning` の短縮修飾、短縮形へ寄せる決定）のみを固有節として残す
- `docs/ai-workflow.md` — キット本文 ＋ `images.yml` / `image-build` 必須チェックの記述を保持
- `docs/README.md` / `scripts/README.md` — キットの行を取り込み、本リポ固有の行を保持
- `.github/workflows/changelog.yml` — キット本文（`AUTOMATION_PR_TOKEN` フォールバック・既知の制約の注記）
  ＋ Actions は新しい方（`setup-node@v7` / `create-pull-request@v8`）
- `.github/workflows/openapi.yml` — キット本文 ＋ `paths: src/*/backend/**` ＋ 新しい Actions
- `.github/workflows/security.yml` — キット本文（gitleaks 誤検知の運用注記）
  ＋ `src/*` submodule 取得ステップ ＋ `src/*/backend/backend.slnx` の明示ループ ＋ `setup-dotnet@v6`
- `scripts/setup.sh` — キット本文 ＋ `src/*/backend/*.slnx` の明示ループ
  （キット既定の「maxdepth 4 で自動発見」は `templates/unit-template/backend/backend.slnx` を拾い、
  `dotnet restore` が `無効なフレームワーク識別子`（exit 1）で失敗することを実測で確認したため）
- `.github/workflows/doc-links-planning.yml` — キット本文（`timeout-minutes`・失敗時 issue 起票）
  ＋ `setup-node@v7`
- `.github/workflows/claude-coding.yml` / `claude-code-review.yml` — キット本文で全面置換
  （`--allowedTools` を引用符付きカンマ区切りの 1 引数へ是正、`concurrency` / `timeout-minutes`、
  レビュー用プロンプトの計画書探索順・MCP 名の注記）＋ `setup-dotnet@v6`
- `.github/workflows/ci.yml` — キットの `ai-workflow-config` ジョブを追加（他ジョブは現状維持）
- `scripts/scripts.test.js` — キットのテストブロック 2 件
  （`check-ai-workflow-config` / `validateIdExistence`）を復元

**C: 変更しない**

`.gitignore`（キットは真部分集合）・`.gitmodules`・`CHANGELOG.md`・`docs/adr/README.md`・
`docs/operations/operations.md`・`docs/security/security.md`・`docs/tech/tech-requirements.md`・
`.github/workflows/{codeql,frontend,frontend-tests,copilot-setup-steps,images,image-mapping}.yml`・
`scripts/check-doc-links.js`（本リポ側がキットより進んでいる）・`.claude/agents/traceability-auditor.md`

### 計画リポジトリへのフィードバック

キット側の不足として次を `feedback/` に起票し、GitHub Issue 本文を用意する。

1. `check-doc-links.js` の submodule 判定が `planning/` 固定。本リポの
   `.gitmodules` 由来の一般化（MSP #283）をキットへ取り込むべき。
2. キットの GitHub Actions のバージョンが古い（`setup-node@v6` / `setup-dotnet@v5` /
   `create-pull-request@v7` / `upload-artifact@v4`）。キットは Dependabot の対象外のため、
   同期のたびに実装リポ側が再度バンプする必要がある。
3. `.claude/agents/traceability-auditor.md` にキット自身の規約
   （修飾付き ID を突合対象から除外する）が書かれておらず、`traceability.md` と自己不整合。
4. `docs/how-to/`（使い方・デプロイ手順）が仕様書の種別表に無い。
5. `copilot-setup-steps.example.yml` の .NET が `8.0.x` で、`ci.example.yml`（`10.0.x`）と不整合。
6. `setup.sh` / `security.example.yml` のソリューション自動発見（`find . -maxdepth 4`）が、
   ビルド不可の雛形ソリューション（本リポの `templates/unit-template/`）を拾って失敗する。

### 第 2 ラウンド（planning#98 反映後の再同期）

初回同期（pin `6a1cc9f`）で起票した [planning#96](https://github.com/endazon/project-planning/issues/96) の
6 件が planning#98（`12cc9b8`）で**すべてキットへ反映された**（ai-stock-trading からの planning#97 と
併せて計 12 件）。同 pin へ再同期し、固有デルタを次のとおり**縮小**した。

**固有デルタが解消した（キットとバイト一致に戻った）ファイル**

- `scripts/check-doc-links.js` — キットが `.gitmodules` 由来の判定へ一般化（指摘 1）
- `scripts/setup.sh` / `.github/workflows/security.yml` — キットの自動発見が `./templates/*` を
  除外するようになった（指摘 6）。明示ループの固有デルタを撤去した
- `.claude/agents/traceability-auditor.md` — キットが修飾付き ID の除外規則を同梱（指摘 3）
- `.claude/commands/new-spec.md` / `docs/README.md` / `CLAUDE.md` — `runbook` / `how-to` 種別が
  正式化（指摘 4）。`docs/how-to/.gitkeep` を追加
- `scripts/gen-changelog.js` / `scripts/commit-allowlist.json` — テスト注入可能な `applyOverride` と、
  実データ非依存の allowlist テンプレートを取り込み

**この再同期で見つかった本リポジトリ側の欠陥（キットの新テストが検出）**

`scripts/commit-allowlist.json` に載っていた 5 件の SHA は、**本リポジトリの git 履歴に 1 件も
存在しなかった**（`git cat-file -t` が全件失敗＝キットが言う「幻 SHA」）。他リポジトリの
allowlist をそのまま引き継いだものと考えられる。実害としては、規約チェックの除外リストが
**何も除外していないのに『除外実績がある』ように見え**、以後の追加を正当化しかねない状態だった。

`origin/develop` の全履歴（bot / merge / `[skip ci]` を除く）を `validateSubject` で走査したところ
**非準拠コミットは 0 件**であったため、allowlist はキットのテンプレート（空）へ戻した。
以後は `scripts.test.js` の 3 テスト（完全 SHA と reason の存在 / 履歴実在と到達可能性 /
準拠件名を無意味に除外していないこと）が同型の混入を機械的に止める。

**残した固有デルタ**

- `.github/workflows/copilot-setup-steps.yml` — 雛形除外がキット側に未反映のため、
  `src/*/backend/*.slnx` の明示ループを維持する。`.NET` は `8.0.x` → `10.0.x` へ揃えた（指摘 5）。
  キットへは [planning#104](https://github.com/endazon/project-planning/issues/104) として追報済み
  （planning#96 は追報の 10 分前に CLOSED 済みだったため独立起票した。planning `bf94477` 時点でも未反映）。
- `.github/workflows/doc-links-planning.yml` — `.example` 由来の「本ファイルをリネームする」手順を
  除去（有効化済みの実ファイルのため。PR #433 の AI レビュー指摘）。
- `.github/workflows/frontend.yml` / `frontend-tests.yml` — キットは IADR 参照を汎用化のため
  削除したが、本リポジトリでは IADR-0033 / IADR-0034 / IADR-0056 が実在するため残す。
- `scripts/verify-qdrant-attribute-payload.sh` — キットからは削除された（MSP 固有のため妥当）。
  本リポジトリの成果物として保持する（IADR-0014 / #71）。

### 第 3 ラウンド（planning#105 / #107 反映後の再同期）

pin を `12cc9b8` → `35b830a` へ進めた。キット側では planning#105（`7546777`。#98 の反映漏れ・回帰 3 件の是正）
と planning#107（`35b830a`。配布物から他プロジェクトの痕跡を除去）が入っている。

**本リポジトリに存在した実害の是正**

`scripts/gen-changelog.js` が `TypeError: overrides.find is not a function` で**完全に壊れていた**。
第 2 ラウンドで `applyOverride(c, overrides = OVERRIDES)`（テスト注入可能な第 2 引数）を取り込んだ一方、
呼び出し側が `.map(applyOverride)` の point-free のままだったため、`map` が渡す `index`（数値）が
`overrides` を上書きし、1 件目から例外になっていた。planning#105 の修正
（`.map((c) => applyOverride(c))`）を取り込んで解消した。

この回帰が PR CI をすり抜けたのは、`changelog.yml` が develop/main への push でしか起動しないうえ、
`scripts.test.js` がどの CI ジョブからも実行されていなかったためである（後述の指摘 8）。

**固有デルタが解消したファイル**

- `.github/workflows/copilot-setup-steps.yml` — 雛形除外が入り（planning#105・指摘 7）、明示ループを撤去
- `scripts/check-doc-links.js` / `scripts/gen-changelog.js` / `scripts/validate-pipeline-config.js`

**キットの置換点へ寄せたファイル**

- `scripts/check-commit-messages.js` — 計画 ADR の実在集合が**自プロジェクトの名前空間に限定**された
  （従来は `projects/` 全走査で、他プロジェクトにしか無い ADR 番号まで実在として受理していた）。
  【置換点】`PLAN_PROJECT` に `microservices-platform` を設定。これがキットとの唯一の差分。
- `.github/workflows/ci.yml` の `pipeline-config` ジョブ — キットが `PIPELINE_CONFIG` 環境変数による
  置換点に変わったため同形へ寄せ、値に `deploy/helm/microservices-platform/files/pipeline.json` を設定。
- `.github/workflows/openapi.yml` — キットの【置換点】コメントを保ったまま `src/*/backend/**` を指定。

**この再同期で新たに見つかったキットの不足（指摘 8）**

`scripts.test.js` を実行する CI ジョブがキットに無い。キット全体で同ファイルへの言及は
`pr-title.yml` のコメント 1 行のみで、`ci.example.yml` には対応ジョブが無い。上記の
`gen-changelog` 回帰がマージ後まで検出されなかった直接の原因であり、planning#105 が同時に追加した
「実行して確かめる」E2E テストも、そのままでは CI で走らない。
[planning#108](https://github.com/endazon/project-planning/issues/108) として起票し、本リポジトリは
先行して `ci.yml` に `scripts-tests` ジョブ（`fetch-depth: 0`）を追加した。

### 第 4 ラウンド（planning#110 反映後の再同期）

pin を `35b830a` → `7701d25` へ進めた。第 3 ラウンドで環流した
[planning#108](https://github.com/endazon/project-planning/issues/108)（`scripts.test.js` の CI 未結線）が
**キットへ反映された**（planning#110。ai-stock-trading からの planning#109 と併せて是正）。

**固有デルタが解消したファイル**

- `.github/workflows/ci.yml` の `scripts-tests` ジョブ — 先行追加していたものをキットの版
  （コメント・配置とも `commit-messages` の直後）へ揃えた
- `scripts/README.md` — キットの「検査（CI）」節を取り込み、本リポジトリ固有のジョブ 5 行を追記
- `scripts/check-commit-messages.js` — `PLAN_PROJECT` の fail-open を警告で可視化する変更を取り込み
  （置換点の値 `microservices-platform` のみが依然キットとの唯一の差分）

**この再同期で新たに見つかったキットの不足（指摘 9）**

指摘 6 の雛形ソリューション対策は `setup.sh` / `security.yml` / `copilot-setup-steps.example.yml` の
3 ファイルに入ったが、**同じトラップを踏む `codeql.example.yml` が対象外**のままである。
`autobuild` はリポジトリ全体を走査してビルド対象を推定するため雛形を拾って失敗するが、`find` の
除外では直せず対処法が異なり、しかもエラーは「ビルド失敗」としか出ない。本リポジトリは Issue #230 で
実際にこれを踏み、`codeql.yml` の `autobuild` を実ユニットの明示ビルドへ置き換えている。
[planning#111](https://github.com/endazon/project-planning/issues/111) として起票した。

### 第 5 ラウンド（planning#113 反映後の再同期）

pin を `7701d25` → `c72dbf2` へ進めた。第 4 ラウンドで環流した
[planning#111](https://github.com/endazon/project-planning/issues/111)（`codeql.example.yml` の雛形トラップ）が
反映され、あわせて planning#112 由来の**固有テストの受け口**（companion ファイル）が入った。

**最大の成果: `scripts/scripts.test.js` がキットとバイト一致になった**

キットが `scripts/scripts.local.test.js` を自動読み込みする受け口を設けたため、本リポジトリ固有の
テスト 48 件（`check-doc-links` / `check-unit-dependencies` / `check-image-mapping` /
`check-realm-constraints` / `check-unit-service-ownership`）を companion へ移した。
これまで同期のたびに手作業でスプライスしていた `scripts.test.js` が、以後は**上書きコピー 1 回**で済む。
分類 A（バイト一致）へ移行した。

**その他の変更**

- `.github/workflows/copilot-setup-steps.yml` — キットのコメント追記を取り込み（キットと一致）
- `.github/workflows/codeql.yml` — キットの【落とし穴】注記が示す置き換えを実施済みである旨と、
  同種トラップの他 3 か所への相互参照をコメントへ追記
- `scripts/README.md` — キットの「リポジトリ固有のテストを足す場所」節を取り込み

**この再同期で新たに見つかったキットの不足（指摘 10）**

companion の受け口は「あれば読む」だけであり、**ファイルが消えても exit 0 のまま件数だけが静かに減る**。
実測で削除前 101 件 → 削除後 53 件、いずれも exit 0 で CI は green のままだった。さらに受け口自体の
回帰テストは「companion が既に存在すると skip」するため、**この仕組みを使っているリポジトリでは
一度も実効しない**。planning#108 や PLAN_PROJECT の fail-open と同じ「ジョブは成功するのに検査が
効いていない」型である。[planning#114](https://github.com/endazon/project-planning/issues/114) として起票した。

### 第 6 ラウンド（planning#116 反映後の再同期）

pin を `c72dbf2` → `30a4b78` へ進めた。第 5 ラウンドで環流した
[planning#114](https://github.com/endazon/project-planning/issues/114)（companion の消失が検出されない）が
planning#116 で反映され、提案より良い実装になった——受け口の回帰テストを一時ディレクトリ上で行うことで
**実 companion がある環境でも常に実効する**（旧実装は companion があると skip され、この仕組みを
使っているリポジトリでだけ検証されないという逆転が起きていた）。

**適用内容**

- companion を `scripts/scripts.local.test.js` → **`scripts/scripts.repo.test.js`** へ改名
  （planning#115 由来。`.local` は多くのプロジェクトで「コミットしない」の目印であり、
  `.gitignore` に除外されると固有テストが黙って消えるため）。本リポジトリの `.gitignore` には
  `*.local` があるが `scripts.local.test.js` には一致しなかった（実害は無かったが改名する）。
- `.github/workflows/ci.yml` の `scripts-tests` ジョブで **`REQUIRE_REPO_TESTS: "1"` を有効化**。
  消失時 exit 1・正常時 exit 0 を実測で確認した。
- `scripts/scripts.test.js` はキットとバイト一致を維持（分類 A）。`scripts/README.md` の該当節も差し替え。

**この再同期で新たに見つかったキットの不足（指摘 11）**

`REQUIRE_REPO_TESTS` は opt-in で、`ci.example.yml` では既定でコメントアウトされている。つまり
指摘 10 の防御は「companion を作る」「env を有効化する」の 2 ステップを両方こなしたリポジトリにだけ
効くが、**2 つ目を忘れた状態に対する注意喚起が一切出ない**（未追跡のときは警告が出るのに、
より起きやすいこちらは無言）。[planning#117](https://github.com/endazon/project-planning/issues/117)
として起票した。

## Issue #434 の受け入れ基準

本作業のキット同期が Issue #434（最優先バグ）の是正を運ぶ。同 issue の受け入れ基準に対する実測結果。

| 受け入れ基準 | 結果 |
| --- | --- |
| `check-ai-workflow-config.js` が両ファイルについて不備 0 件 | ✅ ローカル・CI（`ai-workflow-config` ジョブ）とも合格 |
| ジョブログの `SDK options:` で `allowedTools` が割れていない | ✅ run `30688146948` で確認（下記） |
| AI レビューが検証を実走し「承認待ちでブロック」の報告が消える | ✅ 実走を確認（`dotnet test` は本 PR に `src/` 変更が無く対象外） |
| 1 PR に対するレビュー起動が 1 本に収まる | ✅ 8 回の push に対しレビュー実行は 8 本・並走なし |

`SDK options:` の実測（run `30688146948`）。空白を含む指定が 1 要素として保たれている。

```
"Bash(gh issue create:*)",
"Bash(gh pr view:*)",
"Bash(dotnet test:*)",
"Bash(dotnet format:*)"
```

是正前は `"Bash(gh", "issue", "create:*)"` のように割れていた（Issue #434 の根本原因）。

なお #434 が挙げる是正 5 点（記法・レビュー側の検証系ツール・`concurrency`/`timeout-minutes`・
プロンプトの計画書探索順・`automation/` 除外と `.claude-pr/` の説明）はいずれも反映済みである。
同 issue の「関連」が指摘する `ai-stock-trading` 側の同一是正は本リポジトリの範囲外。

## 受け入れ基準

- [x] `planning` submodule が `origin/main`（`30a4b78`）を指す
- [x] 分類 A のファイルが `repo-template` と **バイト一致**する
- [x] 分類 B のファイルが、キット由来の記述をすべて含み、固有デルタが上記 4 種に限られる
- [x] `node scripts/check-ai-workflow-config.js --self-test` と実チェックが成功する
- [x] `node scripts/scripts.test.js` が全件成功する
- [x] `node scripts/check-doc-links.js` が成功する
- [x] `node scripts/check-commit-messages.js --title "chore(NFR,IADR-0115): planning submodule を最新化し impl-handoff-kit を全面同期する"` が成功する
- [x] キットへの不足 6 件が `feedback/` に記録され、計画側へ起票する本文が用意されている

## テスト方針

本作業はコードのふるまいを変えないため、既存の機械検査で回帰を確認する。

- `scripts/scripts.test.js`（キット由来のテストブロック復元を含む）
- `scripts/check-ai-workflow-config.js --self-test` ＋ 実ツリー検査
  （＝ワークフローの `--allowedTools` 記法是正が効いていることの検証）
- `scripts/check-doc-links.js`（本仕様書・IADR からの相対リンク）
- 差分検証: 分類 A は `diff` でバイト一致を確認する

## 計画書との差異

- 差異: あり。キット側の不足 6 件（上記「計画リポジトリへのフィードバック」）。
  いずれも本リポジトリ側で先行対応済み、またはキットの内部不整合であり、
  `/plan-feedback` の記録として `feedback/20260801_impl-handoff-kit-gaps.md` に残し、
  計画リポジトリへ [planning#96](https://github.com/endazon/project-planning/issues/96) として起票済み。

## 未決事項

- なし（キット側の不足はフィードバックとして環流し、キットの更新を待って再同期する）
