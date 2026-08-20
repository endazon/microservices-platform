---
title: 作業仕様書 — companion（`scripts.repo.test.js`）の単体実行を沈黙の exit 0 から fail-fast へ変える（#797）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0115
  - IADR-0141
  - IADR-0183
  - IADR-0184
  - IADR-0192
  - IADR-0198
  - IADR-0208
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - planning:projects/microservices-platform/02_requirements/01_requirements.md (NFR: 運用・保守)
  - planning:docs/ai-implementation-workflow-guide.md
related_specs:
  - "../adr/IADR-0208_companion-direct-run-guard.md"
  - "../adr/IADR-0115_impl-handoff-kit-as-single-source.md"
  - "../adr/IADR-0192_kit-sync-classification-and-check.md"
  - "../adr/IADR-0198_kit-delta-fifth-kind-and-review-verdict.md"
  - "../../scripts/README.md"
---

# 作業仕様書: companion の単体実行を fail-fast にする（#797）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし
- ユースケース（UC）/ 画面（SC）: なし
- 非機能要件: **`NFR`（無採番）** —— 検査基盤・証跡の信頼性に関するメタ作業であり、計画側の
  非機能要件表に当たる番号が無い（`.claude/rules/traceability.md`「起点 ID の種別」の 2 の場合）。
  **この場合は環流しない。**
- 関連 ADR: 計画側に該当なし。実装側は [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)
  （キットを足場の単一情報源とする / companion 方式）・
  [IADR-0192](../adr/IADR-0192_kit-sync-classification-and-check.md)（同期分類と機械検査）・
  [IADR-0198](../adr/IADR-0198_kit-delta-fifth-kind-and-review-verdict.md)（キットが委ねる欄）。
- 計画書リンク: `planning/docs/ai-implementation-workflow-guide.md`（計画リポ）
  （フェーズ末監査は**証跡（実行コマンドと出力）必須**。本件はその証跡が空でも緑に見える穴である）

## 目的・背景

`scripts/scripts.repo.test.js` は **companion 形式**（`module.exports = ({ ok, assert }) => {...}`）で、
キット配布物 `scripts/scripts.test.js` から `require()` されて初めてテストが走る。
**単体で直接実行すると、代入が 1 回起きるだけで 1 件も検査しないまま出力ゼロで exit 0 になる。**

**沈黙の exit 0 は、全件通過の exit 0 と区別できない。** 実害が 2 件出ている。

1. **確定済み仕様書に空の証跡が残っている。**
   `docs/specs/20260807_issue-580_adr-records-drift.md:347` の「検証の実測結果」表に
   `` | `node scripts/scripts.repo.test.js` | **0** | `` の行があるが、**この行が示す検査は 1 件も走っていない**。
2. **#790 / #791 の作業中にも同じ形で exit 0 を得て緑と読みかけた**（2026-08-16）。気づいたのは
   変異試験が「変異させても exit 0」を示したためである。**変異試験を挟まなければ誤報告していた。**

着手時に実測で再現した（ガード投入前）。

```console
$ node scripts/scripts.repo.test.js ; echo "exit=$?"
exit=0            ← 出力ゼロ。1 件も検査していない
$ REQUIRE_REPO_TESTS=1 node scripts/scripts.repo.test.js ; echo "exit=$?"
exit=0            ← 環境変数を足しても同じ（受け口を通らないので効かない）
```

`CLAUDE.md`「検査器・規約の追加は**同型の事故が 2 回起きたら**」の条件を満たしている（2 件）。

## 対象範囲

- 対象:
  - `scripts/scripts.repo.test.js` に**直接実行のガード**を入れ、沈黙の exit 0 をうるさい exit 1 へ変える。
  - 同ファイルに**ガードそのものの回帰テスト**（子プロセスで自分を直接起動し exit 1 とメッセージを固定）。
  - キットへ環流すべきかの判定と根拠（[IADR-0208](../adr/IADR-0208_companion-direct-run-guard.md)）。
  - 確定済み仕様書に残った**空の証跡**の扱いの決定と記録（本書 §空の証跡の扱い）。
