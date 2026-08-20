---
title: 作業仕様書 — check-planning-pin-freshness の逆方向比較を祖先判定で止め、比較元を出力する
type: spec
status: done
related_ids:
  - NFR
  - IADR-0119
  - IADR-0142
  - IADR-0170
  - IADR-0202
author: claude
created: 2026-08-15
updated: 2026-08-16
plan_refs:
  - planning:projects/microservices-platform/07_adr/README.md
related_specs:
  - "../adr/IADR-0170_planning-pin-freshness-detection.md"
  - "../adr/IADR-0202_pin-freshness-comparison-source.md"
  - "20260811_issue-589_planning-pin-freshness.md"
  - "20260815_planning-pin-ce9abd2.md"
---

# 作業仕様書: 逆方向比較を検出し、比較元を必ず出力する（#749）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（**NFR**。実装作業の統制・検知装備）
- ユースケース（UC）/ 画面（SC）: なし
- 関連 ADR: [IADR-0170](../adr/IADR-0170_planning-pin-freshness-detection.md)（本検査器の設計。fail-open ／
  「検査していない」と「乖離なし」を読み分ける）、[IADR-0119](../adr/IADR-0119_fr17-21-hold-until-adr-fixed.md) ／
  [IADR-0142](../adr/IADR-0142_fr19-20-scoped-release-by-overturn-range.md)（着手条件）、
  本作業で新設する **[IADR-0202](../adr/IADR-0202_pin-freshness-comparison-source.md)**（案 A の採否）
- 計画書リンク: `planning/projects/microservices-platform/07_adr/README.md`

## 目的・背景

**#749**: `scripts/check-planning-pin-freshness.js` が、計画側に着手可否に効く変更（`ADR-0046` の新設・
`06_technical/09_datasource-connectors.md` の新節）が入っている状態で

```console
[check-planning-pin-freshness] pin は古いですが、着手可否に効く変更はありません（66 件の差分はすべて draft / tools / 索引）。
exit=0
```

と報告した。原因は比較対象である。同スクリプトは submodule 内の `origin/HEAD` / `origin/main` /
`origin/master` を解決するが、**その `origin` は GitHub ではなく隣接クローン `/home/user/project-planning`
を指しており、誰も更新しないため pin より後ろにあった**。結果、`git diff <新しい pin> <古い ref>` という
**逆方向の比較**になり、出てきた 66 件は「pin にあって古い ref に無いもの」＝ `draft` / `tools` / 索引だった。
**分類器は正しく、入力が壊れていた。**

本検査器は `scripts/setup.sh`（SessionStart hook）から毎セッション呼ばれるため、
**「効く変更はありません」が毎回目に入り、人も AI もそれを見て pin を据え置く。**

## 対象範囲

- **対象**
  1. **案 B**: 比較元と pin の**祖先関係**を見て、比較元が pin より後ろ（または分岐）なら
     **「乖離なし」と報告しない**（`scripts/check-planning-pin-freshness.js`）。
  2. **比較元をどこから取ったかを全経路の出力に含める**（受け入れ基準 3）。
  3. 回帰テスト（fixture）＋**変異試験**を `scripts/scripts.repo.test.js` へ追加。
  4. 夜間ワークフローの気付き導線を「壊れた比較」にも通す（`.github/workflows/planning-pin-freshness.yml`）。
  5. **案 A（比較前にネットワーク fetch する）の採否を IADR-0202 に落とす**（実装はしない）。
- **対象外**
  - 案 A の実装（ネットワーク fetch）。SessionStart hook をネットワーク依存にする判断であり、IADR で決める。
  - キット版（`planning/tools/impl-handoff-kit/repo-template/scripts/check-planning-pin-freshness.js`）との
    539 行差分の突合・乗り換え。本ファイルは分類 B（本リポが originate）であり、**キットへの環流は別 issue**。
  - `scripts/scripts.test.js`（分類 A・キットとバイト一致）の変更。

## 母集合（自分で引き直した結果）

**「submodule の位置関係を比較している箇所」を、誤りの側の文字列で走査した。** issue 本文の一覧は転記していない。
走査はいずれも `--exclude-dir` で `.git` / `node_modules` / `planning` / `src/ai-stock-trading` を外し、
**拡張子で絞らず**（規則 3）、**パスで除外**した（規則 4）。

| 軸 | 走査コマンド（要点） | 件数 | 判定 |
| --- | --- | --- | --- |
| 1 | `grep -rn "origin/HEAD\|origin/main\|origin/master\|'origin'\|\"origin\""` | 20 | 該当 **1**（本ファイル `:187,:192`） |
| 2 | `grep -rn "ls-tree\|gitlink\|submodule status\|rev-parse\|merge-base\|FETCH_HEAD"` | 46 | 該当 **1**（本ファイル `:172`） |
| 3 | `scripts/*.js` `scripts/lib/*.js` `.claude/hooks/*.js` のうち子プロセスを起こすものを列挙し `-C` の引数を見る | 10 ファイル | 該当 **1** |
| 4 | `grep -rn "pin が古い\|pin を進め\|先端\|upstream"` | 11 | 該当 **1**（＋呼び出し側 2） |
| 5 | `grep -rn "pin-freshness"`（呼び出し側の洗い出し） | — | `scripts/setup.sh` / `.github/workflows/planning-pin-freshness.yml` |
| 6 | `grep -rn "diff', '--name-only\|diff --name-only"` | 4 | 該当 **1** |

