---
title: 作業仕様書 — scripts/scripts.test.js をキット版へ追随させ、分類 A（バイト一致）へ戻す（#757）
type: spec
status: done
related_ids:
  - NFR
  - IADR-0115
  - IADR-0140
  - IADR-0170
  - IADR-0183
  - IADR-0192
author: claude
created: 2026-08-15
updated: 2026-08-15
plan_refs:
  - planning:tools/impl-handoff-kit/HOWTO.md
  - planning:tools/impl-handoff-kit/repo-template/scripts/scripts.test.js
---

# 仕様書: `scripts/scripts.test.js` のキット追随（#757）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（**NFR / 運用保守**。メタ作業のため計画の非機能要件表に当たる番号が無い。
  `.claude/rules/traceability.md` の「無採番 `NFR` を許す場合 2」に当たり、**環流しない**）
- ユースケース（UC）/ 画面（SC）: なし
- 関連 ADR: 計画側なし。実装ADR は [IADR-0115](../adr/IADR-0115_impl-handoff-kit-as-single-source.md)（キット同期の分類）/ [IADR-0192](../adr/IADR-0192_kit-sync-classification-and-check.md)（分類表と X の追跡先）/
  [IADR-0140](../adr/IADR-0140_cross-repo-issue-ref-checker.md)（クロスリポ参照検査）/ [IADR-0170](../adr/IADR-0170_planning-pin-freshness-detection.md)（pin 鮮度の検出）/ [IADR-0183](../adr/IADR-0183_false-green-warning-on-worktree-state.md)（worktree 状態の警告）
- 計画書リンク: `planning/tools/impl-handoff-kit/HOWTO.md`（計画リポ） §B-5
- 関連 issue: #757（本件）/ #755（分類 C 再判定・起点）/ #756（先行検査器 3 本の優劣判定。直前に着地）/
  #749（pin-freshness の逆方向比較。closed）/ planning#373・planning#374（#756 の環流先）
- 直前の判定結果: [`20260815_issue-756_kit-superiority-three-checkers.md`](20260815_issue-756_kit-superiority-three-checkers.md)

## 目的・背景

`scripts/scripts.repo.test.js` の冒頭は「本ファイルへ分離することで `scripts.test.js` を**キットと
バイト一致に保てる**」と述べており、`scripts.test.js` は設計上 **分類 A** であるべき配布物である。
ところが分類表では **B（X）** に置かれ、キット版（pin `4d6a7d6`）が先行している。X は環流債務の
測定値であり、放置は債務の隠蔽になる（[IADR-0192](../adr/IADR-0192_kit-sync-classification-and-check.md) 決定 2）。

本作業で `scripts.test.js` をキット版へ**バイト一致**まで追随させ、分類 A へ戻す。
テストは検査器の公開関数を前提にするため、**キット版テストが前提とする検査器の追随と対**で行う。

## 母集合（自分で引き直した結果）

**時点: 2026-08-15、ブランチ `chore/nfr-scripts-test-kit-parity`、base `6f9edef`、planning pin `4d6a7d6`。**
issue 本文の「やること」は母集合として採らず（[IADR-0141](../adr/IADR-0141_audit-rounds-and-population-drawing.md) 決定 1・規則 6）、次の 5 軸で引き直した。

### 軸 0 — 差分の大きさを自分で数え直す（他人の数えを転記しない）

```bash
KIT=planning/tools/impl-handoff-kit/repo-template/scripts/scripts.test.js
wc -c $KIT scripts/scripts.test.js          # kit 95,943 B / repo 56,314 B
git diff --no-index --numstat scripts/scripts.test.js $KIT
#   → 750  87  （repo → kit で +750 / −87）
diff -u scripts/scripts.test.js $KIT | wc -l   # 879 行・6 ハンク
```

**自分で数えた結果は「キット版が +750 / −87 行」で、issue 本文の「+750 行」と一致した。**
（−87 の側は「移動 ＋ 書き換え」であって純粋な削除ではない。軸 5 で全数確認した。）

### 軸 1 — キット版テストが `require` する自リポ内モジュール（全数）

```bash
grep -oE "require\('\./[^']+'\)" $KIT | sed "s/.*'\.\///;s/'.*//" | sort -u   # 15 件
```

15 件のうち、本リポに**実体が無い**もの: **`scripts/kit-sync-classification.example.json` の 1 件**
（分類表で `notApplicable` に置いていた）。残り 14 件は実在する。

