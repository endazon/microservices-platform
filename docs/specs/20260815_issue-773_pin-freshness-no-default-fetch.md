---
title: 作業仕様書 — pin 鮮度検知の既定を「fetch しない」へ戻し、fetch を --fetch の opt-in にする
type: spec
status: done
related_ids:
  - NFR
  - IADR-0170
  - IADR-0202
author: claude
created: 2026-08-15
updated: 2026-08-16
plan_refs:
  - "../../planning/projects/microservices-platform/07_adr/README.md"
related_specs:
  - "../adr/IADR-0170_planning-pin-freshness-detection.md"
  - "../adr/IADR-0202_pin-freshness-comparison-source.md"
  - "20260815_issue-749_pin-freshness-reverse-comparison.md"
  - "20260815_issue-757_scripts-test-kit-parity.md"
---

# 作業仕様書: 既定でネットワーク fetch しない（#773）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（**NFR**）
- ユースケース（UC）/ 画面（SC）: なし
- **無採番 `NFR` の根拠**: `.claude/rules/traceability.md` の「無採番を許す 2 つの場合」の
  **2（ID 列はあるが、その作業に当たる番号が無い場合）**に当たる。本作業は
  **検査器（`check-planning-pin-freshness.js`）を ADR の決定へ合わせる統制作業**であり、
  計画側の非機能要件表（`02_requirements/` の `NFR-01`〜`NFR-27`）が扱う**稼働する製品の要件**では
  ない。計画の ID 列を見たうえで、当たる番号が無いことを確認している（1 ではないので**環流しない**）。
- 関連 ADR: **[IADR-0202](../adr/IADR-0202_pin-freshness-comparison-source.md) 決定 4**（案 A =
  ネットワーク fetch は採らない）／ [IADR-0170](../adr/IADR-0170_planning-pin-freshness-detection.md)
  （pin 鮮度検知そのもの。fail-open ／「検査していない」と「乖離なし」の読み分け）
- 関連 issue: **#773**（本作業）／ #749（IADR-0202 を起こした issue）／ #757（キット版との突合）
- 計画書リンク: `planning/projects/microservices-platform/07_adr/README.md`

## 目的・背景

[IADR-0202](../adr/IADR-0202_pin-freshness-comparison-source.md) **決定 4** は
「**案 A は採らない。** SessionStart hook をネットワーク・認証に依存させる代償が、得られる根治に
見合わない」と決めている。にもかかわらず、実装は**既定でネットワーク fetch を実行していた。**

**`--no-fetch` を opt-in にした時点で、既定は ADR が捨てたほうになっていた。** 本作業は
**実装を決定へ合わせる**（決定は変えない。新 IADR も立てない）。

### 自分で取った裏（issue 本文からの転記ではない）

| # | 確認した事実 | 実測 |
| --- | --- | --- |
| 1 | `resolveComparisonSource` の既定 | `function resolveComparisonSource(root = REPO_ROOT, { fetch = true } = {})`（`:252`）。**既定 `true`** |
| 2 | CLI のフラグ解析 | `const noFetch = argv.includes('--no-fetch')`（`:491`）→ `{ fetch: !noFetch }`（`:517`）。**fetch しないほうが opt-in** |
| 3 | 本番の呼び出し経路（全走査） | 後述「呼び出し経路の全走査」。**2 件**（`scripts/setup.sh:52` / `.github/workflows/planning-pin-freshness.yml:62`）。**どちらもフラグを渡していない** |
| 4 | fetch が実際に走ること | 後述「fetch が走ることの実測」。**`git fetch` の実行を PATH shim で捕捉**した |
| 5 | 決定 4 の原文 | 「**案 A は採らない。** SessionStart hook をネットワーク・認証に依存させる代償が、得られる根治に見合わない」（`IADR-0202` 決定 4）。表の案 B 列も「ネットワーク: 不要」 |

### 呼び出し経路の全走査

`git grep -n "check-planning-pin-freshness"`（追跡下の全ファイル。`planning` / `src/ai-stock-trading`
の submodule を除く）。**拡張子で絞っていない。**

