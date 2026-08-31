---
title: IADR-0327 撤去済み planning submodule の残存記述は「凍結記録を残し、それを引く live 側を直す」で是正する
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0048
  - IADR-0058
  - IADR-0060
  - IADR-0065
  - IADR-0228
author: claude
created: 2026-08-31
updated: 2026-08-31
plan_refs:
  - planning:projects/microservices-platform/07_adr/ADR-0048_impl-docs-restructure.md
---

# IADR-0327: 撤去済み planning submodule の残存記述の是正方針

- 状態: Accepted
- 日付: 2026-08-31
- 決定者: claude（実装）

## 起点・関連

- 起点 ID: NFR（文書整合）／計画 `ADR-0048` 決定 2。
- Issue: #1092（出所は #1080 の母集合引き直し）。
- 作業仕様書: `.ai-context/specs/20260831_issue-1092_planning-submodule-residual-refs.md`。

## コンテキストと課題

[IADR-0228](./IADR-0228_planning-dependency-removal.md) が planning submodule 依存を全面撤去した
（計画 `ADR-0048` 決定 2）。しかし撤去は「submodule・検査器・ワークフロー・環流機構」という**機構**を
対象にしており、**それらを現況として述べていた散文**は取り残された。

取り残しが目立たないのは、`planning` という語が**正当な参照**（別リポジトリ名・`planning#NNN` の
issue 修飾・隣接クローンのパス）としてリポジトリ全体で 943 ファイルに現れるためである。
**誤りの側だけを取り出す軸が要る。**

さらに、撤去は**理由を宙に浮かせた**。CI が `actions/checkout` の `submodules: recursive`/`true` を
使わない理由として複数の文書が「private な `planning` を巻き込むから」と書いていたが、この理由は
消えている。**理由を消すだけでは、次に読む人が `recursive` へ戻してしまう。**

## 決定

### 1. 是正の射程は「live な記述」に限る。凍結記録は残し、それを引く live 側を直す

- `.ai-context/adr/` の [IADR-0058](./IADR-0058_doc-links-planning-submodule-ci.md) /
  [IADR-0060](./IADR-0060_submodule-unit-operations.md) /
  [IADR-0065](./IADR-0065_public-unit-submodule-ci-fetch-no-token.md) は**書き換えない。
  `Superseded` にもしない。**
  [IADR-0228](./IADR-0228_planning-dependency-removal.md) が「個々の撤去対象を定めた旧 IADR は
  書き換えず、本 IADR から一括で参照する」「Supersedes: なし」と**既に決めている**からである。
  Accepted な IADR の決定を覆すには新しい IADR が要り、**本件の射程はそこではない**。
- 代わりに、**それらを現況の手順として引いている live 側**（`docs/how-to/` の手順書・
  `.github/workflows/` のコメント・`src/README.md` / `templates/unit-template/README.md`）を直す。
- **日付つき追記で「当時の測定」と明示済みのブロックは触らない** ——
  `docs/how-to/adr-supersede-citation-annex.md` §1・`plan-id-range-history-annex.md` §3・
  `commit-message-rules-annex.md`「旧記述（黙って消さない）」の 3 箇所は、いずれも 2026-08-21 に
  是正の追記が入っている。**本文は点時点記録として残すのが本リポジトリの運用**である。

### 2. `submodules: recursive` を避ける理由は、消さずに置き換える

**理由ごと消さない。** [IADR-0065](./IADR-0065_public-unit-submodule-ci-fetch-no-token.md) 決定 1 が
挙げた 2 つの理由のうち、**「トップレベルの private `planning` を対象外にする」だけが失効**しており、
残る半分はそのまま生きている。live 文書には次の形で置く。

1. **`src/*` 限定**: ユニットの実体が要るのはビルド・テスト・機械検査のジョブだけである。
   `src/` の外へ submodule を足したとき、それらのジョブが不要な取得と権限要求を抱え込まない。
   `checkout` の `submodules:` には**取得対象を選ぶ手段が無い**。
2. **非再帰**: ユニットが内包する入れ子 submodule を辿らない。入れ子が private だと既定
   `GITHUB_TOKEN` では read できず、**checkout ステップごと `Repository not found` で落ちる**
   （ジョブ本体に入る前に失敗するため原因が読み取りにくい）。

### 3. 前提を失った変異試験は、フィクスチャへ置き換えて生かす（消さない）

`scripts/scripts.repo.test.js` の「develop 時点の workflow へ当てると空白区切りを検出する（変異試験）」は、
入力を `git show origin/develop:.github/workflows/doc-links-planning.yml` から取っていた。同ワークフローは
撤去済みであり、`git show` は必ず失敗して `catch { return; }` に落ちる —— **何も検査しないまま緑を返す
死んだ試験**になっていた。

