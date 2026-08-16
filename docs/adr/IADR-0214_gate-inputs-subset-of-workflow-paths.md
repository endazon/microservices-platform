---
title: IADR-0214 ゲートが読むファイルは、そのゲートを走らせるワークフローの `paths:` にも載る（列挙ではなく検査器の実体から導く）
type: impl-adr
status: Accepted
related_ids:
  - NFR
  - ADR-0031
  - IADR-0060
  - IADR-0141
  - IADR-0147
  - IADR-0179
  - IADR-0183
  - IADR-0203
  - IADR-0209
  - IADR-0211
author: claude
created: 2026-08-16
updated: 2026-08-16
plan_refs:
  - "../../planning/projects/microservices-platform/02_requirements/01_requirements.md (NFR 表の射程注記: メタ作業は本表の対象外)"
  - "../../planning/projects/microservices-platform/06_technical/13_frontend-stack.md (採用技術一覧: Dead Code 検出 = Knip)"
  - "../../planning/docs/ai-implementation-workflow-guide.md"
---

# IADR-0214: ゲートの入力 ⊆ そのゲートを走らせるワークフローの `paths:`

- 状態: Accepted
- 日付: 2026-08-16
- 決定者: 実装担当（AI）／起票 = 波 7 末クロス監査

## 起点・関連

- 関連する計画書 ID: **`NFR`（無採番）** —— CI の起動条件という**工程の統制**であり、計画側の
  非機能要件表（`NFR-01`〜`NFR-27`）に当たる番号が無い
  （[IADR-0179](./IADR-0179_unnumbered-nfr-for-meta-work.md) 決定 1。
  **無いことは「実装側で採番してよい」ではない**＝同 決定 2）。**環流しない。**
  ゲートそのものの根拠は `ADR-0031`（Dead Code 検出 = Knip）＋
  [IADR-0211](./IADR-0211_knip-scope-and-unused-ratchet.md)。
- 作業仕様書: [`docs/specs/20260816_wave7-audit-followup.md`](../specs/20260816_wave7-audit-followup.md)
- 関連 IADR:
  - [IADR-0209](./IADR-0209_vitest-include-subset-of-frontend-tests-paths.md)
    （**同族の不変条件**。`test.include` ⊆ `frontend-tests.yml` の `paths:`。対象が違う＝下の決定 1）
  - [IADR-0211](./IADR-0211_knip-scope-and-unused-ratchet.md)（**本 ADR が起動条件を直すゲートの本体**。
    同 決定 4 の「`paths:` は `src/knip.jsonc` の 1 行だけ足す」を本 ADR が改める）
  - [IADR-0183](./IADR-0183_false-green-warning-on-worktree-state.md)（**偽の緑**を返す条件は fail へ倒す）
  - [IADR-0147](./IADR-0147_chunk-rule-presence-check.md)（**検出漏れは開示してよいが偽陽性は塞ぐ**）
  - [IADR-0141](./IADR-0141_audit-rounds-and-population-drawing.md)（同じ値を 2 箇所に置くと片方が腐る）

## コンテキストと課題

`.github/workflows/frontend.yml` は Knip のラチェット
（`node scripts/check-knip.js --require`）を走らせる**唯一の**ジョブである。ところが同ファイルの
`paths:` には `src/knip.jsonc` だけが在り、**床 `scripts/knip-baseline.json` も検査器本体
`scripts/check-knip.js` も無かった**。すなわち **床だけを緩める PR では、ゲートが 1 度も起動しない。**

これは実測で再現する。床の `counts.exports` を 18 → 60 に緩めたとき:

| 実行 | 結果 |
| --- | --- |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`（`ci.yml` の `scripts-tests`。**全 PR で起動**） | **EXIT=0 / 636 tests passed**（素通り） |
| `node scripts/check-knip.js --require`（ゲート本体。`frontend.yml` でしか走らない） | **EXIT=1**（検出する） |

さらに `frontend.yml` のコメントは

> 床 `scripts/knip-baseline.json` 側は `ci.yml` の `scripts-tests` が見る。

と書いていたが、**これは誤りである**。`scripts-tests` が走らせるのは `check-knip.js --self-test` で、
その中身は**構造検査だけ**である —— `aggregate` / `evaluate` の純関数の振る舞い、
`.gitmodules` と `knip.jsonc` の `ignoreWorkspaces` の突合、床の区分名が既知であること、
床が 0 件でないこと。**床と実測値（実際に Knip を走らせた結果）の突合は 1 度も行わない。**

### 「`paths:` の取りこぼしで検査が静かに素通りする」型は 5 件目である

| # | issue | 着地 | 内容 |
| --- | --- | --- | --- |
| 1 | #562 | `ce96eb81`（2026-08-08） | 整形ゲートの設定が `paths:` に無く、単独変更で CI が走らなかった |
| 2 | #558 | `4dbd5010`（2026-08-10） | 契約と生成の設定が `frontend-tests.yml` に無かった |
| 3 | #747 | `3cf2437a`（2026-08-15） | AST submodule の gitlink が一致せず、**3 回の bump が素通り**した |
| 4 | #801 | `49ec8e32`（2026-08-16） | `test.include` が拾う雛形を `frontend-tests.yml` の `paths:` が拾わなかった |
| 5 | **本件** | `f423ca4e`（2026-08-16） | **Knip ゲートの入力**（床・検査器本体）が `frontend.yml` の `paths:` に無い |

**5 件目は 4 件目を直したのと同じ波（波 7）で作り込んだ。**
`CLAUDE.md`「検査器・規約の追加は同型の事故が 2 回起きたら」の条件はとうに超えている。

**既に置いた 2 本の検査器は、いずれも本件を素通りする。**

- #747 の検査器は `.gitmodules` の **gitlink** しか見ない。
- #801 の検査器（IADR-0209）は `vitest` の **`test.include`** しか見ない。`knip-baseline.json` は
  `include` に一致しない。

## 検討した選択肢

### 軸 A: 何を不変条件にするか

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A1（採用）** | **ゲートが読むファイル ⊆ そのゲートを走らせるワークフローの `paths:`** | **入力と起動条件を直接結ぶ。**IADR-0209 と同族で、読み方が既に知られている |
| A2 | `paths:` へ 2 行足して終わる（検査器を置かない） | **採らない。**同型が 4 回起きており、5 回目も同じ手順で再発する |
| A3 | `frontend.yml` の `paths:` を撤廃して常時起動にする | **採らない。**#705 が「`paths:` を持つこと自体は正しい運用」と決めており、両ユニットの CI 独立（`CLAUDE.md` §CI）を壊す |

### 軸 B: 入力ファイルの集合をどう決めるか

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **B1（採用）** | **検査器のソースから静的に導く**（`path.join` / `path.resolve` のリテラル式を解決） | 検査器を書き換えれば追随が自動で要求される。**列挙を書き写さない** |
| B2 | テスト側に「検査器 → 入力」の表をハードコードする | **採らない。**[IADR-0141](./IADR-0141_audit-rounds-and-population-drawing.md) の「同じ値を 2 箇所に置くと片方が腐る」に正面から反する |
| B3 | 検査器を実際に走らせ、`strace` 等で実 open を観測する | **採らない。**本リポの検査器は外部依存ゼロの単体テストから呼ばれる前提であり、OS 依存の観測を持ち込まない |

### 軸 C: 対象ゲートの一覧をどう決めるか

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **C1（採用）** | **ワークフローの `run:` から `node scripts/<name>.js` を全部拾う** | ゲートを 1 本足したら自動で射程に入る |
| C2 | `check-knip.js` だけを対象にする | **採らない**（下の決定 5） |

## 決定

### 1. 不変条件は「**ゲートが読むファイル ⊆ そのゲートを走らせるワークフローの `paths:`**」（軸 A = A1）

[IADR-0209](./IADR-0209_vitest-include-subset-of-frontend-tests-paths.md) と**同じ族だが対象が違う**。
あちらは**走らせる対象**（テストファイル）を見る。本 ADR は**検査器が読む入力**
（床・設定・検査器本体）を見る。両者は交わらない —— `knip-baseline.json` は `test.include` に
一致せず、雛形のテストは `check-knip.js` の `path.join` に現れない。

**「そのゲートを走らせるワークフロー」以外は見ない。** `frontend-tests.yml` は
`pnpm run test:coverage` 1 本だけで `node scripts/*.js` のゲートを持たないため、本不変条件の
対象にならない。IADR-0209 決定 1 の「**対称性を検査にすると、理由つきの非対称 4 件を誤検出する**」と
同じ理由で、2 本のワークフローを揃えにいかない。

### 2. 入力ファイルは**検査器の実体から導く**。列挙を書き写さない（軸 B = B1）

各検査器のソースを静的に読み、`path.join(...)` / `path.resolve(...)` の
**文字列リテラルと既知の定数だけで組まれた式**を解決してリポジトリ相対パスへ落とし、
**実在するファイルだけ**を残す。基点は `__dirname`（= `scripts`）で、
`const NAME = path.join(...)` は解決結果を記号表へ入れ、後続の式から参照できるようにする
（`REPO_ROOT` → `SRC_DIR` → `KNIP_CONFIG_PATH` の連鎖を辿るために要る）。

**検査器自身のパスも常に入力に含める。** 本体を書き換える PR でゲートが起動しないのは同じ穴である。

実測（本決定の抽出結果。`frontend.yml` が走らせる 4 本すべて）:

| 検査器 | 導出されたリポジトリ内ファイル |
| --- | --- |
| `check-knip.js` | `.gitmodules` / `scripts/check-knip.js` / `scripts/knip-baseline.json` / `src/knip.jsonc` / `src/package.json` |
| `check-chunk-budget.js` | `scripts/check-chunk-budget.js` / `scripts/chunk-budget-baseline.json` / `src/platform/frontend/vite.config.ts` |
| `check-static-egress.js` | `scripts/check-static-egress.js`（走査先は実行時引数で決まるため静的には見えない） |
| `check-i18n-catalogs.js` | `scripts/check-i18n-catalogs.js` / `src/lingui.config.ts` |

### 3. **検出しないことを明記する**（本検査は網羅ではない）

- **`require()` の依存グラフは辿らない。** 辿ると `scripts/lib/ci-annotate.js` のような共有ライブラリを
  引き込むが、それらは壊れれば**例外で落ちる**ので「静かに素通りする」型ではない。
  共有ライブラリの回帰は `ci.yml` の `scripts-tests`（各検査器の `--self-test`）が見ている。
- **実行時引数で決まる入力は見えない**（`check-static-egress.js --require <dist>` の走査先など）。
- **変数・テンプレートリテラルで組まれたパスは解決できず、黙って落ちる。**
  [IADR-0147](./IADR-0147_chunk-rule-presence-check.md) と同じ判断軸——**検出漏れは開示してよいが、
  偽陽性は塞ぐ**。解決できない式を推測で埋めると、存在しないファイルを `paths:` へ要求してしまう。

### 4. **fail-closed の門を 3 つ置く**（[IADR-0183](./IADR-0183_false-green-warning-on-worktree-state.md)）

① ワークフローから `node scripts/*.js` のゲートを **1 件も取れない**
② 検査器の本文に `path.join(` / `path.resolve(` が在るのに **式を 1 件も切り出せない**
③ `paths:` が読めない／0 件

いずれも **throw** する。**抽出が腐ったときに「0 件検査して緑」を返さない。**
とくに ② は、決定 3 が「解決できない式は黙って落とす」と定めた**代償**である ——
全滅（＝入力が検査器自身だけ）を緑にしない。

### 5. 対象ゲートは**ワークフローから導き**、`check-knip.js` だけに絞らない（軸 C = C1）

監査が挙げたのは Knip だけだが、**同じ抽出を残り 3 本へ当てると同型の穴がその場で見える** ——
`scripts/chunk-budget-baseline.json` を緩める PR、`check-static-egress.js` / `check-i18n-catalogs.js`
本体を書き換える PR も、いずれも `frontend.yml` を起動しなかった。
片方だけ塞ぐと「**機械が同型を挙げているのに人が 2 件だけ直した**」記録が残る。

### 6. **push と pull_request を別々に見る**（片側だけ足す事故を止める）

違反メッセージは `frontend.yml: push.paths に "scripts/knip-baseline.json" が無い（scripts/check-knip.js の入力）`
の形で、**どちら側か・どのゲートの入力か**を名指しする。

### 7. 置き場所は `scripts/scripts.repo.test.js` の #747 / #801 節の隣

既存の `pathsOf` / `globToRegExp` を**再利用する**（重複実装しない。IADR-0209 決定 7 と同じ）。
`scripts/scripts.test.js` は**キット配布物・分類 A**であり触らない。

### 8. **必読規約（`CLAUDE.md` / `.claude/rules/`）は 0 バイト増**とする

余白は 1,070 B しかない（`check-reading-budget.js` の実測で 50,130 / 51,200）。
正本は本 ADR・作業仕様書・`frontend.yml` と検査器のコメントが持つ。

## 理由

- **ゲートの入力と起動条件は別々のファイルにあり、片方だけ直しても機械が何も言わない。**
  #493 は `paths:` へ `src/knip.jsonc` を足しながら、床と検査器本体を落とした。
  **人の注意で防ぐ形はこれで 5 回失敗している。**
- **列挙ではなく導出**にしたのは、`paths:` の側が手書きである以上、
  **せめて「何を書くべきか」は機械が言えるようにする**ためである。検査は「足し忘れ」を検出するが、
  自動では足さない（IADR-0209 と同じ限界）。
- **fail-open な検査器を新設しない。** 検査器を足す作業そのものが同じ穴を開けうる（#664 / PR #672）。
  だから抽出の全滅を throw にした。

## 結果

- 良い影響:
  - **床だけを緩める PR で Knip ゲートが起動する。** 同じ穴が残り 3 本のゲートでも塞がった。
  - 検査器へ新しい入力（設定・床）を足したとき、`paths:` の追随漏れが**その場で赤くなる**。
  - `frontend.yml` の誤ったコメント（「床は `scripts-tests` が見る」）が是正され、
    **`--self-test` の射程（構造検査のみ）**が明記された。
- 悪い影響・トレードオフ:
  - **`paths:` の側は依然として手で書く。**
  - **`frontend.yml` の起動頻度が上がる。** `scripts/check-*.js` を触る PR で
    フロント CI（build / e2e を含む）が回る。これは意図した代償である ——
    起動しないことが本 ADR の問題そのものだった。
  - **抽出は近似である**（決定 3）。`require()` も実行時引数も見ない。
  - **`.gitmodules` を `paths:` に載せた。** submodule の追加・削除・改名でフロント CI が回る。
    頻度は極めて低く、かつそのとき `knip.jsonc` の `ignoreWorkspaces` の追随が要る場面である。
- フォローアップ:
  - **他のワークフロー（`ci.yml` / `security.yml` 等）へ本不変条件を広げるかは未判断。**
    それらは `paths:` を持たない（＝常時起動）ため、現時点で同型の穴は無い。
    **`paths:` を足す改定が出たら、そのとき本 ADR の射程を広げること。**
  - 本 ADR の検査は `paths:` の**過剰**（もう使われていないエントリ）は見ない。

## 関連

- Supersedes: なし（[IADR-0211](./IADR-0211_knip-scope-and-unused-ratchet.md) 決定 4 の
  「`paths:` は `src/knip.jsonc` の 1 行だけ足す」を改めるが、決定本体〔baseline ラチェット〕は
  生きているため Supersede ではなく**追補**の関係にある。IADR-0211 側に
  `［2026-08-16 追記 / 波 7 末クロス監査］` を入れて後継 ID を併記した）
- Superseded by: なし