| 種別 | 箇所 | フラグ |
| --- | --- | --- |
| **本番（SessionStart hook）** | `scripts/setup.sh:52` | なし → **fetch する** |
| **本番（夜間ワークフロー）** | `.github/workflows/planning-pin-freshness.yml:62` | なし → **fetch する** |
| 試験 | `scripts/scripts.repo.test.js`（`:6399` / `:6601` ほか） | `--no-fetch` |
| 試験 | `scripts/scripts.test.js`（分類 A・変更禁止） | `--self-test` のみ |
| 分類表 | `scripts/kit-sync-classification.json:134` | 呼び出しではない |
| 文書 | `docs/specs/` 各所・`docs/adr/IADR-0170` `IADR-0183` `IADR-0201` | 呼び出しではない |

**`.claude/` の hook 設定・`package.json` の scripts には呼び出しが無い**
（`grep -rn "pin-freshness\|pin 鮮度" .claude/ package.json` = 0 件）。
`scripts/setup.sh` は SessionStart hook から走る（＝ `.claude/settings.json` 経由の**間接**の本番経路）。

**`.github/workflows/` の他のワークフローにも呼び出しは無い**（走査は全ワークフローを含む）。

### fetch が走ることの実測（2026-08-15・修正前）

隔離した fixture（上流 planning リポ ＋ gitlink を持つ実装リポ）を作り、**PATH の先頭に `git` の
shim** を置いて全 `git` 実行を記録し、**本番と同じ引数形**（フラグなし）で走らせた。

| 実行 | 記録された `git fetch` | 出力の比較元行 |
| --- | --- | --- |
| `node scripts/check-planning-pin-freshness.js --root <fixture>` | **1 件**（`-C <fixture>/planning fetch --quiet origin`） | `… / fetch 成功` |
| `… --root <fixture> --no-fetch` | **0 件** | `… / fetch 省略` |

**ネットワークが無くても「fetch を試みたこと」は捕捉できる**（shim は引数を記録してから実 git へ
exec する。上流をローカルパスに置いたため実際に成功もした）。

## 対象範囲

- 対象: `scripts/check-planning-pin-freshness.js`（既定値と CLI）、`scripts/scripts.repo.test.js`
  （追随 ＋ 変異試験）、`docs/adr/IADR-0202_*.md`（**決定は変えず**実装が追随した旨の追記）、
  `docs/specs/20260815_issue-749_*.md`（**確定済み**。日付つき追記ブロックのみ）
- 対象外: IADR-0202 の**決定そのもの**（改定しない。新 IADR も立てない）／ `scripts/scripts.test.js`
  （分類 A・キットとバイト一致）／ キット版本体（`planning/` は変更しない。環流は #757 の系列で扱う）／
  `docs/adr/README.md` の索引行（**文面が今も正しい**ため変更しない。後述）

## 設計

### 1. 既定を `fetch: false` にする（issue の選択肢 1）

```js
function resolveComparisonSource(root = REPO_ROOT, { fetch = false } = {}) {
```

ADR は「先に入れるべきは案 B」「案 A は fetch が成功しても認証・プロキシの都合で塞がらない」と
**根拠を持って案 A を退けている**。**それを覆す新しい実測は無い**ため、実装を決定へ合わせる。

### 2. フラグ名は `--no-fetch` → **`--fetch`（opt-in）へ改める**

**判断: 改める。** 理由は 3 つで、いずれも**実際の呼び出し箇所を全走査した結果**から出ている。

1. **既定が `false` になると `--no-fetch` は無条件の no-op になる。** 死んだフラグは
   「これを付ければ fetch を止められる（＝付けなければ fetch する）」という**逆の既定を読ませる**。
   本 issue はまさにその読み違いから生まれている。
2. **既存の呼び出しは 1 件も壊れない。** `--no-fetch` を渡しているのは
   `scripts/scripts.repo.test.js` の**試験 2 箇所だけ**であり、**本番の 2 経路はフラグを渡していない**
   （上表）。「既存の呼び出しを壊さない」ことと衝突しないため、「死んだフラグを残さない」を採れる。
