---
title: 作業仕様書 — AI レビューのプロンプトに「検証の下限」を置く（#1911）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0232
author: claude
created: 2026-10-12
updated: 2026-10-12
issue: "#1911"
plan_refs:
  - planning:docs/ai-implementation-workflow-guide.md
related_specs:
  - "20261010_1876_ci-claude-5-5.md"
---

# 作業仕様書 — AI レビューのプロンプトに「検証の下限」を置く（#1911）

> 本仕様書は実装着手前に作成した（着手 2026-10-12）。基点は MSP `origin/develop` `dae84209`。
> 計画への変更は無い（NFR のメタ作業。CI の AI レビューのプロンプトと許可の変更で、製品の要求には触れない）。

## 起点

- issue: #1911。フェーズ 1 の点検結果は同 issue のコメント（2026-10-12）にある。利用者裁定（2026-10-12）は「是正案 A を実装する。C（opus への振り分け）と D（モデルの切り戻し）は今回はやらない」である。
- 実装 ADR: IADR-0232 決定 7（`claude-review` は必須のまま残す。AI レビューが事実上唯一のレビュー層）。**新しい IADR は起こさない** ——
  決定 7 が守ろうとしている「唯一のレビュー層の深さ」を保つための是正であり、別 IADR に割ると根拠が 2 か所に分かれる。決定 7 に日付つきで追記する。

## 問題（点検の実測。#1911 のコメントの要約）

- 2026-10-10 の既定モデル切替（#1878）の後、抜き取り 10 対で次のように変わった。
  - agent の中央値: 347 → 22 秒。
  - `scripts.test.js` 全件の実走: 8/10 → 0/10。
  - backend を変える PR での `dotnet test`: 4/4 → 0/6。
  - 文書系の検査器: 10/10 → 0/10。
  - 変更ファイルを全部読んだ: 10/10 → 2/10。
- 未検証の申告 43 件のうち 20 件は「自分で省略した・読まなかった」、2 件は「時間配分」を理由にしていた。
- 時間・ターンの上限ではない。`timeout-minutes: 20` で `--max-turns` は無く、agent は 16〜30 秒で終わっている。
- 許可の不足でもない。`dotnet test` は許可済みで、環境変数の前置きは `node -e` の子プロセス形で回避できる。
- **モデルの性向である。** プロンプトの「変更範囲に応じて検証を絞る」「時間が足りなければ未検証と書く」を早めに使っている。

## やること

1. `claude-code-review.yml` の `prompt:` に、【検証の下限・最重要】節を新設する。内容は次のとおり。
   - **変更ファイルは全部読む。** 対象は `git diff --name-only origin/develop...HEAD` の全件である。除外してよいのは生成物（orval の生成物・`locales/*/messages.ts`・`*.lock`・`pnpm-lock.yaml`・`CHANGELOG.md`）だけで、除外したら列挙する。
   - **backend の差分**（`src/<unit>/backend/**`）があれば、その unit の `dotnet build` と、変更したサービスのテストプロジェクトの `dotnet test` を走らせる。`--filter` は付けず、プロジェクト単位で全件を回す。あわせて `dotnet format <slnx> --verify-no-changes` も走らせる。
   - **`scripts/` か `.github/` の差分**があれば、`scripts.test.js` を子プロセス形（`REQUIRE_REPO_TESTS=1`）で全件走らせる。あわせて文書系の検査器も走らせる。
   - **文書系の検査器**は次の 6 本とする: `check-trace-blocks` / `gen-knowledge-graph --check` / `check-doc-links` / `check-doc-updated --base origin/develop` / `check-commit-messages` / `check-plan-id-qualification`。`docs/` か `.ai-context/` の差分があるときにも走らせる。
   - **helm の差分**（`deploy/helm/**`）があれば、`helm lint` と `helm template` を走らせる。
   - **未検証の理由に使えない語を定める。** 「時間」「時間配分」「時間優先」「差分が大きい」「ファイル数が多い」「静的な読み取りに絞った」は理由として認めない。上限は 20 分で、ターン上限は無い。
   - **ワークスペースへ書き込まない。** 出力ファイルを取る引数（`--out` / `--publish-out` 等）が要るときは `/tmp/` 配下を指定し、結果は標準出力で読む。