- 対象外:
  - **`scripts/scripts.test.js`**（キット配布物・分類 B で固有デルタは 1 か所のみ）。**触らない。**
  - **`CLAUDE.md` / `.claude/rules/`**（必読規約の余白は 1,000B 台。**正味 0 バイト増**とする）。
  - **`planning/`**（編集しない）。
  - **`docs/specs/20260807_issue-580_adr-records-drift.md` の本文**（確定済み `docs/specs/` へ
    後付け注記をしない規約。`.claude/rules/traceability.repo.md`）。
  - **誤った呼び出し形を文書から締め出す静的検査（issue 案 B）**。採らない（§設計 3）。
  - `feedback/` への環流記録の作成。**伝達と同時でなければ未送付 0 件のラチェットを割る**ため、
    草案は本書 §付録に置く（[IADR-0207](../adr/IADR-0207_pr-title-trailing-number-must-be-own.md) 決定 7 と同じ扱い）。

## 母集合の実測（`.claude/rules/traceability.repo.md`「是正・追随の母集合の取り方」）

**誤りの側**（= companion を単体で叩く形）から引いた。走査基準は `origin/develop` = `d63c3a6e`。
**`planning/`（submodule）だけをパスで除外し、拡張子・行フィルタでは絞っていない**（規則 3・4）。

| 軸 | 走査コマンド | 件数 |
| --- | --- | --- |
| 1 | `git grep -n -I "repo\.test\.js" -- . ':!planning'` | **284 行 / 127 ファイル**（言及の全体） |
| 2a | `git grep -n -I -E "node +[^ \`\"']*repo\.test\.js" -- . ':!planning'` | **1 行** |
| 2b | `git grep -n -I -E "[A-Z_]+=[^ ]+ +node +[^ ]*repo\.test\.js" -- . ':!planning'`（環境変数つき） | **0 行** |
| 2c | `git grep -n -I "scripts\.local\.test\.js"`（**旧名**。受け口は今も読み込む） | **10 行**（すべて履歴の記述。実行形は 0） |
| 3 | `git grep -n -I -E "repo\.test\.js" -- '*.sh' '*.yml' '*.yaml' '*.json' 'Makefile' '*.mk'` | **6 行**（すべてコメント／説明。実行形は 0） |
| 4 | 軸 1 の 284 行すべてについて、直前 24 文字の文脈を機械抽出して分類 | 実行形は **1 件のみ** |

**走査の時点**（キット規則 8。**自分の記録が母集合を動かす**）: 上の値は**本仕様書を追跡下へ入れる前**の
ものである（`git grep` は追跡下しか見ない）。本仕様書と IADR-0208・ガード・回帰テストを `git add` した
あとの実測は §検証の実測 に置き、**引き算を見せる**。

**軸 1 を 284 行そのまま分類した結果、誤った呼び出し形は 1 件だけである。**

| # | 箇所 | 判定 |
| --- | --- | --- |
| 1 | `docs/specs/20260807_issue-580_adr-records-drift.md:347` | **誤り**（空の証跡）。**確定済みのため書き換えない**（§空の証跡の扱い） |

**除外したものと理由**（規則 6。黙って落とさない）:

| 除外した群 | 件数 | 理由 |
| --- | --- | --- |
| Markdown リンク・インラインコードでの**ファイル名の言及** | 約 270 | 呼び出し形ではない（「companion に検査を置いた」等の記述） |
| **正しい入口を書いた記述**（`node scripts/scripts.test.js` / `REQUIRE_REPO_TESTS=1` 版） | 150 行 | 誤りではない |
| **単体で叩くなと明示した記述** 3 件（`20260816_issue-743_*.md:249` / `20260816_issue-793_*.md:274` / `20260816_wave6-audit-followup-session-handoff.md:113`） | 3 | **誤りではなく警告**。ガード投入後も残す |
| `.github/workflows/*.yml` のコメント 4 件・`scripts/adr-index-title-baseline.json` の 2 件 | 6 | 「companion が検査する」の説明であり呼び出し形ではない |
| `planning/`（submodule） | — | 本リポの母集合ではない（編集禁止） |

**別軸（規則 5）: 同じ「沈黙の exit 0」を起こす他のファイルが無いか。**
`scripts/` 配下の追跡下 `.js` のうち `require.main === module` を持たないものを全列挙した。