3. **キット版の正準名が `--fetch` である。** `planning/tools/impl-handoff-kit/repo-template/scripts/
   check-planning-pin-freshness.js` は `const doFetch = argv.includes('--fetch')` を持ち、ヘッダに
   「**既定はオフラインで完結する**」と書いている。IADR-0202 の選択肢表の案 C 列
   「不要（`--fetch` は opt-in）」もこれを指す。本ファイルは分類 B（本リポ originate）で
   **是正をキットへ環流する順序**であり（IADR-0202 決定 5）、名前を揃えておくほど環流時の差分が減る
   （キットの正準名へ寄せる前例: [IADR-0201](../adr/IADR-0201_class-c-rejudgement-and-fail-closed-kit-checks.md) の `isBotLogin`）。

なお `--no-fetch` を渡し続けても**挙動は変わらない**（fetch しない）。壊れる呼び出しは存在しない。

### 3. 変異試験（必須）

**既定を `fetch: true` へ戻すと落ちる**試験を `scripts/scripts.repo.test.js` へ置く
（`scripts.test.js` は分類 A のため触らない）。**正例だけの緑は受け入れない**ので、
**変異版が実際に fetch することも同じ fixture で実測**する。
**変異点は 2 つある**（CLI のフラグ解析 ／ 関数の既定引数）—— 詳細と実測は後述。

## 受け入れ基準

- [x] 既定でネットワーク fetch が起きない（`resolveComparisonSource` の既定が `false`）
- [x] `scripts/setup.sh` と `.github/workflows/planning-pin-freshness.yml` の呼び出しで
      fetch が起きないことを**実測**する（PATH shim で `git fetch` 0 件）
- [x] **変異試験**: 既定を `{ fetch = true }` へ戻した複製は同じ fixture で `git fetch` を実行する
- [x] `--fetch` を付けたときだけ fetch する（opt-in が死んでいない）
- [x] 本番の 2 経路が `--fetch` を渡していないことをテストで固定する
- [x] `node scripts/scripts.test.js` と `REQUIRE_REPO_TESTS=1` 版が緑
- [x] IADR-0202 の**決定は改定しない**（新 IADR も立てない）

### 検証の証跡（2026-08-15〜16）

| コマンド | 結果 |
| --- | --- |
| `node scripts/check-planning-pin-freshness.js --self-test` | exit 0（28 件） |
| `node scripts/check-planning-pin-freshness.js` | exit 0（populate 済み。`比較元: … / fetch 省略`） |
| `node scripts/scripts.test.js` | 全件 pass |
| `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` | 全件 pass（`planning` を pin どおり populate してから実行） |
| `node scripts/check-doc-links.js` / `check-doc-updated.js` / `check-doc-status-vocabulary.js` / `check-doc-type-vocabulary.js` / `check-adr-numbering.js` / `check-cross-repo-refs.js` / `check-plan-id-qualification.js` / `check-commit-messages.js` / `check-reading-budget.js` | いずれも OK |

#### 変異試験の実測 —— **変異点は 2 つある**（実測でそう分かった）

★ **最初に書いた変異試験は空振りした。** `{ fetch = false }` → `{ fetch = true }` へ戻した複製を
CLI として走らせても **fetch しなかった** —— `main()` は `resolveComparisonSource(root, { fetch: doFetch })`
と**常に明示で渡す**ため、**既定引数は CLI 経路では観測できない**。
**本番で効いている変異点は CLI のフラグ解析のほうである。** 両方に門を置いた。

| 変異点 | 戻した形 | 観測手段 | 実測 |
| --- | --- | --- | --- |
| **A: CLI のフラグ解析**（**本番で効くのはこちら**） | `const doFetch = !argv.includes('--no-fetch');` | PATH shim ＋ フラグなし実走 | **`git fetch` 1 件**・`fetch 成功` |
| **B: 関数の既定引数** | `{ fetch = true } = {}` | `require()` して `resolveComparisonSource(repo).fetch` | **`'ok'`**（原本は `'skipped'`） |
| 原本（修正後）・フラグなし | — | PATH shim | **`git fetch` 0 件**・`fetch 省略` |
| 原本（修正後）＋ `--fetch` | — | PATH shim | **`git fetch` 1 件**・`fetch 成功` |