### 軸 2 — キット版テストが**子プロセスとして実走する**スクリプト（全数）

```bash
grep -oE "__dirname, '[^']+'" $KIT | sed "s/.*'\(.*\)'/\1/" | sort -u
```

kit 7 件（`..` を除く）: `check-action-versions.js` / `check-doc-links.js` /
`check-feedback-dispatched.js` / `check-kit-sync.js` / `check-permission-denials.js` /
`check-review-verdict.js` / `gen-changelog.js`。**本リポ版は 5 件**で、
`check-kit-sync.js` / `check-review-verdict.js` の 2 件が増える。いずれも実体は在る。

### 軸 3 — 公開関数の充足（**静的走査では正規表現が破綻したため、実走で引いた**）

分割代入の名前を正規表現で抜く方法は、同一ブロックに多数の `require` が入り混じるため
誤った切り出しをした（貪欲一致で本文を飲み込む）。**加工した出力を判断に使わない**（規則 7）ため、
**キット版を本リポへ置いて実走**し、`ok()` を「例外を捕まえて報告する」形に一時改造して
**落ちるものを 1 回で全数**出した。

```bash
cp $KIT scripts/ztmp-kit.test.js   # ok() を try/catch へ一時改造
SCRIPTS_TEST_CHILD=1 node scripts/ztmp-kit.test.js | grep -E "^  FAIL|tests passed"
```

```
FAIL pin 鮮度はしきい値ちょうどで鳴らさない（毎回鳴ると読まれなくなる） :: freshness is not a function
FAIL isBotLogin は大小文字を無視して完全一致する :: isBotLogin is not a function
FAIL 自リポ名を CROSS_REPOS へ入れたら設定エラーで止まる :: The input did not match the regular expression /SELF_NAMES/
✓ 599 tests passed
```

**落ちる前提は 3 件だけである**（`kit-sync-classification.example.json` を置いた後の数。
置く前は `MODULE_NOT_FOUND` でプロセスごと死ぬため、これを 4 件目として数える）。

| # | 前提 | 本リポの状態 | 採る対処 |
| --- | --- | --- | --- |
| 1 | `kit-sync-classification.example.json` の実体 | 無し（`notApplicable`） | **(a) 追随**: キット版を実体で置き、分類を `notApplicable` → **A** へ |
| 2 | `check-commit-messages.js` が `isBotLogin` を公開 | `isBotAuthorName` で公開（**実装は同一**） | **(a) 追随**: 本リポ側を `isBotLogin` へ改名 |
| 3 | `check-planning-pin-freshness.js` が `freshness` を公開 | 目的の違う別実装（着手可否の分類） | **(a) 追随**: `freshness` / `pinnedCommitDate` を**足して実経路へ配線** |
| 4 | `check-cross-repo-refs.js` の `createChecker` が SELF_NAMES 衝突を設定エラーにする | `createChecker` 自体が無い | **(a) 追随**: `createChecker` 構造を導入（検出力は 1 つも落とさない） |

### 軸 4 — 逆向き（キット版へ移ると**失われる** `ok()`）

```bash
grep -oE "^\s*ok\('[^']*'" <各版> | sed "s/.*ok('//" | sort   # repo 115 / kit 177
comm -23 repo.oks kit.oks
```

差分は 6 行だが、**5 行は kit 側で改名された同一テスト**（`check-feedback-dispatched` の
「起票済み」→「伝達」への語彙変更。kit 側に対応するテストが在る）、
**1 行は本リポ版が同じ `ok()` を 2 回持っていた重複**（`scripts/scripts.test.js:848` と `:1042`。
`grep -n "GITHUB_ACTIONS=true でも全テスト"` で実測）。**恒久的に失われるテストは 0 件**である。

### 軸 5 — companion（`scripts/scripts.repo.test.js`）との重複

キット版が新設した 8 ブロックに対し、companion の該当箇所を全数突合した
（`grep -n "check-cross-repo-refs\|selfTest\|check-plan-id-qualification\|check-review-verdict\|check-kit-sync\|check-feedback-status-sync\|check-planning-pin-freshness\|kit-sync-classification.example" scripts/scripts.repo.test.js`）。

**判定規則: companion の言明がキット版の言明の「真部分集合」であるときだけ削る。**