| ファイル | 直接実行したときの挙動 | 判定 |
| --- | --- | --- |
| `scripts/scripts.repo.test.js` | **沈黙の exit 0** | **本件の対象** |
| `scripts/scripts.test.js` | トップレベルで全件走る | 該当せず（キット配布物・触らない） |
| `scripts/k8s-local-up.test.js` | トップレベルで走り `✓ N tests passed` を出す | 該当せず |
| `scripts/gen-openapi-skeleton.js` / `scripts/validate-pipeline-config.js` | 末尾で `main()` を無条件に呼ぶ | 該当せず |
| `scripts/lib/ci-annotate.js` | 純ライブラリ。直接実行は無言 exit 0 | **対象外**。`*.test.js` ではなく**テストの証跡と誤読されない**。かつ**分類 A（キットとバイト一致）**であり触れない |

**companion 形式のファイルの実測**（★ の判断材料）:

| ファイル | 形式 | 直接実行で沈黙するか |
| --- | --- | --- |
| `scripts/scripts.repo.test.js` | **実行可能な JS の companion** | **する** |
| `scripts/action-versions.repo.json` | JSON データの companion | しない（実行対象ではない） |
| `.claude/rules/traceability.repo.md` | Markdown の companion | しない（同上） |

**実行可能な companion は本リポに 1 本しか無い。**（`git grep -l -E "^module\.exports = \(\{ *ok"` の
ヒットは `scripts/scripts.repo.test.js` と、その雛形を載せている `scripts/README.md` の 2 件のみ。）

## 設計

### 1. ガード（`scripts/scripts.repo.test.js`）

docstring の直後・`module.exports` の前に置く。**`require()` 経由では `require.main` が
本ファイルにならないため、受け口からの読み込みは一切変わらない。**

```js
if (require.main === module) {
  process.stderr.write(/* 使い方 */);
  process.exit(1);
}
```

メッセージには**正しい入口を 2 行とも書く**（`node scripts/scripts.test.js` と
`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`）。「間違いだ」だけを言って
直し方を書かない失敗メッセージにしない（[IADR-0207](../adr/IADR-0207_pr-title-trailing-number-must-be-own.md) 決定 5 と同じ作法）。

### 2. ガードの回帰テスト（同ファイル内）

**ガードは、外れても誰も気づかない種類の変更である**（外れた状態＝元の沈黙）。
よって companion 内に回帰テストを置き、**正しい入口から**子プロセスで自分自身を直接起動して
`status === 1` と使用法メッセージを固定する。**これが変異試験「ガードを外すと沈黙へ戻る」を
CI で恒久化したもの**である。

### 3. 静的検査（issue 案 B）は採らない

**採らない。** 根拠は 3 つ。

1. **A が入れば実行時に必ず落ちる。** 誤った形を書いた人は、走らせた瞬間に exit 1 と入口を得る。
2. **誤った形は必ずインラインコード／コードフェンスの中に現れる。** 本リポの表記検査
   （`check-cross-repo-refs.js` 系）は**インラインコードを検査対象外と定義**しており、
   同じ土俵に乗らない。逆に対象に含めると、**「単体で叩かない」と警告している 3 件**と
   **本仕様書自身**を違反として上げる。**規約自身が反例を書けなくなる**型の失敗である。
3. `CLAUDE.md` の「検査器の追加は同型の事故が 2 回起きたら」は**検査器 1 本**の話であり、
   同じ 1 つの誤りに 2 本を重ねる根拠にはならない。**A ＋ その回帰テストで 2 段は足りている。**

### 4. キットへ環流すべきか（★ の判定）

**結論: ガードの実体は本リポに置く。ただし「companion はガードを持つ」という契約はキットへ環流する。**

| 論点 | 実測・根拠 |
| --- | --- |
| `scripts.repo.test.js` はキットの配布物か | **違う。** キット `repo-template/scripts/` の実ファイル一覧に `scripts.repo.test.js` は無い（`scripts.test.js` はある）。`kit-sync-classification.json` にも本ファイルの項目は無い —— 同表は**キット側に在るファイル**を A / B / C / notApplicable へ割り付ける表であり、本ファイルはキットに対応物を持たない |
| したがって分類は | **キットに対応物が無い本リポ固有の実体**である。issue 本文の「分類 B」は誤り（**issue の記述をそのまま採らず、着手時に表を引いて確かめた**） |
| ガードを書くことの同期コスト | **ゼロ。** バイト一致を保つべき相手が存在しない。固有デルタが増えるわけでもない |
| キットが持つべきものは何か | **穴を作っているのはキット側の契約である。** キットは `scripts.test.js` の受け口と `scripts/README.md` で「固有テストは companion に書け」と**全配布先へ指示**しており、配布先はそれぞれ自前の companion を書く。**穴は配布先の数だけ複製される** |
| キット側で持てる形 | (a) `scripts.test.js` の docstring と `scripts/README.md` の**雛形にガード行を含める**（以後の配布先は最初から持つ）／(b) **受け口が、読み込んだ companion がガードを持つかを検査する**（既存の「登録 0 件なら fail」「未追跡なら warning」と同じ列に並ぶ） |
| 本作業でキットを直せるか | **直せない。** `planning/` は編集禁止で、`scripts.test.js` / `scripts/README.md` はキット配布物である。よって**環流の草案を §付録に置く** |