**除外したものと理由**（黙って除外しない。規則 6）

- `scripts/scripts.test.js:36-39` / `.claude/hooks/check-impl.js:15`（`BASE_CANDIDATES`）:
  **自リポの**統合ブランチを解決する。submodule の位置比較ではない。**かつ前者は分類 A で変更禁止。**
- `scripts/check-doc-updated.js:166,189` / `scripts/check-landed-subjects.js:172,178` / `scripts/scripts.test.js:591`:
  `merge-base` を使うが**自リポ内**であり、`base...HEAD` の向きは構成上固定される（逆転し得ない）。
- `scripts/check-cross-repo-refs.js:340` / `scripts/check-plan-id-qualification.js:124` /
  `scripts/scripts.repo.test.js:4019,5216,5265`: submodule を `:!planning` で**除外**しているだけで、
  submodule 内で git を実行しない。
- `scripts/check-action-versions.js:153` / `scripts/compose-up.sh:15` / `deploy/**`:
  自リポの ref・短縮 SHA の取得。比較ではない。
- `.github/workflows/claude-*.yml` / `feedback/**` / `docs/specs/**` / `docs/adr/IADR-0170` / `CHANGELOG.md`:
  許可リストの列挙・過去の記録。**確定済みの記録は書き換えない**（`.claude/rules/traceability.repo.md`）。
- **`src/ai-stock-trading`（AST）submodule の鮮度検査器は存在しない**（軸 2・3 で 0 件）。
  #747 は同じ submodule の話だが「フロント CI の `paths:` に掛からない」型であり、位置の逆比較ではない。
  **AST 用の検査器を本作業で新設しない**（計画外の追加）。

**結論: 同型の誤りは `scripts/check-planning-pin-freshness.js` 1 件だけである。**

## 設計

### 1. 比較元の解決に「どこから取ったか」を持たせる

`remoteHead()`（commit 文字列だけを返す）を **`resolveComparisonSource()`** へ置き換え、
`{ commit, ref, remoteUrl, fetch: 'ok'|'failed'|'skipped' }` を返す。`describeSource()`（純関数）が
1 行の説明文を組み立て、**OK・乖離・壊れた比較のすべての経路で出力する。**

```
比較元: planning の origin/main = 5e53b9d（remote origin = https://github.com/endazon/project-planning.git / fetch 成功）
```

**`origin` が URL ではなくローカルパスなら、その旨を必ず添える**（#749 の根本原因を出力から読めるようにする）。

### 2. 祖先判定（案 B）

`git merge-base --is-ancestor` を両向きに引き、純関数 `classifyRelation()` で位置関係を決める。

| 関係 | 意味 | 挙動 |
| --- | --- | --- |
| `same` | pin == 比較元 | 従来どおり OK（＋比較元） |
| `forward` | pin が比較元の祖先 | **正しい向き**。従来どおり分類して報告 |
| `reverse` | 比較元が pin の祖先 | **比較が壊れている。「効く変更はありません」と報告しない** |
| `diverged` | 双方向とも祖先でない | 同上（比較元が別系統を指している） |
| `unknown` | 判定できない（浅いクローン等） | 従来どおり続行するが、**向きを確認できなかった旨を出力へ添える** |

`reverse` / `diverged` は **`warn()` で注釈を出し exit 0**（fail-open は維持。受け入れ基準 2）。
`GITHUB_OUTPUT` へ `comparison=reverse` 等を出し、夜間ワークフローが**別タイトルの issue** を立てる。

### 3. テスト可能性のための `--root`

fixture に対して**プロセスとして**走らせるため、`--root <path>` でリポジトリルートを差し替えられるようにする
（既定は従来どおり `__dirname/..`）。**これが無いと end-to-end の回帰テストが書けない**
（`REPO_ROOT` が定数で、gitlink の読み取りは実際の git リポジトリを要する）。

## 受け入れ基準

- [x] **今回の状況を再現する回帰テスト**（pin より後ろの ref を掴ませたとき、緑を返さない）
- [x] オフライン／submodule 未 populate では従来どおり fail-open（**CI やセッション開始を止めない**。fixture でも exit 0 を assert）
- [x] **「比較対象をどこから取ったか」を出力に含める**（ref・commit・remote URL・fetch の成否。ローカルパス注記つき）
- [x] 案 A の採否が [IADR-0202](../adr/IADR-0202_pin-freshness-comparison-source.md) に残っている（採らない）
- [x] 変異試験: 祖先判定を外した複製は同じ fixture で緑になる（＝門が効いていることの実測）