| companion のテスト | キット版の対応 | 判定 |
| --- | --- | --- |
| `check-planning-pin-freshness --self-test が通る` | 同じ CLI を `execSync` で実走 | **削る**（完全な部分集合） |
| `#524: checkSingleTitle は作成者で分岐する（bot=skip / App=検査）` | `PR 作成者 …` 6 件が同じ 4 言明を含む | **削る**（完全な部分集合） |
| `#524: 除外は名前で判定する` | `isBotLogin は大小文字を無視して完全一致する`（5 言明） | **重複行のみ削る**（`github-actions[bot]` / `undefined` / 部分一致 3 種 / 前後空白は本リポ固有で残す） |
| `check-cross-repo-refs --self-test が通る` | `selfTest()` を**プロセス内**で呼ぶ | **残す**（companion は CLI の argv 配線と `all passed` の出力を見る。部分集合でない） |
| `check-plan-id-qualification --self-test が通る` | 同上（プロセス内） | **残す**（同上） |
| `#737: --self-test が比較ロジックを fixture で駆動する` | `--self-test` を実走（stdout は捨てる） | **残す**（companion は件数報告 `自己試験 N 件 all passed` の消失を見る） |
| `check-kit-sync` の `main()` 実走 | `--self-test` のみ | **残す**（実データ突合は別言明） |

### 除外したものと理由（黙って落とさない）

| 除外 | 理由 |
| --- | --- |
| `check-commit-messages.js` / `check-cross-repo-refs.js` の**キット版への差し替え** | #756 が実走で「本リポ版が優る」と判定済み。**検出力を下げない**という制約に従い、差し替えず**キット版が要求する構造だけ**を足す |
| `check-planning-pin-freshness.js` の**キット版への差し替え** | #749 が本リポ版の逆方向比較バグを直した当のファイル。着手可否の分類を失う。キット版自身が「ここが要るリポジトリは本スクリプトを土台に分類規則を足すこと」と述べており、**足す**のが正しい向き |
| キットの他 114 ファイル | 本 issue の対象は `scripts.test.js` とその前提のみ。`check-kit-sync.js` が引き続き全数を見る |
| `CLAUDE.md` / `.claude/rules/` | 必読規約 50KB 予算が 98%。**1 バイトも足さない** |

> **この走査に自己参照は入る。** 走査対象のうち `docs/specs/` 配下（＝本書）はキットに対応物が
> 無いため軸 0〜4 の母集合には入らないが、**軸 5 の `grep` は追跡下の `scripts/` に限っている**ため
> やはり入らない。よって「生の数 → 自己参照を引く → 最終値」は **軸 1: 15 → 0 → 15**、
> **軸 3: 4 → 0 → 4**、**軸 5: 7 → 0 → 7** である（引くものが無い）。時点は上記のとおり。

## 対象範囲

- 対象: `scripts/scripts.test.js`（キット版へバイト一致）/ 上表 4 件の前提の追随 /
  `scripts/scripts.repo.test.js` の重複整理と新デルタの変異試験 / `scripts/kit-sync-classification.json` の分類更新。
- 対象外: キットへの環流の**起票**（planning#373 / planning#374 が既に在る）。
  `CLAUDE.md` / `.claude/rules/`。キット版検査器への全面差し替え（上表の理由）。

## 設計

### 1. `scripts/kit-sync-classification.example.json` を実体で置く

キット版と**バイト一致**でコピーする。分類表の `notApplicable` から外し **A** へ移す。
`notApplicable` の説明文にある「`*.example.json` は本リポが実体を持つ」は**もはや理由にならない**
——キット版 `scripts.test.js`（分類 A）が `require('./kit-sync-classification.example.json')` で
**雛形そのもの**を検査対象にしており、実体が無いとテストがプロセスごと落ちる。
雛形（配布の正）と実表（本リポの中身）は別物であり、両方を持つのが正しい。

### 2. `check-commit-messages.js`: `isBotAuthorName` → `isBotLogin` へ改名

実装は 1 バイトも変わらない（#756 の突合表で「実装は同一」と実測済み）。
`.claude/rules/traceability.md`（分類 A の配布物）が既に `isBotLogin()` と書いており、
**改名によって規約と実装が初めて一致する**。参照箇所（companion・`docs/`）も追随させる。

### 3. `check-planning-pin-freshness.js`: `freshness` を足し、**実経路へ配線する**