**削除せず、当時の形を写したフィクスチャへ入力を差し替える。** 捕まえたい形（`.md` 外の YAML コメントに
現れる空白区切りのクロスリポ参照）は変わっておらず、**試験の意図はまだ有効**だからである。
陽性対照（違反形を検出する）に加え、**陰性対照**（修飾つき・空白なしの正しい形を検出しない）を対で置く。

**ただし「フィクスチャへ置き換えて生かす」は無条件ではない。** 同じ走査で見つかった 2 本
——「issue テンプレートはキットとバイト一致（分類 A）」と「状態欄の更新主体をキットが定めている」——は
**撤去する**。入力が `planning/tools/impl-handoff-kit/…` にあり、常に「未 populate のため省略」で
抜けていた点は同じだが、**`CLAUDE.md` が「kit 同期のバイト一致検査は退役済み。復活させない」
「kit との乖離は受容する」（ADR-0048 決定 6）と明示的に定めている**ためである。
**判定の分かれ目は「試験の意図がまだ有効か」であり、「入力が取れるか」ではない。**

### 4. 母集合は「誤りの側の語」で引く。正当な参照を陽性対照として使う

`planning` の素引き（943 ファイル）は母集合にならない。次の 9 軸で引き、
**軸 H（`project-planning` の正当な参照 80 行）を陽性対照**として「巻き込んでいないこと」を示す。

同一行の `planning` ∧ `submodule` 系／`submodules:\s*(recursive|true)`／退役資産名
（`PLANNING_REPO_TOKEN`・`doc-links-planning`・`--require-planning`・`planningPopulated`・`planning-pin`）／
`.gitmodules` の言及／パス形 `planning/`／`--recurse-submodules`・`submodule update --init`／
カタカナ `サブモジュール`／`project-planning`／**ファイル単位の共起**。

最後の軸（行またぎ）だけが `.github/workflows/codeql.yml` と `security.yml` を出した。
**軸を 1 本で終わらせない**（`traceability.repo.md` 規則 5）が実際に効いた。

## 理由

- **凍結記録を書き換えないのは記録保全のため**であり、[IADR-0228](./IADR-0228_planning-dependency-removal.md)
  の理由節と同じである。同時に、**凍結記録を「現況の手順」として引く live 側は直さないと実害が出る**
  —— 読者は live 側を手順書として使うからである。この 2 つは矛盾しない。
- **理由の置き換えを決定として残すのは、次の是正で「理由ごと消す」を防ぐため**である。
  失効した理由を削除するだけだと、規範（`recursive` を使わない）が根拠なしで残り、次に読む人が覆す。
- **死んだ試験を消さずに生かすのは、検査の総数ではなく検査の中身を守るため**である。
  消すと「その型の事故を捕まえる試験が無くなった」ことが差分から読み取れない。

## 結果

- 是正 15 箇所（`docs/how-to/adding-a-unit-submodule.md` / `local-development.md` / `session-handoff.md`、
  `docs/operations/llm-model-pin-runbook.md`、`README.md`、`.github/workflows/codeql.yml` /
  `security.yml` / `claude-code-review.yml`、`src/README.md`、`templates/unit-template/README.md`、
  `scripts/check-test-traceability.js` / `check-reading-budget.js` / `scripts.repo.test.js`）。
- `.github/workflows/` の変更は**コメント行のみ**であり、`on:` / `jobs` / `steps` を動かさない。
  起動条件・必須チェック名は変わらない。
- `scripts/check-doc-links.js` の planning 分岐は**実装として存在しない**ことを実測した
  （`--require-planning` の引数解釈も `planningPopulated` の定義も無く、残っているのは退役を過去形で
  述べるコメント 1 行）。`CLAUDE.md`「planning 依存の検査器は退役させた。復活させない」と一致するため
  **追加の退役作業は不要**とし、コメントは根拠として残す。
- **積み残し（別 issue へ切り出す）**: (a) パス形 `planning/docs/glossary.md`・`planning/projects/…`
  の残存（`docs/functional/` `docs/screens/` `src/**` `scripts/**` に 20 行超）。submodule であるとは
  述べていないが、`planning/` 前置は submodule マウント時代の名残である。(b) `claude-coding.yml` /
  `claude-code-review.yml` の `git -C src/ai-stock-trading/planning …` 許可 5 エントリ×2。AST の現 pin
  `0844b584` に `planning/` は無いが、**AST 側の pin 事情であって計画 `ADR-0048` 決定 2 の射程外**であり、
  許可リストは 3 系統同期と `check-ai-workflow-config.js` の非対称検査に縛られている。

## 関連

- Supersedes: なし（[IADR-0058](./IADR-0058_doc-links-planning-submodule-ci.md) /
  [IADR-0060](./IADR-0060_submodule-unit-operations.md) /
  [IADR-0065](./IADR-0065_public-unit-submodule-ci-fetch-no-token.md) を `Superseded` にはしない。決定 1）
- Superseded by: なし