**「同型の companion が他にもあるならキット側で持つほうが筋が良い」への回答**: 本リポの実行可能な
companion は **1 本**である（上の実測表）。**リポジトリ内の横展開の必要は無い。** 横展開が要るのは
**リポジトリを跨いだ方向**であり、それはファイルの配布ではなく**契約と検査**の環流で満たす。

## 受け入れ基準

- [x] `node scripts/scripts.repo.test.js` が **exit 1**、かつメッセージに正しい入口が書いてある
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.repo.test.js` も **exit 1**
- [x] `node scripts/scripts.test.js` が**従来どおり全件走って exit 0**（件数が減っていない）
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` も exit 0
- [x] **ガードを外すと単体実行が沈黙の exit 0 へ戻り、かつ回帰テストが exit 1 で落ちる**（変異試験）
- [x] 正しい呼び出し形が `scripts/README.md` の記述から一意に読める（**必読規約は 0 バイト増**）
- [x] キットへ環流すべきかを判定し、結論と根拠を本書と [IADR-0208](../adr/IADR-0208_companion-direct-run-guard.md) に書いた

## 空の証跡の扱い（issue 案 C）

`docs/specs/20260807_issue-580_adr-records-drift.md:347` の
`` | `node scripts/scripts.repo.test.js` | **0** | `` は、**検査が 1 件も走っていない空の証跡**である。

**決定: 当該行は書き換えない。記録は本書と IADR-0208 に残す。**

- 確定済み `docs/specs/` の本文へ後付け注記をしない規約に従う（`.claude/rules/traceability.repo.md`）。
  この規約は**当時の判断の記録としての価値**を守るためのものであり、**当時 exit 0 を得たこと自体は
  事実である**（誤っているのは「それを検査の証跡として読んだ」ほう）。
- **同じ表の 1 行上**（`:346`）に `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（274 tests）の
  行があり、**当該作業の検査そのものは正しい入口から実測されている**。つまり `:347` は
  **重複した余分な 1 行**であって、#580 の結論を無効化するものではない。この確認をもって
  「空の証跡が結論を汚染していないか」を判定した。
- **同型の再発は、行を消すことではなくガードで止める**（本作業）。

## 検証（[IADR-0183](../adr/IADR-0183_false-green-warning-on-worktree-state.md) の順序）

`git add -A` → 検査器 → コミット → HEAD を読む検査器（`check-doc-updated.js` / `check-commit-messages.js`）。

### 変異試験（4 方向。実測）

| # | 操作 | 期待 | **実測** |
| --- | --- | --- | --- |
| M1 | `node scripts/scripts.repo.test.js` | exit 1 ＋ 入口 | **exit 1**。stderr に `node scripts/scripts.test.js` と `REQUIRE_REPO_TESTS=1 …` の 2 行 |
| M2 | `REQUIRE_REPO_TESTS=1 node scripts/scripts.repo.test.js` | exit 1 | **exit 1**（同じメッセージ。環境変数では走らないことも止める） |
| M3 | `node scripts/scripts.test.js` | exit 0・件数が減らない | **exit 0** / **633 tests passed**（着手前 630 → 本作業の 3 件で +3） |
| M4 | `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | exit 0 | **exit 0** / **633 tests passed** |
| **M5** | **ガードを除去**して M1 と M3 を再走 | 沈黙へ戻り、回帰テストが落ちる | **M1 → exit 0・出力ゼロ（沈黙へ戻る）** ／ **M3 → exit 1**、`AssertionError: 単体実行が exit 0 を返した。沈黙の exit 0 は「全件通過」と区別できない（#797）` |