### 検証の証跡（2026-08-15）

| コマンド | 結果 |
| --- | --- |
| `node scripts/check-planning-pin-freshness.js --self-test` | exit 0（28 件。うち #749 の向き 6 件・比較元 7 件） |
| `node scripts/check-planning-pin-freshness.js` | exit 0（本 worktree は未 populate → 「検査していません」） |
| `node scripts/check-planning-pin-freshness.js --root <populate 済みツリー> --no-fetch` | exit 0。`比較元: planning の origin/HEAD = 5e53b9d（…）` を出力 |
| `node scripts/scripts.test.js` / `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 530 tests passed |
| `node scripts/check-adr-numbering.js` | OK（`IADR-0202` を追加後も欠番なし・索引と双方向一致） |

**変異試験の実測**（同一 fixture・pin = B / 比較元 = A）

| 版 | 出力 |
| --- | --- |
| **修正前相当**（`const relation = relationOf(...)` を `RELATION.FORWARD` へ置換） | `pin は古いですが、着手可否に効く変更はありません（1 件の差分はすべて draft / tools / 索引）。` ＝ **緑** |
| **修正後** | `比較できていません: 比較元が pin より後ろにあります（逆方向の比較）。` ＝ **緑を返さない** |

## テスト方針

`scripts/scripts.repo.test.js`（companion。`scripts.test.js` は分類 A のため触らない）へ追加する。

1. **純関数**: `classifyRelation` の 5 分岐、`describeSource` の 4 経路（ローカルパス注記を含む）。
2. **fixture（end-to-end）**: 一時ディレクトリに planning 上流リポ（A → B → C）と実装リポ 2 本を作る。
   - **逆方向**: pin = B、`origin/HEAD` = A（B は `draft/x.md` を足すだけ）
     → **修正後は「着手可否に効く変更はありません」を出さない**。
   - **正方向**: pin = A、`origin/HEAD` = C（C は ADR を `Proposed → Accepted` にする）
     → 従来どおり `adr-accepted` を鳴らす（**絞りすぎていないことの側**）。
3. **変異試験**: 検査器を一時ディレクトリへ複製し、祖先判定の 1 行を `'forward'` 固定へ書き換えて
   同じ逆方向 fixture へ当てる。**複製は緑（「効く変更はありません」）になること**を実測で示す。
   変異が当たったこと（複製 ≠ 原本）も assert する。

## 計画書との差異

- 差異: なし（NFR の実装装備。計画書の記述に反する点は無い）

## 実装中に判明した注意点

1. **警告文に「着手可否に効く変更はありません」という語を入れてはならない。** 初版は
   「〜とは報告しません」と否定形で書いたが、**回帰テストの `doesNotMatch` が自分の文言に当たった。**
   検査する側から見れば、否定形の引用と肯定の報告は区別できない。
   文言を「よって乖離の有無は判定できていません」へ変えた。
2. **変異点は呼び出し側で一意に取る。** `relationOf(root, pin, head)` だけで置換すると
   **関数宣言の側**（引数名が同じ）に当たり、複製が構文エラーで落ちる。
   **「落ちたから門が効いている」ではない** —— 置換対象の出現回数が 1 であることを assert した。
3. **`--root` を足した。** fixture をプロセスとして走らせるため。既定の挙動は変えていない。

## 未決事項

- **案 A（fetch）は実装しない。** IADR-0202 で「採らない」を確定させる。
- **キット版との突合**（issue コメント）は本作業の範囲外。分類 B のため、本リポの是正を先に確定させ、
  キットへの環流は別途起票する。

---

［2026-08-16 追記 / #773］**本作業が入れた実装は、同時に確定させた
[IADR-0202](../adr/IADR-0202_pin-freshness-comparison-source.md) 決定 4（案 A =
ネットワーク fetch は採らない）に反していた。** `resolveComparisonSource` の既定が
`{ fetch = true }` で、CLI は `--no-fetch` を opt-in にしていたため、**フラグを渡さない本番の
2 経路**（`scripts/setup.sh` ／ 夜間ワークフロー）が既定で `git fetch` していた。
フェーズ末のクロス監査が検出し、#773 で是正した（**決定は変えず、実装を決定へ合わせた**）。

**本文は当時の記録として書き換えない。** ただし上表「検証の証跡」の
`node scripts/check-planning-pin-freshness.js --root <populate 済みツリー> --no-fetch` は、
**現在は `--no-fetch` を受け付けない**（既定が fetch しないになり、fetch が `--fetch` の opt-in に
なったため）。**現在の同等形はフラグなしの `--root <populate 済みツリー>` である。**
是正の詳細は [20260815_issue-773](20260815_issue-773_pin-freshness-no-default-fetch.md)。