**「実際に落ちること」の実測**（本番コードを壊して `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`）

| 壊した箇所 | 結果 |
| --- | --- |
| CLI を `!argv.includes('--no-fetch')` へ戻す | **fail** —— `既定でネットワーク fetch を実行した（IADR-0202 決定 4 は案 A を採らないと決めている）` |
| 既定引数を `{ fetch = true }` へ戻す | **fail** —— `resolveComparisonSource の既定引数が fetch する側になっている（IADR-0202 決定 4）` |
| `scripts/setup.sh` の呼び出しへ `--fetch` を足す | **fail** —— `SessionStart hook がネットワーク fetch を有効にしている（IADR-0202 決定 4 に反する）` |
| いずれも戻した状態 | **610 tests passed** |

**素通りしたもの**: `--fetch` の opt-in 試験（`pin 鮮度 #773: --fetch を付けたときだけ fetch する`）は
上記 3 つの変異のいずれでも**落ちない**。**これは門ではなく正例**であり、「既定を落としただけで
opt-in が死ぬ」形を防ぐために置いている（落ちないことは想定どおりで、門は上の 3 件が担う）。

★ **テストの並び順を実測で入れ替えた。** 変異 A を入れると、当初は先に並んでいた **#749 の fixture 試験**が
先に落ち（`--no-fetch` を外した helper が fetch して仕込んだ位置関係が消えるため）、
**`比較できていない」と言っていない` という無関係なメッセージで赤になった**。
テストランナーは最初の失敗で中断するため、**#773 の門より前に置くと診断が誤導される。**
#773 のブロックを #749 のブロックの**前**へ移し、変異 A が `既定でネットワーク fetch を実行した` で
落ちることを再実測した。

## テスト方針

`scripts/scripts.repo.test.js` へ 3 件追加し、既存の `--no-fetch` 参照 2 箇所を追随させる
（**#749 のブロックより前**に置く。理由は上記「テストの並び順」）。

1. **`pin 鮮度 #773: 既定でネットワーク fetch を実行しない（IADR-0202 決定 4・変異試験つき）`** ——
   fixture（上流 ＋ 実装リポ）を作り、**PATH 先頭に `git` の shim** を置いて**本番と同じ引数形**
   （フラグなし）で実走。`git fetch` の記録が **0 件**であること、出力が `fetch 省略` であることを
   assert する。あわせて `resolveComparisonSource(repo).fetch === 'skipped'` も見る（既定引数の側）。
   続けて**同じ fixture へ変異版を 2 種**当てる —— **A: CLI を `--no-fetch` opt-in へ戻した複製**は
   `git fetch` が **1 件**に、**B: 既定引数を `true` へ戻した複製**は `resolveComparisonSource().fetch`
   が **`'ok'`** になることを assert する（変異が当たったこと・変異点が一意であることも assert）。
2. **`pin 鮮度 #773: --fetch を付けたときだけ fetch する（opt-in が死んでいない）`** —— 同じ fixture へ
   `--fetch` 付きで実走し、`git fetch` が 1 件記録され `fetch 成功` が出ることを assert する。
3. **`pin 鮮度 #773: 本番の 2 経路が --fetch を渡していない`** —— `scripts/setup.sh` の**呼び出し行**と
   `.github/workflows/planning-pin-freshness.yml` の `run:` 行を**特定してから**見る
   （`|| log` の空振り（#680）と同型の取り違えを避ける）。両方に `--fetch` が無いことを assert する。
   **本番経路の固定はこの静的検査 ＋ 既定の門（1）の 2 枚で成立する** —— 経路が増えても、
   その経路がフラグを渡さない限り既定（fetch しない）が効く。

`git` が無い環境と Windows（shim が POSIX の実行可能スクリプト）では **skip し、その旨を出力する**
（黙って緑にしない。既存の #749 fixture と同じ扱い）。

## 追随（母集合の引き直し）