**M5 がガードの効いている証拠である。** 除去すると単体実行は元の沈黙へ戻り、同時に
正しい入口が赤くなる —— **ガードが外れたことを CI が検出する**。除去は退避した複製から復元し、
復元後に M1（exit 1）と M3（exit 0）を再確認した。

### 検査器の実測（`git add -A` 後）

| コマンド | exit code |
| --- | --- |
| `node scripts/scripts.test.js` | **0**（633 tests passed） |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | **0**（633 tests passed） |
| `node scripts/check-kit-sync.js` | **0** |
| `node scripts/check-doc-links.js` | **0** |
| `node scripts/check-cross-repo-refs.js` | **0** |
| `node scripts/check-plan-id-qualification.js` | **0** |
| `node scripts/check-doc-type-vocabulary.js` | **0** |
| `node scripts/check-adr-numbering.js` | **0** |
| `node scripts/check-reading-budget.js` | **0**（必読規約は**未変更**。正味 0 バイト増） |
| `node scripts/check-doc-updated.js`（コミット後） | 下表 |
| `node scripts/check-commit-messages.js`（コミット後） | 下表 |

**`planning` submodule は pin どおり populate してから走らせた。** 未 populate だと
`check-kit-sync` が throw して `scripts.test.js` の後続テストが 1 件も走らず、
**本件と同型の「沈黙で通る」**が起きる。

### 母集合の再測（キット規則 8 の引き算）

`git add -A` 後に軸 2a（`node …repo.test.js` の形）を引き直した。**引き算を見せる。**

| 内訳 | 件数 |
| --- | --- |
| 走査がそのまま返す数（`git grep --cached`） | **9** |
| － 本仕様書が自分で書いた分（誤例・変異試験の記述） | −6 |
| － [IADR-0208](../adr/IADR-0208_companion-direct-run-guard.md) が書いた分（同上） | −2 |
| **＝ 残る誤った呼び出し形** | **1**（`20260807_issue-580_adr-records-drift.md:347`。確定済みのため書き換えない） |

**着手前の 1 件から増減なし。** 増えた 8 件はすべて**ガードが exit 1 で受け止める形の引用**であり、
実行されれば入口つきで落ちる。

## 付録: キットへの環流（草案・未送付）

**本作業では `feedback/` へ置かない。** 未送付の環流記録を 0 件で固定するラチェット
（`check-feedback-dispatched.js`）があり、記録の作成と伝達は同時でなければならないためである。
伝達と同じ変更で `feedback/<日付>_kit-companion-direct-run-guard.md` へ移す。

- **件名案**: companion（`scripts.repo.test.js`）を単体で叩くと沈黙の exit 0 になり、
  検証の証跡として誤って記録される
- **事実**: キットは `scripts.test.js` の受け口と `scripts/README.md` で「固有テストは companion に書け」と
  全配布先へ指示する。配布先が書く companion は `module.exports = ({ ok, assert }) => {...}` のみで、
  **直接実行すると 1 件も走らず exit 0** になる。**沈黙の 0 は全件通過の 0 と区別できない。**
- **実害**（本リポの実測）: 確定済み仕様書 1 件に空の証跡が残った／別の作業で緑と読みかけた（変異試験で発覚）。
- **提案**:
  1. `scripts/README.md` と `scripts.test.js` docstring の**雛形にガードを含める**
     （`if (require.main === module) { /* 使い方を出して */ process.exit(1); }`）。
  2. 受け口 `loadCompanionTests()` の**検出表へ 1 行足す** —— 読み込んだ companion が
     直接実行のガードを持たなければ `warning:`。既存の「登録 0 件なら fail」「未追跡なら warning」
     と同じ列であり、**受け口が持てば全配布先が同時に守られる**。
  3. 実体（ガード本体）は各リポの companion に置く。**キットに companion の実ファイルは無い**ため、
     配布できるのは**雛形と検査**だけである。
- **本リポの先行実装**: #797 / [IADR-0208](../adr/IADR-0208_companion-direct-run-guard.md)。

## 参照

- [IADR-0208](../adr/IADR-0208_companion-direct-run-guard.md)（本作業の決定）
- [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)（キットを単一情報源とする / companion 方式）
- [IADR-0192](../adr/IADR-0192_kit-sync-classification-and-check.md)（同期分類と機械検査）
- [`scripts/README.md`](../../scripts/README.md)（companion の受け口の挙動表）