キット版の純関数 `freshness(pinnedEpoch, nowEpoch, maxAgeDays)` と `pinnedCommitDate(dir)` を移植する。
**未使用のまま公開しない**（それは「テストを通すためだけの飾り」であり、本リポが繰り返し戒めてきた
「0 件検査で緑」と同じ形になる）。配線先は **`比較対象を取得できないため検査していません` の
fail-open 経路**とする —— この経路は planning が populate 済みでも比較元が取れなければ通り、
現状は**何の情報も出さずに緑**を返す。ここで pin の**経過日数**を併記すれば、
「検査できていない」ことと「pin が何日前か」を読み分けられる（#749 が問題にした形の残り）。

### 4. `check-cross-repo-refs.js`: `createChecker` 構造を導入する

キット版の `createChecker({ crossRepos, selfNames })` を移植し、本リポの検出力（型 1〜4・
`〔〕` 区切り・`.md` 外走査）を**その上に載せる**。これは #756 が「環流の理想形」と書いた形
そのものであり、planning#374 の着地を本リポ側で先に作ることになる。

- `CROSS_REPOS = { 'project-planning': 'planning', 'ai-stock-trading': 'AST' }` を単一情報源にする。
- **`SELF_NAMES = ['MSP', 'microservices-platform']` を明示する。** 現状この不変条件は
  **コメントでしか守られていない**（「自リポジトリを指す修飾語は型 3 の集合から意図的に外してある」）。
  設定エラーとして機械に守らせるのがキット版の唯一の優位点であり、それを取り込む。
- 正規表現は `createChecker` の中で**設定から組み立てる**。長い名前を先に並べる
  （`project-planning` を `planning` より先に当てる）キット版の順序を採る。
- `LONG_RE` / `ENUM_RE` / `SPACED_RE` / `ENUM_FIX_RE` / `SHORT_NAMES` / `LONG_NAMES` は
  `DEFAULT_CHECKER` から導出して**公開面を変えない**（companion と `check-commit-messages.js` が
  引いているため）。
- **検出力は 1 つも落とさない。** 本リポ版の 85 件の自己試験と実データ走査で確かめる。

### 5. `scripts/scripts.test.js` をキット版で置き換える

`cp` でバイト一致させる。以後、本ファイルは**変更禁止**（分類 A）。

### 6. companion の整理と、新デルタの変異試験

軸 5 の表に従って重複を削る。あわせて 2・3・4 で入れたデルタの変異試験を足す
（「足した機能が働くこと」と「壊れたら落ちること」を対で置く）。

## 受け入れ基準

- [x] `cmp scripts/scripts.test.js <kit>` が**一致**（exit 0）
- [x] `node scripts/check-kit-sync.js` が緑で、`scripts/scripts.test.js` が **A** に在る
- [x] `node scripts/scripts.test.js` / `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が緑
- [x] `check-cross-repo-refs.js` / `check-commit-messages.js` の**検出力が下がっていない**
      （両者の `--self-test` と実データ走査、companion の変異試験で実測）
- [x] 追随できなかった前提があれば、環流先の planning issue 番号が分類表の理由欄に入る
      （`check-cross-repo-refs.js` は X のまま・planning#374 / `check-commit-messages.js` は X のまま・planning#373 /
      `check-planning-pin-freshness.js` は X のまま・#749）
- [x] companion に本リポ固有のテストが残り、キット版と**同じ言明を 2 箇所に持たない**

## 実施した変更と実測（証跡）

| 変更 | 内容 |
| --- | --- |
| `scripts/scripts.test.js` | **キット版で置き換え**（+750/−87）。以後**変更禁止**（分類 A） |
| `scripts/kit-sync-classification.example.json` | キット版を**新規にバイト一致で配置** |
| `scripts/check-commit-messages.js` | `isBotAuthorName` → `isBotLogin` へ改名（実装は不変） |
| `scripts/check-planning-pin-freshness.js` | `freshness` / `pinnedCommitDate` / `ageNote` / `DEFAULT_MAX_AGE_DAYS` を追加し、**比較元を取れない fail-open 経路へ配線**。自己試験 +6 件 |
| `scripts/check-cross-repo-refs.js` | `createChecker` 構造 ＋ 置換点 `CROSS_REPOS` / `SELF_NAMES` ＋ 設定の妥当性検査。**検出力は不変** |
| `scripts/check-landed-subjects.js` / `docs/how-to/commit-message-rules-annex.md` | 改名の追随 |
| `docs/adr/IADR-0187` / `docs/adr/IADR-0201` | 日付つき追記（旧 API 名の記録である旨 / 分類が A へ戻った旨） |
| `scripts/kit-sync-classification.json` | `scripts.test.js` **X → A** / `*.example.json` **notApplicable → A** / 3 件の理由欄更新 / `$comment` の `notApplicable` 定義を是正 |
| `scripts/scripts.repo.test.js` | 重複 2 件を削除・重複行を整理・**変異試験 5 件**を追加 |

### 追随の前後

```
【前】cmp scripts/scripts.test.js <kit>  → differ: char 431, line 14   exit 1
      node scripts/check-kit-sync.js     → OK（A 76 / B 26 / C 4 / 対象外 9）      exit 0
      node scripts/scripts.test.js                     → ✓ 540 tests passed  exit 0
      REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js → ✓ 540 tests passed  exit 0

