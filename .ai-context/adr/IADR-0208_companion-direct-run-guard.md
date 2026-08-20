---
title: IADR-0208 companion（`scripts.repo.test.js`）の単体実行は沈黙の exit 0 ではなく exit 1 にする
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - IADR-0115
  - IADR-0141
  - IADR-0179
  - IADR-0183
  - IADR-0184
  - IADR-0192
  - IADR-0198
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)
  - planning:docs/ai-implementation-workflow-guide.md
---

# IADR-0208: companion の単体実行を沈黙の exit 0 から fail-fast へ変える

- 状態: Accepted
- 日付: 2026-08-16
- 決定者: 実装担当（AI）／起票 #797

## 起点・関連

- 関連する計画書 ID: **`NFR`（無採番）** —— 検査基盤・証跡の信頼性というメタ作業であり、計画側の
  非機能要件表に当たる番号が無い（`.claude/rules/traceability.md`「起点 ID の種別」の 2 の場合。
  [IADR-0179](./IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 1）。**環流しない。**
- 作業仕様書: [`docs/specs/20260816_issue-797_companion-direct-run-guard.md`](../specs/20260816_issue-797_companion-direct-run-guard.md)
- 関連 IADR: [IADR-0115](./IADR-0115_impl-handoff-kit-as-single-source.md)（キットを足場の単一情報源とし、
  固有テストは companion へ分離する）／[IADR-0183](./IADR-0183_false-green-warning-on-worktree-state.md)
  （**検査器が「偽の緑」を返す条件は警告する**）／[IADR-0192](./IADR-0192_kit-sync-classification-and-check.md)
  （同期分類）／[IADR-0198](./IADR-0198_kit-delta-fifth-kind-and-review-verdict.md)（キットが委ねる欄）

## コンテキストと課題

`scripts/scripts.repo.test.js` は companion 形式（`module.exports = ({ ok, assert }) => {...}`）で、
キット配布物 `scripts/scripts.test.js` の受け口 `loadCompanionTests()` から `require()` されて
初めてテストが走る。**単体で直接実行すると、代入が 1 回起きるだけで 1 件も検査せず exit 0 になる。**

```console
$ node scripts/scripts.repo.test.js ; echo "exit=$?"      # ガード投入前の実測
exit=0                                                     ← 出力ゼロ。検査していない
```

**沈黙の exit 0 は、全件通過の exit 0 と区別できない。** 実害が 2 件出ている。

| # | 事象 | 実体 |
| --- | --- | --- |
| 1 | **確定済み仕様書に空の証跡が残った** | `docs/specs/20260807_issue-580_adr-records-drift.md:347` の「検証の実測結果」表に `` `node scripts/scripts.repo.test.js` \| **0** `` の行がある。**この行が示す検査は 1 件も走っていない** |
| 2 | **#790 / #791 の作業中に緑と読みかけた**（2026-08-16） | 気づいたのは変異試験が「変異させても exit 0」を示したためである。**変異試験を挟まなければ誤報告していた** |

`CLAUDE.md`「検査器・規約の追加は**同型の事故が 2 回起きたら**」の条件を満たしている。

**これは「読み手が悪い」型ではない。** 受け口はキットが配り、companion は配布先が書く。
**キットの契約が、実行しても何も起きないファイルを各リポジトリに 1 本ずつ作らせている。**

## 検討した選択肢

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A（採用）** | companion の先頭に `require.main === module` のガードを置き、使い方を出して exit 1 | **沈黙の 0 をうるさい 1 へ変える。** `require()` 経由では `require.main` が一致しないため本来の経路は不変 |
| **A'（併せて採用）** | ガードの**回帰テストを companion 内に置く**（子プロセスで自分を直接起動し exit 1 とメッセージを固定） | ガードは外れても誰も気づかない（外れた状態＝元の沈黙）。**変異試験を CI で恒久化する** |
| B | 誤った呼び出し形を**文書から締め出す静的検査**を足す | **採らない**（下の決定 3） |
| C | ファイル名を `*.test.js` から変える（テストに見えなくする） | 受け口 `COMPANION` 定数がキット側で名前を固定しており、変えると読み込まれなくなる。**固有テストが黙って消える**——直そうとした事故そのものを起こす |
| D | 何もせず、規約で「単体で叩くな」と書く | 現状。既に 3 件の文書が警告しているのに 2 度混入した |

## 決定

1. **`scripts/scripts.repo.test.js` を直接実行したら exit 1 にする。**
   `require.main === module` のとき stderr に使い方を書いて `process.exit(1)` する。
   置き場所は docstring の直後・`module.exports` の前（**何もしないうちに落とす**）。
2. **失敗メッセージには正しい入口を 2 行とも書く** ——
   `node scripts/scripts.test.js` と `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`。
   **`REQUIRE_REPO_TESTS=1` を companion へ直接付けても効かない**（受け口が読む変数である）ため、
   その誤解ごと止める。「間違いだ」だけを言って直し方を書かない失敗メッセージにしない。
3. **誤った呼び出し形の静的検査は置かない。** 理由は 3 つ。
   (a) 決定 1 で実行時に必ず落ちる。(b) 誤った形は**常にインラインコード／コードフェンスの中**に
   現れるが、本リポの表記検査（`check-cross-repo-refs.js` 系）は**インラインコードを対象外と定義**して
   おり同じ土俵に乗らない。対象に含めれば「単体で叩くな」と警告している 3 件と本件の仕様書自身を
   違反として上げ、**規約が反例を書けなくなる**。(c) 「同型の事故が 2 回起きたら」は検査器 1 本の話であり、
   同じ 1 つの誤りに 2 本を重ねる根拠にはならない。
4. **ガード本体は本リポに置く。キットへは「契約と検査」を環流する。**
   実測: キット `repo-template/scripts/` に `scripts.repo.test.js` は**存在しない**（`scripts.test.js` はある）。
   `kit-sync-classification.json` にも項目が無い —— 同表は**キット側に在るファイル**を割り付ける表であり、
   本ファイルはキットに対応物を持たない。したがって**分類 B ではなく、キットに対応物が無い固有の実体**である
   （起票時の「分類 B」という前提は誤り）。**バイト一致を保つ相手が無く、同期コストはゼロ。**
   一方、穴を作っているのは**キット側の契約**（「固有テストは companion に書け」を全配布先へ指示する）で
   あり、**穴は配布先の数だけ複製される**。よってキットへは次の 2 点を環流する。
   - `scripts/README.md` と `scripts.test.js` docstring の**雛形にガード行を含める**
   - 受け口 `loadCompanionTests()` の**検出表へ 1 行足す**（ガードが無ければ `warning:`）。
     既存の「登録 0 件なら fail」「未追跡なら warning」と同じ列である
5. **リポジトリ内での横展開はしない。** 実行可能な companion は**本リポに 1 本しか無い**（実測）。
   `action-versions.repo.json`（JSON）・`.claude/rules/traceability.repo.md`（Markdown）は
   実行対象ではなく、沈黙の exit 0 を起こさない。`scripts/lib/ci-annotate.js` は直接実行すると
   無言 exit 0 だが、**`*.test.js` ではなくテストの証跡と誤読されない**うえ**分類 A** であり触れない。
6. **必読規約（`CLAUDE.md` / `.claude/rules/`）は 0 バイト増とする。** 余白は 1,000B 台しかない。
   正しい入口は `scripts/README.md` と本 IADR・作業仕様書が持つ。
7. **確定済み仕様書 `20260807_issue-580_adr-records-drift.md:347` の空の証跡は書き換えない。**
   確定済み `docs/specs/` の本文へ後付け注記をしない規約に従う。**当時 exit 0 を得たこと自体は事実**で
   あり、誤っているのは「それを検査の証跡として読んだ」ほうである。**同じ表の 1 行上に
   `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（274 tests）の行があり、#580 の検査は
   正しい入口から実測されている** —— `:347` は重複した余分な 1 行であって結論を汚染していない。
   この確認と事実の記録は本 IADR と作業仕様書に置く。

## 理由

- **止められる面はここしか無い。** 呼び出しは人（および AI）が手で打つものであり、
  CI のジョブ定義には現れない。**打った本人にその場で返す以外に接点が無い。**
- **「証跡が空でも緑に見える」は監査の前提を壊す。** 上流ガイドはフェーズ末監査に
  **証跡（実行コマンドと出力）必須**を課しているが、**出力ゼロ・exit 0 は「実行した」と
  区別がつかない**。IADR-0183 が扱った「偽の緑」と同じ族であり、同じ扱い（黙らせない）を採る。
  **「実行していないものを実行したと書いた記録」になる**という点では
  [IADR-0184](./IADR-0184_feedback-dispatch-checker-verbatim.md)（記録へ嘘を書かない）と同じ帰結であり、
  違うのは**書き手が嘘と気づけない**ことである —— だから書き手の側で止める。
- **ガードは回帰テストとセットでなければ意味が薄い。** 外れた状態が元の沈黙であるため、
  外れたことを誰も検出できない。**A' は A の付属ではなく、A を守る機構である。**

## 結果

- 良い影響:
  - 誤った入口が**その場で入口つきの exit 1** を返す。空の証跡が新たに生まれない。
  - ガードの存在が CI（`scripts-tests`）で恒久的に固定される（回帰テスト 3 件）。
- 悪い影響・トレードオフ:
  - **既に着地した空の証跡 1 件は残る**（決定 7）。読み手は本 IADR で経緯を知る。
  - **キットへの環流が未了のあいだ、他の実装リポジトリには同じ穴が残る**。
    配布点はキットに一本化する運用（上流ガイド §11）のため、本リポだけで直しても他リポには届かない。
- フォローアップ:
  - **キットへの環流（未了）**。草案は作業仕様書 §付録。**`feedback/` へは伝達と同じ変更で置く**
    （未送付 0 件のラチェット `check-feedback-dispatched.js` を割らないため。
    [IADR-0207](./IADR-0207_pr-title-trailing-number-must-be-own.md) 決定 7 と同じ扱い）。

## 関連

- Supersedes: なし
- Superseded by: なし