**issue 本文の一覧は母集合ではない。** 誤りの側の文字列で**追跡下の全ファイル**を走査した
（`git grep -F`。`planning` / `src/ai-stock-trading` の submodule を除く。**拡張子で絞っていない**）。

| 走査語 | ヒット（ファイル数 / 行数） | 追随 | 除外とその理由 |
| --- | --- | --- | --- |
| `--no-fetch` | 3 / 5 | `scripts/check-planning-pin-freshness.js:491`・`scripts/scripts.repo.test.js:6399,6598,6601` | `docs/specs/20260815_issue-749_*.md:152` は**確定済みの実行証跡**（当時そう実行した事実）。本文は書き換えず**日付つき追記ブロック**を足す |
| `fetch = true` | 1 / 1 | `scripts/check-planning-pin-freshness.js:252` | — |
| `fetch = false` | 0 / 0 | — | 走査前は 0 件（変更後に 1 件） |
| `fetch:` | 6 / 10 | `check-planning-pin-freshness.js:517`（`{ fetch: !noFetch }`） | `.claude/settings.json` / `claude-code-review.yml` / `claude-coding.yml` / `20260802_impl-handoff-kit-sync.md` は**権限指定 `Bash(git fetch:*)`** で無関係。`:250,272,438,440` と `20260815_issue-749_*.md:108` は**戻り値の型** `fetch: 'ok'\|'failed'\|'skipped'` で、**本作業で変えない** |
| `fetch 成功` / `fetch 省略` / `fetch 失敗` | 2 / 1 / 8 | なし | 出力の**状態名は変えない**（`skipped` の意味は「fetch していない」で不変）。`fetch 失敗` の他 6 件は **DataSource の同期**（`IADR-0051` ほか）で語が同じだけの別物 |
| `ネットワーク fetch` | 3 / 5 | `docs/adr/IADR-0202_*.md`（追記のみ） | `docs/adr/README.md:258` の索引行は「**案 A（ネットワーク fetch）は採らない**」と書いており、**修正後も文面が正しい**（決定は変えない）ので変更しない。`20260815_issue-749_*.md:62,64` は確定済みで、記述も今なお正しい |
| `--fetch` | 1 / 1 | — | `IADR-0202:51` の案 C 列（キット版の説明）。**キット版の事実**であり変更不要。本作業はここへ寄せた |

**追随した箇所: 4 件**（`check-planning-pin-freshness.js` ／ `scripts.repo.test.js` ／
`docs/adr/IADR-0202_*.md` の追記 ／ `docs/specs/20260815_issue-749_*.md` の追記）。

**規則 8（自分の記録の引き算）**: 上表の件数は **本仕様書を書く前の時点**（`HEAD = a5bbad6`）で
`git grep … HEAD` を引いた値である。**本仕様書自身は母集合に入っていない。** 本書は
`--no-fetch` / `fetch = true` / `--fetch` を**引用として含む**ため、コミット後に同じ語で引くと
**`--no-fetch` は 3 → 4 ファイル、`--fetch` は 1 → 3 ファイル**（本書 ＋ 変更後のコード）へ増える。
**是正対象ではなく本作業の記録である**ため、追随先には数えない。

**規則 10（この変更で新たに誤りになる自分の記述の引き直し）**: 既定を反転したことで
「`--no-fetch` を付ければ fetch を止められる」型の記述が誤りになる。**変更後の語**
（`--fetch` / `既定` ＋ `fetch`）で引き直した結果、live な文書での該当は 0 件である
（確定済み specs の実行証跡は当時の事実であり誤りにならない。追記で今の形を添えた）。

## 計画書との差異

- 差異: なし（本作業は**実装を既存の実装 ADR へ合わせる**是正であり、計画書の記述に触れない）

## 未決事項

- **キットへの環流**（IADR-0202 決定 5 / #757 の系列）。キット版は既に `--fetch` opt-in であり、
  本是正で**名前と既定が一致した**ため、環流すべき差分はフラグ周りには残らない。分類 B（本リポ
  originate）の突合そのものは #757 / #756 の系列が扱う。**本作業では起票しない。**