2. 【実行制約】の「時間が足りなければ…」と「変更範囲に応じて検証を絞る」を、下限と矛盾しない形に直す。
   - 投稿を優先してよいのは、ジョブの上限に近づいた場合だけとする。
   - 絞ってよいのは、下限に含まれない検証だけとする。
3. helm を実行できるようにする。`Bash(helm template:*)` / `Bash(helm lint:*)` を 3 系統に足す。3 系統は、レビュー用 `--allowedTools`、実装用 `claude-coding.yml`、`.claude/settings.json` である。
   - 理由: 現状は 3 系統のどれにも helm が無く、下限の「helm の差分 → template / lint」が実行できない。
   - 範囲は読み取り専用の 2 形だけとする（AST の構成と同じ）。`helm install` 等は足さない。
   - 【使える Bash コマンドの一覧】の記述も直す。
4. `scripts.repo.test.js` に固定試験を足す（AST#1248 の【Helm】節の試験と同じ型）。
   - 【検証の下限】節が在ること。
   - 節が案内する要の語（`scripts.test.js`・`REQUIRE_REPO_TESTS`・`dotnet test`・`dotnet format`・`helm lint`・`/tmp/`・「時間」）が在ること。
   - 節が案内する `helm template` / `helm lint` が `--allowedTools` に在ること。
   - 陰性対照（節を消す・許可を外す）で赤になること。
5. IADR-0232 決定 7 に日付つき追記を足す。

## やらないこと

- モデルの変更（案 C・D）。effort の指定（案 B）。
- ジョブ名・必須チェック・`timeout-minutes` の変更。
- 「Check review verdict」への「時間」理由の警告（任意とされた。下限は prompt と固定試験で守り、効果は抜き取りで見る。警告は効果の確認後に要否を決める）。

## 母集団（規則 9・10）

- 誤りの側の文字列で、`.ai-context/specs` を除く全追跡ファイルを走査した: `git grep -n -E "変更範囲に応じて(検証を)?絞|全スイートは最後の手段|時間が足りなければ"`。
  - 当たりは `claude-code-review.yml` の 284 行目と 286 行目の 2 件だけで、どちらも本 PR で直す。
- 「helm は拒否される」の記述: `git grep -n "helm"` で見ると、prompt の【使える Bash コマンドの一覧】の 1 行（`他の bash <スクリプト> や helm は拒否される`）だけであり、これを直す。
- 是正で新たに誤りになる自分の記述: IADR-0232 決定 7 の 10/12 追記にある「レビューの深さの点検は別 issue で扱う」は、本 PR の追記で結果を示す（本文は書き換えない）。

## 受け入れ基準

- [x] 【検証の下限】節が prompt に在り、上の内容を含む。
- [x] `helm template` / `helm lint` が 3 系統で一致して許可されている（`check-ai-workflow-config.js` が緑）。
- [x] 固定試験と陰性対照が `scripts.test.js` 全件の中で緑になる。
- [x] 文書系の検査器が緑になる。
- [x] **効果の確認**: 本 PR 自身の `claude-review` の本文で、次が満たされていることを示す（PR 本文と本仕様書の追記に記録する）。
  - `scripts.test.js` 全件を実走している。
  - 変更ファイルを全部読んでいる。
  - 「時間」を理由にした未検証が無い。
  - 所要（agent / job）を記録する。

## 効果の確認（2026-10-12 追記 / #1911）

本 PR（#1915）自身の `claude-review`（run 38246482427。新しいプロンプトで走った）の結果は次のとおり。

| 観点 | 結果 | 切替後の抜き取り（10 本） |
| --- | --- | --- |
| 変更ファイルの読了 | 6/6（本文冒頭に「変更 6 ファイル中 6 ファイルを読んだ」と申告） | 2/10 |
| `scripts.test.js` 全件 | 実走、子プロセス形、1044 件 passed | 0/10 |
| 文書系の検査器 | 6 本すべて実走 | 0/10 |
| `dotnet` / `helm` | 対象外（`src/*/backend`・`deploy/helm/**` の差分なし）と下限の条件どおりに判断 | — |
| 「時間」を理由にした未検証 | 0 件 | 2/10 本 |
| 所要（agent / job） | 147 秒 / 216 秒 | 中央値 22 秒 / 81 秒 |

下限は守られた。所要は延びたが、切替前の中央値（347 秒 / 404 秒）より短い。1 本だけの確認なので、規模の違う PR（backend・helm の差分を含む）での抜き取りを、マージ後に #1911 で行う。