【後】cmp scripts/scripts.test.js <kit>  → 差分なし（無出力）           exit 0
      node scripts/check-kit-sync.js     → OK（A 78 / B 25 / C 4 / 対象外 8）      exit 0
      node scripts/scripts.test.js                     → ✓ 607 tests passed  exit 0
      REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js → ✓ 607 tests passed  exit 0
```

### テスト件数の増減（**減った分を 1 件ずつ説明する**）

**540 → 607（+67）。** 内訳:

| 向き | 件数 | 中身 |
| --- | --- | --- |
| ＋ | **+64** | キット版が持ち込んだブロック（`check-review-verdict` 8 / `check-kit-sync` 4 / `check-feedback-status-sync`・`check-planning-pin-freshness` 5 / `check-feedback-dispatched` の拡張 / `PR 作成者` 8 / `check-cross-repo-refs` 3 / `check-plan-id-qualification` 4 / 置換点非依存 1） |
| ＋ | **+5** | companion の変異試験（`pin 鮮度 #757` 2 件 / `check-cross-repo-refs #757` 3 件） |
| − | **−2** | companion の重複削除（下表） |
| ＋ | **±0** | 本リポ版が同じ `ok()` を 2 回持っていた重複 1 件はキット版で 1 回に畳まれ、代わりにキット版が別テストを 1 件持つ |

**削った 2 件が守っていたもの**（別のどこかで守られていることを実測で確認済み）:

| 削ったテスト | 守っていたこと | どこで守られるか（実測） |
| --- | --- | --- |
| `#524: checkSingleTitle は作成者で分岐する（bot=skip / App=検査）` | bot ログインは規約外件名でも 0 / `claude[bot]` は検査され 1 / 規約適合なら 0 / 作成者未指定でも検査 | キット版 `scripts.test.js` の `PR 作成者 dependabot[bot] は…` ほか 6 件が**同じ 4 言明**を持つ。実行ログで `ok  PR 作成者 claude[bot] は除外しない（規約外件名は 1）` 等を確認 |
| `check-planning-pin-freshness --self-test が通る` | 自己試験が exit 0 で通る | キット版の `` `${s} の自己試験が通る` `` が `execSync`（非 0 で throw）で**同じ CLI を実走**。実行ログで `ok  check-planning-pin-freshness.js の自己試験が通る` を確認 |

`#524: 除外は名前で判定する` は**残した**（キット版が見ていない 4 群 —— `github-actions[bot]` /
人間ログイン / `undefined` / 部分一致 3 方向 ＋ 前後空白 —— を持つため）。キット版と重なる 5 行だけを削り、
**何をキットへ譲ったかをコメントで明記**した。

### 変異試験の実測（「配線した」と書いて実体が無い状態を作らない）

```
scripts/check-planning-pin-freshness.js の `ageNote(root)` を `''` へ置換して実走
  → AssertionError: 経過日数が添えられていない（配線が切れている）   exit 1
  （復元後は緑）
```

`check-cross-repo-refs` 側は、設定を壊した入力（`createChecker({crossRepos:{'my-repo':'MINE'},selfNames:['MINE']})` /
長い表記側の衝突 / 空 `crossRepos`）が**実際に例外で止まること**を companion で固定した。

### 検出力の非退行（#756 の判定を下げていないことの実測）

```
node scripts/check-cross-repo-refs.js --self-test  → 自己試験 85 件 all passed  exit 0
node scripts/check-cross-repo-refs.js              → OK: 1616 件（scripts/ 非 md 70 件は除外）exit 0
   ※ 1615 → 1616 の +1 は新設した kit-sync-classification.example.json
node scripts/check-plan-id-qualification.js        → OK: 1324 件  exit 0
node scripts/check-commit-messages.js              → ✓ すべてのコミットが規約に適合  exit 0
node scripts/check-doc-links.js                    → OK: 631 件  exit 0
node scripts/check-planning-pin-freshness.js --self-test → self-test OK（+6 件）exit 0
node scripts/check-reading-budget.js               → 50,193 B（98.0%・**1 バイトも増やしていない**）
```

型 1〜4 と `〔〕` 区切りの検出は companion の
`check-cross-repo-refs #757: 載せ替えで型 1〜4 と〔〕区切りの検出が落ちていない` で
**正例・負例を対で**固定した（負例＝規約どおりの形で鳴らないこと。片側だけ見ると検査器が外される）。

### 母集合の規則 10（この変更で新たに誤りになる自分の記述）の引き直し

```bash
grep -rn "isBotAuthorName" --exclude-dir=.git --exclude-dir=planning .      # 是正前 26 箇所 / 11 ファイル
grep -rn "A 76\|B 26\|対象外 9\|scripts\.test\.js.*[BX]\|+750" ...          # 17 件
grep -rn "example.json は本リポが実体を持つ" ...                             # 1 件
```

- **live なもの（書き換えた）**: `scripts/check-commit-messages.js` / `scripts/check-landed-subjects.js` /
  `scripts/scripts.repo.test.js` / `docs/how-to/commit-message-rules-annex.md`（改名の追随）、
  `scripts/kit-sync-classification.json` の `$comment`（`notApplicable` の定義）、
  `docs/adr/IADR-0201`（分類が A へ戻った）、`docs/adr/IADR-0187`（旧 API 名は**当時の記録**である旨の追記）。
- **書き換えないもの**: `docs/specs/` / `feedback/` の確定済み記録（`.claude/rules/traceability.repo.md`
  「確定済みの `docs/specs/`・`feedback/` は書き換えない」）。`docs/adr/README.md` /
  `docs/adr/IADR-0115` は**過去の経緯の記述**であり、いま読んでも誤りにならない。
- **既存の不正確さ（本変更とは無関係・触らない）**: `scripts/README.md` が
  `check-cross-repo-refs.js` の対象を「追跡下の `*.md`」と書いている（実際は追跡下の全ファイル。
  #583 / [IADR-0169](../adr/IADR-0169_cross-repo-ref-scan-beyond-markdown.md) 以降ずれている）。#756 の作業仕様書も同種の既存ずれを記録している。**母集合外**。

## テスト方針

- キット版 `scripts.test.js` は**触らない**（分類 A）。本リポ固有の言明は companion にだけ置く。
- 新デルタ（`freshness` の配線 / `createChecker` の設定検証 / `isBotLogin` の改名）は
  **変異試験を対で置く**: 働くことと、配線を切ったら落ちることの両方。
- 検出力の非退行は、`check-cross-repo-refs.js --self-test`（85 件）と実データ走査（`.md` 外を含む）で見る。

## 計画書との差異

- 差異: なし。HOWTO §B-5 の手順（実走で差を確かめる／差があればキット版で上書きしない）に従い、
  **上書きせず、キット版が要求する構造だけを本リポ版へ足した**。

## 未決事項

- **`check-cross-repo-refs.js` / `check-commit-messages.js` / `check-planning-pin-freshness.js` は
  X のまま残る。** 本作業で解消したのは「キット版が優っていた点」だけであり、
  **本リポの検出力をキットへ載せる環流（planning#373 / planning#374）が着地するまで X は消えない。**
  着地後は 3 本ともキット版へ戻して A へ移せる。
- **別 issue へ切り出す提案（本作業では起票しない）**: `check-planning-pin-freshness.js` の
  `freshness` は現在**比較元を取れない経路にしか配線していない**。比較が成功した経路でも
  「pin が N 日前」を併記するか（＝キット版の主経路と同じ扱いにするか）は、
  **鳴りすぎの是非**（判断 3「鳴りすぎると読まれなくなる」）に関わるため別途判断が要る。
  本作業では**情報が増える側にだけ**入れた。
